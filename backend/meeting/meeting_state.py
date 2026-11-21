"""Small, durable state for the spatial assembly meeting."""
import re

from .contracts import MeetingError, now_ms, text_field

LIFECYCLE_COMMANDS = {"meeting_start", "meeting_pause", "meeting_end", "meeting_retry", "meeting_readopt"}
TERMINAL = {"finalizing", "completed", "save_error"}


def initialize(state):
    if not state.get("assembly"):
        return
    from . import shared_workflows
    shared_workflows.initialize(state)
    if "session_status" in state:
        return
    state.update(session_status="ready" if state["status"] == "active" else "completed",
                 adopted_revision=sum(d.get("selected_model_version_id") is not None for d in state["decisions"]),
                 design_revision=0, shared_preview=None, shared_view_history=[], result=None,
                 meeting={"mode": None, "started_at": None, "ended_at": None, "capture_requested": False, "audio_device_id": state["audio_host_device_id"]},
                 meeting_audio={}, result_exports={}, reviewed_candidates=[], discussion=None)


def snapshot_fields(state):
    actionable = next((r for r in reversed(state["rounds"])
                       if r["status"] in {"generating", "building", "needs_input", "reviewable", "failed"}), None)
    preview = state.get("shared_preview")
    design_status = "comparing" if preview else {
        "generating": "interpreting", "building": "generating", "reviewable": "proposals_ready",
        "failed": "error",
    }.get((actionable or {}).get("status"), "idle")
    if state["session_status"] in TERMINAL:
        design_status = "idle"
    ready = next((r for r in reversed(state["rounds"]) if r["status"] == "reviewable"), None)
    adopted = next((d for d in reversed(state["decisions"]) if d.get("selected_model_version_id")), None)
    conditions = (adopted or {}).get("constraints", []) + (actionable or {}).get("request_constraints", []) + [
        cond for cond in state["constraints"] if cond["status"] == "active"]
    measured = {p["property_id"]: p["label"] for p in state["profile"].get("measured_properties", [])}
    preserve = ["결합부·장착 기준", *dict.fromkeys(measured.get(c["property_id"], c["property_id"]) for c in conditions)]
    topic = state.get("discussion") or {}
    question = (actionable or {}).get("question") if (actionable or {}).get("status") == "needs_input" else None
    summary = {"discussion": topic.get("text", "외형 수정 논의 대기"),
               "preserved": " · ".join(preserve), "decision": decision_label(state, adopted),
               "proposal_count": sum(c["status"] == "valid" for c in (ready or {}).get("candidates", [])),
               "round_id": (ready or {}).get("round_id"), "question": question,
               "question_round_id": (actionable or {}).get("round_id") if question else None,
               "through_utterance_id": state["utterances"][-1]["utterance_id"] if state["utterances"] else None,
               "discussion_evidence": topic.get("utterance_id")}
    result = {"design_submit_mode": state["meeting"].get("design_submit_mode", "automatic"),
              "adopted_version_id": state["committed_version_id"], "design_status": design_status, "summary": summary}
    result["meeting_audio_status"] = state["capture_state"]
    if "transcript_status" in state["meeting"]:
        result["transcript_status"] = state["meeting"]["transcript_status"]
        result["transcript_finalized_at"] = state["meeting"].get("transcript_finalized_at")
    if state.get("meeting", {}).get("design_submit_mode") == "explicit":
        from . import speech_ranges
        result["design_speech_ranges"] = speech_ranges.snapshot(state)
    from . import cable_demo
    if cable_demo.enabled(state) and state.get("provider_mode") == "fixture" and state["meeting"].get("mode") != "live":
        result["cable_demo"] = cable_demo.presentation(state)
    return result


def decision_label(state, adopted=None):
    if adopted is None:
        adopted = next((d for d in reversed(state["decisions"]) if d.get("selected_model_version_id")), None)
    if not adopted:
        return "변경 채택 없음" if state["session_status"] in TERMINAL else "아직 미정"
    if adopted.get("type") == "readopt":
        return "이전 버전 재채택"
    return f'{adopted.get("candidate_label") or adopted.get("candidate_title") or "후보"} 채택'


def reset_preview(state, time_ms):
    state["shared_preview"] = None
    state["shared_view_history"].append({"captured_session_time_ms": time_ms, "cleared": True})


def note_view(state, view, item, share):
    candidate = view["candidate_id"]
    if candidate and candidate not in state["reviewed_candidates"]:
        state["reviewed_candidates"].append(candidate)
    if not share:
        return
    if not candidate:
        reset_preview(state, view["captured_session_time_ms"])
        return
    view["candidate_set_id"] = item["candidate_set_id"]
    state["shared_preview"] = {k: view[k] for k in
                               ("version_id", "round_id", "candidate_id", "candidate_set_id", "participant_id", "evidence_id")}
    state["shared_view_history"].append(view)


