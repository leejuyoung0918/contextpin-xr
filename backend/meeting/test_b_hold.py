"""B-held command integration on one PCM websocket, including a guest's authenticated clock markers."""
import base64
import io
import os
import struct
import threading
import time
import unittest
import wave
from unittest.mock import patch

from . import bundle_interpretation
from .command_window import PcmTail, reject
from .contracts import MeetingError
from .test_bundle_candidates import CandidateSetCase


class PcmTailTests(unittest.TestCase):
    def test_rejected_replayed_id_does_not_shadow_previous_microphone_stream(self):
        original = {"b_status": "final", "participant_id": "host"}
        state = {"meeting_audio": {"first": {"design_ranges": {"request": original}},
                                   "resumed": {"status": "open"}}}
        reject(state, "resumed", "host", "request", 300, "v1", "이미 사용한 요청")
        self.assertEqual(state["meeting_audio"]["resumed"]["design_ranges"], {})
        self.assertEqual(original["b_status"], "final")

    def test_only_gaps_within_the_marked_samples_invalidate_a_clip(self):
        tail = PcmTail(8000, 400)
        packet = struct.pack("<h", 1200) * 2000
        tail.append(0, packet, None)
        tail.append(2500, packet, None)
        with wave.open(io.BytesIO(tail.clip(0, 2000))) as wav:
            self.assertEqual(wav.readframes(2000), packet)
        with self.assertRaises(MeetingError):
            tail.clip(1000, 2800)
        # A jump far into a meeting evicts expired audio while retaining this run.
        tail.append(8000 * 3600, packet, None)
        with self.assertRaises(MeetingError):
            tail.clip(0, 1000)
        self.assertEqual(len(tail.runs), 1)


