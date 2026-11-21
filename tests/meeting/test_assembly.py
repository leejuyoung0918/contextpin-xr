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

    def request_design(self, text, base=None):
        note = self.command("text", {"text": text}, 200)
        before = self.state()
        return self.command("design_request", {"base_version_id": base or before["committed_version_id"],
                                                "expected_adopted_revision": before["adopted_revision"],
                                                "part_id": "outer_shell", "utterance_ids": [note["utterance_id"]]}, 200)

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
        next_round = self.wait(self.request_design("이 후보에서 조금 더", c["version_id"]))
        self.assertEqual(next_round["edit_base_version_id"], c["version_id"])
        v = self.version(next_round["candidates"][0]["version_id"])
        self.assertNotEqual(v["geometry_hash"], models[0]["geometry_hash"])
        self.assertEqual(v["parent_version_id"], c["version_id"])

    def test_locked_change_and_old_planner_completion(self):
        self.command("edit", {"parameters": {"mounting_origin": 3}}, 400)
        request = self.request_design("결합 부분을 넓혀줘")
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

    def wait_session(self, desired):
        deadline = time.monotonic() + 25
        while time.monotonic() < deadline:
            state = self.state()
            if state["session_status"] == desired:
                return state
            if state["session_status"] == "save_error":
                self.fail(str(state["result"]))
            time.sleep(.04)
        self.fail(f"Expected {desired}, got {state['session_status']}")

    def show_candidate(self, review, index, share=True):
        item = review["candidates"][index]
        self.command("view", {"version_id": item["version_id"], "round_id": review["round_id"],
                              "candidate_id": item["candidate_id"], "share": share}, 200)
        return item

    def test_meeting_freezes_adopted_a_while_b_is_shared_and_exports_all_records(self):
        self.command("meeting_start", {"mode": "replay"}, 200)
        response = self.command("replay", {"sample": "curves"})
        review = self.wait(response)
        state = self.state()
        self.assertIsNone(state["shared_preview"])
        self.assertEqual(state["adopted_version_id"], self.initial["version_id"])
        self.assertGreaterEqual(state["summary"]["proposal_count"], 2)
        self.assertGreater(state["design_revision"], 0)
        note = self.command("text", {"text": "B가 좋네"}, 200)
        self.assertNotIn("round_id", note)
        self.assertEqual(len(self.state()["rounds"]), len(state["rounds"]))
        self.assertEqual(self.state()["design_revision"], state["design_revision"])
        a = self.show_candidate(review, 0)
        decided = self.command("decide", {"outcome": "commit", "round_id": review["round_id"], "revision": review["revision"],
                              "candidate_id": a["candidate_id"], "expected_adopted_revision": 0,
                              "expected_committed_version_id": self.initial["version_id"], "reason": "모서리 각을 줄임"}, 200)
        self.assertEqual(decided["evidence_utterances"][0]["utterance_id"], response["utterance_id"])
        b = self.show_candidate(review, 1)
        self.assertEqual(self.state()["shared_preview"]["version_id"], b["version_id"])
        self.assertEqual(self.state()["adopted_version_id"], a["version_id"])
        end = {"command_id": "repeat-end", "type": "meeting_end", "payload": {}}
        for _ in range(2):
            self.assertEqual(self.client.post(self.parent + "/commands", json=end, headers=self.headers).status_code, 200)
        saved = self.wait_session("completed")
        self.assertEqual(saved["result"]["revision"], 1)
        finalized = self.get(saved["result"]["api_url"]).json()
        self.assertEqual(finalized["manifest"]["adopted_version_id"], a["version_id"])
        self.assertEqual(finalized["manifest"]["utterance_count"], 2)
        self.assertIn("B가 좋네", finalized["transcript_text"])
        self.assertIn(response["utterance_id"], finalized["summary_markdown"])
        self.assertIn("모서리 각을 줄임", finalized["summary_markdown"])
        self.assertEqual(finalized["manifest"]["final_scad"], "cad/final.scad")
        adopted = self.version(a["version_id"])
        saved_zip = self.get(saved["result"]["zip_url"]).content
        with zipfile.ZipFile(io.BytesIO(saved_zip)) as export:
            self.assertEqual(export.read("cad/final.stl"), self.get(adopted["stl_url"]).content)
            self.assertEqual(export.read("cad/final.scad"), self.get(adopted["assets"]["candidate.scad"]["url"]).content)
            self.assertEqual(json.loads(export.read("cad/parameters.json")), adopted["parameters"])
            self.assertIn(f"candidates/{b['candidate_id']}/review.json", export.namelist())
            for file, props in finalized["manifest"]["files"].items():
                self.assertEqual(hashlib.sha256(export.read(file)).hexdigest(), props["sha256"])
        self.command("transcript_correction", {"utterance_id": response["utterance_id"], "text": "외형 중 모서리 쪽을 강조"}, 200)
        limit = time.monotonic() + 10
        while time.monotonic() < limit and self.state()["result"]["revision"] == 1:
            time.sleep(.04)
        corrected = self.get(self.parent + "/result").json()
        self.assertEqual(corrected["manifest"]["revision"], 2)
        self.assertEqual(corrected["manifest"]["stl_sha256"], finalized["manifest"]["stl_sha256"])
        self.assertIn("이후 정정되었습니다", corrected["summary_markdown"])
        self.assertEqual(self.get(saved["result"]["zip_url"]).content, saved_zip)

    def test_failed_publish_preserves_frozen_original_and_retries(self):
        self.command("meeting_start", {"mode": "replay"}, 200)
        talk = self.command("text", {"text": "회의 목적을 공유했습니다"}, 200)
        self.assertNotIn("round_id", talk)
        with patch("backend.meeting.meeting_result.build_result", side_effect=OSError("disk fault")):
            self.command("meeting_end", {}, 200)
            deadline = time.monotonic() + 4
            while self.state()["session_status"] == "finalizing" and time.monotonic() < deadline:
                time.sleep(.04)
            failed = self.state()
            self.assertEqual(failed["session_status"], "save_error")
            self.assertEqual(failed["meeting"]["adopted_version_id"], self.initial["version_id"])
        self.assertEqual(self.client.get(self.parent + "/result.zip", headers=self.headers).status_code, 404)
        self.command("meeting_retry", {}, 200)
        saved = self.wait_session("completed")
        result = self.get(saved["result"]["api_url"]).json()
        self.assertEqual(result["manifest"]["adopted_version_id"], self.initial["version_id"])
        self.assertIn("변경 채택 없음", result["summary_markdown"])
        self.assertIn("회의 목적", result["transcript_text"])

    def test_design_speech_uses_only_acknowledged_pcm_between_button_edges(self):
        import base64
        import struct
        import wave
        from backend.meeting.speech_ranges import validate_refs
        from backend.meeting.contracts import MeetingError

        coordinator = self.client.app.state.coordinator
        coordinator.mode = "live"
        coordinator.speech_available = True
        state = self.command("meeting_start", {"mode": "live", "design_submit_mode": "explicit"}, 200)["snapshot"]
        sid = state["session_id"]
        actor = state["host_id"]
        started = threading.Event()
        release = threading.Event()
        chunks = []
        def provider(path, data):
            self.assertEqual(path, "/v1/transcriptions")
            with wave.open(io.BytesIO(base64.b64decode(data["wav_base64"]))) as wav:
                values = struct.unpack("<" + "h" * wav.getnframes(), wav.readframes(wav.getnframes()))
            voiced = {v for v in values if v}
            self.assertEqual(len(voiced), 1, "PCM from either side of the button was mixed into the selected utterance.")
            value = voiced.pop()
            chunks.append((value, values.count(value)))
            if value == 4200:
                started.set()
                release.wait(5)
            if value == 5500:
                raise MeetingError("mocked STT failure")
            return {"status": "final", "transcript": {2300: "이전 문장", 4100: "홈을 넓혀줘",
                    4200: "수정안 보여줘", 5300: "다음 문장"}.get(value, "홈 검토")}

        def wait_window(key, status):
            deadline = time.monotonic() + 6
            while time.monotonic() < deadline:
                window = self.state()["design_speech_ranges"].get(key, {})
                if window.get("status") == status:
                    return window
                time.sleep(.02)
            self.fail(f"{key}: expected {status}, got {window}")

        try:
            with patch.dict(os.environ, {"VAD_END_SILENCE_MS": "120"}), patch(
                    "backend.meeting.planner.gateway_request", side_effect=provider):
                with self.client.websocket_connect("/v1/audio-streams/ranged") as socket:
                    socket.send_json({"session_id": sid, "token": self.headers["Authorization"].split()[1],
                                      "device_id": "pc", "stream_id": "ranged", "actual_sample_rate_hz": 8000,
                                      "channels": 1, "codec": "pcm_s16le", "stream_start_sample_index": 0})
                    self.assertEqual(socket.receive_json()["type"], "ready")
                    sample = seq = 0
                    def send(value, frames=1800):
                        nonlocal seq, sample
                        socket.send_bytes(struct.pack("<IQI", seq, sample, frames) + struct.pack("<h", value) * frames)
                        seq += 1
                        sample += frames
                    def mark(key, edge):
                        socket.send_json({"type": "design_speech_boundary", "range_id": key, "edge": edge,
                                          "sample_index": sample})

                    send(2300)
                    mark("multiple", "start")
                    mark("multiple", "start")  # retry of the identical boundary
                    send(4100)
                    send(0, 1000)
                    send(4200)
                    mark("multiple", "end")
                    mark("multiple", "end")
                    send(5300)
                    self.assertTrue(started.wait(5))
                    working = wait_window("multiple", "transcribing")
                    self.assertEqual((working["start_sample"], working["end_sample"], working["received_through_sample"]),
                                     (1800, 6400, 6400))
                    self.assertEqual(self.state()["rounds"], [])
                    release.set()
                    window = wait_window("multiple", "final")
                    self.assertEqual([u["text"] for u in window["utterances"]], ["홈을 넓혀줘", "수정안 보여줘"])
                    self.assertCountEqual(chunks, [(2300, 1800), (4100, 1800), (4200, 1800)])
                    refs = [{k: u[k] for k in ("utterance_id", "revision")} for u in window["utterances"]]
                    with coordinator.lock:
                        saved = coordinator.load(sid)
                    self.assertEqual([u["text"] for u in validate_refs(saved, actor, "multiple", refs)],
                                     ["홈을 넓혀줘", "수정안 보여줘"])
                    for bad in ([], refs[:1], refs[::-1], [dict(refs[0], revision=2), refs[1]]):
                        with self.assertRaises(MeetingError):
                            validate_refs(saved, actor, "multiple", bad)
                    with self.assertRaises(MeetingError):
                        validate_refs(saved, "other-actor", "multiple", refs)
                    mark("silent", "start")
                    send(0)
                    mark("silent", "end")
                    self.assertEqual(wait_window("silent", "no_speech")["utterances"], [])
                    mark("failed", "start")
                    send(5500)
                    mark("failed", "end")
                    self.assertTrue(wait_window("failed", "failed")["error"])
                    mark("limit", "start")
                    for value in range(6000, 6006):
                        send(value)
                        send(0, 1000)
                    mark("limit", "end")
                    self.assertEqual(len(wait_window("limit", "failed")["utterances"]), 6)
                    mark("missing", "start")
                    seq += 1
                    sample += 200
                    send(4100)
                    mark("missing", "end")
                    self.assertIn("공백", wait_window("missing", "failed")["error"])
                    socket.send_json({"type": "stop", "last_sequence": seq-1})
                    self.assertEqual(socket.receive_json()["type"], "stopped")
                self.assertEqual(self.state()["rounds"], [])
        finally:
            release.set()

    def test_single_continuous_audio_waits_for_eos_and_late_stt_revises_only_transcript(self):
        import struct
        coord = self.client.app.state.coordinator
        coord.mode = "live"
        coord.speech_available = True
        coord.planner = lambda context, mode: fixture_plan(context)
        first = threading.Event()
        tail = threading.Event()
        release_tail = threading.Event()
        segments = []
        def provider(path, _):
            self.assertEqual(path, "/v1/transcriptions")
            segments.append(True)
            if len(segments) == 1:
                first.set()
                return {"status": "final", "transcript": "시제품 회의 목적을 공유합니다."}
            tail.set()
            release_tail.wait(12)
            return {"status": "final", "transcript": "두 번째 발화는 가장자리를 더 둥글게 하자는 제안입니다."}

        armed = self.command("meeting_start", {"mode": "live"}, 200)["snapshot"]
        self.assertEqual(armed["session_status"], "ready")
        self.assertEqual(armed["capture_state"], "off")
        rate = 8000
        instant = int(time.time() * 1000) - 2500
        try:
            with patch.dict(os.environ, {"VAD_END_SILENCE_MS": "160", "MEETING_FINALIZE_WAIT_SECONDS": ".2"}), patch(
                    "backend.meeting.planner.gateway_request", side_effect=provider):
                with self.client.websocket_connect("/v1/audio-streams/continuous") as socket:
                    hello = {"session_id": self.initial["preview_url"].split("/")[3],
                             "token": self.headers["Authorization"].split()[1], "device_id": "pc",
                             "stream_id": "continuous", "actual_sample_rate_hz": rate, "channels": 1,
                             "codec": "pcm_s16le", "stream_start_sample_index": 0,
                             "stream_start_source_monotonic_ms": 100, "clock_offset_ms": instant - 100,
                             "clock_uncertainty_ms": 8}
                    socket.send_json(hello)
                    self.assertEqual(socket.receive_json()["type"], "ready")
                    self.assertEqual(self.state()["capture_state"], "starting")
                    def frame(index, voiced):
                        count = 320
                        socket.send_bytes(struct.pack("<IQI", index, index * count, count) +
                                          struct.pack("<h", 2500 if voiced else 0) * count)
                    for index in range(11):
                        frame(index, index < 6)
                    self.assertTrue(first.wait(4))
                    self.assertEqual(self.state()["session_status"], "recording")
                    for index in range(11, 17):
                        frame(index, True)
                    self.command("meeting_end", {}, 200)
                    socket.send_json({"type": "stop", "last_sequence": 16})
                    self.assertEqual(socket.receive_json()["type"], "stopped")
                    self.assertTrue(tail.wait(4))
                saved = self.wait_session("completed")
                self.assertEqual(saved["result"]["status"], "partial")
                original = self.get(saved["result"]["api_url"]).json()
                self.assertEqual(original["manifest"]["utterance_count"], 1)
                planned = len(saved["rounds"])
                release_tail.set()
                deadline = time.monotonic() + 10
                while self.state()["result"].get("revision", 0) < 2 and time.monotonic() < deadline:
                    time.sleep(.04)
                new = self.get(self.parent + "/result").json()
                self.assertEqual(new["manifest"]["status"], "complete")
                self.assertEqual(new["manifest"]["revision"], 2)
                self.assertEqual(len(new["transcript"]), 2)
                self.assertIsNone(new["transcript"][-1]["speaker_id"])
                self.assertEqual(new["manifest"]["stl_sha256"], original["manifest"]["stl_sha256"])
                self.assertEqual(len(self.state()["rounds"]), planned)
        finally:
            release_tail.set()
