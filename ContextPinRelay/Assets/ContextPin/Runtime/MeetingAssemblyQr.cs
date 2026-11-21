using System;
using System.Collections.Generic;
using System.Text;
using Meta.XR.MRUtilityKit;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        readonly List<MRUKTrackable> assemblyQrTrackables = new List<MRUKTrackable>();
        static readonly UTF8Encoding qrUtf8 = new UTF8Encoding(false, true);
        MRUK assemblyQrMruk;
        MRUKTrackable qrPoseMarker;
        Pose qrFirstPose;
        double qrPoseSince, qrPoseLastSeen;
        bool qrAlreadyEnabled, qrRestartPending, qrRestarted, anchorScanRunning, anchorScanTried;
        readonly HashSet<MRUKTrackable> newlyObservedQr = new HashSet<MRUKTrackable>();
        int lastQrObjects = -1;
        double qrEmptySince;
        string assemblyQrPayload;
        string assemblyQrStatus = "";
        Pose assemblyQrMount;
        bool assemblyQrPoseApplied;
        bool assemblyQrWaitingPermission;
        bool assemblyQrTrackerRequested;
        int assemblyQrAttempt;
        double assemblyQrDeadline, assemblyQrNextSample;
        AssemblyAlignment assemblyQrFallback;

        bool AssemblyQrAvailable => !string.IsNullOrEmpty(assemblyQrPayload);
        bool AssemblyQrScanning => assemblyAlignment == AssemblyAlignment.ScanQr;
        string AssemblyQrStatus => assemblyQrStatus;
        Pose AssemblyPlacementMount => tabletopPoseApplied || a4PoseApplied ? Pose.identity : assemblyQrPoseApplied ? assemblyQrMount : assemblyMount;
        bool AssemblyHasPlacementMount => tabletopPoseApplied || a4PoseApplied || (assemblyQrPoseApplied ? AssemblyQrAvailable : assemblyHasMount);

        void ReadAssemblyQrMount()
        {
            var config = meetingSnapshot?["assembly"]?["qr_alignment"];
            var payload = TextValue(config, "payload");
            assemblyQrPayload = null;
            if (!string.IsNullOrWhiteSpace(payload) && TryReadAssemblyMount(config?["mounting"], out var mount))
            {
                assemblyQrPayload = payload;
                assemblyQrMount = mount;
            }
        }

        void BeginAssemblyQrAlignment()
        {
            if (!xr || !AssemblyQrAvailable || string.IsNullOrEmpty(AssemblyOriginalId) || AssemblyQrScanning) return;
            ClearTabletopAlignment();
            assemblyQrFallback = assemblyAlignment == AssemblyAlignment.Lost ? AssemblyAlignment.Lost : AssemblyAlignment.Unaligned;
            assemblyAlignment = AssemblyAlignment.ScanQr;
            assemblyQrPoseApplied = false;
            assemblyUntrackedSince = -1;
            SetMeetingViewedVersion(AssemblyOriginalId);
            if (OVRManager.IsInsightPassthroughSupported()) SetPassthrough(true);
            if (assemblyRecoveryPanel) RestoreSpatialPanel();
            RequestQrTracker();
        }

        void RequestQrTracker()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission))
            {
                assemblyQrWaitingPermission = true;
                var attempt = ++assemblyQrAttempt;
                SetAssemblyQrStatus("Quest에서 공간 데이터 접근을 허용하세요.");
                var callbacks = new PermissionCallbacks();
                callbacks.PermissionGranted += _ =>
                {
                    if (this && attempt == assemblyQrAttempt && assemblyQrWaitingPermission)
                        StartAssemblyQrTracker();
                };
                callbacks.PermissionDenied += _ =>
                {
                    if (this && attempt == assemblyQrAttempt && assemblyQrWaitingPermission)
                        FailAssemblyQrAlignment(joiningByQr ? "공간 데이터 권한을 허용하거나 참가 코드를 입력하세요." :
                            "공간 데이터 권한을 허용하거나 수동으로 정렬하세요.");
                };
                Permission.RequestUserPermission(OVRPermissionsRequester.ScenePermission, callbacks);
                return;
            }
