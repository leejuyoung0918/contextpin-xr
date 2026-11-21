using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        static Color BundleColor(string key) => key == "A" ? new Color32(251, 160, 63, 224) : new Color32(183, 141, 250, 224);
        const float BundleGap = .010f;
        const float BundleLift = .002f;
        const float PaperEdge = .002f;
        const float SideReviewMargin = .15f; // Desk space on each side confirmed with the paper preview.
        string PaperTooSmall => CenteredPaperLatest ? "종이와 좌우 15cm의 비교 영역에 후보를 실제 크기로 놓을 수 없습니다." :
            "실물과 Latest의 자리를 비우면 이 A4 뒤쪽에 후보를 실제 크기로 놓을 공간이 부족합니다.";

        readonly MeetingModelSlot[] bundleSlots = { new MeetingModelSlot(), new MeetingModelSlot() };
        GameObject bundleContainer;
        string bundleSetId, bundleRequestId, bundleBaseId, bundleCommitTarget, bundleReviewError;
        JObject[] bundleCandidates = Array.Empty<JObject>();
        bool bundleCommitPending;
        MeetingModelSlot bundleHover;

        bool BundleAssetsFailed => Array.Exists(bundleSlots, slot => slot.FailedVersion != null);
        bool BundleOnPaper => A4ReviewSurfaceActive && bundleContainer != null && bundleContainer.activeInHierarchy &&
            (!SharedPaperMeeting || PaperRenderable && bundleSetId == paperPoseSetId && paperCandidatePoses.Count == bundleCandidates.Length);

        bool VerifiedCommitDescriptor(JToken version)
        {
            string id = TextValue(version, "version_id");
            if (id.Length == 0 || !CandidateGenerationMeeting || meetingSnapshot?["candidate_sets"] is not JObject sets) return true;
            var committed = sets.Properties().Select(p => p.Value).FirstOrDefault(s =>
                TextValue(s, "status") == "committed" && TextValue(s, "committed_version_id") == id);
            if (committed == null) return true;
            var selected = Values(committed, "candidates").FirstOrDefault(c =>
                TextValue(c, "candidate_id") == TextValue(committed, "selected_candidate_id"));
            string expected = TextValue(selected?["model_asset"], "sha256");
            string published = TextValue(version?["assets"]?["preview.glb"], "sha256");
            var shown = Array.Find(bundleSlots, slot => slot.LoadedVersion == id);
            if (TextValue(selected, "version_id") == id && expected.Length == 64 &&
                (shown == null || string.Equals(shown.VerifiedHash, expected, StringComparison.OrdinalIgnoreCase)) &&
                string.Equals(published, expected, StringComparison.OrdinalIgnoreCase)) return true;
            CancelMeetingModel(meetingPreview);
            meetingPreview.FailedVersion = id;
            bundleReviewError = "채택된 파일이 화면에서 선택한 후보와 일치하지 않습니다.";
            return false;
        }

        void QueueBundleReview()
        {
            if (!CandidateGenerationMeeting || !AssemblyActive || !A4ReviewSurfaceActive || a4DisplayRoot == null)
            {
                if (!AssemblyActive && bundleSetId != null) ReleaseBundleReview();
                return;
            }
            var set = CurrentCandidateSet;
            // A snapshot can arrive before the HTTP reply or during reconnection. The
            // committed server version and its verified preview finish the same transition.
            var old = bundleRequestId == null ? null : meetingSnapshot["candidate_sets"]?[bundleRequestId];
            if (TextValue(old, "status") == "committed" && TextValue(old, "committed_version_id") == AssemblyAdoptedId)
            {
                bundleCommitTarget = AssemblyAdoptedId;
                if (meetingLoadedVersion != bundleCommitTarget) return;
                ReleaseBundleReview();
            }
            if (TextValue(set, "status") != "ready" || TextValue(set, "base_version_id") != AssemblyAdoptedId)
            {
                if (bundleSetId != null) ReleaseBundleReview();
                return;
            }
            string setId = TextValue(set, "candidate_set_id");
            if (bundleSetId == setId) return;
            ReleaseBundleReview();
            bundleSetId = setId;
            bundleRequestId = TextValue(set, "request_id");
            bundleBaseId = TextValue(set, "base_version_id");
            bundleCandidates = Values(set, "candidates").OfType<JObject>().OrderBy(c => TextValue(c, "candidate_key")).ToArray();
            if (bundleCandidates.Length is < 1 or > 2 ||
                bundleCandidates.Any(c => TextValue(c, "candidate_key") is not ("A" or "B") ||
                    TextValue(c, "candidate_id").Length == 0 || TextValue(c, "version_id").Length == 0) ||
                bundleCandidates.Select(c => TextValue(c, "candidate_key")).Distinct().Count() != bundleCandidates.Length)
            {
                bundleReviewError = "후보 ID와 모델 정보를 확인할 수 없습니다.";
                return;
            }
            bundleContainer = new GameObject("CandidateContainer");
            bundleContainer.SetActive(false);
            bundleContainer.transform.SetParent(a4DisplayRoot, false);
            for (int i = 0; i < bundleCandidates.Length; i++) QueueBundleSlot(i);
        }

        void QueueBundleSlot(int i)
        {
            var candidate = bundleCandidates[i];
            var slot = bundleSlots[i];
            slot.BundleKey = TextValue(candidate, "candidate_key");
            // Adapt the existing version loader to the exact descriptor in this set.
            // It checks SHA-256, GLB header, embedded resources, and CAD metre bounds.
            var asset = candidate["model_asset"];
            QueueMeetingModelSlot(slot, new JObject {
                ["version_id"] = candidate["version_id"], ["preview_url"] = asset?["url"],
                ["assets"] = new JObject { ["preview.glb"] = asset }
            });
        }

        void ReleaseBundleReview()
        {
            if (bundleContainer != null) bundleContainer.SetActive(false);
            foreach (var slot in bundleSlots)
            {
                QueueComparisonSlot(slot, null);
                slot.BundleKey = null;
            }
            if (bundleContainer != null) Destroy(bundleContainer);
            bundleContainer = null;
            bundleCandidates = Array.Empty<JObject>();
            bundleSetId = bundleRequestId = bundleBaseId = bundleCommitTarget = bundleReviewError = null;
            bundleCommitPending = false;
            bundleHover = null;
        }

        void PositionBundleCandidates()
        {
            if (bundleContainer == null) return;
            if (bundleCommitTarget != null && meetingLoadedVersion == bundleCommitTarget)
            {
                ReleaseBundleReview();
                return;
            }
            if (!A4ReviewSurfaceActive || !AssemblyActive || meetingModel == null || !meetingModel.activeInHierarchy ||
                (!SharedPaperMeeting || MeetingHost) && !sessionOriginalBounds.HasValue || meetingLoadedVersion != bundleBaseId ||
                !SharedPaperMeeting && explicitRequest != null && explicitRequest.Id != bundleRequestId ||
                SharedPaperMeeting && bundleSetId != paperPoseSetId ||
                bundleSlots[0].Root == null || bundleCandidates.Length == 2 && bundleSlots[1].Root == null)
            {
                if (bundleContainer.activeSelf) bundleContainer.SetActive(false);
                return;
            }
            Vector3 a = default, b = default;
            bool published = SharedPaperMeeting && paperCandidatePoses.Count == bundleCandidates.Length;
            if (!published)
            {
                if (SharedPaperMeeting && !MeetingHost)
                {
                    if (bundleContainer.activeSelf) bundleContainer.SetActive(false);
                    return;
                }
                var latest = FootprintOnTable(meetingAssetBounds, latestAnchor.position, latestAnchor.rotation, 0);
                var physical = OriginalFootprintOnTable(0);
                if (!TryBundlePositions(physical, latest, out a, out b))
                {
                    if (bundleContainer.activeSelf) bundleContainer.SetActive(false);
                    if (bundleReviewError != PaperTooSmall)
                    {
                        bundleReviewError = PaperTooSmall;
                        Debug.LogWarning($"{PaperTooSmall} paper={reviewTableSize:F3}, physical={physical}, latest={latest}, candidate_count={bundleCandidates.Length}");
                        Draw();
                    }
                    return;
                }
            }
            if (bundleReviewError == PaperTooSmall) bundleReviewError = null;
            bool reveal = !bundleContainer.activeSelf;
            for (int i = 0; i < bundleCandidates.Length; i++)
            {
                var slot = bundleSlots[i];
                var root = slot.Root.transform;
                if (published)
                {
                    if (!paperCandidatePoses.TryGetValue(TextValue(bundleCandidates[i], "candidate_id"), out var local))
                    {
                        bundleContainer.SetActive(false);
                        return;
                    }
                    var world = PaperToWorldPose(reviewTable, local);
                    root.SetPositionAndRotation(world.position, world.rotation);
                }
                else root.SetPositionAndRotation(reviewTable.position + reviewTable.rotation * (i == 0 ? a : b), latestAnchor.rotation);
                if (SharedPaperMeeting && slot.Material != null)
                {
                    bool selected = TextValue(PaperState, "selected_candidate_id") == TextValue(bundleCandidates[i], "candidate_id");
                    var color = BundleColor(slot.BundleKey);
                    slot.Material.color = selected || bundleHover == slot ? Color.Lerp(color, Color.white, .48f) : color;
                }
                root.localScale = Vector3.one;
                if (!slot.Root.activeSelf) slot.Root.SetActive(true);
                if (slot.Label == null || view == null) continue;
                var bounds = slot.Bounds;
                var caption = root.TransformPoint(new Vector3(bounds.center.x, bounds.max.y + .002f, bounds.min.z - .003f));
                var forward = caption - view.transform.position;
                if (forward.sqrMagnitude > .001f) slot.Label.transform.SetPositionAndRotation(caption, Quaternion.LookRotation(forward, Vector3.up));
            }
            if (reveal)
            {
                bundleContainer.SetActive(true);
                Physics.SyncTransforms();
                Draw();
            }
        }

        static bool FitsPaper(Rect r, Vector2 size) =>
            r.xMin >= PaperEdge - size.x * .5f && r.xMax <= size.x * .5f - PaperEdge &&
            r.yMin >= PaperEdge - size.y * .5f && r.yMax <= size.y * .5f - PaperEdge;

        static bool SpaceBetween(Rect a, Rect b) =>
            a.xMin >= b.xMax + BundleGap || b.xMin >= a.xMax + BundleGap ||
            a.yMin >= b.yMax + BundleGap || b.yMin >= a.yMax + BundleGap;

        bool TryBundlePositions(Rect physical, Rect latest, out Vector3 first, out Vector3 second)
        {
            first = second = default;
            bool sideGallery = CenteredPaperLatest;
            if (!FitsPaper(latest, reviewTableSize) ||
                !sideGallery && (!FitsPaper(physical, reviewTableSize) || !SpaceBetween(physical, latest))) return false;
            var gallerySize = sideGallery ? reviewTableSize + new Vector2(SideReviewMargin * 2, 0) : reviewTableSize;
            Rect previous = default;
            for (int i = 0; i < bundleCandidates.Length; i++)
            {
                var b = bundleSlots[i].Bounds;
                var footprint = FootprintOnTable(b, reviewTable.position, latestAnchor.rotation, 0);
                // The key identifies the physical place even when only B passed validation.
                float sign = bundleSlots[i].BundleKey == "A" ? -1 : 1;
                var center = sideGallery ? new Vector2(sign * ((reviewTableSize.x + footprint.width) * .5f + BundleGap), 0) :
                    new Vector2(sign * .25f, .25f) * reviewTableSize;
                var offset = center - footprint.center;
                footprint.center = center;
                if (!FitsPaper(footprint, gallerySize) || !sideGallery && !SpaceBetween(footprint, physical) ||
                    !SpaceBetween(footprint, latest) || i > 0 && !SpaceBetween(footprint, previous)) return false;
                var position = new Vector3(offset.x, BundleLift - b.min.y, offset.y);
                if (i == 0) first = position; else second = position;
                previous = footprint;
            }
            return true;
        }

        bool HandleBundleCandidateRay(Ray rightAim, bool pressed)
        {
            if (!BundleOnPaper || assemblyStopping || bundleCommitPending || bundleCommitTarget != null || AssemblyAdoptedId != bundleBaseId ||
                assemblyMenu || movingMeetingPanel || confirmingMeetingEnd || !AssemblyHeadTracked()) return false;
            MeetingModelSlot slot = null;
            if (Physics.Raycast(rightAim, out var hit, 12))
                foreach (var item in bundleSlots)
                    if (item.Root != null && hit.transform.IsChildOf(item.Root.transform)) { slot = item; break; }
            SetBundleHover(slot);
            if (slot == null) return false;
            probe.position = hit.point - rightAim.direction * .0015f;
            probe.localScale = Vector3.one * .003f;
            probe.gameObject.SetActive(true);
            if (pressed)
            {
                int index = Array.IndexOf(bundleSlots, slot);
                if (SharedPaperMeeting && !MeetingHost)
                    SendPaperCommand("paper_candidate_select", new JObject { ["candidate_set_id"] = bundleSetId,
                        ["candidate_id"] = bundleCandidates[index]["candidate_id"] });
                else CommitBundleCandidate(index);
            }
            return true;
        }

        void SetBundleHover(MeetingModelSlot slot)
        {
            if (bundleHover == slot) return;
            if (bundleHover?.Material != null) bundleHover.Material.color = BundleColor(bundleHover.BundleKey);
            bundleHover = slot;
            if (slot?.Material != null) slot.Material.color = Color.Lerp(BundleColor(slot.BundleKey), Color.white, .42f);
        }

        void CommitBundleCandidate(int index)
        {
            var transport = meetingTransport;
            var setId = bundleSetId;
            var candidate = bundleCandidates[index];
            var payload = new JObject {
                ["meeting_id"] = transport.SessionId, ["request_id"] = bundleRequestId,
                ["candidate_set_id"] = setId, ["candidate_id"] = candidate["candidate_id"],
                ["base_version_id"] = bundleBaseId, ["expected_latest_version_id"] = AssemblyAdoptedId
            };
            if (SharedPaperMeeting)
            {
                if (!MeetingHost || !BundleOnPaper) return;
                payload["paper_frame_revision"] = PaperFrameRevision;
            }
            AddMeetingClock(payload);
            bundleCommitPending = true;
            bundleReviewError = null;
            Draw();
            StartCoroutine(transport.Json(transport.SessionPath + "/commands", new JObject {
                ["command_id"] = Guid.NewGuid().ToString("N"), ["type"] = "commit_edit_candidate", ["payload"] = payload
            }, (reply, error) =>
            {
                if (meetingTransport != transport || bundleSetId != setId) return;
                bundleCommitPending = false;
                if (reply?["snapshot"] is JObject snapshot) AcceptMeetingSnapshot(snapshot);
                if (bundleSetId == setId && bundleCommitTarget == null)
                {
                    if (error != null) bundleReviewError = "저장 실패 · " + error + " 다시 가리키고 A를 누르세요.";
                    else if (TextValue(reply, "version_id") == TextValue(candidate, "version_id"))
                        bundleCommitTarget = TextValue(candidate, "version_id");
                    else bundleReviewError = "서버에서 채택된 버전을 확인할 수 없습니다.";
                }
                RefreshMeetingSnapshot();
                Draw();
            }));
        }
    }
}