class BHoldCase(CandidateSetCase):
    stream_options = {"edit_marker_alignment": "speech_overlap", "edit_command_mode": "b_hold"}
    candidate_mode = {"candidate_generation": "ai"}
    phrases = {1000: "일반 회의 발언입니다.", 2000: "홈을 더 넓혀줘.", 3000: "이후 회의를 계속하자."}

    def start_meeting(self):
        self.calls = []
        self.enterContext(patch.dict(os.environ, {"VAD_END_SILENCE_MS": "120"}))
        super().start_meeting()

    def stt(self, path, body):
        self.assertEqual(path, "/v1/transcriptions")
        with wave.open(io.BytesIO(base64.b64decode(body["wav_base64"]))) as wav:
            samples = list(value for (value,) in struct.iter_unpack("<h", wav.readframes(wav.getnframes())))
        tones = {v for v in samples if v}
        self.calls.append((tones, len(samples)))
        text = " ".join(self.phrases[v] for v in sorted(tones))
        return {"status": "final", "transcript": text}

    def wait_lines(self, count):
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline:
            lines = self.snapshot()["utterances"]
            if len(lines) == count:
                return lines
            time.sleep(.02)
        self.fail("continuous transcript did not reach " + str(count))

    def guest_edge(self, rid, edge, at, headers=None, status=(200, 202), **overrides):
        # The client cannot send a real press that is still in this fixture's future.
        delay = (self.stream_epoch_ms + at // 8 - time.time_ns() // 1_000_000) / 1000
        if delay > 0:
            time.sleep(delay)
        stamp = time.monotonic_ns() // 1_000_000
        fields = {"range_id": rid, "edge": edge, "source_monotonic_ms": stamp, "clock_uncertainty_ms": 0,
                  "clock_offset_ms": self.stream_epoch_ms + at // 8 - stamp}
        if edge == "start":
            fields["base_version_id"] = self.original
        return self.command("design_speech_boundary", {**fields, **overrides}, headers=headers or self.peer, status=status)

class BHoldTests(BHoldCase):
    def test_guest_clipped_command_and_target_then_continuous_during_generation(self):
        key = "guest-hold"
        pose = {"position": [.20, 0, .06], "rotation_xyzw": [0, 0, 0, 1]}
        for actor_headers in (self.headers, self.peer):
            self.command("paper_alignment", {"paper_frame_revision": 1, "status": "aligned"}, headers=actor_headers)
        self.command("paper_layout", {"paper_frame_revision": 1, "latest_version_id": self.original, "latest_pose_paper": pose})
        # The single natural room utterance crosses BOTH markers.
        self.send_pcm(1000, 1300)
        start = self.sample
        self.guest_edge(key, "start", start, participant_id=self.actor, status=(400,))
        self.guest_edge(key, "start", start)
        self.send_pcm(2000, 4000)
        end = self.sample
        self.guest_edge(key, "end", end, headers=self.headers, status=(409,))
        self.guest_edge(key, "end", end)
        self.send_pcm(3000, 1600)
        self.send_pcm(0, 1200)
        window = self.await_range(key, "final")
        self.assertEqual((window["start_sample"], window["end_sample"]), (start, end))
        self.assertEqual(window["command_text_source"], "command_window_stt")
        self.assertEqual(window["command_text"], self.phrases[2000])
        # Guest supplied a clock marker; the existing host mic captured the room.
        self.assertNotEqual(window["command_requested_by"], self.actor)
        self.assertIsNone(window["speaker_id"])
        lines = self.wait_lines(1)
        self.assertTrue(all(value in lines[0]["text"] for value in self.phrases.values()))
        self.assertEqual(window["command_utterance_ids"], [lines[0]["utterance_id"]])
        self.assertEqual(lines[0]["captured_by_participant_id"], self.actor)
        self.assertIn(({2000}, 4000), self.calls)
        self.assertEqual(len(self.calls), 2)
        self.assertFalse(self.snapshot()["edit_requests"])
        self.assertFalse(self.snapshot()["candidate_sets"])
        self.command("cancel_design_speech", {"range_id": key}, headers=self.headers, status=(409,))

        host, self.headers = self.headers, self.peer
        try:
            target = self.spatial_target(key)
            target["reference_selection"].update(paper_frame_revision=1, hit_position_paper=[.21, .007, .06],
                ray_origin_paper=[.21, .61, .06], ray_direction_paper=[0, -1, 0])
            self.command("design_target", target)
            self.assertFalse(self.snapshot()["candidate_sets"])
            request = {**target, "output": "bundle", "speech_range_id": key,
                       "utterance_refs": [{k: u[k] for k in ("utterance_id", "revision")} for u in window["utterances"]]}
            called, release = threading.Event(), threading.Event()
            def provider(ctx, images):
                self.assertEqual(ctx["transcript"], self.phrases[2000])
                self.assertNotIn(self.phrases[1000], str(ctx))
                self.assertEqual(len(images), 2)
                called.set()
                if not release.wait(10):
                    raise AssertionError("planner hold timed out")
                return self.proposed(ctx, 6.1, 6.5), {"source": "test_provider"}
            with patch.object(bundle_interpretation, "call_provider", side_effect=provider):
                try:
                    saved = self.command("design_request", request)["snapshot"]["edit_requests"][key]
                    self.assertTrue(called.wait(6))
                    self.send_pcm(3000, 1800)
                    self.send_pcm(0, 1200)
                    self.assertEqual(self.wait_lines(2)[-1]["text"], self.phrases[3000])
                finally:
                    release.set()
                item = self.wait_set(key)
                self.assertEqual(item["status"], "ready")
                candidates = {c["candidate_id"]: {**pose, "position": [i * .1, 0, .24]}
                              for i, c in enumerate(item["candidates"])}
                self.command("paper_layout", {"paper_frame_revision": 1, "latest_version_id": self.original,
                    "active_candidate_set_id": item["candidate_set_id"], "candidate_poses_paper": candidates}, headers=host)
                commit = {"meeting_id": self.sid, "request_id": key, "candidate_set_id": item["candidate_set_id"],
                          "base_version_id": self.original, "expected_latest_version_id": self.original,
                          "candidate_id": item["candidates"][0]["candidate_id"], "paper_frame_revision": 1}
                self.command("commit_edit_candidate", commit, status=(403,))
                adopted = self.command("commit_edit_candidate", commit, headers=host)
                self.assertEqual(adopted["version_id"], item["candidates"][0]["version_id"])
            self.assertEqual(saved["command"]["speaker_id"], None)
            self.assertEqual(saved["base_version_id"], self.original)
            self.assertEqual(saved["command_requested_by"], window["participant_id"])
            self.assertEqual(saved["command"]["command_end_session_ms"] - saved["command"]["command_start_session_ms"], 500)
            self.assertEqual(saved["command_text_source"], "command_window_stt")
            self.assertEqual(saved["command_text"], saved["transcript"]["text"])
            self.assertEqual(saved["command"]["utterance_refs"], request["utterance_refs"])
            with wave.open(str(self.edge.root / "edit_requests" / self.sid / key / "command.wav")) as wav:
                self.assertEqual(wav.readframes(wav.getnframes()), struct.pack("<h", 2000) * 4000)
        finally:
            self.headers = host

    def test_normal_short_overlong_cancel_and_reuse_timeline(self):
        self.send_pcm(1000, 1600)
        self.send_pcm(0, 1100)
        self.wait_lines(1)
        self.assertFalse(self.snapshot()["design_speech_ranges"])
        self.assertEqual(self.snapshot()["edit_command_limits"], {"min_ms": 400, "max_ms": 15000})
        self.mark("short", "start")
        self.send_pcm(1000, 800)
        self.mark("short", "end")
        self.assertFalse(self.await_range("short", "too_short").get("edit_request_draft"))
        self.send_pcm(0, 1200)
        # Configurability exercised without waiting for 15 s of synthetic audio.
        self.edge.edit_command_limits["max_ms"] = 600
        self.mark("long", "start")
        self.send_pcm(2000, 4000)
        self.send_pcm(2000, 1000)
        self.mark("long", "end")
        self.await_range("long", "too_long")
        self.send_pcm(0, 1200)
        self.mark("valid", "start")
        self.send_pcm(2000, 4000)
        self.mark("valid", "end")
        self.send_pcm(0, 1200)
        row = self.await_range("valid", "final")
        self.assertEqual(row["command_requested_by"], self.actor)
        self.assertEqual(row["command_text_source"], "linked_meeting_utterances")
        self.assertEqual(row["command_text"], self.phrases[2000])
        self.assertNotIn(({2000}, 4000), self.calls)  # No extra command-only provider call.
        self.command("cancel_design_speech", {"range_id": "valid"}, headers=self.peer, status=(409,))
        self.command("cancel_design_speech", {"range_id": "valid"})
        self.assertNotIn("edit_request_draft", self.await_range("valid", "cancelled"))
        stamp = time.time_ns() // 1_000_000
        self.photo("valid", "hit", stamp, status=409)
        self.assertFalse(self.snapshot()["edit_requests"])
        self.assertFalse(self.snapshot()["candidate_sets"])

    def test_failed_marker_and_guest_disconnect_do_not_stop_the_microphone(self):
        self.socket.send_json({"type": "design_speech_boundary", "range_id": "stale", "edge": "start",
                               "sample_index": self.sample, "base_version_id": "old-model"})
        self.await_range("stale", "failed")
        self.mark("stale", "end")
        self.socket.send_json({"type": "design_speech_boundary", "range_id": "late-sample", "edge": "start",
                               "sample_index": self.sample - 1, "base_version_id": self.original})
        self.await_range("late-sample", "failed")
        with self.client.websocket_connect(self.prefix + "/events") as peer:
            peer.send_json({"token": self.peer["Authorization"].split()[1]})
            peer.receive_json()
            self.guest_edge("orphan", "start", self.sample)
            peer.close()
            self.await_range("orphan", "failed")
        self.send_pcm(1000, 1800)
        self.send_pcm(0, 1200)
        self.mark("again", "start")
        self.send_pcm(2000, 3600)
        self.mark("again", "end")
        self.send_pcm(0, 1200)
        self.assertEqual(self.await_range("again", "final")["command_text"], self.phrases[2000])
        self.assertEqual([u["text"] for u in self.wait_lines(2)], [self.phrases[1000], self.phrases[2000]])

    def test_clip_failure_does_not_fall_back_to_outside_words(self):
        from . import planner
        def partial_clip(path, payload):
            result = self.stt(path, payload)
            if self.calls[-1][0] == {2000}:
                return {"status": "partial", "transcript": "미확정 명령"}
            return result
        with patch.object(planner, "gateway_request", side_effect=partial_clip):
            self.send_pcm(1000, 1200)
            self.mark("private", "start")
            self.send_pcm(2000, 4000)
            self.mark("private", "end")
            self.send_pcm(3000, 1000)
            self.send_pcm(0, 1200)
            row = self.await_range("private", "failed")
            self.assertEqual(row["command_text"], "")
            self.assertNotIn("edit_request_draft", row)
            self.assertIn(self.phrases[1000], self.wait_lines(1)[0]["text"])
            self.assertFalse(self.snapshot()["candidate_sets"])

    def test_clip_only_final_without_adding_a_timeline_line(self):
        from . import bundle_candidates, planner
        def partial_timeline(path, payload):
            result = self.stt(path, payload)
            if len(self.calls[-1][0]) > 1:
                return {"status": "partial", "transcript": "미확정 전체 발화"}
            return result
        with patch.object(planner, "gateway_request", side_effect=partial_timeline), patch.object(bundle_candidates, "interpret"):
            self.send_pcm(1000, 1200)
            self.mark("clip-only", "start")
            self.send_pcm(2000, 4000)
            self.mark("clip-only", "end")
            self.send_pcm(3000, 1000)
            self.send_pcm(0, 1200)
            row = self.await_range("clip-only", "final")
            self.assertEqual(row["command_text_source"], "command_window_stt")
            self.assertEqual(row["command_text"], self.phrases[2000])
            self.assertEqual(row["command_utterance_ids"], [])
            self.assertIsNone(row["edit_request_draft"]["utterance_id"])
            target = self.spatial_target("clip-only")
            self.command("design_target", target)
            saved = self.command("design_request", {**target, "output": "bundle", "speech_range_id": "clip-only",
                                                    "utterance_refs": []})["snapshot"]
            self.assertEqual(saved["edit_requests"]["clip-only"]["transcript"]["text"], self.phrases[2000])
            self.assertEqual(saved["utterances"], [])
