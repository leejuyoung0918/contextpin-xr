"""Run trusted OpenSCAD assembly sources and preview the exact selected STL."""

from collections import Counter, OrderedDict
from functools import lru_cache
import hashlib
import io
import json
import math
import os
from pathlib import Path
import posixpath
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
SCAD_TOKENS = re.compile(
    r'(?P<comment>//[^\r\n]*|/\*[\s\S]*?\*/)|(?P<string>"(?:\\[\s\S]|[^"\\])*")|'
    r'(?P<directive>\b(?:include|use)\s*<[^>\r\n]+>)|(?P<identifier>[a-zA-Z_$][a-zA-Z_0-9$]*)|[^\s]'
)
SCAD_NUMBER = re.compile(r"[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?\Z")
STL_ARGUMENTS = ("--export-format", "binstl")
AXES = {"x": 0, "y": 1, "z": 2}
_baselines = OrderedDict()
_rendered = OrderedDict()
_cache_lock = threading.Lock()


def _is_number(value):
    return type(value) in (int, float) and -1e9 <= value <= 1e9


def _file_info(data):
    return {"sha256": digest(data), "size_bytes": len(data)}


def _tokens(source):
    return [token for token in SCAD_TOKENS.finditer(source) if token.lastgroup != "comment"]


def _assignments(tokens, names):
    """Locate declarations at file scope without reading comments, strings, or module locals as code."""
    result = {}
    depth = 0
    for index, token in enumerate(tokens):
        value = token.group()
        if token.lastgroup == "identifier" and value in names and depth == 0:
            following = tokens[index + 1:index + 3]
            if len(following) == 2 and following[0].group() == "=" and following[1].group() != "=":
                if value in result:
                    raise GeometryError(f"OpenSCAD의 최상위 매개변수 {value}가 중복됩니다.")
                result[value] = index + 2
        depth += (value in ("{", "(", "[")) - (value in ("}", ")", "]"))
    return result


def _literal_edits(tokens, defaults):
    assignments = _assignments(tokens, defaults)
    edits = {}
    for name, value in defaults.items():
        start = assignments.get(name)
        if start is None:
            raise GeometryError(f"OpenSCAD 원본의 맨 위에서 {name}의 기본값을 선언해 주세요.")
        end = next((index for index in range(start, len(tokens)) if tokens[index].group() == ";"), start)
        raw = "".join(item.group() for item in tokens[start:end])
        if isinstance(value, str) and end == start + 1 and tokens[start].lastgroup == "string":
            actual = json.loads(raw)
        elif type(value) in (int, float) and SCAD_NUMBER.fullmatch(raw):
            # Read the untouched token span to reject adjacent tokens such as `1 2`.
            first, last = tokens[start], tokens[end - 1]
            literal = first.string[first.start():last.end()]
            actual = float(literal) if SCAD_NUMBER.fullmatch(literal) else None
        else:
            actual = None
        if actual != value:
            raise GeometryError(f"OpenSCAD의 {name}에는 설정과 같은 숫자 또는 enum 기본값을 선언해 주세요.")
        edits[name] = (tokens[start].start(), tokens[end - 1].end())
    return edits


def _static_assets(tokens):
    for index, token in enumerate(tokens):
        value = token.group()
        if token.lastgroup == "directive":
            prefix, path = value.split("<", 1)
            yield prefix.strip(), path[:-1].strip()
        if value not in {"import", "surface"} or index + 1 == len(tokens) or tokens[index + 1].group() != "(":
            continue
        arguments = []
        argument = []
        depth = 0
        for item in tokens[index + 2:]:
            symbol = item.group()
            if depth == 0 and symbol in {",", ")"}:
                arguments.append(argument)
                if symbol == ")":
                    break
                argument = []
                continue
            depth += (symbol in ("[", "(")) - (symbol in ("]", ")"))
            argument.append(item)
        filename = next((arg[2:] for arg in arguments if [t.group() for t in arg[:2]] == ["file", "="]),
                        arguments[0] if arguments else [])
        if len(filename) != 1 or filename[0].lastgroup != "string":
            raise GeometryError("OpenSCAD import()와 surface()의 파일에는 상대 경로 문자열을 사용해 주세요.")
        yield value, json.loads(filename[0].group())


