"""Resolve an authenticated B hold on the single, continuously transcribed room microphone."""
import base64
import math
import re
import threading
import time
from collections import deque

from . import paper_space, planner, speech_ranges
from .audio import MAX_SECONDS, encode_wav
from .contracts import MeetingError, now_ms, uid


class PcmTail:
    """Recent PCM runs and VAD progress from the shared microphone, in its own sample clock."""
    def __init__(self, rate, max_ms):
        self.rate = rate
        self.capacity = rate * (max_ms + 6000) // 1000
        self.changed = threading.Condition()
        self.runs = deque()
        self.through = self.settled = 0
        self.closed = False

    def append(self, start, pcm, first_open_voice):
        with self.changed:
            if not self.runs or start != self.through:
                self.runs.append((start, bytearray()))
            self.runs[-1][1].extend(pcm)
            self.through = start + len(pcm) // 2
            floor = self.through - self.capacity
            while self.runs and self.runs[0][0] < floor:
                first, data = self.runs.popleft()
                if first + len(data) // 2 > floor:
                    del data[:2 * (floor - first)]
                    self.runs.appendleft((floor, data))
            self.settled = self.through if first_open_voice is None else first_open_voice
            self.changed.notify_all()

    def close(self):
        with self.changed:
            self.closed = True
            self.settled = self.through
            self.changed.notify_all()

    def clip(self, start, end):
        with self.changed:
            self.changed.wait_for(lambda: self.through >= end or self.closed, timeout=5)
            for first, data in self.runs:
                if first <= start < end <= first + len(data) // 2:
                    return encode_wav(bytes(data[2 * (start - first):2 * (end - first)]), self.rate)
            raise MeetingError("버튼 구간의 연속 음성을 확보하지 못했습니다. 다시 말해주세요.")


def mark(edge, state, stream_id, actor, rid, start, sample, base, observed=None):
    capture = state.get("meeting_audio", {}).get(stream_id)
    if (not capture or not capture.get("b_hold") or capture["status"] != "open" or
            state["session_status"] != "recording" or state["meeting"].get("edit_request_output") != "bundle"):
        raise MeetingError("회의의 연속 마이크가 준비된 뒤 B를 눌러 주세요.", 409)
    ranges = capture.setdefault("design_ranges", {})
    if start:
        if base != state["committed_version_id"]:
            raise MeetingError("Latest가 바뀌었습니다. 새 모델에서 B를 눌러 주세요.", 409)
        if any(rid in info.get("design_ranges", {}) for info in state["meeting_audio"].values()):
            raise MeetingError("이미 사용한 수정 명령 ID입니다.", 409)
        for previous in ranges.values():
            if previous["participant_id"] != actor or previous.get("b_status") not in ("recording", "transcribing"):
                continue
            if (previous["b_status"] == "recording" and (sample - previous["start_sample"]) * 1000 >
                    edge.edit_command_limits["max_ms"] * capture["sample_rate"]):
                previous.update(b_status="too_long", error="수정 요청이 너무 깁니다. B를 누르고 짧게 다시 말해주세요.")
            else:
                raise MeetingError("앞선 수정 명령 구간을 먼저 마치세요.", 409)
        placement = paper_space.freeze_for_command(state, actor, observed or {})
        window = ranges[rid] = {**placement, "range_id": rid, "stream_id": stream_id, "participant_id": actor,
            "b_status": "recording", "start_sample": sample, "end_sample": None, "created_at": now_ms(),
            "base_version_id": base, "adopted_revision": state["adopted_revision"]}
    else:
        window = ranges.get(rid)
        if not window or window["participant_id"] != actor or window.get("b_status") != "recording":
            raise MeetingError("이 참가자가 누른 B 버튼 구간을 확인하세요.", 409)
        if sample < window["start_sample"]:
            raise MeetingError("버튼 시계가 변경됐습니다. 구간을 취소하고 다시 말해주세요.", 409)
        window["end_sample"] = sample
        samples_ms = (sample - window["start_sample"]) * 1000
        rate = capture["sample_rate"]
        if samples_ms < edge.edit_command_limits["min_ms"] * rate:
            window.update(b_status="too_short", error="B를 누른 채 수정 내용을 말해주세요.")
        elif samples_ms > edge.edit_command_limits["max_ms"] * rate:
            window.update(b_status="too_long", error="수정 요청이 너무 깁니다. B를 누르고 짧게 다시 말해주세요.")
        else:
            window["b_status"] = "transcribing"
    session_time = speech_ranges.sample_time(capture, sample)
    marker = "start" if start else "end"
    body = {"range_id": rid, "stream_id": stream_id, "marker": "edit_command_" + marker, "edge": marker,
            "sample_index": sample, "timestamp_ms": session_time, "base_version_id": window["base_version_id"]}
    return "speech.range_changed", body, ("command_window", rid, stream_id) if window["b_status"] == "transcribing" else None


