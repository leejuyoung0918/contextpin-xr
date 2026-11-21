"""Exercise the assembly scene against real OpenSCAD and the existing EDGE."""
import hashlib
import io
import json
import os
from pathlib import Path
import shutil
import tempfile
import threading
import time
import unittest
import uuid
import zipfile
from unittest.mock import patch

from fastapi.testclient import TestClient

from backend.meeting.main import create_app
from backend.meeting.planner import fixture_plan


def openscad_available():
    path = os.getenv("OPENSCAD_EXECUTABLE")
    if path:
        return shutil.which(path) is not None
    return shutil.which("openscad") is not None or Path(
        "/Applications/OpenSCAD.app/Contents/MacOS/OpenSCAD").is_file()


@unittest.skipUnless(openscad_available(), "OpenSCAD CLI is unavailable")
class AssemblyTest(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        env = patch.dict(os.environ, {"ACTIVE_OBJECT_PROFILE": "object_2", "AI_MODE": "replay",
                                      "FINAL_CONTEXT_DEBOUNCE_MS": "25"})
        env.start()
        self.addCleanup(env.stop)
        os.environ.pop("OPENSCAD_ASSEMBLY_CONFIG", None)
        self.client = TestClient(create_app(self.folder.name))
        self.client.__enter__()
        self.addCleanup(self.client.__exit__, None, None, None)
        created = self.client.post("/v1/sessions", json={"display_name": "조립체 검토", "device_id": "pc"})
        self.assertEqual(created.status_code, 201, created.text)
        identity = created.json()
        self.parent = f"/v1/sessions/{identity['session_id']}"
        self.headers = {"Authorization": "Bearer " + identity["token"]}
        self.initial = identity["snapshot"]["versions"][0]
        self.profile = identity["snapshot"]["profile"]

    def get(self, path):
        reply = self.client.get(path, headers=self.headers)
        self.assertEqual(reply.status_code, 200)
        return reply

    def state(self):
        return self.get(self.parent).json()

    def command(self, kind, payload, expected=202):
        reply = self.client.post(self.parent + "/commands", headers=self.headers, json={
            "command_id": uuid.uuid4().hex, "type": kind, "payload": payload,
        })
        self.assertEqual(reply.status_code, expected, reply.text)
        return reply.json()

    def wait(self, response, expected="reviewable"):
        deadline = time.monotonic() + 70
        while time.monotonic() < deadline:
            item = next(r for r in self.state()["rounds"] if r["round_id"] == response["round_id"])
            if item["status"] == expected:
                return item
            if item["status"] in {"failed", "superseded", "needs_input", "answered"}:
                self.fail(str({k: item.get(k) for k in ("status", "error", "question", "candidates")}))
            time.sleep(0.08)
        self.fail(f"Timed out in {item['status']}")

    def version(self, vid):
        return next(v for v in self.state()["versions"] if v["version_id"] == vid)

    def manual_edit(self, factor=0.5):
        field = self.profile["parameters"][0]
        key = field["parameter_id"]
        target = field["default_value"] + factor * (field["max_value"] - field["default_value"])
        return self.command("edit", {"base_version_id": self.initial["version_id"], "parameters": {key: target}})

    def test_original_manual_exact_scad_stl_and_assembly_minutes(self):
        initial = self.initial
        state = self.state()
        self.assertEqual(state["original_version_id"], initial["version_id"])
        self.assertTrue(state["assembly"]["is_demo"])
        self.assertEqual(len(state["assembly"]["mounting"]["rotation_xyzw"]), 4)
        self.assertEqual(initial["engine"]["name"].lower(), "openscad")
        self.assertAlmostEqual(initial["bounds_m"]["size"][0], 0.1, places=6)
        item = self.wait(self.manual_edit())
        candidate = item["candidates"][0]
        built = self.version(candidate["version_id"])
        self.assertNotEqual(built["geometry_hash"], initial["geometry_hash"])
        self.command("decide", {"outcome": "commit", "round_id": item["round_id"], "revision": item["revision"],
                                "candidate_id": candidate["candidate_id"], "expected_committed_version_id": initial["version_id"],
                                "reason": "외형을 부드럽게 정리"}, 200)
        bundle_response = self.get(built["export_url"])
        with zipfile.ZipFile(io.BytesIO(bundle_response.content)) as bundle:
            self.assertEqual(bundle.read("model.stl"), self.get(built["stl_url"]).content)
            self.assertEqual(json.loads(bundle.read("parameters.json")), built["parameters"])
            self.assertEqual(bundle.read("object_2.scad"), self.get(built["assets"]["object_2.scad"]["url"]).content)
            minute = json.loads(bundle.read("assembly_meeting.json"))
            self.assertEqual(minute["selected_candidate_id"], candidate["candidate_id"])
            self.assertEqual(minute["reason"], "외형을 부드럽게 정리")
            self.assertEqual(minute["selected_model_sha256"], hashlib.sha256(bundle.read("model.stl")).hexdigest())
        self.assertEqual(self.state()["assembly"]["mounting"], state["assembly"]["mounting"])

    def test_curve_candidates_and_followup_are_real_different_meshes(self):
        first = self.wait(self.command("replay", {"sample": "curves"}))
        self.assertEqual(first["provider_mode"], "fixture")
        self.assertEqual(first["context"]["utterance"]["source_mode"], "replay")
        candidates = [c for c in first["candidates"] if c["status"] == "valid"]
        self.assertGreaterEqual(len(candidates), 2)
        models = [self.version(c["version_id"]) for c in candidates]
        self.assertEqual(len({v["geometry_hash"] for v in models}), len(models))
        c = candidates[0]
        self.command("view", {"version_id": c["version_id"], "candidate_id": c["candidate_id"],
                              "round_id": first["round_id"], "part_ids": []}, 200)
        next_round = self.wait(self.command("text", {"text": "이 후보에서 조금 더"}))
        self.assertEqual(next_round["edit_base_version_id"], c["version_id"])
        v = self.version(next_round["candidates"][0]["version_id"])
        self.assertNotEqual(v["geometry_hash"], models[0]["geometry_hash"])
        self.assertEqual(v["parent_version_id"], c["version_id"])

    def test_locked_change_and_old_planner_completion(self):
        self.command("edit", {"parameters": {"mounting_origin": 3}}, 400)
        request = self.command("text", {"text": "결합 부분을 넓혀줘"})
        blocked = self.wait(request, "needs_input")
        self.assertEqual(blocked["candidates"], [])
        entered = threading.Event()
        release = threading.Event()
        coordinator = self.client.app.state.coordinator
        def held(context, _):
            entered.set()
            release.wait(15)
            return fixture_plan(context)
        coordinator.planner = held
        try:
            first = self.command("replay", {"sample": "curves"})
            self.assertTrue(entered.wait(5))
            edit = self.manual_edit()
            self.wait(first, "superseded")
            release.set()
            selected = self.wait(edit)
            self.assertEqual(selected["provider_mode"], "manual")
            self.assertEqual(self.wait(first, "superseded")["candidates"], [])
            self.assertEqual(self.state()["committed_version_id"], self.initial["version_id"])
        finally:
            release.set()

    def test_failed_cad_candidate_keeps_original_downloadable(self):
        with patch.dict(os.environ, {"OPENSCAD_EXECUTABLE": str(Path(self.folder.name) / "unavailable-openscad")}):
            item = self.wait(self.manual_edit(), "failed")
        self.assertEqual(item["candidates"][0]["status"], "invalid")
        self.assertIn("OpenSCAD", item["candidates"][0]["error"])
        self.assertEqual(self.state()["committed_version_id"], self.initial["version_id"])
        self.assertEqual(self.get(self.initial["preview_url"]).content[:4], b"glTF")
