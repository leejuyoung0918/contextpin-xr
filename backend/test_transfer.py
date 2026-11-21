"""Exercise the local relay contract with test-only audio and image bytes."""

import base64
from concurrent.futures import ThreadPoolExecutor
from hashlib import sha256
from http.client import HTTPResponse
from io import BytesIO
import json
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest import TestCase
from unittest.mock import patch

import server
from transfer import TransferError, TransferStore

PHOTO_FIXTURE = bytes.fromhex("ffd8ff") + b"TEST_FIXTURE" + bytes.fromhex("ffd9")
PHOTO_SHA = sha256(PHOTO_FIXTURE).hexdigest()


class SocketFixture:
    def __init__(self, data):
        self.input = BytesIO(data)
        self.output = bytearray()

    def makefile(self, *_):
        return self.input

    def sendall(self, content):
        self.output.extend(content)

    def settimeout(self, _):
        pass


class ReadFixture:
    def __init__(self, value):
        self.value = value

    def makefile(self, *_):
        return BytesIO(self.value)


class TransferTests(TestCase):
    def setUp(self):
        self.temp = TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.store = TransferStore(self.temp.name)
        self.session = "fixture-01"
        self.code = "ABCDEFG234"
        self.dev_token = "devtoken-fixture-1234567890-secure"
        self.store.create(self.request(sourceMode="real", consent=True, deviceToken=self.dev_token,
                                       pairingCode=self.code), "")
        self.teacher_token = self.store.pair({"pairingCode": "ABCDE-FG234"})["teacherToken"]
        self.state = self.request(revision=0, stage="registration", registrationRevision=1)
        self.store.publish(self.state, self.dev_token)

    def request(self, **other):
        return {"sessionId": self.session, **other}

    def publish(self, **updates):
        self.state.update(revision=self.state["revision"] + 1)
        self.state.update(updates)
        return self.store.publish(self.state, self.dev_token)

    def capture(self, **updates):
        capture = dict(captureId="fixture-photo", source="real_camera", phase="student_checkpoint",
                       registrationRevision=1, stepId="led", quality="saved", sha256=PHOTO_SHA)
        capture.update(updates)
        self.store.save_asset(self.request(sha256=PHOTO_SHA, mime="image/jpeg",
                                           base64=base64.b64encode(PHOTO_FIXTURE).decode()), self.dev_token)
        self.publish(stage="practice", captures=[capture])
        return self.request(captureId=capture["captureId"], registrationRevision=1, stepId="led")

    def test_credentials_survive_restart_and_protect_separate_roles(self):
        reopened = TransferStore(self.temp.name)
        saved = reopened.report(self.request(), self.teacher_token, "teacher")
        self.assertEqual(saved["snapshot"]["sourceMode"], "real")
        self.assertNotIn(self.dev_token, (Path(self.temp.name) / "transfer.sqlite3").read_bytes().decode("latin1"))
        for token in ("", self.teacher_token):
            with self.assertRaises(TransferError) as failure:
                reopened.poll(self.request(), token)
            self.assertEqual(failure.exception.status, 401)
        with self.assertRaises(TransferError):
            reopened.create(self.request(sourceMode="real", consent=True), "")
        replay = reopened.create(self.request(sourceMode="real", consent=True, pairingCode=self.code), self.dev_token)
        self.assertEqual(replay["deviceToken"], self.dev_token)

    def test_publish_conflict_and_versioned_approval(self):
        self.assertTrue(self.store.publish(self.state, self.dev_token)["duplicate"])
        with self.assertRaises(TransferError) as failure:
            self.store.publish(self.request(revision=0, stage="recording"), self.dev_token)
        self.assertEqual(failure.exception.status, 409)
        guide = {"id": "guide01", "version": 1, "approved": True, "steps": [{"kind": "prepare"}]}
        self.publish(stage="approved", guide=guide)
        with self.assertRaises(TransferError):
            self.publish(stage="approved", guide={**guide, "title": "Changed same approval"})
        self.publish(stage="approved", guide={**guide, "version": 2, "title": "A new approval"})
        report = self.store.report(self.request(), self.teacher_token, "teacher")
        self.assertEqual([a["version"] for a in report["approvals"]], [1, 2])

    def test_unassigned_unity_dtos_are_normalized_without_a_phantom_guide(self):
        self.publish(stage="recording", guide={"id": "", "steps": []},
                     registration={"boardInstanceId": "", "revision": 0}, help={"question": "", "stepId": ""})
        state = self.store.report(self.request(), self.teacher_token, "teacher")["snapshot"]
        self.assertEqual(state["schemaVersion"], 1)
        for field in ("guide", "registration", "help"):
            self.assertIsNone(state[field])

    def test_duplicate_teacher_actions_poll_and_ack_exactly_once(self):
        command = self.request(commandId="capture-01", expectedRevision=0, kind="capture",
                               payload={"phase": "setup"})
        with ThreadPoolExecutor(max_workers=4) as pool:
            results = list(pool.map(lambda _: self.store.issue_command(command, self.teacher_token), range(6)))
        self.assertTrue(all(results[0] == result for result in results))
        pending = self.store.poll(self.request(), self.dev_token)["commands"]
        self.assertEqual(len(pending), 1)
        self.assertEqual(pending[0]["id"], "capture-01")
        ack = self.request(commandId="capture-01", status="applied", detail="Saved")
        first = self.store.acknowledge(ack, self.dev_token)
        self.assertEqual(self.store.acknowledge(ack, self.dev_token), first)
        self.assertEqual(self.store.poll(self.request(), self.dev_token)["commands"], [])
        self.publish(stage="recording")
        old = self.store.issue_command(command, self.teacher_token)
        self.assertEqual(old["status"], "applied")
        with self.assertRaises(TransferError):
            self.store.issue_command({**command, "commandId": "another"}, self.teacher_token)

    def test_teacher_queues_edits_and_does_not_fabricate_approval(self):
        self.publish(stage="review", guide={"id": "guide01", "approved": False, "steps": [{"kind": "prepare"}]})
        payload = {"steps": [{"kind": "prepare"}], "kitConfirmed": True, "kitConditions": "교수 확인"}
        self.store.issue_command(self.request(commandId="approve-01", expectedRevision=1,
                                             kind="approveDraft", payload=payload), self.teacher_token)
        report = self.store.report(self.request(), self.teacher_token, "teacher")
        self.assertFalse(report["snapshot"]["guide"]["approved"])
        self.assertEqual(report["commands"][0]["payload"], payload)
        with self.assertRaises(TransferError):
            self.store.issue_command(self.request(commandId="manual-01", expectedRevision=0,
                                                 kind="manualTranscript", payload={"text": "안내"}), self.teacher_token)

    def test_saved_evidence_requires_hash_and_same_session(self):
        payload = self.request(sha256=PHOTO_SHA, mime="image/jpeg",
                               base64=base64.b64encode(PHOTO_FIXTURE).decode())
        self.assertFalse(self.store.save_asset(payload, self.dev_token)["duplicate"])
        self.assertTrue(self.store.save_asset(payload, self.dev_token)["duplicate"])
        self.assertEqual(self.store.fetch_asset(self.session, PHOTO_SHA, self.teacher_token),
                         ("image/jpeg", PHOTO_FIXTURE))
        with self.assertRaises(TransferError):
            self.store.save_asset({**payload, "sha256": "f" * 64}, self.dev_token)
        another = self.store.create({"sessionId": "fixture-02", "sourceMode": "synthetic", "consent": True}, "")
        with self.assertRaises(TransferError):
            self.store.fetch_asset(self.session, PHOTO_SHA, another["deviceToken"])

    def test_synthetic_cannot_publish_real_capture_or_request_one(self):
        synthetic = self.store.create({"sessionId": "fixture-02", "sourceMode": "synthetic", "consent": True}, "")
        token = synthetic["deviceToken"]
        with self.assertRaises(TransferError):
            self.store.publish({"sessionId": "fixture-02", "stage": "recording", "revision": 0,
                                "captures": [{"captureId": "c", "phase": "after_action", "source": "real_camera"}]}, token)
        self.store.publish({"sessionId": "fixture-02", "stage": "recording", "revision": 0}, token)
        teacher = self.store.pair({"pairingCode": synthetic["pairingCode"]})["teacherToken"]
        with self.assertRaises(TransferError):
            self.store.issue_command({"sessionId": "fixture-02", "expectedRevision": 0,
                                      "commandId": "capture-01", "kind": "capture",
                                      "payload": {"phase": "after_action"}}, teacher)

    def test_setup_photo_is_ineligible_for_observing_current_wiring(self):
        body = self.capture(phase="setup")
        with self.assertRaises(TransferError):
            self.store.begin_observation(body, self.dev_token)

    def test_marker_measurement_uses_saved_setup_metadata_and_checks_revision(self):
        body = self.capture(phase="setup")
        with patch.object(server, "marker_adapter", return_value=None):
            result = server.marker_pose_transfer(self.store, body, self.dev_token)
        self.assertEqual(result["status"], "unavailable")
        self.assertEqual(result["source"], "real_camera")
        self.assertTrue(result["mustVerifyOnBoard"])
        self.publish(registrationRevision=2)
        with self.assertRaises(TransferError) as failure:
            server.marker_pose_transfer(self.store, body, self.dev_token)
        self.assertEqual(failure.exception.status, 409)

    def test_late_observation_from_old_registration_is_unknown(self):
        body = self.capture()
        started, _ = self.store.begin_observation(body, self.dev_token)
        self.publish(registrationRevision=2)
        result = self.store.finish_observation(self.session, started, {"verdict": "visible_candidate"}, "ready", 42)
        self.assertEqual((result["status"], result["verdict"]), ("stale", "unknown"))

    def test_vision_fixture_never_elevates_part_candidate_to_verification(self):
        body = self.capture()
        reply = {"choices": [{"message": {"content": '{"visiblePart":"led","appearance":"visible","electricalVerified":true}'}}]}
        with patch.dict(server.os.environ, {"AI_BASE_URL": "http://127.0.0.1:9998", "AI_VISION_MODEL": "fixture"}), \
                patch.object(server, "upstream", return_value=reply) as provider:
            result = server.observe_transfer(self.store, body, self.dev_token)
            again = server.observe_transfer(self.store, body, self.dev_token)
        self.assertEqual(result, again)
        self.assertEqual(result["verdict"], "visible_candidate")
        self.assertNotIn("electricalVerified", result)
        provider.assert_called_once()


