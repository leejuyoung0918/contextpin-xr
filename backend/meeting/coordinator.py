"""Authoritative design state. Each command and its event commit in one SQLite transaction."""
import copy
import hashlib
import hmac
import json
import math
import os
import re
import secrets
import sqlite3
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
from pathlib import Path

from . import bundle_candidates, cable_demo, command_window, geometry, meeting_state, meeting_result, paper_space, shared_workflows
from .contracts import MeetingError, canonical, clock_stamp, digest, now_ms, text_field, uid
from .planner import CANDIDATE_LETTER, NUMBER, ORDINAL, compile_context, contains, explicit_parameters, resolve_reference, validated_plan

RUNNING = {"collecting", "generating", "building"}
OPEN = RUNNING | {"needs_input", "reviewable"}
CODE_TTL = 10 * 60 * 1000
MAX_PARTICIPANTS = 12
MAX_ROUNDS = 200
MAX_BUSY = 12
MEETING_ROOT = Path(__file__).resolve().parents[2] / "artifacts" / "meeting"


def token_hash(token):
    return hashlib.sha256(token.encode()).hexdigest()


class SessionCoordinator:
    def __init__(self, root=None, planner=None, *, exclusive_archive=False):
        self.root = Path(root or os.environ.get("EDGE_DATA_DIR", MEETING_ROOT)).resolve()
        self.exclusive_archive = exclusive_archive
        if exclusive_archive and not (self.root / "sessions.sqlite3").is_file():
            raise MeetingError("저장된 회의 데이터베이스를 찾지 못했습니다.", 404)
        self.root.mkdir(parents=True, exist_ok=True)
        self.models = self.root / "models"
        self.models.mkdir(exist_ok=True)
        self.profile = geometry.load_profile()
        self.paper_move_margin_m = float(os.environ.get("PAPER_MOVE_MARGIN_M", "0.20"))
        if not math.isfinite(self.paper_move_margin_m) or self.paper_move_margin_m < 0:
            raise ValueError("PAPER_MOVE_MARGIN_M must be a finite, nonnegative distance in metres")
        mode = os.environ.get("AI_MODE", "replay")
        if mode not in ("live", "replay"):
            raise ValueError("AI_MODE must be live or replay")
        self.mode = "live" if mode == "live" else "fixture"
        from .gateway import provider_ready
        self.configured = bool(os.environ.get("AI_GATEWAY_URL") or provider_ready("planner"))
        self.speech_available = bool(os.environ.get("AI_GATEWAY_URL") or provider_ready("speech"))
        self.require_ack = os.environ.get("REQUIRE_ALL_PARTICIPANTS_ACK", "").lower() == "true"
        self.planner = planner or validated_plan
        self.lock = threading.RLock()
        self.changed = threading.Condition(self.lock)
        self.audio_streams = {}
        self.command_tails = {}
        self.edit_command_limits = {"min_ms": int(os.environ.get("MIN_EDIT_COMMAND_MS", "400")),
                                    "max_ms": round(float(os.environ.get("MAX_EDIT_COMMAND_SECONDS", "15")) * 1000)}
        if not 0 < self.edit_command_limits["min_ms"] <= self.edit_command_limits["max_ms"] <= 15000:
            raise ValueError("B command limits require 0 < min_ms <= max_ms <= 15000 (STT WAV limit)")
        self.command_pool = ThreadPoolExecutor(max_workers=3, thread_name_prefix="command-window")
        self.event_connections = {}
        self.timers = set()
        self.stopped = False
        self.plan_pool = ThreadPoolExecutor(max_workers=2, thread_name_prefix="planner")
        self.speech_pool = ThreadPoolExecutor(max_workers=1, thread_name_prefix="speech")
        self.pending_audio = threading.BoundedSemaphore(12)
        self.result_pool = ThreadPoolExecutor(max_workers=2, thread_name_prefix="meeting-result")
        self.exporting = set()
        self.export_updates = set()
        restart_results = []
        self.build_pool = ThreadPoolExecutor(
            max_workers=max(1, min(4, int(os.environ.get("CAD_WORKER_CONCURRENCY", "1")))), thread_name_prefix="geometry")
        self.db = sqlite3.connect(self.root / "sessions.sqlite3", check_same_thread=False)
        self.db.row_factory = sqlite3.Row
        if exclusive_archive:
            # SQLite retains this exclusive lock through subsequent commits and WAL
            # checkpoints until close(). This also excludes older server binaries.
            try:
                self.db.execute("PRAGMA busy_timeout=0")
                self.db.execute("PRAGMA locking_mode=EXCLUSIVE")
                self.db.execute("BEGIN EXCLUSIVE")
                self.db.commit()
            except sqlite3.OperationalError as error:
                self.close()
                raise MeetingError("회의 서버를 정상 종료한 뒤 같은 PC에서 실행하세요.", 409) from error
        self.db.executescript("""
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS sessions (
                session_id TEXT PRIMARY KEY, join_code TEXT NOT NULL UNIQUE, state TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS participants (
                token_hash TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES sessions, participant_id TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS design_continuations (
                source_id TEXT NOT NULL REFERENCES sessions, actor_id TEXT NOT NULL, request_id TEXT NOT NULL,
                identity_hash TEXT NOT NULL, child_id TEXT NOT NULL REFERENCES sessions,
                PRIMARY KEY(source_id,actor_id,request_id)
            );
            CREATE TABLE IF NOT EXISTS commands (
                session_id TEXT NOT NULL REFERENCES sessions, command_id TEXT NOT NULL,
                participant_id TEXT NOT NULL, request_hash TEXT NOT NULL, response TEXT NOT NULL,
                PRIMARY KEY (session_id, command_id)
            );
            CREATE TABLE IF NOT EXISTS events (
                session_id TEXT NOT NULL REFERENCES sessions, server_seq INTEGER NOT NULL, body TEXT NOT NULL,
                PRIMARY KEY (session_id, server_seq)
            );
            CREATE TABLE IF NOT EXISTS contexts (
                session_id TEXT NOT NULL REFERENCES sessions, context_id TEXT NOT NULL, body TEXT NOT NULL,
                PRIMARY KEY (session_id, context_id)
            );
        """)
        if exclusive_archive:
            return  # A local follow-up must never perform server-wide crash recovery.
        with self.transaction():
            for row in self.db.execute("SELECT state FROM sessions").fetchall():
                state = json.loads(row["state"])
                interrupted = []
                lost_participants = []
                meeting_state.initialize(state)
                bundle_candidates.interrupted(self, state)
                if state.get("edit_draft"):
                    shared_workflows.clear(self, state, "서버가 재시작되어 편집권을 해제했습니다.")
                if state.get("assembly"):
                    paper_space.interrupt_pending(state, "서버가 재시작되었습니다. 정렬 후 다시 요청하세요.")
                    if state["status"] == "active":
                        for participant in state["participants"]:
                            actor = participant["participant_id"]
                            if paper_space.lose_alignment(state, actor):
                                lost_participants.append(actor)
                    state["meeting"]["capture_requested"] = False
                    for capture in state["meeting_audio"].values():
                        if capture["status"] == "open":
                            capture["status"] = "interrupted"
                        for window in capture.get("design_ranges", {}).values():
                            if window.get("b_status") in ("recording", "transcribing"):
                                window.update(b_status="failed", error="서버 연결이 끊겼습니다. B를 눌러 다시 말해주세요.")
                        for segment in capture["segments"].values():
                            if segment["status"] == "pending":
                                segment["status"] = "interrupted"
                    if (state["session_status"] == "recording" and state["meeting"]["mode"] == "live"
                            and state["meeting"].get("input_mode") != "manual"):
                        state["session_status"] = "paused"
                    if state["session_status"] == "finalizing":
                        restart_results.append(state["session_id"])
                state["capture_state"] = "off"
                state["provider_mode"] = self.mode
                state["cloud_state"] = "online" if self.mode == "live" and self.configured else "offline"
                for item in state["rounds"]:
                    if item["status"] in RUNNING:
                        self.finish_round(item, "failed", "서버 재시작으로 작업이 중단되었습니다. 새로 요청해 주세요.")
                        interrupted.append(item["round_id"])
                if lost_participants:
                    # Reconnecting clients discard snapshots at or before their last sequence.
                    self.emit(state, "paper.alignments_lost", {"paper_frame_revision": state["paper_frame"]["paper_frame_revision"],
                                                              "participant_ids": lost_participants})
                if interrupted:
                    self.emit(state, "jobs.interrupted", {"round_ids": interrupted})
                if not lost_participants and not interrupted:
                    self.save(state)
        for sid in restart_results:
            self.submit(sid, "result", "", 0)

    @contextmanager
    def transaction(self):
        with self.changed:
            self.db.execute("BEGIN IMMEDIATE")
            try:
                yield
                self.db.commit()
            except BaseException:
                self.db.rollback()
                raise
            self.changed.notify_all()

    def close(self):
        with self.lock:
            self.stopped = True
            for ring in self.command_tails.values():
                ring.close()
            for timer in self.timers:
                timer.cancel()
        with self.changed:
            self.changed.notify_all()
        self.command_pool.shutdown(wait=True, cancel_futures=True)
        self.result_pool.shutdown(wait=True, cancel_futures=True)
        self.speech_pool.shutdown(wait=True, cancel_futures=True)
        self.plan_pool.shutdown(wait=True, cancel_futures=True)
        self.build_pool.shutdown(wait=True, cancel_futures=True)
        with self.lock:
            self.db.close()

    def load(self, sid):
        row = self.db.execute("SELECT state FROM sessions WHERE session_id = ?", (sid,)).fetchone()
        if not row:
            raise MeetingError("회의를 찾을 수 없습니다.", 404)
        state = json.loads(row["state"])
        shared_workflows.initialize(state)
        for line in state["utterances"]:
            line.setdefault("meeting_id", state["session_id"])
            line.setdefault("source", "stt" if line.get("source_mode") == "live_capture" else line.get("source_mode"))
        return state

    def save(self, state):
        self.db.execute("UPDATE sessions SET join_code=?,state=? WHERE session_id=?",
                        (state["join_code"], canonical(state), state["session_id"]))

    def emit(self, state, event_type, data, actor=None, command_id=None, occurred=None):
        state["last_seq"] += 1
        received = now_ms()
        event = {"event_id": uid("event"), "session_id": state["session_id"], "server_seq": state["last_seq"],
                 "event_type": event_type, "actor_id": actor, "command_id": command_id, "job_id": data.get("job_id"),
                 "received_at": received, "occurred_session_time_ms": occurred if occurred is not None else received,
                 "resource_id": data.get("round_id") or data.get("version_id") or data.get("utterance_id"),
                 "resource_revision": data.get("revision"), "payload": data}
        self.db.execute("INSERT INTO events VALUES (?,?,?)",
                        (state["session_id"], event["server_seq"], canonical(event)))
        self.save(state)
        return event

    def public(self, state, actor):
        private = {"explicit_design_targets", "design_target_history", "view_history", "audio_segments", "shared_view_history",
                   "meeting_audio", "result_exports", "pending_minutes_export", "paper_frame", "last_paper_candidate_request"}
        public = {k: v for k, v in state.items() if k not in private}
        if state.get("assembly"):
            public.update(meeting_state.snapshot_fields(state))
            public["edit_command_limits"] = self.edit_command_limits
            public["spatial"] = paper_space.snapshot(state, self.paper_move_margin_m)
            if state.get("paper_frame") and "edit_requests" in public:
                public["edit_requests"] = dict(state["edit_requests"])
                for rid, bundle in state["edit_requests"].items():
                    if bundle["participant_id"] != actor:
                        public["edit_requests"][rid] = {**bundle, "selection": paper_space.public_evidence(bundle["selection"]),
                                                       "visual_evidence": paper_space.public_evidence(bundle["visual_evidence"])}
            public["edit_draft"] = shared_workflows.public_draft(state)
            if state.get("session_status") == "completed":
                saved = shared_workflows.saved_design_utterance(state)
                public["saved_design_utterance"] = {k: saved[k] for k in ("session_id", "utterance_id", "revision", "text")} if saved else None
            elif state.get("session_status") == "recording":
                since = max((d["committed_at"] for d in state["decisions"] if d["selected_model_version_id"]), default=0)
                active = shared_workflows.latest_design_utterance(state, since)
                public["active_design_utterance"] = {k: active[k] for k in ("session_id", "utterance_id", "revision", "text")} if active else None
        if actor != state["host_id"] or now_ms() >= state["join_expires_at"]:
            public["join_code"] = None
        return public

    def actor(self, sid, token):
        if not token or len(token) > 128:
            raise MeetingError("참가 인증이 필요합니다.", 401)
        row = self.db.execute("SELECT participant_id FROM participants WHERE session_id=? AND token_hash=?",
                              (sid, token_hash(token))).fetchone()
        if row is None:
            raise MeetingError("이 회의의 참가 토큰이 아닙니다.", 401)
        return row["participant_id"]

    def authenticated(self, sid, token):
        with self.lock:
            actor = self.actor(sid, token)
            return actor, self.load(sid)

    def snapshot(self, sid, token):
        actor, state = self.authenticated(sid, token)
        return self.public(state, actor)

    def wait_events(self, sid, actor, after_seq):
        with self.changed:
            rows = self.db.execute(
                "SELECT body FROM events WHERE session_id=? AND server_seq>? ORDER BY server_seq LIMIT 100",
                (sid, after_seq)).fetchall()
            if not rows:
                self.changed.wait(timeout=1)
                rows = self.db.execute(
                    "SELECT body FROM events WHERE session_id=? AND server_seq>? ORDER BY server_seq LIMIT 100",
                    (sid, after_seq)).fetchall()
            events = [json.loads(row["body"]) for row in rows]
            state = self.load(sid)
            frame = state.get("paper_frame") or {}
            if frame.get("active_grab_id") and not paper_space.drag_active(frame):
                with self.transaction():
                    grab = frame.pop("active_grab_id")
                    event = self.emit(state, "paper.grip_expired", {"grab_id": grab, "paper_frame_revision": frame["paper_frame_revision"]})
                if len(events) < 100 and event["server_seq"] > after_seq:
                    events.append(event)
            return events, self.public(state, actor)

    def code(self):
        while True:
            code = f"{secrets.randbelow(1_000_000):06d}"
            if not self.db.execute("SELECT 1 FROM sessions WHERE join_code=?", (code,)).fetchone():
                return code

    def participant(self, state, identity, role, token=None):
        participant = {"participant_id": uid("person"), "display_name": identity.display_name,
                       "device_id": identity.device_id, "role": role, "capabilities": identity.capabilities}
        state["participants"].append(participant)
        token = token or secrets.token_urlsafe(32)
        self.db.execute("INSERT INTO participants VALUES (?,?,?)",
                        (token_hash(token), state["session_id"], participant["participant_id"]))
        return participant, token

    def version(self, state, version_id):
        for version in state["versions"]:
            if version["version_id"] == version_id:
                return version
        raise MeetingError("모델 버전을 찾을 수 없습니다.", 404)

    def round(self, state, round_id):
        for item in state["rounds"]:
            if item["round_id"] == round_id:
                return item
        raise MeetingError("검토 라운드를 찾을 수 없습니다.", 404)

    @staticmethod
    def candidate(item, candidate_id):
        for candidate in item["candidates"]:
            if candidate["candidate_id"] == candidate_id:
                return candidate
        raise MeetingError("이 라운드의 후보가 아닙니다.", 404)

    @staticmethod
    def decorate(sid, version, parent=None, candidate=None, constraints=None):
        version_id = version["version_id"]
        prefix = f"/v1/sessions/{sid}/versions/{version_id}"
        assets = {name: {**info, "url": f"{prefix}/assets/{name}"} for name, info in version["assets"].items()}
        return {**version, "parent_version_id": parent, "created_from_candidate_id": candidate,
                "parameters": version.get("parameters", version["full_parameters"]), "measurements": version["measured_dimensions_mm"], "assets": assets,
                "preview_url": assets["preview.glb"]["url"], "stl_url": assets["print.stl"]["url"],
                "export_url": prefix + "/export", "request_constraints": constraints or []}

    def create(self, identity):
        try:
            profile = geometry.load_profile(identity.profile_id) if identity.profile_id else self.profile
        except geometry.GeometryError as error:
            raise MeetingError(str(error), 404) from error
        if self.profile.get("assembly") and not profile.get("assembly"):
            raise MeetingError("조립체 데모에서는 등록된 물체 2를 사용합니다.", 409)
        sid = uid("meeting")
        version_id = uid("model")
        params = geometry.default_parameters(profile)
        try:
            initial = self.build_pool.submit(geometry.build_model, self.models, version_id,
                                             profile, params, profile.get("default_constraints", [])).result()
        except geometry.GeometryError as error:
            raise MeetingError(str(error), 422) from error
        version = self.decorate(sid, initial)
        version["design_version"] = "V1"
        with self.transaction():
            state, actor, token = self._insert_session(sid, profile, version, identity)
        return self.connection(state, actor, token)

    def _insert_session(self, sid, profile, version, identity, access_token=None):
        version_id = version["version_id"]
        state = {"session_id": sid, "profile": profile, "join_code": self.code(),
                 "join_expires_at": now_ms() + CODE_TTL, "last_seq": 0, "status": "active",
                 "committed_version_id": version_id, "versions": [version], "participants": [],
                 "rounds": [], "utterances": [], "decisions": [], "images": [], "view_history": {},
                 "audio_segments": [], "constraints": [
                     {"source_kind": "object_profile", "scope": "session", "status": "active",
                      "strength": "hard", "constraint_id": uid("constraint"), **rule}
                     for rule in profile.get("default_constraints", [])
                 ],
                 "constraint_revision": 0, "capture_state": "off", "audio_host_device_id": identity.device_id,
                 "provider_mode": self.mode, "cloud_state": "online" if self.mode == "live" and self.configured else "offline",
                 "raw_audio_saved": os.environ.get("STORE_RAW_AUDIO", "").lower() == "true",
                 "require_all_ack": self.require_ack, "print_results": []}
        if profile.get("assembly"):
            state["assembly"] = profile["assembly"]
            state["original_version_id"] = version_id
            state["assembly_latest_request_at"] = 0
            version["parameter_sources"] = {key: "SOURCE_DEFAULT" for key in version["parameters"]}
            meeting_state.initialize(state)
        self.db.execute("INSERT INTO sessions VALUES (?,?,?)", (sid, state["join_code"], canonical(state)))
        actor, token = self.participant(state, identity, "host", access_token)
        state["host_id"] = actor["participant_id"]
        self.emit(state, "session.created", {"version_id": version_id}, actor["participant_id"])
        return state, actor, token

    def join(self, identity):
        with self.transaction():
            row = self.db.execute("SELECT state FROM sessions WHERE join_code=?", (identity.join_code,)).fetchone()
            if not row:
                raise MeetingError("참가 코드가 올바르지 않거나 만료되었습니다.", 404)
            state = json.loads(row["state"])
            if now_ms() >= state["join_expires_at"] or state["status"] != "active":
                raise MeetingError("참가 코드가 올바르지 않거나 만료되었습니다.", 404)
            if len(state["participants"]) >= MAX_PARTICIPANTS:
                raise MeetingError("회의 참가 인원이 가득 찼습니다.", 409)
            if any(p["device_id"] == identity.device_id for p in state["participants"]):
                raise MeetingError("이미 참가한 기기입니다. 기존 연결을 사용해 주세요.", 409)
            actor, token = self.participant(state, identity, "participant")
            self.emit(state, "participant.joined", {"participant_id": actor["participant_id"]}, actor["participant_id"])
        return self.connection(state, actor, token)

    def connection(self, state, participant, token):
        actor = participant["participant_id"]
        return {"session_id": state["session_id"], "participant_id": actor, "token": token,
                "snapshot": self.public(state, actor)}

    @staticmethod
    def host(state, actor):
        if actor != state["host_id"]:
            raise MeetingError("진행자만 변경할 수 있습니다.", 403)

    @staticmethod
    def finish_round(item, status, reason=None):
        item["status"] = status
        item["revision"] += 1
        item["generation_epoch"] += 1
        if reason:
            item["error"] = reason
        for candidate in item["candidates"]:
            if candidate["status"] in ("queued", "building"):
                candidate["status"] = "cancelled" if status != "failed" else "invalid"
                candidate["error"] = reason

    def supersede(self, state, exclude=None, evidence_id=None):
        for item in state["rounds"]:
            if item is exclude or item["status"] not in OPEN:
                continue
            if evidence_id and evidence_id not in (item.get("context") or {}).get("evidence_ids", []):
                continue
            self.finish_round(item, "superseded", "기반 모델, 유지 조건 또는 근거가 변경되었습니다.")
            self.emit(state, "round.superseded", {"round_id": item["round_id"], "revision": item["revision"]})

    def command(self, sid, token, command):
        if any(key.startswith("_") for key in command.payload):
            raise MeetingError("내부 명령 필드는 사용할 수 없습니다.")
        request_hash = digest({"type": command.type, "payload": command.payload})
        with self.transaction():
            actor = self.actor(sid, token)
            state = self.load(sid)
            old = self.db.execute("SELECT * FROM commands WHERE session_id=? AND command_id=?",
                                  (sid, command.command_id)).fetchone()
            if old:
                if old["participant_id"] != actor or old["request_hash"] != request_hash:
                    raise MeetingError("같은 command_id가 다른 요청에 사용되었습니다.", 409)
                return {**json.loads(old["response"]), "snapshot": self.public(state, actor)}
            if command.type == "transcript_correction" and state.get("meeting", {}).get("transcript_status") in ("finalizing", "finalized"):
                raise MeetingError("종료한 회의의 전사는 확정되어 수정할 수 없습니다.", 409)
            if state["status"] != "active" and not (state.get("assembly") and command.type in {
                    "meeting_end", "meeting_retry", "transcript_correction", "capabilities", "verify", "generate_edit_candidates"}):
                raise MeetingError("종료된 회의입니다.", 409)
            stamp = clock_stamp(command.payload, now_ms())
            event_type, result, job = self.apply(state, actor, command.type, command.payload, stamp)
            occurred = result["timestamp_ms"] if command.type == "design_speech_boundary" else stamp["captured_session_time_ms"]
            self.emit(state, event_type, result, actor, command.command_id, occurred)
            response = {"command_id": command.command_id, **result}
            self.db.execute("INSERT INTO commands VALUES (?,?,?,?,?)",
                            (sid, command.command_id, actor, request_hash, canonical(response)))
        if job:
            self.submit(sid, *job)
        return {**response, "snapshot": self.public(state, actor)}

    def apply(self, state, actor, kind, payload, stamp):
        profile = state["profile"]
        sid = state["session_id"]
        if kind in paper_space.COMMAND_FIELDS:
            return paper_space.apply(self, state, actor, kind, payload)
        if kind in ("design_speech_boundary", "cancel_design_speech"):
            return command_window.remote(self, state, actor, kind, payload, stamp)
        if kind == "generate_edit_candidates":
            request_id = text_field(payload, "request_id", 100)
            bundle = state.get("edit_requests", {}).get(request_id)
            if bundle and actor not in (state["host_id"], bundle["participant_id"]):
                raise MeetingError("진행자 또는 수정 요청을 기록한 참가자가 후보 생성을 시작할 수 있습니다.", 403)
            return bundle_candidates.queue(self, state, request_id)
        shared_workflows.initialize(state)
        shared_workflows.expire(self, state)
        if kind in shared_workflows.COMMANDS:
            return shared_workflows.apply(self, state, actor, kind, payload, stamp)
        if state.get("edit_draft") and kind in {"edit", "decide", "restore", "meeting_readopt", "constraint", "commit_edit_candidate"} and payload.get("_lease_authorized") != state["edit_draft"]["lease_id"]:
            raise MeetingError("현재 치수 조정을 완료하거나 취소한 뒤 변경하세요.", 409)
        if kind == "commit_edit_candidate":
            return bundle_candidates.commit(self, state, actor, payload)
        if kind in meeting_state.LIFECYCLE_COMMANDS or (kind == "end" and state.get("assembly")):
            return meeting_state.apply(self, state, actor, "meeting_end" if kind == "end" else kind, payload, stamp)
        participant = next(p for p in state["participants"] if p["participant_id"] == actor)
        if kind == "view":
            version = self.version(state, payload.get("version_id"))
            parts = payload.get("part_ids", [])
            device = participant["device_id"]
            known = {part["part_id"] for part in profile["semantic_parts"]}
            if not isinstance(parts, list) or len(parts) > 12 or any(type(part) is not str or part not in known for part in parts):
                raise MeetingError("등록된 부위를 선택해 주세요.")
            item = self.round(state, payload["round_id"]) if payload.get("round_id") else None
            candidate_id = payload.get("candidate_id")
            order = payload.get("display_order_candidate_ids", [])
            visible_ids = [c["candidate_id"] for c in item["candidates"]] if item else []
            versions = {c["candidate_id"]: c["version_id"] for c in item["candidates"] if c["status"] == "valid"} if item else {}
            hits = payload.get("hit_point_asset_m")
            if hits is not None and (not isinstance(hits, list) or len(hits) != 3
                                     or any(type(v) not in (float, int) or abs(v) > 20 for v in hits)):
                raise MeetingError("모델 좌표를 확인해 주세요.")
            if item and "display_order_candidate_ids" not in payload:
                order = visible_ids
            if (not isinstance(order, list) or len(order) > 3
                    or any(type(key) is not str or key not in visible_ids for key in order)
                    or len(order) != len(set(order))):
                raise MeetingError("보고 있는 후보 순서를 확인해 주세요.")
            if candidate_id and (type(candidate_id) is not str or versions.get(candidate_id) != version["version_id"]):
                raise MeetingError("후보와 모델 버전이 일치하지 않습니다.")
            view = {**stamp, "evidence_id": uid("view"), "source_mode": "manual", "revision": 1,
                    "participant_id": actor, "device_id": device, "version_id": version["version_id"],
                    "part_ids": list(dict.fromkeys(parts)), "candidate_id": candidate_id, "candidate_versions": versions,
                    "round_id": item["round_id"] if item else None,
                    "display_order_candidate_ids": order, "hit_point_asset_m": hits,
                    "tracking_state": payload.get("tracking_state") if payload.get("tracking_state") in ("tracking", "lost", "limited", "desktop_view", "local_preview") else "unreported"}
            history = state["view_history"].setdefault(actor, [])
            history.append(view)
            del history[:-400]
            if state.get("assembly"):
                meeting_state.note_view(state, view, item, payload.get("share") is True)
            return "view.recorded", view, None
        if kind == "edit":
            version = self.version(state, payload.get("base_version_id") or state["committed_version_id"])
            params = payload.get("parameters")
            if not isinstance(params, dict) or not params or len(params) > 20:
                raise MeetingError("수정할 매개변수를 입력해 주세요.")
            fields = {f["parameter_id"]: f for f in profile["parameters"]}
            ops = []
            for key, value in params.items():
                if key not in fields:
                    raise MeetingError("등록되지 않은 매개변수입니다.")
                field = fields[key]
                if type(value) not in (int, float, str):
                    raise MeetingError("등록된 치수나 선택값을 입력해 주세요.")
                if value != version["parameters"][key]:
                    ops.append({"operation_id": "set_parameter", "part_id": field["affected_part_ids"][0],
                                "parameter_id": key, "value": value, "unit": field["unit"], "source": "EXPLICIT_USER"})
            if not ops:
                raise MeetingError("변경된 치수가 없습니다.")
            inherited = version.get("request_constraints", []) if version["version_id"] != state["committed_version_id"] else []
            constraints = self.effective(state, inherited)
            self.check_patch(profile, version["parameters"], ops, constraints)
            item = self.new_round(state, actor, version, "manual", inherited, stamp["captured_session_time_ms"])
            item["status"] = "building"
            item["candidates"] = [self.new_candidate(item, {
                "title": "수동 치수 수정", "interpretation": "입력한 치수를 적용한 검토 모델입니다.", "operations": ops})]
            return "candidate.planned", {"round_id": item["round_id"]}, ("build", item["round_id"], 1)
        if kind in ("text", "clarify", "replay", "transcript_correction"):
            previous = None
            reuse_round = None
            if kind == "replay":
                if self.mode != "fixture" or state.get("meeting", {}).get("mode") == "live":
                    raise MeetingError("실시간 회의에는 예시 발화를 넣을 수 없습니다.", 409)
                if cable_demo.enabled(state) and payload.get("sample") in {"curves", "cable"}:
                    return cable_demo.prepare_round(self, state, actor, stamp, payload)
                sample = profile.get("demo_utterance")
                if self.mode != "fixture" or payload.get("sample") != "curves" or not sample:
                    raise MeetingError("현재 모드에서는 이 예시를 열 수 없습니다.", 409)
                line = sample
            else:
                line = text_field(payload, "text")
            prior = None
            if kind == "clarify":
                if state.get("meeting", {}).get("design_submit_mode") == "explicit":
                    raise MeetingError("새 수정 발언 구간과 대상 선택을 함께 제출하세요.", 409)
                reuse_round = self.round(state, payload.get("round_id"))
                if reuse_round["status"] != "needs_input" or reuse_round["actor_id"] != actor:
                    raise MeetingError("이미 해결됐거나 다른 참가자의 질문입니다.", 409)
                previous = reuse_round["context"]
            elif kind == "transcript_correction":
                prior = next((u for u in reversed(state["utterances"]) if u["utterance_id"] == payload.get("utterance_id")), None)
                if not prior:
                    raise MeetingError("발화를 찾을 수 없습니다.", 404)
                if actor not in (state["host_id"], prior["participant_id"]):
                    raise MeetingError("자신의 입력 또는 진행자 권한이 필요합니다.", 403)
                if state["status"] == "active":
                    self.supersede(state, evidence_id=prior["utterance_id"])
            else:
                pending = next((r for r in reversed(state["rounds"])
                                if r["actor_id"] == actor and r["status"] == "needs_input"), None)
                if (pending and kind == "text" and stamp["captured_session_time_ms"] - pending["created_at"] <= 30_000
                        and self.clarification_answer(profile, line)):
                    reuse_round = pending
                    previous = pending["context"]
            timestamp = prior["start_ms"] if prior else stamp["captured_session_time_ms"]
            utterance = self.utterance(
                state, line, timestamp, prior["end_ms"] if prior else timestamp, actor, participant["device_id"],
                "replay" if kind == "replay" else "manual", prior=prior)
            utterance.update({k: v for k, v in stamp.items() if k != "captured_session_time_ms"})
            if prior:
                utterance["corrected_by"] = actor
            if prior and state["status"] != "active":
                job = ("result", "", 0) if state["session_status"] == "completed" else None
                return "transcript.corrected", utterance, job
            if state.get("assembly") and kind in {"text", "transcript_correction"}:
                if prior:
                    return "transcript.corrected", utterance, None
                if "speech_range_id" in payload:
                    from . import edit_bundle, speech_ranges
                    rid = text_field(payload, "speech_range_id", 100)
                    window, capture = edit_bundle.window_for(state, rid)
                    if (window.get("b_status") or state["meeting"].get("edit_request_output") != "bundle" or
                            state["session_status"] != "recording" or not window.get("command_audio") or
                            rid in state["edit_requests"] or window["base_version_id"] != state["committed_version_id"] or
                            speech_ranges.snapshot(state)[rid]["status"] not in ("failed", "no_speech")):
                        raise MeetingError("이 수정 요청에는 수동 입력을 연결할 수 없습니다.", 409)
                    window["manual_utterance_id"] = utterance["utterance_id"]
                    utterance["start_ms"] = speech_ranges.sample_time(capture, window["start_sample"])
                    utterance["end_ms"] = speech_ranges.sample_time(capture, window["end_sample"])
                    if speech_ranges.snapshot(state)[rid]["status"] != "final":
                        raise MeetingError("녹음한 오디오에 공백이 있습니다. 다시 녹음하세요.", 409)
                    return "transcript.finalized", utterance, None
                job = shared_workflows.request_from_utterance(self, state, actor, utterance, stamp)
                return "transcript.finalized", utterance, job
            # A correction to adopted evidence is visible in minutes without applying another edit.
            if prior and any(prior["utterance_id"] in d["evidence_refs"] for d in state["decisions"]):
                return "transcript.corrected", utterance, None
            item = self.attach_context(state, utterance, previous, reuse_round)
            return ("transcript.corrected" if prior else "transcript.finalized",
                    {**utterance, "round_id": item["round_id"]},
                    ("plan", item["round_id"], item["generation_epoch"]))
        if kind == "response":
            item = self.round(state, payload.get("round_id"))
            if item["status"] != "reviewable":
                raise MeetingError("검토할 수 있는 라운드가 아닙니다.", 409)
            key, choice = payload.get("kind"), payload.get("choice")
            ids = payload.get("candidate_ids", [])
            if key not in ("interpretation", "preference") or choice not in ("candidate", "combination", "none", "unsure"):
                raise MeetingError("선택 유형을 확인해 주세요.")
            if not isinstance(ids, list) or any(type(c) is not str for c in ids) or len(ids) != len(set(ids)):
                raise MeetingError("후보 선택이 올바르지 않습니다.")
            if not ((choice == "candidate" and len(ids) == 1) or (choice == "combination" and 2 <= len(ids) <= 3)
                    or (choice in ("none", "unsure") and not ids)):
                raise MeetingError("선택 유형에 맞게 후보를 골라 주세요.")
            for candidate_id in ids:
                if self.candidate(item, candidate_id)["status"] != "valid":
                    raise MeetingError("생성에 성공한 후보만 선택할 수 있습니다.")
            record = {"selection_id": uid("vote"), "participant_id": actor, "kind": key, "choice": choice,
                      "candidate_ids": ids, "created_at": now_ms(), "round_id": item["round_id"]}
            item["responses"].append(record)
            return key + ".recorded", record, None
        if kind == "ack":
            item = self.round(state, payload.get("round_id"))
            candidate = self.candidate(item, payload.get("candidate_id"))
            if candidate["status"] != "valid" or item["status"] != "reviewable":
                raise MeetingError("검토 가능한 후보를 확인해 주세요.", 409)
            item.setdefault("acks", {})[actor] = candidate["candidate_id"]
            return "candidate.acknowledged", {"round_id": item["round_id"], "candidate_id": candidate["candidate_id"]}, None
        if kind == "decide":
            if not state.get("assembly"):
                self.host(state, actor)
            return "decision.recorded", self.decide(state, actor, payload), None
        if kind == "cancel":
            item = self.round(state, payload.get("round_id"))
            if actor not in (state["host_id"], item["actor_id"]):
                raise MeetingError("이 작업을 취소할 수 없습니다.", 403)
            if item["status"] not in OPEN:
                raise MeetingError("이미 끝난 라운드입니다.", 409)
            self.finish_round(item, "superseded", "참가자가 취소했습니다.")
            return "round.cancelled", {"round_id": item["round_id"]}, None
        if kind == "constraint":
            self.host(state, actor)
            key, enabled = payload.get("parameter_id"), payload.get("enabled")
            prop = next((f for f in profile.get("measured_properties", []) if f["property_id"] == key), None)
            current = self.version(state, state["committed_version_id"])
            if not prop or type(enabled) is not bool or key not in current["measurements"]:
                raise MeetingError("측정 가능한 유지 조건을 선택해 주세요.")
            active = next((c for c in state["constraints"] if c["property_id"] == key and c["status"] == "active"), None)
            if active and active.get("source_kind") == "object_profile":
                raise MeetingError("프로필 자체의 제약은 이 화면에서 해제할 수 없습니다.", 409)
            if bool(active) != enabled:
                if active:
                    active["status"] = "superseded"
                    active["released_by"] = actor
                    active["released_at"] = now_ms()
                else:
                    state["constraints"].append({
                        "constraint_id": uid("constraint"), "property_id": key, "operator": "eq",
                        "value": current["measurements"][key], "unit": prop["unit"], "tolerance": 0.001,
                        "scope": "session", "status": "active", "strength": "hard", "source_kind": "explicit_ui",
                        "asserted_by": actor, "reference_model_version_id": current["version_id"], "evidence_refs": [],
                    })
                state["constraint_revision"] += 1
                self.supersede(state)
            return "constraint.changed", {"property_id": key, "enabled": enabled}, None
        if kind == "audio_host":
            if state["capture_state"] in {"starting", "recording"}:
                raise MeetingError("녹음이 끝난 뒤 마이크를 바꿔 주세요.", 409)
            state["audio_host_device_id"] = participant["device_id"]
            if state.get("assembly"):
                state["meeting"]["audio_device_id"] = participant["device_id"]
            return "audio.host_changed", {"device_id": participant["device_id"]}, None
        if kind == "capabilities":
            caps = payload.get("capabilities")
            if not isinstance(caps, dict) or len(canonical(caps)) > 4000:
                raise MeetingError("기기 기능 정보를 확인해 주세요.")
            participant["capabilities"] = caps
            return "device.capabilities", {"participant_id": actor, "capabilities": caps}, None
        if kind == "renew_code":
            self.host(state, actor)
            state["join_code"] = self.code()
            state["join_expires_at"] = now_ms() + CODE_TTL
            return "session.code_renewed", {"join_expires_at": state["join_expires_at"]}, None
        if kind == "printed":
            version = self.version(state, payload.get("version_id"))
            if type(payload.get("slicer_scale_confirmed")) is not bool:
                raise MeetingError("슬라이서 배율 확인 여부를 기록해 주세요.")
            report = {"version_id": version["version_id"], "validation_status": "reported_by_participant",
                      "printed_at": now_ms(), "reported_by": actor,
                      "slicer_scale_confirmed": payload["slicer_scale_confirmed"],
                      "fit_observations": text_field(payload, "fit_observations", required=False),
                      "issues": text_field(payload, "issues", required=False)}
            state["print_results"].append(report)
            return "print.reported", report, None
        if kind == "restore":
            self.host(state, actor)
            if payload.get("expected_committed_version_id") != state["committed_version_id"]:
                raise MeetingError("현재 확정 모델이 바뀌었습니다.", 409, committed_version_id=state["committed_version_id"])
            latest = next((d for d in reversed(state["decisions"]) if d["selected_model_version_id"]), None)
            target = self.version(state, payload.get("version_id") or (latest and latest["previous_committed_model_version_id"]))
            head = self.version(state, state["committed_version_id"])
            item = self.new_round(state, actor, head, "manual", [])
            item["restored_from_version_id"] = target["version_id"]
            item["restore_parameters"] = target["parameters"]
            item["status"] = "building"
            item["candidates"] = [self.new_candidate(item, {
                "title": "이전 모델 복원 초안", "interpretation": "이전 모델 치수를 다시 검토합니다.", "operations": []})]
            return "candidate.planned", {"round_id": item["round_id"]}, ("build", item["round_id"], 1)
        if kind == "end":
            self.host(state, actor)
            if state["capture_state"] == "recording":
                raise MeetingError("녹음을 먼저 종료해 주세요.", 409)
            self.supersede(state)
            state["status"] = "ended"
            return "session.ended", {"session_id": sid}, None
        raise MeetingError("지원하지 않는 명령입니다.")

    def decide(self, state, actor, payload):
        draft = state.get("edit_draft")
        if draft and payload.get("_lease_authorized") != draft["lease_id"]:
            raise MeetingError("현재 치수 조정을 완료하거나 취소한 뒤 채택하세요.", 409)
        item = self.round(state, payload.get("round_id"))
        if "expected_adopted_revision" in payload and payload["expected_adopted_revision"] != state.get("adopted_revision"):
            raise MeetingError("현재 채택본이 변경되었습니다.", 409)
        if (type(payload.get("revision")) is not int or payload["revision"] != item["revision"]
                or payload.get("expected_committed_version_id") != state["committed_version_id"]
                or item["expected_committed_version_id"] != state["committed_version_id"]
                or item["constraint_revision"] != state["constraint_revision"] or item["status"] != "reviewable"):
            raise MeetingError("검토 상태가 바뀌었습니다. 현재 모델을 확인해 주세요.", 409,
                               committed_version_id=state["committed_version_id"], round_revision=item["revision"])
        outcome = payload.get("outcome")
        if outcome not in ("commit", "defer", "reject"):
            raise MeetingError("결정 유형을 확인해 주세요.")
        candidate = self.candidate(item, payload.get("candidate_id")) if payload.get("candidate_id") else None
        confirmed = []
        selected = None
        if outcome == "commit":
            if not candidate or candidate["status"] != "valid":
                raise MeetingError("검사를 통과한 후보를 선택해 주세요.")
            selected = self.version(state, candidate["version_id"])
            self.verify_model(selected)
            confirmed = list(dict.fromkeys([actor, *[a for a, c in item.get("acks", {}).items()
                                                     if c == candidate["candidate_id"]]]))
            if self.require_ack and len(confirmed) != len(state["participants"]):
                raise MeetingError("모든 참가자가 이 후보를 확인해야 합니다.", 409)
        reason = text_field(payload, "reason", 600, required=False)
        context = item.get("context") or {}
        reason_evidence = None
        if reason is None and payload.get("reason_utterance_id"):
            reason_evidence = next((u for u in reversed(state["utterances"]) if
                                    u["utterance_id"] == payload["reason_utterance_id"]), None)
            if not reason_evidence:
                raise MeetingError("결정 이유로 연결할 발화를 찾을 수 없습니다.", 404)
            reason = reason_evidence["text"]
        decision = {"decision_id": uid("decision"), "round_id": item["round_id"],
                    "type": {"commit": "adopt", "defer": "defer", "reject": "reject"}[outcome], "outcome": outcome,
                    "selected_model_version_id": selected["version_id"] if selected else None,
                    "previous_committed_model_version_id": state["committed_version_id"],
                    "candidate_id": candidate["candidate_id"] if candidate else None,
                    "candidate_title": candidate["title"] if candidate else None,
                    "rationale_text": reason, "rationale_source": "explicit" if reason else "not_stated",
                    "confirmed_by": confirmed, "committed_at": now_ms(), "actor_id": actor,
                    "consensus_status": ("confirmed" if len(confirmed) > 1 and len(confirmed) == len(state["participants"])
                                         else "partial" if confirmed and len(state["participants"]) > 1 else "not_checked"),
                    "evidence_refs": context.get("evidence_ids", []), "responses": item["responses"],
                    "constraints": self.effective(state, item["request_constraints"]),
                    "parameter_changes": geometry.get_diff(state["profile"], self.version(
                        state, item["edit_base_version_id"])["parameters"], selected["parameters"]) if selected else []}
        if state.get("assembly"):
            evidence_context = context
            if item.get("adjusted_from_round_id") and not context.get("client_request_id"):
                evidence_context = self.round(state, item["adjusted_from_round_id"]).get("context") or context
            evidence_context = evidence_context if evidence_context.get("client_request_id") else context
            utterance = evidence_context.get("utterance")
            decision["evidence_utterances"] = evidence_context.get("selected_utterances") or ([utterance] if utterance else [])
            decision["trigger_utterance_id"] = context.get("trigger_utterance_id")
            decision["reason_evidence"] = reason_evidence
            if reason_evidence:
                decision["rationale_source"] = "transcript"
            decision["proposed_changes"] = candidate.get("operations", []) if candidate else []
            if candidate:
                index = item["candidates"].index(candidate)
                decision["candidate_label"] = "치수 조정안" if item.get("edit_lease_id") else f"후보 {chr(65 + index)}"
            decision["assembly_record"] = {
                "request_id": context.get("request_id", item["round_id"]),
                "base_model_id": item["edit_base_version_id"],
                "source_utterance": utterance["text"] if utterance else None,
                "source_mode": utterance["source_mode"] if utterance else "manual",
                "candidates": [self.assembly_candidate_record(state, option) for option in item["candidates"]],
                "selected_candidate_id": candidate["candidate_id"] if selected else None,
                "selected_model_file": selected["stl_url"] if selected else None,
                "selected_model_sha256": selected["assets"]["print.stl"]["sha256"] if selected else None,
                "preserved_conditions": state["assembly"]["preserved_conditions"],
                "reason": reason, "timestamp": decision["committed_at"],
            }
            if evidence_context.get("client_request_id"):
                provenance = {key: copy.deepcopy(evidence_context[key]) for key in (
                    "client_request_id", "request_id", "reference_selection", "source_utterance_refs",
                    "speech_range_id", "request_transcript", "expected_adopted_revision")}
                provenance["base_version_id"] = evidence_context["edit_base_version_id"]
                decision["evidence_refs"] = evidence_context["evidence_ids"]
                referenced_round = self.round(state, evidence_context["round_id"])
                provenance["proposal_provenance"] = referenced_round.get("proposal_provenance")
                provenance["geometry_provenance"] = selected.get("geometry_provenance") if selected else None
                decision.update(provenance)
                decision["assembly_record"].update(provenance)
        if selected and cable_demo.enabled(state) and item.get("cable_stage"):
            cable_demo.record_choice(self, state, item, candidate, decision,
                                     self.version(state, item["edit_base_version_id"]), selected)
        if selected and item.get("adjusted_from_round_id"):
            original_round = self.round(state, item["adjusted_from_round_id"])
            if original_round.get("cable_stage"):
                decision["cable_stage"] = original_round["cable_stage"]
        state["decisions"].append(decision)
        self.finish_round(item, {"commit": "committed", "defer": "deferred", "reject": "dismissed"}[outcome])
        if selected:
            self.adopt_decision(state, decision, selected, exclude=item)
        return decision

    def adopt_decision(self, state, decision, selected, exclude=None):
        selected["design_version"] = f'V{sum(bool(d.get("selected_model_version_id")) for d in state["decisions"]) + 1}'
        decision["design_version"] = selected["design_version"]
        if state.get("assembly"):
            active = {c["property_id"] for c in state["constraints"] if c["status"] == "active"}
            retained = [dict(c, scope="session", adopted_decision_id=decision["decision_id"]) for c in decision["constraints"]
                        if c["scope"] == "request" and c["property_id"] not in active]
            if retained:
                state["constraints"].extend(retained)
                state["constraint_revision"] += 1
        self.adopt(state, selected["version_id"])
        self.supersede(state, exclude=exclude)

    def verify_model(self, version):
        try:
            for name in version["assets"]:
                meeting_result.read_asset(self.models, version, name)
        except geometry.GeometryError as error:
            raise MeetingError(str(error), 409) from error

    @staticmethod
    def adopt(state, version_id):
        state["committed_version_id"] = version_id
        if state.get("assembly"):
            state["adopted_revision"] += 1
            state["design_revision"] += 1
            state["last_paper_candidate_request"] = None
            if frame := state.get("paper_frame"):
                frame.update(layout_set=None, candidate_poses_paper={}, selected_candidate_id=None,
                             selected_by_participant_id=None, shared_selection=None)
            meeting_state.reset_preview(state, now_ms())

    def assembly_candidate_record(self, state, candidate):
        model = self.version(state, candidate["version_id"]) if candidate["version_id"] else None
        return {"candidate_id": candidate["candidate_id"], "interpretation": candidate["interpretation"],
                "parameters": model["parameters"] if model else None, "print_file": model["stl_url"] if model else None,
                "version_id": candidate["version_id"], "status": candidate["status"]}

    @staticmethod
    def effective(state, inherited):
        locks = {c["property_id"]: c for c in inherited}
        locks.update({c["property_id"]: c for c in state["constraints"] if c["status"] == "active"})
        return list(locks.values())

    @staticmethod
    def check_patch(profile, parameters, operations, constraints):
        try:
            return geometry.validate_patch(profile, parameters,
                                           [{k: v for k, v in op.items() if k != "source"} for op in operations],
                                           constraints)
        except geometry.GeometryError as error:
            raise MeetingError(str(error), 422) from error

    def new_round(self, state, actor, version, mode, constraints, requested_at=None):
        if state.get("assembly") and mode == "manual":
            state["design_revision"] += 1
            state["assembly_latest_request_at"] = max(state.get("assembly_latest_request_at", 0),
                                                     requested_at if requested_at is not None else now_ms())
            for previous in state["rounds"]:
                if previous["status"] in RUNNING | {"needs_input"}:
                    self.finish_round(previous, "superseded", "더 최근의 수정 요청이 들어왔습니다.")
                    self.emit(state, "round.superseded", {"round_id": previous["round_id"],
                                                         "revision": previous["revision"]})
        if len(state["rounds"]) >= MAX_ROUNDS:
            raise MeetingError("이 회의의 검토 한도에 도달했습니다. 새 회의를 시작해 주세요.", 429)
        if sum(item["status"] in RUNNING for item in state["rounds"]) >= MAX_BUSY:
            raise MeetingError("생성 대기열이 가득 찼습니다. 작업이 끝나면 다시 시도해 주세요.", 429)
        item = {"round_id": uid("round"), "candidate_set_id": uid("set"), "actor_id": actor,
                "revision": 1, "generation_epoch": 1, "status": "collecting",
                "candidates": [], "responses": [], "provider_mode": mode,
                "edit_base_version_id": version["version_id"], "expected_committed_version_id": state["committed_version_id"],
                "constraint_revision": state["constraint_revision"], "request_constraints": constraints,
                "created_at": now_ms(), "context": None}
        state["rounds"].append(item)
        return item

    @staticmethod
    def new_candidate(item, option):
        return {**option, "candidate_id": uid("candidate"), "candidate_set_id": item["candidate_set_id"],
                "round_id": item["round_id"], "status": "queued", "version_id": None,
                "job_id": uid("cad"), "context_id": (item.get("context") or {}).get("context_id")}

    @staticmethod
    def utterance(state, line, start, end, actor, device, source, prior=None, **extra):
        uid_value = prior["utterance_id"] if prior else uid("utterance")
        item = {"utterance_id": uid_value, "evidence_id": uid_value, "revision": prior["revision"] + 1 if prior else 1,
                "meeting_id": state["session_id"], "source": "stt" if source == "live_capture" else source,
                "text": line, "participant_id": prior["participant_id"] if prior else actor,
                "device_id": prior["device_id"] if prior else device, "speaker_id": None,
                "start_ms": start, "end_ms": end, "source_mode": source,
                "source_kind": {"live_capture": "audio", "replay": "fixture_transcript", "manual": "manual_input"}[source],
                "session_id": state["session_id"], "transcription_source": source, "is_final": True, "speaker_status": "unknown",
                "status": "final", "received_at": now_ms(), **extra}
        if prior:
            item["prior_revision"] = prior["revision"]
        state["utterances"].append(item)
        return item

    @staticmethod
    def clarification_answer(profile, line):
        return bool(NUMBER.search(line) or explicit_parameters(profile, line) or any(
            contains(line, [p["label"], *p.get("aliases", [])]) for p in profile["semantic_parts"]))

    def attach_context(self, state, utterance, previous=None, reuse=None, audio_base=None,
                       request_view=None, request_base=None, request_actor=None, selected_lines=None,
                       client_request_id=None, speech_range_id=None):
        actor = request_actor or utterance["participant_id"]
        when = utterance["start_ms"]
        views = [view for view in state["view_history"].get(actor, [])
                 if view["captured_session_time_ms"] <= when]
        view = max(views, key=lambda x: x["captured_session_time_ms"], default=None)
        crossed = False
        if state.get("assembly"):
            view, crossed = meeting_state.current_view(state, utterance)
        if previous and not view:
            view = previous["view"]
        if view and view["tracking_state"] in {"lost", "limited"}:
            view = {**view, "part_ids": [], "hit_point_asset_m": None}
        if request_view is not None:
            view, crossed = request_view, False
        candidate, base = resolve_reference(utterance, view)
        if request_base is not None:
            base = request_base
        reference_missing = bool(ORDINAL.search(utterance["text"]) and not base)
        if state.get("assembly"):
            referring = meeting_state.meeting_reference(utterance["text"])
            reference_missing = reference_missing or (referring and (crossed or not view))
            if CANDIDATE_LETTER.search(utterance["text"]) and not candidate:
                reference_missing = True
        if not base:
            base = previous["edit_base_version_id"] if previous else audio_base or state["committed_version_id"]
        version = self.version(state, base)
        # An ongoing correction replaces an unreviewed plan and leaves a durable transition.
        is_correction = bool(re.search(r"^\s*(아니|정정|아까 말)", utterance["text"]))
        if is_correction and not reuse and selected_lines is None:
            current = next((r for r in reversed(state["rounds"]) if r["actor_id"] == actor and r["status"] in OPEN
                            and r.get("context") and r["context"]["utterance"]["device_id"] == utterance["device_id"]
                            and (actor is not None or r["context"]["utterance"].get("audio_stream_id") == utterance.get("audio_stream_id"))
                            and 0 <= when - r["context"]["utterance"]["end_ms"] <= 4500), None)
            if current:
                previous = current["context"]
                self.finish_round(current, "superseded", "뒤이어 입력한 정정으로 교체되었습니다.")
                self.emit(state, "round.superseded", {"round_id": current["round_id"], "revision": current["revision"]})
        if reuse:
            item = reuse
            if state.get("assembly"):
                state["assembly_latest_request_at"] = max(state["assembly_latest_request_at"], utterance["end_ms"])
            item.update(status="collecting", revision=item["revision"] + 1, generation_epoch=item["generation_epoch"] + 1,
                        question=None, edit_base_version_id=version["version_id"])
        else:
            item = self.new_round(state, actor, version, self.mode, [], utterance["end_ms"])
        context = compile_context(state, utterance, view, version, item["round_id"], item["generation_epoch"], previous, selected_lines)
        context["request_user_id"] = actor
        if selected_lines is not None:
            context.update(client_request_id=client_request_id, speech_range_id=speech_range_id,
                           request_actor_id=actor, requested_at=now_ms(), expected_adopted_revision=state["adopted_revision"])
        if selected_lines is None and request_view is not None and request_view.get("image_id"):
            image = next((i for i in state["images"] if i["evidence_id"] == request_view["image_id"] and i["version_id"] == version["version_id"]), None)
            if image:
                import base64
                context["images_used"] = True
                context["evidence_ids"] = list(dict.fromkeys([*context["evidence_ids"], image["evidence_id"]]))
                context["screenshot_data_url"] = "data:" + image["mime"] + ";base64," + base64.b64encode((self.root / "media" / image["evidence_id"]).read_bytes()).decode()
        context["referenced_candidate_id"] = candidate
        if state.get("assembly"):
            context["context_version"] = state["design_revision"]
            context["reference_crossed"] = crossed
        if state.get("assembly") and version.get("parent_version_id"):
            parent = self.version(state, version["parent_version_id"])
            context["changed_base_parameters"] = [key for key, value in version["parameters"].items()
                                                   if value != parent["parameters"][key]]
        context["reference_missing"] = reference_missing
        context["context_hash"] = digest({"semantic": context["context_hash"], "candidate": candidate,
                                         "missing": context["reference_missing"],
                                         "client_request_id": client_request_id, "speech_range_id": speech_range_id})
        context["context_ready_at"] = now_ms()
        item["planner_job_id"] = uid("plan")
        item["context"] = context
        item["request_constraints"] = context["request_constraints"]
        self.db.execute("INSERT INTO contexts VALUES (?,?,?)",
                        (state["session_id"], context["context_id"], canonical(context)))
        return item

    def submit(self, sid, kind, rid, epoch):
        with self.lock:
            if self.stopped:
                return
            if kind == "result":
                if sid in self.exporting:
                    self.export_updates.add(sid)
                else:
                    self.exporting.add(sid)
                    self.result_pool.submit(self.run_result, sid)
            elif kind == "bundle_plan":
                self.plan_pool.submit(bundle_candidates.interpret, self, sid, rid)
            elif kind == "command_window":
                self.command_pool.submit(command_window.resolve, self, sid, rid, epoch, self.command_tails.get((sid, epoch)))
            elif kind == "plan":
                delay = max(0, min(3000, int(os.environ.get("FINAL_CONTEXT_DEBOUNCE_MS", "600")))) / 1000
                def launch():
                    with self.lock:
                        self.timers.discard(timer)
                        if not self.stopped:
                            self.plan_pool.submit(self.run_plan, sid, rid, epoch)
                timer = threading.Timer(delay, launch)
                self.timers.add(timer)
                timer.daemon = True
                timer.start()
            else:
                self.build_pool.submit(self.run_build, sid, rid, epoch)

    @staticmethod
    def current(state, item, epoch):
        return (state["status"] == "active" and item["generation_epoch"] == epoch and item["status"] in RUNNING
                and item["constraint_revision"] == state["constraint_revision"]
                and item["expected_committed_version_id"] == state["committed_version_id"])

    def run_plan(self, sid, rid, epoch):
        with self.transaction():
            state = self.load(sid)
            item = self.round(state, rid)
            if not self.current(state, item, epoch) or self.stopped:
                return
            item["status"] = "generating"
            item["planner_started_at"] = now_ms()
            context = item["context"]
            mode = item["provider_mode"]
            self.emit(state, "planner.started", {"round_id": rid, "request_id": context["request_id"],
                                                 "job_id": item["planner_job_id"]})
        try:
            if (context.get("reference_selection") and mode == "live"
                    and os.getenv("MEETING_PLAN_BACKEND") != "registered"):
                import base64
                image = context["screenshot"]
                raw = (self.root / "media" / image["evidence_id"]).read_bytes()
                if hashlib.sha256(raw).hexdigest() != image["sha256"]:
                    raise MeetingError("선택 사진의 저장 상태가 변경되었습니다.", 409)
                context = {**context, "screenshot_data_url": "data:" + image["mime"] + ";base64," + base64.b64encode(raw).decode()}
            plan = self.planner(context, mode)
            failure = None
        except Exception as error:
            plan = None
            failure = str(error) if isinstance(error, MeetingError) else "후보 계획을 받지 못했습니다."
        build = False
        with self.transaction():
            state = self.load(sid)
            item = self.round(state, rid)
            if not self.current(state, item, epoch) or self.stopped:
                return
            if plan:
                item["proposal_provenance"] = plan.get("proposal_provenance", {"source": mode})
            item["planner_finished_at"] = now_ms()
            item["planner_latency_ms"] = item["planner_finished_at"] - item["planner_started_at"]
            state["cloud_state"] = ("degraded" if failure else "online") if mode == "live" else "offline"
            if failure:
                self.finish_round(item, "failed", failure)
                event = "planner.failed"
            elif plan["action"] == "ask_clarification":
                item["status"] = "needs_input"
                item["question"] = plan["question"]
                event = "grounding.needs_input"
            elif not plan["options"]:
                item["status"] = "answered"
                item["message"] = plan["question"] or {
                    "resolve_decision_request": "검토한 후보를 열어 진행자가 직접 결정할 수 있습니다.",
                    "unsupported": "현재 모델에서 지원하지 않는 변경입니다.",
                }.get(plan["action"], "발언을 기록했습니다.")
                event = "planner.recorded"
            else:
                if state.get("assembly"):
                    stamp = context.get("requested_at", context["utterance"]["end_ms"])
                    if stamp < state["assembly_latest_request_at"]:
                        self.finish_round(item, "superseded", "더 최근의 수정 요청이 있습니다.")
                        self.emit(state, "round.superseded", {"round_id": rid})
                        return
                    state["assembly_latest_request_at"] = stamp
                    state["design_revision"] += 1
                    state["discussion"] = {"text": (plan.get("summary_patch") or {}).get("discussion")
                                           or context["utterance"]["text"][:100],
                                           "utterance_id": context["utterance"]["utterance_id"]}
                    for previous in state["rounds"]:
                        if previous is not item and previous["status"] in {"building", "needs_input"} and previous["created_at"] <= item["created_at"]:
                            self.finish_round(previous, "superseded", "더 최근에 관련 변경 요청이 들어왔습니다.")
                item["status"] = "building"
                item["candidates"] = [self.new_candidate(item, {**option, "proposal_provenance": item["proposal_provenance"]}) for option in plan["options"]]
                event = "candidate.planned"
                build = True
            self.emit(state, event, {"round_id": rid, "request_id": context["request_id"],
                                    "job_id": item["planner_job_id"]})
        if build:
            self.submit(sid, "build", rid, epoch)

    def build_candidate(self, sid, version_id, profile, base, option, constraints, seen_parameters,
                        seen_surfaces, cancelled, restore_parameters=None):
        """Apply a validated patch to this exact base and register the real geometry."""
        params = restore_parameters if restore_parameters is not None else self.check_patch(
            profile, base["parameters"], option["operations"], constraints)
        key = digest(params)
        if key in seen_parameters or params == base["parameters"]:
            raise MeetingError("이미 제시한 변경과 동일합니다.")
        model = geometry.build_model(self.models, version_id, profile, params, constraints, cancelled=cancelled)
        shape = model.get("surface_sha256", model["geometry_hash"])
        if profile.get("assembly") and shape in seen_surfaces:
            import shutil
            shutil.rmtree(self.models / version_id)
            raise MeetingError("이 후보의 실제 형상이 이미 표시한 모델과 같습니다.")
        seen_parameters.add(key)
        seen_surfaces.add(shape)
        result = self.decorate(sid, model, base["version_id"], option["candidate_id"], constraints)
        if profile.get("assembly"):
            result["parameter_sources"] = {**base.get("parameter_sources", {}),
                **{op["parameter_id"]: op["source"] for op in option["operations"]}}
        return result

    def run_build(self, sid, rid, epoch):
        def cancelled():
            with self.lock:
                if self.stopped:
                    return True
                current_state = self.load(sid)
                return not self.current(current_state, self.round(current_state, rid), epoch)

        with self.lock:
            state = self.load(sid)
            item = self.round(state, rid)
            if not self.current(state, item, epoch):
                return
            profile = state["profile"]
            version = self.version(state, item["edit_base_version_id"])
            constraints = self.effective(state, item["request_constraints"])
            options = item["candidates"]
            restore = item.get("restore_parameters")
        used = set()
        used_geometry = {version.get("surface_sha256", version["geometry_hash"])}
        for option in options:
            with self.transaction():
                state = self.load(sid)
                item = self.round(state, rid)
                if not self.current(state, item, epoch) or self.stopped:
                    return
                candidate = self.candidate(item, option["candidate_id"])
                candidate["status"] = "building"
                candidate["build_started_at"] = now_ms()
                self.emit(state, "candidate.building", {"round_id": rid, "candidate_id": candidate["candidate_id"],
                                                         "job_id": candidate["job_id"]})
            version_id = uid("model")
            result = None
            error = None
            try:
                result = self.build_candidate(sid, version_id, profile, version, option, constraints,
                                              used, used_geometry, cancelled, restore)
            except Exception as exc:
                error = str(exc) if isinstance(exc, (MeetingError, geometry.GeometryError)) else "모델 생성에 실패했습니다."
            with self.transaction():
                state = self.load(sid)
                item = self.round(state, rid)
                if not self.current(state, item, epoch) or self.stopped:
                    if result is not None:
                        import shutil
                        shutil.rmtree(self.models / version_id, ignore_errors=True)
                    return
                candidate = self.candidate(item, option["candidate_id"])
                candidate["build_finished_at"] = now_ms()
                candidate["cad_build_ms"] = candidate["build_finished_at"] - candidate["build_started_at"]
                if error:
                    candidate.update(status="invalid", error=error)
                else:
                    result["request_constraints"] = item["request_constraints"]
                    if restore is not None:
                        result["restored_from_version_id"] = item["restored_from_version_id"]
                    result["constraint_revision"] = item["constraint_revision"]
                    state["versions"].append(result)
                    candidate.update(status="valid", version_id=version_id,
                                     parameter_changes=geometry.get_diff(profile, version["parameters"], result["parameters"]))
                self.emit(state, "candidate.invalid" if error else "candidate.valid",
                          {"round_id": rid, "candidate_id": candidate["candidate_id"], "version_id": candidate["version_id"],
                           "job_id": candidate["job_id"]})
        with self.transaction():
            state = self.load(sid)
            item = self.round(state, rid)
            if not self.current(state, item, epoch) or self.stopped:
                return
            item["status"] = "reviewable" if any(c["status"] == "valid" for c in item["candidates"]) else "failed"
            item["revision"] += 1
            self.emit(state, "round." + item["status"], {"round_id": rid, "revision": item["revision"]})

    def record_image(self, sid, token, raw, mime, version_id, part_id, stamp, source_mode="manual",
                     client_request_id=None, selection_id=None, captured_at_ms=None):
        with self.transaction():
            actor = self.actor(sid, token)
            state = self.load(sid)
            capture = {}
            if any(v is not None for v in (client_request_id, selection_id, captured_at_ms)):
                from .explicit_design import capture_identity
                capture = capture_identity(state, actor, client_request_id, selection_id, captured_at_ms, source_mode)
                prior = next((i for i in state["images"] if i.get("selection_id") == selection_id), None)
                if prior:
                    if (any(prior.get(k) != v for k, v in capture.items()) or prior["participant_id"] != actor or
                            prior["version_id"] != version_id or prior["part_ids"] != [part_id] or
                            prior["source_mode"] != source_mode or prior["mime"] != mime or
                            prior["sha256"] != hashlib.sha256(raw).hexdigest()):
                        raise MeetingError("같은 선택 ID에 다른 사진을 등록할 수 없습니다.", 409)
                    return prior
                if state["meeting"].get("edit_request_output") == "bundle":
                    from . import edit_bundle
                    if version_id != state["committed_version_id"]:
                        raise MeetingError("현재 Latest에서 사진을 다시 지정하세요.", 409)
                    edit_bundle.check_window(state, actor, client_request_id, version_id, state["adopted_revision"])
            if state["status"] != "active" or len(state["images"]) >= 100:
                raise MeetingError("지금은 사진을 추가할 수 없습니다.", 409)
            version = self.version(state, version_id)
            if part_id and part_id not in {p["part_id"] for p in state["profile"]["semantic_parts"]}:
                raise MeetingError("등록된 부위가 아닙니다.")
            eid = uid("image")
            directory = self.root / "media"
            directory.mkdir(exist_ok=True)
            path = directory / eid
            path.write_bytes(raw)
            device = next(p["device_id"] for p in state["participants"] if p["participant_id"] == actor)
            evidence = {**stamp, **capture, "evidence_id": eid, "version_id": version["version_id"], "participant_id": actor,
                        "device_id": device, "part_ids": [part_id] if part_id else [], "source_mode": source_mode,
                        "revision": 1, "sha256": hashlib.sha256(raw).hexdigest(),
                        "url": f"/v1/sessions/{sid}/media/{eid}", "mime": mime}
            state["images"].append(evidence)
            self.emit(state, "image.added", evidence, actor)
            return evidence

    def audio_start(self, sid, token, stream_id, device, *, continuous=False, sample_rate=None,
                    marker_overlap=False, clock_origin_ms=None, command_tail=None):
        with self.transaction():
            actor = self.actor(sid, token)
            state = self.load(sid)
            actual_device = next(p["device_id"] for p in state["participants"] if p["participant_id"] == actor)
            if state["status"] != "active" or sid in self.audio_streams:
                raise MeetingError("이미 녹음 중이거나 회의가 종료되었습니다.", 409)
            if state["audio_host_device_id"] != actual_device or (device and device != actual_device):
                raise MeetingError("지정된 마이크 기기에서 시작해 주세요.", 403)
            if self.mode != "live":
                raise MeetingError("재생 모드에서는 직접 입력 또는 예시 발화를 사용하세요.", 409)
            if not self.speech_available:
                raise MeetingError("전사 공급자가 설정되어 있지 않습니다. 발화를 직접 입력해 주세요.", 503)
            tracked = bool(state.get("assembly") and continuous)
            if tracked and (state["meeting"]["mode"] != "live" or not state["meeting"]["capture_requested"]):
                raise MeetingError("먼저 회의의 마이크를 시작해 주세요.", 409)
            if state.get("assembly") and state["meeting"]["mode"] and not tracked:
                raise MeetingError("진행 중인 회의에서는 지정한 연속 마이크를 사용하세요.", 409)
            if tracked and stream_id in state["meeting_audio"]:
                raise MeetingError("이미 사용한 오디오 스트림입니다.", 409)
            state["capture_state"] = "starting" if tracked else "recording"
            self.audio_streams[sid] = stream_id
            if tracked:
                if marker_overlap and clock_origin_ms is not None:
                    # A resumed microphone resets its sample counter. Clock sync
                    # jitter must not move a new utterance into the previous stream.
                    from .speech_ranges import sample_time
                    for info in state["meeting_audio"].values():
                        through = sample_time(info, info["last_sample"])
                        if through is not None:
                            clock_origin_ms = max(clock_origin_ms, through)
                state["meeting_audio"][stream_id] = {"status": "open", "last_sample": 0, "segments": {}, "gaps": [],
                                                     "started_at": None, "sample_rate": sample_rate,
                                                     "marker_overlap": marker_overlap, "clock_origin_ms": clock_origin_ms,
                                                     "b_hold": command_tail is not None}
                if command_tail is not None:
                    self.command_tails[sid, stream_id] = command_tail
            self.emit(state, "capture.state_changed", {"capture_state": state["capture_state"], "stream_id": stream_id}, actor)
            return actor, actual_device

    def audio_opened(self, sid, stream_id, started_at):
        with self.transaction():
            state = self.load(sid)
            capture = meeting_state.state_of_capture(state, stream_id)
            if capture is None or state["session_status"] in meeting_state.TERMINAL:
                return started_at
            if capture.get("marker_overlap"):
                started_at = capture["clock_origin_ms"]
            capture["started_at"] = started_at
            state.update(capture_state="recording", session_status="recording")
            state["meeting"]["started_at"] = state["meeting"]["started_at"] or started_at
            self.emit(state, "meeting.started", {"session_status": "recording", "stream_id": stream_id})
            return started_at

    def audio_progress(self, sid, stream_id, sample):
        with self.transaction():
            state = self.load(sid)
            capture = meeting_state.state_of_capture(state, stream_id)
            if capture is not None:
                capture["last_sample"] = sample
                for rid, w in capture.get("design_ranges", {}).items():
                    if (w.get("latest_pose_paper") and w.get("b_status") == "recording" and
                            (sample - w["start_sample"]) * 1000 > (self.edit_command_limits["max_ms"] + 6000) * capture["sample_rate"]):
                        w.update(b_status="too_long", error="버튼 종료 신호가 없습니다. 짧게 다시 말해주세요.")
                        self.emit(state, "speech.range_changed", {"range_id": rid, "status": "too_long"}, w["participant_id"])
                self.save(state)

    def reject_hold_marker(self, sid, stream_id, actor, rid, sample, base_id, reason):
        with self.transaction():
            state = self.load(sid)
            command_window.reject(state, stream_id, actor, rid, sample, base_id, reason)
            self.emit(state, "speech.range_changed", {"range_id": rid, "error": reason}, actor)

    def audio_boundary(self, sid, stream_id, actor, range_id, start, sample, segments_finished, command_wav=None, base_id=None, observed=None):
        from . import speech_ranges
        with self.transaction():
            state = self.load(sid)
            capture = meeting_state.state_of_capture(state, stream_id)
            bundle = state["meeting"].get("edit_request_output") == "bundle"
            if bundle and capture and capture.get("b_hold"):
                try:
                    kind, payload, job = command_window.mark(self, state, stream_id, actor, range_id, start, sample, base_id, observed)
                except MeetingError as error:
                    # A rejected button marker must never disconnect the room microphone.
                    command_window.reject(state, stream_id, actor, range_id, sample, base_id, str(error))
                    self.emit(state, "speech.range_changed", {"range_id": range_id, "error": str(error)}, actor)
                    return bundle
                capture["last_sample"] = sample
                self.emit(state, kind, payload, actor, occurred=payload["timestamp_ms"])
                if job:
                    self.submit(sid, *job)
                return bundle
            if bundle and start and base_id is not None and base_id != state["committed_version_id"]:
                raise MeetingError("채택본이 바뀌었습니다. 현재 Latest에서 수정을 다시 시작하세요.", 409)
            speech_ranges.boundary(state, capture, stream_id, actor, range_id, start, sample, segments_finished)
            if bundle and not start and command_wav:
                asset = uid("command")
                directory = self.root / "media"
                directory.mkdir(exist_ok=True)
                (directory / asset).write_bytes(command_wav)
                capture["design_ranges"][range_id]["command_audio"] = asset
            window = capture["design_ranges"][range_id]
            timestamp = speech_ranges.sample_time(capture, sample)
            self.emit(state, "speech.range_changed", {"range_id": range_id, "stream_id": stream_id,
                      "marker": "edit_command_start" if start else "edit_command_end",
                      "edge": "start" if start else "end", "sample_index": sample, "timestamp_ms": timestamp,
                      "base_version_id": window.get("base_version_id")}, actor, occurred=timestamp)
            return bundle

    def audio_cutoff(self, sid, stream_id):
        with self.lock:
            state = self.load(sid)
            return state["meeting"]["ended_at"] if meeting_state.state_of_capture(state, stream_id) else None

    def audio_stop(self, sid, stream_id, eos=False, sample=0, expected_sequence=None, actual_sequence=None,
                   pending_request=None, pending_selection=None):
        with self.transaction():
            if self.audio_streams.get(sid) != stream_id:
                return
            self.audio_streams.pop(sid)
            self.command_tails.pop((sid, stream_id), None)
            state = self.load(sid)
            state["capture_state"] = "off"
            capture = meeting_state.state_of_capture(state, stream_id)
            if capture is not None:
                capture.update(status="eos" if eos else "interrupted", last_sample=sample)
                for window in capture.get("design_ranges", {}).values():
                    if window.get("b_status") == "recording":
                        window.update(b_status="failed", error="마이크 연결이 끝났습니다. B를 눌러 다시 말해주세요.")
                if (eos and isinstance(pending_request, str) and isinstance(pending_selection, str)
                        and re.fullmatch(r"[A-Za-z0-9_-]{1,100}", pending_selection)):
                    window = capture.get("design_ranges", {}).get(pending_request)
                    if window and window.get("base_version_id") and window["end_sample"] is not None:
                        window["pending_selection_id"] = pending_selection
                if expected_sequence is not None and expected_sequence != actual_sequence:
                    capture["gaps"].append({"reason": "eos_sequence_mismatch", "expected_sequence": expected_sequence,
                                            "last_received_sequence": actual_sequence})
                if state["session_status"] == "recording":
                    state["session_status"] = "paused"
                state["meeting"]["capture_requested"] = False
            self.emit(state, "capture.state_changed", {"capture_state": "off", "stream_id": stream_id, "eos": eos})
        if capture is not None and state["session_status"] == "completed":
            self.submit(sid, "result", "", 0)

    def audio_gap(self, sid, stream_id, sample, code, first_missing=None):
        with self.transaction():
            state = self.load(sid)
            gap = {"sample_index": sample, "first_missing_sample": first_missing, "reason": code}
            capture = meeting_state.state_of_capture(state, stream_id)
            if capture is not None:
                capture["gaps"].append(gap)
            self.emit(state, "audio.gap", {"stream_id": stream_id, **gap})

    def audio_submit(self, sid, stream_id, sequence, start, end, wav, actor, device, view_actor=None, stamp=None):
        # Apply backpressure to the PCM reader rather than dropping a spoken segment
        # when CAD work or another meeting temporarily slows down the shared STT worker.
        while not self.pending_audio.acquire(timeout=.5):
            if self.stopped:
                return
        future = None
        try:
            with self.lock:
                if self.stopped:
                    return
                with self.transaction():
                    state = self.load(sid)
                    capture = meeting_state.state_of_capture(state, stream_id)
                    if capture is not None:
                        if str(sequence) in capture["segments"]:
                            return
                        job = {"source_sequence": sequence, "start_ms": start, "end_ms": end,
                               "status": "pending" if wav is not None else "no_speech"}
                        if stamp:
                            job.update({k: stamp[k] for k in ("voice_start_sample", "voice_end_sample") if k in stamp})
                        capture["segments"][str(sequence)] = job
                        self.save(state)
                if wav is None:
                    return
                future = self.speech_pool.submit(self.transcribe_audio, sid, stream_id, sequence, start, end,
                                                 wav, actor, device, view_actor, stamp)
        finally:
            if future is None:
                self.pending_audio.release()

        def finished(task):
            self.pending_audio.release()
            if task.cancelled() or task.exception() is not None:
                with self.transaction():
                    state = self.load(sid)
                    capture = meeting_state.state_of_capture(state, stream_id)
                    if capture and capture["segments"][str(sequence)]["status"] == "pending":
                        self.finish_segment(state, stream_id, sequence, "failed")
                        self.emit(state, "speech.failed", {"stream_id": stream_id, "source_sequence": sequence,
                                  "message": "전사 결과를 저장하지 못했습니다."}, actor)

        future.add_done_callback(finished)

    def transcribe_audio(self, sid, stream_id, sequence, start, end, wav, actor, device, view_actor, stamp):
        import base64
        from .planner import gateway_request
        record = {"stream_id": stream_id, "source_sequence": sequence, "start_ms": start, "end_ms": end}
        segment_key = f"{stream_id}.{sequence}"
        with self.transaction():
            state = self.load(sid)
            if segment_key in state["audio_segments"]:
                return
            state["audio_segments"].append(segment_key)
            self.emit(state, "speech.transcribing", record, actor)
        try:
            payload = {"wav_base64": base64.b64encode(wav).decode()}
            if state["profile"].get("speech_vocabulary"):
                payload["hotwords"] = state["profile"]["speech_vocabulary"]
            result = gateway_request("/v1/transcriptions", payload)
            line = result["transcript"].strip()[:1200] if result.get("status", "final") == "final" else ""
        except Exception as error:
            with self.transaction():
                state = self.load(sid)
                state["speech_error"] = str(error) if isinstance(error, MeetingError) else "전사에 실패했습니다. 다시 녹음하거나 발화를 직접 입력하세요."
                state["cloud_state"] = "degraded"
                self.finish_segment(state, stream_id, sequence, "failed")
                self.emit(state, "speech.failed", {**record, "message": state["speech_error"]}, actor)
            if state.get("session_status") == "completed":
                self.submit(sid, "result", "", 0)
            return
        if not line:
            with self.transaction():
                state = self.load(sid)
                outcome = "partial_discarded" if result.get("status") == "partial" else "no_speech"
                self.finish_segment(state, stream_id, sequence, outcome)
                self.emit(state, "speech." + outcome, record, actor)
            if state.get("session_status") == "completed":
                self.submit(sid, "result", "", 0)
            return
        job = None
        with self.transaction():
            state = self.load(sid)
            self.finish_segment(state, stream_id, sequence, "final")
            state["speech_error"] = None
            utterance = self.utterance(state, line, start, end, view_actor, device, "live_capture",
                                       **(stamp or {}), audio_stream_id=stream_id, source_sequence=sequence,
                                       transcription_provider=result.get("provider", "configured"),
                                       captured_by_participant_id=actor, timing_quality="segment_bounds",
                                       stt_final_received=now_ms(), speech_to_transcript_ms=max(0, now_ms() - end))
            capture = meeting_state.state_of_capture(state, stream_id)
            if capture is not None and str(sequence) in capture["segments"]:
                capture["segments"][str(sequence)]["utterance_id"] = utterance["utterance_id"]
            # The audio host's screen is used only if explicitly pinned as context for this stream.
            # speaker_id stays null for a room microphone even when a screen was pinned.
            expected_head = state["versions"][0]["version_id"]
            for decision in state["decisions"]:
                if decision["committed_at"] <= utterance.get("voice_onset_ms", start) and decision["selected_model_version_id"]:
                    expected_head = decision["selected_model_version_id"]
            if state["raw_audio_saved"]:
                path = self.root / "media"
                path.mkdir(exist_ok=True)
                asset = uid("audio")
                (path / asset).write_bytes(wav)
                utterance["audio_url"] = f"/v1/sessions/{sid}/media/{asset}"
                utterance["audio_asset_id"] = asset
            newer_assembly_request = state.get("assembly") and end < state.get("assembly_latest_request_at", 0)
            if newer_assembly_request:
                utterance["processing_notice"] = "더 최근에 제출한 수정 요청을 유지했습니다."
            elif state.get("assembly") and expected_head == state["committed_version_id"]:
                job = shared_workflows.request_from_utterance(self, state, actor, utterance,
                          {**(stamp or {}), "captured_session_time_ms": end, "received_at": now_ms()})
            elif not state.get("assembly") and state["status"] == "active" and expected_head == state["committed_version_id"]:
                try:
                    pending = next((r for r in reversed(state["rounds"]) if r["status"] == "needs_input"
                                    and r["actor_id"] == view_actor and r.get("context")
                                    and r["context"]["utterance"].get("audio_stream_id") == stream_id
                                    and 0 <= start - r["context"]["utterance"]["end_ms"] <= 30_000
                                    and self.clarification_answer(state["profile"], line)), None)
                    item = self.attach_context(state, utterance, previous=pending["context"] if pending else None,
                                               reuse=pending, audio_base=expected_head)
                    job = ("plan", item["round_id"], item["generation_epoch"])
                except MeetingError as error:
                    utterance["processing_notice"] = str(error)
            elif expected_head != state["committed_version_id"]:
                utterance["processing_notice"] = "발화 후 확정 모델이 바뀌었습니다. 저장된 발화에서 새 검토를 시작할 수 있습니다."
            state["cloud_state"] = "online"
            self.emit(state, "transcript.finalized", utterance, actor, occurred=start)
            if state.get("session_status") == "completed":
                job = ("result", "", 0)
        if job:
            self.submit(sid, *job)

    @staticmethod
    def finish_segment(state, stream_id, sequence, status):
        capture = meeting_state.state_of_capture(state, stream_id)
        if capture is not None and str(sequence) in capture["segments"]:
            capture["segments"][str(sequence)]["status"] = status

    def run_result(self, sid):
        # EOS and all queued STT are counted before copying the durable transcript.
        deadline = time.monotonic() + max(0, min(45, float(os.getenv("MEETING_FINALIZE_WAIT_SECONDS", "12"))))
        rerun = False
        try:
            with self.changed:
                while True:
                    if self.stopped:
                        return
                    state = self.load(sid)
                    pending = any(stream["status"] == "open" or any(s["status"] == "pending"
                                  for s in stream["segments"].values()) for stream in state["meeting_audio"].values())
                    remaining = deadline - time.monotonic()
                    strict = "transcript_status" in state["meeting"]
                    if not pending or not strict and remaining <= 0:
                        break
                    self.changed.wait(timeout=None if strict else remaining)
                checkpoint = state.get("pending_minutes_export")
                revision = checkpoint["revision"] if checkpoint else max((int(key) for key in state["result_exports"]), default=0) + 1
            try:
                if strict and not checkpoint:
                    meeting_result.freeze_transcript(self.root, state)
                    with self.transaction():
                        state = self.load(sid)
                        if state["meeting"]["transcript_status"] != "finalized":
                            state["meeting"].update(transcript_status="finalized", transcript_finalized_at=now_ms())
                            self.emit(state, "transcript.sealed", {"utterance_count": len(state["utterances"])})
                if checkpoint:
                    source_key = checkpoint["source_key"]
                else:
                    _, source_key = meeting_result.build_result(self.root, self.models, state, revision)
                    with self.transaction():
                        latest = self.load(sid)
                        latest["pending_minutes_export"] = {"source_key": source_key, "revision": revision}
                        self.emit(latest, "minutes.generating", {"revision": revision})
                manifest, key = meeting_result.add_minutes(self.root / "results" / sid / source_key, revision)
            except (OSError, ValueError, KeyError) as error:
                with self.transaction():
                    latest = self.load(sid)
                    latest["session_status"] = "save_error"
                    latest["result"] = {"status": "save_error", "missing": ["result_files"],
                                        "message": f"채택본을 유지했습니다. 결과를 다시 저장할 수 있습니다. ({type(error).__name__})"}
                    self.emit(latest, "meeting.save_error", latest["result"])
                return
            with self.transaction():
                latest = self.load(sid)
                prefix = f"/v1/sessions/{sid}/result"
                result = {"status": manifest["status"], "revision": revision, "missing": manifest["missing"],
                          "adopted_version_id": manifest["adopted_version_id"], "minutes_status": manifest["minutes_status"],
                          "zip_url": f"{prefix}.zip?revision={revision}",
                          "api_url": f"{prefix}?revision={revision}", "review_url": f"/meeting/?result={sid}"}
                latest["result_exports"][str(revision)] = {"key": key, "result": result, "evidence_key": source_key}
                latest.pop("pending_minutes_export", None)
                latest.update(session_status="completed", result=result)
                self.emit(latest, "meeting.saved", result)
                rerun = (len(latest["utterances"]) != len(state["utterances"])
                         or meeting_state.capture_missing(latest) != manifest["missing"] and
                         meeting_state.capture_missing(latest) != meeting_state.capture_missing(state))
        finally:
            with self.lock:
                self.exporting.discard(sid)
                rerun = rerun or sid in self.export_updates
                self.export_updates.discard(sid)
            if rerun:
                self.submit(sid, "result", "", 0)

    def regenerate_minutes(self, sid, token):
        with self.transaction():
            actor = self.actor(sid, token)
            state = self.load(sid)
            self.host(state, actor)
            pending = state.get("pending_minutes_export")
            if state.get("session_status") == "finalizing" and pending:
                return {"status": "generating", "revision": pending["revision"]}
            if state.get("session_status") != "completed" or not state.get("result_exports"):
                raise MeetingError("저장된 회의에서 회의록을 다시 생성할 수 있습니다.", 409)
            revision = max(map(int, state["result_exports"])) + 1
            previous = state["result_exports"][str(revision - 1)]
            state["pending_minutes_export"] = {"source_key": previous.get("evidence_key", previous["key"]), "revision": revision}
            state["session_status"] = "finalizing"
            self.emit(state, "minutes.regenerating", {"revision": revision}, actor)
        self.submit(sid, "result", "", 0)
        return {"status": "generating", "revision": revision}

    def result_folder(self, sid, token, revision):
        _, state = self.authenticated(sid, token)
        exports = state.get("result_exports", {})
        key = str(revision or (state.get("result") or {}).get("revision") or max((int(k) for k in exports), default=0))
        registered = exports.get(key)
        if not registered:
            raise MeetingError("아직 저장한 회의 결과가 없습니다.", 404)
        return self.root / "results" / sid / registered["key"], registered["result"]


    def event_connected(self, sid, actor):
        with self.lock:
            key = (sid, actor)
            self.event_connections[key] = self.event_connections.get(key, 0) + 1

    def event_disconnected(self, sid, actor):
        with self.transaction():
            key = (sid, actor)
            self.event_connections[key] = max(0, self.event_connections.get(key, 0) - 1)
            if self.event_connections[key]:
                return
            state = self.load(sid)
            interrupted = paper_space.interrupt_pending(state, "요청자의 연결이 끊겼습니다. B를 눌러 다시 말해주세요.", actor)
            if interrupted:
                self.emit(state, "speech.ranges_interrupted", {"range_ids": interrupted}, actor)
            for capture in state.get("meeting_audio", {}).values():
                for rid, window in capture.get("design_ranges", {}).items():
                    if window["participant_id"] == actor and window.get("b_status") == "recording":
                        window.update(b_status="failed", error="버튼 연결이 끊겼습니다. B를 눌러 다시 말해주세요.")
                        self.emit(state, "speech.range_changed", {"range_id": rid, "status": "failed"}, actor)
            if state["status"] == "active" and paper_space.lose_alignment(state, actor):
                self.emit(state, "paper.alignment_changed", {"paper_frame_revision": state["paper_frame"]["paper_frame_revision"],
                                                            "participant_id": actor, "status": "lost"}, actor)
            draft = state.get("edit_draft")
            if draft and draft["owner_id"] == actor:
                shared_workflows.clear(self, state, "편집자의 연결이 종료되었습니다.")
                self.emit(state, "edit.released", {"participant_id": actor})

    def continue_session(self, sid, token, identity, *, design=False):
        source_actor, source = self.authenticated(sid, token)
        return self._continue_from_source(source, source_actor, identity, design=design, parent_token=token)

    def open_archived_review(self, sid):
        """Local archive-owner operation; unavailable to an HTTP service coordinator."""
        if not self.exclusive_archive:
            raise MeetingError("현장 PC에서 보관함을 단독으로 연 뒤 실행하세요.", 403)
        from .contracts import ContinueDesign
        identity = ContinueDesign(display_name="현장 PC 운영자", device_id="local-archive-operator")
        source = self.load(sid)
        child = self._continue_from_source(source, "local-archive-operator", identity, design=True)
        with self.changed:
            while True:
                state = self.load(child)
                round_state = state["rounds"][-1]["status"]
                if round_state not in RUNNING:
                    break
                self.changed.wait(timeout=2)
        with self.transaction():
            state = self.load(child)
            if now_ms() >= state["join_expires_at"]:
                state["join_code"] = self.code()
            state["join_expires_at"] = now_ms() + CODE_TTL
            self.save(state)
            return {"session_id": child, "join_code": state["join_code"], "status": round_state}

    def _continue_from_source(self, source, source_actor, identity, *, design, parent_token=None):
        sid = source["session_id"]
        if source.get("session_status") != "completed":
            raise MeetingError("회의 결과 저장이 완료된 뒤 다음 작업을 시작하세요.", 409)
        expected_mode = "live" if self.mode == "live" else "replay"
        if source["meeting"]["mode"] != expected_mode:
            raise MeetingError("저장된 결과와 같은 live 또는 replay 모드에서 후속 작업을 시작하세요.", 409)
        selected = self.version(source, source["committed_version_id"])
        self.verify_model(selected)
        if source["profile"]["object_id"] != self.profile["object_id"] or selected["source_hash"] != source["profile"]["source_hash"]:
            raise MeetingError("저장된 모델의 프로필로 서버를 실행해 주세요.", 409)
        if selected["source_hash"] != self.profile["source_hash"]:
            if not source["profile"].get("assembly"):
                raise MeetingError("저장된 모델의 프로필로 서버를 실행해 주세요.", 409)
            from .openscad_geometry import adapter_for_source
            try:
                adapter_for_source(selected["source_hash"])
            except geometry.GeometryError as error:
                raise MeetingError("저장된 모델의 프로필로 서버를 실행해 주세요.", 409) from error
        saved = shared_workflows.saved_design_utterance(source) if design else None
        job = None
        with self.transaction():
            access_token = None
            if design:
                # Derive a retryable child credential from the authenticated caller's secret.
                # Only its hash and a reference to the child are persisted.
                key = identity.request_id or "default"
                signature = digest([identity.device_id, identity.display_name, "continue-design"])
                if parent_token is not None:
                    access_token = hmac.new(parent_token.encode(), canonical(["continue-design",sid,key]).encode(), hashlib.sha256).hexdigest()
                prior = self.db.execute("SELECT identity_hash,child_id FROM design_continuations "
                                        "WHERE source_id=? AND actor_id=? AND request_id=?", (sid, source_actor, key)).fetchone()
                if prior:
                    if prior["identity_hash"] != signature:
                        raise MeetingError("이 요청 ID는 다른 후속 작업에서 사용됐습니다.", 409)
                    child = prior["child_id"]
                    state = self.load(child)
                    if parent_token is None:
                        if state["status"] != "active":
                            # This review completed. Open a fresh child on the next operator request.
                            self.db.execute("DELETE FROM design_continuations WHERE source_id=? AND actor_id=? AND request_id=?",
                                            (sid, source_actor, key))
                        else:
                            return child
                    else:
                        actor_id = self.actor(child, access_token)
                        participant = next(p for p in state["participants"] if p["participant_id"] == actor_id)
                        return self.connection(state, participant, access_token)
                if not saved:
                    raise MeetingError("저장된 실시간 전사에서 수정 요청을 찾지 못했습니다. 새 회의에서 변경 내용을 입력하세요.", 409)
            child = uid("meeting")
            initial = self.decorate(child, copy.deepcopy(selected))
            state, participant, child_token = self._insert_session(child, source["profile"], initial, identity, access_token)
            actor_id = participant["participant_id"]
            state["versions"] = [self.decorate(state["session_id"], copy.deepcopy(v), v.get("parent_version_id"),
                                               v.get("created_from_candidate_id"), v.get("request_constraints")) for v in source["versions"]]
            state["committed_version_id"] = selected["version_id"]
            state["original_version_id"] = source["original_version_id"]
            state["decisions"] = copy.deepcopy(source["decisions"])
            state["constraints"] = copy.deepcopy(source["constraints"])
            state["constraint_revision"] = source["constraint_revision"]
            state["adopted_revision"] = source["adopted_revision"]
            state["rounds"] = copy.deepcopy(source["rounds"])
            for item in state["rounds"]:
                if item["status"] in OPEN:
                    self.finish_round(item, "superseded", "이전 회의의 검토 기록입니다.")
            state["transfer"] = {"source_session_id": sid, "source_version_id": selected["version_id"],
                                 "source_result_revision": source["result"]["revision"],
                                 "source_mode": source["meeting"]["mode"], "loaded_at": now_ms(),
                                 "source_stl_sha256": selected["assets"]["print.stl"]["sha256"],
                                 "prior_decisions": copy.deepcopy(source["decisions"]),
                                 "prior_transcript": copy.deepcopy(source["utterances"])}
            state["workspace"] = copy.deepcopy(source["workspace"])
            if parent_token is None:
                state["transfer"]["initiated_by"] = "local_archive_operator"
            self.emit(state, "result.continued", state["transfer"], actor_id)
            if design:
                stamp = clock_stamp({}, now_ms())
                event, data, _ = meeting_state.apply(self, state, actor_id, "meeting_start",
                                                     {"mode": "live", "input_mode": "manual"}, stamp)
                self.emit(state, event, data, actor_id)
                payload = {"base_version_id": selected["version_id"], "expected_adopted_revision": state["adopted_revision"],
                           "use_registered_scope": True, "_saved_utterance": saved}
                target = next((t for t in reversed(source.get("design_target_history", []))
                               if t["captured_at"] <= saved["end_ms"]), None)
                if (target and target.get("part_id") and target["base_version_id"] == selected["version_id"]
                        and target["expected_adopted_revision"] == state["adopted_revision"]):
                    payload.update({k: target.get(k) for k in ("part_id", "hit_point_asset_m")}, _saved_selection=target)
                    image = next((i for i in source["images"] if i["evidence_id"] == target.get("image_id")
                                  and i["version_id"] == selected["version_id"]), None)
                    if image:
                        state["images"].append({**image, "url": f"/v1/sessions/{child}/media/{image['evidence_id']}"})
                        payload["image_id"] = image["evidence_id"]
                event, data, job = shared_workflows.apply(self, state, actor_id, "design_request", payload, stamp)
                self.emit(state, event, data, actor_id)
                self.db.execute("INSERT INTO design_continuations VALUES (?,?,?,?,?)",
                                (sid, source_actor, key, signature, child))
            result = child if parent_token is None else self.connection(state, participant, child_token)
        if job:
            self.submit(child, *job)
        return result
