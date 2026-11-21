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


def _mount_pose(value):
    if not isinstance(value, dict):
        raise ValueError()
    position, rotation = value["position_m"], value["rotation_xyzw"]
    if (not isinstance(position, list) or len(position) != 3 or not all(_is_number(x) for x in position)
            or not isinstance(rotation, list) or len(rotation) != 4 or not all(_is_number(x) for x in rotation)
            or not math.isclose(math.hypot(*rotation), 1, abs_tol=1e-5)):
        raise ValueError()
    return {"position_m": position, "rotation_xyzw": rotation}


def _qr_alignment(value):
    if value is None:
        return None
    try:
        payload = value["payload"]
        if (not isinstance(payload, str) or payload.strip() != payload or not payload.isprintable()
                or not 1 <= len(payload.encode("utf-8")) <= 128):
            raise ValueError()
        return {"payload": payload, "mounting": _mount_pose(value["mounting"])}
    except (KeyError, TypeError, ValueError) as error:
        raise GeometryError("QR 정렬 설정의 payload와 장착 위치 및 회전을 확인해 주세요.") from error


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
            self.mounting = _mount_pose(self.config["mounting"])
            self.qr_alignment = _qr_alignment(self.config.get("qr_alignment"))
            self.fixed_parameters = self.config.get("fixed_parameters", {})
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
        guard = self.guard
        if (guard["axis"] not in AXES or guard["side"] not in {"below", "above"}
                or not _is_number(guard["limit_mm"]) or not _is_number(guard["tolerance_mm"])
                or not 0.0001 <= guard["tolerance_mm"] <= 0.01):
            raise ValueError()
        if guard.get("method", "original_faces") not in {"original_faces", "clipped_solid", "clipped_convex_prism"}:
            raise ValueError()
        scripted_demo = config.get("scripted_demo")
        if scripted_demo is not None and (not isinstance(scripted_demo, str)
                                          or not re.fullmatch(r"[a-z_]{1,32}", scripted_demo)):
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
        editable = {item["parameter_id"] for item in fields}
        if (not isinstance(self.fixed_parameters, dict) or editable.intersection(self.fixed_parameters)
                or any(not PARAMETER_NAME.fullmatch(key) or not _is_number(value)
                       for key, value in self.fixed_parameters.items())):
            raise ValueError()
        varying = guard.get("vary_parameters", [])
        if (not isinstance(varying, list) or any(not isinstance(item, str) or item not in editable for item in varying)
                or len(varying) != len(set(varying))):
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
        parameter_defaults = {**defaults, **self.fixed_parameters}
        main_tokens = _tokens(self.original_text)
        self.parameter_edits = {name: span for name, span in _literal_edits(main_tokens, parameter_defaults).items()
                                if name in defaults}
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
            if name != original and shares_globals and _assignments(tokens, parameter_defaults):
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
        # Alignment, speech hints and approved comparison ranges do not change the CAD source.
        # Older sessions retain their own profile snapshot and source identity.
        cad_config = {key: value for key, value in config.items() if key not in {"qr_alignment", "comparison_maxima", "speech_vocabulary"}}
        source_hash = digest(json_bytes({"assembly": cad_config, "sources": source}))
        if config.get("speech_vocabulary"):
            profile["speech_vocabulary"] = config["speech_vocabulary"]
        maxima = config.get("comparison_maxima", {})
        if maxima:
            fields = {f["parameter_id"]: f for f in profile["parameters"]}
            if not isinstance(maxima, dict) or any(
                key not in fields or fields[key]["type"] != "number" or not _is_number(value)
                or value < fields[key]["default_value"] for key, value in maxima.items()
            ):
                raise ValueError("비교 치수 상한이 유효하지 않습니다.")
            profile["parameters"] = [{**f, "max_value": max(f["max_value"], maxima.get(f["parameter_id"], f["max_value"]))}
                                     if f["parameter_id"] in maxima else f for f in profile["parameters"]]
        public_assembly = {key: config[key] for key in ("display_name", "is_demo", "preserved_conditions")}
        public_assembly.update(mode="openscad", mounting=self.mounting,
                               fixed_part_id=self.mount_part, editable_part_id=self.outer_part,
                               object_1={"display_name": config["object_1"]["display_name"]},
                               object_2={"display_name": config["object_2"]["display_name"]})
        if self.qr_alignment is not None:
            public_assembly["qr_alignment"] = self.qr_alignment
        if self.fixed_parameters:
            public_assembly["fixed_parameters"] = self.fixed_parameters
        if config.get("scripted_demo"):
            public_assembly["scripted_demo"] = config["scripted_demo"]
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
        from .parameter_relations import validate_relations
        from .contracts import MeetingError
        try:
            validate_relations(profile, result)
        except MeetingError as error:
            raise GeometryError(str(error)) from error
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


