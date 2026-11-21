"""Context compiler, fixture and live planner adapters, and proposal validation."""
import json
import copy
import math
import os
import re
import urllib.error
import urllib.request
from pathlib import Path
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, ValidationError

from .contracts import MeetingError, digest, uid
from .parameter_relations import derived_fields, effective_values, normalize_patch, validate_relations


class StrictModel(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True, allow_inf_nan=False)


class Operation(StrictModel):
    operation_id: Literal["set_parameter"]
    part_id: str
    parameter_id: str
    value: float | int | str
    unit: str
    source: Literal["EXPLICIT_USER", "AI_PROPOSAL"]


class Option(StrictModel):
    title: str = Field(min_length=1, max_length=90)
    interpretation: str = Field(min_length=1, max_length=300)
    operations: list[Operation] = Field(min_length=1, max_length=10)


class SummaryPatch(StrictModel):
    discussion: str = Field(max_length=100)


class Plan(StrictModel):
    schema_version: Literal[1]
    request_id: str
    context_hash: str
    edit_base_version_id: str
    expected_committed_version_id: str
    generation_epoch: int
    action: Literal["no_op", "answer_question", "propose_preview", "propose_candidates",
                    "ask_clarification", "resolve_decision_request", "unsupported"]
    options: list[Option] = Field(max_length=3)
    question: str | None = Field(max_length=300)
    evidence_ids: list[str] = Field(max_length=20)
    summary_patch: SummaryPatch | None = None
    context_version: int | None = None


IDENTITY = ("request_id", "context_hash", "edit_base_version_id",
            "expected_committed_version_id", "generation_epoch")
SYSTEM = Path(__file__).with_name("planner-v1.txt").read_text()
NUMBER = re.compile(r"(-?\d+(?:\.\d+)?)\s*(?:mm|밀리미터)", re.IGNORECASE)
DEICTIC = ("이 부분", "여기", "이 부위")
ORDINAL = re.compile(r"([123])\s*(?:번째|번\s*안)|(첫|두|세)\s*번째")
CANDIDATE_LETTER = re.compile(r"(?<![A-Za-z])([ABC])(?![A-Za-z])", re.I)


def assembly_remark(line):
    return bool(re.search(r"했었|말했지|얘기했지|어땠|했다면|했으면|좋네|좋아 보이|괜찮네", line)
                and not re.search(r"바꿔|수정해|바꾸자|해보자|더 둥글게|더 곡선적", line))


def contains(text, aliases):
    return any(alias.lower() in text.lower() for alias in aliases if alias)


def explicit_parameters(profile, line):
    fields = [*profile["parameters"], *derived_fields(profile)]
    # Prefer the most specific phrase when names overlap, e.g. 바닥 폭 and 폭.
    spans = []
    for field in fields:
        for alias in [field["parameter_id"], field["label"], *field.get("aliases", [])]:
            spans += [(match.start(), match.end(), field["parameter_id"])
                      for match in re.finditer(re.escape(alias), line, re.IGNORECASE)]
    return {key for start, end, key in spans
            if not any(a <= start and end <= b and end - start < b - a for a, b, _ in spans)}


def resolve_reference(utterance, view):
    line = utterance["text"]
    if not view:
        return None, None
    ordinal = ORDINAL.search(line)
    letter = CANDIDATE_LETTER.search(line)
    if not ordinal and not letter:
        return view.get("candidate_id"), view["version_id"]
    if ordinal:
        index = int(ordinal.group(1)) if ordinal.group(1) else {"첫": 1, "두": 2, "세": 3}[ordinal.group(2)]
    else:
        index = ord(letter.group(1).upper()) - ord("A") + 1
    order = view.get("display_order_candidate_ids", [])
    candidate_id = order[index - 1] if index <= len(order) else None
    return candidate_id, view.get("candidate_versions", {}).get(candidate_id)


