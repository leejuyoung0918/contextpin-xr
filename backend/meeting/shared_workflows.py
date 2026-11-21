"""Small room collaboration state, guarded previews and continuation of saved work."""
import copy
import math
import re
from .contracts import MeetingError, digest, now_ms, text_field, uid

COMMANDS = {"design_request", "design_target", "pointer", "edit_begin", "edit_preview", "edit_renew", "edit_cancel", "edit_commit", "verify"}
LEASE_MS = 90_000


def initialize(state):
    if not state.get("assembly"):
        return
    state.setdefault("edit_draft", None)
    state.setdefault("pointers", {})
    state.setdefault("verification_records", [])
    state.setdefault("workspace", {
        "coordinate_system": "object_2_mount", "units": "m",
        "physical_original_version_id": state["original_version_id"],
        "physical_position_m": [0, 0, 0], "active_offset_m": [0, 0, .14],
        "candidate_offsets_m": [[-.15, 0, .15], [0, 0, .25], [.15, 0, .15]],
        "rotation_xyzw": [0, 0, 0, 1],
    })


def expire(coordinator, state):
    draft = state.get("edit_draft")
    if draft and draft["expires_at"] <= now_ms():
        clear(coordinator, state, "편집 시간이 만료되었습니다.")


def clear(coordinator, state, reason):
    draft = state.get("edit_draft")
    if draft and draft.get("round_id") and draft.get("generated_preview"):
        item = coordinator.round(state, draft["round_id"])
        if item["status"] in {"collecting", "generating", "building", "reviewable"}:
            coordinator.finish_round(item, "superseded", reason)
    state["edit_draft"] = None
    if draft:
        from .meeting_state import reset_preview
        reset_preview(state, now_ms())


def public_draft(state):
    draft = copy.deepcopy(state.get("edit_draft"))
    if not draft or draft["expires_at"] <= now_ms():
        return None
    item = next((r for r in state["rounds"] if r["round_id"] == draft.get("round_id")), None)
    candidate = next((c for c in (item or {}).get("candidates", []) if c["candidate_id"] == draft.get("candidate_id")), None)
    if candidate:
        status = {"valid": "ready", "invalid": "failed", "queued": "building"}.get(candidate["status"], candidate["status"])
        if item["status"] in {"failed", "superseded", "dismissed"}:
            status = "failed"
        draft.update(status=status, version_id=candidate.get("version_id"),
                     error=candidate.get("error") or item.get("error"), round_revision=item["revision"])
    return draft


def point(value, optional=False):
    if value is None and optional:
        return None
    if not isinstance(value, list) or len(value) != 3 or any(type(v) not in (float, int) or not math.isfinite(v) or abs(v) > 20 for v in value):
        raise MeetingError("공통 좌표의 위치 세 값을 확인하세요.")
    return value


def check_head(state, payload):
    if payload.get("expected_adopted_revision") != state["adopted_revision"]:
        raise MeetingError("현재 채택본이 변경되었습니다. 최신 상태를 다시 확인하세요.", 409)


def require_lease(state, actor, payload):
    draft = state.get("edit_draft")
    if not draft or draft["owner_id"] != actor or payload.get("lease_id") != draft["lease_id"]:
        raise MeetingError("현재 편집권을 가진 참가자만 조정할 수 있습니다.", 409)
    if draft["base_adopted_revision"] != state["adopted_revision"] or draft["committed_version_id"] != state["committed_version_id"]:
        raise MeetingError("편집 기준 채택본이 변경되었습니다.", 409)
    draft["expires_at"] = now_ms() + LEASE_MS
    return draft


def fields_for(state, candidate, version):
    changed = {op["parameter_id"] for op in candidate.get("operations", [])}
    fields = []
    for field in state["profile"]["parameters"]:
        key = field["parameter_id"]
        if key not in changed or field["type"] != "number":
            continue
        info = {k: field[k] for k in ("parameter_id", "label", "unit", "min_value", "max_value")}
        info.update(step=field.get("step", .025), current_value=version["parameters"][key])
        if key == "slot_pitch" and state["assembly"].get("fixed_parameters", {}).get("slot_count"):
            fixed = state["assembly"]["fixed_parameters"]
            gaps, margins = fixed["slot_count"] - 1, fixed["end_margin"] * 2
            info.update(parameter_id="body_length", label="전체 길이", step=.1,
                        min_value=gaps * field["min_value"] + margins,
                        max_value=gaps * field["max_value"] + margins,
                        current_value=gaps * version["parameters"][key] + margins)
        fields.append(info)
    return fields


