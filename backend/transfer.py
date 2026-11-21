"""Durable, loopback transfer of consented training sessions to the local PC."""

import base64
import binascii
from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
from hashlib import sha256
import hmac
import json
import math
import os
from pathlib import Path
import re
import secrets
import sqlite3
import tempfile

MODES = {"real", "synthetic", "replay"}
STAGES = {"registration", "recording", "review", "approved", "practice", "complete"}
PHASES = {"setup", "before_action", "after_action", "clarification", "student_checkpoint"}
MAX_JPEG = 3 * 1024 * 1024
MAX_WAVE = 5 * 1024 * 1024
NOTE_LIMIT = 240
PAIRED_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"
ID_PATTERN = re.compile(r"[A-Za-z0-9_-]{1,100}\Z")
TOKEN_PATTERN = re.compile(r"[A-Za-z0-9_-]{32,128}\Z")
CODE_PATTERN = re.compile(r"[A-Z2-7]{10}\Z")
HASH_PATTERN = re.compile(r"[0-9a-f]{64}\Z")
STATE_FIELDS = ("guide", "notes", "points", "captures", "capabilities", "help", "runs",
                "metrics", "registration", "registrationRevision", "approvalIssue",
                "sourceMode", "events", "verification")


class TransferError(ValueError):
    def __init__(self, message, status=400):
        super().__init__(message)
        self.status = status


def utc_now():
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds")


def canonical(value):
    try:
        return json.dumps(value, sort_keys=True, ensure_ascii=False, allow_nan=False,
                          separators=(",", ":"))
    except (ValueError, TypeError) as exc:
        raise TransferError("Invalid JSON value") from exc


def obj(value, name):
    if not isinstance(value, dict):
        raise TransferError(name + " must be an object")
    return value


def identifier(value, name):
    if not isinstance(value, str) or not ID_PATTERN.fullmatch(value):
        raise TransferError("Invalid " + name)
    return value


def string(value, name, maximum, empty=False):
    if not isinstance(value, str) or len(value) > maximum or (not empty and not value.strip()):
        raise TransferError("Invalid " + name)
    return value


def revision(value):
    if type(value) is not int or not 0 <= value <= 2_147_483_647:
        raise TransferError("Invalid revision")
    return value


def hashed(text):
    return sha256(text.encode("utf-8")).hexdigest()


def digest(value):
    if not isinstance(value, str) or not HASH_PATTERN.fullmatch(value):
        raise TransferError("Invalid asset SHA-256")
    return value


def compare(value, expected):
    return bool(value and expected and hmac.compare_digest(hashed(value), expected))


def validated_state(body, source_mode):
    session_id = identifier(body.get("sessionId"), "sessionId")
    number = revision(body.get("revision"))
    stage = body.get("stage")
    if stage not in STAGES:
        raise TransferError("Invalid stage")
    claimed_mode = body.get("sourceMode", source_mode)
    if claimed_mode != source_mode:
        raise TransferError("Session source mode cannot be changed", 409)
    schema_version = body.get("schemaVersion", 1)
    if type(schema_version) is not int or schema_version != 1:
        raise TransferError("Unsupported transfer snapshot schema", 409)
    state = {name: body[name] for name in STATE_FIELDS if name in body}
    state.update(schemaVersion=1, sessionId=session_id, revision=number, stage=stage, sourceMode=source_mode)
    for field, limit in {"notes": NOTE_LIMIT, "captures": 240, "points": 64,
                         "runs": 100, "events": 600, "verification": 600}.items():
        if field in state and state[field] is not None:
            values = state[field]
            if not isinstance(values, list) or len(values) > limit or not all(isinstance(item, dict) for item in values):
                raise TransferError("Invalid " + field)
    for note in state.get("notes") or ():
        identifier(note.get("id"), "source note id")
        time = note.get("time", 0)
        if type(time) not in (int, float) or not math.isfinite(time) or time < 0:
            raise TransferError("Invalid source note time")
    for capture in state.get("captures") or ():
        identifier(capture.get("captureId"), "capture id")
        if capture.get("phase") not in PHASES:
            raise TransferError("Invalid capture phase")
        source = capture.get("source")
        if source == "real_camera" and source_mode != "real":
            raise TransferError("Example or replay capture cannot become real camera evidence", 409)
        if source not in {"real_camera", "synthetic", "replay", "manual"}:
            raise TransferError("Invalid capture source")
        if capture.get("sha256"):
            digest(capture["sha256"])
    if "registrationRevision" in state:
        revision(state["registrationRevision"])
    for field, key in (("registration", "boardInstanceId"), ("help", "question")):
        if state.get(field) is not None:
            value = obj(state[field], field)
            name = value.get(key)
            if not isinstance(name, str) or not name.strip():
                state[field] = None
    if state.get("guide") is not None:
        guide = obj(state["guide"], "guide")
        steps = guide.get("steps")
        if not guide.get("id") and not steps and stage in {"registration", "recording"}:
            # JsonUtility can emit an inline default object for an unassigned reference.
            state["guide"] = None
        elif not isinstance(steps, list) or not 1 <= len(steps) <= 40 or not all(isinstance(item, dict) for item in steps):
            raise TransferError("Guide must contain between one and forty steps")
    return state


