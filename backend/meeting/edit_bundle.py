"""Persist one marked audio window and one frame of selection evidence at the adopted CAD."""
import hashlib
import json
import math
import tempfile
from datetime import datetime, timezone
from pathlib import Path

from .contracts import MeetingError, canonical
from . import speech_ranges


def iso_time(milliseconds):
    return datetime.fromtimestamp(milliseconds / 1000, timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")


def window_for(state, request_id):
    for capture in state.get("meeting_audio", {}).values():
        window = capture.get("design_ranges", {}).get(request_id)
        if window:
            return window, capture
    raise MeetingError("수정 요청 녹음 구간을 찾을 수 없습니다.", 409)


def check_window(state, actor, request_id, base, revision):
    window, _ = window_for(state, request_id)
    if (window["participant_id"] != actor or window.get("base_version_id") != base
            or window.get("adopted_revision") != revision):
        raise MeetingError("녹음을 시작한 참가자와 당시 Latest 버전을 확인하세요.", 409)
    if speech_ranges.snapshot(state)[request_id]["status"] != "final":
        raise MeetingError("수정 요청의 최종 전사를 기다려 주세요.", 409)


def command_utterances(state, bundle):
    """Resolve the exact final revisions selected when the command window closed."""
    refs = bundle["transcript"]["utterance_refs"]
    ids = [ref["utterance_id"] for ref in refs]
    if not ids or bundle.get("command_utterance_ids", ids) != ids or len(ids) != len(set(ids)):
        raise MeetingError("수정 요청의 발화 ID가 전사 근거와 다릅니다.", 409)
    timeline = {(u["utterance_id"], u["revision"]): u for u in state["utterances"]
                if u.get("is_final", u.get("status") == "final")
                and u.get("meeting_id", u.get("session_id")) == bundle["meeting_id"]}
    try:
        return [timeline[(ref["utterance_id"], ref["revision"])] for ref in refs]
    except KeyError as error:
        raise MeetingError("회의 전사에서 수정 요청의 확정 발화를 찾을 수 없습니다.", 409) from error


def vector(data, key, size=3, normalized=False):
    v = data.get(key)
    if (not isinstance(v, list) or len(v) != size
            or any(type(x) not in (float, int) or not math.isfinite(x) for x in v)
            or (normalized and abs(math.hypot(*v) - 1) > .002)):
        raise MeetingError(f"{key} 좌표를 확인하세요.")
    return v


def pose(data, key):
    item = data.get(key)
    if not isinstance(item, dict):
        raise MeetingError(f"{key} 위치와 방향이 필요합니다.")
    return {"position": vector(item, "position"), "rotation_xyzw": vector(item, "rotation_xyzw", 4, True)}


def transform_point(model, local):
    q = model["rotation_xyzw"]
    def cross(a, b):
        return [a[1]*b[2] - a[2]*b[1], a[2]*b[0] - a[0]*b[2], a[0]*b[1] - a[1]*b[0]]
    axis_cross = cross(q, local)
    return [p + v + 2*(q[3]*t + s) for p, v, t, s in
            zip(model["position"], local, axis_cross, cross(q, axis_cross))]


def evidence(ref, hit, base):
    if (ref.get("object_id") != base["object_id"] or ref.get("base_version_id") != base["version_id"]
            or hit["reference_version_id"] != base["version_id"]):
        raise MeetingError("녹음을 시작한 Latest CAD의 부위를 선택하세요.", 409)
    rgb = hit["source_mode"] == "live_capture"
    if (ref.get("timestamp") != iso_time(hit["selected_at_ms"])
            or ref.get("capture_timestamp") != iso_time(hit["captured_at_ms"])
            or ref.get("image_source") != ("quest_rgb" if rgb else "rendered_debug")
            or ref.get("actual_rgb") != ("available" if rgb else "unavailable")):
        raise MeetingError("선택 시각, 촬영 시각과 이미지 출처가 일치해야 합니다.", 409)
    if type(ref.get("workspace_revision")) is not int or ref["workspace_revision"] < 1:
        raise MeetingError("A4 정렬을 다시 확인하세요.")
    fields = {k: ref[k] for k in ("object_id", "base_version_id", "timestamp", "workspace_revision",
                                  "image_source", "actual_rgb", "capture_timestamp")}
    for k in ("ray_origin_world", "ray_direction_world", "hit_world_position", "hit_model_local_position", "hit_normal_world"):
        fields[k] = vector(ref, k, normalized=k in ("ray_direction_world", "hit_normal_world"))
    for k in ("camera_pose", "workspace_pose", "latest_model_pose"):
        if k == "camera_pose" or k in ref:
            fields[k] = pose(ref, k)
    local = fields["hit_model_local_position"]
    if math.dist([-local[0], local[1], local[2]], hit["hit_point_asset_m"]) > .0001:
        raise MeetingError("모델 좌표와 CAD 표면의 선택 좌표가 일치하지 않습니다.", 409)
    origin, direction, contact = (fields[k] for k in ("ray_origin_world", "ray_direction_world", "hit_world_position"))
    if "latest_model_pose" in fields and math.dist(transform_point(fields["latest_model_pose"], local), contact) > .001:
        raise MeetingError("선택 순간의 모델 위치와 표면 좌표가 일치하지 않습니다.", 409)
    along = sum((c - o) * d for c, o, d in zip(contact, origin, direction))
    if along < 0 or math.dist([o + along * d for o, d in zip(origin, direction)], contact) > .002:
        raise MeetingError("가리킨 방향과 표면 위치가 일치하지 않습니다.", 409)
    if type(ref.get("sensor_timestamp_us")) is int:
        fields["sensor_timestamp_us"] = ref["sensor_timestamp_us"]
    if isinstance(ref.get("fallback_reason"), str):
        fields["fallback_reason"] = ref["fallback_reason"][:500]
    return fields


def save(coordinator, state, actor, client_id, base_id, ref, lines, fingerprint):
    window, capture = window_for(state, client_id)
    media = coordinator.root / "media"
    image = next(i for i in state["images"] if i["evidence_id"] == ref["image_id"])
    image_file = "target.jpg" if image["mime"] == "image/jpeg" else "target.png"
    source = "manual" if window.get("manual_utterance_id") else "stt"
    if source == "manual":
        if len(lines) != 1 or lines[0]["source_mode"] != "manual" or lines[0]["utterance_id"] != window["manual_utterance_id"]:
            raise MeetingError("직접 입력한 수정 요청의 발화를 확인하세요.", 409)
    else:
        recorded = {u["utterance_id"] for u in state["utterances"] if u["source_mode"] == "live_capture"
                    and u.get("audio_stream_id") == window["stream_id"] and u["is_final"]}
        for line in lines:
            if line["utterance_id"] not in recorded:
                raise MeetingError("수정 요청의 원본 전사를 확인하세요.", 409)
            if line["source_mode"] == "manual" and line.get("prior_revision"):
                source = "corrected"
            elif line["source_mode"] != "live_capture":
                raise MeetingError("최종 발화의 입력 출처를 확인하세요.", 409)
    transcript = {"utterance_id": lines[0]["utterance_id"], "text": " ".join(line["text"] for line in lines),
                  "utterance_refs": [{k: line[k] for k in ("utterance_id", "revision")} for line in lines],
                  "source": source,
                  "start_time": iso_time(capture["started_at"] + round(1000 * window["start_sample"] / capture["sample_rate"])),
                  "end_time": iso_time(capture["started_at"] + round(1000 * window["end_sample"] / capture["sample_rate"]))}
    if source == "manual":
        transcript.update(entered_by=lines[0]["participant_id"], entered_at=iso_time(lines[0]["received_at"]))
    selection = {k: ref[k] for k in ("selection_id", "part_id", "base_version_id", "object_id", "timestamp",
                 "workspace_revision", "ray_origin_world", "ray_direction_world", "hit_world_position",
                 "hit_model_local_position", "hit_normal_world", "hit_point_asset_m", "workspace_pose", "latest_model_pose",
                 "paper_frame_revision", "hit_position_paper", "ray_origin_paper", "ray_direction_paper") if k in ref}
    visual = {k: ref[k] for k in ("selection_id", "image_source", "actual_rgb", "capture_timestamp", "camera_pose",
                                  "sensor_timestamp_us", "fallback_reason", "image_uv", "image_uv_origin") if k in ref}
    visual.update(image_asset_id=image["evidence_id"], file=image_file, sha256=image["sha256"])
    parent = coordinator.root / "edit_requests" / state["session_id"]
    parent.mkdir(parents=True, exist_ok=True)
    # The range ID is validated on the audio socket and has to exist in this meeting.
    folder = parent / client_id
    try:
        audio = (media / window["command_audio"]).read_bytes()
        pixels = (media / image["evidence_id"]).read_bytes()
        if hashlib.sha256(pixels).hexdigest() != image["sha256"]:
            raise MeetingError("업로드한 이미지와 저장된 이미지가 다릅니다.", 409)
        bundle = {"request_id": client_id, "meeting_id": state["session_id"], "participant_id": actor,
                  "base_version_id": base_id, "created_at": iso_time(max(line["received_at"] for line in lines)),
                  "command_utterance_ids": [line["utterance_id"] for line in lines],
                  "command_text": transcript["text"], "transcript": transcript,
                  "selection": selection, "visual_evidence": visual, "fingerprint": fingerprint,
                  "audio": {"file": "command.wav", "sha256": hashlib.sha256(audio).hexdigest(),
                            "stream_id": window["stream_id"], "sample_rate": capture["sample_rate"], "start_sample": window["start_sample"],
                            "end_sample": window["end_sample"]}}
        if folder.exists():
            if json.loads((folder / "request.json").read_text()) != bundle:
                raise MeetingError("다른 내용으로 저장된 수정 요청입니다.", 409)
        else:
            with tempfile.TemporaryDirectory(dir=parent) as temp:
                stage = Path(temp) / "request"
                stage.mkdir()
                (stage / "command.wav").write_bytes(audio)
                (stage / image_file).write_bytes(pixels)
                (stage / "transcript.json").write_text(canonical(transcript), encoding="utf8")
                (stage / "request.json").write_text(canonical(bundle), encoding="utf8")
                stage.rename(folder)
    except OSError as exc:
        raise MeetingError("수정 요청의 파일을 저장할 수 없습니다. 같은 요청으로 다시 시도하세요.", 503) from exc
    state["edit_requests"][client_id] = bundle
    receipt = {"request_id": client_id, "client_request_id": client_id, "base_version_id": base_id}
    job = None
    if state["meeting"].get("candidate_generation") == "ai":
        from . import bundle_candidates
        _, candidate_receipt, job = bundle_candidates.queue(coordinator, state, client_id)
        receipt.update(candidate_receipt)
    return "design.request_saved", receipt, job
