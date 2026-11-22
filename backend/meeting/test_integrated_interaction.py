"""Two authenticated clients, two marked commands, two actual CAD builds and commits on one A4 frame."""
import json
import sqlite3
import threading
import time
from contextlib import closing
from unittest.mock import patch

from . import bundle_interpretation, paper_space
from .coordinator import SessionCoordinator
from .edit_bundle import transform_point
from .test_b_hold import BHoldCase


class IntegratedInteractionTests(BHoldCase):
    phrases = {**BHoldCase.phrases, 4000: "두 후보의 차이를 살펴보자.", 5000: "홈 간격을 줄여줘."}

    def setUp(self):
        super().setUp()
        self.rev = 0
        self.pose = {"position": [.18, .001, 0], "rotation_xyzw": [0, 1, 0, 0]}
        for auth in (self.headers, self.peer):
            self.command("paper_alignment", {"status": "aligned", "paper_frame_revision": 1}, headers=auth)
        self.command("paper_layout", {"paper_frame_revision": 1, "latest_version_id": self.original,
                                      "latest_pose_paper": self.pose})

    def snapshot(self, headers=None):
        if getattr(self, "keep_audio", False) and time.monotonic() >= self.next_silence:
            self.next_silence = time.monotonic() + 1
            self.send_pcm(0, 1000)
        return super().snapshot(headers)

    def move(self, x, z, final=True, auth=None, status=(200,)):
        result = self.command("paper_move_latest", {"paper_frame_revision": 1, "latest_version_id": self.original,
            "expected_pose_revision": self.rev, "paper_size_m": [.297, .210], "grab_id": "host-grip",
            "position_xz_m": [x, z], "final": final}, headers=auth, status=status)
        if "latest_pose_revision" in result:
            self.rev = result["latest_pose_revision"]
            self.pose = result["latest_pose_paper"]
        return result

    def mark_current(self, key, expected):
        self.socket.send_json({"type": "design_speech_boundary", "edge": "start", "range_id": key,
            "sample_index": self.sample, "base_version_id": self.original,
            "paper_frame_revision": 1, "expected_pose_revision": expected})

    def say(self, tone):
        self.send_pcm(tone, 4000)
        self.send_pcm(0, 1200)

    def assert_lock(self, status, rid):
        spatial = self.snapshot(self.peer)["spatial"]
        self.assertEqual(spatial["latest_pose_paper"], self.pose)
        self.assertEqual(spatial["edit_lock"], {"request_id": rid, "participant_id": self.actor, "status": status})

    def test_v1_to_v3_with_frozen_moved_location_and_continuous_audio(self):
        self.say(1000)
        expected_lines = [self.phrases[1000]]
        expected_decisions = 0
        for round_index, (rid, x, z, tone) in enumerate((("hold-v1", .22, .05, 2000), ("hold-v2", -.21, -.08, 5000))):
            base = self.original
            self.move(x, z, auth=self.peer, status=(403,))
            self.move(x, z, final=False)
            self.assertTrue(self.snapshot(self.peer)["spatial"]["drag_active"])
            self.mark_current("during-grip-" + rid, self.rev)
            self.await_range("during-grip-" + rid, "failed")
            self.move(x, z)
            self.assertEqual(self.snapshot(self.peer)["committed_version_id"], base)
            self.assertEqual(len(self.snapshot()["decisions"]), expected_decisions)
            self.mark_current("stale-" + rid, self.rev - 1)
            self.await_range("stale-" + rid, "failed")
            self.mark_current(rid, self.rev)
            row = self.await_range(rid, "recording")
            frozen = {k: row[k] for k in paper_space.POSE_FIELDS}
            self.assertEqual(frozen, {"latest_pose_paper": self.pose, "paper_frame_revision": 1, "latest_pose_revision": self.rev})
            self.assert_lock("recording", rid)
            self.move(0, 0, status=(409,))
            self.guest_edge("guest-" + rid, "start", self.sample, status=(409,))
            self.send_pcm(tone, 4000)
            self.mark(rid, "end")
            self.send_pcm(0, 1400)
            final = self.await_range(rid, "final")
            self.assertEqual(final["command_text"], self.phrases[tone])
            self.assert_lock("awaiting_target", rid)
            self.move(0, 0, status=(409,))
            expected_lines.append(self.phrases[tone])
            target = self.spatial_target(rid)
            target["expected_adopted_revision"] = round_index
            self.command("design_target", target, status=(409, 400))
            ref = target["reference_selection"]
            hit = transform_point(self.pose, ref["hit_model_local_position"])
            ref.update(hit_position_paper=hit, ray_origin_paper=[hit[0], hit[1] + .5, hit[2]], ray_direction_paper=[0, -1, 0],
                       paper_frame_revision=1, latest_pose_revision=self.rev)
            self.command("design_target", {**target, "reference_selection": {**ref, "latest_pose_revision": self.rev - 1}}, status=(409,))
            selected = self.command("design_target", target)["snapshot"]["spatial"]["shared_selection"]
            self.assertEqual((selected["selection_id"], selected["hit_position_paper"]), (ref["selection_id"], hit))
            self.assertFalse(self.snapshot()["candidate_sets"].get(rid))
            request = {**target, "output": "bundle", "speech_range_id": rid,
                       "utterance_refs": [{k: u[k] for k in ("utterance_id", "revision")} for u in final["utterances"]]}
            entering, release = threading.Event(), threading.Event()
            def provider(ctx, images):
                self.assertEqual(ctx["transcript"], self.phrases[tone])
                self.assertEqual(ctx["base_version_id"], base)
                self.assertEqual(len(images), 2)
                entering.set()
                if not release.wait(10):
                    raise AssertionError("timed out holding model generation")
                result = self.proposed(ctx, 6.1, 6.5)
                if round_index == 1:
                    result["interpreted_target"]["target_parameter_candidates"] = ["slot_pitch"]
                    for c, pitch in zip(result["candidates"], [14, 13]):
                        c.update(interpretation="홈 간격을 줄임", parameter_changes={"slot_pitch": pitch})
                return result, {"source": "test_provider"}
            with patch.object(bundle_interpretation, "call_provider", side_effect=provider):
                try:
                    saved = self.command("design_request", request)["snapshot"]["edit_requests"][rid]
                    self.assertTrue(entering.wait(5))
                    self.assert_lock("generating", rid)
                    self.move(0, 0, status=(409,))
                    self.mark_current("generating-" + rid, self.rev)
                    self.await_range("generating-" + rid, "failed")
                    self.say(4000)
                    expected_lines.append(self.phrases[4000])
                    self.wait_lines(len(expected_lines))
                finally:
                    release.set()
                self.keep_audio, self.next_silence = True, time.monotonic()
                item = self.wait_set(rid)
                self.keep_audio = False
            self.assertEqual((item["status"], len(item["candidates"])), ("ready", 2))
            folder = self.edge.root / "edit_requests" / self.sid / rid
            for stored in (saved, item, json.loads((folder / "request.json").read_text()),
                           json.loads((folder / "candidate_set.json").read_text())):
                self.assertEqual({key: stored[key] for key in frozen}, frozen)
            self.assertEqual(saved["selection"]["latest_pose_revision"], self.rev)
            self.assertEqual(saved["command_text"], self.phrases[tone])
            self.assertEqual(len(saved["command_utterance_ids"]), 1)
            self.assert_lock("ready", rid)
            self.move(0, 0, status=(409,))
            poses = {c["candidate_id"]: {**self.pose, "position": [x + offset, self.pose["position"][1], z]}
                     for c, offset in zip(item["candidates"], (-.14, .14))}
            layout = {"candidate_poses_paper": poses, "active_candidate_set_id": item["candidate_set_id"],
                      "latest_version_id": base, "paper_frame_revision": 1, "expected_pose_revision": self.rev}
            self.command("paper_layout", {**layout, "expected_pose_revision": self.rev - 1}, status=(409,))
            self.command("paper_layout", layout)
            self.assertEqual(self.snapshot(self.peer)["spatial"]["candidate_poses_paper"], poses)
            chosen = item["candidates"][1]
            choice = {"paper_frame_revision": 1, "expected_pose_revision": self.rev,
                      "candidate_set_id": item["candidate_set_id"], "candidate_id": chosen["candidate_id"]}
            self.command("paper_candidate_select", choice, headers=self.peer)
            commit = {**choice, "meeting_id": self.sid, "request_id": rid, "base_version_id": base, "expected_latest_version_id": base}
            self.command("commit_edit_candidate", {**commit, "expected_pose_revision": self.rev - 1}, status=(409,))
            self.command("commit_edit_candidate", commit, headers=self.peer, status=(403,))
            adopted = self.command("commit_edit_candidate", commit)
            self.original = adopted["version_id"]
            expected_decisions += 1
            guest = self.snapshot(self.peer)["spatial"]
            self.assertIsNone(guest["edit_lock"])
            self.assertEqual(guest["latest_pose_paper"], self.pose)
            self.assertEqual(guest["latest_pose_revision"], self.rev)
            self.assertEqual(guest["latest_version_id"], chosen["version_id"])
            if round_index == 0:
                self.say(3000)
                expected_lines.append(self.phrases[3000])
        state = self.snapshot()
        self.assertEqual([u["text"] for u in state["utterances"]], expected_lines)
        self.assertEqual([d["outcome"] for d in state["decisions"]], ["commit", "commit"])
        self.assertEqual(next(v["design_version"] for v in state["versions"] if v["version_id"] == self.original), "V3")

    def test_expired_grip_and_cancel_release_pose_without_changing_version(self):
        with patch("backend.meeting.paper_space.now_ms", return_value=10000):
            self.move(.23, 0, final=False)
            before = self.snapshot()
        with patch("backend.meeting.paper_space.now_ms", return_value=14000):
            events, after = self.edge.wait_events(self.sid, self.actor, before["last_seq"])
        self.assertIn("paper.grip_expired", [e["event_type"] for e in events])
        self.assertFalse(after["spatial"]["drag_active"])
        self.mark_current("cancel",  self.rev)
        self.await_range("cancel", "recording")
        self.send_pcm(2000, 4000)
        self.mark("cancel",  "end")
        self.send_pcm(0, 1200)
        self.await_range("cancel", "final")
        self.command("cancel_design_speech", {"range_id": "cancel"})
        self.assertIsNone(self.snapshot(self.peer)["spatial"]["edit_lock"])
        self.move(.18, -.03)
        self.assertEqual(self.snapshot()["decisions"], [])

    def test_awaiting_target_unlocks_after_restart_or_explicit_paper_reset(self):
        self.move(.21, -.04)
        self.mark_current("interrupted", self.rev)
        self.await_range("interrupted", "recording")
        self.send_pcm(2000, 4000)
        self.mark("interrupted", "end")
        self.send_pcm(0, 1200)
        self.await_range("interrupted", "final")
        self.assert_lock("awaiting_target", "interrupted")
        directory = self.edge.root.parent / "recovery"
        directory.mkdir()
        with closing(sqlite3.connect(directory / "sessions.sqlite3")) as db, self.edge.lock:
            self.edge.db.backup(db)
        with closing(SessionCoordinator(directory)) as restarted:
            saved = restarted.public(restarted.load(self.sid), self.actor)
            self.assertIsNone(saved["spatial"]["edit_lock"])
            self.assertEqual(saved["spatial"]["latest_pose_paper"], self.pose)
            self.assertEqual(saved["design_speech_ranges"]["interrupted"]["status"], "failed")
        self.command("paper_reset", {"paper_frame_revision": 1})
        state = self.snapshot(self.peer)
        self.assertEqual(state["spatial"]["paper_frame_revision"], 2)
        self.assertIsNone(state["spatial"]["edit_lock"])
        self.assertEqual(state["design_speech_ranges"]["interrupted"]["status"], "failed")
