import json
import unittest
from unittest.mock import patch

import server
import trace_guide


class GuideTests(unittest.TestCase):
    def setUp(self):
        self.points = [{"id": point, "label": label, "node": node} for point, label, node in (
            ("plus-1", "전원 레일 1번", "plus"), ("a-1", "A-1", "a"),
            ("a-2", "A-2", "a"), ("b-1", "B-1", "b"),
            ("b-2", "B-2", "b"), ("gnd-1", "접지 레일 1번", "gnd"),
        )]
        self.notes = [
            {"id": "prep", "time": 0, "transcript": "준비물을 챙기고 전원을 끕니다."},
            {"id": "r", "time": 2.5, "transcript": "저항을 전원 레일 1번에서 여기에 꽂습니다."},
            {"id": "l", "time": 4.5, "transcript": "LED 긴 다리는 A-2, 짧은 다리는 B-1입니다."},
            {"id": "g", "time": 6.5, "transcript": "B-2에서 접지 레일 1번으로 점퍼선을 꽂습니다."},
            {"id": "end", "time": 8.5, "transcript": "배선을 점검하고 LED가 켜지는지 확인합니다."},
        ]
        self.data = {"points": self.points, "notes": self.notes}
        self.generated = {"approved": True, "steps": [
            dict(kind=kind, sourceNoteId=note["id"], sourceQuote=note["transcript"],
                 fromPoint=first, toPoint=second, ledDirection=direction)
            for kind, note, first, second, direction in zip(
                trace_guide.STEP_TEXT, self.notes,
                ("", "plus-1", "a-2", "b-2", ""), ("", "a-1", "b-1", "gnd-1", ""),
                ("", "", "anode_to_cathode", "", ""),
            )
        ]}

    def result(self):
        return trace_guide.guide_result(self.generated, *trace_guide.guide_request(self.data))

    def test_provider_draft_preserves_exact_evidence_and_unresolved_point(self):
        self.data["notes"] = list(reversed(self.notes))
        response = {"choices": [{"message": {"content": json.dumps(self.generated)}}]}
        with patch.dict(server.os.environ, {"AI_VISION_MODEL": "test"}), \
                patch.object(server, "upstream", return_value=response) as proxy:
            draft = server.trace_guide_draft(self.data)
        self.assertNotIn("approved", draft)
        self.assertEqual([s["kind"] for s in draft["steps"]], list(trace_guide.STEP_TEXT))
        self.assertEqual(draft["steps"][1]["sourceQuote"], self.notes[1]["transcript"])
        self.assertEqual((draft["steps"][1]["fromPoint"], draft["steps"][1]["toPoint"]), ("plus-1", ""))
        self.assertEqual(draft["steps"][2]["ledDirection"], "anode_to_cathode")
        payload = json.loads(proxy.call_args.args[1])
        evidence = json.loads(payload["messages"][-1]["content"])
        self.assertEqual([note["id"] for note in evidence["notes"]], [note["id"] for note in self.notes])

    def test_later_registration_correction_invalidates_earlier_recommendation(self):
        self.notes[1]["transcript"] = "저항을 전원 레일 1번에서 A-1에 꽂습니다."
        self.generated["steps"][1]["sourceQuote"] = self.notes[1]["transcript"]
        self.notes.append({"id": "correction", "time": 12, "transcript": "아까 A-1이 아니고 A-2로 정정합니다."})
        self.assertEqual(self.result()["steps"][1]["toPoint"], "")
        self.generated["steps"][1].update(sourceNoteId="correction", sourceQuote=self.notes[-1]["transcript"], toPoint="a-2")
        self.assertEqual(self.result()["steps"][1]["toPoint"], "a-2")

    def test_speaker_reason_requires_exact_grounded_quote(self):
        actual = "그래야 전류가 제한됩니다."
        self.notes[1]["transcript"] += " " + actual
        self.generated["steps"][1].update(reasonQuote=actual, cautionQuote="측정했습니다.")
        step = self.result()["steps"][1]
        self.assertEqual((step["reason"], step["rationaleSource"], step["caution"]), (actual, "recorded_quote", ""))
        self.generated["steps"][1].update(reasonQuote="모델이 새로 붙인 이유")
        self.assertEqual(self.result()["steps"][1]["rationaleSource"], "template")

    def test_distinct_marks_ground_one_ambiguous_location_only(self):
        self.notes[1]["pinMarks"] = ["a-1", "a-1"]
        self.assertEqual(self.result()["steps"][1]["toPoint"], "a-1")
        self.notes[1]["pinMarks"].append("b-1")
        self.assertEqual(self.result()["steps"][1]["toPoint"], "")

    def test_unregistered_point_fabricated_quote_source_and_missing_steps(self):
        self.generated["steps"][2]["fromPoint"] = "c-99"
        self.generated["steps"][3]["sourceQuote"] = "AI가 만든 문장"
        self.generated["steps"][4]["sourceNoteId"] = "unknown"
        self.generated["steps"].pop(0)
        steps = self.result()["steps"]
        self.assertEqual(len(steps), 5)
        self.assertEqual(steps[0]["sourceNoteId"], "")
        self.assertEqual(steps[2]["fromPoint"], "")
        self.assertEqual((steps[3]["sourceNoteId"], steps[3]["toPoint"]), ("", ""))
        self.assertEqual(steps[4]["sourceNoteId"], "")

    def test_polarity_stays_unknown_without_explicit_terminal_evidence(self):
        text = "LED를 A-2에서 B-1에 연결하세요."
        self.notes[2]["transcript"] = self.generated["steps"][2]["sourceQuote"] = text
        step = self.result()["steps"][2]
        self.assertEqual(step["ledDirection"], "")
        self.assertEqual((step["fromPoint"], step["toPoint"]), ("a-2", "b-1"))

    def test_speech_can_name_the_unique_printed_board_code(self):
        self.points[2]["code"] = "A2"
        self.points[3]["code"] = "B1"
        text = "LED 양극은 A2, 음극은 B1에 꽂습니다."
        self.notes[2]["transcript"] = self.generated["steps"][2]["sourceQuote"] = text
        step = self.result()["steps"][2]
        self.assertEqual((step["fromPoint"], step["toPoint"]), ("a-2", "b-1"))
        self.points[1]["code"] = "a2"
        self.assertEqual(self.result()["steps"][2]["fromPoint"], "")

    def test_invalid_evidence_rejected_before_provider(self):
        self.notes[1]["pinMarks"] = ["unregistered"]
        with self.assertRaises(ValueError), patch.object(server, "upstream") as proxy:
            server.trace_guide_draft(self.data)
        proxy.assert_not_called()

    def test_live_generation_requires_provider_configuration(self):
        with patch.dict(server.os.environ, {}, clear=True), self.assertRaises(server.InputError):
            server.trace_guide_draft(self.data)
