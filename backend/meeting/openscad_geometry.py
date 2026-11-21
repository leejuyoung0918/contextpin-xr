"""Run trusted OpenSCAD assembly sources and preview the exact selected STL."""

from collections import Counter, OrderedDict
from functools import lru_cache
import hashlib
import io
import json
import math
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import tempfile
import threading
import time
import zipfile

from .geometry import (GeometryError, ModelAdapter, _check_job, _slots, default_parameters,
                       describe_asset, digest, json_bytes, violated_constraints)
from .mesh_geometry import MeshGeometry, bounds, export_glb, export_stl, inspect_mesh, validate_exports


SAMPLE = Path(__file__).with_name("openscad_sample") / "assembly.json"
MAX_SOURCE_BYTES = 16 * 1024 * 1024
MAX_STL_BYTES = 8 * 1024 * 1024
PARAMETER_NAME = re.compile(r"[a-zA-Z_][a-zA-Z_0-9]*\Z")
AXES = {"x": 0, "y": 1, "z": 2}
_baselines = OrderedDict()
_baselines_lock = threading.Lock()


def _is_number(value):
    return type(value) in (int, float) and -1e9 <= value <= 1e9


class OpenScadAdapter(ModelAdapter):
    def __init__(self, config_path):
        super().__init__(config_path)
        try:
            if config_path.stat().st_size > 256 * 1024:
                raise ValueError()
            self.config = json.loads(config_path.read_text(encoding="utf-8"))
            self.sources = self.read_sources()
            self.guard = self.config["coupling_guard"]
            self.mounting = self.config["mounting"]
            self.check_assembly()
        except (OSError, TypeError, ValueError, AttributeError, KeyError) as error:
            raise GeometryError("OpenSCAD 조립체 설정의 파일, 부위, 장착 좌표와 고정 영역을 확인해 주세요.") from error

    def read_sources(self):
        config = self.config
        root = self.profile_path.parent
        main = config["source_file"]
        if not main.lower().endswith(".scad"):
            raise ValueError()
        paths = {}
        total_bytes = 0
        for name in (main, *config.get("source_files", [])):
            relative = Path(name)
            path = (root / relative).resolve()
            if (relative.is_absolute() or relative.as_posix() != name or ".." in relative.parts
                    or not path.is_relative_to(root) or name in paths):
                raise ValueError()
            total_bytes += path.stat().st_size
            if total_bytes > MAX_SOURCE_BYTES:
                raise ValueError()
            paths[name] = path.read_bytes()
        return paths

    def check_assembly(self):
        config = self.config
        if (not isinstance(config["display_name"], str) or type(config["is_demo"]) is not bool
                or any(not isinstance(config[key]["display_name"], str) for key in ("object_1", "object_2"))
                or not isinstance(config["preserved_conditions"], list)
                or not all(isinstance(label, str) for label in config["preserved_conditions"])):
            raise ValueError()
        for field, size in (("position_m", 3), ("rotation_xyzw", 4)):
            vector = self.mounting[field]
            if not isinstance(vector, list) or len(vector) != size or not all(_is_number(x) for x in vector):
                raise ValueError()
        if not math.isclose(math.hypot(*self.mounting["rotation_xyzw"]), 1, abs_tol=1e-5):
            raise ValueError()
        guard = self.guard
        if (guard["axis"] not in AXES or guard["side"] not in {"below", "above"}
                or not _is_number(guard["limit_mm"]) or not _is_number(guard["tolerance_mm"])
                or not 0.0001 <= guard["tolerance_mm"] <= 0.01):
            raise ValueError()
        profile = config["profile"]
        parts = [item["part_id"] for item in profile["semantic_parts"]]
        self.mount_part = guard.get("part_id", "coupling_mount")
        self.outer_part = guard.get("outer_part_id", "outer_shell")
        if self.mount_part == self.outer_part or set(parts) != {self.mount_part, self.outer_part}:
            raise ValueError()
        fields = profile["parameters"]
        if not 1 <= len(fields) <= 20 or len({item["parameter_id"] for item in fields}) != len(fields):
            raise ValueError()
        for item in fields:
            if (not PARAMETER_NAME.fullmatch(item["parameter_id"]) or item["affected_part_ids"] != [self.outer_part]
                    or item["type"] not in ("number", "integer", "enum")):
                raise ValueError()
            if item["type"] == "enum" and not all(re.fullmatch(r"[\w .-]{1,64}", value) for value in item["allowed_values"]):
                raise ValueError()
        if any(item["applicable_part_ids"] != [self.outer_part] for item in profile["edit_capabilities"]):
            raise ValueError()
        for item in profile.get("measured_properties", []):
            if item["axis"] not in (0, 1, 2) or item["unit"] != "mm":
                raise ValueError()
        self.validate_parameters(self.describe(), default_parameters(profile))

    def describe(self):
        config = self.config
        profile = dict(config["profile"])
        source = {name: digest(data) for name, data in self.sources.items()}
        source_hash = digest(json_bytes({"assembly": config, "sources": source}))
        public_assembly = {key: config[key] for key in ("display_name", "is_demo", "preserved_conditions")}
        public_assembly.update(mode="openscad", mounting={key: self.mounting[key] for key in ("position_m", "rotation_xyzw")},
                               fixed_part_id=self.mount_part, editable_part_id=self.outer_part,
                               object_1={"display_name": config["object_1"]["display_name"]},
                               object_2={"display_name": config["object_2"]["display_name"]})
        profile.update(object_id="object_2", display_name=config["object_2"]["display_name"], design_units="mm",
                       capabilities={name: True for name in ("can_view", "can_annotate", "can_parametric_edit", "can_export_stl")},
                       source_hash=source_hash, source_asset_id="source_" + source_hash, assembly=public_assembly,
                       print_profile_metadata={"build_axis": "+Z", "process_validation": "not_performed"})
        if not config["is_demo"]:
            profile.pop("demo_utterance", None)
        profile.setdefault("default_constraints", [])
        profile.setdefault("measured_properties", [])
        return profile

    def validate_parameters(self, profile, params):
        result = super().validate_parameters(profile, params)
        if any(type(value) in (int, float) and not _is_number(value) for value in result.values()):
            raise GeometryError("OpenSCAD 수치는 유한한 값이어야 합니다.")
        return result

    def build(self, params):
        raise GeometryError("OpenSCAD 어댑터는 격리된 STL 생성 작업을 사용합니다.")

    def measure(self, geometry):
        size = bounds(geometry.vertices)["size"]
        return {field["property_id"]: size[field["axis"]] for field in self.config["profile"].get("measured_properties", [])}

    def freeze(self, folder):
        for name, data in self.sources.items():
            target = folder / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)

    def archive(self):
        output = io.BytesIO()
        with zipfile.ZipFile(output, "w") as archive:
            archive.writestr(zipfile.ZipInfo("assembly_config.json"), json_bytes(self.config), zipfile.ZIP_DEFLATED)
            for name, data in sorted(self.sources.items()):
                archive.writestr(zipfile.ZipInfo(name), data, zipfile.ZIP_DEFLATED)
        return output.getvalue()


