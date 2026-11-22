using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.XR;

namespace ContextPin.Editor
{
    public static partial class MeetingSmoke
    {
        const string HoldTestLayout = "ContextPinSmokeRightB";
        static XRController bController;
        static bool addedBController, bHeld;
        static int downBefore, downAfter, upBefore, upAfter;
        static string shortWindow, cancellationWindow;
        static double cancelHoldStart;

        // Preserve the hardware level across dynamic/before-render updates for the entire
        // hold. The player reads its usual secondaryButton and the same live audio cursor.
        static void KeepTestBDown()
        {
            if (!bHeld || bController == null) return;
            var button = bController.GetChildControl<ButtonControl>("secondaryButton");
            if (button.isPressed) return;
            // Some Touch layouts store face buttons as a single bit in the device state.
            using var buffer = StateEvent.From(bController, out var data);
            button.WriteValueIntoEvent(1f, data);
            InputState.Change(bController, data);
        }

        static void EndTestBInput()
        {
            InputSystem.onAfterUpdate -= KeepTestBDown;
            bHeld = false;
            if (addedBController && bController != null)
            {
                InputSystem.RemoveDevice(bController);
                InputSystem.RemoveLayout(HoldTestLayout);
            }
            bController = null;
            addedBController = false;
        }

        static void SetTestB(RelayApp app, bool down)
        {
            if (down)
            {
                Require(!bHeld, "Duplicate B-down in the input fixture.");
                bController = XRController.rightHand;
                if (bController == null)
                {
                    InputSystem.RegisterLayout(@"{""name"":""ContextPinSmokeRightB"", ""extend"":""XRController"",
                        ""controls"":[{""name"":""secondaryButton"",""layout"":""Button"",""format"":""FLT"",""offset"":144}]}");
                    bController = (XRController)InputSystem.AddDevice(HoldTestLayout);
                    InputSystem.SetDeviceUsage(bController, "RightHand");
                    addedBController = true;
                }
                InputSystem.onAfterUpdate += KeepTestBDown;
            }
            var button = bController.GetChildControl<ButtonControl>("secondaryButton");
            bHeld = down;
            using var buffer = StateEvent.From(bController, out var data);
            button.WriteValueIntoEvent(down ? 1f : 0f, data);
            InputSystem.QueueEvent(data);
            PumpPlayerInput();
            Require(button.isPressed == down && XRController.rightHand == bController, "Right B level did not reach the active XR controller.");
            int before = PcmCursor();
            AppType.GetMethod("ReadEditCommandButton", AppMembers).Invoke(app, null);
            int after = PcmCursor();
            if (down) { downBefore = before; downAfter = after; }
            else { upBefore = before; upAfter = after; EndTestBInput(); }
        }

        static string PendingRangeId(RelayApp app)
        {
            var pending = AppType.GetField("explicitRequest", AppMembers).GetValue(app);
            return (string)pending?.GetType().GetField("RangeId")?.GetValue(pending);
        }

        static bool MarkCommand(RelayApp app, bool start)
        {
            if (!checkPhoneStand) return Click("ExplicitSpeechAction");
            SetTestB(app, start);
            string status = ExplicitUi("ExplicitDesignStatus");
            Require(status.Contains(start ? "듣는 중" : "확인하는 중"), "Missing B command status after edge: " + status);
            report.bDownUp = true;
            return true;
        }

        static bool CheckShortB(RelayApp app, JObject state)
        {
            if (report.shortBRejected) return true;
            if (shortWindow == null)
            {
                SetTestB(app, true);
                shortWindow = PendingRangeId(app);
                Require(!string.IsNullOrEmpty(shortWindow), "B-down must reserve an edit request ID.");
                SetTestB(app, false);
                return false;
            }
            var range = state["design_speech_ranges"]?[shortWindow];
            if ((string)range?["status"] is null or "recording" or "transcribing") return false;
            if (!ExplicitUi("ExplicitDesignStatus").Contains("누른 채")) return false;
            Require((string)range["status"] == "too_short" && state["edit_requests"]?[shortWindow] == null &&
                state["candidate_sets"]?[shortWindow] == null && (string)state["meeting_audio_status"] == "recording", "A short B click must explain how to hold, without starting CAD.");
            report.shortBRejected = true;
            return true;
        }

