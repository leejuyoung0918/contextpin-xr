using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.XR;

namespace ContextPin.Editor
{
    public static partial class MeetingSmoke
    {
        // Inject Input System control state on the simulator's Touch layouts. Head and scene
        // tracking keep running. Right aim targets CAD while the active left UI ray misses it.
        static bool WithTestHands(Func<XRController, XRController, bool> exercise)
        {
            var left = XRController.leftHand;
            var right = XRController.rightHand;
            bool addLeft = left == null, addRight = right == null;
            const string layout = "ContextPinSmokeTouch";
            if (addLeft || addRight)
                InputSystem.RegisterLayout(@"{
                    ""name"": ""ContextPinSmokeTouch"", ""extend"": ""XRController"", ""controls"": [
                      {""name"":""pointer"", ""layout"":""Pose"", ""offset"":64},
                      {""name"":""primaryButton"", ""layout"":""Button"", ""format"":""FLT"", ""offset"":128},
                      {""name"":""triggerPressed"", ""layout"":""Button"", ""format"":""FLT"", ""offset"":132},
                      {""name"":""gripPressed"", ""layout"":""Button"", ""format"":""FLT"", ""offset"":136},
                      {""name"":""grip"", ""layout"":""Axis"", ""format"":""FLT"", ""offset"":140},
                      {""name"":""secondaryButton"", ""layout"":""Button"", ""format"":""FLT"", ""offset"":144}
                    ]}");
            try
            {
                XRController AddHand(string usage)
                {
                    var device = (XRController)InputSystem.AddDevice(layout);
                    InputSystem.SetDeviceUsage(device, usage);
                    return device;
                }
                if (addLeft) left = AddHand("LeftHand");
                if (addRight) right = AddHand("RightHand");
                return exercise(left, right);
            }
            finally
            {
                if (addLeft && left != null) InputSystem.RemoveDevice(left);
                if (addRight && right != null) InputSystem.RemoveDevice(right);
                if (addLeft || addRight) InputSystem.RemoveLayout(layout);
            }
        }

        static bool PressRightWithLeftActive(RelayApp app, Ray target, bool samplePaper = false, bool selectCandidate = false)
            => WithTestHands((left, right) => ExerciseRightA(app, target, left, right, samplePaper, selectCandidate));

