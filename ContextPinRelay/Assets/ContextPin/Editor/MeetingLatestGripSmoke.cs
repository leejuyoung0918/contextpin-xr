using System;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem.LowLevel;

namespace ContextPin.Editor
{
    public static partial class MeetingSmoke
    {
        static Quaternion FixtureTilt => ColocationTest ? Quaternion.Euler(-11f, 0f, 6f) : Quaternion.identity;
        static Vector3 expectedPhoneLatest;
        static bool sentGuestLiveMove;
        static readonly bool[] integratedGrips = new bool[2];

        static Vector2 ExpectedPhoneSlot(string key) => new Vector2(
            expectedPhoneLatest.x + (key == "A" ? -.38f : -.10f), expectedPhoneLatest.z + .24f);

        // The projected point is on the desk near one front corner of the stand, off its pivot.
        static Ray GripRay(Vector3 paperPoint, float distance)
        {
            var direction = measuredPaper.rotation * new Vector3(.07f, -1f, .04f).normalized;
            var point = measuredPaper.position + measuredPaper.rotation * (paperPoint + new Vector3(.030f, 0, .060f));
            return new Ray(point - distance * direction, direction);
        }

        static void WithGripInput(RelayApp app, Action<Func<Ray, bool, bool>> exercise)
        {
            WithTestHands((left, right) =>
            {
                var reader = AppType.GetMethod("ReadLatestGrip", AppMembers);
                var rig = (OVRCameraRig)AppType.GetField("xrRig", AppMembers).GetValue(app);
                using var oldRight = StateEvent.From(right, out var restoreRight);
                using var oldLeft = StateEvent.From(left, out var restoreLeft);
                try
                {
                    exercise((ray, held) =>
                    {
                        var away = new Ray(ray.origin + Camera.main.transform.right * .12f, -ray.direction);
                        SetControllerState(left, rig.trackingSpace, away, false, false);
                        SetControllerState(right, rig.trackingSpace, ray, false, false, held);
                        PumpPlayerInput();
                        if (held && PaperState(app, "CanMoveLatest"))
                        {
                            AppType.GetField("preferLeftController", AppMembers).SetValue(app, true);
                            object[] args = { false, false };
                            var beam = (Ray)AppType.GetMethod("ReadUiPointer", AppMembers).Invoke(app, args);
                            Require((bool)args[1] && Vector3.Dot(beam.direction, ray.direction) > .9999f,
                                "Right grip must display the same ray that moves the model.");
                        }
                        return (bool)reader.Invoke(app, new object[] { false });
                    });
                }
                finally
                {
                    InputState.Change(left, restoreLeft);
                    InputState.Change(right, restoreRight);
                }
                return true;
            });
        }

        static void RequireGripHit(GameObject latest, Ray ray)
        {
            Physics.SyncTransforms();
            Require(Physics.Raycast(ray, out var hit, 2f) && hit.transform.IsChildOf(latest.transform) &&
                latest.transform.InverseTransformPoint(hit.point).magnitude > .03f,
                "The test grip ray must start on the mesh away from the CAD pivot.");
        }

