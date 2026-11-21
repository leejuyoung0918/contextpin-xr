using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        // Unity metres. Origin is the paper centre, +X is ①→②, +Z is ①→④, +Y is up.
        // Only this device knows worldFromPaper. The wire holds paperFromAsset.
#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            if (!a4PoseApplied) return;
            var frame = A4ReviewSurfaceActive ? reviewTable : a4Surface;
            var x = frame.rotation * Vector3.right * .06f;
            var z = frame.rotation * Vector3.forward * .06f;
            Gizmos.color = Color.red;
            Gizmos.DrawRay(frame.position, x);
            UnityEditor.Handles.Label(frame.position + x, "+X · ①→②");
            Gizmos.color = Color.blue;
            Gizmos.DrawRay(frame.position, z);
            UnityEditor.Handles.Label(frame.position + z, "+Z · ①→④");
        }
#endif

        static Pose WorldToPaperPose(Pose frame, Pose world)
        {
            var inverse = Quaternion.Inverse(frame.rotation);
            return new Pose(inverse * (world.position - frame.position), inverse * world.rotation);
        }

        static Pose PaperToWorldPose(Pose frame, Pose local) =>
            new Pose(frame.position + frame.rotation * local.position, frame.rotation * local.rotation);

        static JObject PaperSelectionPayload(Pose frame, int revision, string version, Ray ray, Vector3 hit)
        {
            var inverse = Quaternion.Inverse(frame.rotation);
            return new JObject {
                ["paper_frame_revision"] = revision, ["model_version_id"] = version,
                ["ray_origin_paper"] = Coordinates(inverse * (ray.origin - frame.position)),
                ["ray_direction_paper"] = Coordinates(inverse * ray.direction.normalized),
                ["hit_position_paper"] = Coordinates(inverse * (hit - frame.position))
            };
        }

        static bool TryPaperPose(JToken data, out Pose pose)
        {
            pose = default;
            if (data is not JObject || ReadMeetingVector(data["position"]) is not Vector3 position ||
                data["rotation_xyzw"] is not JArray q || q.Count != 4 ||
                q.Any(value => value.Type != JTokenType.Float && value.Type != JTokenType.Integer)) return false;
            var rotation = new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]);
            float squaredLength = Quaternion.Dot(rotation, rotation);
            if (!float.IsFinite(squaredLength) || Mathf.Abs(squaredLength - 1) > .01f) return false;
            pose = new Pose(position, rotation.normalized);
            return true;
        }

        JObject PaperState => meetingSnapshot?["spatial"] as JObject;
        bool SharedPaperMeeting => SpatialAssembly && TextValue(meetingSnapshot, "provider_mode") == "live" &&
            PaperState != null && (CenteredPaperLatest || CaptureBundleMeeting && (A4PointSampling || A4PoseApplied) ||
            TextValue(PaperState, "status") == "ready");
        int PaperFrameRevision => PaperState?.Value<int?>("paper_frame_revision") ?? 0;
        bool PaperSessionReady => paperLatestPose.HasValue;
        bool PaperRenderable => SharedPaperMeeting && A4ReviewSurfaceActive && PaperSessionReady &&
            paperRevisionSeen == PaperFrameRevision && paperSelfStatus == "aligned";
        string PaperLocalStatus => A4ReviewSurfaceActive ? "aligned" : A4PointSampling || A4PreviewActive ? "calibrating" :
            assemblyAlignment == AssemblyAlignment.Lost ? "lost" : "not_aligned";

        int paperRevisionSeen;
        bool paperSyncPending, paperGuestOpened;
        double paperRetryAt;
        string paperSelfStatus, paperPoseSetId;
        Pose? paperLatestPose;
        readonly Dictionary<string, Pose> paperCandidatePoses = new Dictionary<string, Pose>();
        GameObject paperHitMarker;

        void ReceivePaperState()
        {
            var spatial = PaperState;
            if (spatial == null || !SpatialAssembly) return;
            int revision = PaperFrameRevision;
            bool replaced = paperRevisionSeen > 0 && paperRevisionSeen != revision;
            paperRevisionSeen = revision;
            paperLatestPose = TryPaperPose(spatial["latest_pose_paper"], out var pose) ? pose : (Pose?)null;
            paperPoseSetId = TextValue(spatial, "active_candidate_set_id");
            paperCandidatePoses.Clear();
            if (spatial["candidate_poses_paper"] is JObject poses)
                foreach (var pair in poses.Properties())
                    if (TryPaperPose(pair.Value, out var candidate)) paperCandidatePoses[pair.Name] = candidate;
            paperSelfStatus = TextValue(Values(spatial, "participants").FirstOrDefault(p => TextValue(p, "participant_id") == meetingParticipant), "status");
            if (replaced)
            {
                InvalidateAssemblyAlignment();
                ClearTabletopAlignment();
                assemblyAlignment = AssemblyAlignment.Lost;
                paperRetryAt = 0;
                if (paperHitMarker != null) paperHitMarker.SetActive(false);
                notice = "호스트가 종이 기준을 바꿨습니다. 같은 A4의 ①→②→③→④를 다시 지정하세요.";
            }
        }

        void ResetPaperState()
        {
            paperRevisionSeen = 0;
            paperSyncPending = paperGuestOpened = false;
            paperRetryAt = 0;
            paperSelfStatus = paperPoseSetId = null;
            paperLatestPose = null;
            paperCandidatePoses.Clear();
            if (paperHitMarker != null) Destroy(paperHitMarker);
            paperHitMarker = null;
        }

        void SendPaperCommand(string type, JObject payload, bool syncing = false)
        {
            var transport = meetingTransport;
            if (transport == null) return;
            if (syncing) paperSyncPending = true;
            payload["paper_frame_revision"] = PaperFrameRevision;
            var command = FrozenDesignCommand(type, payload);
            StartCoroutine(transport.Json(transport.SessionPath + "/commands", command, (reply, error) =>
            {
                if (transport != meetingTransport) return;
                if (syncing) paperSyncPending = false;
                if (reply?["snapshot"] is JObject snapshot) AcceptMeetingSnapshot(snapshot);
                if (error != null)
                {
                    paperRetryAt = Time.unscaledTimeAsDouble + 2;
                    assemblyFeedback = error;
                    RefreshMeetingSnapshot();
                }
                Draw();
            }));
        }

        void RedefinePaperFrame()
        {
            if (SharedPaperMeeting && MeetingHost && !paperSyncPending)
                SendPaperCommand("paper_reset", new JObject(), true);
        }

        void UpdatePaperSync()
        {
            if (!SharedPaperMeeting || meetingTransport == null ||
                AssemblySessionStatus is "completed" or "finalizing" or "save_error") return;
            if (!MeetingHost && !paperGuestOpened && assemblyAlignment == AssemblyAlignment.Unaligned)
            {
                paperGuestOpened = true;
                BeginA4Alignment();
            }
            if (paperSyncPending || Time.unscaledTimeAsDouble < paperRetryAt) return;
            if (paperSelfStatus != PaperLocalStatus)
            {
                SendPaperCommand("paper_alignment", new JObject { ["status"] = PaperLocalStatus }, true);
                return;
            }
            if (!MeetingHost || !A4ReviewSurfaceActive || latestAnchor == null || meetingModel == null ||
                !meetingModel.activeInHierarchy || meetingLoadedVersion != AssemblyAdoptedId) return;
            if (!paperLatestPose.HasValue)
            {
                var pose = new Pose(latestAnchor.position, latestAnchor.rotation);
                SendPaperCommand("paper_layout", new JObject {
                    ["latest_version_id"] = AssemblyAdoptedId, ["latest_pose_paper"] = CapturePose(WorldToPaperPose(reviewTable, pose))
                }, true);
            }
            else if (bundleContainer != null && bundleContainer.activeInHierarchy && bundleSetId == paperPoseSetId && bundleCandidates.Length > 0 && paperCandidatePoses.Count == 0)
            {
                var poses = new JObject();
                for (int i = 0; i < bundleCandidates.Length; i++)
                {
                    var pivot = bundleSlots[i].Root.transform;
                    poses[TextValue(bundleCandidates[i], "candidate_id")] = CapturePose(WorldToPaperPose(reviewTable, new Pose(pivot.position, pivot.rotation)));
                }
                SendPaperCommand("paper_layout", new JObject { ["latest_version_id"] = AssemblyAdoptedId,
                    ["active_candidate_set_id"] = bundleSetId, ["candidate_poses_paper"] = poses }, true);
            }
        }

        bool SharePaperRay(Ray ray)
        {
            if (!PaperRenderable || assemblyMenu || confirmingMeetingEnd || movingMeetingPanel || meetingModel == null ||
                meetingLoadedVersion != AssemblyAdoptedId || !meetingModel.activeInHierarchy ||
                !Physics.Raycast(ray, out var hit, 12) || !hit.transform.IsChildOf(meetingModel.transform)) return false;
            SendPaperCommand("paper_selection", PaperSelectionPayload(reviewTable, PaperFrameRevision, meetingLoadedVersion, ray, hit.point));
            return true;
        }

        void DisplayPaperHit()
        {
            var selection = PaperState?["shared_selection"] as JObject;
            if (!PaperRenderable || selection?.Value<int?>("paper_frame_revision") != PaperFrameRevision ||
                TextValue(selection, "model_version_id") != meetingLoadedVersion || meetingLoadedVersion != AssemblyAdoptedId ||
                meetingModel == null || !meetingModel.activeInHierarchy || a4DisplayRoot == null ||
                ReadMeetingVector(selection?["hit_position_paper"]) is not Vector3 hit)
            {
                if (paperHitMarker != null && paperHitMarker.activeSelf) paperHitMarker.SetActive(false);
                return;
            }
            if (paperHitMarker == null)
            {
                paperHitMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                paperHitMarker.name = "SharedPaperHit";
                var collider = paperHitMarker.GetComponent<Collider>();
                collider.enabled = false;
                Destroy(collider);
                paperHitMarker.GetComponent<Renderer>().sharedMaterial = probe.GetComponent<Renderer>().sharedMaterial;
                paperHitMarker.transform.SetParent(a4DisplayRoot, false);
                paperHitMarker.transform.localScale = Vector3.one * .006f;
            }
            paperHitMarker.transform.localPosition = hit;
            paperHitMarker.SetActive(true);
        }
    }
}
