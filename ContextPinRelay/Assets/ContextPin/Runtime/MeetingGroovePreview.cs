using System;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        const string GroovePreviewResources = "CableGroovePreview/";
        static readonly string[] GroovePreviewIds = { "A", "B", "C", "D" };
        GameObject groovePreviewGallery;
        MeetingModelSlot[] groovePreviews = Array.Empty<MeetingModelSlot>();
        JArray groovePreviewManifest;
        bool groovePreviewEnabled, groovePreviewManifestFailed;

        bool GroovePreviewAvailable => SpatialAssembly && IsCableDemo && assemblyAlignment == AssemblyAlignment.Confirmed &&
            AssemblySessionStatus == "ready" && !assemblyStarting &&
            TextValue(meetingSnapshot?["profile"], "adapter_id") == "openscad_cable_organizer";

        void ToggleExampleGroovePreviews()
        {
            if (!GroovePreviewAvailable) return;
            groovePreviewEnabled = !groovePreviewEnabled;
            if (groovePreviewEnabled)
            {
                QueueGroovePreviews();
            }
            else PositionGroovePreviews();
            Draw();
        }

        void QueueGroovePreviews()
        {
            if (groovePreviewGallery == null)
            {
                var manifest = Resources.Load<TextAsset>(GroovePreviewResources + "variants");
                try { groovePreviewManifest = manifest != null ? JObject.Parse(manifest.text)["variants"] as JArray : null; }
                catch (JsonException error) { Debug.LogWarning("Groove preview · " + error.Message); }
                finally { if (manifest != null) Resources.UnloadAsset(manifest); }
                groovePreviewManifestFailed = groovePreviewManifest == null ||
                    !groovePreviewManifest.Select(value => TextValue(value, "id")).SequenceEqual(GroovePreviewIds);
                if (groovePreviewManifestFailed) return;
                groovePreviewGallery = new GameObject("GroovePreviewGallery");
                groovePreviews = GroovePreviewIds.Select(id => new MeetingModelSlot(groovePreviewId: id)).ToArray();
            }

            for (int i = 0; i < groovePreviews.Length; i++)
            {
                var slot = groovePreviews[i];
                if (slot.Root != null || slot.Request != null) continue;
                var id = slot.GroovePreviewId;
                var entry = groovePreviewManifest[i];
                var hash = TextValue(entry, "sha256");
                var size = ReadMeetingVector(entry["bounds_m"]?["size"]);
                var asset = Resources.Load<TextAsset>(GroovePreviewResources + id);
                var bytes = asset != null ? asset.bytes : null;
                if (asset != null) Resources.UnloadAsset(asset);
                if (bytes == null || size == null || hash.Length != 64)
                {
                    slot.FailedVersion = id;
                    continue;
                }
                slot.FailedVersion = null;
                slot.LoadingVersion = id;
                slot.Request = new CancellationTokenSource();
                _ = ImportMeetingModel(slot, bytes, hash, size, null, id, slot.Request);
            }
            PositionGroovePreviews();
        }

        void AttachGroovePreview(MeetingModelSlot slot)
        {
            slot.Root.transform.SetParent(groovePreviewGallery.transform, false);
            var variant = groovePreviewManifest[Array.IndexOf(groovePreviews, slot)];
            var title = slot.GroovePreviewId + " · " + TextValue(variant, "title");
            var dimensions = TextValue(variant, "dimensions");
            slot.Label = new GameObject("GroovePreviewLabel", typeof(RectTransform), typeof(Canvas));
            var rect = (RectTransform)slot.Label.transform;
            rect.SetParent(slot.Root.transform, false);
            rect.sizeDelta = new Vector2(156, 46);
            rect.localScale = Vector3.one * .00085f;
            slot.Label.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            RoundBox("Background", rect, 0, 0, 156, 46, SpatialBackground, 8);
            BoldLabel("Title", rect, 3, 3, 150, 21, title, 18, HeaderText, TextAnchor.MiddleCenter);
            Label("Dimensions", rect, 3, 24, 150, 18, dimensions, 14, SpatialMuted, TextAnchor.MiddleCenter);
        }

        void PositionGroovePreviews()
        {
            if (groovePreviewGallery == null) return;
            bool visible = groovePreviewEnabled && GroovePreviewAvailable && HasReviewTable && AssemblyHasPlacementMount &&
                assemblyAlignment == AssemblyAlignment.Confirmed && !assemblyCompare && meetingModel != null && meetingModel.activeSelf;
            if (groovePreviewGallery.activeSelf != visible) groovePreviewGallery.SetActive(visible);
            if (!visible) return;

            for (int i = 0; i < groovePreviews.Length; i++)
            {
                var slot = groovePreviews[i];
                if (slot.Root == null) continue;
                if (!PlaceReviewModel(slot, i, groovePreviews.Length)) continue;
                var labelPosition = slot.Root.transform.TransformPoint(slot.Bounds.center) + Vector3.up * .047f;
                var direction = labelPosition - view.transform.position;
                if (direction.sqrMagnitude > .001f && slot.Label != null)
                    slot.Label.transform.SetPositionAndRotation(labelPosition, Quaternion.LookRotation(direction, Vector3.up));
            }
        }

        void DrawGroovePreviewControl()
        {
            string status = "대본 시연용 예시";
            if (groovePreviewEnabled)
            {
                if (groovePreviewManifestFailed || groovePreviews.Any(slot => slot.FailedVersion != null))
                    status = "일부 CAD를 불러오지 못했습니다";
                else if (groovePreviews.Any(slot => slot.LoadingVersion != null)) status = "예시 CAD 불러오는 중…";
            }
            Label("SpatialGroovePreviewStatus", content, 12, 172, 172, 42, status, SpatialFont(17), HeaderText);
            Button("SpatialGroovePreviewToggle", 186, 175, 126, 37, groovePreviewEnabled ? "숨기기" : "예시 CAD 보기", ToggleExampleGroovePreviews);
        }

        void ReleaseGroovePreviews()
        {
            groovePreviewEnabled = groovePreviewManifestFailed = false;
            foreach (var slot in groovePreviews) QueueComparisonSlot(slot, null);
            if (groovePreviewGallery != null) Destroy(groovePreviewGallery);
            groovePreviewGallery = null;
            groovePreviewManifest = null;
            groovePreviews = Array.Empty<MeetingModelSlot>();
        }
    }
}