def adapter():
    return OpenScadAdapter(Path(os.environ.get("OPENSCAD_ASSEMBLY_CONFIG") or SAMPLE).expanduser().resolve())


def load_profile():
    return adapter().describe()


def executable():
    configured = os.environ.get("OPENSCAD_EXECUTABLE")
    if configured:
        path = shutil.which(os.path.expanduser(configured))
    else:
        path = shutil.which("openscad") or next((str(candidate) for candidate in (
            Path("/Applications/OpenSCAD.app/Contents/MacOS/OpenSCAD"),
            Path("/Applications/OpenSCAD-2021.01.app/Contents/MacOS/OpenSCAD"),
        ) if candidate.is_file() and os.access(candidate, os.X_OK)), None)
    if not path:
        raise GeometryError("OpenSCAD 실행 파일을 찾을 수 없습니다. 설치 후 OPENSCAD_EXECUTABLE을 지정해 주세요.")
    return str(Path(path).resolve())


def _environment():
    return {key: value for key, value in os.environ.items() if key in {
        "PATH", "HOME", "SystemRoot", "TMP", "TEMP", "TMPDIR", "LANG", "LC_ALL", "DISPLAY", "QT_QPA_PLATFORM",
    }}


@lru_cache(maxsize=4)
def _version(path, _modified_at):
    try:
        result = subprocess.run([path, "--version"], env=_environment(), capture_output=True, timeout=4, check=True)
    except (OSError, subprocess.SubprocessError) as exc:
        raise GeometryError("OpenSCAD CLI를 시작하지 못했습니다. 실행 파일을 확인해 주세요.") from exc
    match = re.search(rb"OpenSCAD version\s+([^\r\n]+)", result.stdout + result.stderr)
    if not match:
        raise GeometryError("OpenSCAD CLI 버전을 확인하지 못했습니다.")
    return match[1].decode(errors="replace")[:100]


