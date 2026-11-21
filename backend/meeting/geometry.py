"""Trusted model adapters and the bounded geometry worker interface."""

from abc import ABC, abstractmethod
import hashlib
from importlib import import_module
import io
import json
import math
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import threading
import time
import zipfile

from .mesh_geometry import GeometryError, export_glb, export_stl, inspect_mesh, validate_exports
from .model_adapters import PROFILES


ENGINE_REVISION = 1
REPOSITORY = Path(__file__).resolve().parents[2]
ASSET_TYPES = {
    "preview.glb": ("model/gltf-binary", "m", "gltf_y_up"),
    "print.stl": ("model/stl", "mm", "print_z_up"),
    "model.step": ("model/step", "mm", "design_y_up"),
    "adapter_source_snapshot.zip": ("application/zip", None, None),
}
_slots = threading.BoundedSemaphore(max(1, int(os.getenv("CAD_WORKER_CONCURRENCY", "1"))))


def json_bytes(value):
    return (json.dumps(value, ensure_ascii=False, sort_keys=True, allow_nan=False, indent=2) + "\n").encode("utf-8")


def digest(data):
    return hashlib.sha256(data).hexdigest()


class ModelAdapter(ABC):
    """Design behavior owned by the registered object, with common mesh export defaults."""

    def __init__(self, profile_path):
        self.profile_path = profile_path

    def source_files(self):
        module_path = Path(sys.modules[type(self).__module__].__file__)
        return [self.profile_path, module_path, Path(__file__), Path(__file__).with_name("mesh_geometry.py"),
                Path(__file__).with_name("geometry_worker.py")]

    def describe(self):
        profile = json.loads(self.profile_path.read_text(encoding="utf-8"))
        hashes = {str(p.relative_to(REPOSITORY)): digest(p.read_bytes()) for p in self.source_files()}
        profile["source_hash"] = digest(json_bytes(hashes))
        profile["source_asset_id"] = "source_" + profile["source_hash"]
        return profile

    def validate_parameters(self, profile, params):
        schema = {item["parameter_id"]: item for item in profile["parameters"]}
        if not isinstance(params, dict) or set(params) != set(schema):
            raise GeometryError("등록된 전체 매개변수를 제공해 주세요.")
        for name, value in params.items():
            field = schema[name]
            kind = field["type"]
            if kind in ("number", "integer"):
                types = (int, float) if kind == "number" else (int,)
                if type(value) not in types or not field["min_value"] <= value <= field["max_value"]:
                    raise GeometryError(f"{field['label']} 값은 {field['min_value']}~{field['max_value']} {field['unit']} 범위여야 합니다.")
            elif kind != "enum" or value not in field["allowed_values"]:
                raise GeometryError(f"{field['label']}에 등록된 값을 선택해 주세요.")
        return dict(params)

    def validate_patch(self, profile, base_params, operations, constraints):
        if not profile["capabilities"]["can_parametric_edit"]:
            raise GeometryError("이 모델에는 매개변수 편집이 등록되어 있지 않습니다.")
        params = self.validate_parameters(profile, base_params)
        schema = {item["parameter_id"]: item for item in profile["parameters"]}
        editable = {item["operation_id"]: item for item in profile["edit_capabilities"]}
        if not isinstance(operations, list) or not 1 <= len(operations) <= len(schema):
            raise GeometryError("한 개 이상의 등록된 편집을 입력해 주세요.")
        changed = set()
        for operation in operations:
            if not isinstance(operation, dict) or set(operation) != {"operation_id", "part_id", "parameter_id", "value", "unit"}:
                raise GeometryError("편집 명령의 필드가 등록된 형식과 다릅니다.")
            if any(type(operation[k]) is not str for k in ("operation_id", "part_id", "parameter_id", "unit")):
                raise GeometryError("편집 명령의 식별자와 단위는 문자열이어야 합니다.")
            name = operation["parameter_id"]
            field = schema.get(name)
            part_id = operation["part_id"]
            capability = editable.get(operation["operation_id"])
            if (not capability or operation["operation_id"] != "set_parameter" or not field
                    or part_id not in capability["applicable_part_ids"] or part_id not in field["affected_part_ids"]):
                raise GeometryError("선택한 부위에 등록된 편집을 사용해 주세요.")
            if name in changed or operation["unit"] != field["unit"]:
                raise GeometryError("편집 단위를 확인하고 각 매개변수를 한 번만 지정해 주세요.")
            params[name] = operation["value"]
            changed.add(name)
        if params == base_params:
            raise GeometryError("기존 모델과 다른 값을 입력해 주세요.")
        _constraint_rules(profile, constraints)
        return self.validate_parameters(profile, params)

    @abstractmethod
    def build(self, params):
        """Return canonical MeshGeometry in millimetres."""

    @abstractmethod
    def measure(self, geometry):
        """Return actual mesh measurements keyed by profile property ID."""

    def export_preview(self, geometry):
        return export_glb(geometry)

    def export_print(self, geometry):
        return {"print.stl": export_stl(geometry)}

    def get_diff(self, profile, before, after):
        return [{"parameter_id": field["parameter_id"], "label": field["label"], "unit": field["unit"],
                 "before": before[field["parameter_id"]], "after": after[field["parameter_id"]]}
                for field in profile["parameters"] if before[field["parameter_id"]] != after[field["parameter_id"]]]