def adapter_for_source(source_hash):
    model = adapter()
    if model.describe()["source_hash"] == source_hash:
        return model
    legacy = model.config.get("previous_assembly_config")
    if isinstance(legacy, str) and Path(legacy).name == legacy and legacy:
        old = OpenScadAdapter(model.profile_path.with_name(legacy))
        if old.describe()["source_hash"] == source_hash:
            return old
    raise GeometryError("등록된 SCAD 원본이나 조립체 설정이 변경됐습니다. 새 회의를 시작해 주세요.")


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
        result = subprocess.run([path, "--version"], env=_environment(), capture_output=True, timeout=30, check=True)
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


def _stl_faces(stl):
    if len(stl) < 84:
        raise GeometryError("OpenSCAD 출력에서 바이너리 STL 헤더를 읽을 수 없습니다.")
    count = struct.unpack_from("<I", stl, 80)[0]
    if count < 4 or len(stl) != 84 + 50 * count:
        raise GeometryError("OpenSCAD 출력과 바이너리 STL 삼각형 수가 일치하지 않습니다.")
    faces = []
    for index in range(count):
        coords = struct.unpack_from("<9f", stl, 96 + index * 50)
        face = tuple(zip(coords[::3], coords[1::3], coords[2::3]))
        if not all(_is_number(component) for point in face for component in point):
            raise GeometryError("OpenSCAD 출력에 유효하지 않은 좌표가 있습니다.")
        faces.append(face)
    return faces


def _convert(stl, model):
    original = _stl_faces(stl)
    vertices = []
    ids = {}
    parts = {model.mount_part: [], model.outer_part: []}
    for face in original:
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


def _prism_hash(faces, guard):
    """Certify the exported solid is a filled convex prism, independent of coplanar subdivision."""
    axis, tol = AXES[guard["axis"]], guard["tolerance_mm"]
    u, v = [i for i in range(3) if i != axis]
    verts = {p for tri in faces for p in tri}
    low, high = min(p[axis] for p in verts), max(p[axis] for p in verts)
    footprint = sorted({(p[u], p[v]) for p in verts if abs(p[axis] - low) < tol})
    def cross(a, b, c):
        return (b[0]-a[0])*(c[1]-a[1]) - (b[1]-a[1])*(c[0]-a[0])
    def chain(points):
        half = []
        for p in points:
            while len(half) > 1 and cross(half[-2], half[-1], p) <= 1e-10:
                half.pop()
            half.append(p)
        return half[:-1]
    hull = chain(footprint) + chain(reversed(footprint))
    if high-low <= tol or len(hull) < 3:
        raise GeometryError("고정 결합 영역에서 각기둥 단면을 확인할 수 없습니다.")
    edges = [(p, q, math.dist(p, q)) for p, q in zip(hull, hull[1:] + hull[:1])]
    area = sum(cross((0, 0), p, q) for p, q, _ in edges)/2
    caps = [0., 0.]
    for face in faces:
        xy = [(p[u], p[v]) for p in face]
        if all(abs(p[axis]-low) <= tol for p in face):
            caps[0] += abs(cross(*xy))/2
        elif all(abs(p[axis]-high) <= tol for p in face):
            caps[1] += abs(cross(*xy))/2
        elif not any(all(abs(cross(a,b,p)) <= tol*length
                         and min(a[0],b[0])-tol <= p[0] <= max(a[0],b[0])+tol
                         and min(a[1],b[1])-tol <= p[1] <= max(a[1],b[1])+tol for p in xy)
                     for a,b,length in edges):
            raise GeometryError("고정 결합 단면에 내부 구멍 또는 변형된 측면이 있습니다.")
    if any(abs(cap-area) > tol*sum(length for _,_,length in edges) for cap in caps):
        raise GeometryError("고정 결합 단면의 면적이 달라졌습니다.")
    return digest(json_bytes([round(low/tol), round(high/tol), [[round(x/tol),round(y/tol)] for x,y in hull]]))


def _protected_region(path, work, stl, faces, guard, deadline, cancelled):
    if guard.get("method") not in {"clipped_solid", "clipped_convex_prism"}:
        return _guard(faces, guard)
    # Clip the exported model so upper slot changes cannot alter a lower-region
    # comparison solely by changing the tessellation of tall side faces.
    limits = bounds([vertex for face in faces for vertex in face])
    lower = [value - 1 for value in limits["min"]]
    upper = [value + 1 for value in limits["max"]]
    axis = AXES[guard["axis"]]
    if guard["side"] == "below":
        upper[axis] = guard["limit_mm"]
    else:
        lower[axis] = guard["limit_mm"]
    size = [end - start for start, end in zip(lower, upper)]
    if any(value <= 0 for value in size):
        raise GeometryError("바닥 보호 영역이 실제 모델 범위에 없습니다.")
    work.mkdir()
    (work / "candidate.stl").write_bytes(stl)
    source = work / "guard.scad"
    source.write_text(
        'intersection() {\n'
        '    import("candidate.stl");\n'
        f'    translate({json.dumps(lower)}) cube({json.dumps(size)});\n'
        '}\n',
        encoding="ascii",
    )
    clipped = _stl_faces(_render(path, source, work / "fixed.stl", deadline, cancelled))
    return {"triangle_count": len(clipped), "sha256": _prism_hash(clipped, guard)
            if guard.get("method") == "clipped_convex_prism" else _surface_hash(clipped)}


