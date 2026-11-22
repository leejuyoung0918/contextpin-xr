using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        const float SpatialWidth = 420;
        const float SpatialOriginY = 270;
        bool assemblyCompare, assemblyMenu, assemblyPanelReady, assemblyRecoveryPanel;
        bool movingMeetingPanel, meetingPanelMoved, showJoinKeypad, confirmPaperChange;
        Ray meetingPanelRay;
        float meetingGrabDistance;
        Vector3 meetingGrabOffset;
        string minutesCode;
        long minutesCodeExpiresAt, visibleJoinCodeExpiresAt;
        bool fetchingMinutesCode, savedFollowUpPending, confirmingMeetingEnd;
        bool assemblyClarify, assemblyHistory, assemblyCableRequest;
        int assemblyHistoryPage;
        bool IsCableDemo => meetingSnapshot?["cable_demo"] is JObject && AssemblyRecordingMode != "live" && TextValue(meetingSnapshot, "provider_mode") != "live";
        int CableStage => meetingSnapshot?["cable_demo"]?.Value<int?>("stage") ?? 0;
        bool choosingAlignmentMethod;
        string assemblyCompareRound;
        float assemblyPanelDistance, assemblyPanelScale, assemblyFontScale;
        Pose assemblyPanelPose;
        static readonly Color SpatialBackground = new Color32(17, 36, 34, 245);
        static readonly Color SpatialMuted = new Color32(197, 215, 207, 255);

        void SetupSpatialPanel()
        {
            if (assemblyPanelReady) return;
            assemblyPanelReady = true;
            assemblyHistory = assemblyCableRequest = false;
            var config = RelayEvidence.LoadConfig();
            assemblyPanelDistance = Mathf.Clamp(config.meetingPanelDistanceMeters, .65f, 2f);
            assemblyPanelScale = Mathf.Clamp(config.meetingPanelWidthMeters, .22f, .48f) / SpatialWidth;
            assemblyFontScale = Mathf.Clamp(config.meetingPanelTextScale, .8f, 1.25f);
            var forward = Vector3.ProjectOnPlane(view.transform.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < .1f) forward = Vector3.forward;
            var side = Quaternion.AngleAxis(35, Vector3.up) * forward;
            PlaceSpatialPanel(meetingPanelMoved ? panel.position : view.transform.position + side * assemblyPanelDistance - Vector3.up * .17f);
            if (OVRManager.IsInsightPassthroughSupported()) SetPassthrough(true);
        }

        void PlaceSpatialPanel(Vector3 position)
        {
            var forward = (position - view.transform.position).normalized;
            if (forward.sqrMagnitude < .1f) forward = view.transform.forward;
            assemblyPanelPose = new Pose(position, Quaternion.LookRotation(forward, Vector3.up));
            RestoreSpatialPanel();
        }

        void RestoreSpatialPanel()
        {
            panel.localScale = Vector3.one * assemblyPanelScale;
            panel.SetPositionAndRotation(assemblyPanelPose.position, assemblyPanelPose.rotation);
            assemblyRecoveryPanel = false;
        }

        void UpdateSpatialPanelPosition()
        {
            if (!SpatialAssembly || !assemblyPanelReady) return;
            if (visibleJoinCodeExpiresAt > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= visibleJoinCodeExpiresAt)
            {
                visibleJoinCodeExpiresAt = 0;
                Draw();
            }
            if (!string.IsNullOrEmpty(minutesCode) && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= minutesCodeExpiresAt)
            {
                minutesCode = null;
                assemblyFeedback = "코드 사용 시간이 끝났습니다. 새 코드를 받으세요.";
                Draw();
            }
            if (assemblyAlignment != AssemblyAlignment.Lost || assemblyRecoveryPanel || movingMeetingPanel) return;
            var forward = view.transform.forward;
            panel.SetPositionAndRotation(view.transform.position + forward * .75f - view.transform.up * .10f,
                Quaternion.LookRotation(forward, view.transform.up));
            assemblyRecoveryPanel = true;
        }

        void DrawPanelHandle(float x, float y, float width, float height)
        {
            var handle = Button("MeetingPanelHandle", x, y, width, height, "잡고 옮기기", () => { });
            handle.pointClicked = hitPoint =>
            {
                movingMeetingPanel = true;
                meetingPanelMoved = true;
                meetingGrabDistance = Vector3.Distance(meetingPanelRay.origin, hitPoint);
                meetingGrabOffset = panel.position - hitPoint;
            };
        }

        bool PlaceSpatialPanelFromInput(Ray ray, bool clicked)
        {
            meetingPanelRay = ray;
            if (!movingMeetingPanel) return false;
            bool held = xr ? leftTriggerWasDown || rightTriggerWasDown || wasPressed : Mouse.current?.leftButton.isPressed == true;
            if (!held) { movingMeetingPanel = false; return true; }
            panel.position = ray.GetPoint(meetingGrabDistance) + meetingGrabOffset;
            if (SpatialAssembly) assemblyPanelPose = new Pose(panel.position, panel.rotation);
            return true;
        }

        bool RecallSpatialPanel()
        {
            if (!SpatialAssembly || !assemblyPanelReady) return false;
            movingMeetingPanel = false;
            var direction = Quaternion.AngleAxis(26, Vector3.up) * view.transform.forward;
            PlaceSpatialPanel(view.transform.position + direction * assemblyPanelDistance - Vector3.up * .16f);
            return true;
        }

        void PresentReadyProposal(JToken round)
        {
            if (!SpatialAssembly || CaptureBundleMeeting || !AssemblyActive || collaborativeEditing || assemblyAdopting || assemblyStopping || confirmingMeetingEnd) return;
            var status = TextValue(round, "status");
            if (status is not ("reviewable" or "needs_input")) return;
            if (AssemblyRecordingMode == "live")
            {
                var speech = (round?["context"] as JObject)?["utterance"];
                if (TextValue(speech, "status") != "final" || string.IsNullOrWhiteSpace(TextValue(speech, "text")) ||
                    TextValue(speech, "source_mode") is not ("live_capture" or "manual")) return;
            }
            // Finish an in-progress physical alignment first; the result stays available in the panel.
            if (A4PointSampling || TabletopPointSampling || AssemblySampling || AssemblyQrScanning || assemblyAlignment == AssemblyAlignment.Preview) return;
            assemblyMenu = false;
            OpenSpatialComparison(round);
        }

        int SpatialFont(int size) => Mathf.RoundToInt(size * assemblyFontScale);

        void DrawSpatialMeeting()
        {
            SetupSpatialPanel();
            content = Rect("SpatialAssemblyMeeting", panel, (W - SpatialWidth) / 2f, SpatialOriginY, SpatialWidth, 376);
            var state = AssemblySessionStatus;
            bool sharedGuest = SharedPaperMeeting && !MeetingHost;
            if (SharedPaperMeeting && state is not ("finalizing" or "completed")) DrawPaperParticipants();
            var (status, action) = SpatialSessionAction(state);
            bool pausedActions = !sharedGuest && state == "paused" && !assemblyStarting && !assemblyStopping && !confirmingMeetingEnd;
            float statusWidth = pausedActions ? 112 : 222;
            if (action == null)
            {
                RoundBox("SpatialSessionBadge", content, 0, 0, statusWidth, 47, SpatialBackground, 10);
                BoldLabel("SpatialSessionStatus", content, 6, 0, statusWidth - 12, 47, status, SpatialFont(pausedActions ? 16 : 21), HeaderText, TextAnchor.MiddleCenter);
            }
            else
                Button("SpatialSessionAction", 0, 0, statusWidth, 47, status, action, true);
            if (pausedActions)
                Button("SpatialPausedEnd", 118, 0, 104, 47, "종료·저장", AskToEndSpatialMeeting);

            DrawPanelHandle(238, 0, 182, 47);

            if (confirmingMeetingEnd && !assemblyStopping && (AssemblyActive || assemblyStarting || AssemblyCaptureRequested))
            {
                RoundBox("SpatialEndPanel", content, 0, 58, SpatialWidth, 124, SpatialBackground, 12);
                BoldLabel("SpatialEndHeading", content, 12, 65, 396, 43, "회의를 마치고 결과를 저장할까요?", SpatialFont(21), HeaderText);
                Button("SpatialEndKeep", 12, 119, 189, 43, "계속 검토", () => { confirmingMeetingEnd = false; Draw(); }, true);
                Button("SpatialConfirmEnd", 210, 119, 198, 43, "종료하고 저장", () => { confirmingMeetingEnd = false; EndSpatialMeeting(); });
                return;
            }
            confirmingMeetingEnd = false;
            if (state == "finalizing" || assemblyStopping && state != "completed") return;
            if (state == "completed")
            {
                if (assemblyMenu) DrawSpatialMenu(); else DrawSavedResult();
                return;
            }
            if (PaperLayoutNeedsReset)
            {
                RoundBox("PaperLayoutUpdate", content, 0, 58, SpatialWidth, 95, SpatialBackground, 12);
                Label("PaperLayoutUpdateHint", content, 12, 65, 396, 78,
                    "실물 자리를 비우도록 배치를 갱신하고 있습니다. 갱신 후 같은 A4의 네 점을 다시 짚어 주세요.",
                    SpatialFont(18), HeaderText);
                return;
            }
            if (SharedPaperMeeting && assemblyMenu) { DrawSpatialMenu(); return; }
            if (SharedPaperMeeting && assemblyHistory && PaperRenderable) { DrawSpatialHistory(); return; }
            if (sharedGuest)
            {
                if (PaperLocalStatus != "aligned") DrawA4Alignment();
                else if (HoldCommandMeeting && explicitTranscriptPage >= 0) DrawExplicitTranscript();
                else if (HoldCommandMeeting && explicitRequest?.ButtonWindow == true && !explicitRequest.BundleSaved)
                    DrawExplicitRequestPanel();
                else DrawPaperGuestReview();
                return;
            }
            if (PreparingLivePaperMeeting)
            {
                if (!A4ReviewSurfaceActive) { DrawA4Alignment(); return; }
                RoundBox("SpatialStartPanel", content, 0, 58, SpatialWidth, 145, SpatialBackground, 12);
                DrawLatestCaption(12, 68, 396, 35);
                string hint = AdoptedModelFailed ? "Latest CAD를 다시 불러와 배치를 확인하세요." :
                    !string.IsNullOrEmpty(assemblyFeedback) ? assemblyFeedback :
                    MeetingHost ? "회의 시작을 누르면 지금부터 회의 전체를 전사합니다." : "진행자의 회의 시작을 기다립니다.";
                Label("SpatialStartHint", content, 12, 108, 396, 51, hint, SpatialFont(18), HeaderText);
                Button("A4Restart", 12, 163, 188, 32, "A4 다시 지정", BeginA4Alignment);
                if (AdoptedModelFailed)
                    Button("MeetingRetryModel", 220, 163, 188, 32, "CAD 다시 불러오기", RetryAdoptedModel);
                else if (SharedPaperMeeting)
                    Button("SpatialMenu", 220, 163, 188, 32, "메뉴", () => { assemblyMenu = true; Draw(); });
                return;
            }
            if (ExplicitDesignMeeting && explicitTranscriptPage >= 0 && AssemblyActive) { DrawExplicitTranscript(); return; }
            if (ExplicitDesignMeeting && AssemblyActive && !assemblyMenu &&
                (assemblyAlignment != AssemblyAlignment.Confirmed || !HasReviewTable))
            {
                if (assemblyAlignment != AssemblyAlignment.Confirmed) DrawSpatialAlignment();
                else DrawExplicitRequestPanel();
                return;
            }
            if (assemblyHistory) { DrawSpatialHistory(); return; }
            if (CollaborativeDraftVisible) { DrawCollaborativeEdit(); return; }
            if (assemblyCompare) { DrawSpatialComparison(); return; }
            if (assemblyMenu) { DrawSpatialMenu(); return; }
            if (!CanCompareModels && state != "save_error") { DrawSpatialAlignment(); return; }

            if (ExplicitDesignMeeting && AssemblyActive) { DrawExplicitRequestPanel(); return; }
            var summary = meetingSnapshot["summary"] as JObject;
            var round = MeetingLatestRound;
            var roundState = TextValue(round, "status");
            bool active = AssemblyActive;
            var last = LatestFinalUtterance;
            var speechError = active ? TextValue(meetingSnapshot, "speech_error") : "";
            var speechHint = active ? TextValue(last, "processing_notice") : "";
            if (HasRegisteredDesignScope && speechHint.Contains("부위를 선택")) speechHint = "";
            var feedback = speechError.Length > 0 ? speechError : speechHint.Length > 0 ? speechHint : assemblyFeedback ?? "";
            if (active && roundState == "failed" && speechError.Length == 0) feedback = TextValue(round, "error");
            bool retryModel = A4PoseApplied && AdoptedModelFailed;
            if (retryModel) feedback = "채택 CAD 표시 실패";
            else if (A4PoseApplied && meetingLoadingVersion != null && meetingLoadingVersion == AssemblyAdoptedId)
                feedback = "채택 CAD 불러오는 중";
            float height = feedback.Length > 0 ? 215 : 164;
            RoundBox("SpatialSummary", content, 0, 58, SpatialWidth, height, SpatialBackground, 12);
            var transcript = TextValue(last, "text");
            string source = IsCableDemo || AssemblyRecordingMode == "replay" ? "대본" : TextValue(last, "source_kind") == "manual_input" ? "입력" : "전사";
            string waiting = state == "ready" ? "회의를 시작하세요" : IsCableDemo ? "예시 회의 진행 중" : "확정된 발화를 기다리는 중";
            SpatialSummaryLine(source, 65, transcript, waiting);
            string next = !active ? IsCableDemo ? "예시 회의를 시작하세요" : "녹음 시작 후 바꿀 점을 말하세요" : IsCableDemo ?
                CableStage <= 2 ? TextValue(meetingSnapshot["cable_demo"], "target_label") + "을 가리키세요" : "두 번의 설계 변경을 마쳤습니다" :
                TextValue(meetingSnapshot?["meeting"], "input_mode") == "manual" ? "PC에서 바꿀 치수를 입력하세요" :
                HasSelectedDesignTarget ? "선택한 부위를 어떻게 바꿀지 말하세요" : "어떤 부분을 어떻게 바꿀지 말하세요";
            SpatialSummaryLine("안내", 99, next, "시제품과 겹쳐진 원본을 확인하세요");
            SpatialSummaryLine("결정", 131, TextValue(summary, "decision"), "아직 미정");
            if (active && AssemblyRecordingMode == "live")
                DrawLiveDesignAction(round, 175);
            else if (active && (roundState is "collecting" or "generating" or "building" || assemblyCableRequest))
                Label("SpatialDesignStatus", content, 12, 175, 298, 37, "CAD 수정안 준비 중…", SpatialFont(19), SpatialMuted);
            else if (active && roundState is ("reviewable" or "needs_input"))
                Button("SpatialOpenComparison", 10, 175, 298, 37, roundState == "needs_input" ? "확인 질문 열기" : "수정안 다시 보기", () => OpenSpatialComparison(round), true);
            else if (active && IsCableDemo && CableStage > 2)
                Button("SpatialHistory", 10, 175, 298, 37, "설계 이력 보기", OpenSpatialHistory);
            else if (active && IsCableDemo)
                Button("SpatialCableStage", 10, 175, 298, 37, TextValue(meetingSnapshot["cable_demo"], "action"), () => RequestCableDemoStage(), true);
            else if (GroovePreviewAvailable)
                DrawGroovePreviewControl();
            Button("SpatialMenu", 330, 175, 80, 37, "메뉴", () => { assemblyMenu = true; Draw(); });
            if (feedback.Length > 0)
                Label("SpatialFeedback", content, 12, 219, retryModel ? 268 : 396, 48, TruncateSpatial(feedback, "", 70), SpatialFont(17), SpatialMuted);
            if (retryModel) Button("MeetingRetryModel", 291, 223, 116, 38, "다시 불러오기", RetryAdoptedModel);
            DrawTablePlacementControls(height + 65, showCurrentVersion: true);
        }

        bool AdoptedModelFailed => !string.IsNullOrEmpty(AssemblyAdoptedId) && meetingFailedVersion == AssemblyAdoptedId;

        void DrawPaperParticipants()
        {
            if (PaperRenderable && OffsetPaperGallery && LatestPoseRevision >= 0)
            {
                string hint = MeetingHost
                    ? (string.IsNullOrEmpty(paperPoseSetId) ? "Grip · Latest 이동   |   A · 선택" : "후보 검토 중 · A로 채택")
                    : "A · 지점 공유/후보 제안   |   Grip 이동은 진행자만";
                if (HoldCommandMeeting && AssemblySessionStatus == "recording" && string.IsNullOrEmpty(paperPoseSetId))
                    hint = "B 길게 · 수정 발언   |   " + hint;
                if (!IntegratedPaperControls || CurrentPaperInteraction == PaperInteraction.LatestDisplayed)
                {
                    RoundBox("PaperInputHint", content, 0, -72, SpatialWidth, 26, SpatialBackground, 7);
                    Label("PaperInputLabel", content, 8, -72, 404, 26, hint, SpatialFont(15), HeaderText);
                }
            }
            RoundBox("PaperParticipants", content, 0, -43, SpatialWidth, 36, SpatialBackground, 9);
            string alignment = PaperLocalStatus switch {
                "aligned" => "공간 정렬 완료", "lost" => "공간 정렬 필요", "calibrating" => "A4 정렬 중", _ => "공간 정렬 필요"
            };
            int count = Values(meetingSnapshot, "participants").Count();
            Label("PaperAlignment", content, 10, -43, MeetingHost ? 235 : 398, 36, $"{alignment} · 참가자 {count}명", SpatialFont(16), HeaderText);
            if (!MeetingHost) return;
            var code = TextValue(meetingSnapshot, "join_code");
            long expiry = meetingSnapshot.Value<long?>("join_expires_at") ?? 0;
            if (code.Length > 0 && expiry > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            {
                visibleJoinCodeExpiresAt = expiry;
                BoldLabel("SpatialJoinCode", content, 240, -43, 170, 36, "참가 코드 " + code, SpatialFont(16), HeaderText, TextAnchor.MiddleRight);
            }
            else
                Button("MeetingRenewCode", 246, -42, 161, 33, "새 참가 코드", () => SendMeetingCommand("renew_code", new JObject()));
        }

        void DrawPaperGuestReview()
        {
            var card = RoundBox("PaperGuestReview", content, 0, 58, SpatialWidth, 125, SpatialBackground, 12);
            DrawLatestCaption(12, 65, 288, 32);
            Button("SpatialMenu", 327, 65, 82, 34, "메뉴", () => { assemblyMenu = true; Draw(); });
            var set = CurrentCandidateSet;
            string hint = !PaperSessionReady ? "진행자가 같은 A4의 모델 배치를 확인하면 여기에 표시됩니다." :
                !PaperRenderable ? "공간 정렬 상태를 서버와 맞추고 있습니다." :
                AdoptedModelFailed || BundleAssetsFailed ? "CAD를 불러오지 못했습니다." :
                IntegratedPaperControls && CurrentPaperInteraction != PaperInteraction.LatestDisplayed ? PaperInteractionHint() :
                meetingLoadedVersion != AssemblyAdoptedId ? "현재 채택된 CAD를 불러오는 중입니다." :
                TextValue(set, "status") == "ready" ? "후보를 가리키고 A를 누르면 진행자에게 제안합니다." :
                TextValue(set, "status") is ("generating" or "building") ? "수정안 생성 중…" :
                HoldCommandMeeting && AssemblySessionStatus == "recording" ? explicitHint ?? (IntegratedPaperControls ? PaperInteractionHint() :
                    "B를 누른 채 수정 요청을 말하세요. 회의 마이크가 함께 듣습니다.") :
                "Latest의 한 지점을 가리키고 오른손 A를 눌러 함께 보세요.";
            Label("PaperGuestHint", content, 12, 105, 396, 53, hint, SpatialFont(18), HeaderText);
            float details = PaperRenderable ? DrawBundleCandidateInfo(set, 164) : 0;
            if (AdoptedModelFailed || BundleAssetsFailed)
            {
                Button("MeetingRetryModel", 12, 164 + details, 200, 37, "CAD 다시 불러오기", RetryAdoptedModel);
                details += 45;
            }
            card.rectTransform.sizeDelta = new Vector2(SpatialWidth, 125 + details);
        }

        string ShownLatestLabel()
        {
            if (meetingModel == null || !meetingModel.activeInHierarchy || string.IsNullOrEmpty(meetingLoadedVersion))
                return "현재 모델 표시 대기";
            string label = TextValue(MeetingVersion(meetingLoadedVersion), "design_version");
            return "Latest · " + (label.Length == 0 ? MeetingVersionLabel(meetingLoadedVersion) : label);
        }

        string ExplicitTranscriptLabel => explicitRequest?.TranscriptSource switch
        {
            "manual" => "수동 전사",
            "corrected" => "교정된 수정 발언",
            _ => "수정 발언"
        };

        void DrawLatestCaption(float x, float y, float width, float height)
        {
            var caption = ShownLatestLabel();
            if ((PreparingLivePaperMeeting || CandidateGenerationMeeting) && A4PoseApplied && width >= 230)
            {
                var swatch = PaperLatestColor;
                swatch.a = 1;
                RoundBox("LatestColor", content, x, y + height / 2 - 5, 10, 10, swatch, 5);
                x += 16;
                width -= 16;
                if (meetingModel != null && meetingModel.activeInHierarchy) caption += " · 채택본 / " + PaperLatestPosition;
            }
            Label("ExplicitLatest", content, x, y, width, height, caption, SpatialFont(16), SpatialMuted);
        }

        void RetryAdoptedModel()
        {
            if (BundleAssetsFailed)
            {
                bundleReviewError = null;
                for (int i = 0; i < bundleCandidates.Length; i++)
                    if (bundleSlots[i].FailedVersion != null)
                    {
                        bundleSlots[i].FailedVersion = null;
                        QueueBundleSlot(i);
                    }
                Draw();
                return;
            }
            if (!AdoptedModelFailed) return;
            meetingFailedVersion = null;
            QueueMeetingModel(RequestedMeetingVersion());
            Draw();
        }

        void DrawExplicitRequestPanel()
        {
            var background = RoundBox("ExplicitDesignPanel", content, 0, 58, SpatialWidth, 219, SpatialBackground, 12);
            DrawLatestCaption(12, 64, 292, 30);
            Button("SpatialMenu", 330, 63, 80, 34, "메뉴", () => { assemblyMenu = true; Draw(); });
            var guidance = HasReviewTable ? ExplicitDesignStatus : "모델을 놓을 책상을 확인하거나 아래에서 직접 지정하세요.";
            bool retry = HasReviewTable && (AdoptedModelFailed || BundleAssetsFailed);
            if (retry && AdoptedModelFailed) guidance = bundleReviewError ?? "채택한 CAD를 불러오지 못했습니다.";
            else if (HasReviewTable && meetingLoadingVersion != null && meetingLoadingVersion == AssemblyAdoptedId)
                guidance = "채택한 CAD를 불러오는 중입니다.";
            Label("ExplicitDesignStatus", content, 12, 99, retry ? 269 : 396, 48, guidance, SpatialFont(18), HeaderText);
            if (retry)
                Button("MeetingRetryModel", 291, 102, 116, 38, "다시 불러오기", RetryAdoptedModel);
            float details = DrawBundleCandidateInfo(CurrentCandidateSet, 147);
            background.rectTransform.sizeDelta = new Vector2(SpatialWidth, 219 + details);
            Label("ExplicitCameraStatus", content, 12, 147 + details, 278, 34, ExplicitCameraStatus, SpatialFont(16), SpatialMuted);
            if (HasReviewTable && !ExplicitCameraReady)
                Button("ExplicitPrepareCamera", 292, 146 + details, 115, 34, "카메라 준비", () => StartExplicitCamera(true));
            var transcript = ExplicitTranscript;
            string recent = TextValue(LatestFinalUtterance, "text");
            string caption = transcript.Length > 0 ? ExplicitTranscriptLabel + " · " + transcript :
                CaptureBundleMeeting && recent.Length > 0 ? "최근 회의 발언 · " + recent : "확정 전사를 기다립니다.";
            Label("ExplicitTranscript", content, 12, 184 + details, 396, 38,
                TruncateSpatial(caption, "", 74), SpatialFont(17), HeaderText);
            if (!string.IsNullOrEmpty(transcript))
                Button("ExplicitReadTranscript", 300, 224 + details, 107, 40, "원문 보기", () => { explicitTranscriptPage = 0; Draw(); });
            string speech = ExplicitSpeechAction;
            if (HasReviewTable && !string.IsNullOrEmpty(speech))
                Button("ExplicitSpeechAction", 10, 224 + details, 155, 40, speech, ToggleExplicitSpeech, true);
            if (explicitRequest?.ButtonWindow == true && explicitRequest.Speech == DesignSpeech.Final &&
                explicitRequest.Selection == null && !explicitRequest.Sending && !explicitRequest.Conflict)
                Button("ExplicitCancelSpeech", 10, 224 + details, 110, 40, "요청 취소", CancelHeldCommand);
            if (CanRetryExplicitRequest)
                Button("ExplicitRetry", 173, 224 + details, 120, 40, "다시 시도", RetryExplicitRequest);
            if (!SharedPaperMeeting || MeetingHost) DrawTablePlacementControls(282 + details);
            if (!CaptureBundleMeeting && !string.IsNullOrEmpty(TextValue(MeetingLatestRound, "round_id")) &&
                TextValue(MeetingLatestRound, "status") is "reviewable" or "needs_input")
                Button("SpatialOpenComparison", 10, 344, 206, 38, "최근 수정안 열기", () => OpenSpatialComparison(MeetingLatestRound));
        }

        float DrawBundleCandidateInfo(JObject set, float top)
        {
            if (set == null) return 0;
            var rows = new List<(string text, string candidateKey)>();
            var candidates = Values(set, "candidates").ToArray();
            if (TextValue(set, "status") == "ready")
            {
                var spatial = meetingSnapshot?["spatial"];
                string proposed = SharedPaperMeeting ? TextValue(spatial, "selected_candidate_id") : "";
                string participant = TextValue(spatial, "selected_by_participant_id");
                foreach (var candidate in candidates)
                {
                    string values = string.Join(", ", Values(candidate, "parameter_diff").Select(change =>
                        $"{TextValue(change, "label")} {change["before"]} → {change["after"]} {TextValue(change, "unit")}".TrimEnd()));
                    string key = TextValue(candidate, "candidate_key");
                    string side = LatestPoseRevision > 0 ? (key == "A" ? "비교 줄 왼쪽" : "비교 줄 오른쪽") :
                        PublishedCenterLayout ? (key == "A" ? "①·④ 쪽" : "②·③ 쪽") :
                        OffsetPaperGallery ? (key == "A" ? "④ 너머" : "③ 너머") :
                        (key == "A" ? "뒤왼쪽" : "뒤오른쪽");
                    string suggestion = proposed.Length > 0 && proposed == TextValue(candidate, "candidate_id")
                        ? (participant == meetingParticipant ? " · 내가 제안함" : " · 참가자가 제안함") : "";
                    rows.Add(($"후보 {key}{suggestion} · {side} · {values}\n{TextValue(candidate, "interpretation")}", key));
                }
            }
            string reason = TextValue(set, "reason");
            var limits = Values(set, "limitations").Values<string>().Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToArray();
            if (!string.IsNullOrWhiteSpace(reason) && reason != string.Join(" ", limits)) rows.Add((reason, null));
            if (limits.Length > 0)
                rows.Add(((candidates.Length == 1 ? "후보가 1개인 이유 · " : "") + string.Join("\n", limits), null));
            float y = top;
            foreach (var (text, key) in rows)
            {
                float indent = 0;
                if (key != null)
                {
                    var swatch = BundleColor(key);
                    swatch.a = 1;
                    RoundBox("CandidateColor" + key, content, 12, y + 6, 10, 10, swatch, 5);
                    indent = 16;
                }
                var label = Label("CandidateDetail", content, 12 + indent, y, 396 - indent, 24, text, SpatialFont(16), HeaderText);
                float height = Mathf.Max(24, Mathf.Ceil(label.preferredHeight) + 4);
                label.rectTransform.sizeDelta = new Vector2(396 - indent, height);
                y += height + 3;
            }
            return y - top;
        }

        int explicitTranscriptPage = -1;
        void DrawExplicitTranscript()
        {
            const int pageSize = 150;
            var text = ExplicitTranscript ?? "";
            var pages = Mathf.Max(1, Mathf.CeilToInt(text.Length / (float)pageSize));
            explicitTranscriptPage = Mathf.Clamp(explicitTranscriptPage, 0, pages - 1);
            int start = explicitTranscriptPage * pageSize;
            RoundBox("ExplicitTranscriptPage", content, 0, 58, SpatialWidth, 240, SpatialBackground, 12);
            string title = ExplicitTranscriptLabel + " 원문";
            BoldLabel("Title", content, 12, 65, 396, 33, $"{title}  {explicitTranscriptPage + 1}/{pages}", SpatialFont(20), HeaderText);
            Label("FullText", content, 12, 102, 396, 126, text.Substring(start, Mathf.Min(pageSize, text.Length - start)), SpatialFont(17), HeaderText);
            if (explicitTranscriptPage > 0) Button("Previous", 12, 246, 103, 36, "이전", () => { explicitTranscriptPage--; Draw(); });
            if (explicitTranscriptPage + 1 < pages) Button("Next", 123, 246, 100, 36, "다음", () => { explicitTranscriptPage++; Draw(); });
            Button("CloseTranscript", 288, 246, 119, 36, "닫기", () => { explicitTranscriptPage = -1; Draw(); });
        }

        void DrawLiveDesignAction(JToken round, float y)
        {
            var status = TextValue(round, "status");
            if (status is "collecting" or "generating" or "building" || designRequestPending)
                Label("SpatialDesignStatus", content, 12, y, 305, 40, "CAD 수정안 준비 중…", SpatialFont(20), HeaderText);
            else if (status is "reviewable" or "needs_input")
                Button("SpatialOpenComparison", 10, y, 300, 40, status == "needs_input" ? "확인 질문 열기" : "수정안 보기",
                    () => OpenSpatialComparison(round), true);
            else if (targetSnapshotPending)
                Label("SpatialDesignStatus", content, 12, y, 305, 40, "선택 부위 저장 중…", SpatialFont(19), SpatialMuted);
            else if (CurrentDesignUtterance != null)
                Button("SpatialDesignRequest", 10, y, 300, 40, "이 발언으로 수정안 보기", RequestSelectedDesign, true);
            else
                Label("SpatialDesignStatus", content, 12, y, 305, 40, "확정된 수정 발언을 기다리는 중", SpatialFont(18), SpatialMuted);
        }

        void DrawSavedResult()
        {
            var source = meetingSnapshot["saved_design_utterance"] as JObject;
            if (MinutesStatus is "generated" or "generation_failed")
            {
                RoundBox("SpatialSavedPanel", content, 0, 58, SpatialWidth, 112, SpatialBackground, 12);
                string hint = MinutesStatus == "generation_failed" ? "전사와 CAD는 서버에 저장했습니다." :
                    Values(meetingSnapshot["result"], "missing").Any() ? "누락된 자료는 결과 ZIP에서 확인하세요." :
                    "PC 회의 화면에서 코드를 입력하세요.";
                DrawResultDownload(68, hint);
                return;
            }
            RoundBox("SpatialSavedPanel", content, 0, 58, SpatialWidth, 266, SpatialBackground, 12);
            BoldLabel("SpatialSavedHeading", content, 12, 64, 396, 31, source != null ? "저장된 수정 발언" : "저장된 회의", SpatialFont(20), SpatialMuted);
            Label("SpatialSavedUtterance", content, 12, 98, 396, 51,
                TruncateSpatial(TextValue(source ?? LatestFinalUtterance, "text"), "확정 발언이 없습니다.", 75), SpatialFont(22), HeaderText);
            bool live = AssemblyRecordingMode == "live";
            if (source != null)
            {
                if (savedFollowUpPending)
                    Label("SpatialSavedDesignStatus", content, 12, 153, 396, 45, "저장된 발언으로 수정안 준비 중…", SpatialFont(20), HeaderText);
                else Button("SpatialSavedDesign", 12, 153, 396, 45, "이 발언으로 수정안 보기", ContinueSavedDesign, true);
            }
            else if (live)
                Label("SpatialSavedDesignStatus", content, 12, 153, 396, 45, "수정 요청으로 사용할 발언이 없습니다.", SpatialFont(18), SpatialMuted);

            DrawResultDownload(source != null || live ? 207 : 160, source != null ?
                "이 발언과 현재 CAD로 후속 검토를 엽니다." : "PC 회의 화면에서 회의록 코드를 입력하세요.");
        }

        string MinutesStatus => TextValue(meetingSnapshot?["result"], "minutes_status");

        void DrawResultDownload(float y, string hint)
        {
            if (!string.IsNullOrEmpty(minutesCode))
                BoldLabel("SpatialMinutesCode", content, 12, y, 300, 39, "회의록 코드  " + minutesCode, SpatialFont(21), HeaderText);
            else if (!string.IsNullOrEmpty(AssemblyResultUrl))
                Button("SpatialMinutesDownload", 12, y, 296, 39, fetchingMinutesCode ? "코드 확인 중…" :
                    MinutesStatus == "generation_failed" ? "PC에서 회의 자료 받기" : "PC에서 회의록 받기", FetchMinutesCode);
            Button("SpatialMenu", 325, y, 83, 39, "메뉴", () => { assemblyMenu = true; Draw(); });
            Label("SpatialFeedback", content, 12, y + 42, 396, 62, TruncateSpatial(assemblyFeedback, hint, 80), SpatialFont(17), SpatialMuted);
        }

        void AskToEndSpatialMeeting()
        {
            confirmingMeetingEnd = true;
            Draw();
        }

        void FetchMinutesCode()
        {
            if (fetchingMinutesCode || meetingTransport == null) return;
            var transport = meetingTransport;
            fetchingMinutesCode = true;
            StartCoroutine(transport.Json(transport.SessionPath + "/minutes-code", null, (reply, error) =>
            {
                if (transport != meetingTransport) return;
                fetchingMinutesCode = false;
                minutesCode = TextValue(reply, "code");
                minutesCodeExpiresAt = reply?.Value<long?>("expires_at") ?? 0;
                if (minutesCode.Length == 0) assemblyFeedback = error ?? "다운로드 코드를 받지 못했습니다. 다시 시도하세요.";
                else
                {
                    int remaining = Math.Max(1, (int)Math.Ceiling((minutesCodeExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 60000.0));
                    assemblyFeedback = $"PC 회의 화면에서 코드를 입력하세요. 약 {remaining}분간 사용할 수 있습니다.";
                }
                Draw();
            }));
            Draw();
        }

        (string label, Action action) SpatialSessionAction(string state)
        {
            if (assemblyStopping && CaptureBundleMeeting && explicitRequest?.Selection != null && !explicitRequest.BundleSaved &&
                !explicitRequest.RetryTransfer && !explicitRequest.Conflict && state != "completed") return ("마지막 수정 요청 저장 중", null);
            if (assemblyStopping || state == "finalizing") return ("회의 정리 중…", null);
            if (state == "completed")
            {
                if (MinutesStatus == "generated") return ("회의록 저장 완료", null);
                if (MinutesStatus == "generation_failed") return ("회의록 생성 실패", null);
                return (Values(meetingSnapshot?["result"], "missing").Any() ? "누락 포함 저장됨" : "저장됨", null);
            }
            if (SharedPaperMeeting && !MeetingHost)
            {
                if (!meetingEventsConnected && !meetingHttpAvailable) return ("서버 연결 대기", null);
                return (state switch { "recording" => "회의 진행 중", "paused" => "회의 일시 정지",
                    "save_error" => "회의 저장 오류", _ => "진행자 대기" }, null);
            }
            if (state == "save_error") return ("저장 다시 시도", RetrySpatialSave);
            if (RequestedOutputUnavailable && !assemblyStarting && !string.IsNullOrEmpty(assemblyFeedback))
                return ("저장 모드 연결 다시 시도", StartSpatialMeeting);
            if (state == "recording")
            {
                if (!meetingEventsConnected && !meetingHttpAvailable) return ("서버 연결 대기 · 종료", AskToEndSpatialMeeting);
                if (AssemblyRecordingMode == "replay") return ("예시 진행 · 회의 종료", () => EndSpatialMeeting());
                var capture = TextValue(meetingSnapshot, "capture_state");
                if (TextValue(meetingSnapshot?["meeting"], "input_mode") == "manual") return ("설계 검토 · 종료", AskToEndSpatialMeeting);
                var confirmedVoice = Values(meetingSnapshot, "utterances").Any(u => TextValue(u, "status") == "final" && TextValue(u, "source_mode") == "live_capture");
                if (TextValue(meetingSnapshot, "speech_error").Length > 0) return ("전사 오류 · 종료", AskToEndSpatialMeeting);
                string badge = capture == "recording" ? confirmedVoice ? "녹음·전사 중" : "녹음 중 · 전사 대기" : "마이크 미연결";
                return (badge + " · 종료", AskToEndSpatialMeeting);
            }
            if (assemblyStarting || TextValue(meetingSnapshot, "capture_state") == "starting")
                return ("마이크 연결 중 · 종료", AskToEndSpatialMeeting);
            if (state == "paused")
            {
                if (LivePaperMeeting && !PaperReadyToRecord) return ("A4 배치 대기", null);
                return ("녹음 재개", StartSpatialMeeting);
            }
            if (AssemblyCaptureRequested && TextValue(meetingSnapshot?["meeting"], "audio_device_id") != meetingDeviceId) return ("회의 마이크 연결 중", null);
            if (PreparingLivePaperMeeting)
            {
                if (!MeetingHost) return ("진행자 대기", null);
                if (!PaperReadyToRecord) return ("A4 배치 확인 대기", null);
                return ("회의 시작", StartSpatialMeeting);
            }
            return (TextValue(meetingSnapshot, "provider_mode") == "live" ? "회의 시작 · 녹음" : "예시 회의 시작", StartSpatialMeeting);
        }

        void RetrySpatialSave()
        {
            if (assemblyStopping) return;
            assemblyStopping = true;
            StartCoroutine(RunAssemblyAction(meetingTransport, "meeting_retry", new JObject(), (_, error) =>
            {
                assemblyStopping = false;
                assemblyFeedback = error ?? "";
            }));
            Draw();
        }

        static string TruncateSpatial(string value, string fallback, int limit = 31)
        {
            var compact = string.IsNullOrWhiteSpace(value) ? fallback ?? "" : value.Replace('\n', ' ').Trim();
            return compact.Length <= limit ? compact : compact.Substring(0, limit - 1) + "…";
        }

        void SpatialSummaryLine(string name, float y, string value, string fallback)
        {
            BoldLabel("SpatialLabel" + name, content, 12, y, 55, 27, name, SpatialFont(19), SpatialMuted);
            Label("SpatialValue" + name, content, 72, y, 335, 27, TruncateSpatial(value, fallback), SpatialFont(20), HeaderText);
        }

        void ShowAlignmentMethods()
        {
            choosingAlignmentMethod = true;
            ResetA4ProbeStability();
            Draw();
        }

        void DrawAlignmentMethods()
        {
            RoundBox("SpatialAlignment", content, 0, 58, SpatialWidth, 229, SpatialBackground, 12);
            BoldLabel("SpatialAlignmentStatus", content, 12, 66, 396, 37, "다른 정렬 방법", SpatialFont(22), HeaderText);
            Button("TabletopCaptureStart", 12, 115, 188, 47, "책상 한 점 지정", () => { choosingAlignmentMethod = false; BeginTabletopAlignment(); });
            if (assemblyHasMount)
                Button("AssemblyAlignmentStart", 212, 115, 195, 47, "기준점 세 개", () => { choosingAlignmentMethod = false; BeginAssemblyAlignment(); });
            if (AssemblyQrAvailable)
                Button("AssemblyQrStart", 12, 177, 188, 47, "받침 QR 읽기", () => { choosingAlignmentMethod = false; BeginAssemblyQrAlignment(); });
            Button("A4Return", 212, 177, 195, 47, "A4로 돌아가기", () =>
            {
                choosingAlignmentMethod = false;
                if (A4PointSampling || A4PreviewActive) Draw(); else BeginA4Alignment();
            }, true);
        }

        bool AlignmentPassthroughAvailable()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!OVRManager.IsInsightPassthroughSupported())
            {
                BoldLabel("SpatialAlignmentStatus", content, 12, 66, 396, 40, "실물 보기 대기", SpatialFont(22), HeaderText);
                Label("SpatialAlignmentHint", content, 12, 112, 396, 72, "헤드셋의 패스스루를 사용할 수 있어야 실물과 원본을 맞출 수 있습니다.", SpatialFont(18), SpatialMuted);
                return false;
            }
#endif
            return true;
        }

        static readonly string[] A4CornerLabels = { "①", "②", "③", "④" };

        void DrawA4Alignment()
        {
            RoundBox("SpatialAlignment", content, 0, 58, SpatialWidth, 285, SpatialBackground, 12);
            if (!AlignmentPassthroughAvailable()) return;
            if (A4PreviewActive)
            {
                BoldLabel("SpatialAlignmentStatus", content, 12, 67, 396, 38, "실물을 A4 중앙에 놓으세요", SpatialFont(23), HeaderText);
                Label("SpatialAlignmentHint", content, 12, 108, 396, 75,
                    LatestPoseRevision > 0 ? "진행자가 놓은 위치에 청록색 Latest가 보이는지, 바닥과 방향이 책상에 맞는지 확인하세요." :
                    OffsetPaperGallery ? latestPaperError ?? "Latest는 중심에서 ②·③ 쪽으로 24cm에 놓입니다. ④·③ 너머의 후보 자리도 같은 높이의 책상 위인지 확인하세요." :
                    CandidateGenerationMeeting ? latestPaperError ?? "중앙은 실물 자리입니다. 앞오른쪽 청록색 Latest의 바닥과 방향을 확인하세요." :
                        "오른쪽 앞의 가상 CAD와 중앙 실물의 바닥과 방향을 확인한 뒤 트리거로 배치를 확인하세요.",
                    SpatialFont(19), HeaderText);
                DrawLatestCaption(12, 184, 395, 26);
                if (OriginalOverlayLoaded)
                    Button("AssemblyAlignmentConfirm", 12, 217, 395, 47, "배치 확인", ConfirmAssemblyAlignment, true);
                else if (AdoptedModelFailed)
                {
                    Label("SpatialOriginalLoading", content, 12, 217, 239, 47, "채택 CAD 표시 실패", SpatialFont(18), SpatialMuted);
                    Button("MeetingRetryModel", 259, 217, 148, 47, "다시 불러오기", RetryAdoptedModel);
                }
                else
                    Label("SpatialOriginalLoading", content, 12, 217, 395, 47, "표시할 CAD를 불러오는 중입니다.", SpatialFont(18), SpatialMuted);
                Button("A4Undo", 12, 280, 190, 43, "마지막 점 다시", UndoA4Corner);
                Button("A4Restart", 215, 280, 192, 43, "처음부터 다시", BeginA4Alignment);
                return;
            }
            if (!A4PointSampling)
            {
                string title = assemblyAlignment == AssemblyAlignment.Lost ? "같은 A4에 다시 맞춰 주세요" : "번호를 표시한 A4를 준비하세요";
                BoldLabel("SpatialAlignmentStatus", content, 12, 67, 396, 40, title, SpatialFont(22), HeaderText);
                Label("SpatialAlignmentHint", content, 12, 116, 396, 85, "모두 같은 A4의 ①→②→③→④를 짚습니다. 한쪽에 서서 왼쪽 아래에 ①, 오른쪽 아래에 ②, 오른쪽 위에 ③, 왼쪽 위에 ④를 적어 두세요.", SpatialFont(17), SpatialMuted);
                Button("A4Start", 12, 225, 190, 47, "A4 네 점 지정", BeginA4Alignment, true);
                if (SharedPaperMeeting)
                    Button("PaperAlignmentMenu", 215, 225, 192, 47, "메뉴", () => { assemblyMenu = true; Draw(); });
                else Button("A4AlignmentMethods", 215, 225, 192, 47, "다른 정렬 방법", ShowAlignmentMethods);
                return;
            }

            int corner = Mathf.Clamp(A4CornerCount, 0, 3);
            BoldLabel("SpatialAlignmentStatus", content, 12, 66, 396, 40,
                $"A4 정렬 {corner + 1}/4 · 종이의 {A4CornerLabels[corner]}", SpatialFont(23), HeaderText);
            string instruction = corner == 0 ? "모두 같은 A4의 ①→②→③→④를 짚습니다. 종이에 적힌 ①에 오른손 표시점을 대고 A를 누르세요." :
                $"종이에 적힌 {A4CornerLabels[corner]}에 오른손 표시점을 대고 A를 누르세요.";
            Label("SpatialAlignmentHint", content, 12, 110, 256, 102, instruction, SpatialFont(18), HeaderText);
            DrawA4CornerMap(corner);
            string status = !tabletopTipTracked ? "오른쪽 컨트롤러와 헤드셋의 추적을 확인하세요." :
                !string.IsNullOrWhiteSpace(A4Status) ? A4Status :
                a4TipStable ? "표시점이 안정됐습니다. A를 누르세요." : "모서리에서 표시점을 잠깐 멈춰 주세요.";
            Label("A4ProbeStatus", content, 12, 218, 396, 54, status, SpatialFont(17), SpatialMuted);
            if (corner > 0)
            {
                Button("A4Undo", 12, 281, 121, 44, "한 점 취소", UndoA4Corner);
                Button("A4Restart", 143, 281, 126, 44, "처음부터", BeginA4Alignment);
            }
            if (SharedPaperMeeting)
                Button("PaperAlignmentMenu", 279, 281, 128, 44, "메뉴", () => { assemblyMenu = true; Draw(); });
            else Button("A4AlignmentMethods", 279, 281, 128, 44, "다른 방법", ShowAlignmentMethods);
        }

        void DrawA4CornerMap(int current)
        {
            Box("A4GuideSheet", content, 288, 125, 109, 76, Field);
            for (int i = 0; i < A4CornerLabels.Length; i++)
            {
                float x = i == 1 || i == 2 ? 374 : 282;
                float y = i < 2 ? 180 : 117;
                var mark = RoundBox("A4GuideCorner" + i, content, x, y, 29, 29,
                    i == current ? Lime : i < current ? Mint : Field, 9);
                BoldLabel("Index", mark.transform, 0, 0, 29, 29, (i + 1).ToString(), SpatialFont(16),
                    i < current ? Card : Light, TextAnchor.MiddleCenter);
            }
            Label("A4GuideFront", content, 308, 175, 66, 25, "+X →", SpatialFont(15), Light, TextAnchor.MiddleCenter);
        }

        void DrawTabletopAlignment()
        {
            RoundBox("SpatialAlignment", content, 0, 58, SpatialWidth, 229, SpatialBackground, 12);
            if (!AlignmentPassthroughAvailable()) return;
            if (TabletopPreviewActive)
            {
                BoldLabel("SpatialAlignmentStatus", content, 12, 65, 396, 37, "실물 배치 확인", SpatialFont(22), HeaderText);
                var hint = TabletopNeedsSpaceConfirmation
                    ? "원본이 실물에 겹치고 오른쪽 윤곽선 전체가 빈 책상 위에 있는지 확인하세요."
                    : "반투명 원본이 실물과 같은 위치와 방향에 겹치는지 확인하세요.";
                Label("SpatialAlignmentHint", content, 12, 109, 396, 63, hint, SpatialFont(18), SpatialMuted);
                Button("TabletopRotateLeft", 12, 177, 116, 41, "왼쪽 5°", () => RotateTabletopPreview(-5));
                Button("TabletopRotateRight", 138, 177, 116, 41, "오른쪽 5°", () => RotateTabletopPreview(5));
                Button("TabletopRecapture", 264, 177, 143, 41, "위치 다시 짚기", BeginTabletopAlignment);
                if (OriginalOverlayLoaded)
                    Button("AssemblyAlignmentConfirm", 12, 230, 395, 45, "배치 확인", ConfirmAssemblyAlignment, true);
                else Label("SpatialOriginalLoading", content, 12, 230, 395, 45, "원본 모델을 불러오는 중입니다.", SpatialFont(17), SpatialMuted);
                return;
            }
            bool lost = assemblyAlignment == AssemblyAlignment.Lost;
            BoldLabel("SpatialAlignmentStatus", content, 12, 65, 396, 37, lost ? "위치를 다시 짚어 주세요" : "책상 한 점으로 배치", SpatialFont(22), HeaderText);
            Label("SpatialAlignmentHint", content, 12, 108, 396, 85,
                "부품의 긴 방향을 좌우로 놓고, 앞면 가운데 바로 앞의 상판에 오른손 표시점을 대고 A를 누르세요.", SpatialFont(18), SpatialMuted);
            if (TabletopPointSampling)
            {
                string status = !TryGetTabletopOriginalBounds(out _) ? "원본 모델을 불러오는 중" :
                    tabletopTipTracked ? "오른손 표시점 준비됨" : "오른쪽 컨트롤러 추적 대기";
                Label("TabletopProbeStatus", content, 12, 202, 227, 54, status, SpatialFont(16), SpatialMuted);
            }
            else Button("TabletopCaptureStart", 12, 208, 203, 46, "책상 한 점 지정", BeginTabletopAlignment, true);
            Button("TabletopAlignmentMethods", 235, 208, 172, 46, "다른 정렬 방법", ShowAlignmentMethods);
        }

        void DrawSpatialAlignment()
        {
            if (choosingAlignmentMethod) { DrawAlignmentMethods(); return; }
            if (!ReviewTableSampling && (A4PointSampling || A4PreviewActive ||
                (ExplicitDesignMeeting && assemblyAlignment is (AssemblyAlignment.Unaligned or AssemblyAlignment.Lost))))
            {
                DrawA4Alignment();
                return;
            }
            if (!ReviewTableSampling && (TabletopPointSampling || TabletopPreviewActive))
            {
                DrawTabletopAlignment();
                return;
            }
            bool live = AssemblyActive && AssemblyRecordingMode == "live" && !ExplicitDesignMeeting;
            RoundBox("SpatialAlignment", content, 0, 58, SpatialWidth, 138, SpatialBackground, 12);
            DrawTablePlacementControls(201);
            if (live)
            {
                RoundBox("SpatialUnalignedSpeech", content, 0, 263, SpatialWidth, 83, SpatialBackground, 12);
                SpatialSummaryLine("전사", 267, TextValue(LatestFinalUtterance, "text"), "바꿀 부분과 방향을 말하세요");
                DrawLiveDesignAction(MeetingLatestRound, 302);
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!OVRManager.IsInsightPassthroughSupported())
            {
                Label("SpatialAlignmentStatus", content, 12, 65, 396, 34, "패스스루 사용 불가", SpatialFont(21), HeaderText);
                Label("SpatialAlignmentHint", content, 12, 101, 396, 65, "녹음을 시작하고 바꿀 점을 말하면 수정안이 패널에 표시됩니다.", SpatialFont(19), SpatialMuted);
                if (stateCanClose()) Button("SpatialSetupExit", 288, 265, 120, 38, "회의 목록", LeaveMeeting);
                return;
            }
#endif
            bool offerPaper = SpatialAssembly && !ExplicitDesignMeeting && !ReviewTableSampling &&
                assemblyAlignment is (AssemblyAlignment.Unaligned or AssemblyAlignment.Lost);
            Label("SpatialAlignmentStatus", content, 12, 65, offerPaper ? 247 : 396, 34, AssemblyAlignmentText(), SpatialFont(21), HeaderText);
            if (offerPaper)
                Button("A4Start", 268, 65, 139, 34, "A4 네 점 지정", BeginA4Alignment);
            var instruction = AssemblySampling
                ? "기준점을 짚고 왼손을 핀치하거나 오른쪽 컨트롤러의 A를 누르세요."
                : !string.IsNullOrWhiteSpace(AssemblyQrStatus) ? AssemblyQrStatus
                : assemblyAlignment == AssemblyAlignment.Lost ? "정렬을 잃었습니다. 기준 QR을 다시 봐 주세요."
                : AssemblyQrAvailable ? "고정 받침의 QR로 실물 위치를 맞춥니다." : "실물 기준점을 직접 지정할 수 있습니다.";
            Label("SpatialAlignmentHint", content, 12, 101, 396, 47, TruncateSpatial(instruction, "", 80), SpatialFont(17), SpatialMuted);
            if (assemblyAlignment == AssemblyAlignment.Preview)
            {
                if (OriginalOverlayLoaded)
                    Button("AssemblyAlignmentConfirm", 12, 151, 216, 37, "실물 일치 확인", ConfirmAssemblyAlignment, true);
                if (AssemblyQrAvailable)
                    Button("AssemblyQrStart", 244, 151, 163, 37, "QR 다시 보기", BeginAssemblyQrAlignment);
                else if (assemblyHasMount)
                    Button("AssemblyAlignmentStart", 244, 151, 163, 37, "다시 맞추기", BeginAssemblyAlignment);
            }
            else if (AssemblyQrScanning)
                Button("AssemblyQrCancel", 12, 151, 216, 37, "QR 읽기 취소", CancelAssemblyQrAlignment);
            else
            {
                if (AssemblyQrAvailable && !AssemblySampling)
                    Button("AssemblyQrStart", 12, 151, 216, 37, "QR로 자동 정렬", BeginAssemblyQrAlignment, true);
                if (assemblyHasMount)
                    Button("AssemblyAlignmentStart", AssemblyQrAvailable && !AssemblySampling ? 244 : 12, 151,
                        AssemblyQrAvailable && !AssemblySampling ? 163 : 216, 37,
                        AssemblySampling ? "원점부터 다시" : "수동 맞추기", BeginAssemblyAlignment, !AssemblyQrAvailable);
            }
            if (ExplicitDesignMeeting && (AssemblySampling || AssemblyQrScanning || assemblyAlignment == AssemblyAlignment.Preview))
                Button("A4Start", 12, 264, 223, 41, "A4로 다시 맞추기", BeginA4Alignment);
            if (stateCanClose()) Button("SpatialSetupExit", 288, 265, 120, 38, "회의 목록", LeaveMeeting);
            var feedback = !string.IsNullOrEmpty(assemblyFeedback) ? assemblyFeedback : TextValue(meetingSnapshot, "speech_error");
            if (feedback.Length > 0)
                Label("SpatialSetupFeedback", content, 12, live ? 349 : 310, 396, 48, TruncateSpatial(feedback, "", 70), SpatialFont(17), SpatialMuted);
        }

        void DrawTablePlacementControls(float y, bool showCurrentVersion = false)
        {
            RoundBox("SpatialTablePlacement", content, 0, y, SpatialWidth, 55, SpatialBackground, 12);
            string title = ReviewTableSampling ? "지정 취소" : HasReviewTable ? "책상 다시 지정" : "책상 지정";
            Button("SpatialTablePlacementAction", 10, y + 7, 132, 40, title, () =>
            {
                if (ReviewTableSampling) CancelTablePlacement();
                else BeginTablePlacement();
            });
            if (!HasReviewTable && !ReviewTableSampling && DeskNeedsPermission)
                Button("SpatialDeskPermission", 152, y + 7, 251, 40, "공간 권한 허용 · 자동 찾기", RequestDeskAccess);
            else if (CanRequestDeskSetup)
            {
                Label("SpatialTablePlacementHint", content, 152, y + 2, 148, 51, TablePlacementHint, SpatialFont(15), SpatialMuted);
                Button("SpatialDeskSetup", 306, y + 7, 102, 40, "공간 설정", RequestDeskSetup);
            }
            else if (showCurrentVersion && A4ReviewSurfaceActive && string.IsNullOrEmpty(tableFeedback))
                DrawLatestCaption(152, y + 2, 256, 51);
            else Label("SpatialTablePlacementHint", content, 152, y + 2, 256, 51, TablePlacementHint, SpatialFont(17), SpatialMuted);
        }

        bool stateCanClose() => AssemblySessionStatus == "ready" && !assemblyStarting;

        void OpenSpatialComparison(JToken round)
        {
            if (round == null) return;
            assemblyCompare = true;
            assemblyHistory = false;
            assemblyCompareRound = TextValue(round, "round_id");
            assemblyClarify = TextValue(round, "status") == "needs_input";
            if (!assemblyClarify)
            {
                if (!CanCompareModels) StartLocalCandidatePreview();
                var first = Values(round, "candidates").FirstOrDefault(candidate => TextValue(candidate, "status") == "valid");
                var id = TextValue(first, "version_id");
                if (!string.IsNullOrEmpty(id)) SetMeetingViewedVersion(id);
                QueueMeetingModel(RequestedMeetingVersion());
            }
            Draw();
        }

        void CloseSpatialComparison()
        {
            collaborativeEditing = false;
            assemblyCompare = assemblyClarify = assemblyHistory = false;
            assemblyCompareRound = null;
            SetMeetingViewedVersion(null);
            Draw();
        }

        JObject SpatialRound() => Values(meetingSnapshot, "rounds").OfType<JObject>()
            .FirstOrDefault(round => TextValue(round, "round_id") == assemblyCompareRound);
        bool SpatialComparisonVisible => SpatialAssembly && assemblyCompare && !assemblyClarify;
        bool SpatialViewingShared => SpatialComparisonVisible && !assemblyHistory;
        bool SpatialSharesCommitted => SpatialAssembly && !assemblyCompare && assemblyAlignment == AssemblyAlignment.Confirmed;

        void DrawSpatialComparison()
        {
            var round = SpatialRound();
            var candidates = Values(round, "candidates").Take(3).ToArray();
            var selected = candidates.FirstOrDefault(value => TextValue(value, "version_id") == meetingViewedVersion);
            var old = TextValue(round, "status") != "reviewable";
            var question = TextValue(round, "question");
            if (assemblyClarify && question.Length > 0)
            {
                RoundBox("SpatialComparison", content, 0, 58, SpatialWidth, 166, SpatialBackground, 12);
                BoldLabel("SpatialQuestion", content, 12, 67, 396, 41, TruncateSpatial(question, "", 62), SpatialFont(21), HeaderText);
                bool manual = TextValue(meetingSnapshot?["meeting"], "input_mode") == "manual";
                Label("SpatialQuestionHint", content, 12, 114, 396, 40, ExplicitDesignMeeting ?
                    "닫기 후 수정할 범위를 다시 말하고 오른쪽 A로 해당 부위를 지정하세요." : manual ?
                    "PC의 이 회의에서 변경할 치수와 값을 입력하세요." :
                    "변경할 치수를 말한 뒤 ‘수정안 보여줘’라고 하거나 PC에서 답하세요.", SpatialFont(18), SpatialMuted);
                Button("SpatialCloseComparison", 287, 170, 120, 37, "닫기", CloseSpatialComparison);
                if (A4PoseApplied) DrawLatestCaption(12, 170, 262, 37);
                DrawTablePlacementControls(231);
                return;
            }
            var source = TextValue((round?["context"] as JObject)?["utterance"], "text");
            float rowsY = source.Length > 0 ? 146 : 106;
            float detailY = rowsY + 4 + candidates.Length * 46;
            RoundBox("SpatialComparison", content, 0, 58, SpatialWidth, detailY + 53, SpatialBackground, 12);
            var mode = HasReviewTable ? "책상 위 비교" : "책상 지정 대기";
            string count = $"수정안 {candidates.Length}개";
            BoldLabel("SpatialComparisonHeading", content, 12, 64, A4PoseApplied ? 135 : 284, 37,
                A4PoseApplied ? count : count + " · " + mode, SpatialFont(21), HeaderText);
            if (A4PoseApplied) DrawLatestCaption(153, 64, 142, 37);
            Button("SpatialOriginal", 304, 67, 104, 33, "원본", () => { SetMeetingViewedVersion(AssemblyOriginalId); Draw(); }, SpatialComparisonLoadedVersion == AssemblyOriginalId);
            if (source.Length > 0)
                Label("SpatialProposalSource", content, 12, 101, 396, 40, TruncateSpatial("요청 · " + source, "", 62), SpatialFont(18), SpatialMuted);
            for (int i = 0; i < candidates.Length; i++)
            {
                var candidate = candidates[i];
                var id = TextValue(candidate, "version_id");
                var title = TruncateSpatial(TextValue(candidate, "title"), TextValue(candidate, "interpretation"), 30);
                float y = rowsY + i * 46;
                if (string.IsNullOrEmpty(id) || TextValue(candidate, "status") != "valid")
                {
                    Label("SpatialUnavailableCandidate" + i, content, 12, y, 396, 40, $"{(char)('A' + i)}  {title} · 생성 실패", SpatialFont(18), SpatialMuted);
                    continue;
                }
                Button("SpatialCandidate-" + TextValue(candidate, "candidate_id"), 12, y, 396, 40, $"{(char)('A' + i)}  {title}",
                    () => { assemblyFeedback = ""; SetMeetingViewedVersion(id); Draw(); }, id == meetingViewedVersion);
            }
            var line = SpatialComparisonFailedVersion != null && SpatialComparisonFailedVersion == meetingViewedVersion ? "불러오지 못했습니다. 이전 모델을 유지합니다." :
                SpatialComparisonLoadingVersion != null ? "비교할 수정안을 불러오는 중" :
                old ? "이 수정안에는 다시 검토가 필요합니다." : TextValue(selected, "interpretation");
            Label("SpatialCandidateInterpretation", content, 12, detailY, 396, 52,
                TruncateSpatial(line, "비교할 수정안을 선택하세요.", 65), SpatialFont(19), HeaderText);
            var yActions = detailY + 58;
            if (selected != null && !old && HasReviewTable && IsLocalCandidateVisible(meetingViewedVersion) && !assemblyAdopting && !assemblyStopping)
                Button("SpatialAdopt", 12, yActions, 150, 39, "이 안 채택", () => AdoptSpatialCandidate(round, selected), true);
            else if (selected != null && !old && SpatialComparisonFailedVersion != null && SpatialComparisonFailedVersion == meetingViewedVersion)
                Button("SpatialRetryCandidate", 12, yActions, 150, 39, "다시 불러오기", RetrySpatialComparison);
            else if (assemblyAdopting)
                Label("SpatialAdopting", content, 12, yActions, 150, 39, "채택 확인 중", SpatialFont(18), SpatialMuted);
            if (selected != null && !old)
                Button("SpatialAdjustCandidate", 174, yActions, 123, 39, "치수 조정", () => BeginCollaborativeEdit(round, selected));
            Button("SpatialCloseComparison", 309, yActions, 99, 39, "닫기", CloseSpatialComparison);
            DrawTablePlacementControls(yActions + 45);
        }

        void HandleCableDemoTarget(string partId)
        {
            if (!IsCableDemo || assemblyCompare || CableStage < 1 || CableStage > 2) return;
            if (CableStage == 1 && partId != "outer_shell") return;
            RequestCableDemoStage(partId);
        }

        void RequestCableDemoStage(string partId = null)
        {
            if (!IsCableDemo || assemblyCableRequest || !AssemblyActive || CableStage is not (1 or 2) ||
                meetingSnapshot["cable_demo"]?.Value<bool?>("pending") == true) return;
            assemblyMenu = false;
            assemblyCableRequest = true;
            assemblyFeedback = "대본의 수정안을 여는 중";
            var body = new JObject { ["sample"] = "cable" };
            if (!string.IsNullOrEmpty(partId))
            {
                body["target_part_id"] = partId;
                body["target_version_id"] = meetingLoadedVersion;
            }
            StartCoroutine(RunAssemblyAction(meetingTransport, "replay", body, (_, error) =>
            {
                assemblyCableRequest = false;
                assemblyFeedback = error ?? "";
                Draw();
            }));
            Draw();
        }

        IEnumerable<JToken> DesignHistory()
        {
            if (IsCableDemo) return Values(meetingSnapshot["cable_demo"], "history");
            var history = new JArray(new JObject { ["label"] = "V1", ["version_id"] = AssemblyOriginalId, ["what"] = "초기 시제품", ["why"] = "" });
            foreach (var decision in Values(meetingSnapshot, "decisions"))
            {
                var id = TextValue(decision, "selected_model_version_id");
                if (id.Length == 0) continue;
                history.Add(new JObject { ["label"] = TextValue(decision, "design_version"), ["version_id"] = id,
                    ["what"] = TextValue(decision, "candidate_title"), ["why"] = TextValue(decision, "rationale_text") is string reason && reason.Length > 0 ? reason : "선택 이유 미기록" });
            }
            return history;
        }

        void OpenSpatialHistory()
        {
            assemblyHistoryPage = 0;
            assemblyMenu = assemblyClarify = false;
            assemblyHistory = assemblyCompare = true;
            assemblyCompareRound = null;
            var first = DesignHistory().FirstOrDefault();
            if (first != null) SetMeetingViewedVersion(TextValue(first, "version_id"));
            Draw();
        }

        void DrawSpatialHistory()
        {
            RoundBox("SpatialHistoryPanel", content, 0, 58, SpatialWidth, 214, SpatialBackground, 12);
            BoldLabel("SpatialHistoryHeading", content, 12, 65, A4PoseApplied ? 269 : 396, 30,
                "설계 이력 · 선택하면 옆에 표시", SpatialFont(20), HeaderText);
            if (A4PoseApplied) DrawLatestCaption(289, 65, 119, 30);
            var history = DesignHistory().Skip(assemblyHistoryPage * 3).Take(3).ToArray();
            for (int i = 0; i < history.Length; i++)
            {
                var item = history[i];
                var id = TextValue(item, "version_id");
                var label = TextValue(item, "label");
                Button("SpatialHistory-" + label, 12 + 133 * i, 103, 125, 35, label,
                    () => { SetMeetingViewedVersion(id); Draw(); }, id == meetingViewedVersion);
            }
            var selected = history.FirstOrDefault(item => TextValue(item, "version_id") == meetingViewedVersion);
            Label("SpatialHistoryWhat", content, 12, 146, 396, 30,
                TruncateSpatial(TextValue(selected, "what"), ""), SpatialFont(20), HeaderText);
            Label("SpatialHistoryWhy", content, 12, 179, 396, 42,
                TruncateSpatial(TextValue(selected, "why"), "초기 배치와 치수를 확인합니다.", 60), SpatialFont(17), SpatialMuted);
            if (assemblyHistoryPage > 0) Button("SpatialHistoryPrev", 12, 230, 92, 34, "이전", () => { assemblyHistoryPage--; Draw(); });
            if (DesignHistory().Count() > (assemblyHistoryPage + 1) * 3)
                Button("SpatialHistoryNext", 112, 230, 92, 34, "다음", () => { assemblyHistoryPage++; Draw(); });
            Button("SpatialHistoryClose", 287, 230, 120, 34, "닫기", CloseSpatialComparison);
        }

        void DrawSpatialMenu()
        {
            bool changeSetup = SharedPaperMeeting && MeetingHost && PaperSessionReady &&
                AssemblySessionStatus is ("ready" or "recording" or "paused");
            if (changeSetup && confirmPaperChange)
            {
                RoundBox("PaperRedefine", content, 0, 58, SpatialWidth, 142, SpatialBackground, 12);
                Label("PaperRedefineHint", content, 12, 68, 396, 65, "종이를 옮겼나요? 기준을 바꾸면 참가자 모두 네 모서리를 다시 짚어야 합니다.", SpatialFont(19), HeaderText);
                Button("PaperCancelRedefine", 12, 143, 188, 39, "취소", () => { confirmPaperChange = false; Draw(); });
                Button("PaperConfirmRedefine", 212, 143, 194, 39, "기준 바꾸기", () => {
                    confirmPaperChange = assemblyMenu = false;
                    RedefinePaperFrame();
                });
                return;
            }
            confirmPaperChange = false;
            bool savedRequest = AssemblySessionStatus == "completed" && MinutesStatus is ("generated" or "generation_failed") &&
                                meetingSnapshot["saved_design_utterance"] is JObject;
            RoundBox("SpatialOptions", content, 0, 58, SpatialWidth, savedRequest ? 209 : 165, SpatialBackground, 12);
            if (AssemblySessionStatus == "completed")
            {
                Button("SpatialResult", 12, 72, 188, 40, "결과 ZIP 저장", SaveSpatialResult);
                Button("SpatialContinue", 212, 72, 194, 40, "후속 작업", ContinueSavedMeeting);
                if (savedRequest)
                    Button("SpatialSavedDesign", 12, 173, 394, 38, savedFollowUpPending ? "준비 중…" : "저장된 발언으로 수정안 보기", ContinueSavedDesign);
            }
            else
            {
                Button("SpatialRealign", 12, 72, 188, 40, "A4로 다시 맞추기", () =>
                {
                    assemblyMenu = choosingAlignmentMethod = false;
                    BeginA4Alignment();
                });
                if (AssemblyActive && !IsCableDemo && AssemblyRecordingMode == "replay")
                    Button("SpatialReplayUtterance", 212, 72, 194, 40, "예시 발화 재생", () =>
                    {
                        assemblyMenu = false;
                        StartCoroutine(RunAssemblyAction(meetingTransport, "replay", new JObject { ["sample"] = "curves" },
                            (_, error) => { assemblyFeedback = error ?? ""; }));
                    });
                else Button("SpatialHistory", 212, 72, 194, 40, "설계 이력", OpenSpatialHistory);
            }
            Button("SpatialLeave", 12, 126, 188, 39, "회의 목록", LeaveMeeting);
            Button("SpatialMenuClose", 212, 126, 194, 39, "돌아가기", () => { assemblyMenu = false; Draw(); });
            if (changeSetup)
                Button("PaperRedefineFrame", 12, 177, 394, 36, "종이를 옮겼어요 · 공통 기준 변경", () => { confirmPaperChange = true; Draw(); });
            else if (!SharedPaperMeeting || AssemblySessionStatus == "completed")
            {
                var code = TextValue(meetingSnapshot, "join_code");
                Label("SpatialJoinCode", content, 12, savedRequest ? 220 : 177, 396, 35, code.Length > 0 ? "참가 코드  " + code : "왼쪽 컨트롤러 X · 패널 다시 부르기", SpatialFont(18), SpatialMuted);
            }
        }

        void ResetSpatialAssembly()
        {
            assemblyCompare = assemblyMenu = movingMeetingPanel = meetingPanelMoved = assemblyPanelReady = assemblyRecoveryPanel = false;
            showJoinKeypad = confirmPaperChange = false;
            visibleJoinCodeExpiresAt = 0;
            minutesCode = null;
            fetchingMinutesCode = savedFollowUpPending = confirmingMeetingEnd = false;
            assemblyClarify = assemblyStarting = assemblyStopping = assemblyAdopting = assemblyResultDownload = assemblyCaptureFrozen = false;
            assemblyCompareRound = null;
            assemblyPrepOpened = false;
            requireBundleOutput = requireCandidateGeneration = false;
            choosingAlignmentMethod = tabletopTipTracked = tabletopAHeld = false;
            ResetA4ProbeStability();
            explicitTranscriptPage = -1;
            assemblyFeedback = "";
            ReleaseSpatialAudio();
        }
    }
}