#endif
            StartAssemblyQrTracker();
        }

        void StartAssemblyQrTracker()
        {
            assemblyQrWaitingPermission = false;
            assemblyQrMruk = GetMeetingMruk();
            newlyObservedQr.Clear();
            assemblyQrMruk.SceneSettings.TrackableAdded.AddListener(OnQrAdded);
            lastQrObjects = -1;
            qrAlreadyEnabled = assemblyQrMruk.SceneSettings.TrackerConfiguration.QRCodeTrackingEnabled;
            qrRestartPending = qrRestarted = anchorScanTried = false;
            qrEmptySince = -1;
            assemblyQrNextSample = 0;
            assemblyQrDeadline = Time.unscaledTimeAsDouble + 12;
            SetAssemblyQrStatus("Quest QR 추적기를 준비하고 있습니다.");
        }

        void OnQrAdded(MRUKTrackable item)
        {
            if (item == null || item.TrackableType != OVRAnchor.TrackableType.QRCode) return;
            newlyObservedQr.Add(item);
            Debug.Log("ContextPin QR [MRUK] added; tracked=" + item.IsTracked +
                " payloadBytes=" + (item.MarkerPayloadBytes?.Length ?? 0) + " rectangle=" + item.PlaneRect.HasValue);
        }

        string QrScanHint => joiningByQr ? "추적기 켜짐 · PC의 참가 QR을 크게 띄우고 가까이서 바라보세요." :
            "추적기 켜짐 · 받침의 정렬 QR과 흰 여백 전체를 가까이서 정면으로 바라보세요.";

        void SetQrRequested(bool enabled)
        {
            var configuration = assemblyQrMruk.SceneSettings.TrackerConfiguration;
            configuration.QRCodeTrackingEnabled = enabled;
            assemblyQrMruk.SceneSettings.TrackerConfiguration = configuration;
        }

        static string ReadQrPayload(string decoded, byte[] bytes)
        {
            // MRUK only fills MarkerPayloadString for text-mode QR codes. Byte-mode
            // codes may carry the same UTF-8 link or registered marker identifier.
            if (string.IsNullOrEmpty(decoded) && bytes != null && bytes.Length > 0 && bytes.Length <= 2048)
            {
                try { decoded = qrUtf8.GetString(bytes); }
                catch (DecoderFallbackException) { return null; }
            }
            return decoded?.TrimEnd('\0').TrimStart('\uFEFF');
        }

        bool RefreshQrTrackables()
        {
            if (assemblyQrWaitingPermission)
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                // Quest can restore focus after the grant before Unity dispatches the callback.
                if (Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission)) StartAssemblyQrTracker();
