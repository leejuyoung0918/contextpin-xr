using System.Linq;
using UnityEngine.UI;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        void DrawGuideConsent()
        {
            Heading("교수 시연 기록", "설명자와 참여자의 동의를 확인한 뒤 기록을 시작하세요.");
            RoundBox("Guide consent", content, 36, 207, 1168, 335, Card, 14);
            BoldLabel("Recording subject", content, 67, 226, 1101, 64, "브레드보드 LED 직렬 회로", 37, Light);
            Label("Lecture sequence", content, 67, 305, 1101, 70,
                "전원 차단과 준비물, 저항, LED 방향, 접지, 점등 점검을 차례로 시연하세요.", 26, Light);
            Box("Consent rule", content, 67, 398, 1101, 2, Field);
            Label("Guide capture scope", content, 67, 410, 1101, 111,
                "음성과 요청한 장면은 기기에 저장됩니다. 연결한 교수 PC에서 확인할 수 있으며, 설정된 AI에는 전사나 명시적으로 요청한 비교에 필요한 자료를 보냅니다. 기록 화면의 종료 버튼으로 중지할 수 있어요.",
                24, Muted);
            Button("Back", 36, 603, 187, 64, "돌아가기", Home);
            Button("StartRecording", 740, 601, 464, 68,
                xr ? "동의하고 보드 맞추기" : "동의하고 설명 기록 시작", StartPressed, true);
        }

        void DrawGuideAlign() => DrawPhysicalGuideAlign();

        void DrawGuideRecording()
        {
            Heading("교수 시연 기록 중", CanShareGuide ? TransferPairingHint : "설명과 직접 표시한 연결 위치를 기록합니다.");
            DrawGuideBoard(new GuideStep(), 36, 200, 590, 354, selectedTransferTerminal, "",
                "교수가 짚은 실제 구멍을 이 도면에서 표시하세요.", true, MarkGuideDuringRecording, true);
            if (xr && aligned && !string.IsNullOrEmpty(selectedTransferTerminal))
                PlacePhysicalGuideTargets(new[] { selectedTransferTerminal });

            RoundBox("Guide recording transcript", content, 642, 200, 562, 354, Card, 14);
            Label("Latest guide speech", content, 669, 213, 503, 38,
                TransferSpeechHint + " · 저장 사진 " + TraceData.captures.Count, 21, Mint);
            recordSpeech = BoldLabel("Speech", content, 669, 259, 503, 169, "", 27, Light);
            recordCount = Label("Count", content, 669, 434, 503, 44, "", 21, Muted);
            if (xr)
            {
                Button("GuidePhotoBefore", 669, 487, 152, 51, "연결 전", () => SetRecordingPhase("before_action"), recordingPhase == "before_action");
                Button("GuidePhotoAfter", 834, 487, 152, 51, "연결 후", () => SetRecordingPhase("after_action"), recordingPhase == "after_action");
                Button("GuidePhotoQuestion", 999, 487, 177, 51, "모호한 부분", () => SetRecordingPhase("clarification"), recordingPhase == "clarification");
                if (!transferCapturing)
                    Button("GuidePhotoCapture", 36, 604, 297, 66, "이 장면 저장", () => RequestTransferPhoto(recordingPhase), true);
            }
            Button("EndRecording", 803, 604, 401, 70,
                transferCapturing ? "사진 저장 중" : "기록 종료 · 실습 초안 만들기", EndRecording, !transferCapturing);
        }

        void MarkGuideDuringRecording(string id)
        {
            var point = GuideDisplayPoint(id);
            if (point == null) return;
            selectedTransferTerminal = id;
            if (capture.Recording) capture.Pin(id);
            notice = point.code + (capture.Recording ? " 위치를 표시했습니다." : " · 다음 사진의 참고 위치로 표시했습니다.");
            Draw();
        }

        void DrawGuideLibraryRow(TraceSession session, int index)
        {
            float y = 201 + index * 126;
            bool approved = session.guide?.approved == true;
            var lastRun = session.guideRuns.LastOrDefault();
            RoundBox("Guide library row " + index, content, 36, y, 1168, 111, Card, 12);
            BoldLabel("Guide library title", content, 59, y + 12, 426, 43,
                session.guide?.title ?? "브레드보드 LED 실습", 25, Light);
            Label("Guide library mode", content, 59, y + 57, 426, 41,
                session.mode == "guide-example" ? "예시 시연" : "교수 시연", 21, Muted);
            BoldLabel("Guide library state", content, 496, y + 14, 300, 41,
                approved ? "승인된 실습" : "교수 검토 대기", 23, approved ? Mint : Gold);
            if (lastRun != null)
            {
                Label("Guide library progress", content, 496, y + 58, 299, 38,
                    $"{lastRun.stepIndex} / {lastRun.guide?.steps?.Length ?? 5} 단계 완료", 21, Muted);
                Button("GuideResume-" + session.id, 973, y + 28, 211, 57,
                    lastRun.completed ? "최근 결과 보기" : "실습 이어가기", () =>
                    {
                        OpenGuideSession(session);
                        if (!GuideRules.IsCurrentVersion(lastRun, lastRun.guide)) return;
                        guideRun = lastRun;
                        if (guideRun.physical && !guideRun.completed)
                            GuideRules.InvalidatePracticeBoard(guideRun, guideRun.trackingEpoch + 1);
                        EnterGuidePractice();
                    }, true);
            }
            Button("Saved-" + session.id, lastRun == null ? 973 : 807, y + 28, lastRun == null ? 211 : 154, 57,
                approved ? "가이드 열기" : "초안 검토", () =>
                {
                    OpenGuideSession(session);
                    if (guideStudentLibrary && HasPhysicalGuide && xr) BeginGuidePractice(true);
                }, lastRun == null);
        }
    }
}
