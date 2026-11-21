using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        static readonly Color GuideCaution = new Color32(250, 232, 199, 255);
        static readonly Color GuideWire = new Color32(134, 206, 168, 255);
        bool guideEditingPosition;
        bool guideEditingFrom;
        string guideEditingStep;

        static string GuideStepName(GuideStep step)
        {
            switch(step?.kind)
            {
                case "prepare": return "준비와 전원 차단";
                case "resistor": return "저항 연결";
                case "led": return "LED 연결";
                case "ground": return "접지 연결";
                case "check": return "배선과 점등 확인";
                case "explain": return "교수 보충 설명";
                default: return "실습 단계";
            }
        }

        static string GuidePartName(string kind)
        {
            switch(kind)
            {
                case "resistor": return "저항";
                case "led": return "LED";
                default: return "점퍼선";
            }
        }

        string GuideModeCaption => guideIsExample ? "예시 시연" :
            TraceData?.transferMode == "synthetic" ? "시뮬레이터 기록" : TraceData?.transferMode == "replay" ? "기록 재생" : "교수 현장 기록";
        string GuideCheckCaption => guideRun.physical ? "실물 배선 직접 확인" : "시뮬레이션 판정";

        void DrawGuideReview()
        {
            var guide = ViewGuide;
            if(guideGenerating || guide?.steps == null || guide.steps.Length == 0)
            {
                Heading("실습 단계 만들기", GuideModeCaption);
                RoundBox("GuideDraftStatus", content, 36, 204, 1168, 295, Card, 16);
                BoldLabel("GuideDraftStatusText", content, 66, 235, 1108, 83,
                    guideGenerating ? "교수 시연에서 단계를 정리하고 있어요." : "실습 단계 초안을 열 수 없어요.", 34, Light);
                Label("GuideDraftDetail", content, 67, 321, 1105, 116, guideGenerationStatus, 24, Muted);
                Button("GuideHome", 36, 620, 179, 60, "처음으로", GuideBack);
                if(!guideGenerating)
                    Button("GuideRetryGenerate", 938, 620, 266, 60, "초안 다시 만들기", GenerateGuideDraft, true);
                return;
            }

            var step = guide.steps[guideReviewIndex];
            if(guideEvidenceOpen)
            {
                DrawGuideEvidencePanel(step, true);
                return;
            }
            if(step?.kind != guideEditingStep || guide.approved)
                guideEditingPosition = false;
            var state = guide.approved ? "교수 검토 완료" : "교수 검토";
            string description = guide.approved ? "승인한 가이드로 실습을 시작할 수 있어요." :
                (!guideIsExample && !string.IsNullOrEmpty(guideGenerationStatus) ? guideGenerationStatus :
                    "단계별 발화와 지점을 확인하고 실습 가이드를 승인하세요.");
            Heading("LED 직렬 회로  ·  " + state, GuideModeCaption + "  ·  " + description);

            int pageStart = (guideReviewIndex / 5) * 5;
            int pageSize = Math.Min(5, guide.steps.Length - pageStart);
            int width = (1000 - 8 * (pageSize - 1)) / pageSize;
            for(int i = 0; i < pageSize; i++)
            {
                int index = pageStart + i;
                Button("GuideReviewStep-" + index, 36 + i * (width + 8), 197, width, 46,
                    $"{index + 1}  {GuideStepName(guide.steps[index])}", () =>
                    {
                        guideEditingPosition = false;
                        SelectGuideReviewStep(index);
                    }, index == guideReviewIndex);
            }

            if(pageStart > 0) Button("GuideReviewPageBack", 1044, 197, 72, 46, "이전", () => SelectGuideReviewStep(pageStart - 1));
            if(guide.steps.Length > pageStart + pageSize)
                Button("GuideReviewPageNext", 1132, 197, 72, 46, "이후", () => SelectGuideReviewStep(pageStart + pageSize));
            if(GuideRules.IsWiringStep(step))
            {
                string prompt = guideEditingPosition
                    ? (guideEditingFrom ? "첫 위치를 선택하세요." : "둘째 위치를 선택하세요.")
                    : "발화와 비교할 지점을 표시합니다.";
                DrawGuideBoard(step, 36, 253, 590, 347, step.fromPoint, step.toPoint, prompt, guideEditingPosition,
                    id =>
                    {
                        guideEditingPosition = false;
                        SelectGuideReviewPoint(id, guideEditingFrom);
                    }, true);
            }
            else
            {
                DrawGuidePreparation(step, 36, 253, 590, 347);
            }
            DrawGuideReviewDetail(step, guide.approved);

            Button("GuideHome", 36, 622, 143, 58, "처음으로", GuideBack);
            if(guide.approved)
            {
                Button("GuideStartSimulation", xr ? 692 : 903, 622, xr ? 226 : 301, 58,
                    "시뮬레이션 시작", () => BeginGuidePractice(false), !xr);
                if(xr && HasPhysicalGuide)
                    Button("GuideStartPhysical", 929, 622, 275, 58, "실물 보드에서 시작", () => BeginGuidePractice(true), true);
                if(CanShareGuide)
                    Button("GuideNewDraft", 189, 622, 222, 58, "새 버전 편집", StartNextGuideVersion);
                return;
            }
            if(CanShareGuide && string.IsNullOrEmpty(notice)) notice = TransferPairingHint;
            string approvalIssue = GuideValidationMessage;
            if(!guideIsExample && !string.IsNullOrEmpty(approvalIssue))
                Button("GuideRetryGenerate", 190, 622, 235, 58, "초안 다시 만들기", GenerateGuideDraft);
            if(!string.IsNullOrEmpty(approvalIssue))
            {
                Label("GuideApprovalIssue", content, guideIsExample ? 194 : 439, 616, guideIsExample ? 1008 : 763, 65,
                    approvalIssue, 22, Gold, TextAnchor.MiddleRight);
                return;
            }
            Button("GuideApprove", 925, 621, 279, 59, "검토 완료 · 가이드 승인", ApproveGuide, true);
        }

        void DrawGuideReviewDetail(GuideStep step, bool approved)
        {
            RoundBox("GuideReviewDetail", content, 638, 253, 566, 347, Card, 15);
            BoldLabel("GuideReviewInstruction", content, 657, 262, 527, 64, step.instruction, 27, Light);
            Label("GuideReviewCriterion", content, 658, 326, 526, 42, "완료 기준  ·  " + step.completion, 21, Muted);
            Label("GuideReviewReason", content, 658, 369, 526, 39, step.reason, 20, Muted);
            var note = GuideEvidenceNote(step);
            Label("GuideReviewEvidencePreview", content, 658, 409, 525, 43,
                note == null ? "근거 발화를 선택해 주세요." : $"{When(note.time)}  ·  {PreviewText(ReviewQuote(note), 45)}", 20, Mint);
            if(GuideRules.IsWiringStep(step))
            {
                DrawGuideEndpointControl("GuideEditFrom", 658, 455, 259, step.fromPoint,
                    step.kind == "led" ? "양극" : "첫 위치", approved, true, step.kind);
                DrawGuideEndpointControl("GuideEditTo", 924, 455, 260, step.toPoint,
                    step.kind == "led" ? "음극" : "둘째 위치", approved, false, step.kind);
            }
            if(!approved && step.kind == "led")
            {
                bool hasDirection = step.ledDirection == GuideRules.LedDirection;
                Button("GuideDirection", 658, 531, 256, 52,
                    hasDirection ? "양극·음극 방향 확인됨" : "양극·음극 방향 확인",
                    () => SetGuideLedDirection(true), !hasDirection);
            }
            if(!approved || note != null)
                Button("GuideOpenEvidence", 925, 531, 259, 52, "시연 근거 열기",
                    () => OpenGuideEvidence(step), note == null);
        }

        void DrawGuideEndpointControl(string id, float x, float y, float width, string pointId, string endpoint,
            bool approved, bool from, string kind)
        {
            string name = GuideDisplayPoint(pointId)?.code;
            if(approved)
            {
                BoldLabel(id, content, x + 4, y, width - 8, 63, endpoint + "  " + (name ?? "미정"), 22, Light);
                return;
            }
            bool editing = guideEditingPosition && guideEditingFrom == from;
            Button(id, x, y, width, 60,
                name == null ? endpoint + " 지정 필요" : endpoint + "  " + name, () =>
                {
                    guideEditingPosition = !editing;
                    guideEditingFrom = from;
                    guideEditingStep = kind;
                    Draw();
                }, editing || name == null);
        }

        void DrawGuidePractice()
        {
            var step = GuideRules.Viewed(guideRun);
            bool previous = GuideRules.IsViewingPrevious(guideRun);
            if(step == null)
            {
                DrawGuideResult();
                return;
            }
            if(guideEvidenceOpen)
            {
                DrawGuideEvidencePanel(step, false);
                return;
            }

            var total = guideRun.guide.steps.Length;
            Heading(GuideStepName(step), $"{GuideCheckCaption}  ·  {GuideModeCaption}");
            BoldLabel("GuideStepCount", content, 970, 101, 232, 51,
                $"{(previous ? guideRun.viewedStep : guideRun.stepIndex) + 1} / {total} 단계", 25, Mint, TextAnchor.MiddleRight);
            GuideProgress(36, 191, 1168, guideRun.stepIndex + 1, total);

            bool wiring = GuideRules.IsWiringStep(step);
            if(wiring)
            {
                bool physical = guideRun.physical;
                string prompt = physical ?
                    (guideHasPhysicalMarkers ? "전원을 끄고 실물 위 표시를 따라 연결하세요." : "전원을 끄고 실물과 아래 위치를 비교하세요.") :
                    (step.kind == "led" ? "양극, 음극 순서로 선택하세요." : "첫 위치, 둘째 위치 순서로 선택하세요.");
                DrawGuideBoard(step, 36, 207, 590, 384, guideSelectedFrom, guideSelectedTo, prompt,
                    !physical && !previous, SelectGuidePracticePoint, false);
            }
            else
            {
                DrawGuidePreparation(step, 36, 207, 590, 384);
            }
            DrawGuidePracticeDetail(step, wiring);
            Button("GuideHome", 36, 622, 143, 57, "나중에 계속", GuideBack);
            if(guideRun.stepIndex > 0)
                Button("GuidePreviousStep", 186, 622, 143, 57, "이전 단계", GoToPreviousGuideStep);
            if(previous)
            {
                Button("GuideCurrentStep", 911, 620, 293, 60, "현재 단계로 복귀", GoToCurrentGuideStep, true);
                return;
            }
            if(guideRun.physical && xr)
            {
                Button("GuideRealign", 337, 622, 176, 57, guideHasPhysicalMarkers ? "보드 다시 맞추기" : "보드 맞추기", BeginGuideAlignment);
                Button("GuideRequestHelp", 521, 622, 149, 57, "잘 모르겠어요", RecordGuideHelp);
                if(!transferCapturing)
                    Button("GuideCheckpoint", 678, 622, 131, 57, "확인 사진", () => RequestTransferPhoto("student_checkpoint"));
                if(guideHasPhysicalMarkers) SetPhysicalViewHint(40, 588, 570, 31);
                if(step.kind != "prepare" && !guideRun.registrationValid)
                {
                    Label("GuideAlignmentRequired", content, 829, 620, 375, 59, "계속하려면 학생 보드를 등록하세요.", 21, Gold);
                    return;
                }
            }
            else Button("GuideRequestHelp", 399, 622, 171, 57, "도움 요청 기록", RecordGuideHelp);

            if(step.kind == "check")
            {
                if(guideRun.wiringChecked)
                {
                    if(guideRun.physical)
                        Button("GuideNoLight", 830, 563, 345, 48, "켜지지 않았어요", () => CheckGuidePractice());
                    Button("GuideFinish", 826, 620, 378, 60,
                        guideRun.physical ? "LED가 켜진 것을 확인했어요" : "가상 전원 켜고 점등 확인",
                        () => CheckGuidePractice(true), true);
                }
                else
                    Button("GuidePowerCheck", 832, 620, 372, 60, "전원 차단 상태로 배선 확인",
                        () => CheckGuidePractice(), true);
                return;
            }
            if(wiring && !guideRun.physical)
            {
                if(!string.IsNullOrEmpty(guideSelectedFrom) && !string.IsNullOrEmpty(guideSelectedTo))
                    Button("GuideSubmit", 926, 620, 278, 60, "이 연결 확인", () => CheckGuidePractice(), true);
                else
                    Label("GuideSelectHelp", content, 659, 625, 544, 52,
                        string.IsNullOrEmpty(guideSelectedFrom) ? "보드에서 첫 위치를 선택하세요." : "보드에서 둘째 위치를 선택하세요.",
                        22, Muted, TextAnchor.MiddleRight);
            }
            else
            {
                string caption = wiring ? "연결했어요" : step.kind == "explain" ? "설명을 확인했어요" : "준비물과 전원 차단 확인";
                Button("GuideConfirm", 825, 620, 379, 60, caption, () => CheckGuidePractice(), true);
            }
        }

        void DrawGuidePracticeDetail(GuideStep step, bool wiring)
        {
            RoundBox("GuidePracticeDetail", content, 638, 207, 566, 384, Card, 15);
            string action = string.IsNullOrWhiteSpace(step.teacherNote) || step.kind != "explain"
                ? step.instruction : step.teacherNote;
            if(step.kind == "check" && guideRun.wiringChecked)
                action = guideRun.physical ? "교수가 정한 전류 제한 전원을 켜고 LED의 점등을 확인하세요."
                    : "가상 전원을 켜서 회로의 LED가 켜지는지 확인하세요.";
            BoldLabel("GuideAction", content, 661, 219, 518, 80, action, 29, Light);
            Label("GuideCriterion", content, 661, 297, 517, 57, "완료 기준  ·  " + step.completion, 21, Muted);
            if(wiring)
            {
                string first = GuideDisplayName(GuideRules.AvailablePoint(guideRun, step.fromPoint));
                string second = GuideDisplayName(GuideRules.AvailablePoint(guideRun, step.toPoint));
                string location = (step.kind == "led" ? "양극 " : "") + first + "  →  " +
                    (step.kind == "led" ? "음극 " : "") + second;
                BoldLabel("GuideDestination", content, 661, 355, 517, 48, location, 23, Mint);
            }
            string message = guideFeedback;
            if(!string.IsNullOrWhiteSpace(message))
            {
                RoundBox("GuideFeedbackWell", content, 653, 408, 535, 89, GuideCaution, 9);
                BoldLabel("GuideFeedback", content, 666, 415, 507, 75, message, 22, Gold);
            }
            else
            {
                Label("GuideReason", content, 661, 412, 517, 76,
                    string.IsNullOrWhiteSpace(step.caution) ? step.reason : step.caution, 22, Muted);
            }
            var note = GuideEvidenceNote(step);
            if(note != null)
            {
                if(SourceOf(note) != null)
                    GuideAudioButton(note, 658, 517, 281, 55);
                else
                    Label("GuideAudioUnavailable", content, 660, 517, 274, 55, "저장된 음성을 열 수 없어요.", 20, Muted);
                Button("GuideOpenEvidence", 947, 517, 237, 55, "시연 근거 열기", () => OpenGuideEvidence(step));
            }
        }

        void DrawGuidePreparation(GuideStep step, float x, float y, float width, float height)
        {
            RoundBox("GuideChecklist", content, x, y, width, height, Card, 14);
            bool preparing = step.kind == "prepare";
            if(step.kind == "explain")
            {
                BoldLabel("GuideExplanation", content, x + 25, y + 32, width - 50, height - 67,
                    step.teacherNote, 26, Light, TextAnchor.UpperLeft);
                return;
            }
            bool checkedBeforePower = guideRun != null && screen == ScreenState.GuidePractice && guideRun.wiringChecked;
            BoldLabel("GuideChecklistTitle", content, x + 24, y + 16, width - 48, 53,
                preparing ? "준비물" : "전원을 켜기 전에", 29, Light);
            string[] lines = preparing
                ? new[] { "전원 레일을 미리 연결한 교사용 브레드보드", "규격을 확인한 LED와 직렬 저항", "점퍼선", "전원이 꺼진 저전압·전류 제한 전원" }
                : new[] { "저항이 LED에 직렬로 연결됐어요.", "LED 양극과 음극이 맞아요.", "접지 위치가 맞고 전원 단락이 없어요." };
            for(int i = 0; i < lines.Length; i++)
            {
                float row = y + 73 + 45 * i;
                RoundBox("GuideChecklistDot-" + i, content, x + 27, row + 15, 13, 13, Mint, 6);
                Label("GuideChecklistRow-" + i, content, x + 52, row, width - 82, 43, lines[i], 23, Light);
            }
            string status = preparing && UsesPhysicalGuide && !string.IsNullOrWhiteSpace(DisplayGuide?.kitConditions)
                ? "교수 승인 조건 · " + DisplayGuide.kitConditions :
                !checkedBeforePower ? "연결을 바꿀 때 전원은 꺼져 있어야 합니다." :
                (guideRun.physical ? "LED가 켜지는지 확인하세요." : "가상 배선 점검이 끝났어요.");
            RoundBox("GuidePowerNotice", content, x + 17, y + height - 65, width - 34, 52, Field, 9);
            BoldLabel("GuidePowerNoticeText", content, x + 30, y + height - 64, width - 60, 50, status, 21, Mint);
        }

        void DrawGuideBoard(GuideStep step, float x, float y, float width, float height, string selectedFrom, string selectedTo,
            string prompt, bool interactive, Action<string> choose, bool reviewing)
        {
            var well = RoundBox("GuideBoard", content, x, y, width, height, BoardFace, 14).transform;
            BoldLabel("GuideBoardTitle", well, 22, 7, width - 44, 45, "LED 실습 보드", 26, Silkscreen);
            Label("GuideBoardPrompt", well, 22, 52, width - 44, 31, prompt, 21, BoardCaption);
            var positions = new Dictionary<string, Vector2>();
            var pending = string.IsNullOrEmpty(selectedFrom) || string.IsNullOrEmpty(selectedTo);
            foreach(var point in GuideDisplayPoints)
            {
                float px = width * point.x;
                float py = Mathf.Lerp(121, height - 77, Mathf.InverseLerp(.31f, .77f, point.y));
                positions.Add(point.id, new Vector2(px, py));
            }
            foreach(var group in GuideDisplayPoints.GroupBy(point => point.node))
            {
                var row = group.Select(point => positions[point.id]).ToArray();
                float left = row.Min(point => point.x) - 43;
                float top = row.Min(point => point.y) - 30;
                RoundBox("GuideNode-" + group.Key, well, left, top, row.Max(point => point.x) - left + 43,
                    row.Max(point => point.y) - top + 30, BoardEdge, 11);
            }

            if(!reviewing && guideRun?.connections != null)
                foreach(var connection in guideRun.connections)
                    DrawGuideWire(well, positions, connection.fromPoint, connection.toPoint, connection.kind, GuideWire);
            if(!pending)
                DrawGuideWire(well, positions, selectedFrom, selectedTo, step.kind,
                    !reviewing && !string.IsNullOrEmpty(guideFeedback) ? PadGold : Lime);

            string suggestedFrom = reviewing ? step.fromPoint : GuideRules.AvailablePoint(guideRun, step.fromPoint);
            string suggestedTo = reviewing ? step.toPoint : GuideRules.AvailablePoint(guideRun, step.toPoint);
            foreach(var point in GuideDisplayPoints)
            {
                var center = positions[point.id];
                bool selected = point.id == selectedFrom || point.id == selectedTo;
                bool expected = point.id == suggestedFrom || point.id == suggestedTo;
                float cx = center.x - 34;
                float cy = center.y - 25;
                if(interactive)
                {
                    var hit = Button((reviewing ? "GuideReviewPoint-" : "GuidePracticePoint-") + point.id,
                        cx, cy, 68, 50, point.code, () => choose(point.id), selected, well);
                    if(expected && !selected)
                        hit.background.color = hit.resting = Lime;
                }
                else
                {
                    RoundBox("GuideHole-" + point.id, well, cx, cy, 68, 50, expected ? Lime : Field, 9);
                    BoldLabel("GuideHoleCode-" + point.id, well, cx, cy, 68, 50, point.code, 21, Light, TextAnchor.MiddleCenter);
                }
            }
            Label("GuideBoardLegend", well, 22, height - 42, width - 44, 38,
                "띠 하나로 묶인 구멍은 같은 전기적 행이나 레일이에요.", 19, BoardCaption);
        }

        void DrawGuideWire(Transform boardPanel, Dictionary<string, Vector2> positions, string fromId, string toId,
            string kind, Color color)
        {
            if(!positions.TryGetValue(fromId, out var from) || !positions.TryGetValue(toId, out var to))
                return;
            var vector = to - from;
            if(vector.sqrMagnitude < 1)
                return;
            var line = Box("GuideConnection-" + kind, boardPanel, from.x, from.y, vector.magnitude, 6, color);
            line.rectTransform.localEulerAngles = new Vector3(0, 0, -Mathf.Atan2(vector.y, vector.x) * Mathf.Rad2Deg);
            var middle = (from + to) * .5f;
            string part = GuidePartName(kind);
            if(kind == "ground")
                return;
            RoundBox("GuidePart-" + kind, boardPanel, middle.x - 32, middle.y - 16, 64, 32, color, 6);
            BoldLabel("GuidePartName-" + kind, boardPanel, middle.x - 32, middle.y - 16, 64, 32, part, 18,
                BoardFace, TextAnchor.MiddleCenter);
        }

        void GuideProgress(float x, float y, float width, int progress, int total)
        {
            const int gap = 6;
            float segment = (width - (total - 1) * gap) / total;
            for(int i = 0; i < total; i++)
                RoundBox("GuideProgress-" + i, content, x + i * (segment + gap), y, segment, 6, i < progress ? Mint : Divider, 3);
        }

        void DrawGuideEvidencePanel(GuideStep step, bool reviewing)
        {
            var note = GuideEvidenceNote(step);
            Heading("교수의 시연 근거", GuideStepName(step) + "  ·  " + GuideModeCaption);
            Button("GuideCloseEvidence", 962, 92, 242, 54, "현재 단계로 돌아가기", CloseGuideEvidence, true);
            RoundBox("GuideEvidenceSourceCard", content, 36, 200, 590, 371, Card, 15);
            BoldLabel("GuideEvidenceSourceTime", content, 58, 214, 541, 45,
                note == null ? "연결된 발화 없음" : note.sourceMode == "teacher_edit" ? "교수가 직접 입력한 설명" :
                note.sourceMode == "manual_transcript" ? $"교수가 옮긴 녹음 구간 {When(note.time)}" : $"발화 {When(note.time)}", 26, Mint);
            if(note == null)
            {
                Label("GuideNoEvidence", content, 60, 268, 534, 184, "녹음에서 이 단계를 설명하는 발화를 찾아 연결하세요.", 26, Muted);
            }
            else
            {
                int pages = DrawReading("GuideEvidenceSpeech", 58, 264, 546, 207, ReviewQuote(note), 27,
                    guideEvidencePage, out int page);
                guideEvidencePage = page;
                if(pages > 1)
                    ReaderArrows("GuideEvidencePage", 338, 497, 263, 52, page, pages, index =>
                    {
                        guideEvidencePage = index;
                        Draw();
                    });
                if(SourceOf(note) != null)
                    GuideAudioButton(note, 58, 497, 273, 52);
                else
                    Label("GuideEvidenceAudioUnavailable", content, 58, 497, 273, 52, "저장된 음성을 열 수 없어요.", 20, Muted);
            }
            DrawGuideEvidencePhoto(step, note);
            if(!reviewing && guideRun.physical && !GuideRules.IsViewingPrevious(guideRun))
            {
                if (TraceData.captures.Any(photo => photo.captureId == TraceData.latestCheckpoint && photo.stepId == step.stepId &&
                    photo.registrationRevision == guideRun.registrationRevision && photo.trackingEpoch == guideRun.trackingEpoch))
                    Button("GuideToggleCheckpoint", 36, 622, 270, 58,
                        guideViewingCheckpoint ? "교수 사진 보기" : "내 확인 사진 보기", () =>
                        {
                            guideViewingCheckpoint = !guideViewingCheckpoint;
                            guideEvidenceFrame = 0;
                            Draw();
                        });
                if(!transferCapturing)
                    Button("GuideHelpCapture", 638, 622, 258, 58, "지금 상태 촬영", () => RequestTransferPhoto("student_checkpoint"), true);
                Button("GuideVisualCompare", 913, 622, 291, 58, "현재 사진 비교", RequestCoarseComparison);
            }
            if(reviewing && ViewGuide?.approved == false && !guideIsExample)
            {
                Button("GuidePreviousEvidence", 36, 620, 202, 60, "이전 발화 연결", () => ChangeGuideEvidenceSource(step, -1));
                Button("GuideNextEvidence", 248, 620, 202, 60, "다음 발화 연결", () => ChangeGuideEvidenceSource(step, 1));
            }
        }

        void DrawGuideEvidencePhoto(GuideStep step, TraceNote note)
        {
            RoundBox("GuideEvidencePhotoCard", content, 638, 200, 566, 371, Card, 15);
            string[] paths = guideViewingCheckpoint
                ? TraceData.captures.Where(photo => photo.captureId == TraceData.latestCheckpoint && photo.stepId == step.stepId)
                    .Select(photo => photo.path).ToArray()
                : GuidePhotoPaths(step, note);
            int count = paths.Length;
            guideEvidenceFrame = Mathf.Clamp(guideEvidenceFrame, 0, Mathf.Max(0, count - 1));
            var texture = count == 0 ? null : LoadEvidenceTexture(paths[guideEvidenceFrame]);
            const int x = 658, y = 219, width = 526, height = 258;
            RoundBox("GuideEvidenceFrameWell", content, x, y, width, height, PhotoMat, 8);
            if(texture == null)
            {
                Label("GuideNoFrame", content, x + 15, y + 12, width - 30, height - 24,
                    count == 0 ? "이 발화에 저장된 사진이 없어요." : "장면 파일을 열 수 없어요.",
                    22, Color.white, TextAnchor.MiddleCenter);
            }
            else
            {
                float scale = Mathf.Min((width - 6f) / texture.width, (height - 6f) / texture.height);
                var displayWidth = texture.width * scale;
                var displayHeight = texture.height * scale;
                var image = Rect("GuideEvidenceFrame", content, x + (width - displayWidth) / 2,
                    y + (height - displayHeight) / 2, displayWidth, displayHeight).gameObject.AddComponent<RawImage>();
                image.texture = texture;
                image.raycastTarget = false;
            }
            if(count > 0)
            {
                string path = paths[guideEvidenceFrame];
                string label = guideViewingCheckpoint ? "내 확인 사진 · 전기적 판정 없음" :
                    !string.IsNullOrEmpty(path) && frameInfo.TryGetValue(path, out var frame)
                    ? "촬영 " + When(frame.time) : TraceData.captures.Any(photo => photo.path == path && photo.phase == "setup")
                        ? "보드 등록 사진" : "교수가 연결한 시연 사진";
                Label("GuideEvidenceFrameLabel", content, 658, 481, 526, count > 1 ? 27 : 64, label, 20, Muted,
                    TextAnchor.MiddleCenter);
            }
            if(count > 1)
                ReaderArrows("GuideEvidenceFramePage", 658, 513, 526, 43, guideEvidenceFrame, count, index =>
                {
                    guideEvidenceFrame = index;
                    Draw();
                });
        }

        void DrawGuideResult()
        {
            if(guideRun == null)
            {
                Heading("실습 결과", "저장된 실습 결과를 찾을 수 없어요.");
                Button("GuideHome", 36, 620, 179, 60, "처음으로", GuideBack);
                return;
            }
            int total = guideRun.guide.steps.Length;
            Heading(guideRun.lit ? "LED가 켜졌어요" : "LED 실습 진행", GuideCheckCaption + "  ·  " + GuideModeCaption +
                (guideRun.physical && guideRun.completed ? "  ·  전원을 끄고 실습을 마무리하세요." : ""));
            RoundBox("GuideResultCircuit", content, 36, 199, 1168, 111, Card, 15);
            RoundBox("GuideResultLEDGlow", content, 55, 221, 60, 60, guideRun.lit ? Field : Card, 30);
            RoundBox("GuideResultLED", content, 63, 229, 44, 44, guideRun.lit ? Lime : Divider, 22);
            RoundBox("GuideResultLEDCore", content, 76, 242, 18, 18, guideRun.lit ? Card : Field, 9);
            BoldLabel("GuideResultCompletion", content, 123, 220, 1051, 61,
                $"{Mathf.Min(guideRun.stepIndex, total)} / {total} 단계 완료", 31, Light);
            string correction = guideRun.physical ? "직접 확인" :
                (guideRun.wrongAttempts == 0 ? "오배선 없음" : $"{guideRun.correctionSeconds:0.0}초");
            ResultMetric("GuideResultCorrection", 36, "오류 수정 시간", correction,
                guideRun.physical ? "실물 배선은 직접 확인했어요." : $"오배선 뒤 올바른 연결까지의 시간 · 오배선 {guideRun.wrongAttempts}회");
            ResultMetric("GuideResultHints", 433, "힌트 사용", $"{guideRun.hintUses}회", "교수 시연 근거 또는 원음 재생을 연 횟수");
            ResultMetric("GuideResultRequests", 830, "도움 요청", $"{guideRun.helpRequests}회",
                guideRun.helpRequests == 0 && guideRun.completed ? "교수자 도움 요청 없이 완료했어요." : "실습 중 도움을 요청한 횟수");
            Button("GuideHome", 36, 620, 179, 60, "처음으로", GuideBack);
            Button("GuideReviewAgain", 958, 620, 246, 60, "가이드 다시 보기", GuideResumeReview, true);
        }
    }
}