#endif
                return false;
            }
            var now = Time.unscaledTimeAsDouble;
            if (now < assemblyQrNextSample) return false;
            assemblyQrNextSample = now + .15;
            if (assemblyQrMruk == null || anchorScanRunning) return false;
            // A permission/system overlay may suspend the native service. Give it a full
            // startup window after head tracking returns, without accepting a stale pose.
            if (!AssemblyHeadTracked())
            {
                qrPoseMarker = null;
                assemblyQrDeadline = now + 12;
                qrEmptySince = -1;
                SetAssemblyQrStatus("헤드셋 위치 추적 대기 · 착용한 상태에서 카드를 바라보세요.");
                return false;
            }
            if (!assemblyQrTrackerRequested && assemblyQrMruk.QRCodeTrackingSupported)
            {
                SetQrRequested(true);
                assemblyQrTrackerRequested = true;
                assemblyQrDeadline = now + 12;
                SetAssemblyQrStatus("QR 지원 확인 · Quest 추적기를 켜고 있습니다.");
            }
            bool effective = assemblyQrMruk.TrackerConfiguration.QRCodeTrackingEnabled;
            if (qrRestartPending)
            {
                if (!effective)
                {
                    qrRestartPending = false;
                    if (TryStartQrAnchorScan()) return false;
                    SetQrRequested(true);
                    assemblyQrDeadline = now + 12;
                }
                else if (now >= assemblyQrDeadline)
                    FailAssemblyQrAlignment("QR 추적기를 재시작하지 못했습니다. 스캔을 다시 시작하세요.");
                return false;
            }
            if (!effective || !assemblyQrTrackerRequested)
            {
                if (now >= assemblyQrDeadline && !TryStartQrAnchorScan())
                    FailAssemblyQrAlignment(assemblyQrTrackerRequested
                        ? "공간 권한은 있지만 Quest가 QR 추적기를 켜지 못했습니다. 앱을 다시 열어 주세요."
                        : "현재 XR 런타임이 QR 지원을 보고하지 않았습니다. Quest에서 앱을 다시 열어 주세요.");
                return false;
            }
            assemblyQrMruk.GetTrackables(assemblyQrTrackables);
            if (lastQrObjects != assemblyQrTrackables.Count)
            {
                lastQrObjects = assemblyQrTrackables.Count;
                Debug.Log("ContextPin QR [MRUK] published trackables=" + lastQrObjects);
            }
            // If the runtime doesn't publish a located QR, release our subscription
            // before trying the independent marker-space query on supported headsets.
            bool found = assemblyQrTrackables.Exists(item => item != null && item.IsTracked && item.TrackableType == OVRAnchor.TrackableType.QRCode);
            if (found) qrEmptySince = -1;
            else if (qrEmptySince < 0) qrEmptySince = now;
            else if (!qrAlreadyEnabled && !qrRestarted && now - qrEmptySince > 7)
            {
                qrRestarted = qrRestartPending = true;
                qrPoseMarker = null;
                assemblyQrDeadline = now + 5;
                SetQrRequested(false);
                SetAssemblyQrStatus("카드 위치 대기 · Quest의 다른 QR 조회 경로를 확인합니다.");
                return false;
            }
            return true;
        }

        void UpdateAssemblyQr()
        {
            if (!AssemblyQrScanning || !RefreshQrTrackables()) return;
            MRUKTrackable match = null;
            var hint = QrScanHint;
            foreach (var item in assemblyQrTrackables)
            {
                if (item == null || item.TrackableType != OVRAnchor.TrackableType.QRCode) continue;
                var payload = ReadQrPayload(item.MarkerPayloadString, item.MarkerPayloadBytes);
                if (!item.IsTracked)
                {
                    if (payload == assemblyQrPayload) hint = "정렬 카드 내용 확인 · 현재 위치를 다시 찾도록 카드를 고정해 주세요.";
                    continue;
                }
                if (!string.Equals(assemblyQrPayload, payload, StringComparison.Ordinal))
                {
                    hint = string.IsNullOrEmpty(payload) ? "QR 내용을 읽는 중입니다. 잠시 그대로 바라보세요." :
                        TryReadMeetingJoin(payload, out _, out _) ? "참가 QR을 읽었습니다. 받침에 붙인 정렬 카드를 바라보세요." :
                        "이 회의의 정렬 카드와 다른 QR입니다. 받침 카드를 확인하세요.";
                    continue;
                }
                if (match != null)
                {
                    // A recovered native marker can briefly overlap its previous handle.
                    if (Vector3.Distance(match.transform.position, item.transform.position) < .015f &&
                        Quaternion.Angle(match.transform.rotation, item.transform.rotation) < 6f)
                    {
                        if (item == qrPoseMarker) match = item;
                        continue;
                    }
                    qrPoseMarker = null;
                    SetAssemblyQrStatus("같은 정렬 QR이 두 개 보입니다. 받침 카드 하나만 보이게 해주세요.");
                    return;
                }
                match = item;
            }
            var now = Time.unscaledTimeAsDouble;
            if (match == null || !TryQrCenter(match.PlaneRect, match.PlaneBoundary2D, out var planeCenter))
            {
                if (now - qrPoseLastSeen > 1) qrPoseMarker = null;
                SetAssemblyQrStatus(match == null ? hint : "카드 내용 일치 · QR 경계를 찾고 있습니다. 네 모서리를 보여 주세요.");
                return;
            }
            // MRUK applies an additional local Y half-turn for plane anchors.
            var center = match.transform.TransformPoint(planeCenter);
            var pose = new Pose(center, match.transform.rotation * Quaternion.AngleAxis(180, Vector3.up));
            if (now - qrPoseLastSeen > 1) qrPoseMarker = null;
            qrPoseLastSeen = now;
            if (match != qrPoseMarker || Vector3.Distance(pose.position, qrFirstPose.position) > .015f ||
                Quaternion.Angle(pose.rotation, qrFirstPose.rotation) > 6f)
            {
                qrPoseMarker = match;
                qrFirstPose = pose;
                qrPoseSince = now;
                SetAssemblyQrStatus("정렬 QR을 읽었습니다. 위치를 확인하는 동안 카드를 고정하세요.");
            }
            else if (now - qrPoseSince >= .25)
                TryPreviewAssemblyQrPose(assemblyQrPayload, pose);
        }

        static bool TryQrCenter(Rect? rectangle, List<Vector2> boundary, out Vector2 center)
        {
            if (rectangle is Rect rect && rect.width > 0 && rect.height > 0 && float.IsFinite(rect.center.sqrMagnitude))
            {
                center = rect.center;
                return true;
            }
            center = default;
            if (boundary == null || boundary.Count < 4) return false;
            var min = boundary[0];
            var max = min;
            foreach (var corner in boundary)
            {
                if (!float.IsFinite(corner.sqrMagnitude)) return false;
                min = Vector2.Min(min, corner);
                max = Vector2.Max(max, corner);
            }
            if (max.x <= min.x || max.y <= min.y) return false;
            center = (min + max) * .5f;
            return true;
        }

        bool TryPreviewAssemblyQrPose(string payload, Pose reference)
        {
            // This is also the boundary used by simulator smoke with a synthetic tracked pose.
            if (!AssemblyQrScanning || !AssemblyQrAvailable || !string.Equals(payload, assemblyQrPayload, StringComparison.Ordinal)) return false;
            var position = reference.position;
            var rotation = reference.rotation;
            var magnitude = Quaternion.Dot(rotation, rotation);
            if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z) ||
                !float.IsFinite(magnitude) || Mathf.Abs(magnitude - 1f) > .02f) return false;
            Debug.Log("ContextPin QR [assembly] 카드 일치 · 추적된 중심 및 방향 확보");
            StopAssemblyQrTracker();
            assemblyReference = new Pose(position, rotation.normalized);
            assemblyQrPoseApplied = true;
            assemblyAlignment = AssemblyAlignment.Preview;
            assemblyQrStatus = "";
            notice = "기준 QR에 맞췄습니다. 원본이 실물과 겹치는지 확인하세요.";
            PositionMeetingModel();
            Draw();
            return true;
        }

        void CancelAssemblyQrAlignment()
        {
            if (AssemblyQrScanning) FailAssemblyQrAlignment("");
        }

        void FailAssemblyQrAlignment(string message)
        {
            if (!string.IsNullOrEmpty(message)) Debug.LogWarning("ContextPin QR: " + message);
            if (joiningByQr) StopJoinQr();
            else
            {
                StopAssemblyQrTracker();
                assemblyAlignment = assemblyQrFallback;
            }
            assemblyQrStatus = message;
            notice = message;
            PositionMeetingModel();
            Draw();
        }

        void SetAssemblyQrStatus(string message)
        {
            if (assemblyQrStatus == message) return;
            assemblyQrStatus = message;
            Debug.Log("ContextPin QR [" + (joiningByQr ? "join" : "assembly") + "] " + message);
            notice = message;
            Draw();
        }

        void StopAssemblyQrTracker()
        {
            ++assemblyQrAttempt;
            assemblyQrWaitingPermission = false;
            if (assemblyQrMruk != null)
            {
                assemblyQrMruk.SceneSettings.TrackableAdded.RemoveListener(OnQrAdded);
                if (assemblyQrTrackerRequested) SetQrRequested(qrAlreadyEnabled);
            }
            newlyObservedQr.Clear();
            anchorScanRunning = false;
            qrRestartPending = false;
            assemblyQrTrackerRequested = false;
            qrPoseMarker = null;
            assemblyQrMruk = null;
            assemblyQrTrackables.Clear();
        }
    }
}
