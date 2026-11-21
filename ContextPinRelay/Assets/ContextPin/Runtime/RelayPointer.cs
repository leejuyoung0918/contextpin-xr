using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        bool leftTriggerWasDown, rightTriggerWasDown, preferLeftController;
        int lastControllerTracking = -1;
        LineRenderer controllerBeam;
        Material controllerBeamMaterial;

        Ray ReadUiPointer(out bool clicked, out bool controllerPointer)
        {
            controllerPointer = false;
            if (!xr)
            {
                bool mouseDown = Mouse.current?.leftButton.isPressed == true;
                clicked = mouseDown && !wasPressed;
                wasPressed = mouseDown;
                return view.ScreenPointToRay(Mouse.current?.position.ReadValue() ??
                    new Vector2(Screen.width / 2, Screen.height / 2));
            }

            bool leftTracked = ReadController(XRController.leftHand, OVRInput.Controller.LTouch,
                OVRInput.RawButton.LIndexTrigger, out var leftRay, out bool leftDown);
            bool rightTracked = ReadController(XRController.rightHand, OVRInput.Controller.RTouch,
                OVRInput.RawButton.RIndexTrigger, out var rightRay, out bool rightDown);
            bool leftClicked = leftTracked && leftDown && !leftTriggerWasDown;
            bool rightClicked = rightTracked && rightDown && !rightTriggerWasDown;
            leftTriggerWasDown = leftDown;
            rightTriggerWasDown = rightDown;

            var active = OVRInput.GetActiveController();
            if (leftClicked != rightClicked) preferLeftController = leftClicked;
            else if (active == OVRInput.Controller.LTouch) preferLeftController = true;
            else if (active == OVRInput.Controller.RTouch) preferLeftController = false;

            int tracking = (leftTracked ? 1 : 0) | (rightTracked ? 2 : 0);
            if (Application.platform == RuntimePlatform.Android && lastControllerTracking != tracking)
            {
                lastControllerTracking = tracking;
                Debug.Log($"ContextPin input: left tracked={leftTracked}, right tracked={rightTracked}");
            }

            bool pinching = hands.Any(Pinching);
            bool pinchClicked = pinching && !wasPressed;
            wasPressed = pinching;
            bool handsActive = pinching || (active & OVRInput.Controller.Hands) != 0;
            if (leftClicked || rightClicked || (!handsActive && tracking != 0))
            {
                controllerPointer = true;
                bool useLeft = leftTracked && (!rightTracked || preferLeftController);
                clicked = useLeft ? leftClicked : rightClicked;
                return useLeft ? leftRay : rightRay;
            }
            clicked = pinchClicked;
            return new Ray(view.transform.position, view.transform.forward);
        }

        bool ReadController(XRController device, OVRInput.Controller nativeController, OVRInput.RawButton nativeTrigger,
            out Ray ray, out bool pressed)
        {
            ray = default;
            var trigger = device?.TryGetChildControl<AxisControl>("trigger");
            var button = device?.TryGetChildControl<ButtonControl>("triggerPressed");
            pressed = (button != null ? button.isPressed : trigger?.ReadValue() >= .55f) ||
                OVRInput.Get(nativeTrigger, nativeController);

            if (TryReadTrackedAim(device, out ray)) return true;
            var trackingSpace = xrRig.trackingSpace;
            if (Application.platform != RuntimePlatform.Android || !OVRInput.GetControllerPositionTracked(nativeController) ||
                !OVRInput.GetControllerOrientationTracked(nativeController)) return false;
            ray = new Ray(trackingSpace.TransformPoint(OVRInput.GetLocalControllerPosition(nativeController)),
                trackingSpace.TransformDirection(OVRInput.GetLocalControllerRotation(nativeController) * Vector3.forward));
            return true;
        }

        // A design selection is tied to the right aim pose. Never substitute the
        // active UI ray, the headset, or a controller grip pose here.
        bool TryReadTrackedAim(XRController device, out Ray ray)
        {
            ray = default;
            var state = device?.TryGetChildControl<IntegerControl>("pointer/trackingState");
            const int required = (int)(InputTrackingState.Position | InputTrackingState.Rotation);
            if (state == null || (state.ReadValue() & required) != required || xrRig == null) return false;
            var position = device.TryGetChildControl<Vector3Control>("pointer/position");
            var rotation = device.TryGetChildControl<QuaternionControl>("pointer/rotation");
            if (position == null || rotation == null) return false;
            var q = rotation.ReadValue();
            var p = position.ReadValue();
            float norm = Quaternion.Dot(q, q);
            if (!float.IsFinite(p.sqrMagnitude) || !float.IsFinite(norm) || Mathf.Abs(norm - 1f) > .02f) return false;
            var space = xrRig.trackingSpace;
            ray = new Ray(space.TransformPoint(p), space.TransformDirection(q * Vector3.forward));
            return true;
        }

        bool tabletopTipTracked;
        bool tabletopAHeld, a4TipStable;
        int a4SampledCorner = -1;
        Vector3 a4RestPoint;
        double a4StillSince, a4LastSampleAt;

        void ResetA4ProbeStability()
        {
            a4SampledCorner = -1;
            a4TipStable = false;
        }

        bool UpdateA4ProbeStability(Vector3 point)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (a4SampledCorner != A4CornerCount || now - a4LastSampleAt > .35 ||
                (point - a4RestPoint).sqrMagnitude > .008f * .008f)
            {
                a4SampledCorner = A4CornerCount;
                a4RestPoint = point;
                a4StillSince = now;
            }
            a4LastSampleAt = now;
            return now - a4StillSince >= .15;
        }

        static bool RightAWasPressed(XRController right) =>
            right?.TryGetChildControl<ButtonControl>("primaryButton")?.wasPressedThisFrame == true ||
            OVRInput.GetDown(OVRInput.RawButton.A, OVRInput.Controller.RTouch);

        void ReadTabletopPlacementButton(bool panelOwnsPointer)
        {
            var right = XRController.rightHand;
            bool edge = RightAWasPressed(right) && !tabletopAHeld;
            tabletopAHeld = right?.TryGetChildControl<ButtonControl>("primaryButton")?.isPressed == true ||
                OVRInput.Get(OVRInput.RawButton.A, OVRInput.Controller.RTouch);
            bool paper = A4PointSampling;
            if ((!paper && !TabletopPointSampling) || ReviewTableSampling || AssemblyQrScanning || choosingAlignmentMethod ||
                movingMeetingPanel || confirmingMeetingEnd || assemblyMenu)
            {
                probe.gameObject.SetActive(false);
                ResetA4ProbeStability();
                return;
            }
            bool tracked = TryTabletopProbe(out var point);
            probe.gameObject.SetActive(tracked);
            if (tracked)
            {
                probe.position = point;
                probe.localScale = Vector3.one * (paper ? .005f : .009f);
            }
            bool stable = tracked && paper && UpdateA4ProbeStability(point);
            if (!tracked || !paper) a4SampledCorner = -1;
            if (tabletopTipTracked != tracked || a4TipStable != stable)
            {
                tabletopTipTracked = tracked;
                a4TipStable = stable;
                Draw();
            }
            if (panelOwnsPointer || !tracked || !edge) return;
            if (paper)
            {
                if (stable) TryConfirmA4Corner();
            }
            else TryConfirmTabletopPoint();
        }

        bool ReadExplicitTargetButton(bool panelOwnsPointer)
        {
            var right = XRController.rightHand;
            if (BundleOnPaper && !CapturingPaperEdit)
            {
                if (!panelOwnsPointer && TryReadTrackedAim(right, out var candidateRay))
                {
                    if (HandleBundleCandidateRay(candidateRay, RightAWasPressed(right))) return true;
                }
                else SetBundleHover(null);
            }
            if (SharedPaperMeeting && !CapturingPaperEdit && !panelOwnsPointer && RightAWasPressed(right) &&
                TryReadTrackedAim(right, out var paperRay) && SharePaperRay(paperRay)) return true;
            if (!ExplicitDesignReady || OtherDeviceRecordsDesign || panelOwnsPointer || AssemblySampling || AssemblyQrScanning ||
                movingMeetingPanel || explicitRequest?.Conflict == true) return false;
            if (CaptureBundleMeeting && (explicitRequest?.Speech != DesignSpeech.Final ||
                explicitRequest.BundleSaved || explicitRequest.Selection != null)) return false;
            bool pressed = RightAWasPressed(right);
            if (!TryReadTrackedAim(right, out var aim))
            {
                if (pressed) ShowExplicitHint("오른쪽 컨트롤러의 조준 위치가 추적된 뒤 A를 눌러 주세요.");
                return false;
            }
            if (pressed) HandleExplicitTargetPress(aim, panelOwnsPointer);
            // The ordinary UI beam may still be driven by the left controller.
            if (!Physics.Raycast(aim, out var hit, 12) || !hit.collider.TryGetComponent<RelayHit>(out var part) ||
                string.IsNullOrEmpty(part.meetingVersion) || string.IsNullOrEmpty(part.role) ||
                CaptureBundleMeeting && (part.meetingVersion != explicitRequest.BaseId ||
                    ModelRootForHit(part.transform) != meetingModel.transform)) return false;
            probe.position = hit.point - aim.direction * .0015f;
            probe.localScale = Vector3.one * .003f;
            probe.gameObject.SetActive(true);
            return true;
        }

        void ShowControllerBeam(bool visible, Vector3 origin, Vector3 end, bool hittingButton)
        {
            if (!visible)
            {
                if (controllerBeam != null) controllerBeam.enabled = false;
                return;
            }
            if (controllerBeam == null)
            {
                var pointer = new GameObject("Controller pointer");
                pointer.transform.SetParent(transform, false);
                controllerBeam = pointer.AddComponent<LineRenderer>();
                var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
                if (shader != null)
                {
                    controllerBeamMaterial = new Material(shader);
                    controllerBeam.sharedMaterial = controllerBeamMaterial;
                }
                controllerBeam.positionCount = 2;
                controllerBeam.startWidth = .002f;
                controllerBeam.endWidth = .003f;
                controllerBeam.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                controllerBeam.receiveShadows = false;
            }
            Color color = hittingButton ? Lime : new Color(.58f, .86f, .74f, .42f);
            controllerBeam.startColor = color;
            controllerBeam.endColor = color;
            controllerBeam.SetPosition(0, origin);
            controllerBeam.SetPosition(1, end);
            controllerBeam.enabled = true;
        }

        void ReleaseControllerBeam()
        {
            if (controllerBeamMaterial != null) Destroy(controllerBeamMaterial);
        }
    }
}
