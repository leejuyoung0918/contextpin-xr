using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR;

namespace ContextPin.Editor
{
    public static class TraceSimulatorSmoke
    {
        [Serializable]
        sealed class CheckResult
        {
            public bool success, deviceActive, usesRigCamera, homeRayHit, homeAction;
            public int runningDisplays, renderPasses, renderViews;
            public string runtime = "", runtimeVersion = "", graphicsDevice = "", screen = "", error = "";
            public string expectedStep = "", missedQuote = "", recoveredRole = "", firstChoice = "";
            public bool firstCorrect, completed;
            public int choices, displayedEvidenceFrames;
            public bool guideApproved, guideAudioPlayed, guideCompleted, guideLit;
            public int guideWrongAttempts, guideHintUses;
            public List<string> rayActions = new List<string>();
        }

        static readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();
        static CheckResult result;
        static double deadline;
        static int lastFrame;
        static RelayApp app;
        static readonly Queue<(string screen, string control, Action verify)> steps = new Queue<(string, string, Action)>();

        public static void Begin()
        {
            result = new CheckResult { graphicsDevice = SystemInfo.graphicsDeviceType.ToString() };
            lastFrame = -1;
            steps.Clear();
            Schedule();
            deadline = EditorApplication.timeSinceStartup + 65;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            try
            {
                result.deviceActive = XRSettings.isDeviceActive;
                SubsystemManager.GetSubsystems(displays);
                result.runningDisplays = result.renderPasses = result.renderViews = 0;
                foreach (var display in displays)
                {
                    if (!display.running) continue;
                    result.runningDisplays++;
                    int passes = display.GetRenderPassCount();
                    result.renderPasses += passes;
                    for (int i = 0; i < passes; i++)
                    {
                        display.GetRenderPass(i, out var pass);
                        result.renderViews += pass.GetRenderParameterCount();
                    }
                }
                if (result.deviceActive || result.runningDisplays > 0)
                {
                    result.runtime = OpenXRRuntime.name ?? "";
                    result.runtimeVersion = OpenXRRuntime.version ?? "";
                }

                if (app == null) app = UnityEngine.Object.FindAnyObjectByType<RelayApp>();
                result.screen = app != null ? app.CurrentScreen : "";
                var rig = UnityEngine.Object.FindAnyObjectByType<OVRCameraRig>();
                var camera = Camera.main;
                result.usesRigCamera = rig != null && rig.centerEyeAnchor != null && camera != null &&
                    rig.centerEyeAnchor.GetComponent<Camera>() == camera && camera.isActiveAndEnabled;
                if (GameObject.Find("Desktop Preview Camera") != null)
                    throw new InvalidOperationException("The app created its desktop preview camera because it did not acquire an XR display at startup.");
                if (EditorApplication.timeSinceStartup > deadline)
                    throw new TimeoutException("Timed out waiting for the XR display, rig camera, or next ray-accessible action: " + result.expectedStep);
                if (!result.deviceActive || result.runningDisplays == 0 || result.renderViews < 2 || !result.usesRigCamera) return;
                if (result.runtime.IndexOf("Simulat", StringComparison.OrdinalIgnoreCase) < 0)
                    throw new InvalidOperationException("OpenXR reported an unexpected runtime: " + result.runtime);

                if (lastFrame == Time.frameCount) return;
                var step = steps.Peek();
                result.expectedStep = step.screen + " / " + (step.control ?? "verify result");
                if (result.screen != step.screen) return;
                if (step.control == null) { step.verify(); Finish(null); return; }
                var target = GameObject.Find(step.control)?.GetComponent<RelayHit>();
                if (target == null) return;
                Physics.SyncTransforms();
                if (!Physics.Raycast(camera.transform.position, target.Center - camera.transform.position, out var hit, 12) ||
                    hit.collider.GetComponent<RelayHit>() != target) return;
                step.verify?.Invoke();
                if (step.control == "HomeDemo") result.homeRayHit = true;
                if (step.control == "StartRecording") result.homeAction = true;
                lastFrame = Time.frameCount;
                hit.collider.GetComponent<RelayHit>().clicked.Invoke();
                result.rayActions.Add(result.expectedStep);
                steps.Dequeue();
            }
            catch (Exception ex) { Finish(ex); }
        }

