"""Build a durable candidate set for one immutable edit request, independently of the live meeting head."""
import os
import shutil
import tempfile
from pathlib import Path

from . import geometry, paper_space
from .contracts import MeetingError, canonical, now_ms, text_field, uid
from .edit_bundle import command_utterances, frozen_command_text
from .parameter_relations import effective_values


def queue(edge, state, request_id):
    bundle = state.get("edit_requests", {}).get(request_id)
    if not bundle:
        raise MeetingError("이 회의에 저장된 수정 요청을 찾을 수 없습니다.", 404)
    sets = state.setdefault("candidate_sets", {})
    previous = sets.get(request_id)
    if previous:
        return "candidate_set.existing", identity(previous), None
    base = edge.version(state, bundle["base_version_id"])
    lock = paper_space.edit_lock(state)
    if lock and lock["request_id"] != request_id:
        raise MeetingError("진행 중인 종이 기준 수정 요청을 먼저 마치세요.", 409)
    item = {**paper_space.pose_fields(bundle), "candidate_set_id": uid("set"), "request_id": request_id, "base_version_id": base["version_id"],
            "created_at": now_ms(), "status": "generating", "reason": None, "limitations": [], "candidates": [],
            "preserved_constraints": edge.effective(state, base.get("request_constraints", [])),
            "constraint_revision": state["constraint_revision"], "proposal_provenance": None}
    sets[request_id] = item
    if base["version_id"] == state["committed_version_id"]:
        state["last_paper_candidate_request"] = request_id
    return "candidate_set.queued", identity(item), ("bundle_plan", request_id, 0)


def identity(item):
    return {k: item[k] for k in ("candidate_set_id", "request_id", "base_version_id")}


def publish_manifest(edge, state, item):
    folder = edge.root / "edit_requests" / state["session_id"] / item["request_id"]
    temp_path = None
    try:
        # Each edit bundle already owns its directory. Publish the manifest only after assets are ready.
        with tempfile.NamedTemporaryFile(dir=folder, delete=False, mode="w", encoding="utf8") as temp:
            temp_path = Path(temp.name)
            temp.write(canonical(item))
        os.replace(temp_path, folder / "candidate_set.json")
    finally:
        if temp_path:
            temp_path.unlink(missing_ok=True)


def finish(edge, state, item, status, reason=None):
    item.update(status=status, reason=reason, finished_at=now_ms())
    try:
        publish_manifest(edge, state, item)
    except OSError:
        item.update(status="failed", reason="후보 목록 파일을 저장할 수 없습니다.")
    edge.emit(state, "candidate_set." + item["status"], {**identity(item), "status": item["status"]})


def interrupted(edge, state):
    for item in state.get("candidate_sets", {}).values():
        if item["status"] in ("generating", "building"):
            finish(edge, state, item, "failed", "서버 재시작으로 후보 생성이 중단되었습니다. 수정 요청 원본은 저장되어 있습니다.")
        elif item["status"] in ("ready", "committed"):
            # A crash between the atomic file replacement and SQLite commit can leave the projection ahead.
            file = edge.root / "edit_requests" / state["session_id"] / item["request_id"] / "candidate_set.json"
            if file.parent.is_dir() and (not file.exists() or file.read_text(encoding="utf8") != canonical(item)):
                publish_manifest(edge, state, item)


