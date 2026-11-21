"""Resolve the registered array length without scaling its holes or margins."""
import math

from .contracts import MeetingError


def derived_fields(profile):
    fields = {field["parameter_id"]: field for field in profile["parameters"]}
    fixed = profile.get("assembly", {}).get("fixed_parameters", {})
    result = []
    for rule in profile.get("derived_parameters", []):
        if rule["formula"] != "array_span":
            raise MeetingError("지원하지 않는 계산 치수입니다.")
        count = fixed[rule["count_parameter"]]
        margin = fixed[rule["margin_parameter"]]
        spacing = fields[rule["spacing_parameter"]]
        if count <= 1:
            raise MeetingError("배열 길이를 조정하려면 두 개 이상의 홈이 필요합니다.")
        result.append({**rule, "type": "number", "unit": spacing["unit"],
                       "affected_part_ids": spacing["affected_part_ids"],
                       "multiplier": count - 1, "offset": 2 * margin,
                       "min_value": (count - 1) * spacing["min_value"] + 2 * margin,
                       "max_value": (count - 1) * spacing["max_value"] + 2 * margin})
    return result


def effective_values(profile, parameters):
    values = dict(parameters)
    for field in derived_fields(profile):
        values[field["parameter_id"]] = round(
            field["multiplier"] * parameters[field["spacing_parameter"]] + field["offset"], 9)
    return values


def normalize_patch(profile, changes):
    result = dict(changes)
    for field in derived_fields(profile):
        key = field["parameter_id"]
        if key not in result:
            continue
        value = result.pop(key)
        if type(value) not in (int, float) or not math.isfinite(value):
            raise MeetingError("계산 치수는 유한한 숫자여야 합니다.")
        if not field["min_value"] <= value <= field["max_value"]:
            raise MeetingError(f'{field["label"]}는 {field["min_value"]:g}–{field["max_value"]:g}mm 범위입니다.')
        pitch = round((value - field["offset"]) / field["multiplier"], 9)
        target = field["spacing_parameter"]
        if target in result and not math.isclose(result[target], pitch, abs_tol=1e-9):
            raise MeetingError("전체 길이와 홈 간격이 서로 맞지 않습니다.")
        result[target] = pitch
    return result


def validate_relations(profile, parameters):
    values = {**profile.get("assembly", {}).get("fixed_parameters", {}), **parameters}
    for rule in profile.get("parameter_constraints", []):
        actual = sum(values[name] * multiplier for name, multiplier in rule["terms"].items())
        limit = rule["value"]
        valid = actual > limit if rule["operator"] == "gt" else actual >= limit
        if not valid:
            raise MeetingError(rule["message"])