def compile_context(state, utterance, view, version, round_id, epoch, previous=None, selected_lines=None):
    profile = state["profile"]
    params = version["parameters"]
    inherited = version.get("request_constraints", []) if version["version_id"] != state["committed_version_id"] else []
    active = [item for item in state["constraints"] if item["status"] == "active"]
    line = "\n".join(u["text"] for u in selected_lines) if selected_lines is not None else utterance["text"]
    locked = {item["property_id"]: item for item in [*inherited, *active]}
    request_constraints = {item["property_id"]: item for item in inherited}
    # Keep a request's observed physical dimension, rather than just its parameter.
    for prop in profile.get("measured_properties", []):
        key = prop["property_id"]
        aliases = [prop["label"], *prop.get("aliases", [])]
        if key not in version["measurements"] or key in locked:
            continue
        keep = re.compile("(?:" + "|".join(map(re.escape, aliases)) + r")(?:은|는|을|를)?\s*(?:그대로|유지|변경하지|바꾸지)")
        sources = [u["utterance_id"] for u in (selected_lines or [utterance]) if keep.search(u["text"])]
        if sources:
            item = {"constraint_id": uid("constraint"), "property_id": key, "operator": "eq",
                    "value": version["measurements"][key], "unit": prop["unit"], "tolerance": 0.001,
                    "scope": "request", "status": "active", "strength": "hard", "source_kind": "explicit_utterance",
                    "reference_model_version_id": version["version_id"], "evidence_refs": sources}
            locked[key] = request_constraints[key] = item
    choice_parts = view.get("part_ids", []) if view and view["version_id"] == version["version_id"] else []
    named_parts = [part["part_id"] for part in profile["semantic_parts"]
                   if contains(line, [part["label"], *part.get("aliases", [])])]
    parts = choice_parts if selected_lines is not None or contains(line, DEICTIC) else named_parts or choice_parts
    if selected_lines is not None:
        related = selected_lines[:-1]
    else:
        latest_related = {item["utterance_id"]: item for item in state["utterances"] if item["utterance_id"] != utterance["utterance_id"]
                          and 0 <= utterance["end_ms"] - item["end_ms"] <= 30_000}
        related = sorted(latest_related.values(), key=lambda item: item["end_ms"])[-6:]
    if previous:
        related = [previous["utterance"], *[item for item in related
                   if item["utterance_id"] != previous["utterance"]["utterance_id"]]][:6]
    answer_to = previous["utterance"]["text"] if previous else ""
    evidence = [utterance["utterance_id"], *[item["utterance_id"] for item in related]]
    images = [image["evidence_id"] for image in state.get("images", [])
              if image["participant_id"] == utterance["participant_id"]
              and image["version_id"] == version["version_id"]
              and 0 <= utterance["end_ms"] - image["captured_session_time_ms"] <= 5000][-2:]
    reference = (view or {}).get("reference_selection")
    if reference:
        images = [reference["image_id"]]
    if view:
        evidence.append(view["evidence_id"])
    evidence.extend(images)
    body = {
        "schema_version": 1, "session_id": state["session_id"], "object_id": profile["object_id"],
        "edit_base_version_id": version["version_id"], "expected_committed_version_id": state["committed_version_id"],
        "constraint_revision": state["constraint_revision"], "profile": profile,
        "parameters": params, "measurements": version["measurements"], "utterance": utterance,
        "related_utterances": related, "view": view, "part_ids": parts,
        "readiness": "READY" if profile["capabilities"]["can_parametric_edit"] else "UNSUPPORTED",
        "target_resolution": "resolved" if parts else "missing" if contains(line, DEICTIC) else "object",
        "constraints": list(locked.values()), "request_constraints": list(request_constraints.values()),
        "evidence_ids": evidence, "clarification_of": answer_to, "images_used": False, "related_image_ids": images,
        "relevant_decisions": state["decisions"][-3:],
    }
    if state.get("assembly"):
        from .openscad_geometry import adapter
        model = adapter()
        if model.describe()["source_hash"] != version["source_hash"]:
            raise MeetingError("등록된 CAD 원본이 바뀌었습니다. 해당 원본 설정으로 회의를 열어 주세요.")
        image_version = reference["reference_version_id"] if reference else version["version_id"]
        selected_image_id = reference["image_id"] if reference else (view or {}).get("image_id")
        selected_image = next((image for image in state.get("images", [])
                               if selected_image_id and selected_image_id in {image.get("evidence_id"), image.get("asset_id")}
                               and image.get("version_id") == image_version), None)
        body.update(request_actor_id=utterance["participant_id"],
                    scad_code=model.resolve(params).decode("utf-8"),
                    effective_parameters=effective_values(profile, params),
                    fixed_parameters=model.fixed_parameters,
                    parameter_relations=derived_fields(profile),
                    selection_snapshot=copy.deepcopy(view),
                    selected_position_model_m=(view or {}).get("hit_point_asset_m"),
                    screenshot=selected_image,
                    screenshot_status="captured" if selected_image else "unavailable",
                    edit_scope=profile.get("edit_scope"))
        adopted = next(model for model in state["versions"] if model["version_id"] == state["committed_version_id"])
        candidates = next((item for item in reversed(state["rounds"]) if item["status"] == "reviewable" or
                           (view and item["round_id"] == view.get("round_id"))), {})
        parameters_by_id = {model["version_id"]: model["parameters"] for model in state["versions"]}
        body.update(context_version=state["design_revision"], adopted_version_id=adopted["version_id"],
                    adopted_parameters=adopted["parameters"], source_version=version["source_hash"],
                    preview_candidate_id=(view or {}).get("candidate_id"), candidate_set_id=candidates.get("candidate_set_id"),
                    active_candidates=[{"candidate_id": candidate["candidate_id"], "interpretation": candidate["interpretation"],
                                        "parameters": parameters_by_id.get(candidate["version_id"])}
                                       for candidate in candidates.get("candidates", []) if candidate["status"] == "valid"],
                    unresolved_question=next((item.get("question") for item in reversed(state["rounds"])
                                              if item["status"] == "needs_input"), None))
    if selected_lines is not None:
        body.update(request_transcript=line, selected_utterances=selected_lines,
                    source_utterance_ids=[u["utterance_id"] for u in selected_lines],
                    source_utterance_refs=[{k: u[k] for k in ("utterance_id", "revision")} for u in selected_lines],
                    reference_selection=reference,
                    reference_mapping="same_model" if version["version_id"] == reference["reference_version_id"] else "semantic_part_only")
    semantic = {**body, "utterance": utterance["evidence_id"] + ":" + str(utterance["revision"]),
                "related_utterances": [[item["evidence_id"], item["revision"]] for item in related],
                "constraints": [{k: v for k, v in item.items() if k != "constraint_id"}
                                for item in locked.values()],
                "request_constraints": list(request_constraints)}
    return copy.deepcopy({**body, "context_id": uid("context"), "context_hash": digest(semantic),
                          "round_id": round_id, "request_id": uid("req"), "generation_epoch": epoch})


