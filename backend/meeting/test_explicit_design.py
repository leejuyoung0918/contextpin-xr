"""Explicit request HTTP integration with staged STT finals, a PNG fixture and real cable CAD."""
import base64
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
import zipfile
from unittest.mock import patch

from fastapi.testclient import TestClient

from . import gateway
from .contracts import uid
from .main import create_app
from .planner import IDENTITY
from .speech_ranges import boundary

ROOT = Path(__file__).resolve().parents[2]
PNG = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jT5kAAAAASUVORK5CYII=")


class ExplicitDesignCase(unittest.TestCase):
    def setUp(self):
        source = ROOT / "artifacts/cable-demo/cable_organizer.scad"
        bundled = ROOT / "artifacts/tools/OpenSCAD.app/Contents/MacOS/OpenSCAD"
        openscad = os.getenv("OPENSCAD_EXECUTABLE") or shutil.which("openscad") or str(bundled)
        if not source.exists() or not Path(openscad).exists():
            self.skipTest("The registered cable CAD and OpenSCAD executable are needed")
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        work = Path(folder.name)
        config = work / "assembly.json"
        shutil.copyfile(ROOT / "backend/meeting/openscad_sample/cable-assembly.json", config)
        shutil.copyfile(source, work / source.name)
        env = patch.dict(os.environ, {"AI_MODE": "live", "MEETING_PLAN_BACKEND": "registered",
                             "ACTIVE_OBJECT_PROFILE": "object_2", "OPENSCAD_ASSEMBLY_CONFIG": str(config),
                             "FINAL_CONTEXT_DEBOUNCE_MS": "0", "OPENSCAD_EXECUTABLE": openscad})
        env.start()
        self.addCleanup(env.stop)
        self.client = self.enterContext(TestClient(create_app(work / "edge")))
        a = self.client.post("/v1/sessions", json={"display_name": "A", "device_id": "a",
                                                   "capabilities": {"runtime": "simulator"}})
        self.assertEqual(a.status_code, 201, a.text)
        access = a.json()
        self.sid = access["session_id"]
        self.prefix = "/v1/sessions/" + self.sid
        self.headers = {"Authorization": "Bearer " + access["token"]}
        b = self.client.post("/v1/join", json={"display_name": "B", "device_id": "b",
                                                "join_code": access["snapshot"]["join_code"]}).json()
        self.peer = {"Authorization": "Bearer " + b["token"]}
        self.actor = access["snapshot"]["host_id"]
        self.original = access["snapshot"]["committed_version_id"]
        self.start_meeting()

    def start_meeting(self):
        self.command("meeting_start", {"mode": "live", "input_mode": "manual", "design_submit_mode": "explicit"})

    def snapshot(self, headers=None):
        response = self.client.get(self.prefix, headers=headers or self.headers)
        self.assertEqual(response.status_code, 200, response.text)
        return response.json()

    def command(self, kind, payload, *, status=(200, 202), headers=None, cmd_id=None):
        response = self.client.post(self.prefix + "/commands", headers=headers or self.headers, json={
            "type": kind, "command_id": cmd_id or uid("cmd"), "payload": payload})
        self.assertIn(response.status_code, status, response.text)
        return response.json()

    def photo(self, client_id, selection_id, timestamp, *, content=PNG, headers=None, status=201, source="xr_render"):
        response = self.client.post(self.prefix + "/media", headers=headers or self.headers,
            data={"version_id": self.original, "part_id": "outer_shell", "client_request_id": client_id,
                  "selection_id": selection_id, "captured_at_ms": timestamp, "source_mode": source},
            files={"file": ("sample.png", content, "image/png")})
        self.assertEqual(response.status_code, status, response.text)
        return response.json()

    def target(self, key, timestamp, base, revision, uv):
        image = self.photo(key, "hit-" + key, timestamp)
        return {"client_request_id": key, "base_version_id": base, "expected_adopted_revision": revision,
                "reference_selection": {"selection_id": "hit-" + key, "image_id": image["evidence_id"],
                    "part_id": "outer_shell", "reference_version_id": self.original, "hit_point_asset_m": [0, .007, 0],
                    "image_uv": uv, "tracking_state": "tracking", "captured_at_ms": timestamp, "selected_at_ms": timestamp + 4}}

    def staged_range(self, name, refs, pending):
        edge = self.client.app.state.coordinator
        with edge.transaction():
            state = edge.load(self.sid)
            capture = state["meeting_audio"].setdefault("stream-" + name, {
                "status": "open", "segments": {}, "last_sample": 0, "gaps": []})
            if "design_ranges" not in capture:
                boundary(state, capture, "stream-" + name, self.actor, name, True, 100, 0)
                boundary(state, capture, "stream-" + name, self.actor, name, False, 8100, len(refs))
            for seq, ref in enumerate(refs, 1):
                capture["segments"][str(seq)] = {"status": "pending" if pending else "final", **ref}
            if not pending:
                capture["status"] = "eos"
            edge.save(state)

    def spoken(self, *texts):
        refs = []
        for text in texts:
            line = self.command("text", {"text": text})
            refs.append({k: line[k] for k in ("utterance_id", "revision")})
        return refs

    def wait_round(self, rid, wanted="reviewable"):
        deadline = time.monotonic() + 120
        while time.monotonic() < deadline:
            item = next(r for r in self.snapshot()["rounds"] if r["round_id"] == rid)
            if item["status"] not in {"building", "generating", "collecting"}:
                self.assertEqual(item["status"], wanted, (item.get("error"), item.get("question")))
                return item
            time.sleep(.1)
        self.fail("Timed out building " + rid)

    def adopt(self, item):
        state = self.snapshot()
        return self.command("decide", {"round_id": item["round_id"], "revision": item["revision"],
            "candidate_id": item["candidates"][0]["candidate_id"], "outcome": "commit",
            "expected_committed_version_id": state["committed_version_id"],
            "expected_adopted_revision": state["adopted_revision"]}, headers=self.peer)

    def finish_and_check_bundle(self, latest, requests):
        self.command("meeting_end", {})
        deadline = time.monotonic() + 40
        while time.monotonic() < deadline:
            state = self.snapshot()
            if state["session_status"] in {"completed", "save_error"}:
                break
            time.sleep(.1)
        self.assertEqual(state["session_status"], "completed", state["result"])
        result = self.client.get(state["result"]["zip_url"], headers=self.headers)
        self.assertEqual(result.status_code, 200, result.text[:80] if result.status_code != 200 else "")
        with zipfile.ZipFile(io.BytesIO(result.content)) as archive:
            manifest = json.loads(archive.read("manifest.json"))
            model = next(v for v in state["versions"] if v["version_id"] == latest)
            self.assertEqual(manifest["status"], "complete", manifest["missing"])
            self.assertEqual(manifest["adopted_version_id"], latest)
            self.assertEqual(hashlib.sha256(archive.read(manifest["final_stl"])).hexdigest(),
                             model["assets"]["print.stl"]["sha256"])
            expected = f'Latest · {model["design_version"]} · {latest}'
            self.assertIn(expected, archive.read("minutes.txt").decode())
            summary = self.client.get(self.prefix + "/minutes.txt", headers=self.headers)
            self.assertEqual(summary.content, archive.read("minutes.txt"))
            rows = json.loads(archive.read("design_requests.json"))
            self.assertEqual([(r["client_request_id"], r["adopted_version_id"]) for r in rows], requests)
            for row in rows:
                ref = row["reference_selection"]
                self.assertEqual(ref["file"], "evidence/" + ref["image_id"] + ".png")
                self.assertEqual(ref["sha256"], hashlib.sha256(archive.read(ref["file"])).hexdigest())
                self.assertEqual(manifest["files"][ref["file"]]["sha256"], ref["sha256"])