def commit(edge, state, actor, payload):
    rid = text_field(payload, "request_id", 100)
    if text_field(payload, "meeting_id", 100) != state["session_id"]:
        raise MeetingError("요청의 회의가 다릅니다.", 409)
    item = state.get("candidate_sets", {}).get(rid)
    bundle = state.get("edit_requests", {}).get(rid)
    if not item or not bundle or text_field(payload, "candidate_set_id", 100) != item["candidate_set_id"]:
        raise MeetingError("요청과 일치하는 후보 묶음을 찾을 수 없습니다.", 404)
    candidate = edge.candidate(item, text_field(payload, "candidate_id", 100))
    base = text_field(payload, "base_version_id", 100)
    if (base != item["base_version_id"] or base != bundle["base_version_id"]
            or text_field(payload, "expected_latest_version_id", 100) != base):
        raise MeetingError("후보의 기준 버전이 다릅니다.", 409)
    vid = candidate["version_id"]
    ack = {**identity(item), "candidate_id": candidate["candidate_id"], "version_id": vid, "status": "version_committed"}
    if item["status"] == "committed" and item["selected_candidate_id"] == candidate["candidate_id"]:
        return "version.commit_repeated", ack, None
    if not paper_space.matches_frame(state, item):
        raise MeetingError("후보를 생성한 종이 기준 위치가 바뀌었습니다. 다시 수정 요청하세요.", 409)
    paper_space.check_commit(state, actor, payload)
    if item["status"] != "ready" or base != state["committed_version_id"] or item["constraint_revision"] != state["constraint_revision"]:
        raise MeetingError("채택본 또는 유지 조건이 변경됐습니다. 현재 Latest를 확인하세요.", 409,
                           committed_version_id=state["committed_version_id"])
    model = edge.version(state, vid)
    if (candidate["validation"]["status"] != "passed" or model["parent_version_id"] != base
            or model["created_from_candidate_id"] != candidate["candidate_id"]
            or any(candidate[key] != model["assets"][filename] for key, filename in (
                ("stl", "print.stl"), ("scad", "candidate.scad"), ("model_asset", "preview.glb")))):
        raise MeetingError("화면에서 검토한 후보와 채택할 파일이 다릅니다.", 409)
    edge.verify_model(model)
    reason = text_field(payload, "reason_text", 600, required=False)
    transcript, selection, image = bundle["transcript"], bundle["selection"], bundle["visual_evidence"]
    lines = command_utterances(state, bundle)
    command_ids = [line["utterance_id"] for line in lines]
    command_text = frozen_command_text(bundle, " ".join(line["text"] for line in lines))
    evidence = {"utterance_id": transcript["utterance_id"], "utterance_refs": transcript["utterance_refs"],
                "transcript": command_text, "transcript_source": transcript["source"],
                "command_text_source": bundle.get("command_text_source", "linked_meeting_utterances"),
                "command_requested_by": bundle["participant_id"],
                "selection_id": selection["selection_id"], "hit_model_local_position": selection["hit_model_local_position"],
                "image_asset_id": image["image_asset_id"], "image_sha256": image["sha256"]}
    source = {"source_request_id": rid, "candidate_set_id": item["candidate_set_id"], "candidate_id": candidate["candidate_id"],
              "candidate_interpretation": candidate["interpretation"], "parameter_diff": candidate["parameter_diff"],
              "evidence": evidence, "reason": reason or "not_stated"}
    model.update(source, full_parameters=candidate["full_parameters"])
    committed_at = now_ms()
    decision = {**source, "decision_id": uid("decision"), "type": "adopt", "outcome": "commit",
                "edit_request_id": rid, "command_utterance_ids": command_ids, "selected_candidate_id": candidate["candidate_id"],
                "result_version_id": vid, "selection_id": selection["selection_id"], "image_id": image["image_asset_id"],
                "commit_time": committed_at,
                "selected_model_version_id": vid, "previous_committed_model_version_id": base, "base_version_id": base,
                "candidate_label": "후보 " + candidate["label"], "candidate_title": candidate["interpretation"],
                "parameter_changes": candidate["parameter_diff"], "actor_id": actor, "confirmed_by": [actor],
                "committed_at": committed_at, "rationale_text": reason, "rationale_source": "explicit" if reason else "not_stated",
                "consensus_status": "not_checked", "constraints": item["preserved_constraints"],
                "responses": [], "evidence_refs": [*command_ids,
                                                   selection["selection_id"], image["image_asset_id"]],
                "evidence_utterances": lines,
                "proposal_provenance": item["proposal_provenance"]}
    decision["assembly_record"] = {
        "request_id": rid, "base_model_id": base, "candidate_set_id": item["candidate_set_id"],
        "source_utterance": command_text, "source_mode": transcript["source"], "evidence": evidence,
        "candidates": [edge.assembly_candidate_record(state, {**option, "status": "valid"}) for option in item["candidates"]],
        "selected_candidate_id": candidate["candidate_id"], "selected_model_file": model["stl_url"],
        "selected_model_sha256": candidate["stl"]["sha256"], "preserved_conditions": state["assembly"]["preserved_conditions"],
        "reason": reason, "timestamp": decision["committed_at"]}
    state["decisions"].append(decision)
    edge.adopt_decision(state, decision, model)
    item.update(status="committed", selected_candidate_id=candidate["candidate_id"], committed_version_id=vid,
                decision_id=decision["decision_id"], committed_at=decision["committed_at"])
    try:
        publish_manifest(edge, state, item)
    except OSError as error:
        raise MeetingError("선택 기록을 저장할 수 없습니다. 다시 선택하세요.", 503) from error
    return "version_committed", ack, None