def fixture_plan(context):
    return parameter_plan(context, allow_demo=True)


def parameter_plan(context, *, allow_demo=False):
    line = context["utterance"]["text"]
    combined = (context["clarification_of"] + " " + line).strip()
    profile = context["profile"]
    assembly = profile.get("assembly")
    fields = [*profile["parameters"], *derived_fields(profile)]
    params = effective_values(profile, context["parameters"])
    parts = context["part_ids"]
    result = {"schema_version": 1, **{k: context[k] for k in IDENTITY}, "action": "no_op",
              "question": None, "options": [], "evidence_ids": context["evidence_ids"]}

    def ask(question):
        return {**result, "action": "ask_clarification", "question": question}

    def option(field, value, source, title, description, part_id=None):
        key = field["parameter_id"]
        allowed = field["affected_part_ids"]
        if part_id not in allowed:
            part_id = next((part for part in parts if part in allowed), allowed[0])
        return {"title": title, "interpretation": description, "operations": [{
            "operation_id": "set_parameter", "part_id": part_id, "parameter_id": key, "value": value,
            "unit": field["unit"], "source": source,
        }]}

    if context["readiness"] == "UNSUPPORTED":
        return {**result, "action": "unsupported"}
    if assembly and assembly_remark(line):
        return result
    if assembly and re.search(r"(?:연결\s*부(?:분)?|결합\s*부(?:분)?|끼움\s*구조|고정\s*구멍)(?:을|를)?\s*(?:바꿔|바꾸|넓혀|좁혀|옮겨|키워|변경)", line):
        return ask("결합부는 고정되어 있습니다. 대신 바꿀 외형 요소를 알려 주세요.")
    if re.search(r"규칙.*무시|셸\s*코드|shell|실행\s*명령|삭제하지|높이지\s*말|바꾸지\s*말|변경하지\s*말", line, re.I):
        return result
    if re.search(r"결정하|채택하", line):
        return {**result, "action": "resolve_decision_request"}
    if context.get("reference_missing"):
        return ask("발화할 때 보고 계신 후보를 선택해 주세요.")
    if contains(combined, DEICTIC) and not parts:
        return ask("수정할 부위를 모델에서 선택하거나 이름을 알려 주세요.")
    explicit = explicit_parameters(profile, line) or explicit_parameters(profile, combined)
    locks = {item["property_id"]: item for item in context["constraints"]}
    if re.search(r"넓혀|좁혀|늘려|줄여|높여|낮춰", line):
        for prop in profile.get("measured_properties", []):
            key = prop["property_id"]
            if key not in locks or locks[key]["scope"] != "session":
                continue
            for alias in [prop["label"], *prop.get("aliases", [])]:
                if re.search(re.escape(alias) + r"(?:을|를|은|는)?\s*(?:조금\s*)?(?:더\s*)?(?:넓혀|좁혀|높여|낮춰|늘려|줄여)", line):
                    return ask(f'{prop["label"]} 유지 조건과 충돌합니다. 다른 치수를 지정하거나 진행자가 잠금을 해제해 주세요.')
    parts_filter = contains(combined, DEICTIC)
    values = NUMBER.findall(line) or NUMBER.findall(combined)
    if values:
        possible = [f for f in fields if f["parameter_id"] in explicit
                    and (not parts_filter or set(parts) & set(f["affected_part_ids"]))]
        if not possible and not explicit:
            possible = [f for f in fields if (not parts or set(parts) & set(f["affected_part_ids"]))
                        and contains(line, f.get("change_aliases", []))]
        if len(possible) > 1:
            unlocked = [f for f in possible if not re.search(
                re.escape(f["label"]) + r"(?:은|는|을)?\s*(?:유지|그대로)", combined)]
            possible = unlocked or possible
        if len(possible) != 1:
            return ask("어느 치수를 바꿀까요? 변경할 항목 하나를 지정해 주세요.")
        field = possible[0]
        key = field["parameter_id"]
        increment = bool(re.search(r"(?:mm|밀리미터)\s*(?:만큼|정도)?\s*(?:더\s*)?(?:넓혀|높여|늘려|좁혀|낮춰|줄여)", combined))
        sign = -1 if re.search(r"좁혀|낮춰|줄여", combined) else 1
        value = round(params[key] + sign * float(values[-1]), 6) if increment else float(values[-1])
        if key in locks and abs(value - locks[key]["value"]) > locks[key]["tolerance"]:
            return ask(f'{field["label"]} 유지 조건과 충돌합니다. 진행자가 잠금을 해제하거나 다른 치수를 지정해 주세요.')
        if not field["min_value"] <= value <= field["max_value"]:
            return ask(f'{field["label"]}의 등록 범위는 {field["min_value"]}–{field["max_value"]} {field["unit"]}입니다. 값을 다시 입력해 주세요.')
        if value == params[key]:
            return result
        return {**result, "action": "propose_preview", "options": [option(
            field, value, "EXPLICIT_USER", field["label"] + " 수정",
            f'{params[key]:g} → {value:g} {field["unit"]}')]}
    options = []
    refinement = bool(assembly and re.search(r"이\s*(?:후보|안)(?:에서)?", line) and re.search(r"더|조금", line))
    base_changes = context.get("changed_base_parameters", [])
    if refinement and not base_changes:
        return ask("조금 더 수정할 후보를 먼저 열어 주세요.")
    if allow_demo and (refinement or contains(combined, profile.get("demo_ambiguity_aliases", []))):
        by_id = {f["parameter_id"]: f for f in fields}
        for entry in profile.get("demo_interpretations", []):
            key = entry["parameter_id"]
            if refinement and key not in base_changes:
                continue
            if key in locks or (parts_filter and entry["part_id"] not in parts):
                continue
            field = by_id[key]
            if explicit and key not in explicit and not any(k in locks for k in explicit):
                continue
            value = min(field["max_value"], max(field["min_value"], params[key] + entry["delta"]))
            if value != params[key]:
                options.append(option(field, value, "AI_PROPOSAL", entry["title"],
                                      entry["interpretation"], entry["part_id"]))
        if options:
            return {**result, "action": "propose_candidates" if len(options) > 1 else "propose_preview",
                    "options": options[:3]}
    if re.search(r"바꿔|수정|넓혀|높여|낮춰|늘려|줄여|깊게|좁혀", combined):
        return ask("모델에서 변경할 항목을 지정하고 값과 단위를 알려 주세요.")
    return result


