"""Authoritative design state. Each command and its event commit in one SQLite transaction."""
import hashlib
import json
import os
import re
import secrets
import sqlite3
import threading
from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
from pathlib import Path

from . import geometry
from .contracts import MeetingError, canonical, clock_stamp, digest, now_ms, text_field, uid
from .planner import NUMBER, ORDINAL, compile_context, contains, explicit_parameters, resolve_reference, validated_plan

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
    def __init__(self, root=None, planner=None):
        self.root = Path(root or os.environ.get("EDGE_DATA_DIR", MEETING_ROOT)).resolve()
        self.root.mkdir(parents=True, exist_ok=True)
        self.models = self.root / "models"
        self.models.mkdir(exist_ok=True)
        self.profile = geometry.load_profile()
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
        self.timers = set()
        self.stopped = False
        self.plan_pool = ThreadPoolExecutor(max_workers=2, thread_name_prefix="planner")
        self.speech_pool = ThreadPoolExecutor(max_workers=1, thread_name_prefix="speech")
        self.pending_audio = threading.BoundedSemaphore(12)
        self.build_pool = ThreadPoolExecutor(
            max_workers=max(1, min(4, int(os.environ.get("CAD_WORKER_CONCURRENCY", "1")))), thread_name_prefix="geometry")
        self.db = sqlite3.connect(self.root / "sessions.sqlite3", check_same_thread=False)
        self.db.row_factory = sqlite3.Row
        self.db.executescript("""
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS sessions (
                session_id TEXT PRIMARY KEY, join_code TEXT NOT NULL UNIQUE, state TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS participants (
                token_hash TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES sessions, participant_id TEXT NOT NULL
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
        with self.transaction():
            for row in self.db.execute("SELECT state FROM sessions").fetchall():
                state = json.loads(row["state"])
                interrupted = []
                state["capture_state"] = "off"
                state["provider_mode"] = self.mode
                state["cloud_state"] = "online" if self.mode == "live" and self.configured else "offline"
                for item in state["rounds"]:
                    if item["status"] in RUNNING:
                        self.finish_round(item, "failed", "서버 재시작으로 작업이 중단되었습니다. 새로 요청해 주세요.")
                        interrupted.append(item["round_id"])
                if interrupted:
                    self.emit(state, "jobs.interrupted", {"round_ids": interrupted})
                else:
                    self.save(state)

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
            for timer in self.timers:
                timer.cancel()
        self.speech_pool.shutdown(wait=True, cancel_futures=True)
        self.plan_pool.shutdown(wait=True, cancel_futures=True)
        self.build_pool.shutdown(wait=True, cancel_futures=True)
        with self.lock:
            self.db.close()

    def load(self, sid):
        row = self.db.execute("SELECT state FROM sessions WHERE session_id = ?", (sid,)).fetchone()
        if not row:
            raise MeetingError("회의를 찾을 수 없습니다.", 404)
        return json.loads(row["state"])

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

    @staticmethod
    def public(state, actor):
        public = {k: v for k, v in state.items() if k not in {"view_history", "audio_segments"}}
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
            return [json.loads(row["body"]) for row in rows], self.public(self.load(sid), actor)

    def code(self):
        while True:
            code = f"{secrets.randbelow(1_000_000):06d}"
            if not self.db.execute("SELECT 1 FROM sessions WHERE join_code=?", (code,)).fetchone():
                return code

    def participant(self, state, identity, role):
        participant = {"participant_id": uid("person"), "display_name": identity.display_name,
                       "device_id": identity.device_id, "role": role, "capabilities": identity.capabilities}
        state["participants"].append(participant)
        token = secrets.token_urlsafe(32)
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
                "parameters": version["full_parameters"], "measurements": version["measured_dimensions_mm"], "assets": assets,
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
        with self.transaction():
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
            self.db.execute("INSERT INTO sessions VALUES (?,?,?)", (sid, state["join_code"], canonical(state)))
            actor, token = self.participant(state, identity, "host")
            state["host_id"] = actor["participant_id"]
            self.emit(state, "session.created", {"version_id": version_id}, actor["participant_id"])
        return self.connection(state, actor, token)

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
            if state["status"] != "active":
                raise MeetingError("종료된 회의입니다.", 409)
            stamp = clock_stamp(command.payload, now_ms())
            event_type, result, job = self.apply(state, actor, command.type, command.payload, stamp)
            self.emit(state, event_type, result, actor, command.command_id, stamp["captured_session_time_ms"])
            response = {"command_id": command.command_id, **result}
            self.db.execute("INSERT INTO commands VALUES (?,?,?,?,?)",
                            (sid, command.command_id, actor, request_hash, canonical(response)))
        if job:
            self.submit(sid, *job)
        return {**response, "snapshot": self.public(state, actor)}

    def apply(self, state, actor, kind, payload, stamp):
        profile = state["profile"]
        sid = state["session_id"]
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
                    "tracking_state": payload.get("tracking_state") if payload.get("tracking_state") in ("tracking", "lost", "limited") else "unreported"}
            history = state["view_history"].setdefault(actor, [])
            history.append(view)
            del history[:-400]
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
                sample = profile.get("demo_utterance")
                if self.mode != "fixture" or payload.get("sample") != "curves" or not sample:
                    raise MeetingError("현재 모드에서는 이 예시를 열 수 없습니다.", 409)
                line = sample
            else:
                line = text_field(payload, "text")
            prior = None
            if kind == "clarify":
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
            if state["capture_state"] == "recording":
                raise MeetingError("녹음이 끝난 뒤 마이크를 바꿔 주세요.", 409)
            state["audio_host_device_id"] = participant["device_id"]
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
        item = self.round(state, payload.get("round_id"))
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
            for file, artifact in selected["assets"].items():
                path = self.models / selected["version_id"] / file
                if not path.is_file() or path.stat().st_size != artifact["size_bytes"] or hashlib.sha256(path.read_bytes()).hexdigest() != artifact["sha256"]:
                    raise MeetingError("검증한 모델 파일이 유실되거나 변경되었습니다.", 409)
            confirmed = list(dict.fromkeys([actor, *[a for a, c in item.get("acks", {}).items()
                                                     if c == candidate["candidate_id"]]]))
            if self.require_ack and len(confirmed) != len(state["participants"]):
                raise MeetingError("모든 참가자가 이 후보를 확인해야 합니다.", 409)
        reason = text_field(payload, "reason", 600, required=False)
        context = item.get("context") or {}
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
            utterance = context.get("utterance")
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
        state["decisions"].append(decision)
        self.finish_round(item, {"commit": "committed", "defer": "deferred", "reject": "dismissed"}[outcome])
        if selected:
            state["committed_version_id"] = selected["version_id"]
            self.supersede(state, exclude=item)
        return decision

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
        if state.get("assembly"):
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
                "text": line, "participant_id": prior["participant_id"] if prior else actor,
                "device_id": prior["device_id"] if prior else device, "speaker_id": None,
                "start_ms": start, "end_ms": end, "source_mode": source,
                "source_kind": {"live_capture": "audio", "replay": "fixture_transcript", "manual": "manual_text"}[source],
                "status": "final", "received_at": now_ms(), **extra}
        state["utterances"].append(item)
        return item

    @staticmethod
    def clarification_answer(profile, line):
        return bool(NUMBER.search(line) or explicit_parameters(profile, line) or any(
            contains(line, [p["label"], *p.get("aliases", [])]) for p in profile["semantic_parts"]))

    def attach_context(self, state, utterance, previous=None, reuse=None, audio_base=None):
        actor = utterance["participant_id"]
        when = utterance["start_ms"]
        views = [view for view in state["view_history"].get(actor, [])
                 if view["captured_session_time_ms"] <= when]
        view = max(views, key=lambda x: x["captured_session_time_ms"], default=None)
        if previous and not view:
            view = previous["view"]
        if view and view["tracking_state"] in {"lost", "limited"}:
            view = {**view, "part_ids": [], "hit_point_asset_m": None}
        candidate, base = resolve_reference(utterance, view)
        reference_missing = bool(ORDINAL.search(utterance["text"]) and not base)
        if not base:
            base = previous["edit_base_version_id"] if previous else audio_base or state["committed_version_id"]
        version = self.version(state, base)
        # An ongoing correction replaces an unreviewed plan and leaves a durable transition.
        is_correction = bool(re.search(r"^\s*(아니|정정|아까 말)", utterance["text"]))
        if is_correction and not reuse:
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
        context = compile_context(state, utterance, view, version, item["round_id"], item["generation_epoch"], previous)
        context["referenced_candidate_id"] = candidate
        if state.get("assembly") and version.get("parent_version_id"):
            parent = self.version(state, version["parent_version_id"])
            context["changed_base_parameters"] = [key for key, value in version["parameters"].items()
                                                   if value != parent["parameters"][key]]
        context["reference_missing"] = reference_missing
        context["context_hash"] = digest({"semantic": context["context_hash"], "candidate": candidate,
                                         "missing": context["reference_missing"]})
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
            if kind == "plan":
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
        return (item["generation_epoch"] == epoch and item["status"] in RUNNING
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
                item["status"] = "building"
                item["candidates"] = [self.new_candidate(item, option) for option in plan["options"]]
                event = "candidate.planned"
                build = True
            self.emit(state, event, {"round_id": rid, "request_id": context["request_id"],
                                    "job_id": item["planner_job_id"]})
        if build:
            self.submit(sid, "build", rid, epoch)

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
                params = restore if restore is not None else self.check_patch(
                    profile, version["parameters"], option["operations"], constraints)
                key = digest(params)
                if key in used or params == version["parameters"]:
                    raise MeetingError("이미 제시한 변경과 동일합니다.")
                result = geometry.build_model(self.models, version_id, profile, params, constraints, cancelled=cancelled)
                shape = result.get("surface_sha256", result["geometry_hash"])
                if profile.get("assembly") and shape in used_geometry:
                    import shutil
                    shutil.rmtree(self.models / version_id)
                    raise MeetingError("이 후보의 실제 형상이 이미 표시한 모델과 같습니다.")
                used_geometry.add(shape)
                used.add(key)
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
                    result = self.decorate(sid, result, version["version_id"], candidate["candidate_id"],
                                           item["request_constraints"])
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

    def record_image(self, sid, token, raw, mime, version_id, part_id, stamp, source_mode="manual"):
        with self.transaction():
            actor = self.actor(sid, token)
            state = self.load(sid)
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
            evidence = {**stamp, "evidence_id": eid, "version_id": version["version_id"], "participant_id": actor,
                        "device_id": device, "part_ids": [part_id] if part_id else [], "source_mode": source_mode,
                        "revision": 1, "sha256": hashlib.sha256(raw).hexdigest(),
                        "url": f"/v1/sessions/{sid}/media/{eid}", "mime": mime}
            state["images"].append(evidence)
            self.emit(state, "image.added", evidence, actor)
            return evidence

    def audio_start(self, sid, token, stream_id, device):
        with self.transaction():
            actor = self.actor(sid, token)
            state = self.load(sid)
            actual_device = next(p["device_id"] for p in state["participants"] if p["participant_id"] == actor)
            if state["status"] != "active" or state["capture_state"] == "recording":
                raise MeetingError("이미 녹음 중이거나 회의가 종료되었습니다.", 409)
            if state["audio_host_device_id"] != actual_device or (device and device != actual_device):
                raise MeetingError("지정된 마이크 기기에서 시작해 주세요.", 403)
            if self.mode != "live":
                raise MeetingError("재생 모드에서는 직접 입력 또는 예시 발화를 사용하세요.", 409)
            if not self.speech_available:
                raise MeetingError("전사 공급자가 설정되어 있지 않습니다. 발화를 직접 입력해 주세요.", 503)
            state["capture_state"] = "recording"
            self.audio_streams[sid] = stream_id
            self.emit(state, "capture.state_changed", {"capture_state": "recording", "stream_id": stream_id}, actor)
            return actor, actual_device

    def audio_stop(self, sid, stream_id):
        with self.transaction():
            if self.audio_streams.get(sid) != stream_id:
                return
            self.audio_streams.pop(sid)
            state = self.load(sid)
            state["capture_state"] = "off"
            self.emit(state, "capture.state_changed", {"capture_state": "off", "stream_id": stream_id})

    def audio_gap(self, sid, stream_id, sample, code):
        with self.transaction():
            state = self.load(sid)
            self.emit(state, "audio.gap", {"stream_id": stream_id, "sample_index": sample, "reason": code})

    def audio_submit(self, sid, stream_id, sequence, start, end, wav, actor, device, view_actor=None, stamp=None):
        with self.lock:
            if self.stopped:
                return
            if not self.pending_audio.acquire(blocking=False):
                raise MeetingError("전사 대기열이 가득 찼습니다.", 429)
            def work():
                try:
                    self.transcribe_audio(sid, stream_id, sequence, start, end, wav, actor, device, view_actor, stamp)
                finally:
                    self.pending_audio.release()
            self.speech_pool.submit(work)

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
            result = gateway_request("/v1/transcriptions", {"wav_base64": base64.b64encode(wav).decode()})
            line = result["transcript"].strip()[:1200] if result.get("status", "final") == "final" else ""
        except Exception:
            with self.transaction():
                state = self.load(sid)
                state["cloud_state"] = "degraded"
                self.emit(state, "speech.failed", {**record, "message": "전사에 실패했습니다. 발화를 직접 입력할 수 있습니다."}, actor)
            return
        if not line:
            with self.transaction():
                state = self.load(sid)
                self.emit(state, "speech.failed", {**record, "message": "확정된 발화를 받지 못했습니다. 다시 녹음하거나 입력해 주세요."}, actor)
            return
        job = None
        with self.transaction():
            state = self.load(sid)
            utterance = self.utterance(state, line, start, end, view_actor, device, "live_capture",
                                       **(stamp or {}), audio_stream_id=stream_id, source_sequence=sequence,
                                       captured_by_participant_id=actor, timing_quality="segment_bounds",
                                       stt_final_received=now_ms(), speech_to_transcript_ms=max(0, now_ms() - end))
            # The audio host's screen is used only if explicitly pinned as context for this stream.
            # speaker_id stays null for a room microphone even when a screen was pinned.
            expected_head = state["versions"][0]["version_id"]
            for decision in state["decisions"]:
                if decision["committed_at"] <= start and decision["selected_model_version_id"]:
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
            elif state["status"] == "active" and expected_head == state["committed_version_id"]:
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
        if job:
            self.submit(sid, *job)