def current_view(state, utterance):
    """A room mic has no identified wearer. It can use a deliberately shared view."""
    start, end = utterance["start_ms"], utterance["end_ms"]
    actor = utterance["participant_id"]
    if actor is not None and utterance["source_mode"] != "live_capture":
        history = state["view_history"].get(actor, [])
    else:
        history = state["shared_view_history"]
    initial = max((view for view in history if view["captured_session_time_ms"] <= start),
                  key=lambda view: view["captured_session_time_ms"], default=None)
    crossed = any(start < view["captured_session_time_ms"] <= end and
                  (view.get("candidate_id"), view.get("version_id")) !=
                  ((initial or {}).get("candidate_id"), (initial or {}).get("version_id")) for view in history)
    return (initial if initial and not initial.get("cleared") else None), crossed


def state_of_capture(state, stream_id):
    if not state.get("assembly"):
        return None
    return state["meeting_audio"].get(stream_id)


def capture_missing(state):
    missing = []
    streams = state["meeting_audio"]
    if state["meeting"]["mode"] == "live" and state["meeting"].get("input_mode") != "manual" and not any(info["started_at"] for info in streams.values()):
        missing.append({"kind": "audio_not_started"})
    for stream_id, info in streams.items():
        if info["status"] != "eos":
            missing.append({"kind": "audio_end_not_confirmed", "stream_id": stream_id,
                            "last_received_sample": info["last_sample"], "started_at": info.get("started_at"),
                            "sample_rate": info.get("sample_rate")})
        for gap in info["gaps"]:
            missing.append({"kind": "audio_gap", "stream_id": stream_id, **gap})
        for item in info["segments"].values():
            if item["status"] not in {"final", "no_speech"}:
                missing.append({"kind": "transcription_" + item["status"],
                                "stream_id": stream_id, "sequence": item["source_sequence"],
                                "start_ms": item["start_ms"], "end_ms": item["end_ms"]})
    return missing