        static bool CheckTimeoutAndCancellation(RelayApp app, JObject state)
        {
            if (report.cancelledBeforeTarget) return true;
            if (cancellationWindow == null)
            {
                MarkCommand(app, true);
                cancellationWindow = PendingRangeId(app);
                Require(!string.IsNullOrEmpty(cancellationWindow), "Expected a new maximum-length window.");
                Require(inputRing.SetData(speechSamples, PcmCursor() + PcmRate / 8), "Could not enqueue the timeout test speech.");
                cancelHoldStart = Time.realtimeSinceStartupAsDouble;
                return false;
            }
            var window = state["design_speech_ranges"]?[cancellationWindow];
            if (window == null || (string)window["status"] == "recording") return false;
            if (bHeld)
            {
                long duration = (long)window["end_sample"] - (long)window["start_sample"];
                long max = (long)state["edit_command_limits"]["max_ms"] * PcmRate / 1000;
                Require(duration == max && (Time.realtimeSinceStartupAsDouble - cancelHoldStart) * PcmRate >= max,
                    "A held B must close at the configured audio sample limit, before physical release.");
                SetTestB(app, false);
                report.longBAutoClosed = true;
            }
            if ((string)window["status"] == "cancelled")
            {
                if (PendingRangeId(app) != null) return false;
                Require(state["edit_requests"]?[cancellationWindow] == null && state["candidate_sets"]?[cancellationWindow] == null &&
                    state["adopted_version_id"].Value<string>() == report.initialVersion && report.microphoneOpenCount == 1,
                    "Cancelling a finalized range must preserve Latest and the meeting recording.");
                report.cancelledBeforeTarget = true;
                return true;
            }
            if ((string)window["status"] == "final") Click("ExplicitCancelSpeech");
            return false;
        }

        static void CheckRecordedButtonEdges(JObject state, JObject bundle, string directory)
        {
            var range = state["design_speech_ranges"][captureId];
            var command = bundle["command"];
            var start = (long)command["command_start_sample_index"];
            var end = (long)command["command_end_sample_index"];
            Require(start >= downBefore && start <= downAfter && end >= upBefore && end <= upAfter &&
                (long)range["start_sample"] == start && (long)range["end_sample"] == end &&
                Math.Abs((long)command["command_end_session_ms"] - (long)command["command_start_session_ms"] -
                         (end - start) * 1000.0 / PcmRate) <= 1.1 &&
                (new FileInfo(Path.Combine(directory, "command.wav")).Length - 44) / 2 == end - start,
                $"B interval must match both observed mic cursors and the exact saved WAV; start {start} in [{downBefore},{downAfter}], end {end} in [{upBefore},{upAfter}].");
            var app = UnityEngine.Object.FindAnyObjectByType<RelayApp>();
            var participant = (string)AppType.GetField("meetingParticipant", AppMembers).GetValue(app);
            Require((string)range["command_requested_by"] == participant && (string)bundle["command_requested_by"] == participant &&
                (string)command["command_requested_by"] == participant && command["speaker_id"]?.Type == JTokenType.Null &&
                (string)command["command_text"] == captureTranscript &&
                (string)command["command_text_source"] is ("linked_meeting_utterances" or "command_window_stt"),
                "The stored request must identify its authenticated requester, transcript provenance and unknown speaker.");
            Require(!captureTranscript.Contains("기다") && !captureTranscript.Contains("마칠") && !captureTranscript.Contains("괜찮"),
                "Ordinary discussion leaked into the B command.");
            report.bSampleExact = report.bRequesterVerified = true;
        }
    }
}
