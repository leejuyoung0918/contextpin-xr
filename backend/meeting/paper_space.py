"""Session-relative A4 layout. Only poses in the numbered paper frame are shared."""
import copy
import math

from .contracts import MeetingError, now_ms, text_field
from .edit_bundle import pose, transform_point, vector

COMMAND_FIELDS = {
    "paper_alignment": {"status"},
    "paper_layout": {"latest_version_id", "latest_pose_paper", "active_candidate_set_id", "candidate_poses_paper",
                     "expected_pose_revision"},
    "paper_move_latest": {"latest_version_id", "expected_pose_revision", "grab_id", "position_xz_m", "paper_size_m", "final"},
    "paper_reset": set(),
    "paper_selection": {"model_version_id", "selection_id", "ray_origin_paper", "ray_direction_paper", "hit_position_paper"},
    "paper_candidate_select": {"candidate_set_id", "candidate_id", "expected_pose_revision"},
}
CLOCK_FIELDS = {"source_monotonic_ms", "clock_offset_ms", "clock_uncertainty_ms"}
RAY_FIELDS = ("hit_position_paper", "ray_origin_paper", "ray_direction_paper")
POSE_FIELDS = ("latest_pose_paper", "paper_frame_revision", "latest_pose_revision")
GRAB_TIMEOUT_MS = 3000
# Existing single-device evidence remains in the immutable bundle on the PC.
DEVICE_POSES = {"ray_origin_world", "ray_direction_world", "hit_world_position", "hit_normal_world",
                "camera_pose", "workspace_pose", "latest_model_pose"}


def public_evidence(source):
    return {key: value for key, value in source.items() if key not in DEVICE_POSES}


def new_frame(revision=1):
    return {"paper_frame_revision": revision, "latest_pose_paper": None, "alignments": {},
            "layout_set": None, "candidate_poses_paper": {}, "selected_candidate_id": None,
            "selected_by_participant_id": None, "shared_selection": None}


def pose_fields(record):
    return {k: copy.deepcopy(record[k]) for k in POSE_FIELDS if k in record}


def matches_frame(state, record):
    if "latest_pose_paper" not in record:
        return True
    frame = state.get("paper_frame") or {}
    return bool(frame.get("latest_pose_paper") and frame["paper_frame_revision"] == record["paper_frame_revision"]
                and frame.get("latest_pose_revision", 0) == record["latest_pose_revision"]
                and same_pose(frame["latest_pose_paper"], record["latest_pose_paper"]))


def drag_active(frame):
    return bool(frame.get("active_grab_id") and now_ms() - frame["latest_move_at"] < GRAB_TIMEOUT_MS)


def current_set(state):
    # Use the most recently queued request on the current head. A pending or failed
    # replacement must not resurrect the preceding ready layout for late participants.
    latest = state["committed_version_id"]
    sets = state.get("candidate_sets", {})
    if "last_paper_candidate_request" in state:
        current = sets.get(state["last_paper_candidate_request"])
        return current if (current and matches_frame(state, current) and current["base_version_id"] == latest and
                           current["constraint_revision"] == state["constraint_revision"]) else None
    # Existing sessions created before the paper snapshot stored no active-set pointer.
    adopted_at = max((decision["committed_at"] for decision in state["decisions"]
                      if decision.get("selected_model_version_id") == latest), default=0)
    return max((item for item in sets.values() if matches_frame(state, item) and item["base_version_id"] == latest and item["created_at"] >= adopted_at
                and item["constraint_revision"] == state["constraint_revision"]),
               key=lambda item: (item["created_at"], item["request_id"]), default=None)