def remote(edge, state, actor, kind, payload, stamp):
    rid = payload.get("range_id")
    if not isinstance(rid, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,100}", rid):
        raise MeetingError("수정 요청 ID가 필요합니다.")
    if kind == "cancel_design_speech":
        from .edit_bundle import window_for
        window, _ = window_for(state, rid)
        if (not window.get("b_status") or window["participant_id"] != actor or
                rid in state.get("edit_requests", {})):
            raise MeetingError("직접 요청한 미제출 수정 명령만 취소할 수 있습니다.", 409)
        window["b_status"] = "cancelled"
        return "speech.range_changed", {"range_id": rid, "status": "cancelled"}, None
    stream = edge.audio_streams.get(state["session_id"])
    capture = state.get("meeting_audio", {}).get(stream)
    if not capture or capture.get("started_at") is None:
        raise MeetingError("공유 마이크와 기기 시계를 연결한 뒤 다시 말해주세요.", 409)
    if set(payload) - {"range_id", "edge", "base_version_id", "paper_frame_revision", "expected_pose_revision",
                       "source_monotonic_ms", "clock_offset_ms", "clock_uncertainty_ms"}:
        raise MeetingError("인증된 참가자의 버튼 시각만 전송하세요.")
    if payload.get("edge") not in ("start", "end"):
        raise MeetingError("버튼 시작 또는 종료를 지정하세요.")
    # An ordered B Up may be waiting for a delayed B Down acknowledgement. Its captured
    # clock is still usable inside the PCM retention horizon; request-arrival time is not.
    clock = [payload.get(k) for k in ("source_monotonic_ms", "clock_offset_ms", "clock_uncertainty_ms")]
    if any(type(n) not in (int, float) or not math.isfinite(n) for n in clock) or not 0 <= clock[2] <= 500:
        raise MeetingError("기기 시계 동기화를 확인하고 다시 말해주세요.", 409)
    estimate = clock[0] + clock[1]
    if not math.isfinite(estimate):
        raise MeetingError("유효한 버튼 시각이 필요합니다.")
    captured = round(estimate)
    age = stamp["received_at"] - captured
    if not -500 <= age <= edge.edit_command_limits["max_ms"] + 6000:
        raise MeetingError("버튼 시각이 공유 마이크의 보관 구간을 벗어났습니다.", 409)
    sample = round((captured - capture["started_at"]) * capture["sample_rate"] / 1000)
    if sample < 0:
        raise MeetingError("마이크가 열린 후 B를 다시 눌러 주세요.", 409)
    return mark(edge, state, stream, actor, rid, payload["edge"] == "start", sample, payload.get("base_version_id"), payload)


def reject(state, stream_id, actor, rid, sample, base_id, error):
    capture = state.get("meeting_audio", {}).get(stream_id)
    if not capture or not rid:
        return
    ranges = capture.setdefault("design_ranges", {})
    window = ranges.get(rid)
    if window is None:
        if any(rid in other.get("design_ranges", {}) for other in state["meeting_audio"].values()):
            return  # A replay after microphone reconnect must not shadow the original evidence.
        window = ranges[rid] = {"range_id": rid, "stream_id": stream_id, "participant_id": actor,
            "start_sample": sample, "end_sample": None, "created_at": now_ms(),
            "base_version_id": base_id, "adopted_revision": state["adopted_revision"]}
    if window["participant_id"] == actor and window.get("b_status") in (None, "recording"):
        window.update(b_status="failed", error=error)


