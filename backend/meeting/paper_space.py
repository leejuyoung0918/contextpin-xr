"""Session-relative A4 layout. Only poses in the numbered paper frame are shared."""
import math

from .contracts import MeetingError, now_ms, text_field
from .edit_bundle import pose, transform_point, vector

COMMAND_FIELDS = {
    "paper_alignment": {"status"},
    "paper_layout": {"latest_version_id", "latest_pose_paper", "active_candidate_set_id", "candidate_poses_paper"},
    "paper_reset": set(),
    "paper_selection": {"model_version_id", "selection_id", "ray_origin_paper", "ray_direction_paper", "hit_position_paper"},
    "paper_candidate_select": {"candidate_set_id", "candidate_id"},
}
CLOCK_FIELDS = {"source_monotonic_ms", "clock_offset_ms", "clock_uncertainty_ms"}
RAY_FIELDS = ("hit_position_paper", "ray_origin_paper", "ray_direction_paper")
# Existing single-device evidence remains in the immutable bundle on the PC.
DEVICE_POSES = {"ray_origin_world", "ray_direction_world", "hit_world_position", "hit_normal_world",
                "camera_pose", "workspace_pose", "latest_model_pose"}


def public_evidence(source):
    return {key: value for key, value in source.items() if key not in DEVICE_POSES}


def new_frame(revision=1):
    return {"paper_frame_revision": revision, "latest_pose_paper": None, "alignments": {},
            "layout_set": None, "candidate_poses_paper": {}, "selected_candidate_id": None,
            "selected_by_participant_id": None, "shared_selection": None}


def current_set(state):
    # Use the most recently queued request on the current head. A pending or failed
    # replacement must not resurrect the preceding ready layout for late participants.
    latest = state["committed_version_id"]
    sets = state.get("candidate_sets", {})
    if "last_paper_candidate_request" in state:
        current = sets.get(state["last_paper_candidate_request"])
        return current if (current and current["base_version_id"] == latest and
                           current["constraint_revision"] == state["constraint_revision"]) else None
    # Existing sessions created before the paper snapshot stored no active-set pointer.
    adopted_at = max((decision["committed_at"] for decision in state["decisions"]
                      if decision.get("selected_model_version_id") == latest), default=0)
    return max((item for item in sets.values() if item["base_version_id"] == latest and item["created_at"] >= adopted_at
                and item["constraint_revision"] == state["constraint_revision"]),
               key=lambda item: (item["created_at"], item["request_id"]), default=None)


def snapshot(state):
    frame = state.get("paper_frame") or new_frame()
    head = state["committed_version_id"]
    revision = frame["paper_frame_revision"]
    version = next(v for v in state["versions"] if v["version_id"] == head)
    current = current_set(state) if state["status"] == "active" else None
    ready_id = current["candidate_set_id"] if current and current["status"] == "ready" else None
    show_layout = ready_id and frame["layout_set"] == ready_id
    hit = frame["shared_selection"]
    return {"session_id": state["session_id"], "spatial_frame_type": "paper", "host_participant_id": state["host_id"],
            "status": "ready" if frame["latest_pose_paper"] else "waiting_for_host", "paper_frame_revision": revision,
            "latest_version_id": head, "latest_model_asset": version["assets"]["preview.glb"],
            "latest_pose_paper": frame["latest_pose_paper"], "active_candidate_set_id": ready_id,
            "active_request_id": current["request_id"] if current else None,
            "current_round": current["candidate_set_id"] if current else None, "meeting_status": state["session_status"],
            "candidate_poses_paper": frame["candidate_poses_paper"] if show_layout else {},
            "selected_candidate_id": frame["selected_candidate_id"] if show_layout else None,
            "selected_by_participant_id": frame.get("selected_by_participant_id") if show_layout else None,
            "shared_selection": hit if hit and hit["model_version_id"] == head else None,
            "participants": [{"participant_id": p["participant_id"], "device_id": p["device_id"],
                              "paper_frame_revision": revision, "status": frame["alignments"].get(p["participant_id"], "not_aligned")}
                             for p in state["participants"]]}


def check_revision(frame, payload):
    revision = payload.get("paper_frame_revision")
    if type(revision) is not int or revision != frame["paper_frame_revision"]:
        raise MeetingError("종이 기준이 변경되었습니다. 현재 A4의 네 점을 다시 지정하세요.", 409)


