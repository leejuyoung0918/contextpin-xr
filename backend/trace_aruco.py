"""Optional, setup-photo-only ArUco registration preview for the LED board.

The pose is a candidate until an operator checks two physical contacts. A low
pixel residual is not an accuracy measurement for the headset overlay.
"""
from functools import lru_cache
import math


DICTIONARY = "DICT_4X4_50"
LAYOUT_ID = "led-board-26mm-v1"
MARKER_SIDE_METRES = 0.026
MARKER_ORIGINS = {11: (-0.047, 0.012), 29: (0.050, 0.012)}
MAX_RMS_PIXELS = 2.5
MAX_CORNER_PIXELS = 5.0


@lru_cache(maxsize=1)
def _libraries():
    try:
        import cv2
        import numpy as np
    except (ImportError, OSError):
        return None
    if not hasattr(cv2, "aruco") or not hasattr(cv2.aruco, "ArucoDetector"):
        return None
    return cv2, np


def available():
    return _libraries() is not None


def board_corners(marker_id):
    """Printed clockwise TL→TR→BR→BL, in Unity board-local x,y,z metres."""
    left, near = MARKER_ORIGINS[marker_id]
    right, far = left + MARKER_SIDE_METRES, near + MARKER_SIDE_METRES
    return ((left, 0, far), (right, 0, far), (right, 0, near), (left, 0, near))


def intrinsics_from_capture(capture):
    """Transform raw, top-left sensor intrinsics through the centred crop."""
    focal, centre, crop = (capture[k] for k in ("sensorFocal", "sensorPrincipal", "sensorCrop"))
    width, height = capture["width"], capture["height"]
    fx, fy, cx, cy, x, y, w, h = map(float, (
        focal["x"], focal["y"], centre["x"], centre["y"],
        crop["x"], crop["y"], crop["width"], crop["height"],
    ))
    if (not all(math.isfinite(value) for value in (fx, fy, cx, cy, x, y, w, h))
            or min(fx, fy, w, h) <= 0 or min(x, y) < 0):
        raise ValueError("invalid_intrinsics")
    if not (type(width) is int and type(height) is int and 64 <= width <= 1600 and 64 <= height <= 1600):
        raise ValueError("invalid_image_size")
    orientation = capture.get("imageOrientation")
    if orientation not in {"top_left", "top_left_mirrored_x", "bottom_left"}:
        raise ValueError("unknown_image_orientation")
    sx, sy = width / w, height / h
    return (
        (fx * sx, 0, (cx - x) * sx),
        (0, fy * sy, (cy - y) * sy),
        (0, 0, 1),
    )


def image_to_canonical(image, orientation):
    # K is for the nominal top-left, unmirrored camera image. Undo variants
    # before detection rather than quietly changing the camera handedness.
    if orientation == "top_left_mirrored_x":
        return image[:, ::-1].copy()
    if orientation == "bottom_left":
        return image[::-1, :].copy()
    return image


def _point(point):
    return {name: float(value) for name, value in zip(("x", "y", "z"), point)}