def parameter_patch(state, parameters):
    values = dict(parameters)
    if "body_length" in values:
        length = values.pop("body_length")
        fixed = state["assembly"].get("fixed_parameters", {})
        if type(length) not in (int, float) or not math.isfinite(length) or fixed.get("slot_count", 0) <= 1:
            raise MeetingError("등록된 길이 계산 관계를 확인하세요.")
        pitch = (length - 2 * fixed["end_margin"]) / (fixed["slot_count"] - 1)
        if "slot_pitch" in values and abs(values["slot_pitch"] - pitch) > .000001:
            raise MeetingError("전체 길이와 홈 간격이 충돌합니다.")
        values["slot_pitch"] = pitch
    return values


# Only an affirmative directive or a concrete agreement concludes the discussion.
# Trailing particles and quoted/reported commands deliberately do not match.
COMPARE = re.compile(
    r"(?:수정\s*(?:안|한|본)|비교\s*[안한]|설계\s*후보|후보)(?:들)?(?:을|를|좀)?\s*(?:좀\s*)?"
    r"(?:보여\s*(?:줘요?|주세요|주자)|만들어\s*(?:줘요?|주세요|보자)|비교해\s*(?:줘요?|주세요|보자))$")
AGREEMENT = re.compile(r"(?:하기로|기로|하는\s*걸로|하는\s*것으로|하는\s*방향으로|(?:으)?로)\s*(?:합의|결정)(?:하자|했어|했어요|했습니다|하겠습니다|하죠)$")
JOINT_PROPOSAL = re.compile(
    r"^(?:그럼|그러면|좋아[, ]|합의한 대로).*(?:줄이자|줄여\s*보자|좁히자|좁혀\s*보자|"
    r"늘리자|늘려\s*보자|바꾸자|바꿔\s*보자|축소하자|로\s*하자)$")
EDIT = re.compile(r"길이|크기|치수|폭|직경|간격|곡선|곡률|두께|높이|깊이|입구|내부|홈|지름|좁|넓|줄이|줄여|늘리|키워|키우|밀리|mm", re.I)
DESIGN_SUBJECT = re.compile(r"길이|크기|치수|폭|직경|간격|곡선|두께|높이|깊이|입구|내부|홈|지름|케이블|정리대|충전선|부위|부분|결합|우측|좌측|오른쪽|왼쪽|모델|후보|여기|mm|밀리", re.I)
EDIT_REQUEST = re.compile(
    r"(?:키워|넓혀|늘려|줄여|좁혀|바꿔|높여|낮춰|확대해|축소해|수정해|작게\s*해|크게\s*해)"
    r"\s*(?:줘(?:요)?|주세요|주십시오|줬으면\s*좋겠(?:어|어요)|주면\s*좋겠(?:어|어요))$")
NEGATED = re.compile(r"(?:아직|나중에|하지\s*말|보여\s*주지|만들지|않기로|말기로|합의한\s*게\s*아니)")
MISHEARD_CABLE_FIT = re.compile(
    r"(?<![가-힣])(?:[충중]전\s*선|케이블(?:\s*홈)?|홈(?:\s*입구)?|입구|선)"
    r"(?:이|가|은|는|을|를|도)?\s*(?:(?:너무|좀|조금|많이|계속)\s*)*"
    r"(?:헬거워|헓꺼워|허일거워|홀거워)")


def mentions_loose_fit(text):
    if re.search(r"헐렁|헐거|헐겁|빠지|빠져", text):
        return True
    # Accept observed STT spellings only when the line identifies the cable or
    # groove. The original transcript remains the evidence for this request.
    return bool(MISHEARD_CABLE_FIT.search(text))


def cue_text(text):
    line = text.strip().rstrip(".!。！").strip()
    if NEGATED.search(line) or "?" in line:
        return None
    # Local Whisper also hears the standalone "수정안 보여 주세요" as this directive.
    # As with the original cue, dimensions must come from an actual design utterance.
    if re.fullmatch(r"수정해\s*보여\s*주세요", line):
        return ""
    command = COMPARE.search(line)
    if command:
        return line[:command.start()].strip(" ,.!·")
    directed_edit = EDIT_REQUEST.search(line) and DESIGN_SUBJECT.search(line)
    return line if (EDIT.search(line) or mentions_loose_fit(line)) and (
        AGREEMENT.search(line) or JOINT_PROPOSAL.match(line) or directed_edit) else None