def check_aligned(frame, actor):
    if frame["alignments"].get(actor) != "aligned":
        raise MeetingError("같은 A4의 네 점을 지정한 뒤 진행하세요.", 409)


def lose_alignment(state, actor):
    frame = state.get("paper_frame")
    if not frame or frame["alignments"].get(actor) not in ("aligned", "calibrating"):
        return False
    frame["alignments"][actor] = "lost"
    if (frame["shared_selection"] or {}).get("participant_id") == actor:
        frame["shared_selection"] = None
    return True


def check_commit(state, actor, payload):
    frame = state.get("paper_frame")
    if frame is None:
        return
    if frame["latest_pose_paper"] is None:
        if ("paper_frame_revision" in payload or state["host_id"] in frame["alignments"] or frame["paper_frame_revision"] > 1):
            raise MeetingError("종이 위에 Latest 모델을 먼저 배치하세요.", 409)
        # A guest calibrating early cannot switch a legacy host into paper mode.
        return
    if actor != state["host_id"]:
        raise MeetingError("진행자가 공유한 후보를 채택할 수 있습니다.", 403)
    check_revision(frame, payload)
    check_aligned(frame, actor)
    current = current_set(state)
    if (not current or current["status"] != "ready" or current["candidate_set_id"] != frame["layout_set"]
            or current["candidate_set_id"] != payload.get("candidate_set_id")
            or payload.get("candidate_id") not in frame["candidate_poses_paper"]):
        raise MeetingError("현재 종이 위에 표시한 후보를 선택하세요.", 409)


def paper_pose(parent, key):
    value = parent.get(key)
    if not isinstance(value, dict) or set(value) != {"position", "rotation_xyzw"}:
        raise MeetingError("종이 기준 위치(m)와 회전(x,y,z,w)이 필요합니다.")
    return pose(parent, key)


def same_pose(left, right):
    # q and -q represent exactly the same rotation.
    a, b = left["rotation_xyzw"], right["rotation_xyzw"]
    return (math.dist(left["position"], right["position"]) < 1e-4 and
            min(math.dist(a, b), math.dist(a, [-n for n in b])) < 1e-4)


def publish_layout(state, frame, payload):
    if payload.get("latest_version_id") != state["committed_version_id"]:
        raise MeetingError("현재 Latest 배치를 확인하세요.", 409)
    if "latest_pose_paper" in payload:
        latest = paper_pose(payload, "latest_pose_paper")
        if frame["latest_pose_paper"] is None:
            frame["latest_pose_paper"] = latest
        elif not same_pose(frame["latest_pose_paper"], latest):
            raise MeetingError("이미 확정한 배치입니다. 종이를 옮긴 경우 기준을 다시 설정하세요.", 409)
    if frame["latest_pose_paper"] is None:
        raise MeetingError("먼저 Latest의 종이 기준 위치를 등록하세요.", 409)
    if "active_candidate_set_id" not in payload and "candidate_poses_paper" not in payload:
        return
    current = current_set(state)
    if not current or current["status"] != "ready" or payload.get("active_candidate_set_id") != current["candidate_set_id"]:
        raise MeetingError("현재 Latest에서 준비된 후보 묶음을 확인하세요.", 409)
    poses = payload.get("candidate_poses_paper")
    if not isinstance(poses, dict) or set(poses) != {candidate["candidate_id"] for candidate in current["candidates"]}:
        raise MeetingError("표시할 각 후보의 ID와 종이 기준 배치가 필요합니다.", 409)
    poses = {cid: paper_pose(poses, cid) for cid in poses}
    if frame["layout_set"] == current["candidate_set_id"]:
        if any(not same_pose(frame["candidate_poses_paper"][cid], p) for cid, p in poses.items()):
            raise MeetingError("이미 확정한 후보 배치입니다.", 409)
        return
    frame.update(layout_set=current["candidate_set_id"], candidate_poses_paper=poses,
                 selected_candidate_id=None, selected_by_participant_id=None)


