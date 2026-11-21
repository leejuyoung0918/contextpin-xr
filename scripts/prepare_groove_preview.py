"""Render the alignment demo's embedded GLBs from the registered cable organizer."""
import argparse
import json
import os
from pathlib import Path
import tempfile

from backend.meeting import geometry, openscad_geometry


ROOT = Path(__file__).resolve().parents[1]
RESOURCES = ROOT / "ContextPinRelay/Assets/Resources/CableGroovePreview"
VARIANTS = (
    ("A", "입구만 확대", {"throat_width": 5.4, "mouth_width": 8.0}),
    ("B", "내부만 확대", {"groove_diameter": 7.4}),
    ("C", "함께 소폭 확대", {"throat_width": 5.0, "mouth_width": 7.0, "groove_diameter": 6.8}),
    ("D", "함께 대폭 확대", {"throat_width": 6.6, "mouth_width": 9.4, "groove_diameter": 8.8}),
)
DIMENSIONS = {"mouth_width": "입구", "groove_diameter": "Ø"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, default=ROOT / "artifacts/cable-demo/assembly.json")
    args = parser.parse_args()
    model = openscad_geometry.OpenScadAdapter(args.config)
    if model.config["profile"]["adapter_id"] != "openscad_cable_organizer":
        parser.error("Use the registered cable organizer assembly config.")
    bundled = ROOT / "artifacts/tools/OpenSCAD.app/Contents/MacOS/OpenSCAD"
    if bundled.is_file():
        os.environ.setdefault("OPENSCAD_EXECUTABLE", str(bundled))
    baseline = geometry.default_parameters(model.describe())

    # The temporary config permits these specific demo values. The OpenSCAD worker
    # still checks geometry, the registered parameter constraints and the fixed base.
    for field in model.config["profile"]["parameters"]:
        key = field["parameter_id"]
        field["max_value"] = max(field["max_value"], *(patch.get(key, baseline[key]) for _, _, patch in VARIANTS))
    assets = {}
    variants = []
    with tempfile.TemporaryDirectory(prefix="cable-groove-preview-") as temporary:
        work = Path(temporary)
        for name, data in model.sources.items():
            target = work / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        config = work / "assembly.json"
        config.write_bytes(geometry.json_bytes(model.config))
        os.environ["OPENSCAD_ASSEMBLY_CONFIG"] = str(config)
        profile = openscad_geometry.load_profile()
        for letter, title, patch in VARIANTS:
            parameters = {**baseline, **patch}
            version = openscad_geometry.build_model(work / "exports", letter, profile, parameters)
            dimensions = " / ".join(f"{label} {patch[name]:.1f}" for name, label in DIMENSIONS.items() if name in patch)
            preview = version["assets"]["preview.glb"]
            variants.append({"id": letter, "title": title, "dimensions": f"{dimensions} mm",
                             "parameters_mm": {name: parameters[name] for name in ("throat_width", *DIMENSIONS)},
                             "sha256": preview["sha256"], "bounds_m": preview["bounds_m"]})
            assets[letter] = (work / "exports" / letter / "preview.glb").read_bytes()
            print(f"{letter}: {title} · {dimensions} mm", flush=True)

    RESOURCES.mkdir(parents=True, exist_ok=True)
    for letter, data in assets.items():
        (RESOURCES / f"{letter}.bytes").write_bytes(data)
    (RESOURCES / "variants.json").write_bytes(geometry.json_bytes({
        "source_sha256": geometry.digest(model.sources[model.config["source_file"]]),
        "variants": variants,
    }))


if __name__ == "__main__":
    main()