def apply(coordinator, state, actor, kind, payload, stamp):
    if not state.get("assembly"):
        raise MeetingError("조립체 회의에서 사용할 수 있습니다.", 409)
    sid = state["session_id"]
    status = state["session_status"]
    meeting = state["meeting"]
    timestamp = stamp["captured_session_time_ms"]
    if kind == "meeting_start":
        if status not in {"ready", "paused"} or sid in coordinator.audio_streams:
            raise MeetingError("회의 또는 마이크가 이미 시작되었습니다.", 409)
        mode = payload.get("mode", meeting["mode"] or ("live" if coordinator.mode == "live" else "replay"))
        if mode not in {"live", "replay"} or meeting["mode"] not in {None, mode}:
            raise MeetingError("이 회의의 입력 모드를 바꿀 수 없습니다.", 409)
        input_mode = payload.get("input_mode", "microphone" if mode == "live" else "replay")
        if input_mode not in {"microphone", "manual", "replay"}:
            raise MeetingError("회의 입력 방식을 확인하세요.")
        if mode == "live" and coordinator.mode != "live":
            raise MeetingError("서버를 live 모드로 실행해 주세요.", 409)
        if mode == "live" and input_mode == "microphone" and not coordinator.speech_available:
            raise MeetingError("전사 서비스가 연결된 뒤 녹음을 시작할 수 있습니다.", 503)
        if mode == "replay" and coordinator.mode != "fixture":
            raise MeetingError("예시 입력은 PC를 재생 모드로 시작한 경우에 사용할 수 있습니다.", 409)
        default_device = next(p["device_id"] for p in state["participants"] if p["participant_id"] == actor)
        device = payload.get("audio_device_id") or meeting["audio_device_id"] or default_device
        if device not in {p["device_id"] for p in state["participants"]}:
            raise MeetingError("회의에 연결된 마이크 기기를 선택하세요.")
        submit_mode = payload.get("design_submit_mode", meeting.get("design_submit_mode", "automatic"))
        if submit_mode not in ("automatic", "explicit") or (submit_mode == "explicit" and mode != "live"):
            raise MeetingError("수정 요청 방식을 확인하세요.")
        if meeting["mode"] is not None and submit_mode != meeting.get("design_submit_mode", "automatic"):
            raise MeetingError("시작한 회의의 수정 요청 방식을 바꿀 수 없습니다.", 409)
        output = payload.get("edit_request_output", meeting.get("edit_request_output"))
        if output is not None and (output != "bundle" or submit_mode != "explicit"):
            raise MeetingError("수정 요청 저장 방식을 확인하세요.")
        if meeting["mode"] is not None and output != meeting.get("edit_request_output"):
            raise MeetingError("시작한 회의의 수정 요청 저장 방식을 바꿀 수 없습니다.", 409)
        generation = payload.get("candidate_generation", meeting.get("candidate_generation"))
        if generation is not None and (generation != "ai" or output != "bundle"):
            raise MeetingError("AI 후보에는 수정 요청 원본을 저장하는 회의가 필요합니다.")
        if meeting["mode"] is not None and generation != meeting.get("candidate_generation"):
            raise MeetingError("회의 시작 시 선택한 후보 생성 방식을 유지하세요.", 409)
        if output == "bundle":
            meeting["edit_request_output"] = output
            meeting.setdefault("transcript_status", "open")
            state.setdefault("edit_requests", {})
        if generation == "ai":
            meeting["candidate_generation"] = generation
            state.setdefault("candidate_sets", {})
        meeting.update(mode=mode, design_submit_mode=submit_mode, input_mode=input_mode, audio_device_id=device, capture_requested=mode == "live" and input_mode == "microphone")
        state["audio_host_device_id"] = device
        if mode == "replay" or input_mode == "manual":
            meeting["started_at"] = meeting["started_at"] or now_ms()
            state["session_status"] = "recording"
        return "meeting.started" if mode == "replay" else "meeting.microphone_selected", {
            "mode": mode, "audio_device_id": device, "session_status": state["session_status"]}, None
    if kind == "meeting_pause":
        if status != "recording":
            raise MeetingError("진행 중인 회의를 확인해 주세요.", 409)
        state["session_status"] = "paused"
        meeting["capture_requested"] = False
        return "meeting.paused", {"session_status": "paused"}, None
    if kind == "meeting_end":
        if status in TERMINAL:
            return "meeting.end_repeated", {"session_status": status}, None
        meeting["started_at"] = meeting["started_at"] or now_ms()
        meeting["ended_at"] = now_ms()
        if "transcript_status" in meeting:
            meeting["transcript_status"] = "finalizing"
        meeting["capture_requested"] = False
        meeting["adopted_version_id"] = state["committed_version_id"]
        meeting["adopted_revision"] = state["adopted_revision"]
        # Stop future changes in the same transaction that fixes the chosen model.
        state.update(session_status="finalizing", status="ended", result={"status": "finalizing"})
        from . import shared_workflows
        shared_workflows.clear(coordinator, state, "회의를 종료했습니다.")
        reset_preview(state, timestamp)
        for item in state["rounds"]:
            if item["status"] in {"collecting", "generating", "building"}:
                coordinator.finish_round(item, "superseded", "회의를 종료했습니다.")
        return "meeting.finalizing", {"session_status": "finalizing", "adopted_version_id": meeting["adopted_version_id"]}, (
            "result", "", 0)
    if kind == "meeting_retry":
        if status != "save_error":
            raise MeetingError("다시 저장할 결과가 없습니다.", 409)
        state.update(session_status="finalizing", result={"status": "finalizing"})
        return "meeting.finalizing", {"session_status": "finalizing"}, ("result", "", 0)
    if kind == "meeting_readopt":
        version_id = payload.get("version_id")
        prior = next((d for d in reversed(state["decisions"]) if d.get("selected_model_version_id") == version_id), None)
        if version_id != state["original_version_id"] and not prior:
            raise MeetingError("이전 채택본을 선택하세요.", 409)
        if payload.get("expected_adopted_revision") != state["adopted_revision"] or version_id == state["committed_version_id"]:
            raise MeetingError("현재 채택본이 바뀌었거나 같은 버전입니다.", 409)
        from .contracts import uid
        from . import geometry
        version = coordinator.version(state, version_id)
        coordinator.verify_model(version)
        constraints = coordinator.effective(state, [])
        if geometry.violated_constraints(state["profile"], version["measurements"], constraints):
            raise MeetingError("이전 버전이 현재 유지 조건과 충돌합니다.", 409)
        previous = coordinator.version(state, state["committed_version_id"])
        reason = text_field(payload, "reason", 600, required=False)
        decision = {"decision_id": uid("decision"), "type": "readopt", "outcome": "commit",
                    "selected_model_version_id": version_id, "previous_committed_model_version_id": previous["version_id"],
                    "candidate_id": (prior or {}).get("candidate_id"), "confirmed_by": [actor],
                    "consensus_status": "not_checked", "committed_at": now_ms(), "actor_id": actor,
                    "rationale_text": reason, "rationale_source": "explicit" if reason else "not_stated",
                    "evidence_refs": [], "evidence_utterances": [], "constraints": constraints,
                    "parameter_changes": geometry.get_diff(state["profile"], previous["parameters"], version["parameters"])}
        state["decisions"].append(decision)
        coordinator.adopt(state, version_id)
        coordinator.supersede(state)
        return "decision.recorded", decision, None
    raise MeetingError("회의 명령을 확인해 주세요.")


def meeting_reference(line):
    return bool(re.search(r"이거|이s*(?:후보|부분|안)|아까s*(?:것|안)|(?<![A-Za-z])[ABC](?![A-Za-z])", line, re.I))
