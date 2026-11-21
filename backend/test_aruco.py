import unittest

import trace_aruco


class CameraCoordinateTests(unittest.TestCase):
    def test_scale_after_centred_crop(self):
        k = trace_aruco.intrinsics_from_capture(metadata())
        self.assertEqual(k, ((900.0, 0, 480.0), (0, 840.0, 300.0), (0, 0, 1)))
        left = trace_aruco.board_corners(11)
        self.assertAlmostEqual(left[1][0] - left[0][0], .026)
        self.assertAlmostEqual(left[0][2] - left[3][2], .026)
        self.assertNotEqual(trace_aruco.board_corners(29), left)

    def test_setup_only_and_provenance_retained(self):
        frame = metadata()
        frame["phase"] = "student_checkpoint"
        result = trace_aruco.marker_pose(b"x", frame)
        self.assertEqual(result["status"], "needs_manual_alignment")
        self.assertEqual(result["source"], "synthetic")
        self.assertEqual(result["trackingEpoch"], 7)
        self.assertNotIn("cameraOrigin", result)


def metadata():
    return {"captureId": "synthetic-pose", "phase": "setup", "source": "synthetic", "boardInstanceId": "fixture",
            "trackingEpoch": 7, "registrationRevision": 2, "imageOrientation": "top_left",
            "width": 960, "height": 600, "distortion": "rectified",
            "sensorFocal": {"x": 1500, "y": 1400}, "sensorPrincipal": {"x": 960, "y": 720},
            "sensorCrop": {"x": 160, "y": 220, "width": 1600, "height": 1000}}


@unittest.skipUnless(trace_aruco.available(), "OpenCV ArUco optional dependency not present")
class MarkerPoseFixtureTests(unittest.TestCase):
    def fixture(self, frame, marker_ids=(11, 29)):
        import cv2
        import numpy as np
        k = np.array(trace_aruco.intrinsics_from_capture(frame))
        pose = np.array((.32, -.13, .06)), np.array((.017, .021, .42))
        image = np.full((frame["height"], frame["width"]), 255, dtype=np.uint8)
        dictionary = cv2.aruco.getPredefinedDictionary(cv2.aruco.DICT_4X4_50)
        corners = np.array(((0, 0), (95, 0), (95, 95), (0, 95)), dtype=np.float32)
        for identifier in marker_ids:
            board = trace_aruco.board_corners(identifier)
            model = np.array([(x, -z, -y) for x, y, z in board])
            image_points = cv2.projectPoints(model, *pose, k, None)[0].reshape(4, 2).astype(np.float32)
            perspective = cv2.getPerspectiveTransform(corners, image_points)
            marker = cv2.aruco.generateImageMarker(dictionary, identifier, 96)
            projection = cv2.warpPerspective(marker, perspective, (frame["width"], frame["height"]),
                                             flags=cv2.INTER_NEAREST, borderValue=255)
            image = np.minimum(image, projection)
        orientation = frame["imageOrientation"]
        if orientation == "top_left_mirrored_x":
            image = image[:, ::-1].copy()
        if orientation == "bottom_left":
            image = image[::-1].copy()
        valid, jpeg = cv2.imencode(".jpg", image)
        self.assertTrue(valid)
        return jpeg.tobytes(), pose

    def test_two_markers_solve_in_unity_camera_basis(self):
        import cv2
        import numpy as np
        frame = metadata()
        image, (rvec, translation) = self.fixture(frame)
        result = trace_aruco.marker_pose(image, frame)
        self.assertEqual(result["status"], "candidate", result)
        self.assertTrue(result["mustVerifyOnBoard"])
        self.assertEqual(result["markerIds"], [11, 29])
        camera_basis = np.diag((1., -1., 1.))
        board_basis = np.array(((1, 0, 0), (0, 0, -1), (0, -1, 0)))
        expected = camera_basis @ cv2.Rodrigues(rvec)[0] @ board_basis
        measured = np.array([[result["cameraAxis" + axis][value] for axis in "XYZ"] for value in "xyz"])
        origin = np.array([result["cameraOrigin"][axis] for axis in "xyz"])
        np.testing.assert_allclose(origin, camera_basis @ translation, atol=.006)
        np.testing.assert_allclose(measured, expected, atol=.10)
        self.assertAlmostEqual(float(np.linalg.det(measured)), 1, places=5)

    def test_explicit_mirroring_or_flip_reverts_to_canonical_pixels(self):
        frame = metadata()
        baseline, _ = self.fixture(frame)
        expected = trace_aruco.marker_pose(baseline, frame)["cameraOrigin"]
        for orientation in ("top_left_mirrored_x", "bottom_left"):
            with self.subTest(orientation=orientation):
                frame["imageOrientation"] = orientation
                image, _ = self.fixture(frame)
                result = trace_aruco.marker_pose(image, frame)
                self.assertEqual(result["status"], "candidate", result)
                for axis in "xyz":
                    self.assertAlmostEqual(result["cameraOrigin"][axis], expected[axis], places=3)

    def test_a_single_tag_or_image_size_mismatch_does_not_supply_a_pose(self):
        frame = metadata()
        image, _ = self.fixture(frame, (11,))
        result = trace_aruco.marker_pose(image, frame)
        self.assertEqual(result["reason"], "both_setup_markers_required")
        self.assertNotIn("cameraOrigin", result)
        frame["width"] -= 1
        result = trace_aruco.marker_pose(image, frame)
        self.assertEqual(result["reason"], "image_metadata_size_mismatch")

    def test_paired_relay_solves_the_archived_setup_snapshot_with_its_revision(self):
        from base64 import b64encode
        from hashlib import sha256
        from tempfile import TemporaryDirectory
        from server import marker_pose_transfer
        from transfer import TransferError, TransferStore

        frame = metadata()
        image, _ = self.fixture(frame)
        frame["sha256"] = sha256(image).hexdigest()
        session_id, token = "aruco-fixture", "aruco-test-device-token-0123456789"
        with TemporaryDirectory() as folder:
            relay = TransferStore(folder)
            relay.create({"sessionId": session_id, "deviceToken": token, "pairingCode": "ABCDEFG234",
                          "sourceMode": "synthetic", "consent": True}, "")
            relay.save_asset({"sessionId": session_id, "sha256": frame["sha256"], "mime": "image/jpeg",
                              "base64": b64encode(image).decode()}, token)
            relay.publish({"sessionId": session_id, "revision": 1, "stage": "registration",
                           "registrationRevision": 2, "captures": [frame]}, token)
            request = {"sessionId": session_id, "captureId": frame["captureId"], "registrationRevision": 2}
            observed = marker_pose_transfer(relay, request, token)
            self.assertEqual(observed["status"], "candidate")
            self.assertEqual(observed["source"], "synthetic")
            self.assertEqual(observed["trackingEpoch"], frame["trackingEpoch"])
            relay.publish({"sessionId": session_id, "revision": 2, "stage": "registration",
                           "registrationRevision": 3, "captures": [frame]}, token)
            with self.assertRaises(TransferError) as ctx:
                marker_pose_transfer(relay, request, token)
            self.assertEqual(ctx.exception.status, 409)