        static bool ExerciseRightA(RelayApp app, Ray target, XRController left, XRController right, bool samplePaper, bool selectCandidate)
        {
            var tracking = ((OVRCameraRig)AppType.GetField("xrRig", AppMembers).GetValue(app)).trackingSpace;
            var aimLeft = new Ray(target.origin - Camera.main.transform.right * .15f, -target.direction);
            using var oldLeft = StateEvent.From(left, out var restoreLeft);
            using var oldRight = StateEvent.From(right, out var restoreRight);
            try
            {
                // ButtonControl records edges during input updates. Establish a release in one
                // input frame, then enqueue both devices' presses for the following frame.
                SetControllerState(left, tracking, aimLeft, false, false);
                SetControllerState(right, tracking, target, false, false);
                PumpPlayerInput();
                var primary = right.GetChildControl<ButtonControl>("primaryButton");
                _ = primary.wasPressedThisFrame;
                var placement = AppType.GetMethod("ReadTabletopPlacementButton", AppMembers);
                var pendingBeforeTouch = AppType.GetField("explicitRequest", AppMembers).GetValue(app);
                if (samplePaper)
                {
                    placement.Invoke(app, new object[] { false });
                    // Keep the tracked grip steady for the production 150 ms capture window.
                    // Native simulator pose updates resume as soon as this fixture releases the devices.
                    System.Threading.Thread.Sleep(180);
                    placement.Invoke(app, new object[] { false });
                }
                SetControllerState(left, tracking, aimLeft, true, false);
                SetControllerState(right, tracking, target, false, true);
                PumpPlayerInput();
                AppType.GetField("leftTriggerWasDown", AppMembers).SetValue(app, false);
                object[] ui = { false, false };
                var pointing = (Ray)AppType.GetMethod("ReadUiPointer", AppMembers).Invoke(app, ui);
                Require((bool)ui[0] && (bool)ui[1] && Vector3.Dot(pointing.direction, aimLeft.direction) > .9999f,
                    "The left trigger must actually own the UI pointer during right A.");
                Require(primary.wasPressedThisFrame,
                    $"Simulator must produce the right A down edge (value={primary.ReadValue()}, frame={InputState.updateCount}, deviceUpdated={right.wasUpdatedThisFrame}).");
                if (samplePaper)
                {
                    placement.Invoke(app, new object[] { false });
                    int cornersAfterDown = PaperCornerCount(app);
                    placement.Invoke(app, new object[] { false });
                    var moved = new Ray(target.origin + Camera.main.transform.right * .210f, target.direction);
                    SetControllerState(right, tracking, moved, false, true);
                    PumpPlayerInput();
                    placement.Invoke(app, new object[] { false });
                    Require(PaperCornerCount(app) == cornersAfterDown,
                        "Holding A over multiple input frames must commit at most one paper corner.");
                    Require(AppType.GetField("explicitRequest", AppMembers).GetValue(app) == pendingBeforeTouch,
                        "The paper placement press must not open a design request or take a photo.");
                    return true;
                }
                var input = AppType.GetMethod("ReadExplicitTargetButton", AppMembers);
                var alignment = AppType.GetField("assemblyAlignment", AppMembers);
                var confirmed = alignment.GetValue(app);
                try
                {
                    alignment.SetValue(app, Enum.Parse(alignment.FieldType, "CaptureOrigin"));
                    Require(!(bool)input.Invoke(app, new object[] { false }), "Manual alignment must consume A exclusively.");
                }
                finally { alignment.SetValue(app, confirmed); }
                var handled = (bool)input.Invoke(app, new object[] { false });
                if (selectCandidate) return handled;
                var pending = AppType.GetField("explicitRequest", AppMembers).GetValue(app);
                bool accepted = pending?.GetType().GetField("Selection").GetValue(pending) != null;
                if (checkCaptureOnly && accepted)
                {
                    var away = new Ray(target.origin + Camera.main.transform.right * .28f, -target.direction);
                    SetControllerState(right, tracking, away, false, false);
                    PumpPlayerInput();
                    input.Invoke(app, new object[] { false });
                }
                return accepted;
            }
            finally
            {
                InputState.Change(left, restoreLeft);
                InputState.Change(right, restoreRight);
            }
        }

        // EditorApplication.update can have Editor focus where the Input System deliberately
        // defers XR events to the next player update. Pump that update for this input fixture.
        static void PumpPlayerInput()
        {
            var update = typeof(InputSystem).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(InputUpdateType) }, null);
            update.Invoke(null, new object[] { InputUpdateType.Dynamic });
        }

        static void SetControllerState(XRController device, Transform tracking, Ray world,
            bool trigger, bool primary, bool grip = false)
        {
            using var buffer = StateEvent.From(device, out var data);
            var position = tracking.InverseTransformPoint(world.origin);
            var rotation = Quaternion.Inverse(tracking.rotation) * Quaternion.LookRotation(world.direction);
            device.GetChildControl<IntegerControl>("trackingState").WriteValueIntoEvent(3, data);
            device.GetChildControl<ButtonControl>("isTracked").WriteValueIntoEvent(1f, data);
            device.GetChildControl<Vector3Control>("devicePosition").WriteValueIntoEvent(position, data);
            device.GetChildControl<QuaternionControl>("deviceRotation").WriteValueIntoEvent(rotation, data);
            device.GetChildControl<IntegerControl>("pointer/trackingState").WriteValueIntoEvent(3, data);
            device.GetChildControl<Vector3Control>("pointer/position").WriteValueIntoEvent(position, data);
            device.GetChildControl<QuaternionControl>("pointer/rotation").WriteValueIntoEvent(rotation, data);
            device.GetChildControl<ButtonControl>("primaryButton").WriteValueIntoEvent(primary ? 1f : 0f, data);
            device.GetChildControl<ButtonControl>("triggerPressed").WriteValueIntoEvent(trigger ? 1f : 0f, data);
            device.TryGetChildControl<AxisControl>("grip")?.WriteValueIntoEvent(grip ? 1f : 0f, data);
            device.TryGetChildControl<ButtonControl>("gripPressed")?.WriteValueIntoEvent(grip ? 1f : 0f, data);
            InputSystem.QueueEvent(data);
        }
    }
}