def _adapter(object_id):
    registration = PROFILES.get(object_id)
    if not registration:
        raise GeometryError("등록되지 않은 ObjectProfile입니다.")
    module_name, class_name, profile_file = registration
    module = import_module(module_name, package=f"{__package__}.model_adapters")
    return getattr(module, class_name)(Path(module.__file__).with_name(profile_file))


def load_profile(object_id=None):
    object_id = object_id or os.getenv("ACTIVE_OBJECT_PROFILE") or next(iter(PROFILES))
    return _adapter(object_id).describe()


def list_profiles():
    return [load_profile(object_id) for object_id in PROFILES]


def default_parameters(profile):
    return {field["parameter_id"]: field["default_value"] for field in profile["parameters"]}


def validate_patch(profile, base_params, operations, constraints=()):
    return _adapter(profile["object_id"]).validate_patch(profile, base_params, operations, constraints)


def get_diff(profile, before, after):
    return _adapter(profile["object_id"]).get_diff(profile, before, after)


def _constraint_rules(profile, constraints):
    fields = {field["property_id"]: field for field in profile["measured_properties"]}
    rules = []
    if not isinstance(constraints, (list, tuple)):
        raise GeometryError("유지 조건 목록이 유효하지 않습니다.")
    for rule in (*profile["default_constraints"], *constraints):
        if not isinstance(rule, dict):
            raise GeometryError("유지 조건 형식이 유효하지 않습니다.")
        if rule.get("status", "active") != "active":
            continue
        field = fields.get(rule.get("property_id"))
        operator = rule.get("operator", "eq")
        value = rule.get("value")
        tolerance = rule.get("tolerance", 0.01)
        if (not field or rule.get("unit", field["unit"]) != field["unit"] or operator not in ("eq", "gte", "lte")
                or type(value) not in (int, float) or not (-1e9 <= value <= 1e9)
                or type(tolerance) not in (int, float) or not (0 <= tolerance <= 1e9)):
            raise GeometryError("등록된 치수·단위·비교 방식으로 유지 조건을 지정해 주세요.")
        rules.append((rule, operator, value, tolerance))
    return rules


def violated_constraints(profile, measured, constraints=(), *, hard_only=True):
    violations = []
    for rule, operator, value, tolerance in _constraint_rules(profile, constraints):
        if hard_only and rule.get("strength", "hard") != "hard":
            continue
        actual = measured.get(rule["property_id"])
        if type(actual) not in (int, float) or not math.isfinite(actual):
            raise GeometryError("생성된 형상에서 유지 치수를 측정할 수 없습니다.")
        passed = {
            "eq": abs(actual - value) <= tolerance,
            "gte": actual >= value - tolerance,
            "lte": actual <= value + tolerance,
        }[operator]
        if not passed:
            violations.append(rule.get("constraint_id", rule["property_id"]))
    return violations


