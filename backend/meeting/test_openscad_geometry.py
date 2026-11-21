import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from .geometry import GeometryError, default_parameters
from .openscad_geometry import SAMPLE, build_model, executable, load_profile
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

    def test_open_scad_uses_same_mounting_origin_and_fixed_surface_for_new_geometry(self):
        profile = load_profile()
        baseline = default_parameters(profile)
        original = build_model(self.root, "original", profile, baseline)
        changed = build_model(self.root, "changed", profile, {**baseline, "outer_rounding": 7})
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


if __name__ == "__main__":
    unittest.main()
