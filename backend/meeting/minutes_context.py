"""Build the minutes evidence exclusively from one immutable result package."""
import difflib
import json
from datetime import datetime
from pathlib import Path, PurePosixPath
import zipfile

from .edit_bundle import frozen_command_text
from .parameter_relations import effective_values


def package_file(folder, name):
    path = PurePosixPath(name)
    if path.is_absolute() or ".." in path.parts or not path.parts:
        raise ValueError("회의 결과의 상대 파일 경로를 확인하세요.")
    return folder.joinpath(*path.parts)


def read_json(path):
    return json.loads(path.read_bytes())


def load_version(folder, directory, record, files, profile, final=False):
    path = package_file(folder, directory)
    version = read_json(path / "version.json")
    if version["version_id"] != record["version_id"]:
        raise ValueError("CAD 저장 경로와 버전 ID가 다릅니다.")
    scad = package_file(path, version["entry"]).relative_to(folder).as_posix()
    stl = (path / ("final.stl" if final else "candidate.stl")).relative_to(folder).as_posix()
    model = (path / "preview.glb").relative_to(folder).as_posix()
    editable = read_json(path / "parameters.json")
    full = effective_values(profile, {**profile["assembly"]["fixed_parameters"], **editable})
    if any(key in full and full[key] != value for key, value in (version.get("full_parameters") or {}).items()):
        raise ValueError("채택 버전의 전체 치수와 적용 매개변수가 다릅니다.")
    build = read_json(path / "build.json")
    if record.get("parameters", editable) != editable or record.get("stl_sha256", files[stl]["sha256"]) != files[stl]["sha256"]:
        raise ValueError("회의 이력과 저장한 CAD의 매개변수 또는 STL이 다릅니다.")
    return {"version_id": version["version_id"], "label": record["label"],
            "parent_version_id": version.get("parent_version_id"), "full_parameters": full,
            "parameter_units": read_json(path / "parameter_provenance.json")["units"],
            "scad_file": scad, "source_sha256": files[scad]["sha256"], "stl_file": stl,
            "stl_metadata": {**build["stl"], **files[stl]}, "model_file": model,
            "model_metadata": {**build["preview"], **files[model]}}


def actual_changes(before, after, recorded_changes, source_by_version):
    """Compute accepted changes from exported files. Proposal prose cannot change these values."""
    labels = {change["parameter_id"]: change.get("label") for change in recorded_changes}
    old, new = before["full_parameters"], after["full_parameters"]
    changes = [{"parameter_id": key, "label": labels.get(key) or key,
                "before": old.get(key), "after": new.get(key), "unit": after["parameter_units"].get(key)}
               for key in sorted(old.keys() | new.keys()) if old.get(key) != new.get(key)]
    a, b = source_by_version[before["version_id"]], source_by_version[after["version_id"]]
    patch = "\n".join(difflib.unified_diff(a.splitlines(), b.splitlines(), n=0, lineterm="",
                                         fromfile=before["scad_file"], tofile=after["scad_file"]))
    return changes, {"before_scad": before["scad_file"], "after_scad": after["scad_file"],
                     "before_sha256": before["source_sha256"], "after_sha256": after["source_sha256"],
                     "scad_diff": patch[:3600], "diff_truncated": len(patch) > 3600}


def evidence_refs(rows):
    return [{"utterance_id": row["utterance_id"], "revision": row["revision"]} for row in rows]


def resolve_command(refs, lines):
    try:
        return " ".join(lines[(ref["utterance_id"], ref["revision"])]["text"] for ref in refs)
    except KeyError as exc:
        raise ValueError("수정 요청이 참조한 확정 발화 개정을 찾을 수 없습니다.") from exc


def candidate_rows(group):
    source = (group.get("proposal_provenance") or {}).get("source") or group.get("provider_mode")
    return [{"candidate_id": row["candidate_id"], "label": row.get("label") or row.get("title") or row["candidate_id"],
             "interpretation": row.get("interpretation"), "source": source,
             "version_id": row.get("version_id"), "parameter_diff": row.get("parameter_diff", row.get("parameter_changes", []))}
            for row in group.get("candidates", [])]