def build_model(root, version_id, profile, params, constraints=(), *, cancelled=None):
    """Generate a version in an isolated process and publish its verified files together."""
    if not isinstance(version_id, str) or not re.fullmatch(r"[a-zA-Z0-9_-]{1,128}", version_id):
        raise GeometryError("모델 버전 ID가 유효하지 않습니다.")
    root = Path(root).resolve()
    root.mkdir(parents=True, exist_ok=True)
    deadline = time.monotonic() + max(0.1, float(os.getenv("CAD_JOB_TIMEOUT_SECONDS", "20")))
    while not _slots.acquire(timeout=0.05):
        _check_job(deadline, cancelled)
    try:
        _check_job(deadline, cancelled)
        with tempfile.TemporaryDirectory(dir=root, prefix=".geometry-") as temp:
            directory = Path(temp)
            request_file = directory / "request.json"
            request_file.write_bytes(json_bytes({"version_id": version_id, "object_id": profile["object_id"],
                "source_hash": profile["source_hash"], "parameters": params, "constraints": constraints}))
            worker_env = {key: value for key, value in os.environ.items()
                          if key not in {"AI_API_KEY", "AI_GATEWAY_TOKEN", "PROVIDER_API_KEY"}}
            with subprocess.Popen([sys.executable, "-m", "backend.meeting.geometry_worker", str(request_file)],
                                  cwd=REPOSITORY, env=worker_env, stdout=subprocess.PIPE,
                                  stderr=subprocess.PIPE, text=True) as worker:
                try:
                    while True:
                        _check_job(deadline, cancelled)
                        try:
                            output, _ = worker.communicate(timeout=0.05)
                            break
                        except subprocess.TimeoutExpired:
                            continue
                except BaseException:
                    worker.kill()
                    worker.communicate()
                    raise
            try:
                result = json.loads(output)
            except ValueError as exc:
                raise GeometryError("모델 생성 작업이 결과를 반환하지 못했습니다.") from exc
            if worker.returncode != 0:
                raise GeometryError(result.get("error", "모델 생성 작업이 실패했습니다."))
            _check_job(deadline, cancelled)
            try:
                (directory / "files").rename(root / version_id)
            except OSError as exc:
                raise GeometryError("이미 등록된 모델이거나 산출물을 저장할 수 없습니다.") from exc
            return result
    finally:
        _slots.release()


def _check_job(deadline, cancelled):
    if cancelled is not None and cancelled():
        raise GeometryError("모델 생성 작업이 취소되었습니다.")
    if time.monotonic() > deadline:
        raise GeometryError("모델 생성 제한 시간이 지났습니다.")