class OpenScadAdapter(ModelAdapter):
    def __init__(self, config_path):
        config_path = Path(config_path).resolve()
        super().__init__(config_path)
        try:
            if config_path.stat().st_size > 256 * 1024:
                raise ValueError()
            self.config = json.loads(config_path.read_text(encoding="utf-8"))
            self.sources = self.read_sources()
            self.guard = self.config["coupling_guard"]
            self.mounting = self.config["mounting"]
            self.check_assembly()
            self.check_source()
        except GeometryError:
            raise
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

    def check_source(self):
        original = self.config["source_file"]
        self.original_text = self.sources[original].decode("utf-8")
        defaults = default_parameters(self.config["profile"])
        main_tokens = _tokens(self.original_text)
        self.parameter_edits = _literal_edits(main_tokens, defaults)
        self.resolved_entry = str(Path(original).with_name("candidate.scad"))
        if self.resolved_entry != original and self.resolved_entry in self.sources:
            raise GeometryError("candidate.scad 파일 이름은 실행 가능한 수정안에 사용됩니다.")
        checked = set()
        pending = [(original, main_tokens, True)]
        while pending:
            name, tokens, shares_globals = pending.pop()
            if (name, shares_globals) in checked:
                continue
            checked.add((name, shares_globals))
            if name != original and shares_globals and _assignments(tokens, defaults):
                raise GeometryError("편집할 OpenSCAD 매개변수의 최상위 선언은 원본 진입 파일 하나에만 두세요.")
            for dependency_kind, target in _static_assets(tokens):
                path = posixpath.normpath(posixpath.join(posixpath.dirname(name), target))
                if path not in self.sources or target.startswith("/") or "\\" in target or path == original:
                    raise GeometryError(f"OpenSCAD 참조 파일 {target}을 source_files에 상대 경로로 등록해 주세요.")
                if dependency_kind in {"include", "use"}:
                    pending.append((path, _tokens(self.sources[path].decode("utf-8")), shares_globals and dependency_kind == "include"))

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

    def resolve(self, params):
        source = self.original_text
        changes = [(start, end, json.dumps(params[name], ensure_ascii=False, allow_nan=False))
                   for name, (start, end) in self.parameter_edits.items()]
        for start, end, replacement in sorted(changes, reverse=True):
            source = source[:start] + replacement + source[end:]
        return source.encode("utf-8")

    def write_source(self, folder, source):
        for name, data in self.source_dependencies().items():
            target = folder / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        entry = folder / self.resolved_entry
        entry.parent.mkdir(parents=True, exist_ok=True)
        entry.write_bytes(source)
        return entry

    def source_dependencies(self):
        return {name: data for name, data in self.sources.items() if name != self.config["source_file"]}

    def resolved_archive(self, source):
        output = io.BytesIO()
        with zipfile.ZipFile(output, "w") as archive:
            for name, data in sorted({**self.source_dependencies(), self.resolved_entry: source}.items()):
                archive.writestr(zipfile.ZipInfo(name), data, zipfile.ZIP_DEFLATED)
        return output.getvalue()

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
        result = subprocess.run([path, "--version"], env=_environment(), capture_output=True, timeout=10, check=True)
    except (OSError, subprocess.SubprocessError) as exc:
        raise GeometryError("OpenSCAD CLI를 시작하지 못했습니다. 실행 파일을 확인해 주세요.") from exc
    match = re.search(rb"OpenSCAD version\s+([^\r\n]+)", result.stdout + result.stderr)
    if not match:
        raise GeometryError("OpenSCAD CLI 버전을 확인하지 못했습니다.")
    return match[1].decode(errors="replace")[:100]


