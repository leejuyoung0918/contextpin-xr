using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        enum GuideAlignment { None, Recording, Practice }

        GuideRun guideRun;
        GuideAlignment guideAlignment;
        bool guideRecording, guideGenerating, guideEvidenceOpen, guideViewingCheckpoint;
        int guideReviewIndex, guideEvidencePage, guideEvidenceFrame, guideRequestEpoch;
        string guideGenerationStatus = "";
        OVRDisplay guideDisplay;

        LabGuide ViewGuide => TraceData?.guide;
        LabGuide DisplayGuide => guideRun?.guide ?? ViewGuide;
        bool UsesPhysicalGuide => (DisplayGuide?.boardId ?? TraceData?.transferBoardId) == LabBoards.PhysicalId;
        IReadOnlyList<GuidePoint> GuideDisplayPoints => screen == ScreenState.Home ? GuideBoard.Points :
            DisplayGuide == null && UsesPhysicalGuide ? physicalGuideBoard.points : GuideRules.BoardPoints(DisplayGuide);
        GuidePoint GuideDisplayPoint(string id) => GuideDisplayPoints.FirstOrDefault(point => point.id == id);
        string GuideDisplayName(string id) => GuideDisplayPoint(id)?.label ?? "미지정";
        bool HasPhysicalGuide => UsesPhysicalGuide && ViewGuide?.sourceMode == "real";
        GuideStep CurrentGuideReviewStep => ViewGuide?.steps?.ElementAtOrDefault(guideReviewIndex);
        bool guideIsExample => TraceData?.mode == "guide-example";
        bool guideHasPhysicalMarkers => guideRun?.physical == true && xr && aligned;
        string guideFeedback => guideRun?.feedback ?? "";
        string guideSelectedFrom => guideRun?.fromPoint;
        string guideSelectedTo => guideRun?.toPoint;
        string GuideValidationMessage => GuideRules.Validate(ViewGuide, GuideSourceNotes().Select(note => note.id));

        IEnumerable<TraceNote> GuideSourceNotes()
        {
            if (TraceData == null) return Enumerable.Empty<TraceNote>();
            var superseded = TraceData.notes.Select(note => note.supersedesId).Where(id => !string.IsNullOrEmpty(id)).ToHashSet();
            return TraceData.InOrder().Where(note => !string.IsNullOrWhiteSpace(note.quote) && !superseded.Contains(note.id));
        }

        void ResetGuideView()
        {
            StopTransferRelay();
            ResetPhysicalGuideAlignment(false);
            guideRecording = false;
            guideAlignment = GuideAlignment.None;
            guideRun = null;
            guideGenerating = false;
            guideRequestEpoch++;
            ResetGuideEvidence();
        }

        void BeginGuideLiveConsent()
        {
            ResetGuideView();
            guideRecording = true;
            demo = false;
            aligned = false;
            worldPoints.Clear();
            notice = "";
            SetPassthrough(false);
            Go(ScreenState.Consent);
        }

        void StartGuideRecording()
        {
            if (screen == ScreenState.Consent)
            {
                TraceData = TraceSession.New(false);
                TraceData.mode = "guide-live";
                TraceData.transferConsent = true;
                TraceData.transferMode = xr && Application.isEditor ? "synthetic" : "real";
                TraceData.transferBoardId = LabBoards.PhysicalId;
                transferClockOrigin = Time.realtimeSinceStartupAsDouble;
                selectedTransferTerminal = null;
                lastCaptureFrame = 0;
                recordingPhase = "after_action";
                transferCapturing = false;
                storageError = "";
                Save();
                StartTransferRelay();
            }
            if (xr && !aligned)
            {
                StartGuideAlignment(GuideAlignment.Recording);
                return;
            }

            notice = "";
            Save();
            Go(ScreenState.Recording);
            capture.StartCapture(TraceArchive.Folder(TraceData), ResolveWorld, OnLiveEvent, OnLiveStatus,
                GuideDisplayPoints.Select(point => point.id).ToArray(), explicitSnapshots: true);
            if (xr && aligned) RequestTransferPhoto("setup");
        }

        void EndGuideRecording()
        {
            TraceData.status = "review";
            guideRecording = false;
            aligned = false;
            worldPoints.Clear();
            ResetPhysicalGuideAlignment(false);
            SetPassthrough(false);
            Recenter();
            LinkSavedGuidePhotos();
            ViewOrCreateGuide();
            RefreshGuideEvidenceCatalogue();
            Save();
            Go(ScreenState.GuideReview);
            GenerateGuideDraft();
        }

        void OpenGuideExample()
        {
            var sample = TraceSession.New(true);
            sample.mode = "guide-example";
            sample.status = "review";
            sample.guide = GuideRules.DemoDraft();
            sample.guide.id = Guid.NewGuid().ToString("N");
            sample.guide.sourceSessionId = sample.id;
            float start = 0;
            foreach (var step in sample.guide.steps)
            {
                string speech = GuideRules.DemoSpoken(step.kind);
                sample.notes.Add(new TraceNote
                {
                    id = step.sourceNoteId,
                    time = start,
                    quote = speech,
                    transcript = speech,
                    status = "example", sourceMode = "synthetic", revision = 1,
                });
                start += Resources.Load<AudioClip>("TraceGuideDemo/" + step.kind)?.length ?? 6;
            }
            OpenGuideSession(sample);
            Save();
        }

        void ViewOrCreateGuide()
        {
            if (!string.IsNullOrEmpty(ViewGuide?.id)) return;
            TraceData.guide = new LabGuide
            {
                id = Guid.NewGuid().ToString("N"),
                sourceSessionId = TraceData.id,
                boardId = TraceData.transferBoardId ?? GuideBoard.Id,
                boardDefinition = TraceData.transferBoardId == LabBoards.PhysicalId ? LabBoards.Resolve(LabBoards.PhysicalId) : null,
                demonstrationBoardInstanceId = TraceData.registrations.LastOrDefault(item => item.role == "demonstration")?.boardInstanceId,
                sourceMode = TraceData.transferMode ?? "legacy",
                title = "브레드보드 LED 실습",
                steps = GuideRules.EmptyDraft().steps,
            };
            GuideRules.Migrate(ViewGuide);
        }

        void OpenGuideSession(TraceSession session)
        {
            ResetGuideView();
            TraceData = session;
            transferClockOrigin = Time.realtimeSinceStartupAsDouble;
            transferCapturing = false;
            GuideRules.Migrate(session.guide);
            foreach (var run in session.guideRuns) GuideRules.Migrate(run);
            demo = guideIsExample;
            aligned = false;
            worldPoints.Clear();
            storageError = "";
            notice = "";
            guideReviewIndex = 0;
            guideGenerationStatus = guideIsExample ? "예시 시연 · 합성 음성과 준비된 초안" : "";
            LoadFrameInfo();
            SetPassthrough(false);
            Recenter();
            ViewOrCreateGuide();
            if (!ViewGuide.approved) RefreshGuideEvidenceCatalogue();
            Go(ScreenState.GuideReview);
            StartTransferRelay();
        }

        [Serializable] sealed class GuideDraftRequest
        {
            public GuideSourceRequest[] notes;
            public GuidePoint[] points;
        }

        [Serializable] sealed class GuideSourceRequest
        {
            public string id, transcript;
            public float time;
            public string[] pinMarks;
        }

        void GenerateGuideDraft()
        {
            if (guideGenerating || guideIsExample || ViewGuide == null || ViewGuide.approved) return;
            var notes = GuideSourceNotes().Select(note => new GuideSourceRequest
            {
                id = note.id,
                transcript = note.quote,
                time = note.time,
                pinMarks = (note.pinMarks ?? Array.Empty<string>()).Where(id => GuideDisplayPoint(id) != null).ToArray(),
            }).ToArray();
            if (notes.Length == 0 || notes.Length > 120)
            {
                guideGenerationStatus = notes.Length == 0
                    ? "전사된 설명이 없습니다. 마이크와 AI 서버를 확인하고 다시 기록하세요."
                    : "한 번에 분석할 발화가 너무 많습니다. 짧게 다시 기록하거나 원문으로 직접 지정하세요.";
                Draw();
                return;
            }
            guideGenerating = true;
            guideGenerationStatus = "교수의 설명에서 실습 단계를 만드는 중입니다.";
            Draw();
            StartCoroutine(RequestGuideDraft(new GuideDraftRequest { notes = notes, points = GuideDisplayPoints.ToArray() },
                ++guideRequestEpoch));
        }

        IEnumerator RequestGuideDraft(GuideDraftRequest body, int epoch)
        {
            // Audio and photographs stay in the existing capture archive. This request only
            // needs the text, its position in the recording and the registered board points.
            string url = (RelayEvidence.LoadConfig().serverUrl ?? "").TrimEnd('/') + "/trace/guide";
            using (var web = new UnityWebRequest(url, "POST"))
            {
                web.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(body)));
                web.downloadHandler = new DownloadHandlerBuffer();
                web.SetRequestHeader("Content-Type", "application/json");
                web.timeout = 52;
                yield return web.SendWebRequest();
                if (epoch != guideRequestEpoch || screen != ScreenState.GuideReview) yield break;

                guideGenerating = false;
                GuideDraft response = null;
                if (web.result == UnityWebRequest.Result.Success)
                {
                    try { response = JsonUtility.FromJson<GuideDraft>(web.downloadHandler.text); }
                    catch (ArgumentException exception) { Debug.LogWarning("Guide draft response: " + exception.Message); }
                }

                var steps = ViewGuide.steps;
                bool valid = response?.steps != null && response.steps.Length == steps.Length &&
                    response.steps.Select(step => step?.kind).SequenceEqual(steps.Select(step => step?.kind));
                if (valid)
                {
                    var previousSteps = ViewGuide.steps;
                    ViewGuide.steps = response.steps;
                    for (int index = 0; index < ViewGuide.steps.Length; index++)
                    {
                        ViewGuide.steps[index].stepId = previousSteps[index].stepId;
                        ViewGuide.steps[index].prerequisites = previousSteps[index].prerequisites;
                        ViewGuide.steps[index].rationaleSource = "suggested_explanation";
                    }
                    foreach (var step in ViewGuide.steps)
                    {
                        var note = GuideEvidenceNote(step);
                        if (note == null || string.IsNullOrEmpty(step.sourceQuote) || !note.quote.Contains(step.sourceQuote))
                        {
                            step.sourceNoteId = step.sourceQuote = step.fromPoint = step.toPoint = step.ledDirection = "";
                        }
                    }
                    guideGenerationStatus = "초안 생성 완료 · 발화와 연결 위치를 검토하세요.";
                    RefreshGuideEvidenceCatalogue();
                    Save();
                }
                else guideGenerationStatus = "AI 초안을 만들 수 없습니다. 서버를 확인하거나 원문에서 직접 지정하세요.";
                Draw();
            }
        }

        void SelectGuideReviewStep(int index)
        {
            guideReviewIndex = Mathf.Clamp(index, 0, ViewGuide.steps.Length - 1);
            ResetGuideEvidence();
            Draw();
        }

        void SelectGuideReviewPoint(string pointId, bool from)
        {
            var step = CurrentGuideReviewStep;
            if (guideGenerating || ViewGuide.approved || !GuideRules.IsWiringStep(step) || GuideDisplayPoint(pointId) == null) return;
            if (from) step.fromPoint = pointId;
            else step.toPoint = pointId;
            GuideRules.RecordDraftEdit(ViewGuide, step.stepId, from ? "fromPoint" : "toPoint");
            notice = "";
            Save();
            Draw();
        }

        void SetGuideLedDirection(bool confirmed)
        {
            var step = CurrentGuideReviewStep;
            if (guideGenerating || ViewGuide.approved || step?.kind != "led") return;
            step.ledDirection = confirmed ? "anode_to_cathode" : "";
            GuideRules.RecordDraftEdit(ViewGuide, step.stepId, "ledDirection");
            notice = "";
            Save();
            Draw();
        }

        void ChangeGuideEvidenceSource(GuideStep step, int offset)
        {
            if (guideGenerating || ViewGuide.approved || step == null || offset == 0) return;
            var notes = GuideSourceNotes().ToArray();
            if (notes.Length == 0) return;
            int index = Array.FindIndex(notes, note => note.id == step.sourceNoteId);
            int next = index < 0 ? (offset < 0 ? notes.Length - 1 : 0) : Mathf.Clamp(index + offset, 0, notes.Length - 1);
            step.sourceNoteId = notes[next].id;
            step.sourceQuote = notes[next].quote;
            step.evidenceIds = null;
            GuideRules.RecordDraftEdit(ViewGuide, step.stepId, "source");
            RefreshGuideEvidenceCatalogue();
            guideEvidenceFrame = guideEvidencePage = 0;
            notice = "";
            Save();
            Draw();
        }

        void ApproveGuide()
        {
            if (guideGenerating || ViewGuide == null || ViewGuide.approved) return;
            RefreshGuideEvidenceCatalogue();
            string error = GuideRules.Approve(ViewGuide, GuideSourceNotes().Select(note => note.id));
            if (string.IsNullOrEmpty(error))
            {
                TraceData.status = "approved";
                if (!TraceData.approvedGuides.Any(guide => guide.contentHash == ViewGuide.contentHash))
                    TraceData.approvedGuides.Add(ViewGuide);
                guideGenerationStatus = "교수 확인 완료 · 학생 실습을 시작할 수 있어요.";
                Save();
            }
            else notice = error;
            Draw();
        }

        void BeginGuidePractice(bool physical)
        {
            if (!GuideRules.VerifyApproved(ViewGuide) || physical && (!xr || !HasPhysicalGuide)) return;
            var existing = TraceData.guideRuns.LastOrDefault(run =>
                GuideRules.IsCurrentVersion(run, ViewGuide) && !run.completed && run.physical == physical);
            guideRun = existing ?? GuideRules.NewRun(ViewGuide, physical, DateTime.UtcNow);
            if (guideRun == null) return;
            if (existing == null) TraceData.guideRuns.Add(guideRun);
            if (physical && !guideRun.completed) GuideRules.InvalidatePracticeBoard(guideRun, guideRun.trackingEpoch + 1);
            EnterGuidePractice();
        }

        void EnterGuidePractice()
        {
            ResetPendingPhysicalPowerCheck();
            GuideRules.Resume(guideRun, DateTime.UtcNow);
            ResetGuideEvidence();
            notice = "";
            Save();
            SetPassthrough(xr && guideRun.physical);
            Go(guideRun.completed ? ScreenState.GuideResult : ScreenState.GuidePractice);
        }

        void SelectGuidePracticePoint(string pointId)
        {
            if (GuideRules.SelectPoint(guideRun, pointId))
            {
                Save();
                Draw();
            }
        }

        void CheckGuidePractice(bool observedLed = false)
        {
            if (guideRun == null || guideRun.completed) return;
            int previousStep = guideRun.stepIndex;
            var step = GuideRules.Current(guideRun);
            string actionId = Guid.NewGuid().ToString("N");
            if (!guideRun.physical && GuideRules.IsWiringStep(step))
                GuideRules.Submit(guideRun, DateTime.UtcNow, step?.stepId, actionId);
            else GuideRules.Confirm(guideRun, DateTime.UtcNow, observedLed, step?.stepId, actionId);

            if (guideRun.stepIndex != previousStep) ResetGuideEvidence();
            Save();
            if (guideRun.completed) Go(ScreenState.GuideResult);
            else Draw();
        }

        void RecordGuideHint()
        {
            if (screen != ScreenState.GuidePractice || guideRun == null) return;
            GuideRules.Hint(guideRun);
            Save();
        }

        void RecordGuideHelp()
        {
            if (screen != ScreenState.GuidePractice || guideRun == null) return;
            var step = GuideRules.Current(guideRun);
            string question = "이 단계의 연결을 확인하고 싶어요.";
            GuideRules.RequestHelp(guideRun, DateTime.UtcNow, question);
            if (guideRun.physical && step != null)
            {
                var checkpoint = TraceData.captures.LastOrDefault(photo => photo.captureId == TraceData.latestCheckpoint &&
                    photo.stepId == step.stepId && photo.registrationRevision == guideRun.registrationRevision);
                TraceData.recentHelp = new TransferHelp
                {
                    stepId = step.stepId, question = question, sourceQuote = step.sourceQuote, captureId = checkpoint?.captureId,
                    attempted = "교수 설명과 보드 위치를 다시 확인 중",
                    createdAt = DateTime.UtcNow.ToString("o"), boardInstanceId = guideRun.studentBoardInstanceId,
                    registrationRevision = guideRun.registrationRevision, trackingEpoch = guideRun.trackingEpoch,
                    selectedTerminals = new[] { step.fromPoint, step.toPoint },
                };
                OpenGuideEvidence(step);
            }
            notice = "도움 요청을 기록했어요. 전원을 끄고 배선을 확인해 주세요.";
            Save();
            Draw();
        }

        void ResetPendingPhysicalPowerCheck()
        {
            if (guideRun?.physical != true || guideRun.completed || !guideRun.wiringChecked) return;
            guideRun.wiringChecked = false;
            guideRun.feedback = "전원을 끈 뒤 배선 점검부터 다시 진행하세요.";
            Save();
        }

        void GuideBack()
        {
            ResetPendingPhysicalPowerCheck();
            GuideRules.Pause(guideRun, DateTime.UtcNow);
            if (TraceData != null) Save();
            Home();
        }

        void GuideResumeReview()
        {
            guideRun = null;
            aligned = false;
            worldPoints.Clear();
            ResetPhysicalGuideAlignment(false);
            ResetGuideEvidence();
            SetPassthrough(false);
            Recenter();
            Go(ScreenState.GuideReview);
        }

        void ResetGuideEvidence()
        {
            guideEvidenceOpen = false;
            guideViewingCheckpoint = false;
            guideEvidencePage = guideEvidenceFrame = 0;
        }

        TraceNote GuideEvidenceNote(GuideStep step) =>
            string.IsNullOrEmpty(step?.sourceNoteId) ? null : TraceData?.notes.Find(note => note.id == step.sourceNoteId);

        void OpenGuideEvidence(GuideStep step)
        {
            if (step == null) return;
            guideEvidenceOpen = true;
            guideEvidencePage = guideEvidenceFrame = 0;
            RecordGuideHint();
            Draw();
        }

        void CloseGuideEvidence()
        {
            ResetGuideEvidence();
            Draw();
        }

        void GuideAudioButton(TraceNote note, float x, float y, float w, float h) =>
            SourceAudioButton(note, x, y, w, h, "GuidePlayAudio",
                guideIsExample || DisplayGuide?.sourceMode == "synthetic" ? "예시 설명 다시 듣기" :
                DisplayGuide?.sourceMode == "replay" ? "기록 음성 다시 듣기" : "교수 설명 다시 듣기", RecordGuideHint);

        void BeginGuideAlignment()
        {
            if (!xr || guideRun?.physical != true) return;
            if (transferCapturing) { notice = "사진 저장이 끝나면 다시 맞춰 주세요."; RefreshStatus(); return; }
            StartGuideAlignment(GuideAlignment.Practice);
        }

        void StartGuideAlignment(GuideAlignment target)
        {
            guideAlignment = target;
            aligned = false;
            worldPoints.Clear();
            BeginPhysicalGuideRegistration(guideRun?.studentBoardInstanceId, guideRun?.registrationRevision ?? 0, guideRun?.trackingEpoch ?? 0);
            if (target == GuideAlignment.Practice)
                GuideRules.InvalidatePracticeBoard(guideRun, PhysicalTrackingEpoch);
            notice = "";
            SetPassthrough(true);
            Recenter();
            Go(ScreenState.Align);
        }

        bool AcceptGuideBoardRegistration(GuideAlignment target, BoardRegistration registration)
        {
            if (TraceData == null) return false;
            if (target == GuideAlignment.Practice &&
                !GuideRules.BindPracticeBoard(guideRun, registration.boardInstanceId, registration.revision,
                 registration.trackingEpoch, registration.profileId, registration.profileRevision, registration.profileHash))
            {
                notice = "승인 가이드의 보드 프로필과 이번 등록을 확인하세요.";
                RefreshStatus();
                return false;
            }
            TraceData.registrations.Add(registration.Copy());
            Save();
            return true;
        }

        void CancelGuideAlignment()
        {
            var target = guideAlignment;
            guideAlignment = GuideAlignment.None;
            worldPoints.Clear();
            ResetPhysicalGuideAlignment(false);
            Recenter();
            if (target == GuideAlignment.Recording)
            {
                SetPassthrough(false);
                Go(ScreenState.Consent);
            }
            else Go(ScreenState.GuidePractice);
        }

        void UpdateGuideTracking()
        {
            if (!xr) return;
            if (guideDisplay == null && OVRManager.display != null)
            {
                guideDisplay = OVRManager.display;
                guideDisplay.RecenteredPose += InvalidateRegisteredPoses;
            }

            if (!guideHasPhysicalMarkers && !(guideRecording && aligned) && guideAlignment == GuideAlignment.None) return;
            var head = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.Head);
            if (!head.isValid || !head.TryGetFeatureValue(UnityEngine.XR.CommonUsages.isTracked, out bool tracked) || !tracked)
                InvalidateGuideAlignment();
        }

        void InvalidateGuideAlignment()
        {
            bool usesGuideAlignment = guideHasPhysicalMarkers || guideRecording || guideAlignment != GuideAlignment.None;
            if (!usesGuideAlignment || (!aligned && worldPoints.Count == 0 && physicalReferenceSamples.Count == 0 && !markerRequestBusy)) return;
            aligned = false;
            worldPoints.Clear();
            ResetPhysicalGuideAlignment(true);
            GuideRules.InvalidatePracticeBoard(guideRun, PhysicalTrackingEpoch);
            if (TraceData != null) Save();
            Recenter();
            notice = "보드 위치를 다시 맞출 때까지 화면의 보드 그림을 사용하세요.";
            Draw();
        }

        void ReleaseGuideTracking()
        {
            if (guideDisplay != null) guideDisplay.RecenteredPose -= InvalidateRegisteredPoses;
        }

        void InvalidateRegisteredPoses()
        {
            InvalidateGuideAlignment();
            InvalidateAssemblyAlignment();
        }

        void DrawGuideStudentMarkers()
        {
            if (!guideHasPhysicalMarkers || guideEvidenceOpen) return;
            var step = GuideRules.Viewed(guideRun);
            if (GuideRules.IsWiringStep(step)) PlacePhysicalGuideTargets(new[] {step.fromPoint, step.toPoint});
        }
    }
}