def collect_rounds(folder, files, lines, groups, indices, decision_by_round):
    by_request = {g["request_id"]: g for g in groups if g.get("request_id")}
    by_round = {g["round_id"]: g for g in groups if g.get("round_id")}
    rounds = []
    for index in indices:
        if index.get("file"):
            request = read_json(package_file(folder, index["file"]))
            rid = request["request_id"]
            if rid != index["request_id"] or request["base_version_id"] != index["base_version_id"]:
                raise ValueError("회의 패키지의 수정 요청과 인덱스가 다릅니다.")
            group = by_request.get(rid, {})
            refs = request["transcript"]["utterance_refs"]
            command = frozen_command_text(request, resolve_command(refs, lines))
            ids = [r["utterance_id"] for r in refs]
            if request.get("command_utterance_ids", ids) != ids:
                raise ValueError("수정 명령이 요청 당시의 확정 전사와 다릅니다.")
            image = dict(request["visual_evidence"])
            image_path = (PurePosixPath(index["file"]).parent / image["file"]).as_posix()
            package_file(folder, image_path)
            if image_path in files and files[image_path]["sha256"] != image["sha256"]:
                raise ValueError("수정 요청과 저장된 이미지의 해시가 다릅니다.")
            image.update(file=image_path, available=image_path in files)
            selection = request["selection"]
            part = selection["part_id"]
            target = {"part_id": part, "description": part, "selection": selection, "visual_evidence": image,
                      "proposed_description": (group.get("interpreted_target") or {}).get("description")}
            rounds.append({"request_id": rid, "time": request["created_at"], "base_version_id": request["base_version_id"],
                           "command_utterance_ids": ids, "command_utterance_refs": refs, "command_text": command,
                           "command_text_source": request.get("command_text_source", "linked_meeting_utterances"),
                           "command_requested_by": request.get("participant_id"),
                           "target": target, "candidates": candidate_rows(group), "status": group.get("status", "saved"),
                           "question": group.get("question") or group.get("reason"), "request_file": index["file"],
                           "interrupted_at_meeting_end": group.get("interrupted_at_meeting_end", False)})
        else:
            # Legacy explicit design rounds used a client request ID and a separate server round ID.
            rid = index["client_request_id"]
            group = by_round.get(index["round_id"], {})
            refs = evidence_refs(index.get("utterances", []))
            selection = index.get("reference_selection") or {}
            image = {k: selection[k] for k in ("image_id", "file", "sha256", "source_mode") if k in selection}
            if image:
                image["available"] = image.get("file") in files
            rounds.append({"request_id": rid, "round_id": index["round_id"], "time": group.get("created_at"),
                           "base_version_id": index["base_version_id"], "command_utterance_refs": refs,
                           "command_utterance_ids": [r["utterance_id"] for r in refs], "command_text": resolve_command(refs, lines),
                           "target": {"description": selection.get("part_id"), "part_id": selection.get("part_id"),
                                      "selection": selection, "visual_evidence": image or None},
                           "candidates": candidate_rows(group), "status": index["status"], "question": group.get("question") or group.get("error"),
                           "interrupted_at_meeting_end": group.get("interrupted_at_meeting_end", False)})
    represented = {r.get("round_id") for r in rounds}
    for round_id, group in by_round.items():
        if round_id in represented:
            continue
        decision = decision_by_round.get(round_id, {})
        # Manual and older replay submissions may have no Ray, photo or spoken command.
        refs = evidence_refs(decision.get("evidence_utterances", []))
        record = decision.get("assembly_record") or {}
        rounds.append({"request_id": record.get("request_id") or round_id, "round_id": round_id,
                       "time": group.get("created_at"), "base_version_id": group.get("edit_base_version_id"),
                       "command_utterance_refs": refs, "command_utterance_ids": [r["utterance_id"] for r in refs],
                       "command_text": resolve_command(refs, lines), "target": None, "candidates": candidate_rows(group),
                       "status": group["status"], "question": group.get("question") or group.get("error"),
                       "interrupted_at_meeting_end": group.get("interrupted_at_meeting_end", False)})
    return rounds


