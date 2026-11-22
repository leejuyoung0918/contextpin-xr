using System;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XR;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        bool editBHeld;
        bool HoldCommandMeeting => CaptureBundleMeeting && OffsetPaperGallery &&
            meetingSnapshot?["edit_command_limits"] is JObject;

        void ReadEditCommandButton()
        {
            if (!HoldCommandMeeting) { editBHeld = false; return; }
            var right = XRController.rightHand;
            bool held = right?.TryGetChildControl<ButtonControl>("secondaryButton")?.isPressed == true ||
                OVRInput.Get(OVRInput.RawButton.B, OVRInput.Controller.RTouch);
            bool pressed = held && !editBHeld;
            editBHeld = held;
            var p = explicitRequest;
            if (p?.ButtonWindow == true && !p.WindowClosed)
            {
                int position = p.RemoteWindow || assemblyAudio == null || !SpatialMicRecording() ? -1 : SpatialMicPosition();
                bool atLimit = p.RemoteWindow
                    ? (Time.realtimeSinceStartupAsDouble - p.StartedAt) * 1000 >= p.MaxWindowMs
                    : assemblyAudio != null && assemblyAudio.AtSampleLimit(position, p.EndLimitSample);
                if (!held || atLimit || p.Conflict || p.Speech == DesignSpeech.Failed || !ExplicitDesignReady ||
                    (!p.RemoteWindow && position < 0)) EndHeldCommand(p);
                return;
            }
            if (!pressed || !ExplicitDesignReady || latestGrab != null || movingMeetingPanel || assemblyMenu ||
                assemblyHistory || confirmingMeetingEnd || assemblyStarting || bundleCommitPending || bundleCommitTarget != null ||
                string.IsNullOrEmpty(meetingParticipant) || TextValue(CurrentCandidateSet, "status") is ("ready" or "generating" or "building") ||
                (p != null && !p.BundleSaved && !p.Conflict && p.Speech != DesignSpeech.Failed)) return;
            if (IntegratedPaperControls && (CurrentPaperInteraction != PaperInteraction.LatestDisplayed ||
                paperLatestPose is not Pose confirmed || !VisibleAtPaperPose(confirmed))) return;
            bool remote = OtherDeviceRecordsDesign;
            if (remote && (!meetingClockValid || !meetingEventsConnected))
            {
                ShowExplicitHint("회의 마이크와 시간을 동기화한 뒤 B를 다시 누르세요.");
                return;
            }
            var limits = meetingSnapshot["edit_command_limits"];
            explicitRequest = p = new PendingDesignRequest {
                BaseId = AssemblyAdoptedId, Revision = meetingSnapshot.Value<long>("adopted_revision"),
                StartedAt = Time.realtimeSinceStartupAsDouble, ButtonWindow = true, RemoteWindow = remote,
                MaxWindowMs = limits.Value<int>("max_ms"), ClockOffset = meetingClockOffsetMs,
                ClockUncertainty = meetingClockUncertaintyMs, Speech = DesignSpeech.Marking
            };
            if (IntegratedPaperControls)
            {
                p.FrozenPose = paperLatestPose;
                p.PaperFrame = PaperFrameRevision;
                p.PoseRevision = LatestPoseRevision;
            }
            p.RangeId = p.Id;
            p.RangeMarkedAt = p.StartedAt;
            if (remote)
                SendRemoteBoundary(p, true, RemoteBoundary(p, true, p.StartedAt));
            else if (TryMarkDesignSpeech(p.Id, true, out var error))
                p.EndLimitSample = assemblyAudio.LastMarkerSample + (long)assemblyAudio.SampleRate * p.MaxWindowMs / 1000;
            else { explicitRequest = null; ShowExplicitHint(error); return; }
            ShowExplicitHint(null);
        }

        static JObject RemoteBoundary(PendingDesignRequest p, bool start, double at)
        {
            var stamp = new JObject {
                ["range_id"] = p.Id, ["edge"] = start ? "start" : "end", ["source_monotonic_ms"] = at * 1000,
                ["clock_offset_ms"] = p.ClockOffset, ["clock_uncertainty_ms"] = p.ClockUncertainty
            };
            if (start) stamp["base_version_id"] = p.BaseId;
            if (start && p.FrozenPose.HasValue)
            {
                stamp["paper_frame_revision"] = p.PaperFrame;
                stamp["expected_pose_revision"] = p.PoseRevision;
            }
            return stamp;
        }

        void EndOpenHeldCommand()
        {
            if (explicitRequest?.ButtonWindow == true && !explicitRequest.WindowClosed)
                EndHeldCommand(explicitRequest);
        }

        void EndHeldCommand(PendingDesignRequest p)
        {
            if (p.WindowClosed) return;
            p.WindowClosed = true;
            if (p.Speech == DesignSpeech.Marking) p.Speech = DesignSpeech.Transcribing;
            p.RangeMarkedAt = Time.realtimeSinceStartupAsDouble;
            p.ObservedSnapshot = null;
            if (p.RemoteWindow)
            {
                // Freeze the release even if the start is still crossing the network.
                double end = Math.Min(p.RangeMarkedAt, p.StartedAt + p.MaxWindowMs / 1000.0);
                p.RemoteEnd = RemoteBoundary(p, false, end);
                if (p.RemoteStartAccepted) SendRemoteBoundary(p, false, p.RemoteEnd);
            }
            else if (!TryMarkDesignSpeech(p.Id, false, out var error, p.EndLimitSample))
            {
                p.Speech = DesignSpeech.Failed;
                p.SpeechError = error;
            }
            ShowExplicitHint(null);
        }

        void SendRemoteBoundary(PendingDesignRequest p, bool start, JObject frozen)
        {
            StartCoroutine(RunAssemblyAction(meetingTransport, "design_speech_boundary", frozen, (_, error) =>
            {
                if (error != null)
                {
                    if (explicitRequest == p) { p.Speech = DesignSpeech.Failed; p.SpeechError = error; }
                    return;
                }
                if (!start) return;
                p.RemoteStartAccepted = true;
                if (p.RemoteEnd != null) SendRemoteBoundary(p, false, p.RemoteEnd);
            }, preserveClock: true));
        }

        void CancelHeldCommand()
        {
            var p = explicitRequest;
            if (p?.ButtonWindow != true || p.Speech != DesignSpeech.Final || p.Selection != null || p.Sending) return;
            p.Sending = true;
            Draw();
            StartCoroutine(RunAssemblyAction(meetingTransport, "cancel_design_speech", new JObject { ["range_id"] = p.Id },
                (_, error) => {
                    if (explicitRequest != p) return;
                    p.Sending = false;
                    if (error == null) explicitRequest = null;
                    ShowExplicitHint(error ?? "수정 요청을 취소했습니다. B를 누른 채 다음 요청을 말하세요.");
                }, preserveClock: true));
        }
    }
}