def registered_plan(context):
    """Interpret only the registered organizer dimensions. All models use the normal CAD build."""
    from .shared_workflows import cue_text, AGREEMENT, mentions_loose_fit
    line = context["utterance"]["text"]
    topic = cue_text(line)
    if topic:
        line = AGREEMENT.sub("", topic).strip()
    context = {**context, "utterance": {**context["utterance"], "text": line}}
    plan = parameter_plan(context)
    def ask(question):
        return {**plan, "action": "ask_clarification", "question": question, "options": []}
    profile = context["profile"]
    selected_parts = set(context["part_ids"])
    if selected_parts and not any(selected_parts.intersection(f["affected_part_ids"]) for f in profile["parameters"]):
        return ask("선택한 부위는 고정 영역입니다. 수정할 외형 부위를 선택하세요.")
    if profile.get("adapter_id") != "openscad_cable_organizer" or plan["action"] == "unsupported":
        return plan
    if re.search(r"그대로\s*(?:놔?두자|두세요|둡시다|두죠|둬요?|유지하자)[.!。！]?\s*$", line):
        return {**plan, "action": "no_op", "question": None, "options": []}
    # "선이 빠지지 않도록" specifies the desired fit. Only this cable-retention goal
    # is excluded from the veto check; a second clause may still forbid changing a dimension.
    directive = re.sub(r"(?:충전\s*선|케이블|선)(?:이|가|은|는)?\s*(?:(?:쉽게|잘|자꾸)\s*)?"
                       r"빠지지\s*않(?:도록|게)(?=\s|[,.!?。！]|$)", "", line)
    if re.search(r"지\s*(?:말|마|않)|마세요|말자|규칙.*무시", directive):
        return ask("유지할 치수와 실제로 바꿀 치수를 알려 주세요.")
    if NUMBER.search(line):
        return plan
    if re.search(r"결합|장착|바닥|받침|높이|깊이|우측|좌측|오른쪽|왼쪽", line):
        return ask("이 모델에서 변경할 수 있는 홈 입구·내부 지름 또는 전체 길이 중 어느 치수를 바꿀까요?")
    if plan["action"] in {"resolve_decision_request", "answer_question"}:
        return plan
    grow = bool(re.search(r"키워|키우|넓|늘[려리]|확대|크게", line))
    shrink = bool(re.search(r"좁|축소|줄[여이일]|짧|작게|줄자", line)) or mentions_loose_fit(line)
    if grow == shrink:
        return ask("어느 치수를 넓히거나 좁힐까요? 홈 입구, 내부 지름 또는 전체 길이를 지정해 주세요.")
    base = context["parameters"]
    fields = {f["parameter_id"]: f for f in profile["parameters"]}
    if re.search(r"길이|전체\s*크기", line):
        lengths = (60, 66, 84) if grow else (66, 60, 54)
        current = effective_values(profile, base)["body_length"]
        sets = [(f"전체 {length} mm", "이전 홈 치수를 유지하고 전체 길이를 조정합니다.",
                 normalize_patch(profile, {"body_length": length})) for length in lengths
                if (length > current if grow else length < current)]
    elif mentions_loose_fit(line) or re.search(r"입구|내부|안쪽|홈|직경|지름", line):
        # Ignore dimensions explicitly preserved by the speaker when interpreting the target.
        mutable = re.sub(r"(?:입구|내부|안쪽|지름|직경)(?:\s*(?:폭|공간|크기|직경|지름))?(?:은|는|을|를)?\s*(?:그대로|유지)", "", line)
        entrance = bool(re.search(r"입구", mutable))
        inside = bool(re.search(r"내부|안쪽|직경|지름", mutable))
        direction = "확대" if grow else "축소"
        alternatives = [("입구만", ["throat_width", "mouth_width"]), ("홈 내부만", ["groove_diameter"]),
                        ("입구와 내부", ["throat_width", "mouth_width", "groove_diameter"])]
        if entrance or inside:
            alternatives = [alternatives[2 if entrance and inside else 0 if entrance else 1]]
        # The step sizes are suggested comparisons. Always derive them from the currently viewed model.
        deltas = {"throat_width": 1.2, "mouth_width": 1.6, "groove_diameter": 1.6} if grow else {
            "throat_width": -.8, "mouth_width": -.8, "groove_diameter": -.8}
        sets = []
        for title, keys in alternatives:
            patch = {k: round(min(fields[k]["max_value"], max(fields[k]["min_value"], base[k] + deltas[k])), 3) for k in keys}
            if any(patch[k] == base[k] for k in keys):
                continue
            measurements = ", ".join(f'{fields[k]["label"]} {base[k]:g} → {patch[k]:g} mm' for k in keys)
            sets.append((f"{title} {direction}", measurements, patch))
    else:
        return ask("어느 치수를 바꿀까요? 홈 입구, 내부 지름 또는 전체 길이를 지정해 주세요.")
    options = []
    for title, description, patch in sets:
        try:
            validate_relations(profile, {**base, **patch})
        except MeetingError:
            continue
        ops = [{"operation_id": "set_parameter", "parameter_id": key, "part_id": fields[key]["affected_part_ids"][0],
                "value": value, "unit": fields[key]["unit"], "source": "AI_PROPOSAL"} for key, value in patch.items()]
        options.append({"title": title, "interpretation": description + " (비교용 제안값)", "operations": ops})
    if not options:
        return ask("현재 모델과 등록된 범위에서는 이 방향으로 바꿀 수 없습니다. 다른 치수를 지정해 주세요.")
    return {**plan, "action": "propose_candidates" if len(options) > 1 else "propose_preview", "question": None, "options": options}


