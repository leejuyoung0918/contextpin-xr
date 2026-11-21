"""Exercise the minutes model's evidence contract without a paid inference endpoint."""
import json
from unittest import TestCase
from unittest.mock import patch

from . import minutes_generator as generator
from .contracts import MeetingError


class MinutesGeneratorTests(TestCase):
    def setUp(self):
        self.ctx = {"meeting_id": "m", "started_at": 0, "ended_at": 8000, "object_name": "거치대",
                    "initial_version_id": "v1", "final_version_id": "v2", "unresolved_items": [],
                    "versions": [{"version_id": "v1", "label": "V1", "full_parameters": {"width": 10}},
                                 {"version_id": "v2", "label": "V2", "full_parameters": {"width": 12}}],
                    "final_cad": {"version_id": "v2", "label": "V2", "full_parameters": {"width": 12},
                                  "source_code": "width = 12; cube(width);", "scad_file": "cad/final.scad",
                                  "stl_file": "cad/final.stl"},
                    "edit_rounds": [{"request_id": "e", "command_utterance_ids": ["u2"], "base_version_id": "v1",
                                     "command_text": "폭을 조금 더 넓혀 주세요.", "candidates": [], "status": "committed",
                                     "target": {"description": "받침", "part_id": "base",
                                                "selection": {"selection_id": "s", "hit_model_local_position": [0, 1, 0]},
                                                "visual_evidence": {"image_source": "rendered_debug", "sha256": "h"}}}],
                    "design_decisions": [{"decision_id": "d", "commit_time": 3000, "edit_request_id": "e",
                                          "base_version_id": "v1", "base_label": "V1", "result_version_id": "v2", "result_label": "V2",
                                          "command": "폭을 조금 더 넓혀 주세요.", "candidates": [],
                                          "candidate_selected": {"candidate_id": "c", "label": "B"}, "reason": None,
                                          "parameter_diff": [{"parameter_id": "width", "before": 10, "after": 12, "unit": "mm"}],
                                          "evidence_utterance_ids": ["u2"]}]}
        texts = ["이 바닥은 안정적이에요.", "폭을 조금 더 넓혀 주세요.", "이 안이 좋은 것 같아요.",
                 "색상은 아직 미정이에요.", "내일 시제품을 출력하겠습니다."]
        self.ctx["full_transcript"] = [{"utterance_id": "u" + str(i), "text": text, "revision": 1,
                                       "start_ms": i * 1000, "end_ms": i * 1000 + 100, "source": "stt", "speaker_id": None}
                                      for i, text in enumerate(texts, 1)]
        self.bodies = []
        self.runtime = self.enterContext(patch.object(generator.provider_http, "upstream", side_effect=self.infer))
        self.enterContext(patch.object(generator, "settings", return_value=("local", "http://127.0.0.1:18905/v1", "local-qwen", "")))

    def infer(self, path, payload, **transport):
        self.assertEqual((path, transport["base_url"], transport["api_key"]), ("/chat/completions", "http://127.0.0.1:18905/v1", ""))
        body = json.loads(payload)
        self.assertEqual(body["response_format"]["type"], "json_schema")
        packet = json.loads(body["messages"][1]["content"])
        self.bodies.append(packet)
        choices = {k: v[:1] for k, v in packet["eligible_citations"].items()}
        return {"choices": [{"finish_reason": "stop", "message": {"content": json.dumps(choices)}}]}

    def test_evidence_selection_and_server_owned_decisions(self):
        minutes = generator.generate(self.ctx)
        packet, = self.bodies
        self.assertEqual([r["text"] for r in packet["transcript"]], [r["text"] for r in self.ctx["full_transcript"]])
        facts = packet["meeting_evidence"]
        self.assertEqual(facts["versions"][0]["full_parameters"], {"width": 10})
        self.assertEqual(facts["final_cad"]["source_code"], self.ctx["final_cad"]["source_code"])
        self.assertEqual(facts["edit_rounds"][0]["target"]["selection"]["hit_model_local_position"], [0, 1, 0])
        self.assertEqual(facts["edit_rounds"][0]["target"]["image"]["sha256"], "h")
        self.assertEqual(minutes["design_decisions"], self.ctx["design_decisions"])
        self.assertEqual([i["evidence_utterance_ids"] for i in minutes["unresolved_items"]], [["u4"]])
        self.assertEqual([i["evidence_utterance_ids"] for i in minutes["next_actions"]], [["u5"]])
        report = generator.render_markdown(minutes)
        for text in ("V1 → V2", "채택한 후보 · B", "10 → 12 mm", "선택 이유 · 미기록",
                     "이 바닥은 안정적", "색상은 아직 미정", "시제품을 출력", "../cad/final.scad"):
            self.assertIn(text, report)

    def test_unknown_citations_or_model_supplied_facts_fail(self):
        template = {"purpose": [2], "discussion": [1], "unresolved": [], "next_actions": []}
        cases = [{"discussion": [99]}, {"reason": "더 예뻐서"}, {"next_actions": [2]},
                 {"unresolved": [1]}, {"purpose": [True]}, {"discussion": []}]
        for patch_ in cases:
            with self.subTest(patch=patch_):
                self.runtime.side_effect = None
                self.runtime.return_value = {"choices": [{"finish_reason": "stop", "message": {"content": json.dumps({**template, **patch_})}}]}
                with self.assertRaises(MeetingError):
                    generator.generate(self.ctx)

    def test_long_transcript_is_all_classified_and_size_errors_do_not_discard_evidence(self):
        with patch.object(generator, "BATCH_ROWS", 2):
            minutes = generator.generate(self.ctx)
        self.assertEqual(minutes["generation"]["calls"], 3)
        self.assertEqual([r["citation"] for body in self.bodies for r in body["transcript"]], [1, 2, 3, 4, 5])
        self.assertTrue(all(body["meeting_evidence"]["final_cad"]["source_code"] for body in self.bodies))
        self.ctx["final_cad"]["source_code"] = "a" * generator.BATCH_BYTES
        with self.assertRaises(MeetingError):
            generator.generate(self.ctx)
        self.assertEqual(self.runtime.call_count, 3)

    def test_no_commit_no_adoption_and_no_implicit_action(self):
        self.ctx["design_decisions"] = []
        self.ctx["edit_rounds"][0]["status"] = "awaiting_review"
        self.ctx["unresolved_items"] = [{"request_id": "e", "status": "awaiting_review",
                                         "description": "후보 검토 중", "evidence_utterance_ids": ["u2"]}]
        self.ctx["final_version_id"] = self.ctx["final_cad"]["version_id"] = "v1"
        self.ctx["final_cad"]["label"] = "V1"
        self.ctx["full_transcript"][4]["text"] = "내일 출력하지 말자."
        minutes = generator.generate(self.ctx)
        self.assertEqual(minutes["design_decisions"], [])
        self.assertEqual(minutes["next_actions"], [])
        report = generator.render_markdown(minutes)
        self.assertIn("채택된 설계 변경 없음", report)
        self.assertIn("후보 검토 중", report)

    def test_transport_and_truncated_completion_are_errors(self):
        self.runtime.side_effect = ConnectionError("offline")
        with self.assertRaises(MeetingError):
            generator.generate(self.ctx)
        self.runtime.side_effect = None
        self.runtime.return_value = {"choices": [{"finish_reason": "length", "message": {"content": "{}"}}]}
        with self.assertRaises(MeetingError):
            generator.generate(self.ctx)

    def test_explicit_purpose_and_cancellation_across_batches(self):
        template = self.ctx["full_transcript"][-1]
        self.ctx["full_transcript"][0]["text"] = "오늘 회의 목표는 받침 폭을 정하는 거예요."
        for stamp, words in enumerate(("다음 주에 동료에게 STL을 공유하겠습니다.", "출력 계획은 취소합시다.",
                                       "STL 파일은 이미 보냈습니다."), 6):
            self.ctx["full_transcript"].append({**template, "utterance_id": "u" + str(stamp), "text": words,
                                                "start_ms": stamp * 1000, "end_ms": stamp * 1000 + 100})
        with patch.object(generator, "BATCH_ROWS", 2):
            minutes = generator.generate(self.ctx)
        self.assertEqual([i["text"] for i in minutes["purpose"]], [self.ctx["full_transcript"][0]["text"]])
        self.assertEqual(minutes["next_actions"], [])

    def test_purpose_includes_the_whole_edit_command_and_source_diff_is_readable(self):
        req = self.ctx["edit_rounds"][0]
        req.update(command_utterance_ids=["u1", "u2"], command_text="이 부분이 받침입니다. 폭을 조금 더 넓혀 주세요.")
        self.ctx["design_decisions"][0]["actual_cad_change"] = {
            "scad_diff": "--- old\n+++ new\n@@ -1 +1 @@\n-width = 10;\n+width = 12;", "before_scad": "old/a.scad", "after_scad": "new/a.scad"}
        minutes = generator.generate(self.ctx)
        self.assertEqual(minutes["purpose"][0]["text"], req["command_text"])
        self.assertEqual(minutes["purpose"][0]["evidence_utterance_ids"], ["u1", "u2"])
        self.assertIn("+width = 12;", generator.render_markdown(minutes))

    def test_many_edit_rounds_split_without_losing_decisions_cad_or_transcript(self):
        first = self.ctx["edit_rounds"][0]
        for n in range(1, 7):
            self.ctx["edit_rounds"].append({**first, "request_id": f"extra-{n}", "command_utterance_ids": [],
                                            "command_text": "", "candidates": [{"interpretation": "조정안 " * 128}]})
        with patch.object(generator, "BATCH_BYTES", 3600):
            minutes = generator.generate(self.ctx)
        bodies = [p["meeting_evidence"] for p in self.bodies]
        self.assertEqual(set(r["request_id"] for b in bodies for r in b["edit_rounds"]),
                         set(r["request_id"] for r in self.ctx["edit_rounds"]))
        self.assertEqual([d["decision_id"] for b in bodies for d in b["design_decisions"]], ["d"])
        self.assertEqual(sorted(r["citation"] for p in self.bodies for r in p["transcript"]), [1, 2, 3, 4, 5])
        self.assertTrue(all(b["final_cad"]["source_code"] == self.ctx["final_cad"]["source_code"] for b in bodies))
        self.assertGreater(minutes["generation"]["calls"], 1)
