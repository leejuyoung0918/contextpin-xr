"""Context compiler, fixture and live planner adapters, and proposal validation."""
import json
import os
import re
import urllib.error
import urllib.request
from pathlib import Path
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, ValidationError

from .contracts import MeetingError, digest, uid


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


IDENTITY = ("request_id", "context_hash", "edit_base_version_id",
            "expected_committed_version_id", "generation_epoch")
SYSTEM = Path(__file__).with_name("planner-v1.txt").read_text()
NUMBER = re.compile(r"(-?\d+(?:\.\d+)?)\s*(?:mm|밀리미터)", re.IGNORECASE)
DEICTIC = ("이 부분", "여기", "이 부위")
ORDINAL = re.compile(r"([123])\s*(?:번째|번\s*안)|(첫|두|세)\s*번째")


def contains(text, aliases):
    return any(alias.lower() in text.lower() for alias in aliases if alias)


def explicit_parameters(profile, line):
    fields = profile["parameters"]
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
    if not ordinal:
        return view.get("candidate_id"), view["version_id"]
    index = int(ordinal.group(1)) if ordinal.group(1) else {"첫": 1, "두": 2, "세": 3}[ordinal.group(2)]
    order = view.get("display_order_candidate_ids", [])
    candidate_id = order[index - 1] if index <= len(order) else None
    return candidate_id, view.get("candidate_versions", {}).get(candidate_id)


def compile_context(state, utterance, view, version, round_id, epoch, previous=None):
    profile = state["profile"]
    params = version["parameters"]
    inherited = version.get("request_constraints", []) if version["version_id"] != state["committed_version_id"] else []
    active = [item for item in state["constraints"] if item["status"] == "active"]
    line = utterance["text"]
    locked = {item["property_id"]: item for item in [*inherited, *active]}
    request_constraints = {item["property_id"]: item for item in inherited}
    # Keep a request's observed physical dimension, rather than just its parameter.
    for prop in profile.get("measured_properties", []):
        key = prop["property_id"]
        aliases = [prop["label"], *prop.get("aliases", [])]
        if key in version["measurements"] and key not in locked and any(
            re.search(re.escape(alias) + r"(?:은|는|을|를)?\s*(?:그대로|유지|변경하지|바꾸지)", line)
            for alias in aliases
        ):
            item = {"constraint_id": uid("constraint"), "property_id": key, "operator": "eq",
                    "value": version["measurements"][key], "unit": prop["unit"], "tolerance": 0.001,
                    "scope": "request", "status": "active", "strength": "hard", "source_kind": "explicit_utterance",
                    "reference_model_version_id": version["version_id"], "evidence_refs": [utterance["utterance_id"]]}
            locked[key] = request_constraints[key] = item
    choice_parts = view.get("part_ids", []) if view and view["version_id"] == version["version_id"] else []
    named_parts = [part["part_id"] for part in profile["semantic_parts"]
                   if contains(line, [part["label"], *part.get("aliases", [])])]
    parts = choice_parts if contains(line, DEICTIC) else named_parts or choice_parts
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
    semantic = {**body, "utterance": utterance["evidence_id"] + ":" + str(utterance["revision"]),
                "related_utterances": [[item["evidence_id"], item["revision"]] for item in related],
                "constraints": [{k: v for k, v in item.items() if k != "constraint_id"}
                                for item in locked.values()],
                "request_constraints": list(request_constraints)}
    return {**body, "context_id": uid("context"), "context_hash": digest(semantic),
            "round_id": round_id, "request_id": uid("req"), "generation_epoch": epoch}


def fixture_plan(context):
    line = context["utterance"]["text"]
    combined = (context["clarification_of"] + " " + line).strip()
    profile = context["profile"]
    fields = profile["parameters"]
    params = context["parameters"]
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
    if contains(combined, profile.get("demo_ambiguity_aliases", [])):
        by_id = {f["parameter_id"]: f for f in fields}
        for entry in profile.get("demo_interpretations", []):
            key = entry["parameter_id"]
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
    if context.get("reference_missing") or (
            contains(context["utterance"]["text"], DEICTIC) and context["target_resolution"] == "missing"):
        raw = fixture_plan(context)
        raw.update(action="ask_clarification", options=[],
                   question=raw["question"] or "발화 당시의 대상을 선택해 주세요.")
        return Plan.model_validate(raw).model_dump()
    try:
        raw = fixture_plan(context) if mode == "fixture" else gateway_request("/v1/plans", context)
        plan = Plan.model_validate(raw)
    except (ValidationError, urllib.error.URLError, OSError, ValueError) as error:
        raise MeetingError("후보 계획을 받지 못했습니다. 수동 치수 수정은 계속할 수 있습니다.", 502) from error
    if any(getattr(plan, key) != context[key] for key in IDENTITY):
        raise MeetingError("다른 요청의 응답을 받았습니다.")
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
    for option in plan.options:
        for op in option.operations:
            if contains(context["utterance"]["text"], DEICTIC) and op.part_id not in context["part_ids"]:
                raise MeetingError("발화 당시 지정한 부위와 후보의 적용 부위가 다릅니다.")
            if mode == "live" and op.source == "EXPLICIT_USER" and op.value not in {float(v) for v in spoken}:
                op.source = "AI_PROPOSAL"
    return plan.model_dump()