def interpret(edge, sid, request_id):
    from .bundle_interpretation import interpret_bundle
    with edge.lock:
        state = edge.load(sid)
        item = state["candidate_sets"][request_id]
        if item["status"] != "generating" or edge.stopped:
            return
        bundle = state["edit_requests"][request_id]
        # Later decisions and locks belong to their own requests.
        state["constraints"] = item["preserved_constraints"]
    try:
        plan = interpret_bundle(edge, state, bundle)
        if plan["request_id"] != request_id or plan["base_version_id"] != bundle["base_version_id"]:
            raise MeetingError("AI 응답의 요청 또는 기준 모델이 일치하지 않습니다.")
        error = None
    except Exception as exc:
        error = str(exc) if isinstance(exc, (MeetingError, geometry.GeometryError)) else "수정 요청의 AI 해석에 실패했습니다."
    with edge.transaction():
        state = edge.load(sid)
        item = state["candidate_sets"][request_id]
        if item["status"] != "generating" or edge.stopped:
            return
        if error is not None:
            finish(edge, state, item, "failed", error)
            return
        item.update(ambiguity_type=plan["ambiguity_type"], interpreted_target=plan["interpreted_target"],
                    limitations=list(plan["limitations"]), proposal_provenance=plan["proposal_provenance"])
        if plan["action"] != "propose":
            finish(edge, state, item, plan["action"], plan.get("question") or " ".join(item["limitations"]))
            return
        item["status"] = "building"
        edge.emit(state, "candidate_set.building", identity(item))
        # The existing build pool applies the same CAD concurrency bound as manual edits.
        edge.build_pool.submit(build, edge, sid, request_id, plan["candidates"])


def build(edge, sid, request_id, proposals):
    with edge.lock:
        state = edge.load(sid)
        item = state["candidate_sets"][request_id]
        if item["status"] != "building" or edge.stopped:
            return
        base = edge.version(state, item["base_version_id"])
        profile = state["profile"]
    seen_surfaces = {base.get("surface_sha256", base["geometry_hash"])}
    seen_parameters = set()
    models = []
    candidates = []
    limitations = list(item["limitations"])
    for proposal in proposals:
        cid, vid = uid("candidate"), uid("model")
        key = proposal["candidate_key"]
        try:
            model = edge.build_candidate(sid, vid, profile, base, {**proposal, "candidate_id": cid},
                                         item["preserved_constraints"], seen_parameters, seen_surfaces, lambda: edge.stopped)
            model["constraint_revision"] = item["constraint_revision"]
            assets = model["assets"]
            params = model["parameters"]
            candidate = {"candidate_key": key, "label": key, "candidate_id": cid, "version_id": vid,
                         "interpretation": proposal["interpretation"],
                         "provider_interpretation": proposal.get("provider_interpretation"),
                         "full_parameters": effective_values(profile, {**profile["assembly"].get("fixed_parameters", {}), **params}),
                         "parameter_diff": geometry.get_diff(profile, base["parameters"], params),
                         "preserved_parameters": proposal["preserved_parameters"],
                         "model_asset": assets["preview.glb"], "preview_asset": assets["preview.glb"],
                         "stl": assets["print.stl"], "scad": assets["candidate.scad"],
                         "source_archive": assets["candidate_source.zip"],
                         "validation": {"status": "passed", "report": assets["geometry_report.json"],
                                        "surface_sha256": model["surface_sha256"],
                                        "geometry_provenance": model["geometry_provenance"]}}
            models.append(model)
            candidates.append(candidate)
        except Exception as exc:
            message = str(exc) if isinstance(exc, (MeetingError, geometry.GeometryError)) else "모델 파일 생성에 실패했습니다."
            limitations.append(f"후보 {key} · {message}")
            # A validated build may already have published its directory before publication of candidate metadata failed.
            if (edge.models / vid).is_dir():
                shutil.rmtree(edge.models / vid)
    with edge.transaction():
        state = edge.load(sid)
        item = state["candidate_sets"][request_id]
        if item["status"] != "building" or edge.stopped:
            for model in models:
                shutil.rmtree(edge.models / model["version_id"], ignore_errors=True)
            return
        state["versions"].extend(models)
        item.update(candidates=candidates, limitations=limitations)
        reason = " ".join(limitations) or (None if candidates else "검사를 통과한 후보가 없습니다.")
        finish(edge, state, item, "ready" if candidates else "failed", reason)
