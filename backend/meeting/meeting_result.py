"""Export immutable meeting revisions using the exact adopted CAD artifacts."""
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import tempfile
import zipfile

from .geometry import GeometryError, digest, json_bytes
from .meeting_state import capture_missing, snapshot_fields


def read_asset(models, version, name):
    try:
        meta = version["assets"][name]
        raw = (models / version["version_id"] / name).read_bytes()
    except (KeyError, OSError) as error:
        raise GeometryError(f"채택 파일을 읽지 못했습니다. {name}") from error
    if len(raw) != meta["size_bytes"] or digest(raw) != meta["sha256"]:
        raise GeometryError(f"채택 파일의 해시가 일치하지 않습니다. {name}")
    return raw


def write_json(path, value):
    path.write_bytes(json_bytes(value))


def copy_tree(raw, folder):
    folder.mkdir(parents=True, exist_ok=True)
    with tempfile.SpooledTemporaryFile(max_size=2 * 1024 * 1024) as source:
        source.write(raw)
        source.seek(0)
        with zipfile.ZipFile(source) as archive:
            seen = set()
            for item in archive.infolist():
                path = PurePosixPath(item.filename)
                if item.is_dir() or path.is_absolute() or ".." in path.parts or path.as_posix() != item.filename:
                    raise GeometryError("SCAD 묶음에 허용되지 않는 상대 경로가 있습니다.")
                if path in seen:
                    raise GeometryError("SCAD 묶음 파일 경로가 중복됩니다.")
                seen.add(path)
                target = folder.joinpath(*path.parts)
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(archive.read(item))


def save_model(folder, models, version, final=False):
    folder.mkdir(parents=True)
    scad = read_asset(models, version, "candidate.scad")
    build_bytes = read_asset(models, version, "build.json")
    build = json.loads(build_bytes)
    source = folder / "source"
    copy_tree(read_asset(models, version, "candidate_source.zip"), source)
    entry = PurePosixPath(build["input_path"])
    if entry.is_absolute() or ".." in entry.parts or (source.joinpath(*entry.parts)).read_bytes() != scad:
        raise GeometryError("생성에 사용한 SCAD 파일이 소스 묶음과 다릅니다.")
    alias = "final.scad" if final else "candidate.scad"
    direct = source.joinpath(*entry.parts).with_name(alias)
    if not direct.exists():
        direct.write_bytes(scad)
    elif direct.read_bytes() != scad:
        direct = source.joinpath(*entry.parts)
    if len(entry.parts) == 1 and not any((source / item).exists() for item in
                                        ("source", "parameters.json", "parameter_provenance.json", "build.json", "final.stl", "candidate.stl", "preview.glb", "version.json")):
        # Move the unflattened source tree to the CAD root when there are no naming conflicts.
        for child in source.iterdir():
            child.rename(folder / child.name)
        source.rmdir()
        direct = folder / direct.name
    (folder / ("final.stl" if final else "candidate.stl")).write_bytes(read_asset(models, version, "print.stl"))
    (folder / "preview.glb").write_bytes(read_asset(models, version, "preview.glb"))
    (folder / "build.json").write_bytes(build_bytes)
    raw_parameters = read_asset(models, version, "parameters.json")
    if json.loads(raw_parameters) != version["parameters"]:
        raise GeometryError("선택한 버전의 매개변수가 일치하지 않습니다.")
    (folder / "parameters.json").write_bytes(raw_parameters)
    write_json(folder / "parameter_provenance.json", {"units": version["parameter_units"],
                                                        "sources": version.get("parameter_sources", {})})
    metadata = {key: version[key] for key in ("version_id", "parent_version_id", "source_request_id",
                "candidate_set_id", "candidate_id", "candidate_interpretation", "full_parameters", "parameter_diff",
                "evidence", "reason") if key in version}
    write_json(folder / "version.json", {**metadata, "entry": direct.relative_to(folder).as_posix()})
    return direct


def transcript_views(state):
    rows = sorted((u for u in state["utterances"] if u.get("is_final", u.get("status", "final") == "final")),
                  key=lambda u: (u["start_ms"], u["end_ms"], u["utterance_id"], u["revision"]))
    origin = state["meeting"]["started_at"] or (rows[0]["start_ms"] if rows else 0)
    readable_rows = rows
    if "transcript_status" in state["meeting"]:
        # Corrections keep the same time range and ID. Keep every revision in JSONL
        # for the CAD evidence, but print each spoken sentence only once.
        readable_rows = {u["utterance_id"]: u for u in rows}.values()
    lines = []
    for item in readable_rows:
        label = "예시" if item["source_mode"] == "replay" else "직접 입력" if item["source_mode"] == "manual" or item.get("source_kind") == "manual_input" else "화자 미상"
        if item.get("speaker_label"):
            label += " " + item["speaker_label"]
        ms = max(0, item["start_ms"] - origin)
        offset = f"{ms // 60000:02}:{ms // 1000 % 60:02}.{ms % 1000:03}"
        lines.append(f'[{offset}] [{item["utterance_id"]} r{item["revision"]}] {label} · {item["text"]}')
    return rows, "\n".join(lines) + ("\n" if lines else "")