def build_artifacts(request, directory):
    """Worker entry point; no provider-generated code or paths enter this process."""
    adapter = _adapter(request["object_id"])
    profile = adapter.describe()
    if profile["source_hash"] != request["source_hash"]:
        raise GeometryError("등록된 모델 원본이 바뀌었습니다. 프로필을 다시 불러와 주세요.")
    version_id = request["version_id"]
    constraints = request["constraints"]
    params = adapter.validate_parameters(profile, request["parameters"])
    geometry = adapter.build(params)
    if not geometry.parts or not set(geometry.parts) <= {part["part_id"] for part in profile["semantic_parts"]}:
        raise GeometryError("모델에 등록되지 않은 부위 ID가 포함되어 있습니다.")
    report = inspect_mesh(geometry)
    measured = adapter.measure(geometry)
    violated = violated_constraints(profile, measured, constraints)
    if violated:
        raise GeometryError("생성된 형상이 유지 조건과 충돌합니다. " + ", ".join(violated))
    preview, part_map = adapter.export_preview(geometry)
    print_files = adapter.export_print(geometry)
    stl = print_files["print.stl"]
    report.update(validate_exports(geometry, preview, stl))
    report.update(measured_dimensions_mm=measured, violated_constraint_ids=violated,
                  soft_constraint_violations=violated_constraints(profile, measured, constraints, hard_only=False),
                  mesh_check_results={name: "passed" for name in report["mesh_checks_performed"]},
                  mesh_checks_not_performed=["self_intersection", "wall_thickness", "overhangs", "material", "fit", "strength"],
                  export_success=True, print_process_validation="not_performed")
    engine = {"name": "contextpin_parametric_mesh", "revision": ENGINE_REVISION, "python": sys.version.split()[0]}
    files = {"preview.glb": preview, **print_files, "semantic_parts.json": json_bytes(part_map),
             "geometry_report.json": json_bytes(report), "parameters.json": json_bytes(params),
             "constraints.json": json_bytes(list(constraints)), "object_profile.json": json_bytes(profile),
             "adapter_source_snapshot.zip": _source_archive(adapter, profile), "engine.json": json_bytes(engine)}
    assets = {name: describe_asset(version_id, name, data) for name, data in files.items()}
    assets["preview.glb"]["bounds_m"] = report["bounds_m"]
    assets["preview.glb"]["semantic_part_map_asset_id"] = assets["semantic_parts.json"]["asset_id"]
    assets["print.stl"]["bounds_mm"] = report["stl_bounds_mm"]
    geometry_hash = digest(stl)
    manifest = {"model_version_id": version_id, "object_id": profile["object_id"], "source_hash": profile["source_hash"],
                "geometry_hash": geometry_hash, "build_mode": "generated", "assets": assets, "engine": engine}
    files["artifact_manifest.json"] = json_bytes(manifest)
    assets["artifact_manifest.json"] = describe_asset(version_id, "artifact_manifest.json", files["artifact_manifest.json"])
    directory.mkdir()
    for name, data in files.items():
        (directory / name).write_bytes(data)
    return {"version_id": version_id, "object_id": profile["object_id"], "adapter_id": profile["adapter_id"],
            "adapter_revision": profile["adapter_revision"], "source_hash": profile["source_hash"],
            "design_units": profile["design_units"], "full_parameters": params, "bounds_mm": report["bounds_mm"],
            "bounds_m": report["bounds_m"], "measured_dimensions_mm": measured, "geometry_hash": geometry_hash,
            "build_mode": "generated", "assets": assets, "preview_asset_id": assets["preview.glb"]["asset_id"],
            "stl_asset_id": assets["print.stl"]["asset_id"], "validation_report_asset_id": assets["geometry_report.json"]["asset_id"],
            "semantic_part_map_asset_id": assets["semantic_parts.json"]["asset_id"]}


def describe_asset(version_id, name, data):
    media_type, units, frame = ASSET_TYPES.get(name, ("application/json", None, None))
    checksum = digest(data)
    asset_id = "asset_" + digest(f"{version_id}:{name}:{checksum}".encode("utf-8"))
    return {"asset_id": asset_id, "immutable_download_path": f"/v1/assets/{asset_id}",
            "model_version_id": version_id, "media_type": media_type, "sha256": checksum,
            "size_bytes": len(data), "units": units, "coordinate_frame": frame}


def _source_archive(adapter, profile):
    archive_data = io.BytesIO()
    with zipfile.ZipFile(archive_data, "w") as archive:
        for path in sorted(adapter.source_files()):
            archive.writestr(zipfile.ZipInfo(str(path.relative_to(REPOSITORY))), path.read_bytes(), zipfile.ZIP_DEFLATED)
        object_id = profile["object_id"]
        registry = Path(__file__).with_name("model_adapters") / "__init__.py"
        archive.writestr(zipfile.ZipInfo(str(registry.relative_to(REPOSITORY))),
                         f"PROFILES = {{{object_id!r}: {PROFILES[object_id]!r}}}\n", zipfile.ZIP_DEFLATED)
    return archive_data.getvalue()
