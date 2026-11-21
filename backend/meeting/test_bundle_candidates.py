"""HTTP edit bundles → mocked multimodal proposals → actual OpenSCAD and authenticated version assets."""
import hashlib
import io
import json
import threading
import time
import zipfile
from concurrent.futures import ThreadPoolExecutor
from unittest.mock import patch

from . import bundle_candidates, bundle_interpretation
from .contracts import uid
from .test_edit_bundle import EditBundleCase


class CandidateSetCase(EditBundleCase):
    def save_request(self, key, text="선택한 위치를 기준으로 케이블 홈 전체의 지름을 좀 더 넓혀줘."):
        self.transcript = text
        _, window = self.record(key)
        target = self.spatial_target(key)
        target["expected_adopted_revision"] = self.snapshot()["adopted_revision"]
        self.command("design_target", target)
        return self.command("design_request", self.request(target, window))

    def wait_set(self, key):
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            item = self.snapshot().get("candidate_sets", {}).get(key)
            if item and item["status"] not in ("generating", "building"):
                return item
            time.sleep(.03)
        self.fail("Candidate set took too long: " + key)

    def proposed(self, ctx, width_a, width_b):
        key = "groove_diameter"
        return {"action": "propose", "ambiguity_type": "degree", "request_id": ctx["request_id"],
                "base_version_id": ctx["base_version_id"], "question": None,
                "interpreted_target": {"description": "다섯 홈의 안쪽 지름", "target_part": "outer_shell",
                    "target_parameter_candidates": [key], "evidence": "광선의 선택점, CAD와 사진의 홈 위치"},
                "candidates": [{"candidate_key": letter, "interpretation": f"모든 홈 내부를 {value} mm로 넓히기",
                                "parameter_changes": {key: value}} for letter, value in (("A", width_a), ("B", width_b))]}


