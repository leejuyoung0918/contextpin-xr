import json
import os
from pathlib import Path
import struct
import subprocess
import tempfile
import time
import unittest
from unittest.mock import patch
import zipfile

from .geometry import GeometryError, default_parameters, digest
from .mesh_geometry import MeshGeometry, export_stl
from .openscad_geometry import SAMPLE, OpenScadAdapter, _prism_hash, _render, _stl_faces, build_model, executable, load_profile
from .test_geometry import glb_width, stl_width


class OpenScadGeometryTests(unittest.TestCase):
    def setUp(self):
        try:
            executable()
        except GeometryError as exc:
            self.skipTest(str(exc))
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.root = Path(folder.name)
        environment = patch.dict(os.environ, {"OPENSCAD_ASSEMBLY_CONFIG": str(SAMPLE)})
        environment.start()
        self.addCleanup(environment.stop)

    def test_phone_stand_exports_keep_the_base_origin_and_exact_scale(self):
        config = Path(__file__).with_name("phone_stand") / "assembly.json"
        with patch.dict(os.environ, {"OPENSCAD_ASSEMBLY_CONFIG": str(config)}):
            profile = load_profile()
            defaults = default_parameters(profile)
            self.assertEqual(profile["review_layout"], "center_latest_side_candidates")
            # Published phone-stand versions must continue resolving the same SCAD source.
            self.assertEqual(profile["source_hash"], "497191d5005450ff767276b80443cc01ab5cb3528411b2f7edb39100cf40004a")
            self.assertEqual(set(defaults), {"viewing_angle", "backrest_length", "backrest_width",
                                             "base_width", "base_depth", "lip_height", "charging_notch_width"})
            hashes = set()
            for variant, values in [("original", defaults),
                                    ("small", {f["parameter_id"]: f["min_value"] for f in profile["parameters"]}),
                                    ("large", {f["parameter_id"]: f["max_value"] for f in profile["parameters"]})]:
                with self.subTest(variant=variant):
                    version = build_model(self.root, variant, profile, values)
                    folder = self.root / variant
                    mesh = version["bounds_m"]
                    self.assertEqual(mesh["min"][1], 0)
                    for axis, parameter in ((0, "base_width"), (2, "base_depth")):
                        half_extent_m = values[parameter] / 2000
                        self.assertAlmostEqual(mesh["min"][axis], -half_extent_m, places=6)
                        self.assertAlmostEqual(mesh["max"][axis], half_extent_m, places=6)
                    if variant == "original":
                        self.assertAlmostEqual(mesh["max"][1], 0.1279005, places=6)
                        self.assertEqual((folder / "candidate.scad").read_bytes(),
                                         config.with_name("phone_stand_parametric.scad").read_bytes())
                    manifest = json.loads((folder / "build.json").read_text())
                    self.assertEqual(manifest["preview"]["from_stl_sha256"], digest((folder / "print.stl").read_bytes()))
                    hashes.add(manifest["stl"]["surface_sha256"])
            self.assertEqual(len(hashes), 3)

    def test_local_corner_removes_only_material_above_the_selected_outer_corner(self):
        location = Path(__file__).resolve().parents[2] / "artifacts/cable-demo"
        if not (location/"corner-assembly.json").is_file():
            self.skipTest("Register the supplied cable CAD with --upper-corners")
        model = OpenScadAdapter(location/"corner-assembly.json")
        original = OpenScadAdapter(location/"assembly.json")
        defaults = default_parameters(model.describe())
        engine = executable()
        deadline = time.monotonic() + 100
        entries = {}
        for name, cad, params in [("original", original, default_parameters(original.describe())),
                                  ("baseline", model, defaults), ("changed", model, {**defaults, "upper_corner_nx_py": 1.7})]:
            entries[name] = cad.write_source(self.root/name, cad.resolve(params))
        def render(name, path):
            return _stl_faces(_render(engine, path, self.root/(name + ".stl"), deadline, None))
        def surface(faces):
            return {tuple(sorted(triangle)) for triangle in faces}
        self.assertEqual(surface(render("original", entries["original"])), surface(render("baseline", entries["baseline"])))
        removed = self.root/"removed.scad"
        removed.write_text("module v1() { include <baseline/candidate.scad>\n}\n"
                           "module v2() { include <changed/candidate.scad>\n}\ndifference(){v1(); v2();}")
        # Difference of the two actual CAD solids avoids roundoff from booleaning re-imported binary STLs.
        triangles = render("removed", removed)
        self.assertGreater(len(triangles), 0)
        self.assertTrue(all(-42.001 <= x <= -36.8 and 3.8 <= y <= 9.001 and 6 <= z <= 10.001
                            for face in triangles for x, y, z in face))

    def test_prism_guard_rejects_an_interior_wall(self):
        floor = [(0,0,0), (10,0,0), (10,8,0), (0,8,0)]
        roof = [(x,y,3.5) for x,y,_ in floor]
        triangles = [(cap[0],cap[1],cap[2]) for cap in [floor,roof]] + [(cap[0],cap[2],cap[3]) for cap in [floor,roof]]
        for i in range(4):
            j = (i+1)%4
            triangles.extend([(floor[i],floor[j],roof[j]), (floor[i],roof[j],roof[i])])
        guard = {"axis":"z", "tolerance_mm":0.001}
        self.assertTrue(_prism_hash(triangles,guard))
        with self.assertRaises(GeometryError):
            _prism_hash(triangles + [((4,4,1),(4,5,1),(4,4,3))],guard)

    def test_open_scad_uses_same_mounting_origin_and_fixed_surface_for_new_geometry(self):
        profile = load_profile()
        baseline = default_parameters(profile)
        original = build_model(self.root, "original", profile, baseline)
        full_params = {**baseline, "outer_rounding": 7}
        changed = build_model(self.root, "changed", profile, full_params)
        with patch("backend.meeting.openscad_geometry._render", side_effect=AssertionError("valid candidate was rerendered")):
            cached = build_model(self.root, "cached", profile, full_params)
        self.assertEqual(changed["geometry_hash"], cached["geometry_hash"])
        self.assertNotEqual(original["geometry_hash"], changed["geometry_hash"])
        self.assertEqual(original["bounds_mm"]["min"], [-50, 0, -24])
        self.assertEqual(original["bounds_mm"], changed["bounds_mm"])
        reports = [json.loads((self.root / item / "geometry_report.json").read_text()) for item in ("original", "changed")]
        self.assertEqual(reports[0]["coupling_guard"]["sha256"], reports[1]["coupling_guard"]["sha256"])
        self.assertEqual(reports[0]["fixed_mounting"], reports[1]["fixed_mounting"])
        self.assertAlmostEqual(glb_width(self.root / "changed" / "preview.glb"), 0.1, places=7)
        self.assertEqual(stl_width(self.root / "changed" / "print.stl"), 100)
        self.assertEqual((self.root / "changed" / "object_2.scad").read_bytes(), SAMPLE.with_name("object_2.scad").read_bytes())

    def test_cli_geometry_is_rejected_if_an_exterior_parameter_changes_a_mating_hole(self):
        config = self.root / "assembly.json"
        config.write_bytes(SAMPLE.read_bytes())
        original = SAMPLE.with_name("object_2.scad").read_text()
        changed = original.replace("r = 4.5,", "r = 4.5 + (outer_rounding - 3) * 0.1,")
        self.assertNotEqual(original, changed)
        (self.root / "object_2.scad").write_text(changed)
        with patch.dict(os.environ, {"OPENSCAD_ASSEMBLY_CONFIG": str(config)}):
            profile = load_profile()
            params = {**default_parameters(profile), "outer_rounding": 7}
            with self.assertRaisesRegex(GeometryError, "고정 결합 영역"):
                build_model(self.root / "models", "rejected", profile, params)
        self.assertFalse((self.root / "models" / "rejected").exists())

    def test_candidate_archive_rerenders_the_exact_surface_with_relative_dependencies(self):
        config, original = write_source_with_dependencies(self.root)
        with patch.dict(os.environ, {"OPENSCAD_ASSEMBLY_CONFIG": str(config)}):
            profile = load_profile()
            full_params = {**default_parameters(profile), "outer_rounding": 7}
            built = build_model(self.root / "models", "resolved", profile, full_params)
        model = self.root / "models" / built["version_id"]
        metadata = json.loads((model / "build.json").read_text())
        self.assertEqual(metadata["resolved_entry"], "parts/candidate.scad")
        self.assertEqual(metadata["parameter_overrides"], [])
        self.assertEqual((model / "object_2.scad").read_text(), original)
        self.assertEqual(json.loads((model / "parameters.json").read_text()), full_params)

        destination = self.root / "independent"
        with zipfile.ZipFile(model / "candidate_source.zip") as archive:
            self.assertEqual(set(archive.namelist()), {"parts/candidate.scad", *metadata["dependencies"]})
            archive.extractall(destination)
        source = destination / metadata["resolved_entry"]
        self.assertEqual(source.read_bytes(), (model / "candidate.scad").read_bytes())
        self.assertEqual(digest(source.read_bytes()), metadata["resolved_source"]["sha256"])
        for name, expected in metadata["dependencies"].items():
            self.assertEqual(digest((destination / name).read_bytes()), expected["sha256"])
        self.assertIn("outer_rounding = 7;", source.read_text())
        output = destination / "reproduced.stl"
        subprocess.run([executable(), "--export-format", "binstl", "-o", output, source],
                       cwd=destination, timeout=20, check=True, capture_output=True)
        original_stl = (model / "print.stl").read_bytes()
        reproduced = output.read_bytes()
        self.assertEqual(surface_triangles(original_stl), surface_triangles(reproduced))
        self.assertEqual(digest(original_stl), metadata["stl"]["sha256"])
        self.assertEqual(digest(original_stl), metadata["preview"]["from_stl_sha256"])
        self.assertEqual(digest((model / "preview.glb").read_bytes()), metadata["preview"]["sha256"])