def validate_command(kind, value, stage):
    payload = obj(value, "Command payload")
    if kind == "capture":
        phases = {
            "registration": {"setup"},
            "recording": {"before_action", "after_action", "clarification", "setup"},
            "practice": {"clarification", "student_checkpoint"},
        }
        if payload.get("phase") not in phases.get(stage, set()):
            raise TransferError("This capture is unavailable for the current session stage", 409)
        return {"phase": payload["phase"]}
    if kind in ("saveDraft", "approveDraft"):
        if stage != "review":
            raise TransferError("The device must open the review before sending draft edits", 409)
        steps = payload.get("steps")
        if not isinstance(steps, list) or not 1 <= len(steps) <= 40 or not all(isinstance(item, dict) for item in steps):
            raise TransferError("Invalid draft steps")
        if len(canonical(steps)) > 180_000:
            raise TransferError("Draft is too large", 413)
        result = {"steps": steps}
        if "kitConfirmed" in payload:
            if type(payload["kitConfirmed"]) is not bool:
                raise TransferError("kitConfirmed must be a boolean")
            result["kitConfirmed"] = payload["kitConfirmed"]
        if "kitConditions" in payload:
            result["kitConditions"] = string(payload["kitConditions"], "kit conditions", 1000, empty=True)
        return result
    if kind == "manualTranscript":
        if stage != "review":
            raise TransferError("Open the review to add a manual transcript", 409)
        result = {"text": string(payload.get("text"), "manual transcript", 6000)}
        if payload.get("sourceNoteId"):
            result["sourceNoteId"] = identifier(payload["sourceNoteId"], "source note id")
        return result
    if kind == "helpResolved":
        if stage != "practice":
            raise TransferError("No practice is active", 409)
        return {"detail": string(payload.get("detail", ""), "detail", 500, empty=True)}
    raise TransferError("Unknown command")


