using System;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        void AddMeetingDigit(string digit)
        {
            if (!meetingBusy && meetingJoinCode.Length < 6) { meetingJoinCode += digit; Draw(); }
        }

        void RemoveMeetingDigit()
        {
            if (!meetingBusy && meetingJoinCode.Length > 0)
            { meetingJoinCode = meetingJoinCode.Substring(0, meetingJoinCode.Length - 1); Draw(); }
        }

        void DrawMeeting()
        {
            if (meetingSnapshot == null) { DrawMeetingSetup(); return; }
            var profile = meetingSnapshot["profile"];
            var name = TextValue(profile, "display_name");
            var members = Values(meetingSnapshot, "participants").Count();
            var status = meetingEventsConnected ? "연결됨" : meetingHttpAvailable ? "동기화 중" : "오프라인";
            var mode = TextValue(MeetingLatestRound, "provider_mode") switch
            {
                "fixture" => "REPLAY · 예시 AI 응답",
                "live" => "LIVE · AI 사용",
                "manual" => "직접 입력 · 실제 기하",
                _ => TextValue(meetingSnapshot, "provider_mode") == "live" ? "AI 연결 설정" : "예시 모드 대기",
            };
            Heading(string.IsNullOrEmpty(name) ? "3D 설계 회의" : name, $"{status}  ·  참가자 {members}명  ·  {mode}");
            var code = TextValue(meetingSnapshot, "join_code");
            if (!string.IsNullOrEmpty(code)) Label("MeetingJoinCode", content, 910, 13, 288, 43, "참가 코드  " + code, 22, HeaderText, TextAnchor.MiddleRight);
            else if (MeetingHost) Button("MeetingRenewCode", 942, 13, 260, 43, "새 참가 코드", () => SendMeetingCommand("renew_code", new JObject()));
            RoundBox("Meeting model card", content, 36, 196, 606, 339, Card, 14);
            RoundBox("Meeting panel card", content, 660, 196, 544, 476, Card, 14);
            var requestedVersion = RequestedMeetingVersion();
            var requestedId = TextValue(requestedVersion, "version_id");
            var versionLabel = MeetingVersionLabel(meetingLoadedVersion);
            Label("MeetingDrawingMode", content, 55, 209, 570, 33,
                meetingLoadedVersion == null ? "3D 모델을 기다리는 중" :
                (meetingRealSize ? "실제 크기" : "확대 보기") + "  ·  " + versionLabel + (meetingViewedVersion == null ? "  확정본" : "  검토 중"),
                22, Mint);
            var part = meetingPartId == null ? "모델에서 부위를 선택할 수 있어요" : "선택한 부위  " + MeetingPartLabel(meetingPartId);
            if (meetingLocalPlacement) part += "  ·  기기별 수동 배치";
            Label("MeetingSelectedPart", content, 56, 453, 568, 34, part, 21, Light);
            var dimensions = ReadMeetingVector(requestedVersion?["bounds_mm"]?["size"]);
            if (dimensions.HasValue && meetingLoadedVersion == requestedId)
            {
                var size = dimensions.Value;
                Label("MeetingDimensions", content, 56, 489, 568, 31, $"{size.x:0.##} × {size.y:0.##} × {size.z:0.##} mm  ·  폭 × 높이 × 깊이", 20, Muted);
            }
            if (meetingLoadingVersion != null) Label("MeetingModelLoading", content, 55, 254, 566, 35, "새 버전을 확인하는 중입니다.", 22, Gold);
            if (meetingFailedVersion == requestedId && !string.IsNullOrEmpty(requestedId))
                Button("MeetingRetryModel", 185, 319, 267, 55, "모델 다시 불러오기", () => { meetingFailedVersion = null; QueueMeetingModel(requestedVersion); Draw(); });

            Button("MeetingScale", 36, 548, 158, 50, meetingRealSize ? "확대 보기" : "실제 크기", () => { meetingRealSize = !meetingRealSize; PositionMeetingModel(); Draw(); });
            Button("MeetingRotate", 205, 548, 138, 50, "돌려 보기", () => { meetingYaw += 35; PositionMeetingModel(); });
            if (xr) Button("MeetingPlace", 354, 548, 160, 50, meetingLocalPlacement ? "배치 갱신" : "실물 앞에 배치", PlaceMeetingModel);
            if (meetingLocalPlacement)
            {
                Button("MeetingRaise", 525, 548, 53, 50, "↑", () => { meetingHeight += .05f; PositionMeetingModel(); });
                Button("MeetingLower", 587, 548, 53, 50, "↓", () => { meetingHeight -= .05f; PositionMeetingModel(); });
            }
            Button("MeetingHome", 36, 614, 119, 49, "나가기", LeaveMeeting);
            Button("MeetingObserver", 167, 614, 147, 49, "웹 검토 열기", () => Application.OpenURL(meetingTransport.Endpoint("/meeting/").AbsoluteUri));
            Button("MeetingReturnHead", 326, 614, 136, 49, "확정본 보기", () => { SetMeetingViewedVersion(null); Draw(); });
            DrawMeetingCaptureButton();

            Button("MeetingModelTab", 673, 208, 155, 50, "치수", () => OpenMeetingPage(MeetingPage.Model), meetingPage == MeetingPage.Model);
            Button("MeetingCandidatesTab", 836, 208, 166, 50, "수정 후보", () => OpenMeetingPage(MeetingPage.Candidates), meetingPage == MeetingPage.Candidates);
            Button("MeetingMinutesTab", 1010, 208, 181, 50, "회의록·출력", () => OpenMeetingPage(MeetingPage.Minutes), meetingPage == MeetingPage.Minutes);
            switch (meetingPage)
            {
                case MeetingPage.Model: DrawMeetingParameters(requestedVersion); break;
                case MeetingPage.Candidates: DrawMeetingCandidates(); break;
                case MeetingPage.Minutes: DrawMeetingMinutes(requestedVersion); break;
            }
        }

        void DrawMeetingSetup()
        {
            Heading("3D 설계 회의", "같은 모델을 보며 수정 후보를 비교하고, 결정한 모델과 이유를 남깁니다.");
            RoundBox("MeetingSetupNew", content, 36, 204, 537, 390, Card, 16);
            BoldLabel("MeetingSetupCreateTitle", content, 64, 229, 475, 59, "새 회의", 36, Light);
            Label("MeetingSetupCreateHelp", content, 65, 295, 473, 98,
                "초기 모델을 불러와 치수를 바꾸고, 참가 코드를 공유하세요.", 25, Muted);
            Button("MeetingCreate", 65, 417, 471, 66, meetingBusy ? "연결 중" : "새 회의 만들기", () => BeginMeetingSession(false), true);
            Label("MeetingEndpoint", content, 65, 504, 470, 50, MeetingAddress(), 20, Muted);
            RoundBox("MeetingSetupJoin", content, 595, 204, 609, 390, Card, 16);
            BoldLabel("MeetingSetupJoinTitle", content, 623, 229, 540, 54, "참가 코드", 33, Light);
            BoldLabel("MeetingCodeInput", content, 623, 289, 541, 49,
                string.Join("  ", meetingJoinCode.PadRight(6, '·').Select(c => c.ToString())), 35, Mint);
            for (int n = 0; n < 10; n++)
            {
                var digit = ((n + 1) % 10).ToString(CultureInfo.InvariantCulture);
                Button("MeetingDigit-" + digit, 623 + n % 5 * 107, 355 + n / 5 * 66, 94, 54, digit, () => AddMeetingDigit(digit));
            }
            Button("MeetingErase", 623, 488, 149, 56, "한 자리 지우기", RemoveMeetingDigit);
            Button("MeetingJoin", 783, 488, 362, 56, "회의 참가", () => BeginMeetingSession(true), meetingJoinCode.Length == 6);
            Button("MeetingHome", 36, 612, 209, 53, "홈으로", LeaveMeeting);
            Label("MeetingSetupCapability", content, 605, 605, 597, 63,
                xr ? "공간 배치는 기기별로 맞춥니다. 마이크는 권한과 서버 연결이 필요합니다." : "마우스로 모델의 부위를 선택할 수 있어요.", 21, Muted);
        }

        void DrawMeetingParameters(JObject version)
        {
            var parameters = Values(meetingSnapshot?["profile"], "parameters")
                .Where(p => TextValue(p, "type") == "number" && !string.IsNullOrEmpty(TextValue(p, "parameter_id"))).ToArray();
            if (parameters.Length == 0)
            {
                Label("MeetingReadOnly", content, 678, 276, 498, 61, "이 모델에서 편집할 수 있는 치수가 없습니다.", 23, Muted);
                return;
            }
            int maxPage = Mathf.Max(0, (parameters.Length - 1) / 3);
            meetingParameterPage = Mathf.Clamp(meetingParameterPage, 0, maxPage);
            if (meetingSelectedParameter == null) SelectMeetingParameter(parameters[meetingParameterPage * 3], version, false);
            for (int i = 0; i < 3; i++)
            {
                int index = meetingParameterPage * 3 + i;
                if (index >= parameters.Length) break;
                var parameter = parameters[index];
                string id = TextValue(parameter, "parameter_id");
                string label = TextValue(parameter, "label");
                string value = (version?["parameters"]?[id] ?? version?["full_parameters"]?[id] ?? parameter["default_value"])?.ToString() ?? "";
                Button("MeetingParameter-" + id, 678, 280 + i * 58, 507, 48,
                    $"{label}    {value} {TextValue(parameter, "unit")}", () => SelectMeetingParameter(parameter, version), id == meetingSelectedParameter);
            }
            if (meetingParameterPage > 0)
                Button("MeetingParamPrev", 678, 459, 104, 42, "이전", () => { meetingParameterPage--; meetingSelectedParameter = null; Draw(); });
            if (meetingParameterPage < maxPage)
                Button("MeetingParamNext", 1081, 459, 104, 42, "다음", () => { meetingParameterPage++; meetingSelectedParameter = null; Draw(); });
            var selected = parameters.FirstOrDefault(p => TextValue(p, "parameter_id") == meetingSelectedParameter);
            if (selected == null) return;
            string unit = TextValue(selected, "unit");
            Button("MeetingParameterMinus", 678, 513, 105, 50, "−1", () => StepMeetingParameter(selected, -1));
            Label("MeetingParamValue", content, 797, 513, 270, 50, $"{meetingParameterValue:0.##} {unit}", 32, Light, TextAnchor.MiddleCenter);
            Button("MeetingParameterPlus", 1081, 513, 104, 50, "+1", () => StepMeetingParameter(selected, 1));
            Button("MeetingApplyEdit", 678, 580, 507, 60, meetingBusy ? "보내는 중" : "수정 모델 만들기", SaveMeetingParameter, true);
        }

        void SelectMeetingParameter(JToken parameter, JObject version, bool draw = true)
        {
            meetingSelectedParameter = TextValue(parameter, "parameter_id");
            var value = version?["parameters"]?[meetingSelectedParameter] ?? version?["full_parameters"]?[meetingSelectedParameter] ?? parameter["default_value"];
            meetingParameterValue = value?.Value<double>() ?? 0;
            if (draw) Draw();
        }

        void StepMeetingParameter(JToken parameter, double change)
        {
            meetingParameterValue = Math.Max(parameter.Value<double?>("min_value") ?? double.MinValue,
                Math.Min(parameter.Value<double?>("max_value") ?? double.MaxValue, meetingParameterValue + change));
            Draw();
        }

        void DrawMeetingCandidates()
        {
            var round = MeetingLatestRound;
            if (round == null)
            {
                var speech = Values(meetingSnapshot, "utterances").LastOrDefault();
                Label("MeetingCandidateInstructions", content, 681, 277, 500, 110,
                    speech == null ? "치수를 바꾸거나 수정할 내용을 이야기하면 후보가 이곳에 표시됩니다." : TextValue(speech, "text"), 24, Muted);
                if (TextValue(meetingSnapshot, "provider_mode") != "live")
                    Button("MeetingReplay", 678, 407, 507, 60, "예시 발화로 후보 만들기", () => SendMeetingCommand("replay", new JObject { ["sample"] = "curves" }), true);
                return;
            }
            var entries = Values(round, "candidates").Take(3).ToArray();
            var question = TextValue(round, "question");
            var message = TextValue(round, "message");
            Label("MeetingRoundStatus", content, 681, 268, 500, 58,
                !string.IsNullOrEmpty(question) ? question : !string.IsNullOrEmpty(message) ? message : MeetingRoundStatus(round, entries.Length), 21, Mint);
            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                var version = TextValue(entry, "version_id");
                var label = TextValue(entry, "title");
                var candidateId = TextValue(entry, "candidate_id");
                if (string.IsNullOrEmpty(version))
                    Label("MeetingUnavailableCandidate-" + candidateId, content, 681, 330 + i * 50, 502, 45,
                        label + (string.IsNullOrEmpty(TextValue(entry, "error")) ? " · 생성 중" : " · 생성 실패"), 21, Gold);
                else Button("MeetingCandidate-" + candidateId, 678, 330 + i * 50, 507, 44, label,
                    () => { SetMeetingViewedVersion(version); Draw(); }, version == meetingViewedVersion);
            }
            var selected = entries.FirstOrDefault(item => !string.IsNullOrEmpty(meetingViewedVersion) && TextValue(item, "version_id") == meetingViewedVersion);
            bool reviewable = TextValue(round, "status") == "reviewable" && selected != null && meetingLoadedVersion == meetingViewedVersion;
            if (selected != null) Label("MeetingInterpretation", content, 681, 487, 503, 48, TextValue(selected, "interpretation"), 21, Muted);
            var roundStatus = TextValue(round, "status");
            if (roundStatus == "reviewable" && selected == null)
                Button("MeetingUnsureVote", 678, 541, 507, 44, "잘 모르겠음", () => SaveMeetingResponse("interpretation", "unsure", null));
            if (reviewable)
            {
                Button("MeetingInterpretationVote", 678, 541, 164, 44, "내 뜻과 같음", () => SaveMeetingResponse("interpretation", "candidate", selected));
                Button("MeetingPreferenceVote", 850, 541, 161, 44, "이 안 선호", () => SaveMeetingResponse("preference", "candidate", selected));
                Button("MeetingConfirmCandidate", 1019, 541, 166, 44, "검토 확인", () => ConfirmMeetingCandidate(selected));
                if (MeetingHost)
                {
                    Button("MeetingAdopt", 909, 598, 276, 51, "이 모델 결정", () => SaveMeetingDecision("commit", selected), true);
                    Button("MeetingDefer", 678, 598, 111, 51, "보류", () => SaveMeetingDecision("defer", null));
                    Button("MeetingReject", 796, 598, 105, 51, "기각", () => SaveMeetingDecision("reject", null));
                }
            }
            else if (TextValue(meetingSnapshot, "provider_mode") != "live" &&
                roundStatus is "committed" or "superseded" or "dismissed" or "deferred" or "failed" or "answered")
                Button("MeetingReplay", 678, 598, 507, 51, "예시 발화로 후보 만들기", () => SendMeetingCommand("replay", new JObject { ["sample"] = "curves" }), true);
        }

        static string MeetingRoundStatus(JToken round, int count) => TextValue(round, "status") switch
        {
            "reviewable" => $"{count}개 해석을 검토할 수 있어요.",
            "decided" or "committed" => "결정을 회의록에 기록했습니다.",
            "superseded" => "최신 요청으로 바뀌었습니다.",
            "failed" => "후보 만들기에 실패했습니다.",
            "cancelled" => "후보 만들기가 취소되었습니다.",
            "deferred" => "이 후보는 보류했습니다.",
            "dismissed" or "rejected" => "이 후보는 기각했습니다.",
            _ => "말씀의 의미와 모델을 확인하고 있습니다.",
        };

        void DrawMeetingMinutes(JObject version)
        {
            var decisions = Values(meetingSnapshot, "decisions").Reverse().ToArray();
            if (decisions.Length == 0)
                Label("MeetingEmptyMinutes", content, 681, 276, 500, 75, "최종 결정을 내리면 원래 발화와 함께 여기에 기록합니다.", 23, Muted);
            else
            {
                meetingDecisionPage = Mathf.Clamp(meetingDecisionPage, 0, decisions.Length - 1);
                var decision = decisions[meetingDecisionPage];
                var model = TextValue(decision, "selected_model_version_id");
                if (string.IsNullOrEmpty(model)) model = TextValue(decision, "version_id");
                var reason = TextValue(decision, "reason");
                if (string.IsNullOrEmpty(reason)) reason = TextValue(decision, "rationale_text");
                var decisionKind = TextValue(decision, "outcome");
                if (string.IsNullOrEmpty(decisionKind)) decisionKind = TextValue(decision, "type");
                var title = decisionKind == "defer" ? "보류" : decisionKind == "reject" ? "기각" : "모델 결정";
                BoldLabel("MeetingDecisionTitle", content, 681, 270, 502, 49,
                    $"{title}  {MeetingVersionLabel(model)}  ·  {meetingDecisionPage + 1} / {decisions.Length}", 25, Light);
                Label("MeetingDecisionReason", content, 681, 321, 500, 67,
                    string.IsNullOrEmpty(reason) ? "명시된 결정 이유가 없습니다." : reason, 23, Muted);
                if (MeetingVersion(model) != null)
                    Button("MeetingHistoryModel", 678, 460, 235, 47, "기록의 모델 보기", () => { SetMeetingViewedVersion(model); Draw(); });
                if (meetingDecisionPage + 1 < decisions.Length)
                    Button("MeetingOlderDecision", 921, 460, 129, 47, "이전 결정", () => { meetingDecisionPage++; Draw(); });
                if (meetingDecisionPage > 0)
                    Button("MeetingNewerDecision", 1057, 460, 128, 47, "다음 결정", () => { meetingDecisionPage--; Draw(); });
                var context = Values(meetingSnapshot, "rounds").FirstOrDefault(round => TextValue(round, "round_id") == TextValue(decision, "round_id"))?["context"] as JObject;
                var text = TextValue(context?["utterance"], "text");
                if (!string.IsNullOrEmpty(text)) Label("MeetingSourceUtterance", content, 681, 387, 500, 64, text, 21, Muted);
            }
            if (version == null) return;
            Label("MeetingPrintStatus", content, 681, 519, 500, 41,
                MeetingVersionLabel(TextValue(version, "version_id")) + "  ·  출력 전 슬라이서에서 확인하세요.", 21, Muted);
            Button("MeetingExportStl", 678, 574, 221, 61, "STL 저장", () => SaveMeetingExport(false), true);
            Button("MeetingExportZip", 907, 574, 278, 61, "모델·회의록 ZIP", () => SaveMeetingExport(true));
        }
    }
}