def _render(path, entry, artifact, deadline, cancelled):
    args = [path, *STL_ARGUMENTS, "-o", str(artifact), entry.name]
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
            source = model.resolve(params)
            entry = model.write_source(sandbox / "source", source)
            modified = Path(path).stat().st_mtime_ns
            engine = {"name": "openscad", "version": _version(path, modified), "stl_format": "binary"}
            cache_key = (current["source_hash"], path, engine["version"], modified, STL_ARGUMENTS)
            render_key = (*cache_key, digest(source))
            with _cache_lock:
                cached = _rendered.get(render_key)
            stl = cached if cached is not None else _render(path, entry, sandbox / "candidate.stl", deadline, cancelled)
            mesh, faces = _convert(stl, model)
            guard = _guard(faces, model.guard)
            shape = _surface_hash(faces)
            initial = default_parameters(current)
            if params != initial:
                with _cache_lock:
                    baseline = _baselines.get(cache_key)
                if baseline is None:
                    original_entry = model.write_source(sandbox / "baseline", model.resolve(initial))
                    original = _render(path, original_entry, sandbox / "original.stl", deadline, cancelled)
                    _, original_faces = _convert(original, model)
                    baseline = (_guard(original_faces, model.guard), _surface_hash(original_faces))
                    _cache_set(_baselines, cache_key, baseline)
                if baseline[0] != guard:
                    raise GeometryError("수정안이 고정 결합 영역을 변경했습니다. 이 후보는 표시하지 않습니다.")
                if baseline[1] == shape:
                    raise GeometryError("매개변수 값을 바꿨지만 실제 OpenSCAD 형상이 동일합니다.")
            result = _register(sandbox, root, version_id, model, current, params, constraints, mesh, stl, guard,
                               shape, engine, source, deadline, cancelled)
            _cache_set(_rendered, render_key, stl)
            if params == initial:
                _cache_set(_baselines, cache_key, (guard, shape))
            return result
    finally:
        _slots.release()


def _cache_set(entries, key, value):
    with _cache_lock:
        entries[key] = value
        entries.move_to_end(key)
        while len(entries) > 4:
            entries.popitem(last=False)


def _register(sandbox, root, version, model, profile, params, constraints, mesh, stl, guard, shape, engine,
              source, deadline, cancelled):
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
    original_entry = model.config["source_file"]
    raw_source = model.sources[original_entry]
    parameters = json_bytes(params)
    resolved_archive = model.resolved_archive(source)
    build = {
        "schema_version": 1, "engine": engine,
        "source_hash": profile["source_hash"], "original_entry": original_entry,
        "resolved_entry": model.resolved_entry, "input_path": model.resolved_entry,
        "argument_template": [*STL_ARGUMENTS, "-o", "<output.stl>", model.resolved_entry],
        "parameters_via": "top_level_literal_assignments", "parameter_overrides": [],
        "original_source": _file_info(raw_source), "resolved_source": _file_info(source),
        "dependencies": {path: _file_info(data) for path, data in model.source_dependencies().items()},
        "candidate_source_archive": _file_info(resolved_archive), "parameters": _file_info(parameters),
        "stl": {**_file_info(stl), "units": "mm", "surface_sha256": shape},
        "preview": {**_file_info(preview), "from_stl_sha256": digest(stl), "units": "m",
                    "coordinate_transform": "(x,z,-y)/1000", "simplification": "none"},
    }
    files = {"print.stl": stl, "preview.glb": preview, "semantic_parts.json": json_bytes(semantic),
             "parameters.json": parameters, "geometry_report.json": json_bytes(report),
             "constraints.json": json_bytes(list(constraints)), "object_profile.json": json_bytes(profile),
             "object_2.scad": raw_source, "adapter_source_snapshot.zip": model.archive(),
             "candidate.scad": source, "candidate_source.zip": resolved_archive, "build.json": json_bytes(build),
             "assembly_config.json": json_bytes(model.config), "engine.json": json_bytes(engine)}
    assets = {name: describe_asset(version, name, data) for name, data in files.items()}
    assets["object_2.scad"]["media_type"] = "text/plain"
    assets["candidate.scad"]["media_type"] = "text/plain"
    assets["candidate_source.zip"]["media_type"] = "application/zip"
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
            "build_mode": "generated", "engine": engine, "assets": assets, "candidate_input_path": model.resolved_entry,
            "preview_asset_id": assets["preview.glb"]["asset_id"], "stl_asset_id": assets["print.stl"]["asset_id"],
            "semantic_part_map_asset_id": assets["semantic_parts.json"]["asset_id"],
            "validation_report_asset_id": assets["geometry_report.json"]["asset_id"]}