class TransferStore:
    def __init__(self, root):
        self.root = Path(root)
        self.root.mkdir(parents=True, exist_ok=True, mode=0o700)
        self.assets = self.root / "assets"
        self.database = self.root / "transfer.sqlite3"
        with self.db() as db:
            db.executescript("""
                CREATE TABLE IF NOT EXISTS sessions (
                  id TEXT PRIMARY KEY, source_mode TEXT NOT NULL, label TEXT NOT NULL,
                  device_hash TEXT NOT NULL, code_hash TEXT NOT NULL UNIQUE, teacher_hash TEXT,
                  revision INTEGER NOT NULL, stage TEXT NOT NULL, snapshot TEXT NOT NULL,
                  created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS commands (
                  session_id TEXT NOT NULL REFERENCES sessions(id), id TEXT NOT NULL,
                  kind TEXT NOT NULL, expected_revision INTEGER NOT NULL, payload TEXT NOT NULL,
                  status TEXT NOT NULL, detail TEXT NOT NULL, created_at TEXT NOT NULL,
                  resolved_at TEXT, PRIMARY KEY(session_id,id));
                CREATE TABLE IF NOT EXISTS assets (
                  session_id TEXT NOT NULL REFERENCES sessions(id), sha256 TEXT NOT NULL,
                  mime TEXT NOT NULL, size INTEGER NOT NULL, created_at TEXT NOT NULL,
                  PRIMARY KEY(session_id,sha256));
                CREATE TABLE IF NOT EXISTS approvals (
                  session_id TEXT NOT NULL REFERENCES sessions(id), guide_id TEXT NOT NULL,
                  version INTEGER NOT NULL, content_hash TEXT NOT NULL, snapshot TEXT NOT NULL,
                  created_at TEXT NOT NULL, PRIMARY KEY(session_id,guide_id,version));
                CREATE TABLE IF NOT EXISTS observation_jobs (
                  session_id TEXT NOT NULL REFERENCES sessions(id), id TEXT NOT NULL,
                  status TEXT NOT NULL, value TEXT NOT NULL, created_at TEXT NOT NULL,
                  PRIMARY KEY(session_id,id));
            """)
        self.database.chmod(0o600)

    @contextmanager
    def db(self, write=False):
        db = sqlite3.connect(self.database, timeout=5)
        db.row_factory = sqlite3.Row
        try:
            with db:
                if write:
                    db.execute("BEGIN IMMEDIATE")
                yield db
        finally:
            db.close()

    @staticmethod
    def authenticated(db, session_id, token, role):
        session_id = identifier(session_id, "sessionId")
        row = db.execute("SELECT * FROM sessions WHERE id=?", (session_id,)).fetchone()
        if row is None or not compare(token, row[role + "_hash"]):
            raise TransferError("Invalid paired session or credentials", 401)
        return row

    def create(self, body, token):
        session_id = identifier(body.get("sessionId"), "sessionId")
        mode = body.get("sourceMode")
        if mode not in MODES or body.get("consent") is not True:
            raise TransferError("Mode and recording consent are required")
        label = string(body.get("deviceLabel", ""), "deviceLabel", 80, empty=True)
        provided = body.get("deviceToken", token)
        if provided and (not isinstance(provided, str) or not TOKEN_PATTERN.fullmatch(provided)):
            raise TransferError("Invalid device token")
        code = body.get("pairingCode", "")
        if code and (not isinstance(code, str) or not CODE_PATTERN.fullmatch(code)):
            raise TransferError("Invalid pairing code")
        with self.db(write=True) as db:
            existing = db.execute("SELECT * FROM sessions WHERE id=?", (session_id,)).fetchone()
            if existing:
                if not compare(provided, existing["device_hash"]):
                    raise TransferError("Existing session requires its original device token", 401)
                if existing["source_mode"] != mode:
                    raise TransferError("Session source mode cannot be changed", 409)
                if code and not compare(code, existing["code_hash"]):
                    raise TransferError("Existing session requires its original pairing code", 409)
                return dict(sessionId=session_id, deviceToken=provided, pairingCode=code)
            device_token = provided or secrets.token_urlsafe(32)
            code = code or "".join(secrets.choice(PAIRED_ALPHABET) for _ in range(10))
            initial = canonical(dict(schemaVersion=1, sessionId=session_id, revision=0, stage="registration", sourceMode=mode))
            now = utc_now()
            try:
                db.execute("INSERT INTO sessions VALUES (?,?,?,?,?,NULL,?,?,?,?,?)",
                           (session_id, mode, label, hashed(device_token), hashed(code), -1,
                            "registration", initial, now, now))
            except sqlite3.IntegrityError as exc:
                raise TransferError("Pairing code already in use", 409) from exc
            return dict(sessionId=session_id, deviceToken=device_token, pairingCode=code)

    def pair(self, body):
        raw_code = string(body.get("pairingCode"), "pairing code", 24)
        code = re.sub(r"[-\s]", "", raw_code).upper()
        if not CODE_PATTERN.fullmatch(code):
            raise TransferError("Invalid pairing code", 401)
        with self.db(write=True) as db:
            row = db.execute("SELECT id FROM sessions WHERE code_hash=?", (hashed(code),)).fetchone()
            if row is None:
                raise TransferError("Invalid pairing code", 401)
            token = secrets.token_urlsafe(32)
            db.execute("UPDATE sessions SET teacher_hash=? WHERE id=?", (hashed(token), row["id"]))
            return dict(sessionId=row["id"], teacherToken=token)

    def publish(self, body, token):
        with self.db(write=True) as db:
            row = self.authenticated(db, body.get("sessionId"), token, "device")
            snapshot = validated_state(body, row["source_mode"])
            encoded = canonical(snapshot)
            number = snapshot["revision"]
            if number < row["revision"] or (number == row["revision"] and encoded != row["snapshot"]):
                raise TransferError("Session revision is stale or conflicts with saved state", 409)
            if number == row["revision"]:
                return dict(revision=number, stage=snapshot["stage"], duplicate=True)
            self.store_approval(db, snapshot)
            db.execute("UPDATE sessions SET snapshot=?, revision=?, stage=?, updated_at=? WHERE id=?",
                       (encoded, number, snapshot["stage"], utc_now(), row["id"]))
            return dict(revision=number, stage=snapshot["stage"], duplicate=False)

    @staticmethod
    def store_approval(db, snapshot):
        guide = snapshot.get("guide")
        if not guide or guide.get("approved") is not True:
            return
        guide_id = identifier(guide.get("id"), "guide id")
        version = revision(guide.get("version") or 1)
        encoded = canonical(guide)
        fingerprint = hashed(encoded)
        saved = db.execute("SELECT content_hash FROM approvals WHERE session_id=? AND guide_id=? AND version=?",
                           (snapshot["sessionId"], guide_id, version)).fetchone()
        if saved:
            if saved["content_hash"] != fingerprint:
                raise TransferError("Approved guide changed without a new version", 409)
            return
        db.execute("INSERT INTO approvals VALUES (?,?,?,?,?,?)",
                   (snapshot["sessionId"], guide_id, version, fingerprint, encoded, utc_now()))

    def issue_command(self, body, token):
        session_id = body.get("sessionId")
        command_id = identifier(body.get("commandId"), "commandId")
        expected = revision(body.get("expectedRevision"))
        kind = string(body.get("kind"), "kind", 30)
        with self.db(write=True) as db:
            row = self.authenticated(db, session_id, token, "teacher")
            existing = db.execute("SELECT * FROM commands WHERE session_id=? AND id=?",
                                  (session_id, command_id)).fetchone()
            if existing is not None:
                if (existing["expected_revision"] != expected or existing["kind"] != kind or
                        existing["payload"] != canonical(body.get("payload"))):
                    raise TransferError("Command ID already used with a different payload", 409)
                return self.command_json(existing)
            if expected != row["revision"]:
                raise TransferError("Device has a newer revision. Reload before issuing a command.", 409)
            payload = validate_command(kind, body.get("payload"), row["stage"])
            if row["source_mode"] != "real" and kind == "capture":
                raise TransferError("This source cannot produce a real camera snapshot", 409)
            pending = db.execute("SELECT COUNT(*) FROM commands WHERE session_id=? AND status='pending'",
                                 (session_id,)).fetchone()[0]
            if pending >= 8:
                raise TransferError("The device has not yet accepted previous requests", 409)
            if kind in {"capture", "approveDraft", "saveDraft"} and db.execute(
                    "SELECT 1 FROM commands WHERE session_id=? AND status='pending' AND kind=?",
                    (session_id, kind)).fetchone():
                raise TransferError("An equivalent request is already waiting for the device", 409)
            db.execute("INSERT INTO commands VALUES (?,?,?,?,?,'pending','',?,NULL)",
                       (session_id, command_id, kind, expected, canonical(payload), utc_now()))
            inserted = db.execute("SELECT * FROM commands WHERE session_id=? AND id=?",
                                  (session_id, command_id)).fetchone()
            return self.command_json(inserted)

    @staticmethod
    def command_json(row):
        return dict(id=row["id"], kind=row["kind"], expectedRevision=row["expected_revision"],
                    payload=json.loads(row["payload"]), status=row["status"], detail=row["detail"],
                    createdAt=row["created_at"], resolvedAt=row["resolved_at"] or "")

    def poll(self, body, token):
        with self.db() as db:
            row = self.authenticated(db, body.get("sessionId"), token, "device")
            commands = db.execute("SELECT * FROM commands WHERE session_id=? AND status='pending' ORDER BY rowid LIMIT 8",
                                  (row["id"],)).fetchall()
            return {"commands": [self.command_json(command) for command in commands]}

    def acknowledge(self, body, token):
        command_id = identifier(body.get("commandId"), "commandId")
        status = body.get("status")
        if status not in {"applied", "rejected"}:
            raise TransferError("Unexpected command status")
        detail = string(body.get("detail", ""), "status detail", 500, empty=True)
        with self.db(write=True) as db:
            row = self.authenticated(db, body.get("sessionId"), token, "device")
            previous = db.execute("SELECT * FROM commands WHERE session_id=? AND id=?",
                                  (row["id"], command_id)).fetchone()
            if previous is None:
                raise TransferError("Unknown command", 404)
            if previous["status"] != "pending":
                if previous["status"] != status:
                    raise TransferError("Command outcome already recorded", 409)
                return self.command_json(previous)
            db.execute("UPDATE commands SET status=?, detail=?, resolved_at=? WHERE session_id=? AND id=?",
                       (status, detail, utc_now(), row["id"], command_id))
            updated = db.execute("SELECT * FROM commands WHERE session_id=? AND id=?",
                                 (row["id"], command_id)).fetchone()
            return self.command_json(updated)

    def save_asset(self, body, token):
        content_hash = digest(body.get("sha256"))
        mime = body.get("mime")
        if mime not in {"image/jpeg", "audio/wav"}:
            raise TransferError("An image/jpeg or audio/wav asset must be supplied")
        try:
            data = base64.b64decode(body.get("base64", ""), validate=True)
        except (binascii.Error, ValueError, TypeError) as exc:
            raise TransferError("Invalid asset base64") from exc
        if sha256(data).hexdigest() != content_hash:
            raise TransferError("The asset did not match its content hash")
        jpeg = 12 <= len(data) <= MAX_JPEG and data.startswith(b"\xff\xd8\xff") and data.endswith(b"\xff\xd9")
        wave = 44 <= len(data) <= MAX_WAVE and data.startswith(b"RIFF") and data[8:12] == b"WAVE"
        if (mime == "image/jpeg" and not jpeg) or (mime == "audio/wav" and not wave):
            raise TransferError("Asset contents do not match the declared type")
        with self.db(write=True) as db:
            row = self.authenticated(db, body.get("sessionId"), token, "device")
            folder = self.assets / row["id"]
            folder.mkdir(parents=True, exist_ok=True, mode=0o700)
            path = folder / content_hash
            already = db.execute("SELECT 1 FROM assets WHERE session_id=? AND sha256=?",
                                 (row["id"], content_hash)).fetchone()
            if not already or not path.is_file():
                with tempfile.NamedTemporaryFile(dir=folder, prefix=".upload-", delete=False) as file:
                    temporary = Path(file.name)
                    try:
                        file.write(data)
                        file.flush()
                        os.fsync(file.fileno())
                        os.replace(temporary, path)
                    finally:
                        temporary.unlink(missing_ok=True)
                db.execute("INSERT OR IGNORE INTO assets VALUES (?,?,?,?,?)",
                           (row["id"], content_hash, mime, len(data), utc_now()))
            return dict(sha256=content_hash, size=len(data), duplicate=bool(already))

    def fetch_asset(self, session_id, content_hash, token):
        content_hash = digest(content_hash)
        with self.db() as db:
            row = db.execute("SELECT * FROM sessions WHERE id=?", (identifier(session_id, "sessionId"),)).fetchone()
            if row is None or not any(compare(token, row[role + "_hash"]) for role in ("teacher", "device")):
                raise TransferError("Invalid paired session or credentials", 401)
            asset = db.execute("SELECT mime,size FROM assets WHERE session_id=? AND sha256=?", (session_id, content_hash)).fetchone()
        if asset is None:
            raise TransferError("Asset is not available on this PC yet", 404)
        try:
            raw = (self.assets / session_id / content_hash).read_bytes()
        except FileNotFoundError as exc:
            raise TransferError("Asset is not available on this PC yet", 404) from exc
        if len(raw) != asset["size"] or sha256(raw).hexdigest() != content_hash:
            raise TransferError("Saved asset did not match its content hash", 409)
        return asset["mime"], raw

    def report(self, body, token, role):
        with self.db() as db:
            row = self.authenticated(db, body.get("sessionId"), token, role)
            commands = db.execute("SELECT * FROM commands WHERE session_id=? ORDER BY rowid DESC LIMIT 25",
                                  (row["id"],)).fetchall()
            assets = db.execute("SELECT mime,COUNT(*) AS count,SUM(size) AS bytes FROM assets WHERE session_id=? GROUP BY mime",
                                (row["id"],)).fetchall()
            approvals = db.execute("SELECT guide_id AS guideId,version,content_hash AS hash,created_at AS createdAt "
                                   "FROM approvals WHERE session_id=? ORDER BY created_at", (row["id"],)).fetchall()
            jobs = db.execute("SELECT value FROM observation_jobs WHERE session_id=? ORDER BY rowid DESC LIMIT 50",
                              (row["id"],)).fetchall()
            return dict(schemaVersion=1, snapshot=json.loads(row["snapshot"]), deviceLabel=row["label"],
                        commands=[self.command_json(cmd) for cmd in commands],
                        assets=[dict(asset) for asset in assets], approvals=[dict(approved) for approved in approvals],
                        observations=[json.loads(job["value"]) for job in jobs],
                        createdAt=row["created_at"], updatedAt=row["updated_at"])

    def begin_observation(self, body, token):
        capture_id = identifier(body.get("captureId"), "capture ID")
        step_id = identifier(body.get("stepId"), "step ID")
        expected = revision(body.get("registrationRevision"))
        session_id = body.get("sessionId")
        job_id = hashed(f"{capture_id}:{step_id}:{expected}")
        with self.db(write=True) as db:
            row = self.authenticated(db, session_id, token, "device")
            state = json.loads(row["snapshot"])
            captures = state.get("captures") or []
            capture = next((item for item in captures if item["captureId"] == capture_id), None)
            if row["source_mode"] != "real" or not capture or capture["source"] != "real_camera":
                raise TransferError("A saved real camera capture is required", 409)
            if capture["phase"] not in {"after_action", "clarification", "student_checkpoint"}:
                raise TransferError("This capture is a reference and cannot represent current wiring", 409)
            if capture.get("registrationRevision") != expected or state.get("registrationRevision") != expected:
                raise TransferError("The capture predates the current board registration", 409)
            if capture.get("stepId") and capture["stepId"] != step_id:
                raise TransferError("The capture belongs to a different practice step", 409)
            quality = capture.get("quality", "")
            if quality not in {"usable", "accepted", "saved", "visibility_unverified"}:
                raise TransferError("This image needs a clearer view of the board", 409)
            content_hash = digest(capture.get("sha256"))
            expired = db.execute("SELECT id,value FROM observation_jobs WHERE session_id=? AND status='pending' AND created_at<?",
                (session_id, (datetime.now(timezone.utc) - timedelta(seconds=65)).isoformat(timespec="milliseconds"))).fetchall()
            for prior in expired:
                interrupted = json.loads(prior["value"])
                interrupted.update(status="failed", verdict="unknown", detail="사진 비교가 중단되었습니다. 새 사진으로 다시 시도하세요.")
                db.execute("UPDATE observation_jobs SET status='failed', value=? WHERE session_id=? AND id=?",
                           (canonical(interrupted), session_id, prior["id"]))
            existing = db.execute("SELECT * FROM observation_jobs WHERE session_id=? AND id=?",
                                  (session_id, job_id)).fetchone()
            if existing is not None:
                return json.loads(existing["value"]), None
            if db.execute("SELECT 1 FROM observation_jobs WHERE session_id=? AND status='pending'",
                          (session_id,)).fetchone():
                raise TransferError("An observation is already being processed for this session", 409)
            pending = dict(observationId=job_id, captureId=capture_id, stepId=step_id, registrationRevision=expected,
                           source="vision_provider", status="pending", verdict="unknown", elapsedMs=0)
            db.execute("INSERT INTO observation_jobs VALUES (?,?,?,?,?)",
                       (session_id, job_id, "pending", canonical(pending), utc_now()))
        return pending, content_hash

    def finish_observation(self, session_id, job, verdict, status, elapsed_ms):
        with self.db(write=True) as db:
            saved = db.execute("SELECT status,value FROM observation_jobs WHERE session_id=? AND id=?",
                               (session_id, job["observationId"])).fetchone()
            if saved and saved["status"] != "pending":
                return json.loads(saved["value"])
            row = db.execute("SELECT snapshot FROM sessions WHERE id=?", (session_id,)).fetchone()
            state = json.loads(row["snapshot"])
            if state.get("registrationRevision") != job["registrationRevision"]:
                status = "stale"
                verdict = {"verdict": "unknown", "detail": "새로 맞춘 보드에서 사진을 다시 촬영하세요."}
            result = {**job, **verdict, "status": status, "elapsedMs": elapsed_ms}
            db.execute("UPDATE observation_jobs SET status=?, value=? WHERE session_id=? AND id=?",
                       (status, canonical(result), session_id, job["observationId"]))
        return result

    def setup_capture(self, body, token):
        capture_id = identifier(body.get("captureId"), "capture ID")
        expected = revision(body.get("registrationRevision"))
        with self.db() as db:
            row = self.authenticated(db, body.get("sessionId"), token, "device")
            state = json.loads(row["snapshot"])
            capture = next((item for item in (state.get("captures") or []) if item["captureId"] == capture_id), None)
        if not capture or capture["phase"] != "setup":
            raise TransferError("A saved registration photo is required", 409)
        if capture.get("registrationRevision") != expected or state.get("registrationRevision") != expected:
            raise TransferError("The setup photo predates the current registration", 409)
        mime, image = self.fetch_asset(body["sessionId"], capture.get("sha256"), token)
        if mime != "image/jpeg":
            raise TransferError("The saved registration photo must be JPEG", 409)
        return capture, image