class CandidateSetTests(CandidateSetCase):
    candidate_mode = {"candidate_generation": "ai"}

    def commit_payload(self, item, candidate):
        return {"meeting_id": self.sid, "base_version_id": item["base_version_id"],
                "request_id": item["request_id"], "candidate_set_id": item["candidate_set_id"],
                "candidate_id": candidate["candidate_id"], "expected_latest_version_id": item["base_version_id"]}

    def test_async_generation_keeps_captured_v2_through_a_new_adoption(self):
        v1 = self.original
        item = self.wait_round(self.command("edit", {"parameters": {"throat_width": 3.8}})["round_id"])
        v2 = self.original = self.adopt(item)["selected_model_version_id"]
        entered, release = threading.Event(), threading.Event()
        self.addCleanup(release.set)
        def provider(ctx, images):
            self.assertEqual(ctx["base_version_id"], v2)
            self.assertIn("홈 전체", ctx["transcript"])
            self.assertEqual(ctx["base_parameters"]["throat_width"], 3.8)
            self.assertEqual(ctx["selection"]["part_id"], "outer_shell")
            self.assertEqual([mime for mime, raw in images], ["image/png", "image/png"])
            entered.set()
            if not release.wait(30):
                raise AssertionError("Candidate test timed out waiting for concurrent adoption.")
            return self.proposed(ctx, 6.1, 6.5), {"source": "test_provider", "images_sent": 2}
        with patch.object(bundle_interpretation, "call_provider", side_effect=provider) as mock:
            saved = self.save_request("phase2-v2")
            self.assertTrue(entered.wait(10))
            key = saved["request_id"]
            self.assertEqual(saved["snapshot"]["candidate_sets"][key]["status"], "generating")
            existing = self.command("generate_edit_candidates", {"request_id": key})
            self.assertEqual(existing["candidate_set_id"], saved["candidate_set_id"])
            later = self.wait_round(self.command("edit", {"parameters": {"slot_pitch": 14.3}})["round_id"])
            v3 = self.adopt(later)["selected_model_version_id"]
            release.set()
            result = self.wait_set(key)
            self.assertEqual(result["status"], "ready", result)
            self.assertEqual(mock.call_count, 1)
            self.assertEqual(self.command("generate_edit_candidates", {"request_id": key})["candidate_set_id"], saved["candidate_set_id"])
            self.assertEqual(mock.call_count, 1)
        self.assertEqual(result["base_version_id"], v2)
        snapshot = self.snapshot()
        versions = {v["version_id"]: v for v in snapshot["versions"]}
        self.assertEqual(snapshot["adopted_version_id"], v3)
        self.assertNotEqual(v1, v2)
        self.command("commit_edit_candidate", self.commit_payload(result, result["candidates"][0]), status=(409,))
        self.assertEqual([c["label"] for c in result["candidates"]], ["A", "B"])
        folder = self.edge.root / "edit_requests" / self.sid / key
        self.assertEqual(json.loads((folder / "candidate_set.json").read_text()), result)
        surfaces = set()
        for cand in result["candidates"]:
            model = versions[cand["version_id"]]
            self.assertEqual(model["parent_version_id"], v2)
            self.assertEqual(cand["full_parameters"]["slot_count"], 5)
            self.assertEqual(cand["full_parameters"]["throat_width"], 3.8)
            self.assertEqual(cand["full_parameters"]["slot_pitch"], versions[v2]["parameters"]["slot_pitch"])
            self.assertEqual([d["parameter_id"] for d in cand["parameter_diff"]], ["groove_diameter"])
            self.assertEqual(cand["validation"]["status"], "passed")
            for asset_key in ("scad", "stl", "preview_asset"):
                asset = cand[asset_key]
                response = self.client.get(asset["url"], headers=self.peer)
                self.assertEqual(response.status_code, 200)
                self.assertEqual(hashlib.sha256(response.content).hexdigest(), asset["sha256"])
            surfaces.add(cand["validation"]["surface_sha256"])
        self.assertEqual(len(surfaces), 2)

    def test_one_invalid_candidate_does_not_build_or_replace_its_sibling(self):
        with patch.object(bundle_interpretation, "call_provider",
                          side_effect=lambda c, images: (self.proposed(c, 6.1, 9000), {"source": "test_provider"})):
            r = self.save_request("bad-B")
            item = self.wait_set(r["request_id"])
        self.assertEqual(item["status"], "ready", item)
        self.assertEqual([c["label"] for c in item["candidates"]], ["A"])
        self.assertTrue(item["limitations"])
        self.assertTrue(item["reason"])
        self.assertEqual(len(self.snapshot()["versions"]), 2)

    def test_commit_two_exact_candidates_including_retry_conflict_and_zip(self):
        def provider(ctx, images):
            raw = self.proposed(ctx, 6.1, 6.5)
            if ctx["request_id"] == "second":
                self.assertEqual(ctx["base_parameters"]["groove_diameter"], 6.5)
                raw["interpreted_target"]["target_parameter_candidates"] = ["slot_pitch"]
                for cand, pitch in zip(raw["candidates"], (14, 13)):
                    cand.update(interpretation="슬롯 간격을 줄여 전체 길이를 줄임", parameter_changes={"slot_pitch":pitch})
            return raw, {"source":"test_provider"}

        with patch.object(bundle_interpretation, "call_provider", side_effect=provider) as invoke:
            for key, text, label in (("first", "홈을 넓혀줘", "V2"), ("second", "전체 길이를 조금 줄여줘", "V3")):
                self.save_request(key, text)
                item = self.wait_set(key)
                self.assertEqual(item["status"], "ready", item)
                self.assertEqual(len(item["candidates"]), 2, item)
                chosen = item["candidates"][1]
                payload = self.commit_payload(item, chosen)
                if key == "second":
                    payload["reason_text"] = "책상 위 공간이 필요합니다."
                self.command("commit_edit_candidate", {**payload, "expected_latest_version_id":"other"}, status=(409,))
                self.command("commit_edit_candidate", {**payload, "candidate_set_id":"other"}, status=(404,))
                before = self.snapshot()
                # Two distinct commands against the same head collapse into a single adoption.
                with ThreadPoolExecutor(max_workers=2) as pool:
                    responses = list(pool.map(lambda _: self.command("commit_edit_candidate", payload), range(2)))
                state = self.snapshot()
                self.assertEqual(len(state["decisions"]), len(before["decisions"])+1)
                self.assertEqual(len(state["versions"]), len(before["versions"]))
                vid = chosen["version_id"]
                self.assertEqual([r["version_id"] for r in responses], [vid,vid])
                chosen_model = next(v for v in state["versions"] if v["version_id"] == vid)
                self.assertEqual((chosen_model["parent_version_id"], chosen_model["design_version"]), (self.original, label))
                self.assertEqual(state["committed_version_id"], vid)
                self.assertEqual(chosen_model["full_parameters"], chosen["full_parameters"])
                self.assertEqual(chosen_model["parameters"]["groove_diameter"], 6.5)
                bundle = state["edit_requests"][key]
                self.assertEqual(chosen_model["evidence"]["hit_model_local_position"], bundle["selection"]["hit_model_local_position"])
                self.assertEqual(chosen_model["evidence"]["image_asset_id"], bundle["visual_evidence"]["image_asset_id"])
                self.assertEqual(chosen_model["reason"], payload.get("reason_text", "not_stated"))
                self.assertEqual(chosen_model["evidence"]["transcript"], text)
                committed = state["candidate_sets"][key]
                self.assertEqual((committed["status"], committed["selected_candidate_id"], committed["committed_version_id"]),
                                 ("committed", chosen["candidate_id"], vid))
                disk = self.edge.root / "edit_requests" / self.sid / key / "candidate_set.json"
                self.assertEqual(json.loads(disk.read_bytes()), committed)
                exported = self.client.get(chosen_model["export_url"], headers=self.peer)
                self.assertEqual(exported.status_code, 200)
                with zipfile.ZipFile(io.BytesIO(exported.content)) as archive:
                    record = json.loads(archive.read("assembly_meeting.json"))
                    self.assertEqual(record["selected_model_sha256"], chosen["stl"]["sha256"])
                    self.assertEqual(record["evidence"], chosen_model["evidence"])
                self.command("commit_edit_candidate", self.commit_payload(item, item["candidates"][0]), status=(409,))
                self.original = vid
            self.assertEqual(invoke.call_count, 2)
        self.command("meeting_end", {})
        self.socket.send_json({"type":"stop", "last_sequence":self.sequence-1})
        deadline = time.monotonic() + 40
        while self.snapshot()["session_status"] not in ("completed", "save_error") and time.monotonic() < deadline:
            time.sleep(.1)
        final = self.snapshot()
        self.assertEqual(final["session_status"], "completed", final["result"])
        response = self.client.get(final["result"]["zip_url"], headers=self.headers)
        with zipfile.ZipFile(io.BytesIO(response.content)) as archive:
            self.assertEqual(json.loads(archive.read("cad/version.json"))["source_request_id"], "second")
            self.assertEqual(hashlib.sha256(archive.read("cad/final.stl")).hexdigest(), chosen["stl"]["sha256"])
            self.assertEqual(json.loads(archive.read("edit_requests/second/request.json")), bundle)
            self.assertEqual(hashlib.sha256(archive.read("edit_requests/first/target.png")).hexdigest(),
                             final["edit_requests"]["first"]["visual_evidence"]["sha256"])

    def test_failed_save_and_competing_selections_preserve_one_head(self):
        with patch.object(bundle_interpretation, "call_provider", side_effect=lambda c, imgs:
                          (self.proposed(c, 6.1, 6.5), {"source":"test_provider"})):
            self.save_request("competing")
            item = self.wait_set("competing")
        payloads = [self.commit_payload(item, c) for c in item["candidates"]]
        self.assertEqual(len(payloads), 2)
        manifest = self.edge.root / "edit_requests" / self.sid / "competing" / "candidate_set.json"
        preview = self.edge.models / item["candidates"][0]["version_id"] / "preview.glb"
        original = preview.read_bytes()
        preview.write_bytes(original + b"corruption")
        self.command("commit_edit_candidate", payloads[0], status=(409,))
        preview.write_bytes(original)
        with patch.object(bundle_candidates, "publish_manifest", side_effect=OSError("disk full")):
            self.command("commit_edit_candidate", payloads[0], status=(503,))
        snapshot = self.snapshot()
        self.assertEqual((snapshot["committed_version_id"], snapshot["decisions"]), (self.original, []))
        self.assertEqual(json.loads(manifest.read_bytes()), item)
        def select(payload):
            return self.client.post(self.prefix+"/commands", headers=self.headers,
                json={"type":"commit_edit_candidate", "command_id":uid("cmd"), "payload":payload})
        with ThreadPoolExecutor(max_workers=2) as pool:
            results = list(pool.map(select, payloads))
        self.assertCountEqual([r.status_code for r in results], [200, 409])
        winner = next(r.json() for r in results if r.status_code == 200)
        after = self.snapshot()
        self.assertEqual(len(after["decisions"]), 1)
        self.assertEqual(after["committed_version_id"], winner["version_id"])
        self.assertEqual(json.loads(manifest.read_bytes()), after["candidate_sets"]["competing"])