class TransferHTTPTests(TestCase):
    def request(self, method, route, body=None, origin=None, host="127.0.0.1:8787", bearer=None):
        raw = json.dumps(body).encode() if body is not None else b""
        headers = [f"{method} {route} HTTP/1.1", f"Host: {host}", "Connection: close"]
        if body is not None:
            headers += ["Content-Type: application/json", f"Content-Length: {len(raw)}"]
        if origin:
            headers.append(f"Origin: {origin}")
        if bearer:
            headers.append(f"Authorization: Bearer {bearer}")
        fixture = SocketFixture(("\r\n".join(headers) + "\r\n\r\n").encode() + raw)
        server.Handler(fixture, ("127.0.0.1", 12345), None)
        response = HTTPResponse(ReadFixture(fixture.output))
        response.begin()
        return response.status, response.headers, response.read()

    def test_only_same_origin_teacher_requests_and_static_csp(self):
        status, headers, data = self.request("GET", "/teacher")
        self.assertEqual(status, 200)
        self.assertIn(b"/teacher.js", data)
        self.assertIn("frame-ancestors 'none'", headers["Content-Security-Policy"])
        status, _, _ = self.request("GET", "/teacher", host="untrusted.invalid:8787")
        self.assertEqual(status, 403)
        status, _, _ = self.request("POST", "/transfer/teacher/pair", {"pairingCode": "ABCDEFG234"},
                                     origin="https://third-party.invalid")
        self.assertEqual(status, 403)
        with TemporaryDirectory() as directory, patch.dict(server.os.environ, {"RELAY_DATA_DIR": directory}):
            status, _, data = self.request("POST", "/transfer/session", {"sessionId": "http-fixture",
                "sourceMode": "replay", "consent": True}, origin=None)
            self.assertEqual(status, 200)
            code = json.loads(data)["pairingCode"]
            status, _, data = self.request("POST", "/transfer/teacher/pair", {"pairingCode": code},
                                         origin="http://127.0.0.1:8787")
            self.assertEqual(status, 200)
            teacher = json.loads(data)
            status, _, data = self.request("POST", "/transfer/teacher/state", {"sessionId": "http-fixture"},
                                         origin="http://127.0.0.1:8787", bearer=teacher["teacherToken"])
            self.assertEqual(status, 200)
            self.assertEqual(json.loads(data)["snapshot"]["sourceMode"], "replay")

    def test_missing_remote_key_does_not_report_model_configuration_ready(self):
        with patch.dict(server.os.environ, {"AI_TRANSCRIPTION_MODEL": "test-model"}, clear=True):
            status, _, data = self.request("GET", "/health/capabilities")
        self.assertEqual(status, 200)
        payload = json.loads(data)
        self.assertFalse(payload["speechConfigured"])
        self.assertEqual(payload["speechProvider"]["status"], "unconfigured")