class OpenScadSourceTests(unittest.TestCase):
    def test_qr_pose_is_exposed_without_changing_cad_identity(self):
        with tempfile.TemporaryDirectory() as name:
            config, _ = write_source_with_dependencies(Path(name))
            data = json.loads(config.read_text())
            qr = data.pop("qr_alignment")
            config.write_text(json.dumps(data))
            previous = OpenScadAdapter(config).describe()
            self.assertNotIn("qr_alignment", previous["assembly"])

            data["qr_alignment"] = qr
            config.write_text(json.dumps(data))
            current = OpenScadAdapter(config).describe()
            self.assertEqual(current["assembly"]["qr_alignment"], qr)
            self.assertEqual(current["assembly"]["mounting"], previous["assembly"]["mounting"])
            self.assertEqual(current["source_hash"], previous["source_hash"])

    def test_invalid_qr_configuration_is_reported(self):
        with tempfile.TemporaryDirectory() as name:
            config, _ = write_source_with_dependencies(Path(name))
            data = json.loads(config.read_text())
            mounting = data["qr_alignment"]["mounting"]
            for invalid in ({"payload": "wrong\npayload", "mounting": mounting},
                            {"payload": "valid", "mounting": {**mounting, "rotation_xyzw": [0, 0, 0, 0]}}):
                with self.subTest(qr=invalid):
                    data["qr_alignment"] = invalid
                    config.write_text(json.dumps(data))
                    with self.assertRaisesRegex(GeometryError, "QR 정렬 설정"):
                        OpenScadAdapter(config)

    def test_source_resolution_skips_comment_and_module_defaults(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            config, source = write_source_with_dependencies(root)
            source = ('// outer_rounding = 80;\n/* outer_rounding = 90; */\n'
                      'module unused(outer_rounding = 110) { outer_rounding = 120; }\n' + source)
            (root / "parts" / "object_2.scad").write_text(source)
            model = OpenScadAdapter(config)
            initial = default_parameters(model.describe())
            changed = model.resolve({**initial, "outer_rounding": 7}).decode()
            self.assertEqual(changed, source.replace("outer_rounding = 3;", "outer_rounding = 7;"))

    def test_unregistered_dependencies_or_overridden_defaults_are_rejected(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            config, _ = write_source_with_dependencies(root)
            dimensions = root / "lib" / "dimensions.scad"
            dimensions.write_text(dimensions.read_text() + "outer_rounding = 3;\n")
            with self.assertRaisesRegex(GeometryError, "진입 파일 하나"):
                OpenScadAdapter(config)
            dimensions.write_text("mount_width = 100;\n")
            data = json.loads(config.read_text())
            data["source_files"].remove("assets/slot.stl")
            config.write_text(json.dumps(data))
            with self.assertRaisesRegex(GeometryError, "source_files"):
                OpenScadAdapter(config)


def write_source_with_dependencies(root):
    config = root / "assembly.json"
    json_data = json.loads(SAMPLE.read_text())
    json_data.update(source_file="parts/object_2.scad", source_files=["lib/dimensions.scad", "lib/edges.scad", "assets/slot.stl"])
    config.write_text(json.dumps(json_data))
    for name in ("parts", "lib", "assets"):
        (root / name).mkdir()
    main = SAMPLE.with_name("object_2.scad").read_text().replace("cube([100, 48, mount_height])", "cube([mount_width, 48, mount_height])")
    main = main.replace("outer_rounding);", "edge_radius(outer_rounding));")
    main = main.replace("module mating_voids() {", 'module mating_voids() {\n    translate([-10, 0, -1]) import(file = "../assets/slot.stl");')
    main = 'include <../lib/dimensions.scad>\nuse <../lib/edges.scad>\n' + main
    (root / "parts" / "object_2.scad").write_text(main)
    (root / "lib" / "dimensions.scad").write_text("mount_width = 100;\n")
    (root / "lib" / "edges.scad").write_text("function edge_radius(radius) = radius;\n")
    cutter = MeshGeometry([(0, 0, 0), (3, 0, 0), (0, 3, 0), (0, 0, 3)],
                          {"slot": [(0, 2, 1), (0, 1, 3), (0, 3, 2), (1, 2, 3)]})
    (root / "assets" / "slot.stl").write_bytes(export_stl(cutter))
    return config, main


def surface_triangles(stl):
    return sorted(tuple(sorted(zip(triangle[3:12:3], triangle[4:12:3], triangle[5:12:3])))
                  for triangle in struct.iter_unpack("<12fH", stl[84:]))


if __name__ == "__main__":
    unittest.main()
