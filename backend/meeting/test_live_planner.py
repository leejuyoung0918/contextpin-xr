import copy
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from . import planner
from .contracts import MeetingError
from .geometry import GeometryError, default_parameters
from .openscad_geometry import OpenScadAdapter
from .parameter_relations import normalize_patch
from .planner import IDENTITY, compile_context, validated_plan
from .shared_workflows import cue_text, design_topic


class LivePlannerTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        config = json.loads(Path(__file__).with_name("openscad_sample").joinpath("cable-assembly.json").read_text())
        (root / "assembly.json").write_text(json.dumps(config))
        defaults = {**config["fixed_parameters"], **default_parameters(config["profile"])}
        (root / config["source_file"]).write_text("\n".join(f"{k} = {v};" for k, v in defaults.items()) + "\ncube([84,18,10]);")
        environment = patch.dict(os.environ, {"OPENSCAD_ASSEMBLY_CONFIG": str(root / "assembly.json")})
        environment.start()
        self.addCleanup(environment.stop)
        self.model = OpenScadAdapter(root / "assembly.json")
        self.profile = self.model.describe()
        self.parameters = {**default_parameters(self.profile), "throat_width": 3.4, "mouth_width": 5.2}
        self.version = {"version_id": "v2", "source_hash": self.profile["source_hash"],
                        "parameters": self.parameters, "measurements": {"width_mm": 84, "height_mm": 10, "depth_mm": 18}}
        self.state = {"profile": self.profile, "session_id": "session", "assembly": self.profile["assembly"],
                      "committed_version_id": "v2", "versions": [self.version], "rounds": [], "decisions": [],
                      "constraint_revision": 0, "design_revision": 1, "constraints": [], "utterances": [], "images": []}
        self.view = {"evidence_id": "view", "version_id": "v2", "part_ids": ["outer_shell"],
                     "hit_point_asset_m": [0, .008, 0]}

    def context(self, text="정리대 전체 길이를 57.3mm로 바꿔 주세요."):
        utterance = {"utterance_id": "utterance", "evidence_id": "utterance", "revision": 1,
                     "participant_id": "participant", "text": text, "start_ms": 1000, "end_ms": 2000}
        return compile_context(self.state, utterance, self.view, self.version, "round", 1)

    def plan(self, context, parameter="body_length", value=57.3):
        return {"schema_version": 1, **{key: context[key] for key in IDENTITY},
                "action": "propose_preview", "question": None, "evidence_ids": context["evidence_ids"],
                "options": [{"title": "길이 조정", "interpretation": "요청 치수 적용",
                             "operations": [{"operation_id": "set_parameter", "part_id": "outer_shell",
                                             "parameter_id": parameter, "value": value, "unit": "mm",
                                             "source": "EXPLICIT_USER"}]}]}

    def test_request_uses_frozen_selected_model_source_and_pose(self):
        context = self.context()
        self.assertIn("throat_width = 3.4;", context["scad_code"])
        self.assertEqual(context["effective_parameters"]["body_length"], 84)
        self.view["hit_point_asset_m"][0] = 9
        self.parameters["mouth_width"] = 5.9
        self.assertEqual(context["selection_snapshot"]["hit_point_asset_m"], [0, .008, 0])
        self.assertEqual(context["parameters"]["mouth_width"], 5.2)
        self.assertFalse(context["edit_scope"]["individual_slot_editing"])

    def test_quest_loose_fit_transcript_proposes_registered_options_on_request(self):
        self.parameters.update(default_parameters(self.profile))
        for cue in ("수정한 보여줘", "수정본 만들어줘", "수정본을 만들어 주세요."):
            self.assertEqual(cue_text(cue), "")
            self.assertFalse(design_topic({"text": cue}))
        for unrelated in ("오늘 너무 헓꺼워", "너무 헬거워", "목이 홀거워", "허일거워", "충전선이 불거워",
                          "충전선은 괜찮고 신발이 홀거워", "홈페이지가 홀거워"):
            self.assertFalse(design_topic({"text": unrelated}))
        for text in ("충전선이 너무 헐거워", "충전선이 헐겁습니다", "충전선이 너무 헬거워",
                     "충전선이 헬거워", "충전선이 허일거워", "중전선이 헬거워", "충전선이 홀거워",
                     "충전선이 너무 헓꺼워 수정한 보여줘"):
            with self.subTest(text=text), patch.dict(os.environ, {"MEETING_PLAN_BACKEND": "registered"}):
                context = self.context(text)
                self.assertTrue(design_topic(context["utterance"]))
                self.assertEqual(cue_text(text), "충전선이 너무 헓꺼워" if "보여줘" in text else None)
                proposal = validated_plan(context, "live")
                self.assertEqual([o["title"] for o in proposal["options"]],
                                 ["입구만 축소", "홈 내부만 축소", "입구와 내부 축소"])
                self.assertEqual(proposal["proposal_provenance"]["source"], "registered_rules")
                self.assertEqual(context["utterance"]["text"], text)

    def test_upper_right_only_does_not_change_all_five_grooves(self):
        context = self.context("우측 상단을 더 키워줬으면 좋겠어.")
        self.assertTrue(design_topic(context["utterance"]))
        with patch.object(planner, "gateway_request", side_effect=AssertionError("unsupported localized edit")):
            result = validated_plan(context, "live")
        self.assertEqual(result["action"], "ask_clarification")
        self.assertEqual(result["options"], [])

    def test_cable_retention_goal_and_explicit_veto(self):
        self.parameters.update(default_parameters(self.profile))
        with patch.dict(os.environ, {"MEETING_PLAN_BACKEND": "registered"}):
            observation = "충전선이 너무 헐렁해서 자꾸 빠져요. "
            goal = "이번에 선이 빠지지 않도록 수정해주세요."
            context = self.context(observation + goal)
            self.assertEqual([o["title"] for o in validated_plan(context, "live")["options"]],
                             ["입구만 축소", "홈 내부만 축소", "입구와 내부 축소"])
            for veto in ("홈을 줄이지 않도록 수정해주세요.", goal + " 입구를 좁히지 마세요.",
                         "전체 길이를 54 mm로 줄이지 않도록 해주세요.", "헐거워도 그대로 두자."):
                with self.subTest(veto=veto):
                    self.assertEqual(validated_plan(self.context(observation + veto), "live")["options"], [])

    def test_live_groove_request_changes_only_named_dimensions_in_spoken_direction(self):
        self.parameters.update(default_parameters(self.profile))
        with patch.dict(os.environ, {"MEETING_PLAN_BACKEND": "registered"}), \
             patch.object(planner, "fixture_plan", side_effect=AssertionError("live used rehearsal")):
            for line, expected, grows in [
                ("케이블 홈 크기를 좀 키워줘.", [2, 1, 3], True),
                ("내부 공간은 유지하고 입구만 넓혀 주세요.", [2], True),
                ("입구는 그대로 두고 내부 지름을 좁혀줘.", [1], False),
                ("우측 상단을 더 키워줬으면 좋겠어.", [], True),
            ]:
                with self.subTest(line=line):
                    self.assertIsNotNone(cue_text(line))
                    answer = validated_plan(self.context(line), "live")
                    self.assertEqual([len(o["operations"]) for o in answer["options"]], expected)
                    for option in answer["options"]:
                        for op in option["operations"]:
                            self.assertEqual(op["value"] > self.parameters[op["parameter_id"]], grows)
                            self.assertEqual(op["source"], "REGISTERED_COMPARISON")
            for f in self.profile["parameters"]:
                self.parameters[f["parameter_id"]] = f["max_value"]
            self.assertEqual(validated_plan(self.context("홈을 더 키워줘"), "live")["action"], "ask_clarification")

    def test_new_exact_length_is_server_calculated_and_prior_entrance_is_retained(self):
        context = self.context()
        with patch.object(planner, "gateway_request", return_value=self.plan(context)):
            response = validated_plan(context, "live")
        operation = response["options"][0]["operations"][0]
        self.assertEqual(operation["parameter_id"], "slot_pitch")
        self.assertEqual(operation["value"], 9.825)
        self.assertEqual(context["parameters"]["throat_width"], 3.4)
        self.assertEqual(normalize_patch(self.profile, {"body_length": 57.3}), {"slot_pitch": 9.825})

    def test_provider_cannot_replace_exact_length_with_a_prepared_value(self):
        context = self.context()
        with patch.object(planner, "gateway_request", return_value=self.plan(context, value=60)):
            with self.assertRaisesRegex(MeetingError, "명시한 길이"):
                validated_plan(context, "live")

    def test_invalid_parameter_and_units_fail_before_geometry(self):
        context = self.context()
        for name, value in (("slot_count", 4), ("slot_pitch", 100)):
            with self.subTest(name=name), patch.object(planner, "gateway_request", return_value=self.plan(context, name, value)):
                with self.assertRaises(MeetingError):
                    validated_plan(context, "live")
        invalid = self.plan(context)
        invalid["options"][0]["operations"][0]["unit"] = "cm"
        with patch.object(planner, "gateway_request", return_value=invalid):
            with self.assertRaisesRegex(MeetingError, "단위"):
                validated_plan(context, "live")

    def test_live_provider_failure_never_enters_fixture(self):
        context = self.context()
        with patch.object(planner, "gateway_request", side_effect=MeetingError("quota", 503)), \
                patch.object(planner, "fixture_plan", side_effect=AssertionError("fixture used")):
            with self.assertRaisesRegex(MeetingError, "quota"):
                validated_plan(context, "live")

    def test_geometric_relationship_is_checked_even_when_individual_ranges_pass(self):
        profile = copy.deepcopy(self.profile)
        profile["parameter_constraints"].append({"terms": {"mouth_width": 1, "throat_width": -1},
                                                  "operator": "gt", "value": 3,
                                                  "message": "fixture clearance violation"})
        with self.assertRaisesRegex(GeometryError, "clearance"):
            self.model.validate_parameters(profile, self.parameters)


if __name__ == "__main__":
    unittest.main()
