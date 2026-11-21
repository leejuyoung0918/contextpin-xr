using System;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        long captureWorkspaceRevision;

        static string UtcCaptureTime(long milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            .UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        static JObject CapturePose(Pose pose) => new JObject
        {
            ["position"] = Coordinates(pose.position),
            ["rotation_xyzw"] = new JArray(pose.rotation.x, pose.rotation.y, pose.rotation.z, pose.rotation.w)
        };

        bool TryCaptureDesignSelection(Ray aim, RaycastHit hit, RelayHit part, Transform modelRoot,
            PendingDesignRequest pending, out DesignSelection photo, out string error)
        {
            photo = null;
            error = null;
            if (view == null || modelRoot == null || meetingModel == null || modelRoot != meetingModel.transform ||
                !meetingModel.activeInHierarchy || !A4ReviewSurfaceActive || captureWorkspaceRevision <= 0 ||
                part.meetingVersion != pending.BaseId || meetingLoadedVersion != pending.BaseId)
            {
                error = "현재 표시된 채택 CAD의 부위를 다시 가리키세요.";
                return false;
            }
            long selectedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var localPoint = modelRoot.InverseTransformPoint(hit.point);
            var selected = new DesignSelection { Stamp = new JObject() };
            AddMeetingClock(selected.Stamp);
            // Freeze transforms before the GPU readback. No Transform, Collider or RaycastHit
            // is stored in the request; retries transmit the original coordinates and bytes.
            var evidence = new JObject
            {
                ["selection_id"] = selected.Id,
                ["base_version_id"] = pending.BaseId,
                ["object_id"] = TextValue(meetingSnapshot?["profile"], "object_id"),
                ["timestamp"] = UtcCaptureTime(selectedAt),
                ["ray_origin_world"] = Coordinates(aim.origin),
                ["ray_direction_world"] = Coordinates(aim.direction.normalized),
                ["hit_world_position"] = Coordinates(hit.point),
                ["hit_model_local_position"] = Coordinates(localPoint),
                ["hit_normal_world"] = Coordinates(hit.normal.normalized),
                ["workspace_revision"] = captureWorkspaceRevision,
                ["workspace_pose"] = CapturePose(new Pose(WorkspaceOrigin, WorkspaceRotation)),
                ["latest_model_pose"] = CapturePose(new Pose(modelRoot.position, modelRoot.rotation))
            };
            try
            {
                if (meetingCapture == null) meetingCapture = gameObject.AddComponent<RelayEvidence>();
                var image = meetingCapture.CaptureSelectionImage(view, hit.point, selectedAt, !ExplicitSimulator);
                if (image.Jpeg == null || image.Jpeg.Length == 0)
                    throw new InvalidOperationException("선택 순간의 이미지가 비어 있습니다.");
                selected.Image = image.Jpeg;
                selected.Source = image.ActualRgb ? "live_capture" : "xr_render";
                selected.Stamp["client_request_id"] = pending.Id;
                selected.Stamp["selection_id"] = selected.Id;
                selected.Stamp["captured_at_ms"] = image.CapturedAtMs;
                selected.Reference = new JObject
                {
                    ["selection_id"] = selected.Id, ["reference_version_id"] = part.meetingVersion,
                    ["part_id"] = part.role, ["hit_point_asset_m"] = Coordinates(new Vector3(-localPoint.x, localPoint.y, localPoint.z)),
                    ["image_uv"] = new JArray(image.Uv.x, image.Uv.y), ["image_uv_origin"] = "top_left",
                    ["captured_at_ms"] = image.CapturedAtMs, ["selected_at_ms"] = selectedAt,
                    ["source_mode"] = selected.Source, ["tracking_state"] = "tracking"
                };
                evidence["camera_pose"] = CapturePose(image.CameraPose);
                evidence["capture_timestamp"] = UtcCaptureTime(image.CapturedAtMs);
                evidence["image_source"] = image.ActualRgb ? "quest_rgb" : "rendered_debug";
                evidence["actual_rgb"] = image.ActualRgb ? "available" : "unavailable";
                if (image.SensorTimestampUs.HasValue) evidence["sensor_timestamp_us"] = image.SensorTimestampUs.Value;
                if (image.FallbackReason != null) evidence["fallback_reason"] = image.FallbackReason;
                selected.BundleEvidence = evidence;
                photo = selected;
                return true;
            }
            catch (Exception failure)
            {
                error = "선택 순간의 이미지를 기록하지 못했습니다. CAD를 바라보고 다시 A를 누르세요. " + failure.Message;
                return false;
            }
        }
    }
}
