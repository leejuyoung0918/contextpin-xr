using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        // The primary MRUK path uses XR_EXT spatial entity snapshots. Some Quest
        // runtimes detect a printed QR but don't publish a trackable in that context.
        // The older Meta marker extension has a separate detected-anchor query.
#pragma warning disable 0618
        bool TryStartQrAnchorScan()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (assemblyQrMruk == null || qrAlreadyEnabled || anchorScanTried || !OVRAnchor.TrackerConfiguration.QRCodeTrackingSupported)
                return false;
            anchorScanTried = true;
            if (assemblyQrTrackerRequested) SetQrRequested(false);
            anchorScanRunning = true;
            SetAssemblyQrStatus("QR 마커 직접 조회 중 · 카드를 고정하고 바라보세요.");
            _ = ScanQrAnchors(assemblyQrAttempt);
            return true;
#else
            return false;
#endif
        }

        bool IsAnchorScanCurrent(int attempt) => this && anchorScanRunning && assemblyQrAttempt == attempt && AssemblyQrScanning;

        async Task ScanQrAnchors(int attempt)
        {
            var anchors = new List<OVRAnchor>();
            using var tracker = new OVRAnchor.Tracker();
            try
            {
                var configured = await tracker.ConfigureAsync(new OVRAnchor.TrackerConfiguration { QRCodeTrackingEnabled = true });
                if (!IsAnchorScanCurrent(attempt)) return;
                Debug.Log("ContextPin QR [anchor query] start=" + configured.Status);
                if (!configured.Success) return;
                Pose? previous = null;
                double lastSeen = 0;
                int previousCount = -1;
                while (IsAnchorScanCurrent(attempt))
                {
                    var query = await tracker.FetchTrackablesAsync(anchors);
                    if (!IsAnchorScanCurrent(attempt)) return;
                    if (!query.Success)
                    {
                        Debug.LogWarning("ContextPin QR [anchor query] fetch=" + query.Status);
                        return;
                    }
                    if (anchors.Count != previousCount)
                    {
                        previousCount = anchors.Count;
                        Debug.Log("ContextPin QR [anchor query] markers=" + previousCount);
                    }
                    bool matched = false, multiple = false, other = false;
                    Pose? seen = null;
                    foreach (var anchor in anchors)
                    {
                        if (!anchor.TryGetComponent<OVRMarkerPayload>(out var data) || data.ByteCount <= 0 || data.ByteCount > 2048) continue;
                        var payload = ReadQrPayload(null, data.Bytes.ToArray());
                        if (joiningByQr && TryReadMeetingJoin(payload, out var address, out var code))
                        {
                            AcceptJoinQr(address, code);
                            return;
                        }
                        if (joiningByQr || payload != assemblyQrPayload) { other = true; continue; }
                        matched = true;
                        if (!anchor.TryGetComponent<OVRLocatable>(out var locatable)) continue;
                        if (!locatable.IsEnabled && !await locatable.SetEnabledAsync(true, 1)) continue;
                        if (!IsAnchorScanCurrent(attempt)) return;
                        if (!TryLocatedQrPose(anchor, locatable, out var pose)) continue;
                        if (seen is Pose otherPose && !SameQrPose(otherPose, pose)) multiple = true;
                        seen = pose;
                    }
                    double now = Time.unscaledTimeAsDouble;
                    if (!AssemblyHeadTracked() || multiple)
                    {
                        previous = null;
                        SetAssemblyQrStatus(multiple ? "같은 정렬 카드 두 장이 보입니다. 한 장만 보여 주세요." : "헤드셋 위치를 다시 찾고 있습니다.");
                    }
                    else if (seen is Pose current)
                    {
                        if (previous is Pose last && now - lastSeen < 1.5 && SameQrPose(last, current))
                        {
                            TryPreviewAssemblyQrPose(assemblyQrPayload, current);
                            return;
                        }
                        previous = current;
                        lastSeen = now;
                        SetAssemblyQrStatus("카드 내용 일치 · 위치와 방향을 확인하고 있습니다.");
                    }
                    else SetAssemblyQrStatus(matched ? "정렬 QR 판독 완료 · 네 모서리가 보이도록 카드를 고정해 주세요." :
                        other ? (joiningByQr ? "다른 QR입니다. PC의 참가 QR을 보여 주세요." : "다른 QR입니다. 받침 정렬 카드를 보여 주세요.") :
                        "QR 마커 직접 조회 중 · 카드 전체를 가까이서 정면으로 바라보세요.");
                    ReleaseQrAnchors(anchors);
                    await Task.Delay(350);
                }
            }
            catch (Exception error)
            {
                if (IsAnchorScanCurrent(attempt))
                    Debug.LogWarning("ContextPin QR [anchor query] " + error.GetType().Name);
            }
            finally
            {
                ReleaseQrAnchors(anchors);
                if (IsAnchorScanCurrent(attempt))
                {
                    anchorScanRunning = false;
                    qrRestarted = true;
                    assemblyQrTrackerRequested = true;
                    SetQrRequested(true);
                    assemblyQrDeadline = Time.unscaledTimeAsDouble + 12;
                    SetAssemblyQrStatus("기본 QR 추적기로 돌아왔습니다. 카드의 흰 여백까지 보여 주세요.");
                }
            }
        }

        static bool SameQrPose(Pose a, Pose b) => Vector3.Distance(a.position, b.position) <= .015f &&
            Quaternion.Angle(a.rotation, b.rotation) <= 6f;

        static void ReleaseQrAnchors(List<OVRAnchor> anchors)
        {
            foreach (var anchor in anchors) anchor.Dispose();
            anchors.Clear();
        }

        bool TryLocatedQrPose(OVRAnchor anchor, OVRLocatable locatable, out Pose pose)
        {
            pose = default;
            if (xrRig == null || !anchor.TryGetComponent<OVRBounded2D>(out var plane) ||
                !locatable.TryGetSceneAnchorPose(out var location) || !location.IsPositionTracked || !location.IsRotationTracked) return false;
            var origin = location.ComputeWorldPosition(xrRig.trackingSpace);
            var rotation = location.ComputeWorldRotation(xrRig.trackingSpace);
            if (!origin.HasValue || !rotation.HasValue) return false;
            Rect bounds;
            try { bounds = plane.BoundingBox; }
            catch (InvalidOperationException) { return false; }
            if (!TryQrCenter(bounds, null, out var center)) return false;
            pose = new Pose(origin.Value + rotation.Value * (Vector3)center, rotation.Value * Quaternion.Euler(0, 180, 0));
            return true;
        }
#pragma warning restore 0618
    }
}
