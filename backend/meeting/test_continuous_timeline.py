"""PCM integration: markers tag natural STT finals on a single room microphone."""
import base64
import io
import json
import os
from pathlib import Path
import shutil
import sqlite3
import struct
import tempfile
import threading
import time
import wave
from unittest.mock import patch

from .edit_bundle import command_utterances
from .coordinator import SessionCoordinator
from .test_edit_bundle import EditBundleCase


class ContinuousTimelineTests(EditBundleCase):
    stream_options = {"edit_marker_alignment": "speech_overlap"}
    phrases = {1200: "전체 크기는 괜찮은 것 같아.", 2200: "나는 이 부분을",
               3200: "조금 더 둥글게 하고 싶어.", 4200: "이 안이 더 좋아 보인다."}

    def start_meeting(self):
        self.enterContext(patch.dict(os.environ, {"VAD_END_SILENCE_MS": "120", "MEETING_FINALIZE_WAIT_SECONDS": ".02"}))
        self.calls = []
        self.last_started = threading.Event()
        self.release = threading.Event()
        super().start_meeting()

    def stt(self, path, body):
        self.assertEqual(path, "/v1/transcriptions")
        with wave.open(io.BytesIO(base64.b64decode(body["wav_base64"]))) as audio:
            pcm = audio.readframes(audio.getnframes())
        sample = max(value for (value,) in struct.iter_unpack("<h", pcm))
        self.calls.append((sample, pcm.count(struct.pack("<h", sample))))
        if sample == 4200:
            self.last_started.set()
            if not self.release.wait(10):
                raise AssertionError("last STT was never released")
        if sample == 5200:
            return {"status": "partial", "transcript": "미확정 결과"}
        return {"status": "final", "transcript": self.phrases[sample]}

    def stop_audio(self, **pending):
        self.socket.send_json({"type": "stop", "last_sequence": self.sequence - 1, **pending})
        self.assertEqual(self.socket.receive_json()["type"], "stopped")

    def wait_transcript(self, status):
        until = time.monotonic() + 10
        while time.monotonic() < until:
            state = self.snapshot()
            if state["transcript_status"] == status:
                return state
            time.sleep(.025)
        self.fail(f"Expected transcript status {status}, got {state['transcript_status']}")

    def test_natural_utterance_overlap_and_final_drain(self):
        self.send_pcm(1200, 1700)
        key = "round-corner"
        self.mark(key, "start")
        self.send_pcm(1200, 300)  # Tail of the preceding ordinary sentence.
        self.send_pcm(0, 1000)
        self.send_pcm(2200, 1700)
        self.send_pcm(0, 1000)
        self.send_pcm(3200, 700)
        self.mark(key, "end")
        self.await_range(key, "transcribing")
        self.send_pcm(3200, 550)  # A word straddles the click. Send the whole word once.
        self.send_pcm(0, 1000)
        window = self.await_range(key, "final")
        lines = window["utterances"]
        self.assertEqual([line["text"] for line in lines], [self.phrases[2200], self.phrases[3200]])
        self.assertEqual(window["command_utterance_ids"], [line["utterance_id"] for line in lines])
        self.assertEqual(self.calls, [(1200, 2000), (2200, 1700), (3200, 1250)])
        for edge in ("start", "end"):
            self.assertEqual(window[edge + "_ms"], self.stream_epoch_ms + round(self.edges[key][edge] / 8))
        target = self.spatial_target(key)
        self.command("design_target", target)
        self.command("design_request", self.request(target, window))
        self.send_pcm(4200, 1600)  # No trailing silence. EOS flushes the final sentence.
        self.stop_audio()
        self.assertTrue(self.last_started.wait(2))
        try:
            self.command("meeting_end", {})
            time.sleep(.12)
            self.assertEqual(self.snapshot()["transcript_status"], "finalizing")
            self.assertFalse((self.edge.root / "results" / self.sid / "transcript").exists())
        finally:
            self.release.set()
        state = self.wait_transcript("finalized")
        timeline = [json.loads(line) for line in (self.edge.root / "results" / self.sid / "transcript" /
                                                  "transcript.jsonl").read_text().splitlines()]
        self.assertEqual([line["text"] for line in timeline], list(self.phrases.values()))
        self.assertEqual(window["command_utterance_ids"], [line["utterance_id"] for line in timeline[1:3]])
        for line in timeline:
            self.assertEqual((line["meeting_id"], line["speaker_id"], line["source"], line["revision"], line["is_final"]),
                             (self.sid, None, "stt", 1, True))
            self.assertGreater(line["end_ms"], line["start_ms"])
        self.assertEqual(len(state["utterances"]), 4)
        self.command("transcript_correction", {"utterance_id": timeline[-1]["utterance_id"], "text": "새 문장"}, status=(409,))
        self.assertEqual(len(self.calls), 4)

    def test_silent_short_click_and_partial_never_become_commands(self):
        for key, amplitude, count in (("empty", 0, 0), ("silence", 0, 1500), ("click", 3600, 350)):
            self.mark(key, "start")
            if count:
                self.send_pcm(amplitude, count)
            self.mark(key, "end")
            self.send_pcm(0, 1000)
            result = self.await_range(key, "no_speech")
            self.assertEqual((result["error"], result["command_utterance_ids"]), ("수정 요청을 듣지 못했습니다.", []))
            self.assertNotIn("edit_request_draft", result)
        self.assertEqual(self.calls, [])
        self.mark("partial", "start")
        self.send_pcm(5200, 2000)
        self.mark("partial", "end")
        self.stop_audio()
        self.assertEqual(self.await_range("partial", "failed")["command_utterance_ids"], [])
        self.command("meeting_end", {})
        state = self.wait_transcript("finalized")
        self.assertEqual((state["edit_requests"], state["utterances"]), ({}, []))

    def test_eos_accepts_only_the_frozen_selection_and_its_owner(self):
        key = "selected-before-stop"
        self.mark(key, "start")
        self.send_pcm(2200, 2000)
        self.mark(key, "end")
        self.send_pcm(0, 1000)
        window = self.await_range(key, "final")
        selection_id = "hit-" + key
        self.stop_audio(pending_edit_request_id=key, pending_selection_id=selection_id)
        self.assertEqual(self.snapshot()["session_status"], "paused")
        stamp = time.time_ns() // 1_000_000
        self.photo(key, "a-different-selection", stamp, status=409)
        self.photo(key, selection_id, stamp, headers=self.peer, status=409)
        self.photo("another-request", selection_id, stamp, status=409)
        target = self.spatial_target(key)
        self.command("design_target", {**target, "expected_adopted_revision": 3}, status=(409,))
        self.command("design_target", target, headers=self.peer, status=(409,))
        self.command("design_target", target)
        request = self.request(target, window)
        self.assertEqual(self.command("design_request", request)["request_id"], key)
        self.assertEqual(set(self.snapshot()["edit_requests"]), {key})

    def test_restart_finalizes_the_surviving_transcript_with_explicit_gaps(self):
        self.send_pcm(1200, 2000)
        self.send_pcm(0, 1000)
        self.send_pcm(4200, 1800)
        self.send_pcm(0, 1000)
        self.assertTrue(self.last_started.wait(3))
        try:
            self.command("meeting_end", {})
            with tempfile.TemporaryDirectory() as temp:
                directory = Path(temp)
                shutil.copytree(self.edge.models, directory / "models")
                database = sqlite3.connect(directory / "sessions.sqlite3")
                try:
                    with self.edge.lock:
                        self.edge.db.backup(database)
                finally:
                    database.close()
                # This process starts with the exact persisted state from the
                # previous one, but no socket, worker job or STT response.
                restarted = SessionCoordinator(directory)
                try:
                    with restarted.changed:
                        self.assertTrue(restarted.changed.wait_for(
                            lambda: restarted.load(self.sid)["session_status"] == "completed", 15))
                        state = restarted.load(self.sid)
                    self.assertEqual(state["meeting"]["transcript_status"], "finalized")
                    self.assertEqual([u["text"] for u in state["utterances"]], [self.phrases[1200]])
                    self.assertEqual(set(gap["kind"] for gap in state["result"]["missing"]),
                                     {"audio_end_not_confirmed", "transcription_interrupted"})
                finally:
                    restarted.close()
        finally:
            self.release.set()
            self.stop_audio()

    def test_resumed_sample_clock_cannot_move_a_new_command_before_the_first_stream(self):
        self.mark("first", "start")
        self.send_pcm(1200, 2000)
        self.mark("first", "end")
        self.send_pcm(0, 1000)
        first = self.await_range("first", "final")
        self.stop_audio()
        self.command("meeting_start", {"mode": "live", "input_mode": "microphone"})
        self.socket = self.enterContext(self.client.websocket_connect("/v1/audio-streams/resumed"))
        monotonic = time.monotonic_ns() // 1_000_000
        self.socket.send_json({"session_id": self.sid, "token": self.headers["Authorization"].split()[1],
            **self.stream_options, "actual_sample_rate_hz": 8000, "channels": 1, "codec": "pcm_s16le",
            "stream_start_sample_index": 0, "stream_start_source_monotonic_ms": monotonic,
            "clock_offset_ms": self.stream_epoch_ms - 500 - monotonic, "clock_uncertainty_ms": 300})
        self.assertEqual(self.socket.receive_json()["type"], "ready")
        self.sequence = self.sample = 0
        self.mark("second", "start")
        self.send_pcm(2200, 1500)
        self.mark("second", "end")
        self.stop_audio()
        second = self.await_range("second", "final")
        self.assertGreaterEqual(second["start_ms"], first["end_ms"])
        self.assertNotEqual(first["command_utterance_ids"], second["command_utterance_ids"])
        self.assertEqual([u["text"] for u in self.snapshot()["utterances"]], [self.phrases[1200], self.phrases[2200]])

    def test_valid_pcm_after_the_one_hour_sample_index(self):
        # Skip earlier silence in this fixture. The stream records the gap;
        # all successive frames across the hour boundary must still be accepted.
        self.sample = 8000 * 3600 - 1000
        self.send_pcm(0, 1800)
        self.mark("late", "start")
        self.send_pcm(2200, 1500)
        self.mark("late", "end")
        self.stop_audio()
        late = self.await_range("late", "final")
        self.assertGreater(late["start_ms"] - self.stream_epoch_ms, 3600 * 1000)
        self.assertEqual(late["command_text"], self.phrases[2200])

    def test_corrected_speech_before_selection_keeps_the_submitted_revisions(self):
        key = "corrected-command"
        self.mark(key, "start")
        self.send_pcm(2200, 1400)
        self.send_pcm(0, 1000)
        self.send_pcm(3200, 1900)
        self.mark(key, "end")
        self.send_pcm(0, 1000)
        first = self.await_range(key, "final")
        last_id = first["command_utterance_ids"][-1]
        changed = "조금 더 완만하게 하고 싶어."
        self.command("transcript_correction", {"utterance_id": last_id, "text": changed})
        corrected = self.await_range(key, "final")
        self.assertEqual(corrected["transcript_source"], "corrected")
        self.assertEqual(corrected["command_utterance_ids"], first["command_utterance_ids"])
        self.assertEqual([line["revision"] for line in corrected["utterances"]], [1, 2])
        target = self.spatial_target(key)
        self.command("design_target", target)
        request = self.request(target, corrected)
        self.command("design_request", request)
        self.command("transcript_correction", {"utterance_id": last_id, "text": "나중에 고친 문장"})
        state = self.snapshot()
        self.assertEqual(state["utterances"][-1]["revision"], 3)
        saved = state["edit_requests"][key]
        self.assertEqual(saved["transcript"]["source"], "corrected")
        self.assertEqual(saved["transcript"]["utterance_refs"], request["utterance_refs"])
        self.assertEqual([u["text"] for u in command_utterances(state, saved)], [self.phrases[2200], changed])
        self.assertEqual(state["design_speech_ranges"][key]["utterances"], corrected["utterances"])
        self.assertEqual(self.calls, [(2200, 1400), (3200, 1900)])
        self.stop_audio()
        self.command("meeting_end", {})
        self.wait_transcript("finalized")
        folder = self.edge.root / "results" / self.sid / "transcript"
        self.assertEqual(len((folder / "transcript.txt").read_text().splitlines()), 2)
        archived = [json.loads(line) for line in (folder / "transcript.jsonl").read_text().splitlines()]
        self.assertEqual([row["revision"] for row in archived if row["utterance_id"] == last_id], [1, 2, 3])
