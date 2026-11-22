using System;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XR;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        // A grab belongs to one participant, adopted mesh and calibrated paper frame.
        sealed class LatestGrab
        {
            public string Id, Participant, Version;
            public int FrameRevision, ExpectedPoseRevision;
            public Pose InitialPose, Preview;
            public Vector3 HitWorld, HitPaper, OffsetPaper;
            public bool Pending, Released;
            public double HeartbeatAt;
        }

        LatestGrab latestGrab;
        bool latestGripHeld;
        bool latestMoveInFlight;
        double nextGripSendAt;
        int LatestPoseRevision => PaperState?.Value<int?>("latest_pose_revision") ?? -1;
        bool CanMoveLatest => PaperRenderable && OffsetPaperGallery && MeetingHost && !string.IsNullOrEmpty(meetingParticipant) &&
            LatestPoseRevision >= 0 && meetingTransport?.EventsConnected == true &&
            AssemblySessionStatus is ("ready" or "paused" or "recording") && !assemblyStopping &&
            !assemblyMenu && !movingMeetingPanel && !confirmingMeetingEnd && !CapturingPaperEdit &&
            string.IsNullOrEmpty(paperPoseSetId) && !bundleCommitPending && bundleCommitTarget == null &&
            (!IntegratedPaperControls || CurrentPaperInteraction is (PaperInteraction.LatestDisplayed or PaperInteraction.MovingLatest) &&
                (latestGrab != null || !latestMoveInFlight && !RoomDragging)) &&
            latestAnchor != null && meetingModel != null && meetingModel.activeInHierarchy &&
            meetingLoadedVersion == AssemblyAdoptedId;

        bool GrabApplies => latestGrab != null && CanMoveLatest && latestGrab.Participant == meetingParticipant &&
            latestGrab.FrameRevision == PaperFrameRevision && latestGrab.Version == meetingLoadedVersion &&
            (latestGrab.Pending || latestGrab.ExpectedPoseRevision == LatestPoseRevision);

        static bool TryGripPlanePoint(Pose frame, Ray ray, out Vector3 pointPaper)
        {
            pointPaper = default;
            var normal = frame.rotation * Vector3.up;
            var plane = new Plane(normal, frame.position);
            if (Mathf.Abs(Vector3.Dot(normal, ray.direction)) < .01f ||
                !plane.Raycast(ray, out float distance) || distance > 12f) return false;
            pointPaper = Quaternion.Inverse(frame.rotation) * (ray.GetPoint(distance) - frame.position);
            return float.IsFinite(pointPaper.sqrMagnitude);
        }

        static bool RightLatestGripHeld(XRController right) =>
            right?.TryGetChildControl<ButtonControl>("gripPressed")?.isPressed == true ||
            right?.TryGetChildControl<AxisControl>("grip")?.ReadValue() >= .55f ||
            OVRInput.Get(OVRInput.RawButton.RHandTrigger, OVRInput.Controller.RTouch);

        bool ReadLatestGrip(bool uiBlocked)
        {
            var right = XRController.rightHand;
            bool held = RightLatestGripHeld(right);
            Ray ray = default;
            bool tracked = (held || latestGrab != null) && TryReadTrackedAim(right, out ray);
            return HandleLatestGrip(ray, tracked, held, uiBlocked);
        }

        bool HandleLatestGrip(Ray ray, bool tracked, bool held, bool uiBlocked)
        {
            bool pressed = held && !latestGripHeld;
            latestGripHeld = held;
            if (latestGrab == null)
            {
                if (!pressed || !tracked || uiBlocked || !CanMoveLatest ||
                    !Physics.Raycast(ray, out var hit, 12f) || !hit.collider.transform.IsChildOf(meetingModel.transform) ||
                    !TryGripPlanePoint(reviewTable, ray, out var start)) return false;
                var initial = paperLatestPose.Value;
                latestGrab = new LatestGrab {
                    Id = Guid.NewGuid().ToString("N"), Participant = meetingParticipant, Version = meetingLoadedVersion,
                    FrameRevision = PaperFrameRevision, ExpectedPoseRevision = LatestPoseRevision,
                    HitWorld = hit.point, HitPaper = Quaternion.Inverse(reviewTable.rotation) * (hit.point - reviewTable.position),
                    InitialPose = initial, Preview = initial, OffsetPaper = initial.position - start
                };
                if (IntegratedPaperControls) Draw();
            }
            if (!GrabApplies || !tracked && !latestGrab.Released && !IntegratedPaperControls)
            {
                latestGrab = null;
                if (PaperRenderable && meetingModel != null && meetingModel.activeInHierarchy) PositionA4Latest();
                if (IntegratedPaperControls) Draw();
                return false;
            }
            var grab = latestGrab;
            if (!grab.Released && tracked && TryGripPlanePoint(reviewTable, ray, out var point))
            {
                var margin = PaperState.Value<float>("paper_move_margin_m");
                var limit = reviewTableSize * .5f + Vector2.one * margin;
                var position = point + grab.OffsetPaper;
                grab.Preview.position = new Vector3(Mathf.Clamp(position.x, -limit.x, limit.x),
                    grab.InitialPose.position.y, Mathf.Clamp(position.z, -limit.y, limit.y));
                var world = PaperToWorldPose(reviewTable, grab.Preview);
                latestAnchor.SetPositionAndRotation(world.position, world.rotation);
            }
            if ((!held || !tracked) && !grab.Released)
            {
                grab.Released = true;
                if (IntegratedPaperControls) Draw();
            }
            bool changed = (grab.Preview.position - paperLatestPose.Value.position).sqrMagnitude > .000001f;
            bool heartbeat = IntegratedPaperControls && Time.unscaledTimeAsDouble >= grab.HeartbeatAt;
            if (!grab.Pending && (grab.Released || (changed || heartbeat) && Time.unscaledTimeAsDouble >= nextGripSendAt))
                PublishLatestGrab(grab);
            return true;
        }

        void PublishLatestGrab(LatestGrab grab)
        {
            var transport = meetingTransport;
            bool final = grab.Released;
            var p = grab.Preview.position;
            var body = new JObject {
                ["paper_frame_revision"] = grab.FrameRevision, ["latest_version_id"] = grab.Version,
                ["expected_pose_revision"] = grab.ExpectedPoseRevision, ["grab_id"] = grab.Id, ["final"] = final,
                ["position_xz_m"] = new JArray(p.x, p.z), ["paper_size_m"] = new JArray(reviewTableSize.x, reviewTableSize.y)
            };
            grab.Pending = true;
            latestMoveInFlight = true;
            grab.HeartbeatAt = Time.unscaledTimeAsDouble + .85;
            nextGripSendAt = Time.unscaledTimeAsDouble + 1.0 / 3.0;
            StartCoroutine(transport.Json(transport.SessionPath + "/commands", FrozenDesignCommand("paper_move_latest", body), (reply, error) =>
            {
                if (transport != meetingTransport) return;
                latestMoveInFlight = false;
                var snapshot = reply?["snapshot"] as JObject;
                if (snapshot != null) AcceptMeetingSnapshot(snapshot);
                if (latestGrab != grab) return;
                grab.Pending = false;
                if (error != null || snapshot?["spatial"]?["latest_pose_revision"] == null)
                {
                    latestGrab = null;
                    assemblyFeedback = error ?? "모델 위치를 확인하지 못했습니다.";
                    RefreshMeetingSnapshot();
                    Draw();
                    return;
                }
                grab.ExpectedPoseRevision = (int)snapshot["spatial"]["latest_pose_revision"];
                if (final) { latestGrab = null; Draw(); }
            }));
        }
    }
}
