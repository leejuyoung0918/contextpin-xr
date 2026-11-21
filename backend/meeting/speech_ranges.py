"""Resolve explicit speech windows against the existing continuous stream and its STT jobs."""
from .contracts import MeetingError, now_ms


def boundary(state, capture, stream_id, actor, range_id, start, sample, segments_finished):
    if (state.get("meeting", {}).get("design_submit_mode") != "explicit" or capture is None
            or capture["status"] != "open"):
        raise MeetingError("수정 발언은 명시적으로 시작한 회의의 녹음에서 지정하세요.", 409)
    ranges = capture.setdefault("design_ranges", {})
    if start:
        resuming = (state["session_status"] == "paused" and state["meeting"].get("capture_requested")
                    and capture.get("started_at") is None)
        if state["session_status"] not in {"recording", "ready"} and not resuming:
            raise MeetingError("회의 녹음 상태를 확인하세요.", 409)
        if any(range_id in info.get("design_ranges", {}) for info in state["meeting_audio"].values()):
            raise MeetingError("이미 사용한 발언 구간입니다.", 409)
        ranges[range_id] = {"range_id": range_id, "stream_id": stream_id, "participant_id": actor,
                            "start_sample": sample, "end_sample": None, "first_sequence": segments_finished + 1}
        if state["meeting"].get("edit_request_output") == "bundle":
            ranges[range_id].update(base_version_id=state["committed_version_id"], adopted_revision=state["adopted_revision"],
                                    created_at=now_ms())
    else:
        window = ranges.get(range_id)
        if not window or window["participant_id"] != actor or window["end_sample"] is not None:
            raise MeetingError("시작한 수정 발언 구간을 확인하세요.", 409)
        window.update(end_sample=sample, last_sequence=segments_finished)
    # This boundary message follows all prior PCM on the same WebSocket.
    capture["last_sample"] = sample


def sample_time(capture, sample):
    epoch = capture.get("started_at") or capture.get("clock_origin_ms")
    rate = capture.get("sample_rate")
    if epoch is not None and rate and sample is not None:
        return epoch + round(sample * 1000 / rate)
    return None


def meaningful_overlap(job, start, end):
    """At least half of the voiced span must be in the half-open command window.

    STT pre-roll and trailing silence deliberately do not count. Old archives
    split the PCM at both markers and identify matching jobs by sequence.
    """
    first, last = job["voice_start_sample"], job["voice_end_sample"]
    duration = last - first
    overlap = min(last, end) - max(first, start)
    return duration > 0 and 2 * overlap >= duration