def marker_pose(image_bytes, capture):
    refs = {k: capture.get(k) for k in ("captureId", "boardInstanceId", "registrationRevision", "trackingEpoch")}
    source = capture.get("source")
    def answer(status, reason, **details):
        return dict(refs, status=status, reason=reason, source=source, layoutId=LAYOUT_ID,
                    mustVerifyOnBoard=True, **details)

    if capture.get("phase") != "setup" or source not in {"real_camera", "replay", "synthetic"}:
        return answer("needs_manual_alignment", "expected_setup_snapshot")
    if capture.get("quality") in {"dark_or_warmup", "overexposed", "out_of_frame", "motion_detected"}:
        return answer("needs_manual_alignment", "setup_frame_quality")
    libraries = _libraries()
    if libraries is None:
        return answer("unavailable", "opencv_aruco_unavailable")
    cv2, np = libraries
    try:
        camera = np.array(intrinsics_from_capture(capture), dtype=np.float64)
        distortion = capture.get("distortion")
        if distortion not in {"rectified", "calibrated", "sdk_image_unspecified"}:
            return answer("needs_manual_alignment", "unknown_optical_calibration")
        coefficients = np.zeros(5, dtype=np.float64)
        if distortion == "calibrated":
            supplied = capture.get("distortionCoefficients")
            if not isinstance(supplied, list) or len(supplied) not in {4, 5, 8}:
                return answer("needs_manual_alignment", "missing_distortion_coefficients")
            coefficients = np.asarray(supplied, dtype=np.float64)
            if not np.isfinite(coefficients).all():
                return answer("needs_manual_alignment", "invalid_distortion_coefficients")
        if not isinstance(image_bytes, bytes) or not 100 <= len(image_bytes) <= 2_000_000:
            return answer("needs_manual_alignment", "invalid_image")
        image = cv2.imdecode(np.frombuffer(image_bytes, dtype=np.uint8), cv2.IMREAD_GRAYSCALE)
        if image is None or image.shape != (capture["height"], capture["width"]):
            return answer("needs_manual_alignment", "image_metadata_size_mismatch")
        image = image_to_canonical(image, capture["imageOrientation"])
        dictionary = cv2.aruco.getPredefinedDictionary(getattr(cv2.aruco, DICTIONARY))
        parameters = cv2.aruco.DetectorParameters()
        parameters.cornerRefinementMethod = cv2.aruco.CORNER_REFINE_SUBPIX
        corners, ids, _ = cv2.aruco.ArucoDetector(dictionary, parameters).detectMarkers(image)
        marker_corners = {}
        for identifier, detected in zip(ids.flatten() if ids is not None else [], corners):
            identifier = int(identifier)
            if identifier not in MARKER_ORIGINS:
                continue
            if identifier in marker_corners:
                return answer("needs_manual_alignment", "duplicate_marker_id")
            points = detected.reshape(-1, 2)
            if cv2.contourArea(points) < 64:
                return answer("needs_manual_alignment", "marker_too_small")
            marker_corners[identifier] = points
        if len(marker_corners) != len(MARKER_ORIGINS):
            return answer("needs_manual_alignment", "both_setup_markers_required")

        marker_ids = sorted(marker_corners)
        pixels = np.concatenate([marker_corners[i] for i in marker_ids]).astype(np.float64)
        board_local = np.array([point for i in marker_ids for point in board_corners(i)], dtype=np.float64)
        # OpenCV object coordinates (x right, y toward near end, z into
        # board) and camera coordinates (x right, y down, z optical forward).
        # Applying both named basis changes yields a proper Unity rotation.
        cv_object_from_board = np.array(((1, 0, 0), (0, 0, -1), (0, -1, 0)), dtype=np.float64)
        unity_camera_from_cv_camera = np.diag((1.0, -1.0, 1.0))
        objects = board_local @ cv_object_from_board.T
        count, vectors, translations, _ = cv2.solvePnPGeneric(
            objects, pixels, camera, coefficients, flags=cv2.SOLVEPNP_IPPE
        )
        valid = []
        for index in range(count):
            rotation = cv2.Rodrigues(vectors[index])[0]
            translation = translations[index].reshape(3)
            transformed = objects @ rotation.T + translation
            distance = float(np.linalg.norm(translation))
            normal_towards_camera = float(np.dot(-rotation[:, 2], -translation / max(distance, 1e-9)))
            if not .13 <= distance <= 1.5 or np.min(transformed[:, 2]) <= 0 or normal_towards_camera < .25:
                continue
            projected = cv2.projectPoints(objects, vectors[index], translations[index], camera, coefficients)[0].reshape(-1, 2)
            residual = np.linalg.norm(projected - pixels, axis=1)
            rms = float(np.sqrt(np.mean(residual ** 2)))
            if rms <= MAX_RMS_PIXELS and float(np.max(residual)) <= MAX_CORNER_PIXELS:
                valid.append((rms, rotation, translation))
        if not valid:
            return answer("needs_manual_alignment", "no_consistent_pose")
        valid.sort(key=lambda candidate: candidate[0])
        rms, rotation, translation = valid[0]
        if len(valid) > 1:
            other_rms, other_rotation, other_translation = valid[1]
            delta = np.clip((np.trace(other_rotation.T @ rotation) - 1) / 2, -1, 1)
            if other_rms - rms < .5 and (math.acos(delta) > .14 or np.linalg.norm(other_translation - translation) > .006):
                return answer("needs_manual_alignment", "ambiguous_planar_pose")
        origin = unity_camera_from_cv_camera @ translation
        axes = unity_camera_from_cv_camera @ rotation @ cv_object_from_board
        return answer(
            "candidate", "verify_two_physical_points", markerIds=marker_ids,
            cameraOrigin=_point(origin), cameraAxisX=_point(axes[:, 0]), cameraAxisY=_point(axes[:, 1]),
            cameraAxisZ=_point(axes[:, 2]), reprojectionErrorPixels=round(rms, 4),
            opticalModel="nominal_sdk_pinhole" if distortion == "sdk_image_unspecified" else distortion,
        )
    except (TypeError, ValueError, KeyError, OverflowError, cv2.error):
        return answer("needs_manual_alignment", "invalid_frame_calibration")
