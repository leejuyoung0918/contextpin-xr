"""Register the user-supplied cable organizer in ignored local storage."""
import argparse
import json
from pathlib import Path
import re
import shutil


ROOT = Path(__file__).resolve().parents[1]
TEMPLATE = ROOT / "backend/meeting/openscad_sample/cable-assembly.json"


def register_upper_corners(model, config):
    directory = config.parent
    source = model.read_text()
    if not re.search(r"(?m)^cable_organizer\(\);\s*\Z", source):
        raise ValueError("상부 모서리 편집에는 cable_organizer()를 호출하는 원본 파일이 필요합니다.")
    registered = json.loads(config.read_text())
    corners = {}
    for sx, x in [(1, "px"), (-1, "nx")]:
        for sy, y in [(1, "py"), (-1, "ny")]:
            key = f"upper_corner_{x}_{y}"
            corners[key] = [sx, sy]
            registered["profile"]["parameters"].append({
                "parameter_id": key, "label": f"상부 외곽 모서리 (X{sx:+d}, Y{sy:+d}) 반경 증가량",
                "type": "number", "unit": "mm", "default_value": 0, "min_value": 0,
                "max_value": 2, "step": 0.1, "affected_part_ids": ["outer_shell"]})
    declared = "\n".join(f"{key} = 0;" for key in corners)
    body = re.sub(r"(?m)^cable_organizer\(\);\s*\Z", "", source)
    entry = directory / "cable_with_upper_corners.scad"
    entry.write_text(body + declared + "\ninclude <upper_corner_finish.scad>\nfinished_cable_organizer();\n")
    shutil.copyfile(TEMPLATE.with_name("upper_corner_finish.scad"), directory / "upper_corner_finish.scad")
    registered.update(source_file=entry.name, source_files=[model.name, "upper_corner_finish.scad"],
                      previous_assembly_config=config.name)
    registered["coupling_guard"]["method"] = "clipped_convex_prism"
    registered["profile"]["upper_corners"] = {"parameters": corners, "min_z_mm": 6, "edge_band_mm": 5.2,
                                               "x_span": "body_length", "y_span": "body_depth",
                                               "archived_baseline": model.name}
    path = directory / "corner-assembly.json"
    path.write_text(json.dumps(registered, indent=2, ensure_ascii=False) + "\n")
    return path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="Path to the original cable_organizer.scad")
    parser.add_argument("--upper-corners", action="store_true", help="Register the localized upper-corner CAD for new meetings")
    args = parser.parse_args()
    source = args.source.expanduser().resolve()
    if not source.is_file() or source.suffix.lower() != ".scad":
        parser.error("Provide the original .scad file.")
    destination = ROOT / "artifacts/cable-demo"
    destination.mkdir(parents=True, exist_ok=True)
    model = destination / "cable_organizer.scad"
    if source != model:
        shutil.copyfile(source, model)
    config = destination / "assembly.json"
    if not config.exists():
        shutil.copyfile(TEMPLATE, config)
    else:
        registered = json.loads(config.read_text())
        template = json.loads(TEMPLATE.read_text())
        if registered.get("profile", {}).get("adapter_id") == template["profile"]["adapter_id"]:
            for key in ("derived_parameters", "edit_scope", "parameter_constraints"):
                registered["profile"].setdefault(key, template["profile"][key])
            defaults = {entry["parameter_id"]: entry for entry in template["profile"]["parameters"]}
            if "comparison_maxima" not in registered:
                registered["comparison_maxima"] = {
                    entry["parameter_id"]: template["comparison_maxima"][entry["parameter_id"]]
                    for entry in registered["profile"]["parameters"]
                    if entry["parameter_id"] in template["comparison_maxima"] and all(
                        entry.get(key) == defaults[entry["parameter_id"]].get(key)
                        for key in ("default_value", "min_value", "max_value"))
                }
            for entry in registered["profile"]["parameters"]:
                spec = defaults.get(entry["parameter_id"], {})
                if "step" in spec:
                    entry.setdefault("step", spec["step"])
            for key in ("corner_radius", "entry_chamfer_height"):
                registered["fixed_parameters"].setdefault(key, template["fixed_parameters"][key])
            config.write_text(json.dumps(registered, ensure_ascii=False, indent=2) + "\n")
    print(register_upper_corners(model, config) if args.upper_corners else config)


if __name__ == "__main__":
    main()
