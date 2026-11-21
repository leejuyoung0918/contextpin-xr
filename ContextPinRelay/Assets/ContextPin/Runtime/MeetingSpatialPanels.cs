using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        const float SpatialWidth = 420;
        const float SpatialOriginY = 270;
        bool assemblyCompare, assemblyMenu, assemblyCollapsed, assemblyCaption, assemblyPlacingPanel, assemblyPanelReady, assemblyRecoveryPanel;
        bool assemblyClarify;
        string assemblyCompareRound, assemblyAdoptedTakesEffect;
        float assemblyPanelDistance, assemblyPanelScale, assemblyFontScale;
        Pose assemblyPanelPose;
        static readonly Color SpatialBackground = new Color32(17, 36, 34, 245);
        static readonly Color SpatialMuted = new Color32(197, 215, 207, 255);

        void SetupSpatialPanel()
        {
            if (assemblyPanelReady) return;
            assemblyPanelReady = true;
            var config = RelayEvidence.LoadConfig();
            assemblyPanelDistance = Mathf.Clamp(config.meetingPanelDistanceMeters, .65f, 2f);
            assemblyPanelScale = Mathf.Clamp(config.meetingPanelWidthMeters, .22f, .48f) / SpatialWidth;
            assemblyFontScale = Mathf.Clamp(config.meetingPanelTextScale, .8f, 1.25f);
            var forward = Vector3.ProjectOnPlane(view.transform.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < .1f) forward = Vector3.forward;
            var side = Quaternion.AngleAxis(35, Vector3.up) * forward;
            PlaceSpatialPanel(view.transform.position + side * assemblyPanelDistance - Vector3.up * .17f);
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
            if (assemblyAlignment != AssemblyAlignment.Lost) return;
            var forward = view.transform.forward;
            panel.SetPositionAndRotation(view.transform.position + forward * .75f - view.transform.up * .10f,
                Quaternion.LookRotation(forward, view.transform.up));
            assemblyRecoveryPanel = true;
        }

        bool PlaceSpatialPanelFromInput(Ray ray, bool clicked)
        {
            if (!SpatialAssembly || !assemblyPlacingPanel) return false;
            if (!clicked) return false;
            PlaceSpatialPanel(ray.GetPoint(assemblyPanelDistance));
            assemblyPlacingPanel = false;
            assemblyFeedback = "";
            Draw();
            return true;
        }

        bool RecallSpatialPanel()
        {
            if (!SpatialAssembly || !assemblyPanelReady || assemblyAlignment == AssemblyAlignment.Lost) return false;
            var direction = Quaternion.AngleAxis(26, Vector3.up) * view.transform.forward;
            PlaceSpatialPanel(view.transform.position + direction * assemblyPanelDistance - Vector3.up * .16f);
            return true;
        }

        int SpatialFont(int size) => Mathf.RoundToInt(size * assemblyFontScale);

        void DrawSpatialMeeting()
        {
            SetupSpatialPanel();
            content = Rect("SpatialAssemblyMeeting", panel, (W - SpatialWidth) / 2f, SpatialOriginY, SpatialWidth, 376);
            var state = AssemblySessionStatus;
            var (status, action) = SpatialSessionAction(state);
            if (action == null)
            {
                RoundBox("SpatialSessionBadge", content, 0, 0, 222, 47, SpatialBackground, 10);
                BoldLabel("SpatialSessionStatus", content, 10, 0, 202, 47, status, SpatialFont(21), HeaderText, TextAnchor.MiddleCenter);
            }
            else
                Button("SpatialSessionAction", 0, 0, 222, 47, status, action, true);

            if (assemblyCollapsed && assemblyAlignment == AssemblyAlignment.Confirmed)
            {
                Button("SpatialSummaryOpen", 236, 0, 184, 47, "요약 열기", () => { assemblyCollapsed = false; Draw(); });
                return;
            }

            var alignmentWork = assemblyAlignment != AssemblyAlignment.Confirmed && state is not ("completed" or "save_error");
            if (alignmentWork) { DrawSpatialAlignment(); return; }
            if (assemblyCompare) { DrawSpatialComparison(); return; }
            if (assemblyMenu) { DrawSpatialMenu(); return; }

            var summary = meetingSnapshot["summary"] as JObject;
            var discussion = TextValue(summary, "discussion");
            var preserved = TextValue(summary, "preserved");
            var decision = TextValue(summary, "decision");
            var count = summary?.Value<int?>("proposal_count") ?? 0;
            var rounds = Values(meetingSnapshot, "rounds").OfType<JObject>().ToArray();
            var proposal = RelevantSpatialRound(rounds, TextValue(summary, "round_id"), "reviewable");
            var questionRound = RelevantSpatialRound(rounds, TextValue(summary, "question_round_id"), "needs_input");
            if (count == 0)
                count = Values(proposal, "candidates").Count(item => TextValue(item, "status") == "valid");
            var round = count > 0 ? proposal : questionRound;
            var question = TextValue(questionRound, "question");
            var active = state is "recording" or "paused";
            var hasActivity = active && round != null && (count > 0 || !string.IsNullOrWhiteSpace(question));
            var showResult = state == "completed" && !string.IsNullOrEmpty(AssemblyResultUrl);
            float height = 145;
            if (assemblyCaption) height += 32;
            if (!string.IsNullOrEmpty(assemblyFeedback)) height += 28;
            RoundBox("SpatialSummary", content, 0, 58, SpatialWidth, height, SpatialBackground, 12);
            SpatialSummaryLine("논의", 68, discussion, "논의할 내용을 기다리는 중");
            SpatialSummaryLine("유지", 97, preserved, string.Join(" · ", Values(meetingSnapshot["assembly"], "preserved_conditions").Select(value => value.ToString())));
            SpatialSummaryLine("결정", 126, decision, "아직 미정");
            if (hasActivity)
                Button("SpatialOpenComparison", 11, 157, 246, 37,
                    count > 0 ? $"수정안 {count}개 준비됨" : "확인 질문 보기", () => OpenSpatialComparison(round), true);
            else if (showResult)
                Button("SpatialResult", 11, 157, 246, 37, "결과 ZIP 받기", SaveSpatialResult, true);
            else
            {
                var design = TextValue(meetingSnapshot, "design_status");
                var text = design switch
                {
                    "interpreting" => "제안 해석 중",
                    "generating" => "형상 생성 중",
                    "error" => "제안 확인 필요",
                    _ => "",
                };
                Label("SpatialDesignStatus", content, 12, 157, 257, 37, text, SpatialFont(18), SpatialMuted);
            }
            Button("SpatialMenu", 338, 157, 69, 37, "더 보기", () => { assemblyMenu = true; Draw(); });
            if (assemblyCaption)
            {
                var last = Values(meetingSnapshot, "utterances").LastOrDefault();
                Label("SpatialCaption", content, 12, 198, 396, 29,
                    TruncateSpatial(TextValue(last, "text"), "확정 발화를 기다리는 중", 42), SpatialFont(17), SpatialMuted);
            }
            if (!string.IsNullOrEmpty(assemblyFeedback))
                Label("SpatialFeedback", content, 12, assemblyCaption ? 228 : 198, 396, 28,
                    TruncateSpatial(assemblyFeedback, "", 64), SpatialFont(16), SpatialMuted);
        }

        (string label, Action action) SpatialSessionAction(string state)
        {
            if (assemblyStopping || state == "finalizing") return ("결과 저장 중", null);
            if (state == "completed") return (Values(meetingSnapshot?["result"], "missing").Any() ? "누락 포함 저장됨" : "저장됨", null);
            if (state == "save_error") return ("저장 다시 시도", RetrySpatialSave);
            if (state == "recording")
            {
                if (AssemblyRecordingMode == "replay") return ("예시 진행 · 회의 종료", () => EndSpatialMeeting());
                var capture = TextValue(meetingSnapshot, "capture_state");
                return (capture == "recording" && !assemblyCaptureFrozen ? "● 녹음 중 · 회의 종료" : "회의 종료", () => EndSpatialMeeting());
            }
            if (assemblyStarting || TextValue(meetingSnapshot, "capture_state") == "starting")
                return ("마이크 연결 중 · 종료", () => EndSpatialMeeting());
            if (AssemblyCaptureRequested && assemblyAlignment != AssemblyAlignment.Confirmed)
                return ("회의 종료", () => EndSpatialMeeting());
            if (state == "paused") return assemblyAlignment == AssemblyAlignment.Confirmed
                ? ("회의 이어가기", StartSpatialMeeting) : ("회의 종료", () => EndSpatialMeeting());
            return ("회의 시작", assemblyAlignment == AssemblyAlignment.Confirmed ? StartSpatialMeeting : null);
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

        void DrawSpatialAlignment()
        {
            RoundBox("SpatialAlignment", content, 0, 58, SpatialWidth, 138, SpatialBackground, 12);
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!OVRManager.IsInsightPassthroughSupported())
            {
                Label("SpatialAlignmentStatus", content, 12, 65, 396, 34, "패스스루 사용 불가", SpatialFont(21), HeaderText);
                Label("SpatialAlignmentHint", content, 12, 101, 396, 65, "이 기기는 AR 배치를 지원하지 않습니다. PC 화면에서 세션을 검토하세요.", SpatialFont(19), SpatialMuted);
                if (stateCanClose()) Button("SpatialSetupExit", 320, 0, 99, 47, "나가기", LeaveMeeting);
                return;
            }
#endif
            Label("SpatialAlignmentStatus", content, 12, 65, 396, 34, AssemblyAlignmentText(), SpatialFont(21), HeaderText);
            var instruction = AssemblySampling
                ? "오른손 검지로 표시를 짚고 왼손을 핀치하거나, 오른쪽 컨트롤러의 추적점을 맞춰 A를 누르세요."
                : assemblyAlignment == AssemblyAlignment.Lost ? "실물 위 표시를 숨겼습니다. 원본 위치를 다시 맞추세요."
                : "고정된 실물 조립체에 원본을 맞춥니다.";
            Label("SpatialAlignmentHint", content, 12, 101, 396, 47, TruncateSpatial(instruction, "", 80), SpatialFont(17), SpatialMuted);
            if (assemblyAlignment == AssemblyAlignment.Preview && meetingLoadedVersion == AssemblyOriginalId)
                Button("AssemblyAlignmentConfirm", 12, 151, 223, 37, "실물 일치 확인", ConfirmAssemblyAlignment, true);
            if (assemblyHasMount)
                Button("AssemblyAlignmentStart", assemblyAlignment == AssemblyAlignment.Preview ? 249 : 12, 151,
                    assemblyAlignment == AssemblyAlignment.Preview ? 157 : 222, 37,
                    assemblyAlignment == AssemblyAlignment.Unaligned ? "원본 맞추기" : "다시 맞추기", BeginAssemblyAlignment);
            if (stateCanClose()) Button("SpatialSetupExit", 320, 0, 99, 47, "나가기", LeaveMeeting);
        }

        bool stateCanClose() => AssemblySessionStatus == "ready" && !assemblyStarting;

        void OpenSpatialComparison(JToken round)
        {
            if (round == null) return;
            assemblyCompare = true;
            assemblyCompareRound = TextValue(round, "round_id");
            assemblyClarify = !string.IsNullOrWhiteSpace(TextValue(round, "question"));
            Draw();
        }

        void CloseSpatialComparison()
        {
            assemblyCompare = assemblyClarify = false;
            assemblyCompareRound = null;
            SetMeetingViewedVersion(null);
            Draw();
        }

        JObject SpatialRound() => Values(meetingSnapshot, "rounds").OfType<JObject>()
            .FirstOrDefault(round => TextValue(round, "round_id") == assemblyCompareRound);
        static JObject RelevantSpatialRound(JObject[] rounds, string id, string status) =>
            rounds.LastOrDefault(round => TextValue(round, "round_id") == id && TextValue(round, "status") == status) ??
            rounds.LastOrDefault(round => TextValue(round, "status") == status);
        bool SpatialViewingShared => SpatialAssembly && assemblyCompare && !assemblyClarify;
        bool SpatialSharesCommitted => SpatialAssembly && !assemblyCompare && assemblyAlignment == AssemblyAlignment.Confirmed;

        void DrawSpatialComparison()
        {
            RoundBox("SpatialComparison", content, 0, 58, SpatialWidth, 166, SpatialBackground, 12);
            var round = SpatialRound();
            var candidates = Values(round, "candidates").Take(3).ToArray();
            var selected = candidates.FirstOrDefault(value => TextValue(value, "version_id") == meetingViewedVersion);
            var old = TextValue(round, "status") != "reviewable";
            var question = TextValue(round, "question");
            if (assemblyClarify && question.Length > 0)
            {
                BoldLabel("SpatialQuestion", content, 12, 67, 396, 41, TruncateSpatial(question, "", 62), SpatialFont(21), HeaderText);
                Label("SpatialQuestionHint", content, 12, 114, 396, 40,
                    "대화로 답하거나 PC에서 입력하세요.", SpatialFont(18), SpatialMuted);
            }
            else
            {
                Button("SpatialOriginal", 12, 69, 91, 38, "원본", () => { SetMeetingViewedVersion(AssemblyOriginalId); Draw(); }, meetingLoadedVersion == AssemblyOriginalId);
                for (var i = 0; i < candidates.Length; i++)
                {
                    var candidate = candidates[i];
                    var id = TextValue(candidate, "version_id");
                    if (string.IsNullOrEmpty(id) || TextValue(candidate, "status") != "valid") continue;
                    Button("SpatialCandidate-" + TextValue(candidate, "candidate_id"), 112 + i * 99, 69, 91, 38, $"{(char)('A' + i)}",
                        () => { assemblyFeedback = ""; SetMeetingViewedVersion(id); Draw(); }, id == meetingViewedVersion);
                }
                var line = meetingLoadingVersion != null ? "수정안을 불러오는 중" :
                    meetingFailedVersion != null && meetingFailedVersion == meetingViewedVersion ? "불러오지 못했습니다. 이전 모델을 유지합니다." :
                    old ? "이 수정안에는 다시 검토가 필요합니다." :
                    TextValue(selected, "interpretation");
                Label("SpatialCandidateInterpretation", content, 12, 112, 396, 54,
                    TruncateSpatial(line, "같은 장착 위치에서 수정안을 비교하세요.", 65), SpatialFont(19), HeaderText);
                if (selected != null && !old && meetingLoadedVersion == meetingViewedVersion && !assemblyAdopting && !assemblyStopping)
                    Button("SpatialAdopt", 12, 176, 229, 37, "이 안 채택", () => AdoptSpatialCandidate(round, selected), true);
                else if (selected != null && !old && meetingFailedVersion != null && meetingFailedVersion == meetingViewedVersion)
                    Button("SpatialRetryCandidate", 12, 176, 229, 37, "수정안 다시 불러오기", () =>
                    {
                        meetingFailedVersion = null;
                        QueueMeetingModel(RequestedMeetingVersion());
                        Draw();
                    });
                else if (assemblyAdopting)
                    Label("SpatialAdopting", content, 12, 176, 229, 37, "채택 확인 중", SpatialFont(18), SpatialMuted);
            }
            Button("SpatialCloseComparison", 287, 176, 120, 37, "닫기", CloseSpatialComparison);
        }

        void DrawSpatialMenu()
        {
            RoundBox("SpatialOptions", content, 0, 58, SpatialWidth, 192, SpatialBackground, 12);
            Button("SpatialMovePanel", 12, 69, 195, 37, "패널 옮기기", () =>
            {
                assemblyMenu = false;
                assemblyPlacingPanel = true;
                assemblyFeedback = "놓을 방향을 가리키고 트리거를 누르세요.";
                Draw();
            });
            Button("SpatialRealign", 215, 69, 193, 37, "실물 다시 맞추기", () => { assemblyMenu = false; BeginAssemblyAlignment(); });
            Button("SpatialCaptions", 12, 115, 195, 37, assemblyCaption ? "자막 끄기" : "마지막 발화 보기",
                () => { assemblyCaption = !assemblyCaption; assemblyMenu = false; Draw(); });
            Button("SpatialFold", 215, 115, 193, 37, "요약 접기", () => { assemblyCollapsed = true; assemblyMenu = false; Draw(); });
            if (AssemblyActive)
            {
                if (AssemblyRecordingMode == "replay")
                    Button("SpatialReplayUtterance", 12, 161, 195, 37, "예시 발화 재생", () =>
                    {
                        assemblyMenu = false;
                        StartCoroutine(RunAssemblyAction(meetingTransport, "replay", new JObject { ["sample"] = "curves" },
                            (_, error) => { if (error != null) assemblyFeedback = error; }));
                        Draw();
                    });
                else Button("SpatialPause", 12, 161, 195, 37, AssemblySessionStatus == "paused" ? "회의 종료" : "일시 정지",
                    () => { assemblyMenu = false; EndSpatialMeeting(AssemblySessionStatus != "paused"); });
            }
            else if (AssemblyCaptureRequested)
                Button("SpatialStopArmed", 12, 161, 195, 37, "회의 종료", () => EndSpatialMeeting());
            else Button("SpatialLeave", 12, 161, 195, 37, "나가기", LeaveMeeting);
            Button("SpatialMenuClose", 287, 161, 120, 37, "닫기", () => { assemblyMenu = false; Draw(); });
            var code = TextValue(meetingSnapshot, "join_code");
            Label("SpatialJoinCode", content, 12, 210, 396, 30,
                string.IsNullOrEmpty(code) ? "왼쪽 컨트롤러 X · 패널 다시 부르기" : "PC 관찰 화면 참가 코드  " + code,
                SpatialFont(17), SpatialMuted);
        }

        void ResetSpatialAssembly()
        {
            assemblyCompare = assemblyMenu = assemblyCollapsed = assemblyCaption = assemblyPlacingPanel = assemblyPanelReady = assemblyRecoveryPanel = false;
            assemblyClarify = assemblyStarting = assemblyStopping = assemblyAdopting = assemblyResultDownload = assemblyCaptureFrozen = false;
            assemblyCompareRound = assemblyAdoptedTakesEffect = null;
            assemblyFeedback = "";
            ReleaseSpatialAudio();
        }
    }
}
