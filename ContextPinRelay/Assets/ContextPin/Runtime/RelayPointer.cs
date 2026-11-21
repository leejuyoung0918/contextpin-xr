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

            // Unity OpenXR exposes the aim pose separately from the grip pose.
            // The aim also follows the simulator's Point & click input.
            var state = device?.TryGetChildControl<IntegerControl>("pointer/trackingState");
            const int required = (int)(InputTrackingState.Position | InputTrackingState.Rotation);
            var trackingSpace = xrRig.trackingSpace;
            if ((trigger != null || button != null) && state != null && (state.ReadValue() & required) == required)
            {
                var position = device.TryGetChildControl<Vector3Control>("pointer/position");
                var rotation = device.TryGetChildControl<QuaternionControl>("pointer/rotation");
                if (position != null && rotation != null)
                {
                    ray = new Ray(trackingSpace.TransformPoint(position.ReadValue()),
                        trackingSpace.TransformDirection(rotation.ReadValue() * Vector3.forward));
                    return true;
                }
            }

            if (Application.platform != RuntimePlatform.Android || !OVRInput.GetControllerPositionTracked(nativeController) ||
                !OVRInput.GetControllerOrientationTracked(nativeController)) return false;
            ray = new Ray(trackingSpace.TransformPoint(OVRInput.GetLocalControllerPosition(nativeController)),
                trackingSpace.TransformDirection(OVRInput.GetLocalControllerRotation(nativeController) * Vector3.forward));
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
