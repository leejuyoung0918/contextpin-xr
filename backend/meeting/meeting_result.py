"""Export immutable meeting revisions using the exact adopted CAD artifacts."""
import json
from pathlib import Path, PurePosixPath
import re
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
    write_json(folder / "version.json", {"version_id": version["version_id"], "parent_version_id": version["parent_version_id"],
                                         "entry": direct.relative_to(folder).as_posix()})
    return direct


def transcript_views(state):
    rows = sorted(state["utterances"], key=lambda u: (u["start_ms"], u["end_ms"], u["utterance_id"], u["revision"]))
    origin = state["meeting"]["started_at"]
    lines = []
    for item in rows:
        label = "예시" if item["source_mode"] == "replay" else "직접 입력" if item["source_mode"] == "manual" else "화자 미상"
        ms = max(0, item["start_ms"] - origin)
        offset = f"{ms // 60000:02}:{ms // 1000 % 60:02}.{ms % 1000:03}"
        lines.append(f'[{offset}] [{item["utterance_id"]} r{item["revision"]}] {label} · {item["text"]}')
    return rows, "\n".join(lines) + ("\n" if lines else "")


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
                          "reviewed": candidate_id in reviewed}
                if vid != final_id:
                    unused.append(record)
                if not record["reviewed"]:
                    continue
                target = folder / "candidates" / candidate_id
                try:
                    save_model(target, models, {**versions[vid], "parameter_units": fields})
                    write_json(target / "review.json", record)
                except (OSError, ValueError, KeyError, zipfile.BadZipFile):
                    import shutil
                    shutil.rmtree(target, ignore_errors=True)
                    missing.append({"kind": "reviewed_candidate_assets", "candidate_id": candidate_id})
                reviewed_records.append(record)
        rows, readable = transcript_views(state)
        (folder / "transcript.jsonl").write_text("".join(json.dumps(item, ensure_ascii=False) + "\n" for item in rows),
                                                  encoding="utf-8")
        (folder / "transcript.txt").write_text(readable, encoding="utf-8")
        (folder / "summary.md").write_text(summary_markdown(state, selected, unused), encoding="utf-8")
        write_json(folder / "decisions.json", state["decisions"])
        write_json(folder / "reviewed_candidates.json", reviewed_records)
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
                    "mode": state["meeting"]["mode"], "status": "partial" if missing else "complete",
                    "adopted_version_id": final_id, "adopted_revision": state["meeting"]["adopted_revision"],
                    "original_version_id": original["version_id"], "final_scad": entry_path,
                    "final_stl": "cad/final.stl", "stl_sha256": selected["assets"]["print.stl"]["sha256"],
                    "utterance_count": len(rows), "files": files, "missing": missing}
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
