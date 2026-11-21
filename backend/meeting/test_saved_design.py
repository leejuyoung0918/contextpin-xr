"""Saved speech is a reference from a new CAD task, never a new recording."""
import concurrent.futures
import os
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch

from fastapi.testclient import TestClient
from .contracts import MeetingError, uid
from .main import create_app
from .shared_workflows import registered_scope

ROOT = Path(__file__).resolve().parents[2]


class SavedDesignTests(unittest.TestCase):
    def test_ambiguous_or_fixed_profile_scope_requires_selection(self):
        profile = {"semantic_parts": [{"part_id": "mount"}, {"part_id": "body"}],
                   "parameters": [{"affected_part_ids": ["body"]}]}
        with self.assertRaises(MeetingError):
            registered_scope(profile)
        profile["edit_scope"] = {"part_id": "mount"}
        with self.assertRaises(MeetingError):
            registered_scope(profile)

    def test_saved_request_is_atomic_uses_latest_speech_and_generated_meshes(self):
        config = ROOT / 'artifacts/cable-demo/assembly.json'
        if not config.is_file():
            self.skipTest("Register the original cable CAD for this integration check")
        env = {"AI_MODE":"live", "MEETING_PLAN_BACKEND":"registered", "ACTIVE_OBJECT_PROFILE":"object_2",
               "OPENSCAD_ASSEMBLY_CONFIG":str(config), "FINAL_CONTEXT_DEBOUNCE_MS":"0"}
        with tempfile.TemporaryDirectory() as tmp, patch.dict(os.environ, env), TestClient(create_app(Path(tmp))) as client:
            identity = {"display_name":"후속 검토", "device_id":"saved-test"}
            r = client.post('/v1/sessions', json=identity)
            self.assertEqual(r.status_code, 201, r.text)
            source = r.json()
            def headers(connection):
                return {"Authorization": "Bearer " + connection["token"]}
            def path(connection):
                return "/v1/sessions/" + connection["session_id"]
            def snapshot(connection):
                return client.get(path(connection), headers=headers(connection)).json()
            def command(connection, kind, payload):
                r = client.post(path(connection) + "/commands", headers=headers(connection),
                                json={"command_id":uid("test"), "type":kind, "payload":payload})
                self.assertIn(r.status_code, {200, 202}, r.text)
                return r.json()
            def wait(connection, check):
                deadline = time.monotonic() + 150
                while time.monotonic() < deadline:
                    state = snapshot(connection)
                    if check(state):
                        return state
                    time.sleep(.2)
                self.fail(str([(r["status"], r.get("error")) for r in state["rounds"]]))
            endpoint = path(source) + '/continue-design'
            body = {**identity, "request_id": "user-action-1"}
            self.assertEqual(client.post(endpoint, json=body).status_code, 401)
            self.assertEqual(client.post(endpoint, json=body, headers=headers(source)).status_code, 409)
            command(source, "meeting_start", {"mode":"live", "input_mode":"manual"})
            first = command(source, "text", {"text":"충전선이 너무 헬거워"})
            original_text = "충전선이 너무 헓꺼워 수정한 보여줘"
            command(source, "transcript_correction", {"utterance_id":first["utterance_id"], "text":original_text})
            command(source, "text", {"text":"수정한 보여줘"})
            command(source, "meeting_end", {})
            finished = wait(source, lambda s: s["session_status"] == "completed")
            self.assertEqual(finished["saved_design_utterance"]["text"], original_text)
            self.assertEqual(finished["saved_design_utterance"]["revision"], 2)
            def repeat(_):
                res = client.post(endpoint, json=body, headers=headers(source))
                self.assertEqual(res.status_code, 201, res.text)
                return res.json()
            with concurrent.futures.ThreadPoolExecutor(2) as pool:
                a, b = pool.map(repeat, (0, 1))
            self.assertEqual((a["session_id"], a["token"]), (b["session_id"], b["token"]))
            child = wait(a, lambda s: s["rounds"][-1]["status"] in ("reviewable", "failed", "needs_input"))
            self.assertEqual(child["meeting"]["input_mode"], "manual")
            self.assertEqual((child["capture_state"], child["utterances"], child["decisions"]), ("off", [], []))
            item = child["rounds"][-1]
            self.assertEqual(item["status"], "reviewable", item.get("error"))
            self.assertEqual(len({c["version_id"] for c in item["candidates"]}), 3)
            ctx = item["context"]
            self.assertEqual((ctx["utterance"]["revision"], ctx["utterance"]["text"]), (2, original_text))
            self.assertEqual(ctx["utterance"]["participant_id"], source["participant_id"])
            self.assertEqual(ctx["request_user_id"], a["participant_id"])
            self.assertEqual(ctx["source_session_id"], source["session_id"])
            self.assertEqual(ctx["source_utterance_ids"], [first["utterance_id"]])
            self.assertEqual(ctx["selection_snapshot"]["source_mode"], "profile_scope")
            self.assertEqual(ctx["selection_snapshot"]["part_ids"], ["outer_shell"])
            self.assertIsNone(ctx["selected_position_model_m"])
            self.assertIsNone(ctx["screenshot"])
            self.assertEqual(snapshot(source), finished)
            command(a, "text", {"text":"케이블 입구를 좁히고 싶어요."})
            command(a, "design_request", {"base_version_id":child["committed_version_id"],
                    "expected_adopted_revision":child["adopted_revision"], "use_registered_scope":True})
            active = wait(a, lambda s: s["rounds"][-1]["status"] in ("reviewable","failed","needs_input"))
            self.assertEqual(active["rounds"][-1]["status"], "reviewable")
            self.assertEqual(active["rounds"][-1]["context"]["selection_snapshot"]["source_mode"], "profile_scope")

    def test_pc_archive_operator_can_open_only_a_private_offline_child_for_join_and_adoption(self):
        import json
        import subprocess
        import sys

        config = ROOT / "artifacts/cable-demo/assembly.json"
        if not config.is_file():
            self.skipTest("Register the cable CAD to verify archive operator geometry")
        with tempfile.TemporaryDirectory() as tmp, patch.dict(os.environ, {
            "AI_MODE":"live", "ACTIVE_OBJECT_PROFILE":"object_2", "MEETING_PLAN_BACKEND":"registered",
            "EDGE_DATA_DIR":tmp, "OPENSCAD_ASSEMBLY_CONFIG":str(config), "FINAL_CONTEXT_DEBOUNCE_MS":"0",
        }):
            with TestClient(create_app(Path(tmp))) as c:
                source = c.post("/v1/sessions", json={"display_name":"제작팀", "device_id":"source-headset"})
                self.assertEqual(source.status_code, 201, source.text)
                source = source.json()
                sid = source["session_id"]
                original_headers = {"Authorization":"Bearer "+source["token"]}
                endpoint = "/v1/sessions/" + sid
                def send(kind, payload):
                    r = c.post(endpoint + "/commands", headers=original_headers,
                               json={"type":kind,"command_id":uid("test"),"payload":payload})
                    self.assertTrue(r.is_success, r.text)
                send("meeting_start", {"mode":"live", "input_mode":"manual"})
                line = "충전선이 너무 헓꺼워 수정한 보여줘"
                send("text", {"text":line})
                send("meeting_end", {})
                deadline = time.monotonic()+90
                while time.monotonic()<deadline:
                    before = c.get(endpoint, headers=original_headers).json()
                    if before["session_status"] == "completed":
                        break
                    time.sleep(.15)
                self.assertEqual(before["session_status"], "completed")
                before_zip = c.get(endpoint+"/result.zip",headers=original_headers).content
                cli = [sys.executable, "scripts/relay.py", "open-saved-review", "--session", sid]
                locked = subprocess.run(cli, cwd=ROOT, text=True, capture_output=True, timeout=20)
                self.assertNotEqual(locked.returncode, 0)
                self.assertIn("회의 서버를 정상 종료", locked.stderr)
                self.assertEqual(c.get(endpoint, headers=original_headers).json(), before)
            opened = subprocess.run(cli, cwd=ROOT, text=True, capture_output=True, timeout=180)
            self.assertEqual(opened.returncode, 0, opened.stderr)
            result = json.loads(opened.stdout)
            self.assertEqual(set(result), {"join_code","session_id","status"})
            self.assertEqual(result["status"], "reviewable")
            self.assertNotEqual(result["session_id"], sid)
            with TestClient(create_app(Path(tmp))) as c:
                joined = c.post("/v1/join", json={"display_name":"실제 검토자","device_id":"new-headset",
                                                   "join_code":result["join_code"]}).json()
                child = joined["snapshot"]
                self.assertEqual(child["session_status"], "recording")
                self.assertEqual((child["meeting"]["input_mode"], child["capture_state"]), ("manual","off"))
                self.assertEqual(child["transfer"]["initiated_by"], "local_archive_operator")
                self.assertEqual(child["transfer"]["source_session_id"], sid)
                round_ = child["rounds"][-1]
                self.assertEqual(round_["status"], "reviewable")
                self.assertEqual(len(round_["candidates"]), 3)
                original = round_["context"]["utterance"]
                self.assertEqual(original["text"], line)
                self.assertEqual(original["participant_id"], source["participant_id"])
                self.assertNotEqual(original["participant_id"], child["host_id"])
                self.assertEqual(child["utterances"], [])
                head = {"Authorization":"Bearer "+joined["token"]}
                adopt = c.post("/v1/sessions/"+result["session_id"]+"/commands", headers=head, json={
                    "type":"decide","command_id":uid("test"), "payload":{
                        "round_id":round_["round_id"],"revision":round_["revision"],"outcome":"commit",
                        "candidate_id":round_["candidates"][0]["candidate_id"],
                        "expected_committed_version_id":child["committed_version_id"],
                        "expected_adopted_revision":child["adopted_revision"]}})
                self.assertEqual(adopt.status_code, 200, adopt.text)
                self.assertEqual(c.get(endpoint,headers=head).status_code, 401)
                self.assertEqual(c.get(endpoint,headers=original_headers).json(), before)
                self.assertEqual(c.get(endpoint+"/result.zip",headers=original_headers).content,before_zip)