def resolve(edge, sid, rid, stream, ring):
    """Wait off the PCM and continuous-STT workers; never add a command-only STT to the timeline."""
    def current():
        state = edge.load(sid)
        capture = state["meeting_audio"][stream]
        return state, capture, capture["design_ranges"][rid]

    try:
        with edge.lock:
            state, _, window = current()
            if window["b_status"] != "transcribing" or edge.stopped:
                return
            start, end = window["start_sample"], window["end_sample"]
        if ring is None:
            raise MeetingError("공유 마이크 연결이 바뀌었습니다. 다시 말해주세요.")
        wav = ring.clip(start, end)
        with ring.changed:
            # Wait for VAD to bound an utterance still crossing B Up. Its normal
            # max length is 15s; the continuous transcript proceeds independently.
            if not ring.changed.wait_for(lambda: ring.settled >= end or ring.closed, timeout=MAX_SECONDS + 5):
                raise MeetingError("회의 음성 구간을 확정하지 못했습니다. 다시 말해주세요.")
        with edge.changed:
            deadline = time.monotonic() + 60
            while True:
                state, capture, window = current()
                if window["b_status"] != "transcribing" or edge.stopped:
                    return
                if any(g["sample_index"] > start and g.get("first_missing_sample", end) < end
                       for g in capture["gaps"] if g.get("first_missing_sample") is not None):
                    raise MeetingError("버튼 구간의 음성에 공백이 있습니다. 다시 말해주세요.")
                jobs = sorted((j for j in capture["segments"].values() if j.get("voice_end_sample", 0) > start
                               and j["voice_start_sample"] < end), key=lambda j: j["source_sequence"])
                if not any(j["status"] == "pending" for j in jobs):
                    break
                if time.monotonic() >= deadline:
                    raise MeetingError("확정 회의 전사를 받지 못했습니다. 다시 말해주세요.")
                edge.changed.wait(timeout=1)
        jobs = [j for j in jobs if j["status"] != "no_speech"]
        if not jobs:
            status, fields = "no_speech", {"error": "수정 요청을 듣지 못했습니다."}
        else:
            refs = [j["utterance_id"] for j in jobs if j["status"] == "final"]
            fields = {"linked_ids": refs}
            if all(j["status"] == "final" and start <= j["voice_start_sample"] and j["voice_end_sample"] <= end for j in jobs):
                status = "final"
                fields["command_text_source"] = "linked_meeting_utterances"
            else:
                with edge.lock:
                    if edge.stopped or current()[2]["b_status"] != "transcribing":
                        return
                # A complete, contained failed STT need not be sent twice. Retry only
                # for a genuinely different input: a clipped boundary-spanning utterance.
                if not any(j["voice_start_sample"] < start or j["voice_end_sample"] > end for j in jobs):
                    raise MeetingError("구간 안의 회의 전사가 확정되지 않았습니다. 다시 말해주세요.")
                payload = {"wav_base64": base64.b64encode(wav).decode()}
                if state["profile"].get("speech_vocabulary"):
                    payload["hotwords"] = state["profile"]["speech_vocabulary"]
                result = planner.gateway_request("/v1/transcriptions", payload)
                outcome = result.get("status", "final")
                if outcome not in ("final", "no_speech"):
                    raise MeetingError("버튼 구간 전사가 확정되지 않았습니다. 다시 말해주세요.")
                text = (result.get("transcript") or "").strip()[:1200] if outcome == "final" else ""
                status = "final" if text else "no_speech"
                if text:
                    fields.update(command_text_source="command_window_stt", clip_text=text)
                else:
                    fields["error"] = "수정 요청을 듣지 못했습니다."
        with edge.transaction():
            state, capture, window = current()
            if window["b_status"] != "transcribing" or edge.stopped:
                return
            if status == "final":
                asset = uid("command")
                directory = edge.root / "media"
                directory.mkdir(exist_ok=True)
                (directory / asset).write_bytes(wav)
                fields["command_audio"] = asset
            window.update(b_status=status, finalized_at=now_ms(), **fields)
            edge.emit(state, "speech.range_changed", {"range_id": rid, "status": status}, window["participant_id"])
    except Exception as exc:
        with edge.transaction():
            state, _, window = current()
            if window["b_status"] == "transcribing" and not edge.stopped:
                window.update(b_status="failed", error=str(exc) if isinstance(exc, MeetingError)
                              else "버튼 구간 전사를 확보하지 못했습니다. 다시 말해주세요.")
                edge.emit(state, "speech.range_changed", {"range_id": rid, "status": "failed"}, window["participant_id"])
