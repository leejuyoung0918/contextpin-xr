"""Verify the factual minutes projection without depending on an LLM or a headset."""
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

from .minutes_context import build_context, unresolved_request


class MinutesContextTests(unittest.TestCase):
    def setUp(self):
        self.folder = Path(self.enterContext(tempfile.TemporaryDirectory()))
        self.files = {}
        self.manifest = {"meeting_id": "m", "started_at": 1, "ended_at": 150,
                         "original_version_id": "v1", "adopted_version_id": "v2", "mode": "live", "files": self.files}
        config = {"object_2": {"display_name": "거치대"}, "fixed_parameters": {"thickness": 7},
                  "profile": {"parameters": [{"parameter_id": "angle"}],
                              "semantic_parts": [{"part_id": "back", "label": "등받이"}]}}
        (self.folder / "original").mkdir()
        with zipfile.ZipFile(self.folder / "original/original_source.zip", "w") as f:
            f.writestr("assembly_config.json", json.dumps(config))
        self.model("original", "v1", 65)
        self.model("models/v2", "v2", 50)
        self.model("cad", "v2", 50, final=True)
        self.write("design_history.json", [{"version_id": "v1", "stage_directory": "original", "label": "V1"},
                                           {"version_id": "v2", "stage_directory": "models/v2", "label": "V2"}])
        raw = [{"utterance_id": "line", "revision": revision, "is_final": True, "text": text,
                "start_ms": 3, "end_ms": 6, "speaker_id": None, "source": "stt"}
               for revision, text in ((1, "각도를 낮추자."), (2, "각도는 충분해."))]
        self.write("transcript.jsonl", "\n".join(json.dumps(row) for row in raw))
        self.write("edit_requests/r/photo.jpg", b"context")
        request = {"request_id": "r", "base_version_id": "v1", "created_at": 5,
                   "transcript": {"utterance_refs": [{"utterance_id": "line", "revision": 1}], "text": raw[0]["text"]},
                   "selection": {"part_id": "back", "selection_id": "ray", "hit_model_local_position": [.01, .08, 0]},
                   "visual_evidence": {"file": "photo.jpg", "image_asset_id": "img", "image_source": "rendered_debug",
                                       "sha256": self.files["edit_requests/r/photo.jpg"]["sha256"]}}
        self.write("edit_requests/r/request.json", request)
        self.write("design_requests.json", [{"request_id": "r", "base_version_id": "v1", "file": "edit_requests/r/request.json"}])
        self.write("candidate_sets.json", [{"request_id": "r", "status": "committed",
                   "candidates": [{"candidate_id": "b", "version_id": "v2", "label": "B", "interpretation": "보기 편할 수 있음"}]}])
        self.decision = {"decision_id": "d1", "outcome": "commit", "type": "adopt", "edit_request_id": "r",
                         "selected_model_version_id": "v2", "previous_committed_model_version_id": "v1",
                         "selected_candidate_id": "b", "command_utterance_ids": ["line"],
                         "selection_id": "ray", "image_id": "img", "rationale_source": "not_stated",
                         "reason": "not_stated", "committed_at": 100,
                         "parameter_changes": [{"parameter_id": "angle", "label": "각도", "before": 0, "after": 100}]}
        self.write("decisions.json", [self.decision])

    def write(self, name, value):
        path = self.folder / name
        path.parent.mkdir(parents=True, exist_ok=True)
        if not isinstance(value, (bytes, str)):
            value = json.dumps(value, ensure_ascii=False)
        data = value if isinstance(value, bytes) else value.encode()
        path.write_bytes(data)
        self.files[name] = {"sha256": hashlib.sha256(data).hexdigest(), "size_bytes": len(data)}

    def model(self, folder, vid, angle, final=False):
        stem = "final" if final else "candidate"
        meta = {"version_id": vid, "entry": stem + ".scad"}
        if vid == "v2":
            meta["full_parameters"] = {"angle": angle, "thickness": 7}
        for name, content in ((stem + ".scad", f"angle = {angle};"), ("parameters.json", {"angle": angle}),
                              ("version.json", meta), ("build.json", {"stl": {"units": "mm"}, "preview": {"units": "m"}}),
                              ("parameter_provenance.json", {"units": {"angle": "deg"}}),
                              (stem + ".stl", b"stl"), ("preview.glb", b"preview")):
            self.write(f"{folder}/{name}", content)

    def test_committed_files_and_original_command_revision_own_the_facts(self):
        ctx = build_context(self.folder, self.manifest)
        self.assertEqual(ctx["full_transcript"][0]["revision"], 2)
        decision, = ctx["design_decisions"]
        self.assertEqual(decision["command"], "각도를 낮추자.")
        self.assertIsNone(decision["reason"])
        self.assertEqual(decision["parameter_diff"], [{"parameter_id": "angle", "label": "각도",
                                                       "before": 65, "after": 50, "unit": "deg"}])
        self.assertEqual(decision["target"]["visual_evidence"]["file"], "edit_requests/r/photo.jpg")
        self.assertEqual(ctx["final_cad"]["source_code"], "angle = 50;")
        self.assertIn("-angle = 65;", decision["actual_cad_change"]["scad_diff"])
        self.assertEqual(ctx["unresolved_items"], [])

    def test_readoption_reinstates_original_without_another_candidate_vote(self):
        self.model("cad", "v1", 65, final=True)
        self.manifest["adopted_version_id"] = "v1"
        self.write("decisions.json", [self.decision, {"decision_id": "restore", "outcome": "commit", "type": "readopt",
                       "candidate_id": "prior-candidate", "selected_model_version_id": "v1",
                       "previous_committed_model_version_id": "v2", "committed_at": 145}])
        d = build_context(self.folder, self.manifest)["design_decisions"][1]
        self.assertEqual((d["base_label"], d["result_label"], d["parameter_diff"][0]["after"]), ("V2", "V1", 65))
        self.assertEqual((d["edit_request_id"], d["candidate_selected"], d["selected_candidate_id"]), (None, None, None))

    def test_clipped_command_and_its_original_meeting_utterance_stay_distinct(self):
        path = "edit_requests/r/request.json"
        original = json.loads((self.folder / path).read_bytes())
        clipped = "등받이 각도를 낮춰줘."
        request = {**original, "command_text_source": "command_window_stt", "command_text": clipped,
                   "participant_id": "person-b", "transcript": {**original["transcript"], "text": clipped}}
        self.write(path, request)
        ctx = build_context(self.folder, self.manifest)
        edit, = ctx["edit_rounds"]
        self.assertEqual((edit["command_text"], edit["command_text_source"], edit["command_requested_by"]),
                         (clipped, "command_window_stt", "person-b"))
        self.assertEqual(ctx["design_decisions"][0]["command"], clipped)
        self.assertEqual(ctx["full_transcript"][0]["text"], "각도는 충분해.")
        self.assertIsNone(ctx["full_transcript"][0]["speaker_id"])
        self.write(path, {**request, "command_text_source": "linked_meeting_utterances"})
        with self.assertRaisesRegex(ValueError, "확정 전사"):
            build_context(self.folder, self.manifest)

    def test_rejected_replaced_deferred_and_unfinished_are_distinguishable(self):
        row = {"request_id": "r", "command_utterance_ids": ["line"], "command_text": "수정 의견"}
        for status in ("dismissed", "superseded"):
            self.assertIsNone(unresolved_request({**row, "status": status}))
        self.assertIn("보류", unresolved_request({**row, "status": "deferred"})["description"])
        self.assertIn("미완료", unresolved_request({**row, "status": "superseded", "interrupted_at_meeting_end": True})["description"])
        self.write("partial_edits.json", [{**row, "request_id": "no-target", "status": "awaiting_target_at_close"}])
        ctx = build_context(self.folder, self.manifest)
        self.assertEqual([x["request_id"] for x in ctx["edit_rounds"]], ["r"])
        self.assertEqual([x["status"] for x in ctx["unresolved_items"]], ["awaiting_target_at_close"])

    def test_wrong_image_link_cannot_claim_a_decision(self):
        self.write("decisions.json", [{**self.decision, "image_id": "other-image"}])
        with self.assertRaisesRegex(ValueError, "선택 근거"):
            build_context(self.folder, self.manifest)
