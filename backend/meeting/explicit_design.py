"""Bind one marked speech interval to one photographed hit on a registered mesh."""
import json
import math

from .contracts import MeetingError, digest, text_field, uid
from .shared_workflows import check_head, point
from . import edit_bundle, paper_space


def check_selection_state(state, actor, request_id, selection_id):
    if state["session_status"] == "recording":
        return
    if state["session_status"] == "paused" and state["meeting"].get("edit_request_output") == "bundle":
        window, capture = edit_bundle.window_for(state, request_id)
        previous = state.get("explicit_design_targets", {}).get(actor, {})
        already_registered = (previous.get("client_request_id") == request_id and
                              previous["reference_selection"]["selection_id"] == selection_id)
        if (capture["status"] == "eos" and window["participant_id"] == actor and
                (window.get("pending_selection_id") == selection_id or already_registered)):
            return
    raise MeetingError("진행 중인 회의에서 수정 요청을 준비하세요.", 409)


def capture_identity(state, actor, client_id, selection_id, captured_at, source):
    if state.get("meeting", {}).get("design_submit_mode") != "explicit":
        raise MeetingError("명시적 수정 요청 회의에서 촬영하세요.", 409)
    identity = {"client_request_id": client_id, "selection_id": selection_id, "captured_at_ms": captured_at}
    for key in ("client_request_id", "selection_id"):
        if text_field(identity, key, 100) != identity[key]:
            raise MeetingError("요청 및 선택 ID에 앞뒤 공백을 포함할 수 없습니다.")
    if type(captured_at) is not int or not 0 < captured_at < 253402300799000:
        raise MeetingError("선택 당시의 촬영 시각이 필요합니다.")
    if state["session_status"] == "paused":
        check_selection_state(state, actor, client_id, selection_id)
    participant = next(p for p in state["participants"] if p["participant_id"] == actor)
    bundle = state["meeting"].get("edit_request_output") == "bundle"
    if source != "live_capture" and not (source == "xr_render" and (bundle or participant["capabilities"].get("runtime") == "simulator")):
        raise MeetingError("실물 사진을 다시 촬영하세요. 시뮬레이터 렌더는 별도 출처로 등록합니다.")
    return identity


def reference_hit(coordinator, state, actor, base, client_id, ref):
    if not isinstance(ref, dict):
        raise MeetingError("오른쪽 A로 선택한 모델 위치와 사진이 필요합니다.")
    hit = point(ref.get("hit_point_asset_m"))
    uv = ref.get("image_uv")
    if (not isinstance(uv, list) or len(uv) != 2 or
            any(type(v) not in (int, float) or not math.isfinite(v) or not 0 <= v <= 1 for v in uv)):
        raise MeetingError("선택 위치가 사진 안에 있는지 확인하세요.")
    if ref.get("tracking_state") != "tracking":
        raise MeetingError("실물 정렬을 확인한 뒤 위치와 사진을 다시 지정하세요.", 409)
    selection_id = text_field(ref, "selection_id", 100)
    part = text_field(ref, "part_id", 100)
    image_id = text_field(ref, "image_id", 100)
    image = next((i for i in state["images"] if i["evidence_id"] == image_id), None)
    captured = ref.get("captured_at_ms")
    if (not image or image["participant_id"] != actor or image.get("selection_id") != selection_id or
            image.get("client_request_id") != client_id or type(captured) is not int or image.get("captured_at_ms") != captured or
            image["version_id"] != ref.get("reference_version_id") or image["part_ids"] != [part] or
            ref.get("source_mode", image["source_mode"]) != image["source_mode"] or
            ref.get("image_uv_origin", "top_left") != "top_left"):
        raise MeetingError("이번 선택과 일치하는 업로드 사진이 필요합니다.", 409)
    reference = coordinator.version(state, ref["reference_version_id"])
    if (any(reference.get(k) is None or reference[k] != base.get(k) or reference[k] != state["profile"].get(k)
            for k in ("object_id", "source_hash", "adapter_id", "adapter_revision")) or
            part not in {p["part_id"] for p in state["profile"]["semantic_parts"]}):
        raise MeetingError("호환되는 부위를 Latest에서 다시 선택하세요.", 409)
    for version_id in {reference["version_id"], base["version_id"]}:
        try:
            parts = json.loads((coordinator.models / version_id / "semantic_parts.json").read_text())["parts"]
        except (OSError, ValueError, KeyError) as e:
            raise MeetingError("모델의 부위 정보를 읽지 못했습니다.", 409) from e
        if not any(p.get("part_id") == part for p in parts):
            raise MeetingError("해당 부위를 Latest에서 다시 선택하세요.", 409)
    bounds = reference["bounds_m"]
    if any(not lo - .001 <= coord <= hi + .001 for coord, lo, hi in zip(hit, bounds["min"], bounds["max"])):
        raise MeetingError("선택 좌표가 참조 모델 바깥입니다. 다시 선택하세요.")
    selected_at = ref.get("selected_at_ms", captured)
    if type(selected_at) is not int or abs(selected_at - captured) > 150:
        raise MeetingError("A 입력 시점의 카메라 프레임으로 다시 촬영하세요.", 409)
    return {"selection_id": selection_id, "reference_version_id": reference["version_id"],
            "selected_at_ms": selected_at,
            "part_id": part, "hit_point_asset_m": hit, "image_uv": uv, "image_uv_origin": "top_left",
            "image_id": image_id, "captured_at_ms": captured, "tracking_state": "tracking",
            "source_mode": image["source_mode"]}