def ray(payload):
    hit = vector(payload, "hit_position_paper")
    origin = vector(payload, "ray_origin_paper")
    direction = vector(payload, "ray_direction_paper", normalized=True)
    distance = sum((h - o) * d for h, o, d in zip(hit, origin, direction))
    if distance < 0 or math.dist(hit, [o + distance*d for o, d in zip(origin, direction)]) > .002:
        raise MeetingError("종이 기준 선택점과 광선 방향이 일치하지 않습니다.")
    return dict(zip(RAY_FIELDS, (hit, origin, direction)))


def check_surface_bounds(latest_pose, version, hit):
    # Unity's loaded model mirrors GLB asset X. Compare in the asset basis.
    x, y, z, w = latest_pose["rotation_xyzw"]
    relative = [h - p for h, p in zip(hit, latest_pose["position"])]
    unity_x, unity_y, unity_z = transform_point({"position": [0, 0, 0], "rotation_xyzw": [-x, -y, -z, w]}, relative)
    bounds = version["bounds_m"]
    if any(not low - .002 <= p <= high + .002 for p, low, high in
           zip((-unity_x, unity_y, unity_z), bounds["min"], bounds["max"])):
        raise MeetingError("현재 Latest 모델 위의 지점을 지정하세요.", 409)


def apply(edge, state, actor, kind, payload):
    if not state.get("assembly"):
        raise MeetingError("공간 정렬은 조립체 회의에서 사용할 수 있습니다.", 409)
    if set(payload) - COMMAND_FIELDS[kind] - CLOCK_FIELDS - {"paper_frame_revision"}:
        raise MeetingError("종이 기준 명령에 등록된 필드만 보낼 수 있습니다.")
    frame = state.setdefault("paper_frame", new_frame())
    check_revision(frame, payload)
    revision = frame["paper_frame_revision"]
    if kind == "paper_reset":
        edge.host(state, actor)
        state["paper_frame"] = new_frame(revision + 1)
        return "paper.frame_changed", {"paper_frame_revision": revision + 1}, None
    if kind == "paper_alignment":
        status = payload.get("status")
        if not isinstance(status, str) or status not in {"not_aligned", "calibrating", "aligned", "lost"}:
            raise MeetingError("참가자의 종이 정렬 상태를 확인하세요.")
        frame["alignments"][actor] = status
        if status != "aligned" and (frame["shared_selection"] or {}).get("participant_id") == actor:
            frame["shared_selection"] = None
        return "paper.alignment_changed", {"paper_frame_revision": revision, "participant_id": actor, "status": status}, None
    if kind == "paper_layout":
        edge.host(state, actor)
        check_aligned(frame, actor)
        publish_layout(state, frame, payload)
        return "paper.layout_changed", {"paper_frame_revision": revision}, None
    check_aligned(frame, actor)
    if frame["latest_pose_paper"] is None:
        raise MeetingError("진행자의 Latest 모델 배치를 기다려 주세요.", 409)
    if kind == "paper_candidate_select":
        current = current_set(state)
        if (not current or current["status"] != "ready" or current["candidate_set_id"] != frame["layout_set"]
                or payload.get("candidate_set_id") != current["candidate_set_id"]
                or not isinstance(payload.get("candidate_id"), str) or payload["candidate_id"] not in frame["candidate_poses_paper"]):
            raise MeetingError("현재 종이 위에 표시한 후보를 선택하세요.", 409)
        frame["selected_candidate_id"] = payload["candidate_id"]
        frame["selected_by_participant_id"] = actor
        return "paper.candidate_selected", {"candidate_set_id": current["candidate_set_id"], "candidate_id": payload["candidate_id"],
                                             "participant_id": actor, "paper_frame_revision": revision}, None
    if payload.get("model_version_id") != state["committed_version_id"]:
        raise MeetingError("현재 표시된 Latest의 부위를 지정하세요.", 409)
    selection = {"participant_id": actor, "model_version_id": state["committed_version_id"], "paper_frame_revision": revision,
                 "updated_at": now_ms(), **ray(payload)}
    check_surface_bounds(frame["latest_pose_paper"], edge.version(state, state["committed_version_id"]), selection["hit_position_paper"])
    if "selection_id" in payload:
        selection["selection_id"] = text_field(payload, "selection_id", 100)
    frame["shared_selection"] = selection
    return "paper.selection_changed", selection, None