def transcript_files(state):
    rows, readable = transcript_views(state)
    return {"transcript.jsonl": "".join(json.dumps(u, ensure_ascii=False, sort_keys=True) + "\n" for u in rows).encode(),
            "transcript.txt": readable.encode()}


def freeze_transcript(root, state):
    """Publish both views together, independently of the later CAD/ZIP export."""
    if state["meeting"].get("transcript_status") not in ("finalizing", "finalized"):
        raise ValueError("회의 종료 후 확정 전사를 저장할 수 있습니다.")
    parent = root / "results" / state["session_id"]
    parent.mkdir(parents=True, exist_ok=True)
    target = parent / "transcript"
    files = transcript_files(state)
    if not target.exists():
        with tempfile.TemporaryDirectory(dir=parent, prefix=".transcript-") as temp:
            draft = Path(temp) / "transcript"
            draft.mkdir()
            for name, raw in files.items():
                (draft / name).write_bytes(raw)
            try:
                draft.rename(target)
            except OSError:
                if not target.is_dir():
                    raise
    if any((target / name).read_bytes() != raw for name, raw in files.items()):
        raise ValueError("저장된 확정 전사가 회의 기록과 일치하지 않습니다.")
    return target


def readable_minutes(sid, mode, decisions, transcript, missing, revision=None, latest_id=None, latest_label=None):
    lines = ["3D 설계 회의록", "회의 " + sid,
             "자료 · " + ("예시 시연" if mode == "replay" else "실제 입력"),
             "저장본 " + str(revision) if revision else "진행 중인 기록", "", "설계 결정"]
    if latest_id:
        lines[4:4] = [f"Latest · {latest_label or '채택 모델'} · {latest_id}"]
    for index, decision in enumerate(decisions, 1):
        action = {"commit": "채택", "defer": "보류", "reject": "반려"}.get(decision.get("outcome"), "기록")
        title = decision.get("candidate_title") or decision.get("design_version") or ""
        lines.append(f"{index}. {action} · {title}".rstrip(" ·"))
        if decision.get("selected_model_version_id"):
            lines.append("   선택 모델 · " + decision["selected_model_version_id"])
        for item in decision.get("parameter_changes", []):
            lines.append(f'   {item["label"]} · {item["before"]} → {item["after"]} {item["unit"]}')
        reason = decision.get("rationale_text")
        lines.append("   이유 · " + (reason or "미기록"))
        for u in decision.get("evidence_utterances", []):
            lines.append(f'   근거 [{u["utterance_id"]} r{u["revision"]}] {u["text"]}')
        ref = decision.get("reference_selection")
        if ref:
            lines.append(f'   요청 · {decision["client_request_id"]} · 기준 {decision["base_version_id"]}')
            lines.append(f'   선택 부위 · {ref["part_id"]} · 참조 {ref["reference_version_id"]} · 사진 {ref["image_id"]}')
        if decision.get("trigger_utterance_id"):
            lines.append("   수정안 요청 발화 · " + decision["trigger_utterance_id"])
    if not decisions:
        lines.append("저장된 결정 없음")
    lines.extend(["", "전체 전사 (시간순 · 정정 이력 포함)", transcript.rstrip() or "확정된 발화 없음"])
    if missing:
        lines.extend(["", "수집 누락 및 미확인 항목"])
        for item in missing:
            lines.append(" · ".join(str(v) for v in item.values() if v is not None))
    return "\n".join(lines) + "\n"


def saved_minutes(folder):
    path = folder / "minutes.txt"
    if path.is_file():
        return path.read_text(encoding="utf-8")
    # Older saved revisions have the same immutable decisions and full transcript.
    manifest = json.loads((folder / "manifest.json").read_text(encoding="utf-8"))
    return readable_minutes(manifest["meeting_id"], manifest["mode"],
                            json.loads((folder / "decisions.json").read_text(encoding="utf-8")),
                            (folder / "transcript.txt").read_text(encoding="utf-8"),
                            manifest.get("missing", []), manifest["revision"], manifest.get("adopted_version_id"),
                            manifest.get("adopted_version_label"))