def gateway_request(path, payload):
    base = os.environ.get("AI_GATEWAY_URL", "").rstrip("/")
    if not base:
        from .gateway import plan_live, transcribe_live
        return plan_live(payload) if path == "/v1/plans" else transcribe_live(payload)
    from urllib.parse import urlsplit
    authority = urlsplit(base)
    if authority.scheme != "https" and not (authority.scheme == "http" and authority.hostname in {"localhost", "127.0.0.1", "::1"}):
        raise MeetingError("원격 AI Gateway는 HTTPS 주소를 사용해야 합니다.")
    headers = {"Content-Type": "application/json"}
    if os.environ.get("AI_GATEWAY_TOKEN"):
        headers["Authorization"] = "Bearer " + os.environ["AI_GATEWAY_TOKEN"]
    request = urllib.request.Request(base + path, data=json.dumps(payload).encode(), headers=headers)
    with urllib.request.urlopen(request, timeout=50) as response:
        return json.loads(response.read(2 * 1024 * 1024))


def validated_plan(context, mode):
    if context.get("request_transcript"):
        # The evidence records retain their individual transcription and revision.
        context = {**context, "utterance": {**context["utterance"], "text": context["request_transcript"]}}
    scope = context.get("edit_scope") or {}
    if scope.get("individual_slot_editing") is False and re.search(
            r"(?:우측|좌측|오른쪽|왼쪽)\s*(?:상단|하단|위|아래)|(?:이|한)\s*홈만|(?:첫|두|세|네|다섯|\d+)\s*(?:번\s*째|번째|번)\s*(?:홈|구멍)|(?:왼쪽|오른쪽|가운데|중앙|마지막)\s*(?:홈|구멍)(?:만|을|의)", context["utterance"]["text"]):
        raw = parameter_plan(context)
        raw.update(action="ask_clarification", options=[],
                   question="이 모델의 홈 치수는 다섯 곳에 공통 적용됩니다. 홈 전체의 입구·내부 치수 또는 전체 길이를 바꿀까요?")
        return {**Plan.model_validate(raw).model_dump(), "proposal_provenance": {"source": "server_guard"}}
    if context["profile"].get("assembly") and assembly_remark(context["utterance"]["text"]):
        return {**Plan.model_validate(parameter_plan(context)).model_dump(),
                "proposal_provenance": {"source": "server_guard"}}
    if context.get("reference_missing") or (
            contains(context["utterance"]["text"], DEICTIC) and context["target_resolution"] == "missing"):
        raw = parameter_plan(context)
        raw.update(action="ask_clarification", options=[],
                   question=raw["question"] or "발화 당시의 대상을 선택해 주세요.")
        return {**Plan.model_validate(raw).model_dump(), "proposal_provenance": {"source": "server_guard"}}
    provenance = {"source": "fixture" if mode == "fixture" else "provider"}
    try:
        if mode == "live" and os.environ.get("MEETING_PLAN_BACKEND") == "registered":
            raw = registered_plan(context)
            provenance = {"source": "registered_rules", "model": "등록 치수 비교"}
        else:
            raw = fixture_plan(context) if mode == "fixture" else gateway_request("/v1/plans", context)
        raw = dict(raw)
        provenance.update(raw.pop("proposal_provenance", {}))
        plan = Plan.model_validate(raw)
    except MeetingError:
        raise
    except (ValidationError, urllib.error.URLError, OSError, ValueError) as error:
        raise MeetingError("후보 계획을 받지 못했습니다. 수동 치수 수정은 계속할 수 있습니다.", 502) from error
    if any(getattr(plan, key) != context[key] for key in IDENTITY):
        raise MeetingError("다른 요청의 응답을 받았습니다.")
    if plan.context_version is not None and plan.context_version != context.get("context_version"):
        raise MeetingError("다른 설계 문맥의 응답을 받았습니다.")
    if not set(plan.evidence_ids).issubset(context["evidence_ids"]):
        raise MeetingError("요청에 포함되지 않은 근거를 참조했습니다.")
    if plan.action == "propose_preview" and len(plan.options) != 1:
        raise MeetingError("명확한 수정에는 후보가 하나여야 합니다.")
    if plan.action == "propose_candidates" and len(plan.options) not in {2, 3}:
        raise MeetingError("비교 후보는 두세 개여야 합니다.")
    if plan.action not in {"propose_preview", "propose_candidates"} and plan.options:
        raise MeetingError("편집 이외의 응답에 연산이 포함되었습니다.")
    if plan.action == "ask_clarification" and not plan.question:
        raise MeetingError("확인 질문이 비어 있습니다.")
    spoken = NUMBER.findall(context["clarification_of"] + " " + context["utterance"]["text"])
    derived = {field["parameter_id"]: field for field in derived_fields(context["profile"])}
    for option in plan.options:
        changed = set()
        for op in option.operations:
            if (context.get("reference_selection") or contains(context["utterance"]["text"], DEICTIC)) and op.part_id not in context["part_ids"]:
                raise MeetingError("발화 당시 지정한 부위와 후보의 적용 부위가 다릅니다.")
            if mode == "live" and op.source == "EXPLICIT_USER" and op.value not in {float(v) for v in spoken}:
                op.source = "AI_PROPOSAL"
            if op.parameter_id in derived:
                field = derived[op.parameter_id]
                if op.unit != field["unit"] or op.part_id not in field["affected_part_ids"]:
                    raise MeetingError("계산 치수의 단위 또는 적용 부위가 다릅니다.")
                patch = normalize_patch(context["profile"], {op.parameter_id: op.value})
                op.parameter_id, op.value = next(iter(patch.items()))
            if op.parameter_id in changed:
                raise MeetingError("같은 후보에서 하나의 매개변수를 여러 번 변경했습니다.")
            changed.add(op.parameter_id)
        from .geometry import validate_patch, GeometryError
        try:
            final = validate_patch(context["profile"], context["parameters"],
                                   [operation.model_dump(exclude={"source"}) for operation in option.operations],
                                   context["constraints"])
        except GeometryError as error:
            raise MeetingError(str(error)) from error
        # A clearly named absolute length is checked after server-side conversion.
        for field in derived.values():
            aliases = [field["label"], *field.get("aliases", [])]
            match = next((found for alias in aliases if (found := re.search(
                re.escape(alias) + r"(?:을|를|은|는)?\s*(?:정확히\s*)?(-?\d+(?:\.\d+)?)\s*(?:mm|밀리미터)",
                context["utterance"]["text"], re.IGNORECASE))), None)
            incremental = re.search(r"(?:mm|밀리미터)\s*(?:만큼|정도)?\s*(?:더\s*)?(?:늘려|줄여)", context["utterance"]["text"])
            if match and not incremental:
                actual = effective_values(context["profile"], final)[field["parameter_id"]]
                if len(plan.options) != 1 or not math.isclose(actual, float(match.group(1)), abs_tol=1e-6):
                    raise MeetingError("명시한 길이와 AI 후보가 다릅니다. 요청한 수치를 그대로 적용해 다시 요청해 주세요.")
    validated = plan.model_dump()
    if provenance["source"] == "registered_rules":
        for option in validated["options"]:
            for operation in option["operations"]:
                if operation["source"] == "AI_PROPOSAL":
                    operation["source"] = "REGISTERED_COMPARISON"
    return {**validated, "proposal_provenance": provenance}