def _render(path, entry, params, artifact, deadline, cancelled):
    args = [path, "--export-format", "binstl", "-o", str(artifact)]
    for name, value in sorted(params.items()):
        args.extend(["-D", f"{name}={json.dumps(value, ensure_ascii=False, allow_nan=False)}"])
    args.append(str(entry))
    try:
        with subprocess.Popen(args, cwd=entry.parent, env=_environment(), stdout=subprocess.DEVNULL,
                              stderr=subprocess.PIPE) as worker:
            try:
                while True:
                    _check_job(deadline, cancelled)
                    try:
                        _, stderr = worker.communicate(timeout=0.05)
                        break
                    except subprocess.TimeoutExpired:
                        continue
            except BaseException:
                worker.kill()
                worker.communicate()
                raise
    except OSError as exc:
        raise GeometryError("OpenSCAD 모델 생성을 시작할 수 없습니다.") from exc
    if (worker.returncode != 0 or b"ERROR:" in stderr or not artifact.is_file()
            or artifact.stat().st_size > MAX_STL_BYTES):
        raise GeometryError("OpenSCAD가 유효한 STL을 만들지 못했습니다. 현재 원본과 매개변수를 확인해 주세요.")
    return artifact.read_bytes()


def _convert(stl, model):
    if len(stl) < 84:
        raise GeometryError("OpenSCAD 출력에서 바이너리 STL 헤더를 읽을 수 없습니다.")
    count = struct.unpack_from("<I", stl, 80)[0]
    if count < 4 or len(stl) != 84 + 50 * count:
        raise GeometryError("OpenSCAD 출력과 바이너리 STL 삼각형 수가 일치하지 않습니다.")
    vertices = []
    ids = {}
    original = []
    parts = {model.mount_part: [], model.outer_part: []}
    for index in range(count):
        coords = struct.unpack_from("<9f", stl, 96 + index * 50)
        face = tuple(zip(coords[::3], coords[1::3], coords[2::3]))
        if not all(_is_number(component) for point in face for component in point):
            raise GeometryError("OpenSCAD 출력에 유효하지 않은 좌표가 있습니다.")
        original.append(face)
        indices = []
        for x, y, z in face:
            point = (x, z, -y)  # Inverse of canonical-to-STL rotation, preserving the CAD origin.
            if point not in ids:
                ids[point] = len(vertices)
                vertices.append(point)
            indices.append(ids[point])
        part = model.mount_part if _at_guard(face, model.guard) else model.outer_part
        parts[part].append(tuple(indices))
    if not all(parts.values()):
        raise GeometryError("고정 결합 영역과 편집 외형을 모두 찾을 수 없습니다.")
    return MeshGeometry(vertices, parts), original


def _at_guard(face, guard):
    axis = AXES[guard["axis"]]
    boundary = guard["limit_mm"]
    sign = 1 if guard["side"] == "below" else -1
    return any(sign * (point[axis] - boundary) <= guard["tolerance_mm"] for point in face)


def _guard(faces, guard):
    tolerance = guard["tolerance_mm"]
    fixed = Counter(tuple(sorted(tuple(round(value / tolerance) for value in point) for point in face))
                    for face in faces if _at_guard(face, guard))
    return {"triangle_count": sum(fixed.values()), "sha256": digest(json_bytes(sorted(fixed.items())))}


def _surface_hash(faces):
    checksum = hashlib.sha256()
    for face in sorted(tuple(sorted(face)) for face in faces):
        checksum.update(struct.pack("<9f", *(value or 0.0 for vertex in face for value in vertex)))
    return checksum.hexdigest()


def build_model(root, version_id, profile, params, constraints=(), *, cancelled=None):
    if not isinstance(version_id, str) or not re.fullmatch(r"[a-zA-Z0-9_-]{1,128}", version_id):
        raise GeometryError("모델 버전 ID가 유효하지 않습니다.")
    model = adapter()
    current = model.describe()
    if current["source_hash"] != profile["source_hash"]:
        raise GeometryError("등록된 SCAD 원본이나 조립체 설정이 변경됐습니다. 새 회의를 시작해 주세요.")
    params = model.validate_parameters(current, params)
    path = executable()
    root = Path(root).resolve()
    root.mkdir(parents=True, exist_ok=True)
    deadline = time.monotonic() + max(0.1, float(os.getenv("CAD_JOB_TIMEOUT_SECONDS", "30")))
    while not _slots.acquire(timeout=0.05):
        _check_job(deadline, cancelled)
    try:
        _check_job(deadline, cancelled)
        with tempfile.TemporaryDirectory(prefix=".openscad-", dir=root) as name:
            sandbox = Path(name)
            source = sandbox / "source"
            model.freeze(source)
            entry = source / model.config["source_file"]
            modified = Path(path).stat().st_mtime_ns
            engine = {"name": "openscad", "version": _version(path, modified), "stl_format": "binary"}
            stl = _render(path, entry, params, sandbox / "candidate.stl", deadline, cancelled)
            mesh, faces = _convert(stl, model)
            guard = _guard(faces, model.guard)
            shape = _surface_hash(faces)
            initial = default_parameters(current)
            cache_key = (current["source_hash"], path, engine["version"], modified)
            if params != initial:
                with _baselines_lock:
                    baseline = _baselines.get(cache_key)
                if baseline is None:
                    original = _render(path, entry, initial, sandbox / "original.stl", deadline, cancelled)
                    _, original_faces = _convert(original, model)
                    baseline = (_guard(original_faces, model.guard), _surface_hash(original_faces))
                    _cache_baseline(cache_key, baseline)
                if baseline[0] != guard:
                    raise GeometryError("수정안이 고정 결합 영역을 변경했습니다. 이 후보는 표시하지 않습니다.")
                if baseline[1] == shape:
                    raise GeometryError("매개변수 값을 바꿨지만 실제 OpenSCAD 형상이 동일합니다.")
            result = _register(sandbox, root, version_id, model, current, params, constraints, mesh, stl, guard,
                               shape, engine, deadline, cancelled)
            if params == initial:
                _cache_baseline(cache_key, (guard, shape))
            return result
    finally:
        _slots.release()