def unresolved_request(request):
    status = request["status"]
    if request.get("result_version_id") or status in {"committed", "dismissed", "rejected", "cancelled"}:
        return None
    if status == "superseded" and not request.get("interrupted_at_meeting_end") and request.get("question") not in {
            "회의를 종료했습니다.", "회의 종료 시 생성 중이어서 채택되지 않았습니다."}:
        return None
    labels = {"deferred": "보류", "failed": "후보 생성 실패", "unsupported_specific_region": "지원되지 않는 부위",
              "clarification": "추가 설명 대기", "needs_input": "추가 설명 대기", "superseded": "회의 종료 시 검토 미완료"}
    description = labels.get(status, "채택 기록 없음")
    if detail := (request.get("question") or request.get("command_text")):
        description += " · " + detail
    return {"request_id": request["request_id"], "status": status, "description": description,
            "evidence_utterance_ids": request["command_utterance_ids"]}


def build_context(folder, manifest):
    """Project timeline, edit and actual adoption facts from the same published revision."""
    folder = Path(folder)
    files = manifest["files"]
    initial = manifest["original_version_id"]
    final = manifest["adopted_version_id"]
    with zipfile.ZipFile(folder / "original" / "original_source.zip") as archive:
        config = json.loads(archive.read("assembly_config.json"))
    profile = {**config["profile"], "assembly": {"fixed_parameters": config.get("fixed_parameters", {})}}
    parts = {p["part_id"]: p["label"] for p in profile["semantic_parts"]}
    stored = read_json(folder / "decisions.json")
    history = read_json(folder / "design_history.json") if "design_history.json" in files else []
    records = {r["version_id"]: r for r in history}
    # Early archives kept committed models under original/candidates/cad. Recover
    # only versions identified by an actual decision and check the saved version.json.
    records.setdefault(initial, {"version_id": initial, "stage_directory": "original", "label": "V1"})
    for d in stored:
        vid = d.get("selected_model_version_id")
        if vid and vid not in records:
            directory = "cad" if vid == final else "candidates/" + d["candidate_id"]
            records[vid] = {"version_id": vid, "stage_directory": directory,
                            "label": d.get("design_version") or f"V{len(records) + 1}"}
    versions = {vid: load_version(folder, r["stage_directory"], r, files, profile, final=r["stage_directory"] == "cad")
                for vid, r in records.items()}
    latest = records[final]
    cad = load_version(folder, "cad", latest, files, profile, final=True)
    if (versions[final]["source_sha256"], versions[final]["full_parameters"]) != (cad["source_sha256"], cad["full_parameters"]):
        raise ValueError("최종 CAD가 마지막 채택 버전과 다릅니다.")
    source_by_version = {vid: package_file(folder, v["scad_file"]).read_bytes().decode("utf-8") for vid, v in versions.items()}
    cad["source_code"] = package_file(folder, cad["scad_file"]).read_bytes().decode("utf-8")

    lines = {}
    latest_lines = {}
    for raw in (folder / "transcript.jsonl").read_text(encoding="utf-8").splitlines():
        row = json.loads(raw)
        if not row.get("is_final", row.get("status", "final") == "final"):
            continue
        uid, rev = row["utterance_id"], row["revision"]
        line = {k: row.get(k) for k in ("utterance_id", "revision", "text", "start_ms", "end_ms", "speaker_id")}
        line["source"] = row.get("source") or row.get("source_mode")
        lines[(uid, rev)] = line
        if uid not in latest_lines or latest_lines[uid]["revision"] < rev:
            latest_lines[uid] = line
    groups = read_json(folder / "candidate_sets.json") if "candidate_sets.json" in files else []
    indices = read_json(folder / "design_requests.json") if "design_requests.json" in files else []
    rounds = collect_rounds(folder, files, lines, groups, indices, {d["round_id"]: d for d in stored if d.get("round_id")})
    for request in rounds:
        if isinstance(request["time"], str):
            request["time"] = round(datetime.fromisoformat(request["time"].replace("Z", "+00:00")).timestamp() * 1000)
        if request["target"] and request["target"].get("part_id") in parts:
            request["target"]["description"] = parts[request["target"]["part_id"]]
    rounds.sort(key=lambda request: request.get("time") or 0)
    requests = {r["request_id"]: r for r in rounds}
    by_round = {r["round_id"]: r for r in rounds if r.get("round_id")}
    committed = []
    last = initial
    for d in stored:  # append/transaction order, including re-adoption of an earlier version
        result = d.get("selected_model_version_id")
        if not result or d.get("outcome") != "commit":
            continue
        base = d.get("previous_committed_model_version_id") or d.get("base_version_id") or last
        if base != last or result not in versions:
            raise ValueError("실제 채택 이력에서 CAD 버전 연결을 확인할 수 없습니다.")
        last = result
        request = requests.get(d.get("edit_request_id") or d.get("client_request_id")) or by_round.get(d.get("round_id")) or {}
        cid = None if d.get("type") == "readopt" else (d.get("selected_candidate_id") or d.get("candidate_id"))
        selected = next((c for c in request.get("candidates", []) if c["candidate_id"] == cid), None)
        if d.get("edit_request_id"):
            if (not request or request["base_version_id"] != base or not selected or selected["version_id"] != result
                    or d.get("command_utterance_ids") != request["command_utterance_ids"]
                    or request["target"]["selection"]["selection_id"] != d.get("selection_id")
                    or request["target"]["visual_evidence"]["image_asset_id"] != d.get("image_id")):
                raise ValueError("채택한 후보와 수정 요청의 버전, 발화 또는 선택 근거가 다릅니다.")
        elif cid and not selected and d.get("type") != "readopt":
            selected = {"candidate_id": cid, "label": d.get("candidate_label") or cid,
                        "interpretation": d.get("candidate_title"), "version_id": result}
        if request:
            request.update(selected_candidate_id=cid, result_version_id=result, decision_id=d["decision_id"])
        diff, code = actual_changes(versions[base], versions[result], d.get("parameter_changes", []), source_by_version)
        if request:
            request.update(parameter_diff=diff, actual_cad_change=code)
        refs = list(request.get("command_utterance_refs") or evidence_refs(d.get("evidence_utterances", [])))
        reason = d.get("rationale_text") if d.get("rationale_source") in {"explicit", "transcript"} else None
        if d.get("rationale_source") == "transcript":
            reason_ref = evidence_refs([d["reason_evidence"]])[0]
            if resolve_command([reason_ref], lines) != reason:
                raise ValueError("결정 이유가 근거 발화와 다릅니다.")
            if reason_ref not in refs:
                refs.append(reason_ref)
        committed.append({"decision_id": d["decision_id"], "type": d.get("type"), "edit_request_id": request.get("request_id"),
                          "base_version_id": base, "base_label": versions[base]["label"],
                          "result_version_id": result, "result_label": versions[result]["label"], "commit_time": d["committed_at"],
                          "command": request.get("command_text") or None, "target": request.get("target"),
                          "candidates": request.get("candidates", []), "candidate_selected": selected,
                          "selected_candidate_id": cid, "parameter_diff": diff, "actual_cad_change": code,
                          "reason": reason, "reason_source": d.get("rationale_source") if reason else "not_stated",
                          "evidence_utterance_ids": list(dict.fromkeys(r["utterance_id"] for r in refs)), "evidence_utterance_refs": refs})
    if last != final:
        raise ValueError("최종 버전이 회의에서 마지막으로 채택한 모델과 다릅니다.")
    unresolved = []
    for request in rounds:
        request.setdefault("result_version_id", None)
        request.setdefault("selected_candidate_id", None)
        if pending := unresolved_request(request):
            unresolved.append(pending)
    if "partial_edits.json" in files:
        for edit in read_json(folder / "partial_edits.json"):
            if edit["request_id"] not in requests:
                unresolved.append({**edit, "description": edit.get("command_text") or "수정 부위 미선택",
                                   "evidence_utterance_ids": edit["command_utterance_ids"]})
    name = manifest.get("object_name") or config.get("object_2", {}).get("display_name") or config["display_name"]
    return {"meeting_id": manifest["meeting_id"], "started_at": manifest["started_at"], "ended_at": manifest["ended_at"],
            "object_name": name, "mode": manifest["mode"], "initial_version_id": initial, "final_version_id": final,
            "full_transcript": sorted(latest_lines.values(), key=lambda l: (l["start_ms"], l["end_ms"], l["utterance_id"])),
            "versions": list(versions.values()), "final_cad": cad, "edit_rounds": rounds, "design_decisions": committed,
            "unresolved_items": unresolved}
