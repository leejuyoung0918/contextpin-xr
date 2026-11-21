"""The cable organizer's two-stage rehearsal using registered SCAD parameters."""
import tempfile
from pathlib import Path

from . import geometry
from .contracts import MeetingError, now_ms, uid

KEY = "cable_organizer"
ENTRANCE = {"throat_width": 3.4, "mouth_width": 5.2}
SEAT = {"groove_diameter": 4.8}
FIRST = (
    ("입구만 축소", "홈 내부 직경을 유지하고 입구만 좁힙니다.", ENTRANCE),
    ("홈 내부만 축소", "입구 폭을 유지하고 홈 내부 직경을 줄입니다.", SEAT),
    ("입구와 내부 축소", "홈 입구와 내부 직경을 함께 줄입니다.", {**ENTRANCE, **SEAT}),
)
SECOND = tuple((f"전체 {length} mm", f"홈 다섯 개와 앞서 정한 입구·내부 치수를 유지합니다. 전체 길이 {length} mm.",
                {"slot_pitch": pitch}) for length, pitch in ((66, 12), (60, 10.5), (54, 9)))
INTRO = (
    ("A", "일단 만들어봤는데, 충전선을 한번 끼워볼까?"),
    ("B", "선이 너무 헐렁하게 들어가네. 좀 안 빠지게 수정해야겠다."),
    ("A", "그러게. 여기 좀 수정해 보자."),
)
LENGTH = (
    ("A", "전체적으로 크기가 좀 크네. 길이를 조금 줄여야겠다."),
    ("B", "대신 충전선 다섯 개는 그대로 정리할 수 있어야 해."),
)
FIRST_DECISION = (
    ("A", "나는 A처럼 입구를 좁혀서 선이 빠지지 않게 하는 걸 생각했어."),
    ("B", "나는 B처럼 홈 자체를 좁히는 걸 생각했는데."),
    ("A", "둘 다 줄이면 선을 끼우기 어려울 수도 있을 것 같아."),
    ("B", "그러면 내부 공간은 유지하고 입구만 좁혀보자."),
)
SECOND_DECISION = (
    ("A", "나는 C 정도로 줄이는 걸 생각했어."),
    ("B", "나는 A 정도면 충분하다고 생각했는데, C는 충전선끼리 너무 가까워질 것 같아."),
    ("A", "실제 크기로 보니까 그렇네. 그러면 중간 크기인 B로 하자."),
    ("B", "좋아. 선 사이 간격도 어느 정도 확보할 수 있겠네."),
)
HISTORY = (
    ("B", "우리 처음에 홈 자체를 줄이기로 했었나, 입구만 줄이기로 했었나?"),
    ("A", "두 수정 사항을 이력에서 확인하고 출력할 최종본을 정하자."),
)


def enabled(state):
    return (state.get("assembly") or {}).get("scripted_demo") == KEY


def adopted_stages(state):
    return [decision for decision in state["decisions"] if decision.get("cable_stage") and decision.get("selected_model_version_id")]


def presentation(state):
    if not enabled(state):
        return {}
    decisions = adopted_stages(state)
    stage = len(decisions) + 1
    pending = next((item for item in reversed(state["rounds"])
                    if item.get("cable_stage") == stage and item["status"] in {"building", "reviewable"}), None)
    history = [{"label": "V1", "version_id": state["original_version_id"], "what": "초기 시제품 · 84 mm", "why": None}]
    history_decisions = [decision for decision in state["decisions"] if decision.get("selected_model_version_id")]
    for idx, chosen in enumerate(history_decisions, start=2):
        history.append({"label": chosen.get("design_version", f"V{idx}"), "version_id": chosen["selected_model_version_id"],
                        "what": chosen["candidate_title"], "why": chosen["rationale_text"]})
    return {"stage": stage, "pending": bool(pending), "target_label": "V1의 케이블 홈" if stage == 1 else "가상 V2 모델",
            "action": "1차 · 홈 수정안 보기" if stage == 1 else "2차 · 길이 수정안 보기",
            "history": history}


def append_dialogue(coordinator, state, stage, dialogue, at):
    result = []
    for index, (speaker, line) in enumerate(dialogue):
        timestamp = at + index
        result.append(coordinator.utterance(state, line, timestamp, timestamp, None, "cable_script", "replay",
                                            speaker_label=speaker, script_scene=stage))
    return result