        static void Schedule()
        {
            steps.Enqueue(("Home", "HomeDemo", null));
            steps.Enqueue(("Consent", "StartRecording", null));
            steps.Enqueue(("Recording", "Point-input", null));
            steps.Enqueue(("Recording", "NextDemoSpeech", null));
            steps.Enqueue(("Recording", "Point-amp", null));
            steps.Enqueue(("Recording", "NextDemoSpeech", null));
            steps.Enqueue(("Recording", "EndRecording", null));
            steps.Enqueue(("Review", "UseCandidate", () =>
            {
                var notes = app.TraceData.InOrder();
                Require(notes.Count == 3 && notes[0].pinchRole == "input" && notes[1].pinchRole == "amp" &&
                        string.IsNullOrEmpty(notes[2].pinchRole), "Expected three utterances and only two student point selections");
                var missed = notes[2];
                Require(!missed.reviewed && missed.candidateRole == "gnd" && missed.transcript.Contains("접지") &&
                    missed.framePaths.Distinct().Count() == 3, "Missing GND did not retain its pending candidate, original words and three different frames");
                result.missedQuote = missed.transcript;
            }));
            steps.Enqueue(("Review", "UseCandidate", () => Require(app.CurrentReviewIndex == 1, "Expected amplifier review")));
            steps.Enqueue(("Review", "ExpandEvidence", () =>
            {
                Require(app.CurrentReviewIndex == 2 && UIContains("OriginalPin", "핀치 없음") && UIContains("Candidate", "접지") &&
                        UIContains("OriginalSpeech", "접지"), "Student A is missing the original words or pending GND candidate on screen");
            }));
            steps.Enqueue(("Review", "NextFrame", () => CheckPhoto(0)));
            steps.Enqueue(("Review", "NextFrame", () => CheckPhoto(1)));
            steps.Enqueue(("Review", "CloseEvidence", () => CheckPhoto(2)));
            steps.Enqueue(("Review", "UseCandidate", null));
            steps.Enqueue(("Review", "BeginReplay", () =>
            {
                var confirmed = app.TraceData.ReplayNotes();
                Require(confirmed.Select(n => n.finalRole).SequenceEqual(new[] { "input", "amp", "gnd" }), "Shared points are missing or out of speech order");
                result.recoveredRole = confirmed[2].finalRole;
            }));
            steps.Enqueue(("Replay", "NextReplay", () => Require(UIContains("ReplayRole", "입력"), "Expected student B's first note at IN")));
            steps.Enqueue(("Replay", "NextReplay", () => Require(UIContains("ReplayRole", "증폭"), "Expected student B's second note at AMP")));
            steps.Enqueue(("Replay", "BeginQuiz", () => Require(UIContains("ReplayRole", "접지") && UIContains("ReplaySource", "접지"), "Recovered GND explanation missing from replay")));
            steps.Enqueue(("Quiz", "Point-input", null));
            steps.Enqueue(("Quiz", "Point-gnd", () =>
            {
                var attempt = app.TraceData.attempts.Last();
                Require(attempt.firstRole == "input" && !attempt.firstCorrect && !attempt.completed && attempt.choices == 1,
                    "Incorrect first choice was not preserved before the retry");
            }));
            steps.Enqueue(("Result", "ResultHome", () =>
            {
                var attempt = app.TraceData.attempts.Last();
                result.firstChoice = attempt.firstRole;
                result.firstCorrect = attempt.firstCorrect;
                result.completed = attempt.completed;
                result.choices = attempt.choices;
                Require(attempt.firstRole == "input" && !attempt.firstCorrect && attempt.answerRole == "gnd" &&
                    attempt.completed && attempt.choices == 2 && UIContains("FinalChoice", "완료"), "Expected completed retry with the first wrong choice preserved");
            }));
            ScheduleGuide();
        }