def edit_lock(state):
    if state["status"] != "active" or not (state.get("paper_frame") or {}).get("latest_pose_paper"):
        return None
    current = current_set(state)
    if current and current["status"] in ("generating", "building", "ready"):
        rid = current["request_id"]
        return {"request_id": rid, "participant_id": state.get("edit_requests", {}).get(rid, {}).get("participant_id"),
                "status": current["status"]}
    for capture in state.get("meeting_audio", {}).values():
        for rid, w in capture.get("design_ranges", {}).items():
            status = w.get("b_status")
            if ("latest_pose_paper" in w and rid not in state.get("edit_requests", {})
                    and w["base_version_id"] == state["committed_version_id"] and matches_frame(state, w)
                    and (capture["status"] == "open" or w.get("pending_selection_id"))
                    and status in ("recording", "transcribing", "final")):
                return {"request_id": rid, "participant_id": w["participant_id"],
                        "status": "awaiting_target" if status == "final" else status}
    return None


def interrupt_pending(state, reason, actor=None):
    interrupted = []
    for capture in state.get("meeting_audio", {}).values():
        for rid, window in capture.get("design_ranges", {}).items():
            if (window.get("latest_pose_paper") and rid not in state.get("edit_requests", {})
                    and (actor is None or window["participant_id"] == actor)
                    and window.get("b_status") in ("recording", "transcribing", "final")):
                window.update(b_status="failed", error=reason)
                interrupted.append(rid)
    return interrupted


def freeze_for_command(state, actor, observed):
    frame = state.get("paper_frame") or {}
    if "expected_pose_revision" in observed or "paper_frame_revision" in observed:
        if not frame.get("latest_pose_paper"):
            raise MeetingError("종이 위에 Latest를 배치한 뒤 수정하세요.", 409)
        check_revision(frame, observed)
        check_pose_revision(frame, observed)
    if not frame.get("latest_pose_paper"):
        return {}
    check_aligned(frame, actor)
    if drag_active(frame) or edit_lock(state):
        raise MeetingError("모델 이동이나 진행 중인 수정 요청을 먼저 마치세요.", 409)
    return {**pose_fields(frame), "latest_pose_revision": frame.get("latest_pose_revision", 0)}


def snapshot(state, move_margin_m):
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
            "latest_pose_revision": frame.get("latest_pose_revision", 0), "paper_move_margin_m": move_margin_m,
            "edit_lock": edit_lock(state), "drag_active": drag_active(frame),
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
    if actor == state["host_id"]:
        frame.pop("active_grab_id", None)
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
    if "expected_pose_revision" in payload:
        check_pose_revision(frame, payload)
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


def check_pose_revision(frame, payload):
    expected = payload.get("expected_pose_revision")
    if type(expected) is not int or expected != frame.get("latest_pose_revision", 0):
        raise MeetingError("Latest 위치가 변경되었습니다. 현재 모델의 위치를 다시 확인하세요.", 409)


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
    if "expected_pose_revision" in payload or frame.get("latest_pose_revision", 0):
        check_pose_revision(frame, payload)
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


