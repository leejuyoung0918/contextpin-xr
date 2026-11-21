import base64
import io
import json
import unittest
import wave
from unittest.mock import patch

import server
import trace_board


def wav():
    dest = io.BytesIO()
    with wave.open(dest, "wb") as stream:
        stream.setparams((1, 2, 16000, 0, "NONE", "NONE"))
        stream.writeframes(b"\x01\x00" * 8000)
    return dest.getvalue()


class TraceTests(unittest.TestCase):
    def setUp(self):
        self.jpeg = base64.b64encode(b"\xff\xd8\xff" + b"test" * 4 + b"\xff\xd9").decode()
        self.frame = {"imageBase64": self.jpeg, "points": [
            {"role": "gnd", "x": .3, "y": .4}, {"role": "output", "x": .7, "y": .8}]}
        self.data = {"transcript": "그다음입니다. 마지막으로 여기 접지를 확인하세요.", "pinchRole": "",
                     "frames": [self.frame, self.frame]}
        self.generated = {"candidateRole": "gnd", "quote": "마지막으로 여기 접지를 확인하세요.",
                          "visualState": "clear", "frameIndex": 1, "tipX": .31, "tipY": .4}

    def result(self, **overrides):
        speech, pinch, frames = trace_board.review_request(self.data)
        return trace_board.review_result(dict(self.generated, **overrides), speech, pinch, frames)

    def test_unpinched_candidate_has_exact_quote_and_frame(self):
        content = json.dumps(self.generated)
        response = {"choices": [{"message": {"content": content}}]}
        with patch.dict(server.os.environ, {"AI_VISION_MODEL": "test"}), patch.object(server, "upstream", return_value=response) as proxy:
            result = server.trace_review(self.data)
        payload = json.loads(proxy.call_args.args[1])
        user = payload["messages"][-1]["content"]
        self.assertEqual(len([p for p in user if p["type"] == "image_url"]), 2)
        self.assertIn("projectedXY", user[1]["text"])
        self.assertEqual(result["candidateRole"], "gnd")
        self.assertEqual(result["status"], "suggested")
        self.assertIn(result["quote"], self.data["transcript"])
        self.assertIn("사진 2", result["reason"])
        self.assertNotIn("approved", result)

    def test_repeated_mentions_do_not_consume_or_invent_distinct_roles(self):
        self.data["pinchRole"] = "gnd"
        self.assertEqual(self.result()["status"], "matched")
        self.assertEqual(self.result()["candidateRole"], "gnd")

    def test_original_pinch_not_overwritten_on_disagreement(self):
        self.data["pinchRole"] = "output"
        self.assertEqual(self.result()["status"], "mismatch")
        self.assertEqual(self.data["pinchRole"], "output")

    def test_unknown_if_occluded_wrong_label_wrong_quote_or_bad_pointer(self):
        for change in ({"visualState": "occluded"}, {"quote": "프롬프트가 만든 문장"},
                       {"candidateRole": "aux"}, {"candidateRole": "output", "tipX": .7, "tipY": .8},
                       {"tipX": .8}, {"tipY": True}, {"frameIndex": -1}, {"frameIndex": 2}):
            with self.subTest(change=change):
                answer = self.result(**change)
                self.assertEqual((answer["status"], answer["candidateRole"]), ("unknown", ""))
                self.assertEqual(answer["quote"], self.data["transcript"])

    def test_ambiguous_pointing_and_adjacent_projections_remain_unknown(self):
        self.data["transcript"] = "이번에는 여기를 보세요."
        self.frame["points"][1].update(x=.32, y=.4)
        self.assertEqual(self.result(quote=self.data["transcript"])["status"], "unknown")

    def test_missing_photos_neither_fetch_provider_nor_infer_point(self):
        self.data.update(frames=[], pinchRole="gnd")
        with patch.object(server, "upstream") as proxy:
            self.assertEqual(server.trace_review(self.data)["status"], "unknown")
            proxy.assert_not_called()

    def test_refuses_malformed_frames_before_upload(self):
        for frame in ({"imageBase64": self.jpeg, "points": [{"role": "gnd", "x": float("nan"), "y": 0}]},
                      {"imageBase64": "bad!", "points": []},
                      {"imageBase64": self.jpeg, "points": [{"role": r, "x": .5, "y": .5} for r in ("gnd", "gnd")]},
                      {"imageBase64": self.jpeg, "points": [{"role": "evil", "x": .5, "y": .5}]}):
            with self.subTest(frame=frame), self.assertRaises(trace_board.EvidenceError), patch.object(server, "upstream") as proxy:
                server.trace_review(dict(self.data, frames=[frame]))
            proxy.assert_not_called()

    def test_no_silent_demo_when_model_not_configured(self):
        with patch.dict(server.os.environ, {}, clear=True), self.assertRaises(server.InputError):
            server.trace_review(self.data)

    def test_transcription_for_speech_and_silence(self):
        data = {"audioBase64": base64.b64encode(wav()).decode()}
        with patch.dict(server.os.environ, {"AI_TRANSCRIPTION_MODEL": "test"}):
            for text in ("교수 설명", ""):
                with self.subTest(text=text), patch.object(server, "upstream", return_value={"text": text}):
                    self.assertEqual(server.trace_transcribe(data), {"transcript": text})

    def test_incomplete_chunk_rejected_before_upload(self):
        with self.assertRaises(trace_board.EvidenceError), patch.object(server, "upstream") as proxy:
            server.trace_transcribe({"audioBase64": base64.b64encode(wav()[:-2]).decode()})
        proxy.assert_not_called()