def build_model(root, version_id, profile, params, constraints=(), *, cancelled=None):
    started = time.monotonic()
    if not isinstance(version_id, str) or not re.fullmatch(r"[a-zA-Z0-9_-]{1,128}", version_id):
        raise GeometryError("모델 버전 ID가 유효하지 않습니다.")
    model = adapter_for_source(profile["source_hash"])
    current = model.describe()
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
            if cached is None:
                stl = _render(path, entry, sandbox / "candidate.stl", deadline, cancelled)
                fixed_region = None
            else:
                stl, fixed_region = cached
            mesh, faces = _convert(stl, model)
            shape = _surface_hash(faces)
            initial = default_parameters(current)
            allowed_variations = {key: params[key] for key in model.guard.get("vary_parameters", [])}
            reference = {**initial, **allowed_variations}
            baseline_key = (*cache_key, digest(model.resolve(reference)))
            if fixed_region is None:
                fixed_region = _protected_region(path, sandbox / "guard", stl, faces, model.guard, deadline, cancelled)
            if params != reference:
                with _cache_lock:
                    baseline = _baselines.get(baseline_key)
                if baseline is None:
                    original_entry = model.write_source(sandbox / "baseline", model.resolve(reference))
                    original = _render(path, original_entry, sandbox / "original.stl", deadline, cancelled)
                    original_faces = _stl_faces(original)
                    baseline_region = _protected_region(path, sandbox / "baseline_guard", original, original_faces,
                                                        model.guard, deadline, cancelled)
                    baseline = (baseline_region, _surface_hash(original_faces))
                    _cache_set(_baselines, baseline_key, baseline)
                if baseline[0]["sha256"] != fixed_region["sha256"]:
                    raise GeometryError("수정안이 고정 결합 영역을 변경했습니다. 이 후보는 표시하지 않습니다.")
                if baseline[1] == shape:
                    raise GeometryError("매개변수 값을 바꿨지만 실제 OpenSCAD 형상이 동일합니다.")
            elif params != initial:
                # The registered variation itself may change the base length. Make
                # sure it changes actual geometry even if no other edit was made.
                initial_key = (*cache_key, digest(model.resolve(initial)))
                with _cache_lock:
                    initial_result = _baselines.get(initial_key)
                if initial_result is None:
                    initial_entry = model.write_source(sandbox / "initial", model.resolve(initial))
                    initial_stl = _render(path, initial_entry, sandbox / "initial.stl", deadline, cancelled)
                    initial_shape = _surface_hash(_stl_faces(initial_stl))
                else:
                    initial_shape = initial_result[1]
                if initial_shape == shape:
                    raise GeometryError("장착 예외 값을 바꿨지만 실제 OpenSCAD 형상이 동일합니다.")
            guard = {**fixed_region, "baseline_parameters": allowed_variations} if allowed_variations else fixed_region
            result = _register(sandbox, root, version_id, model, current, params, constraints, mesh, stl, guard,
                               shape, engine, source, deadline, cancelled,
                               {"source": "cache" if cached is not None else "openscad",
                                "cache_key": digest(json_bytes(render_key)), "engine": engine,
                                "elapsed_ms": round((time.monotonic() - started) * 1000)})
            _cache_set(_rendered, render_key, (stl, fixed_region))
            if params == reference:
                _cache_set(_baselines, baseline_key, (fixed_region, shape))
            return result
    finally:
        _slots.release()


def _cache_set(entries, key, value):
    with _cache_lock:
        entries[key] = value
        entries.move_to_end(key)
        while len(entries) > 12:
            entries.popitem(last=False)


def _register(sandbox, root, version, model, profile, params, constraints, mesh, stl, guard, shape, engine,
              source, deadline, cancelled, provenance):
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
        "geometry_provenance": provenance,
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
            "geometry_provenance": provenance,
            "preview_asset_id": assets["preview.glb"]["asset_id"], "stl_asset_id": assets["print.stl"]["asset_id"],
            "semantic_part_map_asset_id": assets["semantic_parts.json"]["asset_id"],
            "validation_report_asset_id": assets["geometry_report.json"]["asset_id"]}