def _cache_baseline(key, value):
    with _baselines_lock:
        _baselines[key] = value
        _baselines.move_to_end(key)
        while len(_baselines) > 4:
            _baselines.popitem(last=False)


def _register(sandbox, root, version, model, profile, params, constraints, mesh, stl, guard, shape, engine, deadline, cancelled):
    report = inspect_mesh(mesh)
    measured = model.measure(mesh)
    violations = violated_constraints(profile, measured, constraints)
    if violations:
        raise GeometryError("생성된 형상이 유지 조건과 충돌합니다. " + ", ".join(violations))
    preview, semantic = export_glb(mesh)
    # Every canonical triangle is a rotated original OpenSCAD triangle. The copy here
    # groups faces for validator iteration only. The exported file remains the original STL bytes.
    report.update(validate_exports(mesh, preview, export_stl(mesh)))
    report.update(measured_dimensions_mm=measured, violated_constraint_ids=[], export_success=True,
                  mesh_check_results={key: "passed" for key in report["mesh_checks_performed"]},
                  coupling_guard={"status": "passed", "region": model.guard, **guard},
                  surface_sha256=shape,
                  fixed_mounting=profile["assembly"]["mounting"], print_process_validation="not_performed",
                  physical_interference_validation="not_performed")
    raw_source = model.sources[model.config["source_file"]]
    files = {"print.stl": stl, "preview.glb": preview, "semantic_parts.json": json_bytes(semantic),
             "parameters.json": json_bytes(params), "geometry_report.json": json_bytes(report),
             "constraints.json": json_bytes(list(constraints)), "object_profile.json": json_bytes(profile),
             "object_2.scad": raw_source, "adapter_source_snapshot.zip": model.archive(),
             "assembly_config.json": json_bytes(model.config), "engine.json": json_bytes(engine)}
    assets = {name: describe_asset(version, name, data) for name, data in files.items()}
    assets["object_2.scad"]["media_type"] = "text/plain"
    assets["preview.glb"].update(bounds_m=report["bounds_m"], semantic_part_map_asset_id=assets["semantic_parts.json"]["asset_id"])
    assets["print.stl"]["bounds_mm"] = report["stl_bounds_mm"]
    geometry_hash = digest(stl)
    files["artifact_manifest.json"] = json_bytes({"model_version_id": version, "object_id": profile["object_id"],
        "source_hash": profile["source_hash"], "geometry_hash": geometry_hash, "build_mode": "generated",
        "engine": engine, "assets": assets})
    assets["artifact_manifest.json"] = describe_asset(version, "artifact_manifest.json", files["artifact_manifest.json"])
    staged = sandbox / "files"
    staged.mkdir()
    for name, data in files.items():
        (staged / name).write_bytes(data)
    _check_job(deadline, cancelled)
    try:
        staged.rename(root / version)
    except OSError as exc:
        raise GeometryError("이미 등록된 모델이거나 산출물을 저장할 수 없습니다.") from exc
    return {"version_id": version, "object_id": profile["object_id"], "adapter_id": profile["adapter_id"],
            "adapter_revision": profile["adapter_revision"], "source_hash": profile["source_hash"],
            "design_units": "mm", "full_parameters": params, "bounds_mm": report["bounds_mm"],
            "bounds_m": report["bounds_m"], "measured_dimensions_mm": measured, "geometry_hash": geometry_hash, "surface_sha256": shape,
            "build_mode": "generated", "engine": engine, "assets": assets,
            "preview_asset_id": assets["preview.glb"]["asset_id"], "stl_asset_id": assets["print.stl"]["asset_id"],
            "semantic_part_map_asset_id": assets["semantic_parts.json"]["asset_id"],
            "validation_report_asset_id": assets["geometry_report.json"]["asset_id"]}