def prepare_round(coordinator, state, actor, stamp, payload):
    if coordinator.mode != "fixture" or state["meeting"]["mode"] != "replay" or state["session_status"] != "recording":
        raise MeetingError("케이블 데모는 예시 회의를 시작한 후 진행하세요.", 409)
    info = presentation(state)
    stage = info["stage"]
    if stage > 2:
        raise MeetingError("두 수정 단계가 끝났습니다. 설계 이력을 확인하거나 회의를 종료하세요.", 409)
    if info["pending"]:
        raise MeetingError("이미 준비 중이거나 검토 중인 수정안을 열어 주세요.", 409)
    version = coordinator.version(state, state["committed_version_id"])
    options = FIRST if stage == 1 else SECOND
    target = payload.get("target_part_id")
    if target is not None and (payload.get("target_version_id") != version["version_id"] or
        target not in {part["part_id"] for part in state["profile"]["semantic_parts"]} or
        (stage == 1 and target != "outer_shell")):
        raise MeetingError("현재 모델의 케이블 홈을 다시 지정해 주세요.", 409)
    fields = {field["parameter_id"]: field for field in state["profile"]["parameters"]}
    item = coordinator.new_round(state, actor, version, "fixture", [])
    item.update(status="building", cable_stage=stage)
    utterances = append_dialogue(coordinator, state, stage, INTRO if stage == 1 else LENGTH, stamp["captured_session_time_ms"])
    ids = [utterance["utterance_id"] for utterance in utterances]
    latest_view = next((view for view in reversed(state["view_history"].get(actor, []))
                        if view["version_id"] == version["version_id"] and view["part_ids"]), None)
    item["context"] = {"request_id": uid("req"), "context_id": uid("context"), "utterance": utterances[-1],
                       "evidence_ids": ids, "edit_base_version_id": version["version_id"],
                       "view": latest_view, "requested_target_part_id": target, "source": "scripted_demo"}
    subject = "홈 입구와 내부 중 무엇을 좁힐지 비교" if stage == 1 else "홈 다섯 개를 유지하며 전체 길이 비교"
    state["discussion"] = {"text": subject, "utterance_id": ids[-1]}
    state["design_revision"] += 1
    for title, interpretation, values in options:
        operations = [{
            "operation_id": "set_parameter", "part_id": fields[key]["affected_part_ids"][0],
            "parameter_id": key, "value": value, "unit": fields[key]["unit"], "source": "DEMO_PRESET",
        } for key, value in values.items() if version["parameters"][key] != value]
        item["candidates"].append(coordinator.new_candidate(item, {
            "title": title, "interpretation": interpretation, "operations": operations,
        }))
    return "candidate.planned", {"round_id": item["round_id"], "stage": stage, "source": "scripted_demo"}, (
        "build", item["round_id"], item["generation_epoch"])


def record_choice(coordinator, state, item, candidate, decision, before, selected):
    stage = item.get("cable_stage")
    if not stage or selected is None:
        return
    decision["cable_stage"] = stage
    version_name = f"V{stage + 1}"
    decision["design_version"] = version_name
    chosen_index = item["candidates"].index(candidate)
    if stage == 2:
        decision["parameter_changes"].insert(0, {
            "parameter_id": "body_length", "label": "전체 길이", "unit": "mm",
            "before": round(before["measurements"]["width_mm"], 2),
            "after": round(selected["measurements"]["width_mm"], 2),
        })
    if (stage, chosen_index) in {(1, 0), (2, 1)}:
        lines = append_dialogue(coordinator, state, stage, FIRST_DECISION if stage == 1 else SECOND_DECISION, now_ms())
        evidence = lines[-1]
        if not decision["rationale_text"]:
            decision.update(rationale_text=evidence["text"], rationale_source="transcript", reason_evidence=evidence)
            decision["assembly_record"]["reason"] = evidence["text"]
        decision["evidence_utterances"].extend(lines)
        decision["evidence_refs"].extend(line["utterance_id"] for line in lines)
    if stage == 2:
        append_dialogue(coordinator, state, 4, HISTORY, now_ms() + 4)


def warm(coordinator):
    """Render the exact scripted path once, before a live rehearsal starts."""
    profile = coordinator.profile
    if not (profile.get("assembly") or {}).get("scripted_demo") == KEY:
        return
    initial = geometry.default_parameters(profile)
    plans = [initial, *[{**initial, **patch} for _, _, patch in FIRST],
             *[{**initial, **ENTRANCE, **patch} for _, _, patch in SECOND]]
    with tempfile.TemporaryDirectory(dir=coordinator.root, prefix=".cable-preview-") as work:
        for index, params in enumerate(plans):
            geometry.build_model(Path(work), f"cable_prewarm_{index}", profile, params)