def summary_markdown(state, version, unused):
    summary = snapshot_fields(state)["summary"]
    decision = next((item for item in reversed(state["decisions"]) if item["selected_model_version_id"]), None)
    evidence = (decision or {}).get("evidence_utterances", [])
    lines = ["# 회의 요약", "", f'목적 · {state["assembly"]["display_name"]}의 물체 2 외형 검토',
             f'논의 · {summary["discussion"]}', f'유지 · {summary["preserved"]}', ""]
    if summary["discussion_evidence"]:
        lines += [f'논의 근거 · {summary["discussion_evidence"]}', ""]
    lines += ["## 사용자 결정", "", summary["decision"], f'최종 버전 · `{version["version_id"]}`']
    if decision:
        for item in decision.get("parameter_changes", []):
            lines.append(f'- {item["label"]} · {item["before"]} → {item["after"]} {item["unit"]}')
        reason = decision.get("rationale_text")
        lines += ["", "이유 · " + (reason or "선택 이유 미기록")]
        if decision.get("reason_evidence"):
            evidence = [*evidence, decision["reason_evidence"]]
        for item in evidence:
            lines.append(f'- 원문 [{item["utterance_id"]} r{item["revision"]}] {item["text"]}')
        corrected = {(u["utterance_id"], u["revision"]) for u in state["utterances"]}
        if any(any(uid == item["utterance_id"] and rev > item["revision"] for uid, rev in corrected) for item in evidence):
            lines.append("- 근거 발화가 이후 정정되었습니다. 전사에서 후속 개정을 확인하세요.")
    lines += ["", "## AI·수동 제안 중 최종본에 미채택", ""]
    lines += [f'- [{item["source"]}] {item["title"]} · {item["interpretation"]}' for item in unused] or ["해당 제안 없음"]
    questions = [item["question"] for item in state["rounds"] if item["status"] == "needs_input" and item.get("question")]
    lines += ["", "## 미해결 사항", "", *(questions or ["기록된 확인 질문 없음"])]
    return "\n".join(lines) + "\n"


def export_design_requests(root, folder, state, missing):
    """Archive the exact utterance revisions and reference image for explicit decisions."""
    images = {image["evidence_id"]: image for image in state.get("images", [])}
    decisions = {item["round_id"]: item for item in state["decisions"] if item.get("round_id")}
    adopted_by_request = {item["client_request_id"]: item for item in state["decisions"]
                          if item.get("client_request_id") and item.get("selected_model_version_id")}
    requests = []
    for round_ in state["rounds"]:
        context = round_.get("context") or {}
        if not context.get("client_request_id"):
            continue
        selection = dict(context["reference_selection"])
        image_id = selection["image_id"]
        image = images.get(image_id)
        if image and re.fullmatch(r"[A-Za-z0-9_-]+", image_id):
            suffix = {"image/jpeg": ".jpg", "image/png": ".png"}.get(image["mime"])
            source = root / "media" / image_id
            if suffix and source.is_file():
                content = source.read_bytes()
                if digest(content) == image["sha256"]:
                    target = folder / "evidence" / (image_id + suffix)
                    target.parent.mkdir(exist_ok=True)
                    if not target.exists():
                        target.write_bytes(content)
                    selection["file"] = target.relative_to(folder).as_posix()
                    selection["sha256"] = image["sha256"]
                    selection["source_mode"] = image["source_mode"]
        if "file" not in selection:
            missing.append({"kind": "reference_photo", "image_id": image_id})
        decision = adopted_by_request.get(context["client_request_id"]) or decisions.get(round_["round_id"]) or {}
        requests.append({
            "client_request_id": context["client_request_id"], "request_id": context.get("request_id"),
            "round_id": round_["round_id"], "base_version_id": round_["edit_base_version_id"],
            "speech_range_id": context.get("speech_range_id"), "utterances": context.get("source_utterance_refs", []),
            "reference_selection": selection, "status": round_["status"],
            "proposal_provenance": round_.get("proposal_provenance"),
            "candidates": [{k:c.get(k) for k in ("candidate_id", "title", "interpretation", "status", "version_id", "parameter_changes")}
                           for c in round_["candidates"]],
            "selected_candidate_id": decision.get("candidate_id") if decision.get("selected_model_version_id") else None,
            "decision_round_id": decision.get("round_id"), "adopted_version_id": decision.get("selected_model_version_id"),
            "adopted_changes": decision.get("parameter_changes", []),
            "reason": decision.get("rationale_text"), "decided_at": decision.get("committed_at")})
    for rid, bundle in state.get("edit_requests", {}).items():
        item = state.get("candidate_sets", {}).get(rid, {})
        source = root / "edit_requests" / state["session_id"] / rid
        dest = folder / "edit_requests" / rid
        dest.mkdir(parents=True)
        write_json(dest / "request.json", bundle)
        if item:
            write_json(dest / "candidate_set.json", item)
        for evidence in (bundle["visual_evidence"], bundle["audio"]):
            filename = evidence["file"]
            if filename not in ("target.jpg", "target.png", "command.wav"):
                raise GeometryError("수정 요청 근거의 파일명이 다릅니다.")
            try:
                raw = (source / filename).read_bytes()
                if digest(raw) != evidence["sha256"]:
                    raise ValueError
                (dest / filename).write_bytes(raw)
            except (OSError, ValueError):
                missing.append({"kind": "edit_request_evidence", "request_id": rid, "file": filename})
        requests.append({"request_id": rid, "base_version_id": bundle["base_version_id"],
                         "command_utterance_ids": [ref["utterance_id"] for ref in bundle["transcript"]["utterance_refs"]],
                         "candidate_set_id": item.get("candidate_set_id"), "selected_candidate_id": item.get("selected_candidate_id"),
                         "adopted_version_id": item.get("committed_version_id"), "file": f"edit_requests/{rid}/request.json"})
    if requests:
        write_json(folder / "design_requests.json", requests)