def design_topic(item):
    text = item["text"]
    topic = cue_text(text)
    evidence = text if topic is None else topic
    if NEGATED.search(evidence):
        return False
    return mentions_loose_fit(evidence) or bool(DESIGN_SUBJECT.search(evidence) and re.search(
        r"키워|키우|넓|늘[려리]|확대|좁|축소|줄[여이일]|짧|크게|작게|크네|작네|곡선적|둥글게|높여|낮춰|바꿔|수정|조정|[0-9]\s*(?:mm|밀리)", evidence, re.I))


def saved_design_utterance(state):
    if state.get("session_status") != "completed":
        return None
    return latest_design_utterance(state)


def latest_design_utterance(state, since=0):
    if state.get("meeting", {}).get("mode") != "live":
        return None
    latest = {u["utterance_id"]: u for u in state["utterances"] if u["status"] == "final"}
    return max((u for u in latest.values() if u["end_ms"] >= since and design_topic(u)), key=lambda u: u["end_ms"], default=None)


def registered_scope(profile):
    part = (profile.get("edit_scope") or {}).get("part_id")
    mutable = {p for field in profile["parameters"] for p in field["affected_part_ids"]}
    if not isinstance(part, str) or mutable != {part} or part not in {p["part_id"] for p in profile["semantic_parts"]}:
        raise MeetingError("공통 수정 부위가 등록되지 않았습니다. 모델에서 수정할 부위를 선택하세요.", 409)
    return part


def request_from_utterance(coordinator, state, actor, utterance, stamp):
    if state.get("session_status") != "recording" or state["meeting"].get("mode") != "live":
        return None
    if state["meeting"].get("design_submit_mode") == "explicit":
        return None
    topic = cue_text(utterance["text"])
    if topic is None:
        return None
    when = utterance["end_ms"]
    floor = max(state["meeting"]["started_at"] or 0,
                max((d["committed_at"] for d in state["decisions"] if d["selected_model_version_id"]), default=0))
    if when < floor:
        return None
    targets = state.get("design_target_history", [])
    target = next((t for t in reversed(targets) if floor <= t["captured_at"] <= when), None)
    try:
        if not target or not target.get("part_id"):
            registered_scope(state["profile"])
            target = {"base_version_id": state["committed_version_id"],
                      "expected_adopted_revision": state["adopted_revision"], "use_registered_scope": True}
        check_head(state, target)
        selected = {}
        for u in state["utterances"]:
            if u["status"] == "final" and floor <= u["end_ms"] <= when:
                selected[u["utterance_id"]] = u
        recent = sorted(selected.values(), key=lambda u: u["start_ms"])[-6:]
        # A bare 'show options' is evidence of the request, never a design parameter.
        evidence = [u for u in recent if design_topic(u)]
        if not evidence:
            raise MeetingError("어떤 치수를 어떻게 바꿀지 말한 뒤 수정안을 요청하세요.")
        payload = {**target, "utterance_ids": [u["utterance_id"] for u in evidence[-5:]],
                   "_trigger_id": utterance["utterance_id"], "_trigger_at": when}
        from .geometry import GeometryError
        trial = copy.deepcopy(state)
        coordinator.db.execute("SAVEPOINT spoken_comparison")
        try:
            _, data, job = apply(coordinator, trial, actor, "design_request", payload, stamp)
        except (MeetingError, GeometryError, OSError) as error:
            coordinator.db.execute("ROLLBACK TO spoken_comparison")
            raise MeetingError(str(error) if isinstance(error, MeetingError) else "CAD 자료를 읽지 못했습니다. 대화는 기록했습니다.") from error
        finally:
            coordinator.db.execute("RELEASE spoken_comparison")
        state.update(trial)
        utterance["round_id"] = data["round_id"]
        state["utterances"][-1]["round_id"] = data["round_id"]
        state["speech_intent_notice"] = "요청을 접수했습니다. 생성된 수정안을 비교하세요."
        return job
    except MeetingError as error:
        state["speech_intent_notice"] = utterance["processing_notice"] = str(error)
        return None