def apply_selection(coordinator, state, actor, kind, payload, stamp):
    from .speech_ranges import validate_refs
    if state["session_status"] not in {"paused", "recording"}:
        raise MeetingError("진행 중인 회의에서 수정 요청을 준비하세요.", 409)
    if payload.get("use_registered_scope"):
        raise MeetingError("모델 위치와 사진을 직접 선택하세요.")
    client_id = text_field(payload, "client_request_id", 100)
    base = coordinator.version(state, text_field(payload, "base_version_id", 100))
    revision = payload.get("expected_adopted_revision")
    if type(revision) is not int:
        raise MeetingError("채택본 revision이 필요합니다.")
    reference = reference_hit(coordinator, state, actor, base, client_id, payload.get("reference_selection"))
    check_selection_state(state, actor, client_id, reference["selection_id"])
    bundle = state["meeting"].get("edit_request_output") == "bundle"
    if bundle:
        reference.update(edit_bundle.evidence(payload["reference_selection"], reference, base))
        raw = payload["reference_selection"]
        window, _ = edit_bundle.window_for(state, client_id)
        frozen = window.get("latest_pose_paper")
        if frozen or any(k in raw for k in (*paper_space.RAY_FIELDS, "paper_frame_revision", "latest_pose_revision")):
            frame = state.get("paper_frame")
            if not frame or not frame["latest_pose_paper"]:
                raise MeetingError("종이 기준이 준비될 때까지 기다려 주세요.", 409)
            paper_space.check_revision(frame, raw)
            paper_space.check_aligned(frame, actor)
            observed = raw.get("latest_pose_revision")
            if (not paper_space.matches_frame(state, window) or
                    "latest_pose_revision" in raw and (type(observed) is not int or observed != frame.get("latest_pose_revision", 0))):
                raise MeetingError("B를 누른 순간과 모델 배치가 다릅니다. 현재 위치에서 다시 요청하세요.", 409)
            evidence = paper_space.ray(raw)
            point = edit_bundle.transform_point(frozen or frame["latest_pose_paper"], reference["hit_model_local_position"])
            if math.dist(point, evidence["hit_position_paper"]) > .002:
                raise MeetingError("종이 기준 선택점과 CAD의 선택 좌표가 일치하지 않습니다.", 409)
            reference.update(evidence, paper_frame_revision=raw["paper_frame_revision"])
            if frozen:
                reference["latest_pose_revision"] = window["latest_pose_revision"]
    if (kind == "design_request" and (payload.get("output") == "bundle") != bundle):
        raise MeetingError("수정 요청과 회의의 저장 방식이 일치하지 않습니다.", 409)
    target = {"base_version_id": base["version_id"], "expected_adopted_revision": revision, "client_request_id": client_id,
              "reference_selection": reference, "participant_id": actor}
    targets = state.setdefault("explicit_design_targets", {})
    prior_target = targets.get(actor)
    old = next((r for r in state["rounds"] if r.get("client_request_id") == client_id), None)
    refs = payload.get("utterance_refs")
    speech_id = payload.get("speech_range_id")
    if kind == "design_request" and "utterance_ids" in payload and (
            not isinstance(refs, list) or any(not isinstance(ref, dict) for ref in refs)
            or payload["utterance_ids"] != [ref.get("utterance_id") for ref in refs]):
        raise MeetingError("지정한 발화와 revision이 일치하지 않습니다.", 409)
    fingerprint = digest([target, speech_id, refs])
    saved = state.get("edit_requests", {}).get(client_id)
    if saved:
        if kind == "design_request" and saved["fingerprint"] == fingerprint:
            receipt = {"request_id": client_id, "client_request_id": client_id, "base_version_id": base["version_id"]}
            existing_set = state.get("candidate_sets", {}).get(client_id)
            if existing_set:
                receipt["candidate_set_id"] = existing_set["candidate_set_id"]
            return "design.request_saved", receipt, None
        raise MeetingError("이미 저장한 수정 요청은 변경할 수 없습니다.", 409)
    if old:
        if kind == "design_request" and old.get("comparison_fingerprint") == fingerprint:
            return "design.request_reused", {"round_id": old["round_id"], "request_id": old["context"]["request_id"],
                                             "client_request_id": client_id}, None
        raise MeetingError("이미 접수한 수정 요청입니다. 새 요청으로 시작하세요.", 409)
    check_head(state, payload)
    if base["version_id"] != state["committed_version_id"] or state.get("edit_draft"):
        raise MeetingError("현재 채택본을 확인한 뒤 다시 지정하세요.", 409)
    if bundle:
        edit_bundle.check_window(state, actor, client_id, base["version_id"], revision)
    if kind == "design_target":
        if prior_target and target != prior_target:
            before = prior_target["reference_selection"]
            if before["selection_id"] == reference["selection_id"] or reference["captured_at_ms"] <= before["captured_at_ms"]:
                raise MeetingError("선택 정보가 변경되었습니다. 위치와 사진을 다시 확인하세요.", 409)
        targets[actor] = target
        if bundle and frozen:
            state["paper_frame"]["shared_selection"] = {"selection_id": reference["selection_id"],
                "participant_id": actor, "model_version_id": base["version_id"], "updated_at": reference["selected_at_ms"],
                **{k: reference[k] for k in (*paper_space.RAY_FIELDS, "paper_frame_revision", "latest_pose_revision")}}
        shared = ({**target, "reference_selection": paper_space.public_evidence(reference)}
                  if state.get("paper_frame") else target)
        return "design.target_selected", shared, None
    if target != prior_target:
        raise MeetingError("먼저 이번 사진과 부위의 선택을 확정하세요.", 409)
    lines = validate_refs(state, actor, speech_id, refs)
    if bundle:
        if speech_id != client_id:
            raise MeetingError("이 요청에서 녹음한 수정 발언을 선택하세요.", 409)
        return edit_bundle.save(coordinator, state, actor, client_id, base["version_id"], reference, lines, fingerprint)
    # An older worker may still finish after this entirely new, validated request.
    for previous in state["rounds"]:
        if previous.get("client_request_id") and previous["status"] in {"collecting", "generating", "building", "needs_input"}:
            coordinator.finish_round(previous, "superseded", "더 최근의 수정 요청이 들어왔습니다.")
            coordinator.emit(state, "round.superseded", {"round_id": previous["round_id"], "revision": previous["revision"]})
    same_model = reference["reference_version_id"] == base["version_id"]
    view = {**stamp, "evidence_id": uid("view"), "source_mode": "controller_selection", "revision": 1,
            "participant_id": actor, "version_id": base["version_id"], "part_ids": [reference["part_id"]],
            "device_id": next(p["device_id"] for p in state["participants"] if p["participant_id"] == actor),
            "candidate_id": None, "candidate_versions": {}, "display_order_candidate_ids": [],
            "hit_point_asset_m": reference["hit_point_asset_m"] if same_model else None,
            "image_id": reference["image_id"] if same_model else None, "tracking_state": "tracking",
            "reference_selection": reference}
    item = coordinator.attach_context(state, lines[-1], request_view=view, request_base=base["version_id"],
                                      request_actor=actor, selected_lines=lines, client_request_id=client_id,
                                      speech_range_id=speech_id)
    item["client_request_id"] = client_id
    item["comparison_fingerprint"] = fingerprint
    return "design.requested", {"round_id": item["round_id"], "request_id": item["context"]["request_id"],
                                "client_request_id": client_id}, ("plan", item["round_id"], item["generation_epoch"])
