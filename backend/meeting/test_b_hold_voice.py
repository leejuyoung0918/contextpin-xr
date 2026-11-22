"""Check a physical-button window inside one longer Korean utterance with the local STT."""
import json
import os
from pathlib import Path
import struct
import subprocess
import tempfile
import time
import unittest
from unittest.mock import patch
import wave

from .bundle_interpretation import bundle_context
from .gateway import transcribe_live
from .test_edit_bundle import EditBundleCase


@unittest.skipUnless(os.getenv("MEETING_VOICE_SMOKE") == "1", "Set MEETING_VOICE_SMOKE=1 for Korean local STT")
class HeldKoreanSpeechTests(EditBundleCase):
    stream_options = {"edit_marker_alignment": "speech_overlap", "edit_command_mode": "b_hold"}
    sample_rate_hz = 16000

    def setUp(self):
        self.enterContext(patch.dict(os.environ, {"MEETING_STT_BACKEND": "local", "AI_GATEWAY_URL": ""}))
        super().setUp()

    def stt(self, path, body):
        self.assertEqual(path, "/v1/transcriptions")
        return transcribe_live(body)

    def spoken_pcm(self, text):
        # Trim only the TTS padding so the button edges really cut across one continuous VAD segment.
        with tempfile.TemporaryDirectory() as folder:
            audio = Path(folder) / "line.aiff"
            subprocess.run(["say", "-v", "Yuna", "-r", "165", "-o", str(audio), text], check=True)
            trim = "silenceremove=start_periods=1:start_threshold=-42dB"
            return subprocess.check_output(["ffmpeg", "-loglevel", "error", "-i", str(audio), "-af",
                                            f"{trim},areverse,{trim},areverse", "-ac", "1", "-ar", str(self.sample_rate_hz),
                                            "-f", "s16le", "-"])

    def feed(self, pcm):
        for offset in range(0, len(pcm), 640):
            piece = pcm[offset:offset + 640]
            count = len(piece) // 2
            self.socket.send_bytes(struct.pack("<IQI", self.sequence, self.sample, count) + piece)
            self.sequence += 1
            self.sample += count

    def test_exact_window_excludes_room_discussion_from_the_cad_request(self):
        rid = "held-korean-command"
        before = self.spoken_pcm("회의실에 사과가 있습니다.")
        # The speaker takes a short breath inside the hold; all three phrases still share one room utterance.
        breath = bytes(self.sample_rate_hz // 5)
        held = breath + self.spoken_pcm("홈의 입구를 조금 더 좁혀 주세요.") + breath
        after = self.spoken_pcm("바나나는 책상에 있습니다.")
        self.feed(bytes(self.sample_rate_hz))
        self.feed(before)
        self.mark(rid, "start")
        self.feed(held)
        self.mark(rid, "end")
        self.feed(after)
        self.feed(bytes(2 * self.sample_rate_hz))
        deadline = time.monotonic() + 70
        while time.monotonic() < deadline:
            state = self.snapshot()
            window = state["design_speech_ranges"].get(rid, {})
            if window.get("status") == "final" and state["utterances"]:
                break
            self.assertNotIn(window.get("status"), {"failed", "no_speech"}, window)
            time.sleep(.10)
        self.assertEqual(window.get("status"), "final", window)
        self.assertEqual(window["command_text_source"], "command_window_stt")
        self.assertFalse(state["edit_requests"] or state.get("candidate_sets"))
        room, = state["utterances"]
        print(json.dumps({"room": room["text"], "command": window["command_text"],
                          "source": window["command_text_source"], "samples": len(held) // 2}, ensure_ascii=False))
        self.assertIn("사과", room["text"])
        self.assertIn("바나나", room["text"])
        self.assertIsNone(room["speaker_id"])
        self.assertEqual(window["command_utterance_ids"], [room["utterance_id"]])
        self.assertIn("입구", window["command_text"])
        self.assertRegex(window["command_text"], "좁[혀히게]")
        self.assertNotIn("사과", window["command_text"])
        self.assertNotIn("바나나", window["command_text"])

        target = self.spatial_target(rid)
        self.command("design_target", target)
        refs = [{k: item[k] for k in ("utterance_id", "revision")} for item in window["utterances"]]
        self.command("design_request", {**target, "output": "bundle", "speech_range_id": rid, "utterance_refs": refs})
        state = self.snapshot()
        bundle = state["edit_requests"][rid]
        with wave.open(str(self.edge.root / "edit_requests" / self.sid / rid / "command.wav")) as wav:
            self.assertEqual(wav.readframes(wav.getnframes()), held)
        context, images = bundle_context(self.edge, state, bundle)
        self.assertEqual((context["command_text_source"], context["command_requested_by"], context["transcript"]),
                         ("command_window_stt", self.actor, window["command_text"]))
        self.assertEqual(len(images), 2)
        self.assertEqual(len(state["utterances"]), 1)