def apply(coordinator, state, actor, kind, payload, stamp):
    if not state.get("assembly"):
        raise MeetingError("조립체 회의에서 사용할 수 있습니다.", 409)
    initialize(state)
    expire(coordinator, state)
    if kind == "pointer":
        version = coordinator.version(state, payload.get("version_id"))
        part = payload.get("part_id")
        if part and part not in {p["part_id"] for p in state["profile"]["semantic_parts"]}:
            raise MeetingError("등록된 부위를 선택하세요.")
        tracking = payload.get("tracking_state", "limited")
        if tracking not in {"tracking", "limited", "lost", "desktop_view", "local_preview"}:
            raise MeetingError("추적 상태를 확인하세요.")
        value = {"participant_id": actor, "version_id": version["version_id"], "part_id": part,
                 "hit_point_asset_m": point(payload.get("hit_point_asset_m"), optional=tracking in {"lost", "limited"}),
                 "workspace_position_m": None if tracking == "local_preview" else point(payload.get("workspace_position_m"), optional=True),
                 "tracking_state": tracking, "updated_at": now_ms()}
        state["pointers"][actor] = value
        return "pointer.updated", value, None
    if kind == "verify":
        record = {"record_id": uid("verification"), "participant_id": actor, "timestamp": now_ms(),
                  "source": "participant_input", "version_id": state["committed_version_id"]}
        for key in ("target", "method", "dimensions", "next_action"):
            record[key] = text_field(payload, key, 600)
        for key in ("elapsed_ms", "additional_questions"):
            value = payload.get(key)
            if value is not None and (type(value) is not int or value < 0):
                raise MeetingError("직접 측정한 시간과 질문 횟수를 입력하세요.")
            record[key] = value
        state["verification_records"].append(record)
        job = ("result", "", 0) if state.get("session_status") == "completed" else None
        return "verification.recorded", record, job
    if kind in {"design_target", "design_request"}:
        if state.get("meeting", {}).get("design_submit_mode") == "explicit":
            from .explicit_design import apply_selection
            return apply_selection(coordinator, state, actor, kind, payload, stamp)
        if payload.get("reference_selection") is not None or payload.get("client_request_id") is not None:
            raise MeetingError("이 회의는 명시적 수정 요청 모드가 아닙니다.", 409)
        check_head(state, payload)
        if state.get("edit_draft"):
            raise MeetingError("현재 치수 조정을 완료하거나 취소한 뒤 요청하세요.", 409)
        base = coordinator.version(state, payload.get("base_version_id"))
        part = payload.get("part_id")
        profile_scope = kind == "design_request" and payload.get("use_registered_scope") is True and not part
        if profile_scope:
            if base["version_id"] != state["committed_version_id"] or any(payload.get(k) is not None for k in ("hit_point_asset_m", "image_id")):
                raise MeetingError("등록된 범위는 현재 채택본에 적용됩니다. 모델을 다시 확인하세요.", 409)
            part = registered_scope(state["profile"])
        if (part is not None or kind != "design_target") and part not in {p["part_id"] for p in state["profile"]["semantic_parts"]}:
            raise MeetingError("수정할 모델의 부위를 선택하세요.")
        image_id = payload.get("image_id")
        image = next((i for i in state["images"] if i["evidence_id"] == image_id), None)
        if image_id and (not image or image["version_id"] != base["version_id"] or
                         (image.get("part_ids") and part not in image["part_ids"])):
            raise MeetingError("선택한 모델·부위와 사진이 일치하지 않습니다.", 409)
        tracking = payload.get("tracking_state", "unreported")
        if tracking not in {"tracking", "local_preview", "desktop_view", "unreported", "limited", "lost"}:
            raise MeetingError("선택한 모델의 표시 상태를 확인하세요.")
        if kind == "design_target":
            target = {"base_version_id": base["version_id"], "expected_adopted_revision": state["adopted_revision"],
                      "part_id": part, "hit_point_asset_m": point(payload.get("hit_point_asset_m"), optional=True),
                      "image_id": image_id, "tracking_state": tracking, "participant_id": actor, "captured_at": stamp["captured_session_time_ms"]}
            history = state.setdefault("design_target_history", [])
            history.append(target)
            history.sort(key=lambda t: t["captured_at"])
            del history[:-32]
            state["design_target"] = history[-1]
            return "design.target_selected", target, None
        ids = payload.get("utterance_ids", [])
        if not isinstance(ids, list) or len(ids) > 5 or any(type(i) is not str for i in ids):
            raise MeetingError("최대 다섯 개의 확정 발화를 선택하세요.")
        saved_line = payload.get("_saved_utterance")
        lines = [saved_line] if saved_line else state["utterances"]
        latest = {u["utterance_id"]: u for u in lines if u["status"] == "final"}
        if not ids:
            ids = list(latest)[-5:]
        if not ids or any(i not in latest for i in ids):
            raise MeetingError("먼저 수정 요청을 말하거나 직접 입력하세요.")
        utterances = [latest[i] for i in ids]
        # A client can explicitly submit the same discussion while speech is being processed.
        # Reusing the last substantive utterance makes both entry points idempotent.
        substantive = [u for u in utterances if design_topic(u)]
        if profile_scope and not substantive:
            raise MeetingError("수정할 치수나 케이블 홈의 문제를 먼저 말하거나 입력하세요.", 409)
        main = substantive[-1] if substantive else utterances[-1]
        fingerprint = digest([state["adopted_revision"], base["version_id"], part,
                              main["utterance_id"], main["revision"]])
        for previous in reversed(state["rounds"]):
            if previous.get("comparison_fingerprint") == fingerprint and previous["status"] in {
                    "collecting", "generating", "building", "reviewable", "needs_input"}:
                return "design.request_reused", {"round_id": previous["round_id"]}, None
        view = {**stamp, "evidence_id": uid("view"), "source_mode": "manual", "revision": 1,
                "participant_id": actor, "version_id": base["version_id"], "part_ids": [part],
                "device_id": next(p["device_id"] for p in state["participants"] if p["participant_id"] == actor),
                "candidate_id": base.get("created_from_candidate_id"), "candidate_versions": {},
                "display_order_candidate_ids": [], "hit_point_asset_m": point(payload.get("hit_point_asset_m"), optional=True),
                "image_id": image_id, "tracking_state": tracking}
        if profile_scope:
            view.update(source_mode="profile_scope", tracking_state="untracked", participant_id=None, device_id=None)
        elif payload.get("_saved_selection"):
            target = payload["_saved_selection"]
            view.update(source_mode="saved_selection", tracking_state=target.get("tracking_state", "unreported"),
                        source_session_id=saved_line["session_id"], participant_id=target["participant_id"],
                        captured_session_time_ms=target["captured_at"], device_id=None)
        item = coordinator.attach_context(state, main, request_view=view,
                                          request_base=base["version_id"], request_actor=actor)
        item["context"]["selected_utterances"] = copy.deepcopy(utterances)
        item["context"]["source_utterance_ids"] = ids
        if saved_line:
            item["context"]["source_session_id"] = saved_line["session_id"]
        item["context"]["evidence_ids"] = list(dict.fromkeys([*item["context"]["evidence_ids"], *ids]))
        item["context"]["requested_at"] = payload.get("_trigger_at", now_ms())
        item["context"]["request_user_id"] = actor
        item["context"]["request_actor_id"] = actor
        item["comparison_fingerprint"] = fingerprint
        item["context"]["trigger_utterance_id"] = payload.get("_trigger_id")
        item["context"]["context_hash"] = digest({k:v for k,v in item["context"].items() if k != "context_hash"})
        coordinator.db.execute("UPDATE contexts SET body=? WHERE session_id=? AND context_id=?",
                               (coordinator_json(item["context"]), state["session_id"], item["context"]["context_id"]))
        return "design.requested", {"round_id": item["round_id"], "request_id": item["context"]["request_id"]}, ("plan", item["round_id"], item["generation_epoch"])
    if kind == "edit_begin":
        check_head(state, payload)
        if state.get("edit_draft"):
            raise MeetingError("다른 치수 조정이 진행 중입니다.", 409)
        item = coordinator.round(state, payload.get("round_id"))
        candidate = coordinator.candidate(item, payload.get("candidate_id"))
        if item["status"] != "reviewable" or candidate["status"] != "valid" or item["expected_committed_version_id"] != state["committed_version_id"]:
            raise MeetingError("현재 검토 가능한 후보를 선택하세요.", 409)
        version = coordinator.version(state, candidate["version_id"])
        if payload.get("base_version_id") != version["version_id"]:
            raise MeetingError("보고 있는 후보와 편집 대상이 다릅니다.", 409)
        state["edit_draft"] = {"lease_id": uid("edit"), "owner_id": actor, "expires_at": now_ms() + LEASE_MS,
            "base_version_id": version["version_id"], "committed_version_id": state["committed_version_id"],
            "base_adopted_revision": state["adopted_revision"], "preview_revision": 0,
            "round_id": item["round_id"], "candidate_id": candidate["candidate_id"], "version_id": version["version_id"],
            "original_round_id": item["round_id"], "original_candidate_id": candidate["candidate_id"],
            "parameters": copy.deepcopy(version["parameters"]), "fields": fields_for(state, candidate, version),
            "status": "valid", "generated_preview": False}
        return "edit.started", {"edit_draft": public_draft(state)}, None
    draft = require_lease(state, actor, payload)
    if kind == "edit_renew":
        return "edit.renewed", {"expires_at": draft["expires_at"]}, None
    if kind == "edit_cancel":
        clear(coordinator, state, "참가자가 치수 조정을 취소했습니다.")
        return "edit.cancelled", {"lease_id": draft["lease_id"]}, None
    if kind == "edit_preview":
        revision = payload.get("preview_revision")
        if type(revision) is not int or revision <= draft["preview_revision"]:
            raise MeetingError("이전 미리보기 요청입니다.", 409)
        values = payload.get("parameters")
        allowed = {f["parameter_id"] for f in draft["fields"]}
        if not isinstance(values, dict) or not values or set(values) - allowed:
            raise MeetingError("현재 수정안의 슬라이더 값만 조정하세요.")
        base_parameters = coordinator.version(state, draft["base_version_id"])["parameters"]
        desired = {**draft["parameters"], **parameter_patch(state, values)}
        params = {k:v for k,v in desired.items() if v != base_parameters[k]}
        old = coordinator.round(state, draft["round_id"])
        if draft["generated_preview"] and old["status"] in {"collecting", "generating", "building", "reviewable"}:
            coordinator.finish_round(old, "superseded", "더 최근의 치수 조정이 들어왔습니다.")
        if not params:
            draft.update(preview_revision=revision, round_id=draft["original_round_id"],
                         candidate_id=draft["original_candidate_id"], version_id=draft["base_version_id"],
                         parameters=desired, status="ready", generated_preview=False)
            for field in draft["fields"]:
                if field["parameter_id"] in values:
                    field["current_value"] = values[field["parameter_id"]]
            return "edit.preview_ready", {"edit_draft": public_draft(state)}, None
        _, result, job = coordinator.apply(state, actor, "edit", {"base_version_id": draft["base_version_id"], "parameters": params, "_lease_authorized": draft["lease_id"]}, stamp)
        item = coordinator.round(state, result["round_id"])
        item["preview_revision"] = revision
        item["adjusted_from_round_id"] = draft["original_round_id"]
        item["edit_lease_id"] = draft["lease_id"]
        draft.update(preview_revision=revision, round_id=item["round_id"], candidate_id=item["candidates"][0]["candidate_id"],
                     version_id=None, status="queued", generated_preview=True,
                     parameters=desired)
        for field in draft["fields"]:
            if field["parameter_id"] in values:
                field["current_value"] = values[field["parameter_id"]]
        return "edit.preview_requested", {"edit_draft": public_draft(state)}, job
    if kind == "edit_commit":
        rendered = public_draft(state)
        if payload.get("preview_revision") != draft["preview_revision"] or rendered["status"] != "ready" or payload.get("displayed_version_id") != rendered["version_id"]:
            raise MeetingError("최신 치수의 모델 표시가 완료된 뒤 채택하세요.", 409)
        item = coordinator.round(state, draft["round_id"])
        result = coordinator.decide(state, actor, {"round_id": item["round_id"], "revision": item["revision"],
            "expected_committed_version_id": state["committed_version_id"], "expected_adopted_revision": state["adopted_revision"],
            "candidate_id": draft["candidate_id"], "outcome": "commit", "reason": payload.get("reason"),
            "reason_utterance_id": payload.get("reason_utterance_id"), "_lease_authorized": draft["lease_id"]})
        result["slider_adjustment"] = {"preview_revision": draft["preview_revision"], "base_candidate_version_id": draft["base_version_id"],
                                       "parameters": draft["parameters"], "actor_id": actor}
        state["edit_draft"] = None
        return "edit.committed", result, None
    raise MeetingError("지원하지 않는 편집 명령입니다.")


def coordinator_json(value):
    from .contracts import canonical
    return canonical(value)