class ArchivedCandidateSetTests(CandidateSetCase):
    def test_saved_phase1_request_can_be_processed_once_after_meeting_end(self):
        key = "phase1-archived"
        saved = self.save_request(key)
        self.assertNotIn(key, saved["snapshot"].get("candidate_sets", {}))
        self.command("meeting_end", {})
        self.socket.send_json({"type": "stop", "last_sequence": self.sequence-1})
        deadline = time.monotonic() + 30
        while self.snapshot()["session_status"] != "completed":
            if time.monotonic() > deadline:
                self.fail("Archive did not finish.")
            time.sleep(.05)
        before = self.snapshot()
        self.command("generate_edit_candidates", {"request_id": key}, headers=self.peer, status=(403,))
        with patch.object(bundle_interpretation, "call_provider", side_effect=lambda c, imgs:
                          (self.proposed(c, 6.1, 6.5), {"source": "test_provider"})):
            self.command("generate_edit_candidates", {"request_id": key})
            done = self.wait_set(key)
        self.assertEqual(done["status"], "ready", done)
        current = self.snapshot()
        self.assertEqual(current["session_status"], "completed")
        self.assertEqual(current["adopted_version_id"], before["adopted_version_id"])
        self.assertEqual(current["result"], before["result"])
        self.assertEqual(current["edit_requests"][key], before["edit_requests"][key])
