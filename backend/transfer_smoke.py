#!/usr/bin/env python3
"""Real loopback HTTP smoke with bundled fixtures and explicitly mocked providers."""

import argparse
import base64
from datetime import datetime, timezone
from hashlib import sha256
from http.server import ThreadingHTTPServer
import json
from pathlib import Path
import platform
from tempfile import TemporaryDirectory
from threading import Thread
from urllib.request import Request, urlopen
from unittest.mock import patch

import server


def exercise(base_url, fixtures):
    checks = []
    credentials = {}
    session_id = "http-smoke-fixture"

    def get(path, token=None):
        headers = {"Authorization": "Bearer " + token} if token else {}
        with urlopen(Request(base_url + path, headers=headers), timeout=5) as response:
            return response.read()

    def post(path, body, token=None, browser=False):
        headers = {"Content-Type": "application/json"}
        if token:
            headers["Authorization"] = "Bearer " + token
        if browser:
            headers["Origin"] = base_url
        with urlopen(Request(base_url + path, json.dumps(body).encode(), headers), timeout=5) as response:
            return json.load(response)

    def device(path, **body):
        return post(path, {"sessionId": session_id, **body}, credentials["deviceToken"])

    def review(path, **body):
        return post(path, {"sessionId": session_id, **body}, credentials["teacherToken"], browser=True)

    def confirm(condition, description):
        if not condition:
            raise AssertionError(description)
        checks.append(description)

    confirm(b"/teacher.js" in get("/teacher") and b"approveDraft" in get("/teacher.js"),
            "The PC reviewer and its controls load over actual HTTP.")
    configured = json.loads(get("/health/capabilities"))
    confirm(configured["speechProvider"]["model"] == "stt-fixture" and configured["visionProvider"]["configured"],
            "The doctor exposes explicit provider configuration and transport.")
    credentials.update(post("/transfer/session", {"sessionId": session_id, "consent": True,
                     "deviceLabel": "isolated HTTP fixture", "sourceMode": "real"}))
    credentials.update(post("/transfer/teacher/pair", {"pairingCode": credentials["pairingCode"]}, browser=True))

    device("/transfer/state", revision=1, stage="recording", registrationRevision=2)
    remote = review("/transfer/teacher/command", kind="capture", commandId="capture-fixture",
                    expectedRevision=1, payload={"phase": "after_action"})
    repeated = review("/transfer/teacher/command", kind="capture", commandId="capture-fixture",
                      expectedRevision=1, payload={"phase": "after_action"})
    commands = device("/transfer/poll")["commands"]
    confirm(remote == repeated and len(commands) == 1 and commands[0]["kind"] == "capture",
            "A PC capture request reaches the device once across HTTP retries.")
    device("/transfer/ack", commandId=remote["id"], status="applied", detail="Fixture acknowledged")

    wave = (fixtures / "input_voice.wav").read_bytes()
    photo = (fixtures / "input_0.jpg").read_bytes()
    hashes = {}
    for mime, content in [("audio/wav", wave), ("image/jpeg", photo)]:
        digest = sha256(content).hexdigest()
        hashes[mime] = digest
        saved = device("/transfer/blob", sha256=digest, mime=mime, base64=base64.b64encode(content).decode())
        confirm(saved["sha256"] == digest and
                get("/transfer/evidence/" + session_id + "/" + digest, credentials["teacherToken"]) == content,
                "The paired PC reads back the identical " + mime + " asset by SHA-256.")

    stt = post("/trace/transcribe", {"audioBase64": base64.b64encode(wave).decode(), "includeMetadata": True})
    confirm(stt["providerItemId"] == "stt-fixture-01" and stt["timingQuality"] == "segment_bounds",
            "The WAV upload consumes a mocked compatible provider final response with honest segment timing.")

    note = {"id": "fixture-note", "audioSha256": hashes["audio/wav"], "photoSha256s": [hashes["image/jpeg"]],
            "transcript": stt["transcript"], "time": 1}
    capture = {"captureId": "photo-fixture", "phase": "student_checkpoint", "source": "real_camera",
               "registrationRevision": 2, "stepId": "led", "quality": "visibility_unverified",
               "sha256": hashes["image/jpeg"]}
    draft = {"id": "fixture-guide", "version": 1, "approved": False,
             "steps": [{"kind": "led", "stepId": "led", "sourceNoteId": note["id"]}]}
    device("/transfer/state", revision=2, stage="review", registrationRevision=2, guide=draft, notes=[note])
    review("/transfer/teacher/command", kind="manualTranscript", commandId="manual-fixture",
           expectedRevision=2, payload={"text": "교수 작성 설명", "sourceNoteId": note["id"]})
    device("/transfer/ack", commandId="manual-fixture", status="applied", detail="Fixture acknowledged")
    approve = review("/transfer/teacher/command", kind="approveDraft", commandId="approval-fixture",
                     expectedRevision=2, payload={"steps": draft["steps"], "kitConfirmed": True,
                                                  "kitConditions": "격리된 테스트 조건"})
    confirm(approve["status"] == "pending" and not review("/transfer/teacher/state")["snapshot"]["guide"]["approved"],
            "The teacher can send a proposal, while the PC relay does not finalize approval.")
    approved = {**draft, "approved": True}
    device("/transfer/state", revision=3, stage="practice", registrationRevision=2,
           guide=approved, notes=[note], captures=[capture], help={"stepId": "led", "question": "극성을 확인하고 싶어요."})
    device("/transfer/ack", commandId=approve["id"], status="applied", detail="Fixture device accepted")
    observation = device("/transfer/observe", captureId=capture["captureId"], stepId="led", registrationRevision=2)
    repeated_observation = device("/transfer/observe", captureId=capture["captureId"], stepId="led", registrationRevision=2)
    confirm(observation == repeated_observation and observation["verdict"] == "visible_candidate" and
            "electricalVerified" not in observation,
            "The explicit event photo yields a mocked visible-only candidate and no electrical assertion.")

    # Reopen the store from disk, as on a relay restart, while keeping the wire protocol active.
    with server.STORE_LOCK:
        server.STORES.clear()
    saved_report = review("/transfer/teacher/state")
    confirm(saved_report["snapshot"]["guide"]["approved"] and saved_report["snapshot"]["help"]["stepId"] == "led" and
            len(saved_report["approvals"]) == 1 and len(saved_report["observations"]) == 1 and
            len(saved_report["assets"]) == 2,
            "The PC recovers its approvals, help card, observations and assets from disk after reopening the store.")
    return checks, saved_report["assets"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, help="Write the smoke result as JSON")
    args = parser.parse_args()
    fixtures = Path(__file__).resolve().parents[1] / "ContextPinRelay/Assets/Resources/TraceDemo"

    def fixture_provider(path, payload, *args, **kwargs):
        if path == "/audio/transcriptions":
            return {"id": "stt-fixture-01", "text": "보드의 양극 위치를 확인하세요."}
        return {"choices": [{"message": {"content": '{"visiblePart":"led","appearance":"visible"}'}}]}

    result = {
        "status": "pass", "startedAt": datetime.now(timezone.utc).isoformat(),
        "environment": {"system": platform.platform(), "python": platform.python_version()},
        "transport": "real_loopback_http", "mode": "isolated_fixture", "providerSource": "mocked_responses",
        "hardwareSource": "bundled_example_audio_and_photo", "checks": [],
    }
    try:
        with TemporaryDirectory() as directory, patch.dict(server.os.environ, {
            "RELAY_DATA_DIR": directory, "AI_BASE_URL": "http://127.0.0.1:32767",
            "AI_TRANSCRIPTION_MODEL": "stt-fixture", "AI_VISION_MODEL": "vision-fixture",
        }), patch.object(server, "upstream", side_effect=fixture_provider) as provider, \
                ThreadingHTTPServer(("127.0.0.1", 0), server.Handler) as listener:
            thread = Thread(target=listener.serve_forever, daemon=True)
            thread.start()
            try:
                result["checks"], result["assets"] = exercise(f"http://127.0.0.1:{listener.server_port}", fixtures)
                assert provider.call_count == 2
            finally:
                listener.shutdown()
                thread.join()
    except Exception as error:
        result.update(status="fail", detail=f"{type(error).__name__}: {error}")
    result["finishedAt"] = datetime.now(timezone.utc).isoformat()
    encoded = json.dumps(result, ensure_ascii=False, indent=2)
    if args.report:
        args.report.write_text(encoded + "\n", encoding="utf-8")
    print(encoded)
    return 0 if result["status"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