def move_latest(state, frame, payload, margin):
    current_pose = frame["latest_pose_paper"]
    if not current_pose or payload.get("latest_version_id") != state["committed_version_id"]:
        raise MeetingError("현재 종이에 배치한 Latest 모델만 이동할 수 있습니다.", 409)
    check_pose_revision(frame, payload)
    if edit_lock(state):
        raise MeetingError("수정 요청 또는 후보 검토를 마친 뒤 Latest를 이동하세요.", 409)

    grab = text_field(payload, "grab_id", 100)
    if drag_active(frame) and frame["active_grab_id"] != grab:
        raise MeetingError("현재 진행 중인 Grip 이동을 먼저 마치세요.", 409)
    final = payload.get("final")
    if type(final) is not bool:
        raise MeetingError("Grip 해제 여부를 지정하세요.")
    x, z = vector(payload, "position_xz_m", 2)
    width, depth = vector(payload, "paper_size_m", 2)
    # Numbered A4 may be sampled in portrait or landscape and slightly skewed. Unity
    # reports the inscribed rectangle, which can be smaller than the physical sheet.
    short, long = sorted((width, depth))
    if not (.14 <= short <= .24 and .22 <= long <= .33):
        raise MeetingError("현재 측정한 A4의 너비와 깊이가 필요합니다.")
    if abs(x) > width / 2 + margin + 1e-6 or abs(z) > depth / 2 + margin + 1e-6:
        raise MeetingError("Latest의 위치가 A4 주변 이동 범위를 벗어납니다.")
    stamp = now_ms()
    if not final and stamp - frame.get("latest_move_at", 0) < 200:
        raise MeetingError("Latest 이동 중의 중간 위치는 초당 5회까지 전송할 수 있습니다.", 429)

    # The host supplies only plane coordinates. The original mounting height and
    # orientation remain byte-for-byte the values established by calibration.
    pose_paper = {**current_pose, "position": [x, current_pose["position"][1], z]}
    revision = frame.get("latest_pose_revision", 0) + 1
    frame.update(latest_pose_paper=pose_paper, latest_pose_revision=revision, latest_move_at=stamp,
                 active_grab_id=None if final else grab, shared_selection=None)
    return "paper.latest_pose_changed", {"paper_frame_revision": frame["paper_frame_revision"], "grab_id": grab,
            "latest_version_id": state["committed_version_id"], "latest_pose_revision": revision,
            "latest_pose_paper": pose_paper, "final": final}, None


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
        interrupt_pending(state, "종이 기준이 변경되었습니다. 정렬한 뒤 다시 요청하세요.")
        state["paper_frame"] = new_frame(revision + 1)
        return "paper.frame_changed", {"paper_frame_revision": revision + 1}, None
    if kind == "paper_alignment":
        status = payload.get("status")
        if not isinstance(status, str) or status not in {"not_aligned", "calibrating", "aligned", "lost"}:
            raise MeetingError("참가자의 종이 정렬 상태를 확인하세요.")
        frame["alignments"][actor] = status
        if status != "aligned":
            interrupt_pending(state, "종이 정렬이 바뀌었습니다. 정렬한 뒤 다시 요청하세요.", actor)
        if status != "aligned" and actor == state["host_id"]:
            frame.pop("active_grab_id", None)
        if status != "aligned" and (frame["shared_selection"] or {}).get("participant_id") == actor:
            frame["shared_selection"] = None
        return "paper.alignment_changed", {"paper_frame_revision": revision, "participant_id": actor, "status": status}, None
    if kind == "paper_layout":
        edge.host(state, actor)
        check_aligned(frame, actor)
        publish_layout(state, frame, payload)
        return "paper.layout_changed", {"paper_frame_revision": revision}, None
    if kind == "paper_move_latest":
        edge.host(state, actor)
        check_aligned(frame, actor)
        return move_latest(state, frame, payload, edge.paper_move_margin_m)
    check_aligned(frame, actor)
    if frame["latest_pose_paper"] is None:
        raise MeetingError("진행자의 Latest 모델 배치를 기다려 주세요.", 409)
    if kind == "paper_candidate_select":
        if "expected_pose_revision" in payload:
            check_pose_revision(frame, payload)
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
    lock = edit_lock(state)
    if lock and (lock["status"] != "awaiting_target" or lock["participant_id"] != actor):
        raise MeetingError("수정 요청자가 전사를 확인한 뒤 A로 부위를 지정하세요.", 409)
    selection = {"participant_id": actor, "model_version_id": state["committed_version_id"], "paper_frame_revision": revision,
                 "updated_at": now_ms(), **ray(payload)}
    check_surface_bounds(frame["latest_pose_paper"], edge.version(state, state["committed_version_id"]), selection["hit_position_paper"])
    if "selection_id" in payload:
        selection["selection_id"] = text_field(payload, "selection_id", 100)
    frame["shared_selection"] = selection
    return "paper.selection_changed", selection, None
