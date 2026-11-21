"""Candidate review at the provider boundary. These tests do not perform network inference."""
import json
import os
from pathlib import Path
import unittest
from unittest.mock import patch

from . import bundle_interpretation as ai
from .contracts import MeetingError
from .coordinator import SessionCoordinator
from .parameter_relations import derived_fields


class BundleInterpretationTests(unittest.TestCase):
    def setUp(self):
        config = json.loads((Path(__file__).parent / "openscad_sample/cable-assembly.json").read_text())
        self.profile = {**config["profile"], "object_id": "object_2", "capabilities": {"can_parametric_edit": True},
                        "assembly": {"fixed_parameters": config["fixed_parameters"]}}
        for f in self.profile["parameters"]:
            name = f["parameter_id"]
            f["max_value"] = config["comparison_maxima"].get(name, f["max_value"])
        self.context = {"request_id": "request", "base_version_id": "v2", "transcript": "이 홈을 넓혀줘.",
                        "base_parameters": {"throat_width": 3.8, "mouth_width": 6, "groove_diameter": 5.6, "slot_pitch": 16.5},
                        "selection": {"part_id": "outer_shell", "selection_id": "press"},
                        "fixed_parameters": config["fixed_parameters"], "preserve_adopted_parameters": {"throat_width": 3.8},
                        "constraints": [], "shared_edit_scope": self.profile["edit_scope"],
                        "parameters": self.profile["parameters"] + derived_fields(self.profile)}
        self.response = {"action": "propose", "request_id": "request", "base_version_id": "v2", "ambiguity_type": "method",
                         "interpreted_target": {"target_part": "outer_shell", "description": "홈 입구",
                             "evidence": "광선과 사진이 입구를 가리킴", "target_parameter_candidates":
                             ["groove_diameter", "mouth_width", "throat_width", "slot_pitch", "body_length"]},
                         "candidates": [], "question": None}
        self.context["effective_parameters"] = {**self.context["base_parameters"], "body_length": 84}
        self.context["semantic_parts"] = self.profile["semantic_parts"]

    def evaluate(self, a, b):
        rows = [{"candidate_key": name, "interpretation": "홈 폭 변경", "parameter_changes": changes}
                for name, changes in [("A", a), ("B", b)]]
        return ai.validate_response(SessionCoordinator, self.profile, self.context, {**self.response, "candidates": rows},
                                    {"source": "test_stub"})

    def test_locality_and_corner_boundary_override_an_incorrect_model(self):
        for text in ("오른쪽 홈만 넓혀", "오른쪽 모서리를 좀 더 둥글게 만들어줘"):
            self.context["transcript"] = text
            result = self.evaluate({"mouth_width": 6.4}, {"mouth_width": 6.8})
            self.assertEqual(result["action"], "unsupported_specific_region")
            self.assertFalse(result["candidates"])
            self.assertEqual(result["interpreted_target"]["target_parameter_candidates"], [])
            self.assertEqual(result["interpreted_target"]["constraint_evidence"], result["question"])

    def test_only_valid_distinct_patches_survive_and_v2_parameters_are_inherited(self):
        for invalid in ({"throat_width": 4}, {"mouth_width": float("nan")}, {"mouth_width": False},
                        {"groove_diameter": 40}, {"slot_pitch": "14", "body_length": 74}, {"mouth_width": 6.41}):
            with self.subTest(invalid=invalid):
                valid, other = {"mouth_width": 6.4}, invalid
                result = self.evaluate(valid, other)
                self.assertEqual(len(result["candidates"]), 1)
                self.assertTrue(result["limitations"])
                self.assertEqual(result["candidates"][0]["full_parameters"]["throat_width"], 3.8)
        self.assertEqual([c["candidate_key"] for c in self.evaluate({"slot_pitch": "14", "body_length": 74},
                                                                    {"mouth_width": 6.4})["candidates"]], ["B"])

    def test_wrong_request_rejected_and_local_transport_receives_two_images(self):
        with self.assertRaises(MeetingError):
            ai.validate_response(SessionCoordinator, self.profile, self.context, {**self.response, "request_id": "wrong"}, {})
        reply = {"id": "stub", "model": "fake", "choices": [{"message": {"content": json.dumps(self.response)}}],
                 "usage": {"prompt_tokens": 100, "completion_tokens": 20, "total_tokens": 120, "other": "not exported"}}
        with patch.dict(os.environ, {"MEETING_BUNDLE_MODEL": "fake", "MEETING_BUNDLE_BASE_URL": "http://127.0.0.1:18123/v1",
                                     "AI_API_KEY": "remote-only", "MEETING_BUNDLE_API_KEY": ""}), \
                patch.object(ai.provider_http, "upstream", return_value=reply) as transport:
            result, evidence = ai.call_provider(self.context, [("image/jpeg", b"photo"), ("image/png", b"mesh")])
        self.assertEqual(result, self.response)
        request = json.loads(transport.call_args.args[1])
        self.assertEqual([m["type"] for m in request["messages"][1]["content"]], ["image_url", "image_url", "text"])
        self.assertEqual(request["response_format"]["json_schema"]["schema"]["properties"]["base_version_id"]["enum"], ["v2"])
        self.assertEqual(request["max_tokens"], 600)
        self.assertEqual(transport.call_args.kwargs["api_key"], "")
        self.assertEqual(evidence["images_sent"], 2)
        self.assertEqual(evidence["usage"], {"prompt_tokens": 100, "completion_tokens": 20, "total_tokens": 120})
        self.assertEqual(evidence["endpoint_mode"], "local")

    def test_local_schema_respects_current_version_and_explicit_dimensions(self):
        self.context["preserve_adopted_parameters"]["slot_pitch"] = 16.5
        self.context["transcript"] = "홈을 6.37mm로 넓혀줘"
        schema = ai.local_response_format(self.context)["json_schema"]["schema"]
        options = schema["properties"]["candidates"]["anyOf"][2]["prefixItems"][0]["properties"]["parameter_changes"]["anyOf"]
        fields = options[0]["properties"]
        self.assertNotIn("slot_pitch", fields)
        self.assertNotIn("body_length", fields)
        self.assertNotIn("throat_width", fields)
        self.assertTrue(all(v > 6 for v in fields["mouth_width"]["enum"]))
        self.assertIn(6.37, fields["mouth_width"]["enum"])
        self.context["transcript"] = "이 홈을 더 넓혀주세요"
        schema = ai.local_response_format(self.context)["json_schema"]["schema"]
        pair = schema["properties"]["candidates"]["anyOf"][2]["prefixItems"]
        widths = [row["properties"]["parameter_changes"]["anyOf"][0]["properties"]["groove_diameter"]["enum"] for row in pair]
        self.assertTrue(all(widths) and set(widths[0]).isdisjoint(widths[1]))
        self.assertGreaterEqual(min(widths[1]) - max(widths[0]), .2)
        self.response["interpreted_target"]["target_parameter_candidates"] = ["mouth_width"]
        result = self.evaluate({"groove_diameter": 6.1}, {"groove_diameter": 5.8})
        self.assertEqual(result["ambiguity_type"], "degree")
        self.assertEqual(result["interpreted_target"]["target_parameter_candidates"], ["groove_diameter"])

    def test_named_stand_dimension_limits_both_proposals(self):
        config = json.loads((Path(__file__).parent / "phone_stand/assembly.json").read_text())
        fields = config["profile"]["parameters"]
        ctx = {**self.context, "parameters": fields, "effective_parameters": {f["parameter_id"]: f["default_value"] for f in fields},
               "semantic_parts": config["profile"]["semantic_parts"], "preserve_adopted_parameters": {}}
        for phrase, dimension in (("등받이 각도를 조금 줄여주세요.", "viewing_angle"),
                                  ("이 부분은 앞쪽 받침턱입니다. 앞쪽 받침턱의 높이를 늘려 주세요.", "lip_height"),
                                  ("이 부분은 앞쪽 받침턱입니다. 앞쪽 받침턱에 높이를 조금 늘려주세요.", "lip_height")):
            with self.subTest(phrase=phrase):
                self.assertEqual(set(ai.change_options({**ctx, "transcript": phrase})), {dimension})

    def test_truncated_completion_and_unconfigured_separate_endpoint(self):
        truncated = {"choices": [{"finish_reason": "length", "message": {"content": json.dumps(self.response)}}]}
        with patch.dict(os.environ, {"MEETING_BUNDLE_MODEL": "test-model", "MEETING_BUNDLE_BASE_URL": "http://127.0.0.1:18123/v1"}), \
                patch.object(ai.provider_http, "upstream", return_value=truncated), self.assertRaises(MeetingError):
            ai.call_provider(self.context, [("image/jpeg", b"photo"), ("image/png", b"mesh")])
        with patch.dict(os.environ, {"MEETING_BUNDLE_MODEL": "", "AI_PLANNER_MODEL": "some-remote-model",
                                    "MEETING_BUNDLE_BASE_URL": "http://127.0.0.1:18123/v1"}), \
                patch.object(ai.provider_http, "upstream") as transport, self.assertRaises(MeetingError):
            ai.call_provider(self.context, [("image/jpeg", b"photo"), ("image/png", b"mesh")])
        transport.assert_not_called()

    def test_saturated_dimensions_and_array_scope_on_original_quest_groove_request(self):
        self.context["preserve_adopted_parameters"] = {}
        self.context["transcript"] = "이 홈을 더 넓혀주세요."
        options = ai.change_options(self.context)
        self.assertEqual(set(options), {"groove_diameter", "mouth_width", "throat_width"})
        for field in self.context["parameters"]:
            if field["parameter_id"] in ("slot_pitch", "body_length"):
                for step in (0, .001):
                    self.assertEqual(ai.value_schema({**field, "step": step}, self.context, 1), {"enum": []})
        self.context["effective_parameters"]["slot_pitch"] = self.context["base_parameters"]["slot_pitch"] = 12.375
        self.context["effective_parameters"]["body_length"] = 67.5
        self.assertNotIn("slot_pitch", ai.change_options(self.context))
        result = self.evaluate({"slot_pitch": 14}, {"mouth_width": 6.8})
        self.assertEqual([c["candidate_key"] for c in result["candidates"]], ["B"])
        self.context["transcript"] = "홈 간격을 조금 더 넓혀주세요."
        self.assertEqual(ai.change_options(self.context)["slot_pitch"]["exclusiveMinimum"], 12.375)
        self.assertEqual(self.evaluate({"slot_pitch": 12.386}, {"slot_pitch": 12.55})["candidates"][0]["full_parameters"]["slot_pitch"], 12.386)

    def test_only_the_ray_selected_upper_corner_can_change_and_length_inherits_it(self):
        self.context["upper_corners"] = {"parameters": {"upper_corner_nx_py": [-1, 1], "upper_corner_px_py": [1, 1]},
                                         "x_span": "body_length", "y_span": "body_depth", "min_z_mm": 6, "edge_band_mm": 5.2}
        for key in self.context["upper_corners"]["parameters"]:
            field = {"parameter_id": key, "label": key, "type": "number", "unit": "mm", "min_value": 0, "max_value": 2,
                     "step": .1, "affected_part_ids": ["outer_shell"]}
            self.context["parameters"].append(field)
            self.profile["parameters"].append(field)
            self.context["base_parameters"][key] = self.context["effective_parameters"][key] = 0
        self.context["effective_parameters"]["body_depth"] = 18
        self.context.update(transcript="이 모서리를 조금 더 둥글게 만들어줘.", scad_hit_mm=[-40.2, 7.2, 10],
                            selection_image={"image_source": "rendered_debug"})
        self.assertEqual(set(ai.change_options(self.context)), {"upper_corner_nx_py"})
        result = self.evaluate({"upper_corner_px_py": 1}, {"upper_corner_nx_py": 1.5})
        self.assertEqual([c["candidate_key"] for c in result["candidates"]], ["B"])
        self.assertIn("한 곳", result["candidates"][0]["edit_scope"])
        self.context["scad_hit_mm"] = [0, 0, 10]
        self.assertTrue(ai.scope_limitation(self.context))
        self.context["transcript"] = "전체 길이를 조금 줄여줘."
        self.context["base_parameters"]["upper_corner_nx_py"] = self.context["preserve_adopted_parameters"]["upper_corner_nx_py"] = 1.5
        self.assertEqual(set(ai.change_options(self.context)), {"body_length"})
        result = self.evaluate({"body_length": 74}, {"body_length": 66})
        self.assertEqual([c["full_parameters"]["upper_corner_nx_py"] for c in result["candidates"]], [1.5, 1.5])
