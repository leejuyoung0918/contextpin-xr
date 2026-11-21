"""One meeting code, independent participants, shared layout and real candidate GLBs."""
import hashlib
import json
import sqlite3
import time
from contextlib import closing
from unittest.mock import patch

from . import bundle_interpretation
from .coordinator import SessionCoordinator
from .edit_bundle import transform_point
from .test_bundle_candidates import CandidateSetCase


class PaperSpaceTests(CandidateSetCase):
    candidate_mode = {"candidate_generation": "ai"}
    latest_pose = {"position": [.012, .001, -.018], "rotation_xyzw": [0, 1, 0, 0]}

    def paper(self, command, payload=None, *, headers=None, status=(200, 202), revision=1):
        return self.command(command, {"paper_frame_revision": revision, **(payload or {})}, headers=headers, status=status)

    def align(self, headers=None, revision=1):
        for status in ("calibrating", "aligned"):
            self.paper("paper_alignment", {"status": status}, headers=headers, revision=revision)

    def setup_layout(self):
        self.align()
        self.paper("paper_layout", {"latest_version_id": self.original, "latest_pose_paper": self.latest_pose})

    def snapshot(self, headers=None):
        # Real headsets stream even while OpenSCAD builds. Keep the fixture's marked
        # microphone alive during the three builds, including on slow development PCs.
        if not getattr(self, "marking_audio", False) and hasattr(self, "next_silence") and time.monotonic() >= self.next_silence:
            self.send_pcm(0, 320)
            self.next_silence = time.monotonic() + 2
        return super().snapshot(headers)

    def record(self, key):
        self.marking_audio = True
        try:
            return super().record(key)
        finally:
            self.marking_audio = False

    def shared_ray(self, version):
        point = transform_point(self.latest_pose, [.01, .007, 0])
        return {"model_version_id": version, "hit_position_paper": point,
                "ray_origin_paper": [point[0], point[1] + .6, point[2]], "ray_direction_paper": [0, -1, 0]}

    def test_restart_notifies_an_aligned_client_at_the_previous_sequence(self):
        self.setup_layout()
        self.align(self.peer)
        self.paper("paper_selection", self.shared_ray(self.original))
        before = self.snapshot()
        directory = self.edge.root.parent / "restart"
        directory.mkdir()
        with closing(sqlite3.connect(directory / "sessions.sqlite3")) as db, self.edge.lock:
            self.edge.db.backup(db)
        with closing(SessionCoordinator(directory)) as restarted:
            events, after = restarted.wait_events(self.sid, self.actor, before["last_seq"])
            self.assertGreater(after["last_seq"], before["last_seq"])
            self.assertEqual([e["event_type"] for e in events], ["paper.alignments_lost"])
            self.assertEqual([p["status"] for p in after["spatial"]["participants"]], ["lost", "lost"])
            self.assertIsNone(after["spatial"]["shared_selection"])
            for key in ("latest_version_id", "latest_pose_paper", "paper_frame_revision"):
                self.assertEqual(after["spatial"][key], before["spatial"][key])

    def test_alignment_identity_and_revision_protect_poses_and_rays(self):
        guest = self.snapshot(self.peer)["spatial"]
        self.assertEqual(guest["status"], "waiting_for_host")
        self.assertEqual([p["status"] for p in guest["participants"]], ["not_aligned"] * 2)
        layout = {"latest_version_id": self.original, "latest_pose_paper": self.latest_pose}
        self.paper("paper_layout", layout, status=(409,))
        self.setup_layout()
        ray = self.shared_ray(self.original)
        self.paper("paper_selection", ray, headers=self.peer, status=(409,))
        self.align(self.peer)
        self.paper("paper_reset", headers=self.peer, status=(403,))
        self.paper("paper_layout", layout, headers=self.peer, status=(403,))
        self.paper("paper_layout", {**layout, "latest_pose_paper": {**self.latest_pose, "position": [1, 0, 0]}}, status=(409,))
        self.paper("paper_layout", {**layout, "latest_pose_paper": {**self.latest_pose, "rotation_xyzw": [0]*4}}, status=(400,))
        self.paper("paper_selection", {**ray, "hit_world_position": [0, 0, 0]}, status=(400,))
        self.paper("paper_selection", {**ray, "ray_direction_paper": [0, 1, 0]}, status=(400,))
        self.paper("paper_selection", {**ray, "ray_origin_paper": [1, 2, 3], "hit_position_paper": [1, 1, 3]}, status=(409,))
        self.paper("paper_selection", {**ray, "model_version_id": "old"}, status=(409,))
        with self.client.websocket_connect(self.prefix + "/events") as ws:
            ws.send_json({"token": self.peer["Authorization"].split()[1]})
            self.assertEqual(ws.receive_json()["snapshot"]["spatial"]["latest_pose_paper"], self.latest_pose)
            self.paper("paper_selection", {**ray, "selection_id": "ray-1"})
            event = ws.receive_json()
            self.assertEqual(event["event"]["event_type"], "paper.selection_changed")
            self.assertEqual(event["snapshot"]["spatial"]["shared_selection"]["participant_id"], self.actor)
            self.assertEqual(event["snapshot"]["spatial"]["shared_selection"]["hit_position_paper"], ray["hit_position_paper"])
        self.paper("paper_alignment", {"status": "lost"}, headers=self.peer)
        self.assertEqual(self.snapshot()["spatial"]["participants"][1]["status"], "lost")
        self.paper("paper_selection", ray, headers=self.peer, status=(409,))
        self.align(self.peer)
        self.assertEqual(self.snapshot()["spatial"]["latest_pose_paper"], self.latest_pose)
        self.paper("paper_reset")
        after = self.snapshot(self.peer)["spatial"]
        self.assertEqual(after["paper_frame_revision"], 2)
        self.assertEqual(after["latest_version_id"], self.original)
        self.assertIsNone(after["latest_pose_paper"])
        self.assertIsNone(after["shared_selection"])
        self.assertTrue(all(p["status"] == "not_aligned" for p in after["participants"]))
        self.paper("paper_alignment", {"status": "aligned"}, headers=self.peer, status=(409,))
        self.paper("paper_layout", layout, revision=2, status=(409,))
        self.align(revision=2)
        self.paper("paper_layout", layout, revision=2)
        self.assertEqual(self.snapshot(self.peer)["spatial"]["latest_pose_paper"], self.latest_pose)

    def spatial_target(self, key):
        result = super().spatial_target(key)
        ref = result["reference_selection"]
        ref.update({k: v for k, v in self.shared_ray(self.original).items() if k != "model_version_id"}, paper_frame_revision=1)
        return result

    def test_candidates_commits_and_late_join_keep_exact_poses_and_assets(self):
        self.next_silence = time.monotonic()
        self.setup_layout()
        self.align(self.peer)
        def proposed(ctx, images):
            result = self.proposed(ctx, 6.1, 6.5)
            if ctx["request_id"] == "v2-edit":
                result["interpreted_target"]["target_parameter_candidates"] = ["slot_pitch"]
                for candidate, pitch in zip(result["candidates"], (14, 13)):
                    candidate.update(interpretation="슬롯 간격을 줄임", parameter_changes={"slot_pitch": pitch})
            elif ctx["request_id"] == "v3-edit":
                result["interpreted_target"]["target_parameter_candidates"] = ["mouth_width"]
                for candidate, width in zip(result["candidates"], (5.8, 5.6)):
                    candidate.update(interpretation="맨 위 입구 폭을 좁힘", parameter_changes={"mouth_width": width})
            return result, {"source": "test_provider"}

        with patch.object(bundle_interpretation, "call_provider", side_effect=proposed):
            for key, text in (("v1-edit", "홈을 넓혀줘"), ("v2-edit", "슬롯 간격을 줄여줘"), ("v3-edit", "홈 입구 폭을 줄여줘")):
                self.save_request(key, text)
                item = self.wait_set(key)
                self.assertEqual(item["status"], "ready", item)
                poses = {c["candidate_id"]: {"position": [-.16 if c["candidate_key"] == "A" else .16, 0, .01],
                                             "rotation_xyzw": self.latest_pose["rotation_xyzw"]} for c in item["candidates"]}
                selection = {"candidate_set_id": item["candidate_set_id"], "candidate_id": item["candidates"][-1]["candidate_id"]}
                layout = {"latest_version_id": self.original, "active_candidate_set_id": item["candidate_set_id"],
                          "candidate_poses_paper": poses}
                self.paper("paper_candidate_select", selection, headers=self.peer, status=(409,))
                self.paper("paper_layout", {**layout, "candidate_poses_paper": {"bogus": self.latest_pose}}, status=(409,))
                self.paper("paper_layout", layout)
                self.paper("paper_candidate_select", selection, headers=self.peer)
                state = self.snapshot(self.peer)
                spatial = state["spatial"]
                self.assertEqual(spatial["candidate_poses_paper"], poses)
                self.assertEqual(spatial["selected_candidate_id"], selection["candidate_id"])
                self.assertEqual(spatial["selected_by_participant_id"], spatial["participants"][1]["participant_id"])
                self.assertEqual(spatial["active_request_id"], key)
                paper_evidence = state["edit_requests"][key]["selection"]
                self.assertEqual(paper_evidence["hit_position_paper"], self.shared_ray(self.original)["hit_position_paper"])
                self.assertNotIn("ray_origin_world", paper_evidence)
                self.assertNotIn("camera_pose", state["edit_requests"][key]["visual_evidence"])
                saved = json.loads((self.edge.root / "edit_requests" / self.sid / key / "request.json").read_bytes())
                self.assertIn("ray_origin_world", saved["selection"])
                self.assertEqual(saved["selection"]["paper_frame_revision"], 1)
                if key == "v3-edit":
                    break
                cmd = {**selection, "meeting_id": self.sid, "request_id": key, "base_version_id": self.original,
                       "expected_latest_version_id": self.original}
                self.paper("commit_edit_candidate", cmd, headers=self.peer, status=(403,))
                self.paper("commit_edit_candidate", cmd, revision=2, status=(409,))
                accepted = self.paper("commit_edit_candidate", cmd)
                self.original = accepted["version_id"]
                peer = self.snapshot(self.peer)["spatial"]
                self.assertEqual(accepted["snapshot"]["spatial"], peer)
                self.assertEqual(peer["latest_version_id"], self.original)
                self.assertEqual(peer["latest_pose_paper"], self.latest_pose)
                self.assertEqual(peer["candidate_poses_paper"], {})
                self.assertIsNone(peer["selected_candidate_id"])
                self.assertIsNone(peer["shared_selection"])
                self.paper("paper_selection", self.shared_ray(self.original))
        response = self.client.post("/v1/join", json={"display_name": "C", "device_id": "late", "join_code": self.snapshot()["join_code"]})
        self.assertEqual(response.status_code, 200, response.text)
        connection = response.json()
        late = {"Authorization": "Bearer " + connection["token"]}
        spatial = connection["snapshot"]["spatial"]
        self.assertEqual(spatial["participants"][-1]["status"], "not_aligned")
        self.assertEqual((spatial["latest_version_id"], spatial["active_candidate_set_id"], spatial["candidate_poses_paper"]),
                         (self.original, item["candidate_set_id"], poses))
        self.align(late)
        mesh = spatial["latest_model_asset"]
        self.assertEqual(hashlib.sha256(self.client.get(mesh["url"], headers=late).content).hexdigest(), mesh["sha256"])
        self.paper("paper_alignment", {"status": "lost"}, headers=late)
        self.align(late)
        self.assertEqual(self.snapshot(late)["spatial"]["latest_version_id"], self.original)
        self.assertEqual(self.snapshot(late)["spatial"]["candidate_poses_paper"], poses)