class ExplicitDesignTests(ExplicitDesignCase):
    def test_distinct_hits_for_same_speech_and_no_adoption_exports_v1(self):
        now = int(time.time() * 1000)
        refs = self.spoken("두 번째 홈만 더 좁혀줘.")
        self.staged_range("local-feature", refs, False)
        items = []
        for i in range(2):
            target = self.target("local-" + str(i), now + i, self.original, 0, [.2 + .1 * i, .5])
            target["reference_selection"]["hit_point_asset_m"][0] += .002 * i
            self.command("design_target", target)
            request = {**target, "speech_range_id": "local-feature", "utterance_refs": refs}
            item = self.wait_round(self.command("design_request", request)["round_id"], "needs_input")
            self.assertFalse(item["candidates"])
            self.assertIn("공통", item["question"])
            items.append(item)
        self.assertNotEqual(items[0]["context"]["context_hash"], items[1]["context"]["context_hash"])
        self.assertNotEqual(items[0]["round_id"], items[1]["round_id"])
        self.finish_and_check_bundle(self.original, [("local-0", None), ("local-1", None)])

    def test_provider_receives_the_fixed_photo_and_late_response_cannot_replace_newer_failure(self):
        now = int(time.time() * 1000)
        refs = self.spoken("홈을 조금 좁혀 주세요.")
        self.staged_range("r", refs, False)
        reached = threading.Event()
        release = threading.Event()
        received = []

        def upstream(path, raw):
            content = json.loads(raw)["messages"][1]["content"]
            context = json.loads(content[0]["text"])
            received.append((context, content[1]["image_url"]["url"]))
            if len(received) == 2:
                raise ValueError("simulated provider failure")
            reached.set()
            release.wait(10)
            plan = {**{k: context[k] for k in IDENTITY}, "schema_version": 1, "options": [],
                    "evidence_ids": context["evidence_ids"], "action": "ask_clarification", "question": "어느 치수인가요?"}
            return {"id": "fixture-response", "choices": [{"message": {"content": json.dumps(plan)}}]}

        env = {"AI_GATEWAY_URL": "", "MEETING_PLAN_BACKEND": "provider", "AI_PLANNER_MODEL": "test-fixture"}
        with patch.dict(os.environ, env), patch.object(gateway, "provider_ready", return_value=True), \
                patch.object(gateway, "upstream", side_effect=upstream):
            try:
                rounds = []
                for i in range(2):
                    target = self.target("provider-" + str(i), now + i, self.original, 0, [.5, .5])
                    self.command("design_target", target)
                    rounds.append(self.command("design_request", {**target, "utterance_refs": refs, "speech_range_id": "r"})["round_id"])
                    self.assertTrue(reached.wait(5))
                self.wait_round(rounds[1], "failed")
            finally:
                release.set()
            self.wait_round(rounds[0], "superseded")
        snapshot = self.snapshot()
        self.assertEqual(snapshot["adopted_version_id"], self.original)
        self.assertNotIn("data:image/", json.dumps(snapshot))
        for i, (context, data_url) in enumerate(received):
            self.assertEqual(data_url, "data:image/png;base64," + base64.b64encode(PNG).decode())
            self.assertEqual(context["reference_selection"]["source_mode"], "xr_render")
            self.assertEqual(context["reference_selection"]["selection_id"], "hit-provider-" + str(i))
            self.assertEqual(context["source_utterance_refs"], refs)

    def test_freeze_retry_and_build_again_from_latest_using_physical_original(self):
        now = int(time.time() * 1000)
        speech = ["충전선이 너무 헐렁해서 자꾸 빠져요.", "이번에 선이 빠지지 않도록 수정해주세요."]
        refs = self.spoken(*speech)
        self.staged_range("r1", refs, False)
        self.spoken("전체 길이를 54mm로 바꿔줘.")
        self.assertEqual(self.snapshot()["rounds"], [])
        self.photo("r1", "hit-r1", now, content=b"invalid", status=415)
        self.photo("r1", "hit-r1", now, headers=self.peer, status=400)
        first = self.target("r1", now, self.original, 0, [.25, .5])
        image = self.photo("r1", "hit-r1", now)
        self.assertEqual(image["evidence_id"], first["reference_selection"]["image_id"])
        self.photo("r1", "hit-r1", now, content=PNG + b"x", status=409)
        request = {**first, "utterance_refs": refs, "speech_range_id": "r1"}
        self.command("design_request", request, status=(409,))
        for delta in ({"tracking_state": "lost"}, {"selected_at_ms": now + 1000},
                      {"part_id": "coupling_mount"}, {"image_id": "missing"}):
            self.command("design_target", {**first, "reference_selection": {**first["reference_selection"], **delta}}, status=(409,))
        self.command("design_target", first, headers=self.peer, status=(409,))
        self.command("design_target", first)
        self.command("design_target", first)
        self.command("design_request", {**request, "use_registered_scope": True}, status=(400,))
        self.command("design_request", {**request, "utterance_refs": refs[:-1]}, status=(409,))
        self.command("design_request", {**request, "utterance_refs": [{**refs[0], "revision": 2}, refs[1]]}, status=(409,))
        cmd = uid("post")
        rid = self.command("design_request", request, cmd_id=cmd)["round_id"]
        self.assertEqual(self.command("design_request", request, cmd_id=cmd)["round_id"], rid)
        self.assertEqual(self.command("design_request", request)["round_id"], rid)
        self.command("design_request", {**request, "reference_selection": {**request["reference_selection"],
                     "image_uv": [.3, .5]}}, status=(409,))
        item = self.wait_round(rid)
        self.assertEqual(len(self.snapshot()["rounds"]), 1)
        self.assertEqual(item["context"]["source_utterance_refs"], refs)
        self.assertEqual(item["context"]["request_transcript"], "\n".join(speech))
        self.assertEqual(item["context"]["screenshot"]["source_mode"], "xr_render")
        self.assertNotIn("screenshot_data_url", item["context"])
        self.assertEqual(item["proposal_provenance"]["source"], "registered_rules")
        self.assertEqual(len(item["candidates"]), 3)
        state = self.snapshot()
        versions = {v["version_id"]: v for v in state["versions"]}
        for asset in ("print.stl", "preview.glb"):
            self.assertEqual(len({versions[c["version_id"]]["assets"][asset]["sha256"] for c in item["candidates"]}), 3)
        self.assertEqual(state["adopted_version_id"], self.original)
        pending = self.target("interrupted", now + 1, self.original, 0, [.4, .3])
        self.command("design_target", pending)
        decision = self.adopt(item)
        second_base = decision["selected_model_version_id"]
        self.assertEqual(decision["assembly_record"]["reference_selection"]["image_id"], image["evidence_id"])
        self.assertEqual(decision["source_utterance_refs"], refs)
        self.command("design_request", {**pending, "speech_range_id": "r1", "utterance_refs": refs}, status=(409,))
        # Photograph V1 first. A pending transcription is never replaced by a later, unrelated final.
        next_target = self.target("r2", now + 2, second_base, 1, [.1, .7])
        self.command("design_target", next_target)
        next_refs = self.spoken("전체 길이를 60mm로 바꿔 주세요.")
        self.staged_range("r2", next_refs, True)
        self.spoken("홈 내부를 더 크게 늘려줘.")
        next_request = {**next_target, "speech_range_id": "r2", "utterance_refs": next_refs}
        self.command("design_request", next_request, status=(409,))
        self.assertEqual(len(self.snapshot()["rounds"]), 1)
        self.staged_range("r2", next_refs, False)
        second = self.wait_round(self.command("design_request", next_request)["round_id"])
        self.assertEqual(len(second["candidates"]), 1)
        context = second["context"]
        self.assertEqual(context["reference_mapping"], "semantic_part_only")
        self.assertEqual(context["edit_base_version_id"], second_base)
        self.assertEqual(context["screenshot"]["version_id"], self.original)
        self.assertEqual(context["source_utterance_refs"], next_refs)
        self.assertIsNone(context["view"]["hit_point_asset_m"])
        self.assertEqual(context["reference_selection"]["hit_point_asset_m"], [0, .007, 0])
        adopted = self.adopt(second)
        state = self.snapshot(self.peer)
        model = next(v for v in state["versions"] if v["version_id"] == state["adopted_version_id"])
        self.assertEqual(model["version_id"], adopted["selected_model_version_id"])
        self.assertEqual(model["design_version"], "V3")
        self.assertAlmostEqual(model["measurements"]["width_mm"], 60, places=3)
        self.assertEqual(model["parameters"]["throat_width"], versions[second_base]["parameters"]["throat_width"])
        self.assertNotEqual(model["parameters"]["throat_width"], versions[self.original]["parameters"]["throat_width"])
        self.finish_and_check_bundle(model["version_id"], [("r1", second_base), ("r2", model["version_id"])])
