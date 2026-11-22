using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.XR;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        enum AssemblyAlignment { Unaligned, CaptureOrigin, CaptureRight, CaptureUp, Preview, Confirmed, Lost, ScanQr, LocalPreview, CaptureTabletop, CaptureA4 }
        AssemblyAlignment assemblyAlignment;
        Pose assemblyReference, assemblyMount;
        Vector3 assemblyOrigin, assemblyRight;
        bool assemblyHasMount;
        double assemblyUntrackedSince = -1;
        bool IsAssemblyMeeting => TextValue(meetingSnapshot?["assembly"], "mode") == "openscad";
        bool AssemblySampling => assemblyAlignment is AssemblyAlignment.CaptureOrigin or AssemblyAlignment.CaptureRight or AssemblyAlignment.CaptureUp or AssemblyAlignment.CaptureTabletop or AssemblyAlignment.CaptureA4;
        bool AssemblyOverlayAligned => assemblyAlignment is AssemblyAlignment.Preview or AssemblyAlignment.Confirmed;
        bool UsingLocalCandidatePreview => assemblyAlignment == AssemblyAlignment.LocalPreview;
        bool CanCompareModels => assemblyAlignment == AssemblyAlignment.Confirmed || UsingLocalCandidatePreview;
        string AssemblyOriginalId => TextValue(Values(meetingSnapshot, "versions").FirstOrDefault(), "version_id");
        static readonly Color AssemblyReferenceColor = new Color(.16f, .8f, .86f, .5f);

        void StartLocalCandidatePreview()
        {
            if (!SpatialAssembly || SharedPaperMeeting && !MeetingHost || ExplicitDesignMeeting || CanCompareModels || A4PointSampling || A4PreviewActive || view == null) return;
            StopAssemblyQrTracker();
            assemblyQrPoseApplied = false;
            assemblyQrStatus = "";
            ClearTabletopAlignment();
            assemblyAlignment = AssemblyAlignment.LocalPreview;
            if (assemblyRecoveryPanel) RestoreSpatialPanel();
            QueueMeetingModel(RequestedMeetingVersion());
            Draw();
        }

        void StopLocalCandidatePreview()
        {
            if (!UsingLocalCandidatePreview) return;
            assemblyAlignment = AssemblyAlignment.Unaligned;
            PositionMeetingModel();
            Draw();
        }

        // A child session opened immediately on the same headset can keep a confirmed frame.
        // Reopening the app never restores a real-world pose from disk.
        (Pose reference, Pose mount, string qrPayload, bool tabletop, bool a4, Pose? confirmedArea, Vector2 areaSize)? CaptureConfirmedComparisonFrame() =>
            assemblyAlignment == AssemblyAlignment.Confirmed && AssemblyHeadTracked()
                ? (assemblyReference, AssemblyPlacementMount, assemblyQrPoseApplied ? assemblyQrPayload : null,
                    tabletopPoseApplied, a4PoseApplied, (tableAreaConfirmed || tableA4Measured) && HasReviewTable ? (Pose?)reviewTable : null, reviewTableSize)
                : null;

        bool RestoreConfirmedComparisonFrame((Pose reference, Pose mount, string qrPayload, bool tabletop, bool a4, Pose? confirmedArea, Vector2 areaSize)? previous)
        {
            if (!previous.HasValue || !SpatialAssembly || !AssemblyHeadTracked()) return false;
            var frame = previous.Value;
            bool qr = frame.qrPayload != null;
            if (!frame.tabletop && !frame.a4 && (qr ? frame.qrPayload != assemblyQrPayload : !assemblyHasMount)) return false;
            var mount = frame.tabletop || frame.a4 ? Pose.identity : qr ? assemblyQrMount : assemblyMount;
            if (Vector3.Distance(mount.position, frame.mount.position) > .0001f ||
                Quaternion.Angle(mount.rotation, frame.mount.rotation) > .01f) return false;
            assemblyReference = frame.reference;
            assemblyQrPoseApplied = qr;
            tabletopPoseApplied = frame.tabletop;
            a4PoseApplied = frame.a4;
            assemblyAlignment = AssemblyAlignment.Confirmed;
            if (frame.confirmedArea.HasValue) StoreReviewTable(frame.confirmedArea.Value, frame.areaSize, true, !frame.a4, frame.a4);
            QueueMeetingModel(RequestedMeetingVersion());
            Draw();
            return true;
        }

        void ReadAssemblyMount()
        {
            assemblyHasMount = TryReadAssemblyMount(meetingSnapshot?["assembly"]?["mounting"], out assemblyMount);
            ReadAssemblyQrMount();
        }

        static bool TryReadAssemblyMount(JToken mounting, out Pose pose)
        {
            pose = default;
            var position = ReadMeetingVector(mounting?["position_m"]);
            if (position is not Vector3 point || mounting?["rotation_xyzw"] is not JArray rotation ||
                rotation.Count != 4 || rotation.Any(value => value.Type != JTokenType.Float && value.Type != JTokenType.Integer)) return false;
            var q = new Quaternion(rotation[0].Value<float>(), rotation[1].Value<float>(),
                rotation[2].Value<float>(), rotation[3].Value<float>());
            var magnitude = Quaternion.Dot(q, q);
            if (!float.IsFinite(magnitude) || Mathf.Abs(magnitude - 1f) > .02f) return false;
            pose = new Pose(point, q.normalized);
            return true;
        }

        void BeginAssemblyAlignment()
        {
            if (!xr || !assemblyHasMount || string.IsNullOrEmpty(AssemblyOriginalId)) return;
            StopAssemblyQrTracker();
            assemblyQrPoseApplied = false;
            assemblyQrStatus = "";
            ClearTabletopAlignment();
            assemblyAlignment = AssemblyAlignment.CaptureOrigin;
            assemblyUntrackedSince = -1;
            SetMeetingViewedVersion(AssemblyOriginalId);
            if (OVRManager.IsInsightPassthroughSupported()) SetPassthrough(true);
            if (assemblyRecoveryPanel) RestoreSpatialPanel();
            notice = "고정된 기준물의 원점부터 짚으세요.";
            Draw();
        }

        bool AssemblyHeadTracked() => InputDevices.GetDeviceAtXRNode(XRNode.Head)
            .TryGetFeatureValue(UnityEngine.XR.CommonUsages.isTracked, out var tracked) && tracked;

        bool UpdateAssemblyProbe(bool onControl)
        {
            Vector3 point = default;
            bool located = AssemblyHeadTracked() && TryProbe(out point);
            probe.gameObject.SetActive(located);
            if (located)
            {
                probe.position = point;
                probe.localScale = Vector3.one * .009f;
            }
            bool mark = OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.RTouch) ||
                hands.Any(hand => hand.GetHand() == OVRPlugin.Hand.HandLeft && Pinching(hand));
            if (!mark || wasMarking || onControl) return mark;
            if (!located)
            {
                notice = "헤드셋과 오른손 검지 또는 오른쪽 컨트롤러 추적을 확인하세요.";
                Draw();
                return true;
            }
            if (ReviewTableSampling) RecordTablePoint(point);
            else RecordAssemblyPoint(point);
            return true;
        }

        void RecordAssemblyPoint(Vector3 point)
        {
            switch (assemblyAlignment)
            {
                case AssemblyAlignment.CaptureOrigin:
                    assemblyOrigin = point;
                    assemblyAlignment = AssemblyAlignment.CaptureRight;
                    notice = "기준 원점에서 +X 방향 점을 짚으세요.";
                    break;
                case AssemblyAlignment.CaptureRight:
                    var across = point - assemblyOrigin;
                    if (across.magnitude < .025f) { notice = "+X 점을 원점에서 25mm 이상 떨어진 곳에 지정하세요."; break; }
                    assemblyRight = across.normalized;
                    assemblyAlignment = AssemblyAlignment.CaptureUp;
                    notice = "기준 원점에서 +Y 방향 점을 짚으세요.";
                    break;
                case AssemblyAlignment.CaptureUp:
                    var along = point - assemblyOrigin;
                    if (along.magnitude < .025f || Mathf.Abs(Vector3.Dot(along.normalized, assemblyRight)) > .28f)
                    {
                        notice = "+X와 직각인 +Y 방향의 점을 다시 짚으세요.";
                        break;
                    }
                    var forward = Vector3.Cross(assemblyRight, along).normalized;
                    assemblyReference = new Pose(assemblyOrigin, Quaternion.LookRotation(forward, Vector3.Cross(forward, assemblyRight)));
                    assemblyAlignment = AssemblyAlignment.Preview;
                    notice = "반투명 원본의 결합 위치와 방향을 실물에 대조하세요.";
                    break;
            }
            PositionMeetingModel();
            Draw();
        }

        bool OriginalOverlayLoaded => A4PreviewActive
            ? meetingLoadedVersion == AssemblyAdoptedId && meetingModel != null && meetingModel.activeInHierarchy
            : ExplicitDesignMeeting
                ? physicalOriginal.LoadedVersion == AssemblyOriginalId && physicalOriginal.Root != null && physicalOriginal.Root.activeInHierarchy
                : meetingLoadedVersion == AssemblyOriginalId;

        void ConfirmAssemblyAlignment()
        {
            if (!AssemblyHeadTracked() || !AssemblyHasPlacementMount ||
                assemblyAlignment != AssemblyAlignment.Preview || !OriginalOverlayLoaded && (!SharedPaperMeeting || MeetingHost)) return;
            if (!ConfirmTabletopReviewArea() || !ConfirmA4ReviewArea()) return;
            assemblyAlignment = AssemblyAlignment.Confirmed;
            if (A4PoseApplied) captureWorkspaceRevision++;
            if (tabletopPoseApplied) Debug.Log("ContextPin tabletop: confirmed · " +
                (tableAreaConfirmed ? "confirmed comparison area" : tableManual ? "measured desk" : "tracked desk"));
            if (ExplicitDesignMeeting) PrepareExplicitCamera();
            notice = "원본 정렬을 확인했습니다.";
            if (SpatialAssembly)
            {
                assemblyCompare = assemblyMenu = false;
                SetMeetingViewedVersion(null);
                RestoreSpatialPanel();
                groovePreviewEnabled = false;
                PositionGroovePreviews();
            }
            Draw();
        }

        void UpdateAssemblyTracking()
        {
            if (tabletopOutline != null && TabletopNeedsSpaceConfirmation)
                tabletopOutline.gameObject.SetActive(AssemblyHeadTracked());
            if (AssemblyQrScanning) UpdateAssemblyQr();
            if ((tabletopPoseApplied || a4PoseApplied || (A4PointSampling && a4Count > 0)) && !AssemblyHeadTracked())
            {
                InvalidateAssemblyAlignment();
                return;
            }
            UpdateReviewTable();
            if (!xr || !IsAssemblyMeeting || (!AssemblySampling && !AssemblyOverlayAligned)) return;
            if (AssemblyHeadTracked() || TabletopPointSampling || A4PointSampling) { assemblyUntrackedSince = -1; return; }
            if (assemblyUntrackedSince < 0) assemblyUntrackedSince = Time.unscaledTimeAsDouble;
            if (Time.unscaledTimeAsDouble - assemblyUntrackedSince < .4) return;
            InvalidateAssemblyAlignment();
        }

        void InvalidateAssemblyAlignment()
        {
            EndOpenHeldCommand();
            latestGrab = null;
            InvalidateExplicitSelection();
            bool hadTable = tableRegistered || ReviewTableSampling;
            ClearReviewTable();
            if (hadTable) { PositionMeetingModel(); Draw(); }
            if (TabletopPointSampling || (A4PointSampling && a4Count == 0)) return;
            if (AssemblyQrScanning) { qrPoseMarker = null; return; }
            if (!IsAssemblyMeeting || assemblyQrWaitingPermission ||
                (!AssemblySampling && !AssemblyOverlayAligned && !AssemblyQrScanning)) return;
            StopAssemblyQrTracker();
            assemblyQrStatus = "";
            if (tabletopPoseApplied || TabletopPointSampling) Debug.Log("ContextPin tabletop: tracking invalidated");
            if (a4PoseApplied || A4PointSampling) Debug.Log("ContextPin A4: tracking invalidated");
            ClearTabletopAlignment();
            assemblyAlignment = AssemblyAlignment.Lost;
            notice = "추적이 끊겨 실물 위 표시를 숨겼습니다. 원본을 다시 맞추세요.";
            PublishTrackingLoss();
            PositionMeetingModel();
            Draw();
        }

        // Every committed mesh shares its original mounting pose, regardless of its own bounds.
        Transform latestAnchor, a4DisplayRoot;
        string latestPaperError;
        static readonly Color PaperLatestColor = new Color32(64, 216, 210, 215);
        // The deployed phone profile's wire ID predates the offset layout. Keeping it also
        // lets resumed rooms and mixed client versions follow the host's published poses.
        bool OffsetPaperGallery => TextValue(meetingSnapshot?["profile"], "review_layout") == "center_latest_side_candidates";
        const float PaperLatestRight = .24f;
        const float PaperCandidateSide = .14f, PaperCandidateForward = .24f;

        void PositionA4Latest()
        {
            if (a4DisplayRoot == null)
            {
                a4DisplayRoot = new GameObject("A4Root").transform;
                a4DisplayRoot.SetParent(transform, false);
            }
            var surface = A4ReviewSurfaceActive ? reviewTable : a4Surface;
            a4DisplayRoot.SetPositionAndRotation(surface.position, surface.rotation);
            if (latestAnchor == null)
            {
                latestAnchor = new GameObject("LatestAnchor").transform;
                latestAnchor.SetParent(a4DisplayRoot, false);
            }
            var paper = A4CornerCount == 4 ? a4Size : reviewTableSize;
            var origin = OffsetPaperGallery ? surface.position : WorkspaceOrigin;
            var besideProduct = OffsetPaperGallery ? Vector3.right * PaperLatestRight : new Vector3(paper.x * .25f, 0, -paper.y * .25f);
            var shownPose = GrabApplies ? latestGrab.Preview : paperLatestPose;
            var mount = SharedPaperMeeting && shownPose.HasValue ? PaperToWorldPose(surface, shownPose.Value) :
                new Pose(origin + surface.rotation * besideProduct, WorkspaceRotation);
            latestAnchor.SetPositionAndRotation(mount.position, mount.rotation);
            var model = meetingModel.transform;
            var previousError = latestPaperError;
            latestPaperError = null;
            if ((PreparingLivePaperMeeting || CandidateGenerationMeeting) && !(SharedPaperMeeting && paperLatestPose.HasValue))
            {
                if (sessionOriginalBounds is not Bounds original)
                    latestPaperError = "중앙 실물의 원본 치수를 확인하고 있습니다.";
                else
                {
                    var physical = SurfaceFootprint(surface, original, origin, WorkspaceRotation, 0);
                    var latest = SurfaceFootprint(surface, meetingAssetBounds, latestAnchor.position, latestAnchor.rotation, 0);
                    if (OffsetPaperGallery && !SpaceBetween(physical, latest))
                        latestPaperError = "중앙 실물과 오른쪽 Latest가 겹칩니다. 모델 치수를 확인하세요.";
                    else if (!OffsetPaperGallery && (!FitsPaper(physical, paper) || !FitsPaper(latest, paper) || !SpaceBetween(physical, latest)))
                        latestPaperError = "앞오른쪽 고정 자리에서 중앙 실물과 Latest 사이의 여백을 확보할 수 없습니다.";
                }
                if (latestPaperError != null)
                {
                    if (meetingModel.activeSelf) meetingModel.SetActive(false);
                    if (latestPaperError != previousError) { Debug.LogWarning(latestPaperError); Draw(); }
                    return;
                }
            }
            bool attached = model.parent != latestAnchor;
            bool revealed = !meetingModel.activeSelf;
            if (attached) model.SetParent(latestAnchor, false);
            model.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            model.localScale = Vector3.one;
            if (revealed) meetingModel.SetActive(true);
            if (attached || revealed)
            {
                if ((PreparingLivePaperMeeting || CandidateGenerationMeeting) && meetingPreview.Material != null)
                    meetingPreview.Material.color = PaperLatestColor;
                string label = TextValue(MeetingVersion(meetingLoadedVersion), "design_version");
                Debug.Log($"ContextPin A4 Latest: version_id={meetingLoadedVersion} design_version={label} " +
                    $"anchor={latestAnchor.GetEntityId()} position_m={latestAnchor.position:F5} " +
                    $"rotation_xyzw={latestAnchor.rotation:F5} size_m={meetingAssetBounds.size:F5}");
            }
            // The version is on the panel. Keep the physical paper and its CAD unobstructed.
            if (meetingPreview.Label != null && meetingPreview.Label.activeSelf) meetingPreview.Label.SetActive(false);
            if (previousError != null) Draw();
        }

        void PositionAssemblyPreview()
        {
            if (PaperLayoutNeedsReset || SharedPaperMeeting && !MeetingHost && !PaperRenderable)
            {
                if (meetingModel.activeSelf) meetingModel.SetActive(false);
                return;
            }
            if (SpatialAssembly && A4PoseApplied && AssemblyOverlayAligned)
            {
                PositionA4Latest();
                return;
            }
            if (A4PointSampling)
            {
                if (meetingModel.activeSelf) meetingModel.SetActive(false);
                return;
            }
            if (latestAnchor != null && meetingModel.transform.parent == latestAnchor)
                meetingModel.transform.SetParent(null, true);
            if (SpatialAssembly && ExplicitDesignMeeting)
            {
                if (assemblyAlignment == AssemblyAlignment.Confirmed && HasReviewTable)
                {
                    if (PlaceReviewModel(meetingPreview, 0, ReviewLayoutCount)) PositionLatestLabel();
                }
                else if (meetingModel.activeSelf) meetingModel.SetActive(false);
                return;
            }
            if (UsingLocalCandidatePreview || (SpatialAssembly && assemblyAlignment == AssemblyAlignment.Confirmed &&
                meetingLoadedVersion != AssemblyOriginalId))
            {
                PlaceReviewModel(meetingPreview, 0, ReviewLayoutCount);
                return;
            }
            bool visible = AssemblyHasPlacementMount && (!SpatialAssembly || AssemblyOverlayAligned);
            if (meetingModel.activeSelf != visible) meetingModel.SetActive(visible);
            if (!visible) return;
            var reference = AssemblyOverlayAligned ? assemblyReference : new Pose(
                panel.TransformPoint(new Vector3(-285, 5, 0)) - panel.forward * .44f, panel.rotation);
            // The GLB importer has already converted glTF axes. This Unity-local mount
            // and the manually registered reference are common to every model version.
            var mount = AssemblyPlacementMount;
            meetingModel.transform.SetPositionAndRotation(reference.position + reference.rotation * mount.position,
                reference.rotation * mount.rotation);
            meetingModel.transform.localScale = Vector3.one;
        }

        static Material CreateAssemblyMeshMaterial(MeshFilter[] filters, bool comparison)
        {
            var shader = Resources.Load<Shader>("MeetingAssemblyOverlay");
            if (shader == null) throw new InvalidOperationException("원본과 수정안의 반투명 표시를 준비하지 못했습니다.");
            var color = AssemblyReferenceColor;
            if (comparison) color.a = .82f;
            var material = new Material(shader)
            {
                color = color,
            };
            foreach (var filter in filters)
                if (filter.TryGetComponent<MeshRenderer>(out var renderer)) renderer.sharedMaterial = material;
            return material;
        }

        string AssemblyAlignmentText()
        {
            if (UsingLocalCandidatePreview) return "가상 비교 · 실물 정렬 전";
            if (!assemblyHasMount && !AssemblyQrAvailable && !TabletopPointSampling && !A4PointSampling && !AssemblyHasPlacementMount) return "장착 변환 설정이 필요합니다.";
            if (!xr) return "미터 단위 모델 비교 · 데스크톱 보기";
            return assemblyAlignment switch
            {
                AssemblyAlignment.ScanQr => assemblyQrWaitingPermission ? "공간 데이터 권한 대기" : "기준 QR 찾는 중",
                AssemblyAlignment.CaptureTabletop => "책상 한 점 지정",
                AssemblyAlignment.CaptureA4 => $"A4 모서리 {a4Count + 1}/4",
                AssemblyAlignment.CaptureOrigin => "1/3 · 기준 원점",
                AssemblyAlignment.CaptureRight => "2/3 · 기준 +X 방향 점",
                AssemblyAlignment.CaptureUp => "3/3 · 기준 +Y 방향 점",
                AssemblyAlignment.Preview => "실물에 원본이 맞는지 확인하세요",
                AssemblyAlignment.Confirmed when a4PoseApplied => "A4 기준 정렬 확인",
                AssemblyAlignment.Confirmed when tabletopPoseApplied => "책상 한 점 정렬 확인",
                AssemblyAlignment.Confirmed => assemblyQrPoseApplied ? "QR 정렬 확인" : "수동 정렬 확인",
                AssemblyAlignment.Lost => "추적 끊김 · 실물 겹쳐 보기 숨김",
                _ => "가상 보기 · 실물 정렬 전",
            };
        }

        void DrawAssemblyControls()
        {
            var original = AssemblyOriginalId;
            if (!string.IsNullOrEmpty(original) && !AssemblySampling)
                Button("MeetingOriginal", 36, 548, 194, 50, "원본 모델", () => { SetMeetingViewedVersion(original); Draw(); },
                    meetingLoadedVersion == original);
            if (!xr || !assemblyHasMount) return;
            if (AssemblySampling)
            {
                Label("AssemblyRegistrationInstruction", content, 55, 268, 566, 134,
                    "고정된 기준물의 표시를 오른손 검지로 짚고 왼손을 핀치하세요. 컨트롤러는 추적 점을 기준에 맞춘 뒤 A를 누르세요.", 23, Light);
                Button("AssemblyAlignmentRetry", 242, 548, 204, 50, "원점부터 다시", BeginAssemblyAlignment);
                Button("AssemblyAlignmentCancel", 458, 548, 184, 50, "정렬 취소", () =>
                {
                    assemblyAlignment = AssemblyAlignment.Unaligned;
                    SetPassthrough(false);
                    Recenter();
                    notice = "";
                    Draw();
                });
                return;
            }
            Button("AssemblyAlignmentStart", 242, 548, 204, 50,
                assemblyAlignment == AssemblyAlignment.Unaligned ? "원본 맞추기" : "다시 맞추기", BeginAssemblyAlignment);
            if (assemblyAlignment == AssemblyAlignment.Preview && meetingLoadedVersion == original)
                Button("AssemblyAlignmentConfirm", 458, 548, 184, 50, "실물 일치 확인", ConfirmAssemblyAlignment, true);
        }

        void ResetAssemblyPreview()
        {
            StopAssemblyQrTracker();
            assemblyAlignment = AssemblyAlignment.Unaligned;
            assemblyUntrackedSince = -1;
            assemblyHasMount = false;
            ClearTabletopAlignment();
            ClearReviewTable();
            assemblyQrPoseApplied = false;
            assemblyQrPayload = null;
            assemblyQrStatus = "";
        }
    }
}
