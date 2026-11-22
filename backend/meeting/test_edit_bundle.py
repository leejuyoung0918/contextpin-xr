"""Exercise captured edit requests through the HTTP and PCM WebSocket APIs."""
import base64
import hashlib
import io
import json
import math
import struct
import time
import wave
from datetime import datetime, timezone
from unittest.mock import patch

from . import meeting_result, planner
from .contracts import MeetingError
from .test_explicit_design import ExplicitDesignCase, PNG


class EditBundleCase(ExplicitDesignCase):
    candidate_mode = {}
    sample_rate_hz = 8000

    def start_meeting(self):
        self.edge = self.client.app.state.coordinator
        self.edge.speech_available = True
        self.transcript = "두 번째 홈의 입구를 좁혀 주세요."
        self.stt_pcm = []
        self.enterContext(patch.object(planner, "gateway_request", side_effect=self.stt))
        self.command("meeting_start", {"mode": "live", "input_mode": "microphone",
                                      "design_submit_mode": "explicit", "edit_request_output": "bundle", **self.candidate_mode})
        self.socket = self.enterContext(self.client.websocket_connect("/v1/audio-streams/bundle-stream"))
        self.stream_epoch_ms = time.time_ns() // 1_000_000
        monotonic_ms = time.monotonic_ns() // 1_000_000
        self.edges = {}
        self.socket.send_json({"session_id": self.sid, "token": self.headers["Authorization"].split()[1],
                               **getattr(self, "stream_options", {}),
                               "device_id": "a", "actual_sample_rate_hz": self.sample_rate_hz, "channels": 1,
                               "codec": "pcm_s16le", "stream_start_sample_index": 0,
                               "stream_start_source_monotonic_ms": monotonic_ms,
                               "clock_offset_ms": self.stream_epoch_ms - monotonic_ms, "clock_uncertainty_ms": 0})
        self.assertEqual(self.socket.receive_json()["type"], "ready")
        self.sequence = self.sample = 0
        self.send_pcm(0, 300)

    def stt(self, path, body):
        self.assertEqual(path, "/v1/transcriptions")
        with wave.open(io.BytesIO(base64.b64decode(body["wav_base64"]))) as audio:
            self.stt_pcm.append(audio.readframes(audio.getnframes()))
        if self.transcript is None:
            raise MeetingError("test STT unavailable")
        return {"status": "final", "transcript": self.transcript}

    def send_pcm(self, amplitude, samples):
        pcm = struct.pack("<h", amplitude) * samples
        self.socket.send_bytes(struct.pack("<IQI", self.sequence, self.sample, samples) + pcm)
        self.sequence += 1
        self.sample += samples
        return pcm

    def mark(self, key, edge):
        self.edges.setdefault(key, {})[edge] = self.sample
        self.socket.send_json({"type": "design_speech_boundary", "range_id": key, "edge": edge,
                               "sample_index": self.sample, "base_version_id": self.original})

    def await_range(self, key, status):
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline:
            row = self.snapshot()["design_speech_ranges"].get(key, {})
            if row.get("status") == status:
                return row
            time.sleep(.02)
        self.fail(f"Expected range {key} to reach {status}: {row}")

    def record(self, key, amplitude=3600, terminal="final", lead_silence=0):
        self.mark(key, "start")
        started = self.await_range(key, "recording")
        self.assertEqual(started["base_version_id"], self.original)
        self.assertEqual(started["start_sample"], self.sample)
        self.assertNotIn("edit_request_draft", started)
        pcm = self.send_pcm(0, lead_silence) if lead_silence else b""
        pcm += self.send_pcm(amplitude, 1800) + self.send_pcm(0, 120)
        self.mark(key, "end")
        ended = self.await_range(key, terminal)
        self.assertEqual((ended["end_sample"], ended["received_through_sample"]), (self.sample, self.sample))
        if terminal != "final":
            self.assertNotIn("edit_request_draft", ended)
        return pcm, ended

    def spatial_target(self, key):
        stamp = time.time_ns() // 1_000_000
        target = self.target(key, stamp, self.original, 0, [.25, .5])
        ref = target["reference_selection"]
        def instant(ms):
            return datetime.fromtimestamp(ms / 1000, timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")
        pose = {"position": [.05, .70, -.35], "rotation_xyzw": [0, math.sqrt(.5), 0, math.sqrt(.5)]}
        ref.update(object_id=self.snapshot()["profile"]["object_id"], base_version_id=self.original,
                   timestamp=instant(ref["selected_at_ms"]), capture_timestamp=instant(ref["captured_at_ms"]),
                   workspace_revision=1, hit_model_local_position=[.01, .007, 0], hit_point_asset_m=[-.01, .007, 0],
                   ray_origin_world=[.05, .707, -1.36], ray_direction_world=[0, 0, 1],
                   hit_world_position=[.05, .707, -.36], hit_normal_world=[0, 0, -1],
                   latest_model_pose=pose, camera_pose={"position": [0, 1, -1.1], "rotation_xyzw": [0, 0, 0, 1]},
                   workspace_pose={"position": [0, .70, -.30], "rotation_xyzw": [0, 0, 0, 1]},
                   image_source="rendered_debug", actual_rgb="unavailable", source_mode="xr_render")
        return target

    def request(self, target, window):
        lines = window["utterances"]
        ids = {line["utterance_id"] for line in lines}
        received_at = max(u["received_at"] for u in self.snapshot()["utterances"] if u["utterance_id"] in ids)
        draft = window["edit_request_draft"]
        expected = {"request_id": target["client_request_id"], "meeting_id": self.sid,
                    "base_version_id": self.original, "utterance_id": lines[0]["utterance_id"],
                    "transcript": " ".join(line["text"] for line in lines),
                    "transcript_source": window["transcript_source"], "created_at": received_at}
        self.assertEqual({key: draft[key] for key in expected}, expected)
        return {**target, "output": "bundle", "speech_range_id": target["client_request_id"],
                "utterance_refs": [{k: item[k] for k in ("utterance_id", "revision")} for item in lines]}

    def assert_bundle_files(self, request, pcm, text, source):
        key = request["client_request_id"]
        folder = self.edge.root / "edit_requests" / self.sid / key
        self.assertEqual({p.name for p in folder.iterdir()}, {"request.json", "transcript.json", "command.wav", "target.png"})
        meta = json.loads((folder / "request.json").read_text())
        self.assertEqual((meta["request_id"], meta["base_version_id"], meta["meeting_id"]), (key, self.original, self.sid))
        transcript = json.loads((folder / "transcript.json").read_text())
        self.assertEqual(meta["transcript"], transcript)
        self.assertEqual((transcript["text"], transcript["source"], transcript["utterance_refs"]),
                         (text, source, request["utterance_refs"]))
        self.assertEqual(meta["command_utterance_ids"], [u["utterance_id"] for u in request["utterance_refs"]])
        self.assertEqual(meta["command_text"], text)
        self.assertNotIn("edit_request_draft", self.snapshot()["design_speech_ranges"][key])
        for edge in ("start", "end"):
            index = self.edges[key][edge]
            self.assertEqual(meta["audio"][edge + "_sample"], index)
            expected_ms = self.stream_epoch_ms + round(index * 1000 / 8000)
            actual = datetime.fromisoformat(transcript[edge + "_time"])
            self.assertEqual(actual, datetime.fromtimestamp(expected_ms / 1000, timezone.utc))
        image = meta["visual_evidence"]
        self.assertEqual((image["image_source"], image["actual_rgb"], image["image_asset_id"]),
                         ("rendered_debug", "unavailable", request["reference_selection"]["image_id"]))
        self.assertEqual(image["sha256"], hashlib.sha256(PNG).hexdigest())
        self.assertEqual((folder / image["file"]).read_bytes(), PNG)
        for key in ("camera_pose", "selection_id", "capture_timestamp"):
            self.assertEqual(image[key], request["reference_selection"][key])
        for key in ("hit_world_position", "hit_model_local_position", "hit_point_asset_m", "ray_origin_world",
                    "ray_direction_world", "workspace_revision", "workspace_pose", "latest_model_pose"):
            self.assertEqual(meta["selection"][key], request["reference_selection"][key])
        self.assertEqual(meta["audio"]["sha256"], hashlib.sha256((folder / "command.wav").read_bytes()).hexdigest())
        with wave.open(str(folder / "command.wav")) as audio:
            self.assertEqual((audio.getframerate(), audio.getsampwidth(), audio.getnchannels()), (8000, 2, 1))
            self.assertEqual(audio.readframes(audio.getnframes()), pcm)
        return folder

class EditBundleTests(EditBundleCase):
    def test_final_transcript_survives_cad_export_failure_and_retry(self):
        self.transcript = "전체 크기는 괜찮은 것 같아."
        self.send_pcm(3500, 2000)
        self.socket.send_json({"type": "stop", "last_sequence": self.sequence - 1})
        self.assertEqual(self.socket.receive_json()["type"], "stopped")

        def wait_status(status):
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                if self.snapshot()["session_status"] == status:
                    return
                time.sleep(.03)
            self.fail("Meeting did not reach " + status)

        with patch.object(meeting_result, "save_model", side_effect=OSError("CAD export unavailable")):
            self.command("meeting_end", {})
            wait_status("save_error")
        folder = self.edge.root / "results" / self.sid / "transcript"
        raw = (folder / "transcript.jsonl").read_bytes()
        row = json.loads(raw)
        self.assertEqual((row["meeting_id"], row["text"], row["is_final"]), (self.sid, self.transcript, True))
        self.assertIn(self.transcript, (folder / "transcript.txt").read_text())
        self.assertEqual(self.snapshot()["meeting"]["transcript_status"], "finalized")
        self.command("meeting_retry", {})
        wait_status("completed")
        self.assertEqual((folder / "transcript.jsonl").read_bytes(), raw)

    def test_pcm_selection_and_immutable_files(self):
        key = "first-edit"
        # A long silent lead-in is fully recorded even when STT only receives the VAD pre-roll.
        pcm, window = self.record(key, lead_silence=2400)
        self.assertEqual(len(self.stt_pcm), 1)
        self.assertLess(len(self.stt_pcm[0]), len(pcm))
        self.assertTrue(pcm.endswith(self.stt_pcm[0]))
        self.send_pcm(0, 300)
        self.assertEqual(self.snapshot()["rounds"], [])
        self.command("text", {"text": "이미 전사된 발언", "speech_range_id": key}, status=(409,))
        target = self.spatial_target(key)
        ref = target["reference_selection"]
        for changes in ({"actual_rgb": "available"}, {"ray_direction_world": [1, 0, 0]},
                        {"hit_model_local_position": [.02, .007, 0]},
                        {"latest_model_pose": {**ref["latest_model_pose"], "position": [0, .70, -.35]}}):
            self.command("design_target", {**target, "reference_selection": {**ref, **changes}}, status=(409,))
        self.command("design_target", target)
        request = self.request(target, window)
        self.command("design_request", {**request, "utterance_refs": [{**request["utterance_refs"][0], "revision": 77}]},
                     status=(409,))
        self.assertEqual(self.command("design_request", request)["request_id"], key)
        folder = self.assert_bundle_files(request, pcm, self.transcript, "stt")
        original = {p.name: p.read_bytes() for p in folder.iterdir()}
        self.assertEqual(self.command("design_request", request)["request_id"], key)
        self.command("design_request", {**request, "reference_selection": {**ref, "image_uv": [.53, .3]}}, status=(409,))
        self.assertEqual({p.name: p.read_bytes() for p in folder.iterdir()}, original)
        state = self.snapshot()
        self.assertEqual((state["rounds"], state["adopted_version_id"]), ([], self.original))

    def test_manual_recovery_keeps_the_actual_audio(self):
        # A headset can capture an XR render when its RGB sensor is unavailable.
        self.command("capabilities", {"capabilities": {"runtime": "xr", "frame_capture": "denied"}})
        self.transcript = None
        for key, amplitude, failure in (("pc-after-failure", 3500, "failed"), ("pc-after-silence", 0, "no_speech")):
            pcm, _ = self.record(key, amplitude, failure)
            text = "입구 폭을 조금 줄여 주세요."
            self.command("text", {"speech_range_id": key, "text": text}, headers=self.peer)
            window = self.await_range(key, "final")
            self.assertEqual(window["transcript_source"], "manual")
            target = self.spatial_target(key)
            self.command("design_target", target)
            request = self.request(target, window)
            self.command("design_request", request)
            self.assert_bundle_files(request, pcm, text, "manual")
        key = "no-audio"
        self.mark(key, "start")
        self.await_range(key, "recording")
        self.command("text", {"speech_range_id": key, "text": "아직 녹음 중"}, status=(409,))
        self.mark(key, "end")
        self.await_range(key, "failed")
        self.command("text", {"speech_range_id": key, "text": "음성 파일 없음"}, status=(409,))

    def test_range_keeps_v1_when_the_team_adopts_another_version(self):
        key = "old-selection"
        _, window = self.record(key)
        target = self.spatial_target(key)
        self.command("design_target", target)
        item = self.wait_round(self.command("edit", {"parameters": {"throat_width": 3.8}})["round_id"])
        latest = self.adopt(item)["selected_model_version_id"]
        self.assertNotEqual(latest, self.original)
        self.assertEqual(self.snapshot()["design_speech_ranges"][key]["base_version_id"], self.original)
        self.command("design_request", self.request(target, window), status=(409,))
        self.assertFalse((self.edge.root / "edit_requests" / self.sid / key).exists())
        self.mark("outdated-base", "start")
        self.assertEqual(self.socket.receive_json()["type"], "error")
        self.assertNotIn("outdated-base", self.snapshot()["design_speech_ranges"])