def _status(capture, window, utterances_by_id):
    start, end = window["start_sample"], window["end_sample"]
    acknowledged = capture["last_sample"]
    result = {k: window[k] for k in ("range_id", "stream_id", "participant_id", "start_sample", "end_sample")}
    result.update(edit_request_id=window["range_id"], start_ms=sample_time(capture, start), end_ms=sample_time(capture, end),
                  received_through_sample=min(acknowledged, end) if end is not None else acknowledged,
                  utterances=[], error=None)
    if "base_version_id" in window:
        result.update(base_version_id=window["base_version_id"], created_at=window["created_at"])
        if end == start and capture.get("marker_overlap"):
            return {**result, "status": "no_speech", "error": "수정 요청을 듣지 못했습니다."}
        if end is not None and not window.get("command_audio"):
            return {**result, "status": "failed", "error": "수정 요청의 전체 녹음이 없습니다. 짧게 다시 녹음하세요."}
    if end is None:
        if capture["status"] == "open":
            return {**result, "status": "recording"}
        return {**result, "status": "failed", "error": "녹음 연결이 끊겼습니다. 수정 발언을 다시 녹음하세요."}
    missing_audio = any(g["sample_index"] > start and g.get("first_missing_sample", g["sample_index"]) < end
                        for g in capture["gaps"] if g.get("first_missing_sample") is not None)
    if acknowledged < end or missing_audio:
        return {**result, "status": "failed", "error": "수정 발언 구간의 오디오 수신에 공백이 있습니다."}
    if window.get("manual_utterance_id"):
        line = utterances_by_id.get(window["manual_utterance_id"])
        if not line:
            return {**result, "status": "failed", "error": "확정 발화 revision을 찾을 수 없습니다."}
        return {**result, "status": "final", "transcript_source": "manual",
                "utterances": [{key: line[key] for key in ("utterance_id", "revision", "text")}]}
    waiting = False
    source = "stt"
    for seq in range(window["first_sequence"], window["last_sequence"] + 1):
        job = capture["segments"].get(str(seq), {})
        if not job and capture.get("marker_overlap") and capture["status"] == "open":
            waiting = True
            continue
        if capture.get("marker_overlap") and "voice_start_sample" in job and not meaningful_overlap(job, start, end):
            continue
        if job.get("status") == "no_speech":
            continue
        if job.get("status") == "pending":
            waiting = True
            continue
        speech = utterances_by_id.get(job.get("utterance_id"))
        if job.get("status") != "final" or not speech or speech.get("status") != "final" or not speech.get("is_final", True):
            return {**result, "status": "failed", "error": "구간 안의 발언 전사가 끝나지 못했습니다. 다시 녹음하세요."}
        if speech.get("source_mode") == "manual" and speech.get("prior_revision"):
            source = "corrected"
        result["utterances"].append({k: speech[k] for k in ("utterance_id", "revision", "text")})
    if waiting:
        return {**result, "status": "transcribing"}
    if not result["utterances"]:
        return {**result, "status": "no_speech", "error": "수정 요청을 듣지 못했습니다."}
    if len(result["utterances"]) > 5 and "base_version_id" not in window:
        return {**result, "status": "failed", "error": "한 요청에는 최대 다섯 발화를 담을 수 있습니다. 짧게 다시 녹음하세요."}
    return {**result, "status": "final", "transcript_source": source}


def snapshot(state):
    utterances = {u["utterance_id"]: u for u in state["utterances"]}
    revisions = {(u["utterance_id"], u["revision"]): u for u in state["utterances"]}
    result = {}
    for capture in state.get("meeting_audio", {}).values():
        for rid, window in capture.get("design_ranges", {}).items():
            saved = state.get("edit_requests", {}).get(rid)
            # Before submission the user sees corrections live. Afterwards the
            # window points to the exact revisions that were sent to CAD.
            evidence = utterances
            if saved:
                evidence = {ref["utterance_id"]: revisions.get((ref["utterance_id"], ref["revision"]))
                            for ref in saved["transcript"]["utterance_refs"]}
            item = _status(capture, window, evidence)
            lines = item["utterances"] if item["status"] == "final" else []
            item["command_utterance_ids"] = [line["utterance_id"] for line in lines]
            item["command_text"] = " ".join(line["text"] for line in lines)
            if "base_version_id" in window and item["status"] == "final" and not saved:
                item["edit_request_draft"] = {"request_id": rid, "meeting_id": state["session_id"],
                    "base_version_id": window["base_version_id"], "utterance_id": lines[0]["utterance_id"],
                    "command_utterance_ids": item["command_utterance_ids"], "transcript": item["command_text"],
                    "transcript_source": item["transcript_source"],
                    "created_at": max(utterances[line["utterance_id"]]["received_at"] for line in lines)}
            result[rid] = item
    return result


def validate_refs(state, actor, speech_range_id, utterance_refs):
    if not isinstance(speech_range_id, str) or not isinstance(utterance_refs, list) or not utterance_refs:
        raise MeetingError("확정된 수정 발언 구간의 발화가 필요합니다.", 409)
    utterances = {u["utterance_id"]: u for u in state["utterances"]}
    for capture in state.get("meeting_audio", {}).values():
        window = capture.get("design_ranges", {}).get(speech_range_id)
        if window:
            view = _status(capture, window, utterances)
            expected = [{k: ref[k] for k in ("utterance_id", "revision")} for ref in view["utterances"]]
            if (view["participant_id"] != actor or view["status"] != "final" or expected != utterance_refs
                    or any(type(ref.get("revision")) is not int for ref in utterance_refs)):
                break
            return [utterances[ref["utterance_id"]] for ref in expected]
    raise MeetingError("선택한 수정 발언 구간과 확정 전사가 일치하지 않습니다.", 409)