        static bool CheckHostGrip(RelayApp app, JObject state, GameObject latest, int index = 0)
        {
            var spatial = state["spatial"];
            if (IntegratedTest ? integratedGrips[index] : report.hostGripMoved)
                return spatial.Value<int>("latest_pose_revision") > 0 &&
                    Vector3.Distance(ReadGripPosition(spatial), expectedPhoneLatest) < .0005f &&
                    AppType.GetField("latestGrab", AppMembers).GetValue(app) == null;
            if (!PaperState(app, "CanMoveLatest")) return false;
            var initial = RelativePose(latest.transform, measuredPaper);
            var initialWorld = latest.transform.position;
            var target = initial.position + new Vector3(.040f, 0, .050f);
            var ray = GripRay(initial.position, .33f);
            RequireGripHit(latest, ray);
            void CheckAt(Vector3 expected)
            {
                var pose = RelativePose(latest.transform, measuredPaper);
                report.maxDragNormalDriftMm = Mathf.Max(report.maxDragNormalDriftMm, Mathf.Abs(pose.position.y - initial.position.y) * 1000);
                RequireSamePhysicalPose(latest.transform, new Pose(expected, initial.rotation), "Held Latest");
            }
            WithGripInput(app, send =>
            {
                send(ray, false);
                Require(send(ray, true), "A host right grip on Latest must start a grab.");
                CheckAt(initial.position);
                if (IntegratedTest) ProbeRejectedB(app);
                var highRay = GripRay(initial.position, 1.25f);
                Require(send(highRay, true), "Raising the controller must keep the grab.");
                CheckAt(initial.position);
                report.gripHeightLocked = true;
                var outside = new Vector3(2.5f, initial.position.y, -2.3f);
                send(GripRay(outside, .45f), true);
                var limit = explicitAreaSize * .5f + Vector2.one * spatial.Value<float>("paper_move_margin_m");
                CheckAt(new Vector3(limit.x, initial.position.y, -limit.y));
                report.gripClamped = true;
                var finalRay = GripRay(target, .28f);
                send(finalRay, true);
                CheckAt(target);
                report.draggedWorldHeightChangeMm = Mathf.Abs(latest.transform.position.y - initialWorld.y) * 1000;
                Require(report.draggedWorldHeightChangeMm > 5f, "The tilted fixture must distinguish desk-normal from world-height locking.");
                Require(send(finalRay, false), "The real right-grip release must finalize the drag.");
                CheckAt(target);
            });
            expectedPhoneLatest = target;
            integratedGrips[index] = true;
            if (IntegratedTest && index == 1)
            {
                Require(anchor == latest.transform.parent, "V2 drag must retain the original LatestAnchor instance.");
                commitPose = new Pose(anchor.position, anchor.rotation);
            }
            report.hostGripMoved = true;
            return false; // Wait for the final authoritative snapshot before making a CAD edit.
        }

        static Vector3 ReadGripPosition(JToken spatial)
        {
            var p = spatial["latest_pose_paper"]["position"];
            return new Vector3((float)p[0], (float)p[1], (float)p[2]);
        }

        static void CheckBlockedGrip(RelayApp app, GameObject latest, string why)
        {
            var pose = RelativePose(latest.transform, measuredPaper);
            var ray = GripRay(pose.position, .32f);
            RequireGripHit(latest, ray);
            WithGripInput(app, send =>
            {
                send(ray, false);
                Require(!send(ray, true) && !send(GripRay(pose.position + .09f * Vector3.right, .8f), true),
                    "A right grip must be ignored during " + why);
                send(ray, false);
            });
            RequireSamePhysicalPose(latest.transform, pose, why);
        }

        static bool CheckGuestLiveMove(RelayApp app, JObject spatial, GameObject latest)
        {
            if (report.guestReceivedLiveGrip) return true;
            if (!sentGuestLiveMove)
            {
                expectedPhoneLatest += new Vector3(-.015f, 0, .020f);
                app.StartCoroutine(RemoteHostCommand("paper_move_latest", new JObject {
                    ["paper_frame_revision"] = spatial["paper_frame_revision"], ["latest_version_id"] = report.middleVersion,
                    ["expected_pose_revision"] = spatial["latest_pose_revision"], ["grab_id"] = Guid.NewGuid().ToString("N"),
                    ["position_xz_m"] = new JArray(expectedPhoneLatest.x, expectedPhoneLatest.z),
                    ["paper_size_m"] = new JArray(sheetSize.x, sheetSize.y), ["final"] = true }));
                sentGuestLiveMove = true;
                return false;
            }
            if (Vector3.Distance(ReadGripPosition(spatial), expectedPhoneLatest) > .0005f) return false;
            hostLatestPaper.position = expectedPhoneLatest;
            RequireSamePhysicalPose(latest.transform, hostLatestPaper, "Live guest anchor after host update");
            report.guestReceivedLiveGrip = true;
            return true;
        }
    }
}
