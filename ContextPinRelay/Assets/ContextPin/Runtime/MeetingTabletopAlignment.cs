using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        bool tabletopPoseApplied, tabletopProposesArea;
        Vector3 tabletopContact, tabletopLocalContact;
        Quaternion tabletopHeading;
        Pose proposedReviewArea;
        Vector2 proposedReviewSize;
        LineRenderer tabletopOutline;

        bool TabletopPointSampling => assemblyAlignment == AssemblyAlignment.CaptureTabletop;
        bool TabletopPreviewActive => tabletopPoseApplied && assemblyAlignment == AssemblyAlignment.Preview;
        bool TabletopPoseApplied => tabletopPoseApplied;
        bool TabletopNeedsSpaceConfirmation => TabletopPreviewActive && tabletopProposesArea;

        void BeginTabletopAlignment()
        {
            if (!SpatialAssembly || !xr || string.IsNullOrEmpty(AssemblyOriginalId)) return;
            StopAssemblyQrTracker();
            InvalidateExplicitSelection();
            if (tableAreaConfirmed || tableA4Measured || ReviewTableSampling) ClearReviewTable();
            ClearTabletopAlignment();
            assemblyQrPoseApplied = false;
            assemblyQrStatus = "";
            assemblyAlignment = AssemblyAlignment.CaptureTabletop;
            assemblyUntrackedSince = -1;
            SetMeetingViewedVersion(AssemblyOriginalId);
            if (OVRManager.IsInsightPassthroughSupported()) SetPassthrough(true);
            if (assemblyRecoveryPanel) RestoreSpatialPanel();
            notice = "실물 앞면 가운데 바로 앞의 상판에 오른손 표시점을 맞추고 A를 누르세요.";
            Debug.Log("ContextPin tabletop: waiting for right controller contact");
            PositionMeetingModel();
            Draw();
        }

        bool TryTabletopProbe(out Vector3 point)
        {
            point = default;
            if (xrRig == null || xrRig.trackingSpace == null || !AssemblyHeadTracked()) return false;
            var right = XRController.rightHand;
            const int required = (int)(InputTrackingState.Position | InputTrackingState.Rotation);
            Vector3 position;
            Quaternion rotation;
            if (right != null && (right.trackingState.ReadValue() & required) == required)
            {
                position = right.devicePosition.ReadValue();
                rotation = right.deviceRotation.ReadValue();
            }
            else if (OVRInput.GetControllerPositionTracked(OVRInput.Controller.RTouch) &&
                     OVRInput.GetControllerOrientationTracked(OVRInput.Controller.RTouch))
            {
                position = OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch);
                rotation = OVRInput.GetLocalControllerRotation(OVRInput.Controller.RTouch);
            }
            else return false;
            float norm = Quaternion.Dot(rotation, rotation);
            if (!float.IsFinite(position.sqrMagnitude) || !float.IsFinite(norm) || Mathf.Abs(norm - 1) > .02f)
                return false;
            point = xrRig.trackingSpace.TransformPoint(position + rotation * Vector3.forward * .10f);
            return float.IsFinite(point.sqrMagnitude);
        }

        bool TryConfirmTabletopPoint()
        {
            if (!TabletopPointSampling || movingMeetingPanel || ReviewTableSampling) return false;
            if (view == null || !TryTabletopProbe(out var point))
            {
                notice = "오른쪽 컨트롤러와 헤드셋의 위치 추적을 확인한 뒤 A를 다시 누르세요.";
                Draw();
                return false;
            }
            return PreviewTabletopPoint(point, view.transform.right);
        }

        bool TryGetTabletopOriginalBounds(out Bounds bounds)
        {
            if (A4PointSampling && sessionOriginalBounds.HasValue)
            {
                bounds = sessionOriginalBounds.Value;
                return true;
            }
            var original = physicalOriginal.LoadedVersion == AssemblyOriginalId && physicalOriginal.Root != null
                ? physicalOriginal : meetingPreview;
            bounds = original.Bounds;
            return original.Root != null && original.LoadedVersion == AssemblyOriginalId &&
                float.IsFinite(bounds.center.sqrMagnitude) && float.IsFinite(bounds.size.sqrMagnitude) &&
                bounds.size.x > 0 && bounds.size.z > 0;
        }

        // The marker is on the accessible front-bottom edge of V1. Its CAD origin can
        // be anywhere in the asset, so retain the local landmark when adjusting yaw.
        bool PreviewTabletopPoint(Vector3 point, Vector3 viewerRight)
        {
            if (!TabletopPointSampling || !AssemblyHeadTracked() || !float.IsFinite(point.sqrMagnitude)) return false;
            if (!TryGetTabletopOriginalBounds(out var original))
            {
                notice = "실물 원본 모델을 준비 중입니다. 준비된 후 A를 다시 누르세요.";
                Draw();
                return false;
            }
            var right = Vector3.ProjectOnPlane(viewerRight, Vector3.up);
            if (!float.IsFinite(right.sqrMagnitude) || right.sqrMagnitude < .01f) return false;
            tabletopHeading = Quaternion.LookRotation(Vector3.Cross(right.normalized, Vector3.up), Vector3.up);
            tabletopProposesArea = !TabletopSharesRegisteredSurface(point);
            if (!tabletopProposesArea) point.y = reviewTable.position.y;
            tabletopContact = point;
            tabletopLocalContact = new Vector3(original.center.x, original.min.y, original.min.z);
            tabletopPoseApplied = true;
            assemblyAlignment = AssemblyAlignment.Preview;
            SetTabletopRotation(tabletopHeading, original);
            Debug.Log("ContextPin tabletop: point accepted · V1 preview · " +
                (tabletopProposesArea ? "area outline" : tableManual ? "measured desk" : "tracked desk"));
            return true;
        }

        bool TabletopSharesRegisteredSurface(Vector3 contact)
        {
            if (!HasReviewTable || Mathf.Abs(contact.y - reviewTable.position.y) > .025f) return false;
            var offset = Quaternion.Inverse(reviewTable.rotation) * (contact - reviewTable.position);
            return Mathf.Abs(offset.x) <= reviewTableSize.x * .5f + .02f &&
                Mathf.Abs(offset.z) <= reviewTableSize.y * .5f + .02f;
        }

        void RotateTabletopPreview(float degrees)
        {
            if (!TabletopPreviewActive || !AssemblyHeadTracked() || !float.IsFinite(degrees) ||
                !TryGetTabletopOriginalBounds(out var original)) return;
            SetTabletopRotation(Quaternion.AngleAxis(degrees, Vector3.up) * assemblyReference.rotation, original);
        }

        void SetTabletopRotation(Quaternion rotation, Bounds original)
        {
            assemblyReference = new Pose(tabletopContact - rotation * tabletopLocalContact, rotation);
            if (tabletopProposesArea) PrepareTabletopReviewArea(original);
            notice = tabletopProposesArea ? "실물 원본과 오른쪽 테두리 전체가 빈 상판에 맞는지 확인하세요." :
                "반투명 원본이 실물의 위치와 방향에 맞는지 확인하세요.";
            PositionMeetingModel();
            Draw();
        }

        void PrepareTabletopReviewArea(Bounds original)
        {
            // This outlined area is proposed to the wearer. Only confirmation registers
            // it for comparisons. It is independent of the room mesh and physical edges.
            Vector3 span = Vector3.Max(original.size, meetingAssetBounds.size);
            proposedReviewSize = new Vector2(
                Mathf.Max(.20f, span.x + Mathf.Max(.15f, span.x + .025f) + .05f),
                Mathf.Max(.15f, span.z + Mathf.Max(.10f, span.z + .025f) + .05f));
            var right = tabletopHeading * Vector3.right;
            var rotation = assemblyReference.rotation;
            var halfWidth = Mathf.Abs(Vector3.Dot(right, rotation * Vector3.right)) * original.extents.x +
                            Mathf.Abs(Vector3.Dot(right, rotation * Vector3.forward)) * original.extents.z;
            var center = assemblyReference.position + rotation * original.center;
            center.y = tabletopContact.y;
            proposedReviewArea = new Pose(center + right * (halfWidth + .045f + proposedReviewSize.x * .5f), tabletopHeading);
            if (tabletopOutline == null)
            {
                var outline = new GameObject("TabletopComparisonOutline");
                outline.transform.SetParent(transform, false);
                tabletopOutline = outline.AddComponent<LineRenderer>();
                tabletopOutline.sharedMaterial = probe.GetComponent<Renderer>().sharedMaterial;
                tabletopOutline.useWorldSpace = false;
                tabletopOutline.loop = true;
                tabletopOutline.positionCount = 4;
                tabletopOutline.startWidth = tabletopOutline.endWidth = .003f;
            }
            tabletopOutline.transform.SetPositionAndRotation(proposedReviewArea.position + Vector3.up * .002f, proposedReviewArea.rotation);
            var half = proposedReviewSize * .5f;
            tabletopOutline.SetPosition(0, new Vector3(-half.x, 0, -half.y));
            tabletopOutline.SetPosition(1, new Vector3(half.x, 0, -half.y));
            tabletopOutline.SetPosition(2, new Vector3(half.x, 0, half.y));
            tabletopOutline.SetPosition(3, new Vector3(-half.x, 0, half.y));
            tabletopOutline.gameObject.SetActive(true);
        }

        bool ConfirmTabletopReviewArea()
        {
            if (!TabletopPreviewActive) return true;
            if (tabletopProposesArea)
            {
                if (!StoreReviewTable(proposedReviewArea, proposedReviewSize, true, true)) return false;
            }
            else if (!TabletopSharesRegisteredSurface(tabletopContact))
            {
                if (!TryGetTabletopOriginalBounds(out var original)) return false;
                tabletopProposesArea = true;
                SetTabletopRotation(assemblyReference.rotation, original);
                return false; // The wearer has not yet seen the proposed outline.
            }
            if (tabletopOutline != null) tabletopOutline.gameObject.SetActive(false);
            return true;
        }

        void ClearTabletopAlignment()
        {
            ClearA4Alignment();
            tabletopPoseApplied = tabletopProposesArea = false;
            if (tabletopOutline != null) tabletopOutline.gameObject.SetActive(false);
        }
    }
}
