import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest
import zipfile

from .geometry import GeometryError, build_model, default_parameters, digest, load_profile, validate_patch


class GeometryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.profile = load_profile("demo_fixture")
        self.params = default_parameters(self.profile)

    def build(self, name, operations=None, constraints=()):
        params = validate_patch(self.profile, self.params, operations, constraints) if operations else self.params
        return build_model(self.root, name, self.profile, params, constraints)

    def edit(self, parameter_id, value, part_id="body", unit="mm"):
        return [{"operation_id": "set_parameter", "part_id": part_id, "parameter_id": parameter_id,
                 "unit": unit, "value": value}]

    def test_preview_and_stl_share_actual_dimensions_and_version(self):
        original = self.build("initial")
        modified = self.build("wider", self.edit("width_mm", 123.4))
        for version, width in ((original, 100), (modified, 123.4)):
            directory = self.root / version["version_id"]
            self.assertAlmostEqual(glb_width(directory / "preview.glb"), width / 1000, places=7)
            self.assertAlmostEqual(stl_width(directory / "print.stl"), width, places=4)
            manifest = json.loads((directory / "artifact_manifest.json").read_bytes())
            self.assertEqual(manifest["model_version_id"], version["version_id"])
            self.assertEqual(manifest["geometry_hash"], digest((directory / "print.stl").read_bytes()))
            for name, metadata in manifest["assets"].items():
                self.assertEqual(metadata["sha256"], digest((directory / name).read_bytes()))
            report = json.loads((directory / "geometry_report.json").read_bytes())
            self.assertEqual(report["print_process_validation"], "not_performed")
            self.assertEqual(report["solid_count"], 1)
        self.assertNotEqual(original["geometry_hash"], modified["geometry_hash"])

    def test_three_curvature_interpretations_keep_measured_height(self):
        constraint = {"property_id": "height_mm", "operator": "eq", "value": 36, "constraint_id": "keep-height"}
        hashes = set()
        for i, example in enumerate(self.profile["demo_interpretations"]):
            parameter = example["parameter_id"]
            operations = self.edit(parameter, self.params[parameter] + example["delta"], example["part_id"])
            version = self.build(f"option-{i}", operations, [constraint])
            self.assertEqual(version["measured_dimensions_mm"]["height_mm"], 36)
            hashes.add(version["geometry_hash"])
        self.assertEqual(len(hashes), 3)
        with self.assertRaisesRegex(GeometryError, "keep-height"):
            self.build("bad-height", self.edit("height_mm", 45), [constraint])
        self.assertFalse((self.root / "bad-height").exists())

    def test_permission_to_edit_is_profile_specific(self):
        with self.assertRaises(GeometryError):
            self.build("unknown-part", self.edit("rim_bevel_mm", 4, "base"))
        with self.assertRaises(GeometryError):
            self.build("incorrect-unit", self.edit("width_mm", 110, unit="m"))
        operation = {**self.edit("width_mm", 110)[0], "code": "raise RuntimeError()"}
        with self.assertRaises(GeometryError):
            self.build("unexpected-code", [operation])
        with self.assertRaises(GeometryError):
            self.build("excessive", self.edit("width_mm", 500))
        self.assertEqual(list(self.root.iterdir()), [])

    def test_cancellation_never_publishes_a_partly_built_version(self):
        with self.assertRaises(GeometryError):
            build_model(self.root, "cancelled", self.profile, self.params, cancelled=lambda: True)
        self.assertEqual(list(self.root.iterdir()), [])

    def test_extracted_adapter_source_can_rebuild_exact_printer_mesh(self):
        original = self.build("initial")
        source_dir = self.root / "source"
        with zipfile.ZipFile(self.root / "initial" / "adapter_source_snapshot.zip") as source:
            source.extractall(source_dir)
        rebuilt = subprocess.check_output([sys.executable, "-c", """
from backend.meeting.geometry import load_profile, default_parameters, build_model
profile = load_profile()
print(build_model('output', 'from_source', profile, default_parameters(profile))['geometry_hash'])
"""], cwd=source_dir, text=True).strip()
        self.assertEqual(rebuilt, original["geometry_hash"])

    def test_switching_to_a_registered_model_changes_geometry_and_semantic_parts(self):
        self.build("initial")
        source_dir = self.root / "source"
        with zipfile.ZipFile(self.root / "initial" / "adapter_source_snapshot.zip") as source:
            source.extractall(source_dir)
        registration = source_dir / "backend" / "meeting" / "model_adapters"
        with (registration / "__init__.py").open("a") as registry:
            registry.write("PROFILES['test_wedge'] = ('.test_wedge', 'Wedge', 'test_wedge.json')\n")
        (registration / "test_wedge.py").write_text('''
from ..geometry import ModelAdapter
from ..mesh_geometry import MeshGeometry, bounds

class Wedge(ModelAdapter):
    def build(self, params):
        points = [(0, 0, 0), (params['length'], 0, 0), (0, 40, 0), (0, 0, 60)]
        return MeshGeometry(points, {'slope': [(0, 2, 1), (0, 1, 3), (0, 3, 2), (1, 2, 3)]})

    def measure(self, geometry):
        return {'length': bounds(geometry.vertices)['size'][0]}
''', encoding="utf-8")
        (registration / "test_wedge.json").write_text(json.dumps({
            "object_id": "test_wedge", "adapter_id": "tetrahedron", "adapter_revision": 1, "design_units": "mm",
            "semantic_parts": [{"part_id": "slope"}], "default_constraints": [], "measured_properties": [],
            "parameters": [{"parameter_id": "length", "type": "number", "unit": "mm", "default_value": 80,
                            "min_value": 40, "max_value": 150}],
        }), encoding="utf-8")
        subprocess.check_call([sys.executable, "-c", """
from backend.meeting.geometry import load_profile, default_parameters, build_model
profile = load_profile()
assert profile['object_id'] == 'test_wedge'
build_model('output', 'wedge', profile, default_parameters(profile))
"""], cwd=source_dir, env={**os.environ, "ACTIVE_OBJECT_PROFILE": "test_wedge"})
        version = source_dir / "output" / "wedge"
        self.assertAlmostEqual(glb_width(version / "preview.glb"), 0.08, places=7)
        self.assertEqual([p["part_id"] for p in json.loads((version / "semantic_parts.json").read_text())["parts"]], ["slope"])


def glb_width(path):
    data = path.read_bytes()
    json_length = struct.unpack_from("<I", data, 12)[0]
    document = json.loads(data[20:20 + json_length])
    binary_start = 28 + json_length
    xs = []
    for mesh in document["meshes"]:
        accessor = document["accessors"][mesh["primitives"][0]["attributes"]["POSITION"]]
        view = document["bufferViews"][accessor["bufferView"]]
        start = binary_start + view["byteOffset"]
        xs.extend(x for x, _, _ in struct.iter_unpack("<3f", data[start:start + view["byteLength"]]))
    return max(xs) - min(xs)


def stl_width(path):
    triangles = struct.iter_unpack("<12fH", path.read_bytes()[84:])
    xs = [coordinate for triangle in triangles for coordinate in triangle[3:12:3]]
    return max(xs) - min(xs)
