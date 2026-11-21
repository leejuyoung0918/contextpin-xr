using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ContextPin.Editor
{
    // Drive the same scene controls used by mouse, pinch and trigger. Editor updates
    // survive play-mode domain reload and can report a failure even when the UI never loads.
    public static class RelaySmokeDriver
    {
        static readonly List<string> passed = new List<string>();
        static readonly Queue<Action> steps = new Queue<Action>();
        static string artifacts;
        static RelayApp app;
        static int reloadingAppId;
        static bool scheduled;
        static double deadline;
        static Func<bool> awaitAudio;
        static double audioDeadline;
        static AudioSource fixturePlayer;

        public static void Begin()
        {
            artifacts = Path.Combine(Environment.GetEnvironmentVariable("RELAY_WORKSPACE"), "artifacts");
            deadline = EditorApplication.timeSinceStartup + 75;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            try
            {
                if (EditorApplication.timeSinceStartup > deadline) throw new TimeoutException("Scene did not become ready");
                if (app == null)
                {
                    if (GameObject.Find("HomeDemo") == null) return;
                    var nextApp = UnityEngine.Object.FindAnyObjectByType<RelayApp>();
                    if (nextApp == null || nextApp.GetInstanceID() == reloadingAppId) return;
                    app = nextApp;
                    if (!scheduled)
                    {
                        scheduled = true;
                        Schedule();
                    }
                }
                if(awaitAudio!=null)
                {
                    if(!awaitAudio())
                    {
                        if(EditorApplication.timeSinceStartup>audioDeadline)
                            throw new TimeoutException("Review did not load the expected local WAV into its AudioSource");
                        return;
                    }
                    awaitAudio=null;
                }
                if (steps.Count > 0) steps.Dequeue()();
                else Finish(null);
            }
            catch (Exception ex) { Finish(ex); }
        }

        static void Schedule()
        {
            steps.Enqueue(() =>
            {
                TraceCorrelationCheck.RunChecks();
                Check(true, "Capture correlates speech with camera frames, absent frames and two marked guide endpoints");
            });
            steps.Enqueue(() => { Screenshot("trace-home"); Click("HomeDemo"); });
            steps.Enqueue(() => Click("StartRecording"));
            steps.Enqueue(() => Click("Point-input"));
            steps.Enqueue(() =>
            {
                Click("RepeatDemoSpeech");
                Check(app.TraceData.notes.Count==1 && app.TraceData.notes[0].pinchRole=="input",
                    "Replaying a lecture leaves the current note and pinch intact");
                Screenshot("trace-recording"); Click("NextDemoSpeech");
            });
            steps.Enqueue(() => Click("Point-amp"));
            steps.Enqueue(() => Click("NextDemoSpeech"));
            steps.Enqueue(() => Click("EndRecording"));
            steps.Enqueue(() =>
            {
                Check(app.TraceData.notes.Count == 3, "Three spoken passages survived two pinches");
                var notes = app.TraceData.notes;
                Check(notes.Count(n => !string.IsNullOrEmpty(n.pinchRole)) == 2, "Only the student's two pinches were stored");
                Check(string.IsNullOrEmpty(notes[2].pinchRole) && notes[2].candidateRole == "gnd" && !notes[2].reviewed,
                    "Unpinched GND is a suggestion pending human review");
                Check(notes[2].transcript.Contains("접지") && notes[2].framePaths.Length == 3,
                    "Missing point retained original utterance and three frames");
                foreach (var frame in notes[2].framePaths)
                    Check(frame.StartsWith("res:") && Resources.Load<Texture2D>(frame.Substring(4)) != null, "Evidence texture " + frame);
                Click("UseCandidate");
            });
            steps.Enqueue(() => Click("UseCandidate"));
            steps.Enqueue(() =>
            {
                Click("PlaySourceAudio");
                Check(app.GetComponents<AudioSource>().Any(s=>s.clip!=null && s.clip.name=="gnd_voice"),"Review opens the audio belonging to the missed GND note");
                Click("ExpandEvidence"); Click("NextFrame");
                Screenshot("trace-photo");
                var frameLabel=GameObject.Find("FrameNumber").GetComponent<Text>().text;
                Click("CloseEvidence");
                Check(app.CurrentReviewIndex==2 && GameObject.Find("FrameNumber").GetComponent<Text>().text==frameLabel,
                    "Enlarged photograph returns to the same note and selected frame");
                Screenshot("trace-review"); Click("UseCandidate");
            });
            steps.Enqueue(() => { Check(app.TraceData.notes.All(n => n.reviewed), "All shares explicitly reviewed"); Click("BeginReplay"); });
            steps.Enqueue(() => { Check(app.CurrentScreen == "Replay", "Student B replay opened"); Click("NextReplay"); });
            steps.Enqueue(() =>
            {
                Click("ReplayStep-0");Click("ReplayStep-2");
                Check(GameObject.Find("ReplayRole").GetComponent<Text>().text.Contains("접지"),"Timeline jumps directly to the selected utterance");
            });
            steps.Enqueue(() => { Screenshot("trace-replay"); Click("BeginQuiz"); });
            steps.Enqueue(() => { Check(app.CurrentScreen == "Quiz", "Uncued location task opened"); Click("Point-input"); });
            steps.Enqueue(() => Click("Point-gnd"));
            steps.Enqueue(() =>
            {
                Check(app.CurrentScreen == "Result", "Correct retry completed the task");
                var attempt = app.TraceData.attempts.Last();
                Check(attempt.completed && !attempt.firstCorrect && attempt.firstRole == "input",
                    "First-choice error is retained independently of completion");
                Check(TraceArchive.All().Any(s => s.attempts.Any(a => a.completed && !a.firstCorrect)),
                    "Completed attempt survives session reload");
                Screenshot("trace-result");
            });
            steps.Enqueue(() => { Click("ResultHome"); Click("HomeDemo"); Click("StartRecording"); });
            steps.Enqueue(() => { Click("Point-output"); Click("NextDemoSpeech"); Click("NextDemoSpeech"); Click("EndRecording"); });
            steps.Enqueue(() =>
            {
                var first = app.TraceData.notes[0];
                Check(first.status == "mismatch" && first.pinchRole == "output" && first.candidateRole == "input",
                    "Disagreement preserves both the student's original pinch and the candidate");
                Click("KeepPin"); Click("MarkUnknown"); Click("EditLocation"); Click("Choose-amp");
                Check(app.TraceData.ReplayNotes().Select(n => n.finalRole).SequenceEqual(new[] {"output","amp"}),
                    "Keep, unresolved and manual correction produce a replay of only confirmed locations in order");
                Check(app.TraceData.notes[1].reviewed && !string.IsNullOrEmpty(app.TraceData.notes[1].transcript),
                    "Unresolved location retains its original speech");
                var note=app.TraceData.notes[2];
                note.transcript=string.Concat(Enumerable.Repeat("회로의 입력과 출력 위치를 다시 비교합니다. ",48))+"검증문장끝";
                note.reason=string.Concat(Enumerable.Repeat("학생이 누른 위치와 포인터가 가리킨 위치가 다릅니다. ",36))+"근거의끝";
                Click("PrevReview");Click("NextReview");Click("ReadReviewReason");
                int reasonPages=0;
                while(GameObject.Find("NextReviewSource")!=null && reasonPages++<40)Click("NextReviewSource");
                Check(GameObject.Find("CompleteSource").GetComponent<Text>().text.Contains("근거의끝"),
                    "Complete candidate reasoning remains readable beyond the review card");
                Click("CloseReviewSource");
                Click("FullTranscript"); Click("TranscriptSegment-2");
                int pages=0;
                while(GameObject.Find("NextPassage")!=null && pages++<40)Click("NextPassage");
                Check(GameObject.Find("TranscriptPassage").GetComponent<Text>().text.Contains("검증문장끝"),
                    "Long transcript is readable to its final word");
                Click("ReturnTranscript");
            });
            steps.Enqueue(OpenLiveAudioFixture);
            steps.Enqueue(()=>PlayAndWaitForLocalWave("gnd","Live review reopens the retained PCM WAV for this note"));
            steps.Enqueue(()=>
            {
                Click("PlaySourceAudio");
                Check(fixturePlayer.clip==null,"Stop releases the local source clip");
                Click("NextReview");
                PlayAndWaitForLocalWave("amp","A unique old journal match replays its original WAV after archive relocation");
            });
            steps.Enqueue(()=>
            {
                Click("NextReview");
                Check(fixturePlayer.clip==null && GameObject.Find("PlaySourceAudio")==null,
                    "Ambiguous repeated legacy quotes keep their text without playing either neighboring clip");
                Check(app.TraceData.notes[2].quote=="여기를 보세요.","Ambiguous legacy note retains its original words");
            });
            ScheduleGuide();
            ScheduleTransferReplay();
        }

        static void ScheduleGuide()
        {
            string sessionId = "";
            steps.Enqueue(() =>
            {
                Click("ReviewHome");
                Click("HomeGuideExample");
                sessionId = app.TraceData.id;
                Check(app.CurrentScreen == "GuideReview" && app.TraceData.guide.steps.Length == 5 &&
                    GameObject.Find("GuideApprovalIssue") != null && GameObject.Find("GuideStartSimulation") == null,
                    "Example shows five draft steps and withholds student launch when evidence is unresolved");
                Click("GuideReviewStep-1");
                Screenshot("guide-review");
                Click("GuideEditTo");
                Click("GuideReviewPoint-a-1");
                Click("GuideApprove");
                Check(app.TraceData.guide.approved && app.TraceData.guide.steps[1].toPoint == "a-1",
                    "Teacher resolves the resistor endpoint in the guide before approving");
            });
            steps.Enqueue(() =>
            {
                Click("GuideStartSimulation");
                Check(app.CurrentScreen == "GuidePractice" && GameObject.Find("GuideChecklistRow-3") != null,
                    "Student sees the materials and power-off checklist before wiring");
                Click("GuideConfirm");
                WireGuide("plus-1", "c-1");
                var run = app.TraceData.guideRuns.Last();
                Check(run.stepIndex == 1 && run.wrongAttempts == 1 && GuideTextHas("GuideFeedback", "A행") &&
                    !string.IsNullOrEmpty(run.errorStartedUtc), "Wrong row preserves the current step and points back to the correct row");
                Screenshot("guide-error");
                Click("GuidePlayAudio");
                var clip = Resources.Load<AudioClip>("TraceGuideDemo/resistor");
                Check(clip != null && app.GetComponents<AudioSource>().Any(audio => audio.clip == clip),
                    "Student replays the source segment from the step where the wiring failed");
                Click("GuideOpenEvidence");
                Check(GuideTextHas("GuideEvidenceSpeech", GuideRules.DemoSpoken("resistor")),
                    "Source panel shows the original words behind the corrected step");
                Click("GuideCloseEvidence");
                Click("GuideHome");
                reloadingAppId = app.GetInstanceID();
                app = null;
                EditorSceneManager.LoadSceneInPlayMode("Assets/ContextPin/Scenes/Relay.unity", new LoadSceneParameters(LoadSceneMode.Single));
            });
            steps.Enqueue(() =>
            {
                var archived = TraceArchive.All().Single(session => session.id == sessionId);
                var savedRun = archived.guideRuns.Single();
                Check(savedRun.toPoint == "c-1" && savedRun.hintUses == 2 && !savedRun.completed && savedRun.stepIndex == 1,
                    "On-disk archive retains the in-progress step, mistake and two hint interactions");
                Click("Library");
                Click("GuideResume-" + sessionId);
                Check(app.GetInstanceID() != reloadingAppId && app.CurrentScreen == "GuidePractice" && app.TraceData != archived &&
                    app.TraceData.guideRuns.Single().stepIndex == 1 && GuideTextHas("GuideFeedback", "A행"),
                    "A fresh app instance resumes the saved step and feedback from disk" +
                    (app.CurrentScreen == "GuidePractice" ? "" : $" (screen={app.CurrentScreen}, " +
                    $"session seal={GuideRules.VerifyApproved(app.TraceData?.guide)}, run seal={GuideRules.VerifyApproved(savedRun.guide)}, " +
                    $"version match={GuideRules.IsCurrentVersion(savedRun, savedRun.guide)}, step={savedRun.stepIndex})"));
                WireGuide("plus-1", "a-1");
                WireGuide("b-1", "a-2");
                Check(GuideTextHas("GuideFeedback", "방향이 반대"), "Reversed LED polarity has specific correction feedback");
                WireGuide("a-2", "b-1");
                WireGuide("b-2", "gnd-1");
                Check(GameObject.Find("GuideFinish") == null, "Virtual power waits for the final pre-power inspection");
                Click("GuidePowerCheck");
                Click("GuideFinish");
            });
            steps.Enqueue(() =>
            {
                var run = app.TraceData.guideRuns.Single();
                Check(app.CurrentScreen == "GuideResult" && run.completed && run.lit && run.stepIndex == 5 &&
                    run.connections.Count == 3 && run.wrongAttempts == 2 && run.hintUses == 2 &&
                    run.correctionSeconds > 0 && GuideTextHas("GuideResultCompletion", "5 / 5"),
                    "Corrected circuit lights the simulated LED and reports completed steps, corrections and hints");
                Check(TraceArchive.All().Single(session => session.id == sessionId).guideRuns.Single().lit,
                    "Successful simulated lighting is saved with the run");
                Screenshot("guide-result");
            });
        }

        static bool GuideTextHas(string id, string value) =>
            GameObject.Find(id)?.GetComponent<Text>()?.text.Contains(value) == true;

        static void ScheduleTransferReplay()
        {
            string sessionId = "";
            string approvedHash = "";
            string manuallyTranscribed = "기록 재생 테스트에서 저항의 연결 근거를 확인합니다.";
            steps.Enqueue(() =>
            {
                Click("GuideHome");
                Click("HomeDiagnostics");
                Check(app.CurrentScreen == "Diagnostics" && GuideTextHas("Diagnostic-0", "데스크톱") &&
                    GuideTextHas("Diagnostic-1", "수신 대기") && GuideTextHas("Diagnostic-2", "기록 대기"),
                    "Desktop diagnostics keep camera frames and microphone input unavailable until measured");
                Click("DiagnosticsBack");
                sessionId = AddTransferReplayFixture();
                Click("Library");
                Click("Saved-" + sessionId);
                Check(app.CurrentScreen == "GuideReview" && GuideTextHas("Description", "기록 재생") &&
                    GameObject.Find("GuideStartSimulation") == null, "Replay fixture identifies the source and opens an unapproved teacher draft");
                var capture = TeacherFixtureCommand("capture", new FixturePayload { phase = "after_action" });
                Check(capture.status == "rejected" && app.TraceData.captures.Count == 1 &&
                    app.TraceData.captures[0].source == "replay", "A desktop capture command cannot replace replay evidence with real camera proof");
                var raw = app.TraceData.notes.Single(note => note.audioFile == "fixture-resistor.wav");
                var manual = TeacherFixtureCommand("manualTranscript",
                    new FixturePayload { text = manuallyTranscribed, sourceNoteId = raw.id });
                var updated = app.TraceData.notes.Single(note => note.supersedesId == raw.id);
                Check(manual.status == "applied" && updated.author == "instructor" && updated.audioFile == raw.audioFile &&
                    app.TraceData.guide.steps[1].sourceNoteId == updated.id, "Teacher command records manual transcription as a revision of the same clip");
                var draft = app.TraceData.guide;
                var wrongApproval = TeacherFixtureCommand("approveDraft", new FixturePayload { steps = draft.steps });
                Check(wrongApproval.status == "rejected" && !app.TraceData.guide.approved,
                    "An approval command with unchecked kit and unresolved position stays unapproved");
                draft.steps[1].toPoint = "a-1";
                var approval = TeacherFixtureCommand("approveDraft", new FixturePayload
                {
                    steps = draft.steps,
                    kitConfirmed = true,
                    kitConditions = "Synthetic test fixture. No physical equipment used.",
                });
                approvedHash = app.TraceData.guide.contentHash;
                Check(approval.status == "applied" && GuideRules.VerifyApproved(app.TraceData.guide) &&
                    app.TraceData.guide.evidence.Any(item => item.provenance == "replay" && item.kind == "image") &&
                    GameObject.Find("GuideStartPhysical") == null, "Teacher request seals a replay guide with known board geometry and no real-practice launch");
                Click("GuideReviewStep-1");
                Click("GuideOpenEvidence");
                Check(GuideTextHas("GuideEvidenceSpeech", manuallyTranscribed) &&
                    GameObject.Find("GuideEvidenceFrame")?.GetComponent<RawImage>()?.texture != null,
                    "Approved replay step opens the corrected words and original fixture image");
                Screenshot("guide-transfer-replay");
                Click("GuidePlayAudio");
                var reference = Resources.Load<AudioClip>("TraceGuideDemo/resistor");
                audioDeadline = EditorApplication.timeSinceStartup + 8;
                awaitAudio = () =>
                {
                    if (reference == null || !app.GetComponents<AudioSource>().Any(source => source.clip != null &&
                        source.clip != reference && Mathf.Abs(source.clip.length - reference.length) < .06f)) return false;
                    Check(true, "Replay plays the archived WAV from the corrected source segment");
                    return true;
                };
            });
            steps.Enqueue(() =>
            {
                Click("GuideCloseEvidence");
                Click("GuideStartSimulation");
                Click("GuideConfirm");
                WireGuide("plus-1", "a-2");
                var run = app.TraceData.guideRuns.Last();
                Check(run.stepIndex == 2 && run.connections[0].toPoint == "a-2" && run.wrongAttempts == 0 &&
                    run.verifications.All(record => record.basis == "simulator_verified" && record.provenance == "simulation"),
                    "A1 and C1 share a strip in the real-board profile replay simulation");
                Click("GuidePreviousStep");
                Check(GuideTextHas("GuideStepCount", "2 / 5") && GameObject.Find("GuideSubmit") == null &&
                    run.stepIndex == 2, "Previous step is a read-only description and leaves progress intact");
                Click("GuideCurrentStep");
                Check(GuideTextHas("GuideStepCount", "3 / 5"), "Returning from replay reopens the intact current step");
                Click("GuideHome");
                Click("Library");
                Click("Saved-" + sessionId);
                typeof(RelayApp).GetMethod("StartNextGuideVersion", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(app, null);
                Check(!app.TraceData.guide.approved && app.TraceData.guide.version == 2 &&
                    app.TraceData.approvedGuides.Any(guide => guide.contentHash == approvedHash) &&
                    app.TraceData.guideRuns.Last().guideHash == approvedHash,
                    "A new draft leaves the approved replay and active run version unchanged");
                Click("GuideHome");
                Click("Library");
                Click("GuideResume-" + sessionId);
                Check(app.CurrentScreen == "GuidePractice" && GuideTextHas("GuideStepCount", "3 / 5") &&
                    app.TraceData.guideRuns.Last().guideVersion == 1 && app.TraceData.guide.version == 2,
                    "The device archive restores the pinned guide even while a new draft exists");
            });
        }

        static string AddTransferReplayFixture()
        {
            string isolated = Environment.GetEnvironmentVariable("RELAY_TEST_DATA");
            if (string.IsNullOrEmpty(isolated) || Path.GetFullPath(TraceArchive.Root) != Path.GetFullPath(Path.Combine(isolated, "Trace")))
                throw new InvalidOperationException("Replay fixture needs an isolated smoke archive");
            var session = TraceSession.New(false);
            session.mode = "guide-live";
            session.transferMode = "replay";
            session.transferBoardId = LabBoards.PhysicalId;
            session.status = "review";
            session.guide = GuideRules.DemoDraft();
            session.guide.id = Guid.NewGuid().ToString("N");
            session.guide.sourceSessionId = session.id;
            session.guide.sourceMode = "replay";
            session.guide.example = false;
            session.guide.boardId = LabBoards.PhysicalId;
            session.guide.boardDefinition = LabBoards.Resolve(LabBoards.PhysicalId);
            session.guide.demonstrationBoardInstanceId = "fixture-demo-instance";
            string folder = Path.Combine(TraceArchive.Folder(session), "evidence");
            Directory.CreateDirectory(folder);
            string audioPath = Path.Combine(folder, "fixture-resistor.wav");
            string photoPath = Path.Combine(folder, "fixture-photo.jpg");
            File.Copy(Path.Combine(Application.dataPath, "Resources/TraceGuideDemo/resistor.wav"), audioPath);
            File.Copy(Path.Combine(Application.dataPath, "Resources/TraceDemo/input_0.jpg"), photoPath);
            session.captures.Add(new TransferCapture
            {
                captureId = "fixture-photo", sessionId = session.id, phase = "clarification", source = "replay",
                path = Path.GetFileName(photoPath), sha256 = FixtureHash(photoPath), quality = "accepted",
                boardInstanceId = "fixture-demo-instance", registrationRevision = 1, trackingEpoch = 2
            });
            foreach (var step in session.guide.steps)
            {
                bool resistor = step.kind == "resistor";
                session.notes.Add(new TraceNote
                {
                    id = step.sourceNoteId, transcript = step.sourceQuote, quote = step.sourceQuote,
                    sourceMode = "replay", revision = 1, time = session.notes.Count * 4,
                    audioFile = resistor ? Path.GetFileName(audioPath) : "",
                    audioSha256 = resistor ? FixtureHash(audioPath) : "",
                    captureIds = resistor ? new[] { "fixture-photo" } : Array.Empty<string>(),
                });
            }
            TraceArchive.Save(session);
            return session.id;
        }

        static string FixtureHash(string path)
        {
            using var hash = SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
        }

        [Serializable]
        sealed class FixturePayload
        {
            public string text, sourceNoteId, kitConditions, phase;
            public bool kitConfirmed;
            public GuideStep[] steps;
        }

        [Serializable]
        sealed class FixtureCommand
        {
            public string id, kind;
            public FixturePayload payload;
        }

        static TransferCommandReceipt TeacherFixtureCommand(string kind, FixturePayload payload)
        {
            var apply = typeof(RelayApp).GetMethod("ApplyTeacherCommand", BindingFlags.Instance | BindingFlags.NonPublic);
            string json = JsonUtility.ToJson(new FixtureCommand { kind = kind, id = Guid.NewGuid().ToString("N"), payload = payload });
            var command = JsonUtility.FromJson(json, apply.GetParameters()[0].ParameterType);
            return (TransferCommandReceipt)apply.Invoke(app, new[] { command });
        }

        static void WireGuide(string from, string to)
        {
            Click("GuidePracticePoint-" + from);
            Click("GuidePracticePoint-" + to);
            Click("GuideSubmit");
        }

        static void OpenLiveAudioFixture()
        {
            string testRoot=Environment.GetEnvironmentVariable("RELAY_TEST_DATA");
            if(string.IsNullOrEmpty(testRoot) || Path.GetFullPath(TraceArchive.Root)!=Path.GetFullPath(Path.Combine(testRoot,"Trace")))
                throw new InvalidOperationException("Live audio fixture requires an isolated smoke archive");
            var session=TraceSession.New(false);
            session.status="review";
            string folder=Path.Combine(TraceArchive.Folder(session),"evidence");
            Directory.CreateDirectory(folder);
            string direct="현장 구간.wav",legacy="옛 구간.wav";
            string media=Path.Combine(Application.dataPath,"Resources","TraceDemo");
            File.Copy(Path.Combine(media,"gnd_voice.wav"),Path.Combine(folder,direct));
            File.Copy(Path.Combine(media,"amp_voice.wav"),Path.Combine(folder,legacy));
            string[] quotes={"여기서 접지를 확인해요.","증폭단 출력을 확인해요.","여기를 보세요."};
            float[] times={1f,4f,8f};
            for(int i=0;i<quotes.Length;i++)
                session.notes.Add(new TraceNote{id="live-"+i,quote=quotes[i],transcript=quotes[i],time=times[i],
                    audioFile=i==0?direct:null,status="unknown"});
            File.WriteAllLines(Path.Combine(folder,"transcript.jsonl"),new[]{
                JournalChunk(3f,5f,quotes[1],legacy),
                JournalChunk(7f,8f,quotes[2],legacy),JournalChunk(8.2f,9f,quotes[2],direct)
            });
            TraceArchive.Save(session);
            Click("ReviewHome");Click("Library");Click("Saved-"+session.id);
            Check(app.TraceData.mode=="live" && app.TraceData.notes[0].audioFile==direct,
                "Saved live session reloads the note's source WAV filename");
        }

        static string JournalChunk(float start,float end,string words,string file)
        {
            // Imported sessions can retain absolute paths from another device.
            return JsonUtility.ToJson(new TraceTranscript{start=start,end=end,transcript=words,audioPath="/old/headset/files/"+file});
        }

        static void PlayAndWaitForLocalWave(string resourceRole,string message)
        {
            var reference=Resources.Load<AudioClip>("TraceDemo/"+resourceRole+"_voice");
            if(reference==null)throw new InvalidOperationException("Missing WAV fixture "+resourceRole);
            Check(GameObject.Find("PlaySourceAudio").GetComponentInChildren<Text>().text=="현장 음성 듣기",
                "The archived "+resourceRole+" source is labeled as field audio");
            Click("PlaySourceAudio");
            audioDeadline=EditorApplication.timeSinceStartup+8;
            awaitAudio=()=>
            {
                fixturePlayer=app.GetComponents<AudioSource>().FirstOrDefault(s=>s.clip!=null && s.clip!=reference &&
                    s.clip.channels==reference.channels && Mathf.Abs(s.clip.length-reference.length)<.06f);
                if(fixturePlayer==null)return false;
                Check(true,message);
                return true;
            };
        }

        static void Click(string id)
        {
            var go = GameObject.Find(id);
            if (go == null || !go.TryGetComponent<RelayHit>(out var hit) || hit.clicked == null)
                throw new InvalidOperationException("Missing action " + id + " on " + app.CurrentScreen);
            Physics.SyncTransforms();
            if (go.GetComponent<Collider>() == null) throw new InvalidOperationException("Missing input collider " + id);
            var camera = Camera.main;
            if (!Physics.Raycast(camera.transform.position, hit.Center - camera.transform.position, out var visible, 12)
                || visible.collider.gameObject != go) throw new InvalidOperationException("Action is obscured: " + id);
            hit.clicked.Invoke();
        }

        static void Screenshot(string name)
        {
            var cam = Camera.main;
            var previous = cam.targetTexture;
            var active = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(1280, 800, 24);
            var frame = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            try
            {
                cam.targetTexture = rt;
                cam.aspect=(float)rt.width/rt.height;
                app.Recenter();
                Canvas.ForceUpdateCanvases();
                cam.Render(); RenderTexture.active = rt;
                frame.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); frame.Apply();
                File.WriteAllBytes(Path.Combine(artifacts, name + ".jpg"), frame.EncodeToJPG(88));
            }
            finally
            {
                cam.targetTexture = previous; cam.ResetAspect(); app.Recenter(); RenderTexture.active = active;
                RenderTexture.ReleaseTemporary(rt); UnityEngine.Object.DestroyImmediate(frame);
            }
        }

        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            passed.Add(message);
        }

        [Serializable] class Report { public bool success; public string[] checks; public string error; }
        static void Finish(Exception ex)
        {
            EditorApplication.update -= Tick;
            var report = new Report { success = ex == null, checks = passed.ToArray(), error = ex?.ToString() ?? "" };
            File.WriteAllText(Path.Combine(artifacts, "smoke-report.json"), JsonUtility.ToJson(report, true));
            if (ex == null) Debug.Log("TRACE_SMOKE_PASS: " + passed.Count); else Debug.LogException(ex);
            EditorApplication.Exit(ex == null ? 0 : 1);
        }
    }
}