def build_result(root, models, state, revision):
    sid = state["session_id"]
    parent = root / "results" / sid
    parent.mkdir(parents=True, exist_ok=True)
    versions = {v["version_id"]: v for v in state["versions"]}
    final_id = state["meeting"]["adopted_version_id"]
    selected = versions[final_id]
    selected_candidates = {d.get("candidate_id") for d in state["decisions"] if d.get("selected_model_version_id")}
    reviewed = set(state["reviewed_candidates"]) | selected_candidates
    fields = {p["parameter_id"]: p["unit"] for p in state["profile"]["parameters"]}
    reviewed_records = []
    unused = []
    missing = capture_missing(state)
    with tempfile.TemporaryDirectory(dir=parent, prefix=".result-") as staging:
        folder = Path(staging) / "data"
        folder.mkdir()
        cad = folder / "cad"
        entry = save_model(cad, models, {**selected, "parameter_units": fields}, final=True)
        original = versions[state["original_version_id"]]
        original_dir = folder / "original"
        save_model(original_dir, models, {**original, "parameter_units": fields}, final=False)
        (original_dir / "original_source.zip").write_bytes(read_asset(models, original, "adapter_source_snapshot.zip"))
        for item in state["rounds"]:
            for candidate in item["candidates"]:
                vid = candidate.get("version_id")
                if not vid:
                    continue
                candidate_id = candidate["candidate_id"]
                record = {"candidate_id": candidate_id, "version_id": vid, "title": candidate["title"],
                          "interpretation": candidate["interpretation"], "source": item["provider_mode"],
                          "parameter_changes": candidate.get("parameter_changes", []),
                          "evidence_utterance_id": ((item.get("context") or {}).get("utterance") or {}).get("utterance_id"),
                          "reviewed": candidate_id in reviewed,
                          "proposal_provenance": item.get("proposal_provenance", {"source": item["provider_mode"]}),
                          "geometry_provenance": versions[vid].get("geometry_provenance"),
                          "parameters": versions[vid]["parameters"]}
                if vid != final_id:
                    unused.append(record)
                if not record["reviewed"]:
                    continue
                target = folder / "candidates" / candidate_id
                try:
                    save_model(target, models, {**versions[vid], "parameter_units": fields})
                    write_json(target / "review.json", record)
                except (OSError, ValueError, KeyError, zipfile.BadZipFile):
                    shutil.rmtree(target, ignore_errors=True)
                    missing.append({"kind": "reviewed_candidate_assets", "candidate_id": candidate_id})
                reviewed_records.append(record)
        if state["meeting"].get("transcript_status") == "finalized":
            for name in ("transcript.jsonl", "transcript.txt"):
                shutil.copyfile(parent / "transcript" / name, folder / name)
        else:
            for name, raw in transcript_files(state).items():
                (folder / name).write_bytes(raw)
        readable = (folder / "transcript.txt").read_text(encoding="utf-8")
        utterance_count = (len({u["utterance_id"] for u in state["utterances"] if u.get("is_final")})
                           if state["meeting"].get("transcript_status") == "finalized"
                           else (folder / "transcript.jsonl").read_bytes().count(b"\n"))
        (folder / "summary.md").write_text(summary_markdown(state, selected, unused), encoding="utf-8")
        export_design_requests(root, folder, state, missing)
        write_json(folder / "decisions.json", state["decisions"])
        write_json(folder / "verification.json", {"status": "recorded" if state.get("verification_records") else "not_performed",
                                                  "records": state.get("verification_records", [])})
        if state.get("transfer"):
            write_json(folder / "transfer.json", state["transfer"])
        design_history = []
        adopted_ids = [state["original_version_id"], *[d["selected_model_version_id"] for d in state["decisions"] if d.get("selected_model_version_id")]]
        for position, vid in enumerate(dict.fromkeys(adopted_ids)):
            model = versions[vid]
            decision = next((d for d in reversed(state["decisions"]) if d.get("selected_model_version_id") == vid), None)
            target = folder / "adopted_versions" / vid
            save_model(target, models, {**model, "parameter_units": fields})
            design_history.append({"label": model.get("design_version", f"V{position + 1}"), "version_id": vid,
                "parent_version_id": model.get("parent_version_id"), "parameters": model["parameters"],
                "reason": (decision or {}).get("rationale_text"), "reason_source": (decision or {}).get("rationale_source", "not_stated"),
                "stl_sha256": model["assets"]["print.stl"]["sha256"], "stage_directory": str(target.relative_to(folder)),
                "width_mm": round(model["measurements"]["width_mm"], 3)})
        write_json(folder / "design_history.json", design_history)
        write_json(folder / "reviewed_candidates.json", reviewed_records)
        sets = [{k:v for k,v in item.items() if k != "context"} for item in state["rounds"]]
        write_json(folder / "candidate_sets.json", [*sets, *state.get("candidate_sets", {}).values()])
        (folder / "minutes.txt").write_text(readable_minutes(sid, state["meeting"]["mode"], state["decisions"],
                                                              readable, missing, revision, final_id, selected.get("design_version")), encoding="utf-8")
        entry_path = entry.relative_to(folder).as_posix()
        (folder / "README.md").write_text(
            f"# ContextPin 결과\n\n최종 채택 버전 `{final_id}`\n\n"
            f"OpenSCAD에서 `{entry_path}`를 열어 렌더링합니다.\n\n"
            f'```sh\nopenscad --export-format binstl -o rebuilt.stl "{entry_path}"\n```\n\n'
            "STL은 mm, GLB는 m입니다. CAD 결합부 형상과 파일 관계를 확인했습니다. "
            "실물 출력, 간섭, 맞음새는 별도 확인 범위입니다.\n", encoding="utf-8")
        files = {path.relative_to(folder).as_posix(): {"sha256": digest(path.read_bytes()), "size_bytes": path.stat().st_size}
                 for path in sorted(folder.rglob("*")) if path.is_file()}
        manifest = {"schema_version": 1, "meeting_id": sid, "revision": revision,
                    "started_at": state["meeting"]["started_at"], "ended_at": state["meeting"]["ended_at"],
                    "mode": state["meeting"]["mode"], "data_label": "예시 데이터" if state["meeting"]["mode"] == "replay" else "실제 입력",
                    "input_mode": state["meeting"].get("input_mode"), "parent_session_id": (state.get("transfer") or {}).get("source_session_id"),
                    "geometry_provenance": selected.get("geometry_provenance"), "status": "partial" if missing else "complete",
                    "adopted_version_label": selected.get("design_version"), "adopted_version_id": final_id, "adopted_revision": state["meeting"]["adopted_revision"],
                    "original_version_id": original["version_id"], "final_scad": entry_path,
                    "final_stl": "cad/final.stl", "stl_sha256": selected["assets"]["print.stl"]["sha256"],
                    "utterance_count": utterance_count, "files": files, "missing": missing}
        write_json(folder / "manifest.json", manifest)
        with zipfile.ZipFile(folder / "result.zip", "w", zipfile.ZIP_DEFLATED) as archive:
            for name in [*files, "manifest.json"]:
                archive.write(folder / name, name)
        with zipfile.ZipFile(folder / "result.zip") as archive:
            if archive.testzip() is not None:
                raise GeometryError("결과 ZIP의 무결성을 확인하지 못했습니다.")
        # A unique storage key allows safe retries after a crash between rename and DB commit.
        key = Path(staging).name.removeprefix(".result-")
        folder.rename(parent / key)
    return manifest, key