        static void ScheduleGuide()
        {
            steps.Enqueue(("Home", "HomeGuideExample", null));
            steps.Enqueue(("GuideReview", "GuideReviewStep-1", () =>
                Require(GameObject.Find("GuideStartSimulation") == null && GameObject.Find("GuideApprovalIssue") != null,
                    "Pending professor endpoint must withhold the student practice action")));
            steps.Enqueue(("GuideReview", "GuideEditTo", null));
            steps.Enqueue(("GuideReview", "GuideReviewPoint-a-1", null));
            steps.Enqueue(("GuideReview", "GuideApprove", null));
            steps.Enqueue(("GuideReview", "GuideStartSimulation", () =>
            {
                result.guideApproved = app.TraceData.guide.approved && app.TraceData.guide.steps[1].toPoint == "a-1";
                Require(result.guideApproved, "Teacher approval and the contact fix must precede guided practice");
            }));
            steps.Enqueue(("GuidePractice", "GuideConfirm", null));
            ScheduleWire("plus-1", "c-1");
            steps.Enqueue(("GuidePractice", "GuidePlayAudio", () =>
                Require(UIContains("GuideFeedback", "A행") && app.TraceData.guideRuns.Last().stepIndex == 1,
                    "The XR ray action on the wrong node must keep the resistor step in view")));
            steps.Enqueue(("GuidePractice", "GuideOpenEvidence", () =>
            {
                var clip = Resources.Load<AudioClip>("TraceGuideDemo/resistor");
                result.guideAudioPlayed = clip != null && app.GetComponents<AudioSource>().Any(source => source.clip == clip);
                Require(result.guideAudioPlayed, "The student must replay the saved explanation for the current step");
            }));
            steps.Enqueue(("GuidePractice", "GuideCloseEvidence", () =>
                Require(UIContains("GuideEvidenceSpeech", GuideRules.DemoSpoken("resistor")),
                    "The XR panel must display the exact original location phrase")));
            ScheduleWire("plus-1", "a-1");
            ScheduleWire("a-2", "b-1");
            ScheduleWire("b-2", "gnd-1");
            steps.Enqueue(("GuidePractice", "GuidePowerCheck", null));
            steps.Enqueue(("GuidePractice", "GuideFinish", null));
            steps.Enqueue(("GuideResult", null, () =>
            {
                var run = app.TraceData.guideRuns.Last();
                result.guideCompleted = run.completed;
                result.guideLit = run.lit;
                result.guideWrongAttempts = run.wrongAttempts;
                result.guideHintUses = run.hintUses;
                Require(run.completed && run.lit && run.wrongAttempts == 1 && run.hintUses == 2 &&
                    UIContains("GuideResultCompletion", "5 / 5"), "The XR simulator guide must report the recovered error and simulated lighting");
            }));
        }

        static void ScheduleWire(string from, string to)
        {
            steps.Enqueue(("GuidePractice", "GuidePracticePoint-" + from, null));
            steps.Enqueue(("GuidePractice", "GuidePracticePoint-" + to, null));
            steps.Enqueue(("GuidePractice", "GuideSubmit", null));
        }

        static void CheckPhoto(int index)
        {
            var frame = app.TraceData.InOrder()[2].framePaths[index];
            var image = GameObject.Find("Evidence image")?.GetComponent<RawImage>();
            Require(frame.StartsWith("res:") && image != null && image.texture != null &&
                image.texture == Resources.Load<Texture2D>(frame.Substring(4)), "Review is not displaying evidence frame " + (index + 1));
            result.displayedEvidenceFrames++;
        }

        static bool UIContains(string name, string value) => GameObject.Find(name)?.GetComponent<Text>()?.text.Contains(value) == true;

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        static void Finish(Exception failure)
        {
            EditorApplication.update -= Tick;
            result.success = failure == null;
            result.error = failure?.Message ?? "";
            string json = JsonUtility.ToJson(result, true);
            var report = Path.Combine(Environment.GetEnvironmentVariable("RELAY_WORKSPACE"), "artifacts", "simulator-smoke-report.json");
            File.WriteAllText(report, json);
            if (result.success) Debug.Log(json); else Debug.LogError(json);
            // Let Unity close its XR session while the display and input providers still exist.
            SessionState.SetInt("TraceSimulatorExitCode", result.success ? 0 : 2);
            SessionState.SetBool("TraceSimulatorExit", true);
            EditorApplication.isPlaying = false;
            // With domain reload disabled, stopping Play can return directly to edit mode.
            // The EnteredEditMode hook covers the asynchronous/domain-reload case.
            if (!EditorApplication.isPlayingOrWillChangePlaymode) EditorApplication.Exit(result.success ? 0 : 2);
        }
    }
}
