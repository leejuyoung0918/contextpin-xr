"""Exercise the scripted path against the running cable-assembly EDGE."""
import hashlib
from io import BytesIO
import json
import os
from urllib.request import Request, urlopen
from pathlib import Path
import time
import uuid
import zipfile


BASE = os.environ.get("MEETING_EDGE_URL", "http://127.0.0.1:8788").rstrip("/")


def http(path, body=None, token=None):
    headers = {"Authorization": f"Bearer {token}"} if token else {}
    data = json.dumps(body).encode() if body is not None else None
    if data is not None:
        headers["Content-Type"] = "application/json"
    with urlopen(Request(BASE + path, data=data, headers=headers), timeout=30) as response:
        return response.read() if path.endswith(".stl") or "/result.zip" in path else json.load(response)


def wait_for(sid, token, predicate):
    deadline = time.monotonic() + 90
    while time.monotonic() < deadline:
        snapshot = http(sid, token=token)
        if predicate(snapshot):
            return snapshot
        time.sleep(.15)
    raise AssertionError(f"Timed out: {snapshot.get('session_status')}, {snapshot.get('design_status')}")


def main():
    report = {"checks": []}
    create = http("/v1/sessions", {"display_name": "케이블 대본 검증", "device_id": uuid.uuid4().hex})
    prefix = "/v1/sessions/" + create["session_id"]
    token = create["token"]

    def command(kind, payload):
        return http(prefix + "/commands", {"command_id": uuid.uuid4().hex, "type": kind, "payload": payload}, token)

    initial = create["snapshot"]["versions"][0]
    assert abs(initial["measurements"]["width_mm"] - 84) < .01
    assert create["snapshot"]["assembly"]["fixed_parameters"]["slot_count"] == 5
    command("meeting_start", {"mode": "replay"})
    report["checks"].append("registered original is 84mm and the original SCAD has five slots")
    adopted = []
    base = initial
    for index, select in enumerate((0, 1), 1):
        replay = command("replay", {"sample": "cable", "target_part_id": "outer_shell",
                                    "target_version_id": base["version_id"]})
        round_id = replay["round_id"]
        snapshot = wait_for(prefix, token, lambda s: any(
            r["round_id"] == round_id and r["status"] in {"reviewable", "failed"} for r in s["rounds"]))
        round_ = next(r for r in snapshot["rounds"] if r["round_id"] == round_id)
        candidates = round_["candidates"]
        assert len(candidates) == 3 and all(c["status"] == "valid" for c in candidates), candidates
        assert round_["edit_base_version_id"] == base["version_id"]
        models_by_id = {model["version_id"]: model for model in snapshot["versions"]}
        versions = [models_by_id[c["version_id"]] for c in candidates]
        assert len({version["surface_sha256"] for version in versions}) == 3
        if index == 1:
            assert [model["parameters"]["groove_diameter"] for model in versions] == [5.6, 4.8, 4.8]
            assert [model["parameters"]["throat_width"] for model in versions] == [3.4, 4.2, 3.4]
        else:
            assert [round(model["measurements"]["width_mm"]) for model in versions] == [66, 60, 54]
            for model in versions:
                assert model["parameters"]["throat_width"] == base["parameters"]["throat_width"]
                assert model["parameters"]["mouth_width"] == base["parameters"]["mouth_width"]
                assert model["parameters"]["groove_diameter"] == base["parameters"]["groove_diameter"]
        for candidate in candidates:
            command("view", {"version_id": candidate["version_id"], "candidate_id": candidate["candidate_id"],
                             "round_id": round_id, "share": True, "part_ids": []})
        assert http(prefix, token=token)["adopted_version_id"] == base["version_id"]
        selected = candidates[select]
        decision = command("decide", {"round_id": round_id, "revision": round_["revision"],
                                      "expected_committed_version_id": base["version_id"],
                                      "expected_adopted_revision": index - 1, "outcome": "commit",
                                      "candidate_id": selected["candidate_id"]})
        assert decision["design_version"] == f"V{index+1}" and decision["rationale_source"] == "transcript", decision
        adopted.append(decision)
        base = versions[select]
    report["checks"].extend(["all six alternatives have distinct real geometry within their rounds",
                             "round 2 is based on the V2 adoption and preserves V2 opening and seat",
                             "V3 B measures 60 mm"])
    before_end = http(prefix, token=token)
    assert [item["label"] for item in before_end["cable_demo"]["history"]] == ["V1", "V2", "V3"]
    command("meeting_end", {})
    complete = wait_for(prefix, token, lambda s: s["session_status"] in {"completed", "save_error"})
    assert complete["session_status"] == "completed", complete["result"]
    result = http(complete["result"]["api_url"], token=token)
    assert result["manifest"]["adopted_version_id"] == adopted[-1]["selected_model_version_id"]
    assert result["manifest"]["utterance_count"] >= 14
    assert "선택 이유 미기록" not in result["summary_markdown"]
    raw = http(complete["result"]["zip_url"], token=token)
    with zipfile.ZipFile(BytesIO(raw)) as zipped:
        assert hashlib.sha256(zipped.read("cad/final.stl")).hexdigest() == base["assets"]["print.stl"]["sha256"]
        assert zipped.read(result["manifest"]["final_scad"]).decode() == result["final_scad"]
        assert any(name.startswith(f"candidates/{adopted[0]['candidate_id']}/") for name in zipped.namelist())
        history = json.loads(zipped.read("design_history.json"))
        assert [model["label"] for model in history] == ["V1", "V2", "V3"]
        assert [model["width_mm"] for model in history] == [84.0, 84.0, 60.0]
    report["checks"].append("ZIP contains exact V3 STL, runnable SCAD, V2, decisions, and the scripted transcript")
    report["status"] = "pass"
    out = Path("artifacts/cable-demo")
    out.mkdir(parents=True, exist_ok=True)
    (out / "demonstration-result.zip").write_bytes(raw)
    (out / "smoke-report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps(report, ensure_ascii=False))


if __name__ == "__main__":
    main()
