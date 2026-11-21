using System;
using System.Collections.Generic;
using System.Linq;

namespace ContextPin
{
    [Serializable] public sealed class Experience
    {
        public int schemaVersion = 1;
        public string id = Guid.NewGuid().ToString("N");
        public string createdUtc = DateTime.UtcNow.ToString("o");
        public string sourceBoardId, sourceObjectId, targetRole;
        public string context, goal, conditionKey, actionKey, actionText, expectedOutcome;
        public string transcript, sourceQuote, imagePath, imageKind, intent = "context";
        public string provider = "demo", reviewReason = "";
        public bool needsReview, approved, shared;
        // Expected outcomes are instructions, never evidence of actual success.
        public List<Outcome> outcomes = new List<Outcome>();
    }

    [Serializable] public sealed class Outcome
    {
        public string utc = DateTime.UtcNow.ToString("o");
        public string boardId, participant, selectedRole, observedState, feedback, condition;
        public bool success;
        public string verification = "virtual-board";
        public double elapsedSeconds;
    }

    [Serializable] public sealed class TargetDefinition
    {
        public string id, role, label;
        public float x, y;
        public bool pressable;
    }

    [Serializable] public sealed class BoardDefinition
    {
        public string id, name;
        public string task = "indicator-check-v1";
        public List<TargetDefinition> targets = new List<TargetDefinition>();
        public bool yellowOn = true;
        public bool blueOn;

        public TargetDefinition FindRole(string role)
        {
            var found = targets.Where(t => t.role == role).ToArray();
            return found.Length == 1 ? found[0] : null;
        }
        public bool Press(string role)
        {
            var target = FindRole(role);
            if (target == null || !target.pressable) return false;
            if (role == "confirm" && yellowOn) { blueOn = true; return true; }
            if (role == "reset") { blueOn = false; return false; }
            return false;
        }
    }

    public static class DemoBoards
    {
        public const string Explanation = "노란 표시가 켜지면 여기의 확인 버튼을 한 번 누르고, 파란 표시가 뜨는지 보세요.";
        public static BoardDefinition Create(string id)
        {
            var b = new BoardDefinition { id = id, name = id == "A" ? "SOURCE / 보드 A" : id == "B" ? "TRANSFER / 보드 B" : "INCOMPATIBLE / 보드 C" };
            var roles = id == "A"
                ? new[] { "confirm", "cancel", "reset", "yellow", "blue", "power" }
                : new[] { "reset", "cancel", "confirm", "power", "yellow", "blue" };
            var labels = new Dictionary<string,string> { ["confirm"]="확인", ["cancel"]="취소", ["reset"]="초기화", ["yellow"]="노란 표시", ["blue"]="파란 표시", ["power"]="전원 표시" };
            for (int i=0;i<roles.Length;i++)
            {
                var role = id == "C" && roles[i] == "confirm" ? "auxiliary" : roles[i];
                b.targets.Add(new TargetDefinition { id=id+"-"+(i+1), role=role,
                    label=labels.ContainsKey(role)?labels[role]:"보조 버튼",
                    x=(i%3-1)*0.38f, y=i<3?0.12f:-0.16f, pressable=i<3 });
            }
            return b;
        }
    }

    public sealed class TransferDecision
    {
        public bool allowed;
        public string reason;
        public TargetDefinition target;
    }

    public static class ExperienceRules
    {
        public static readonly string[] Roles = { "confirm", "cancel", "reset", "yellow", "blue", "power", "auxiliary" };
        public static string Validate(Experience card)
        {
            if (card == null) return "기록이 없습니다.";
            if (card.schemaVersion != 1) return "지원하지 않는 기록 버전입니다.";
            if (string.IsNullOrWhiteSpace(card.id) || string.IsNullOrWhiteSpace(card.sourceBoardId)) return "기록의 출처가 없습니다.";
            if (string.IsNullOrWhiteSpace(card.transcript) || string.IsNullOrWhiteSpace(card.sourceQuote) ||
                card.transcript.IndexOf(card.sourceQuote, StringComparison.Ordinal) < 0) return "원문에서 근거 문장을 확인할 수 없습니다.";
            if (!Roles.Contains(card.targetRole)) return "대상의 역할을 확인해야 합니다.";
            if (card.context != "indicator-check-v1") return "지원하지 않는 과업입니다.";
            if (card.conditionKey != "yellow_on" || card.actionKey != "press_once" || card.expectedOutcome != "blue_on")
                return "실습 보드의 조건·행동·결과 범위를 벗어났습니다.";
            if (string.IsNullOrWhiteSpace(card.actionText) || string.IsNullOrWhiteSpace(card.goal)) return "행동 또는 목표가 비어 있습니다.";
            return "";
        }

        public static string Approve(Experience card)
        {
            var error = Validate(card);
            if (error.Length > 0) return error;
            if (card.needsReview || card.intent == "question") return "미해결 질문 또는 불명확한 내용이 있어 공유를 보류합니다.";
            card.approved = card.shared = true;
            return "";
        }

        public static TransferDecision Transfer(Experience card, BoardDefinition board)
        {
            string reason = Validate(card);
            if (reason.Length == 0 && (!card.approved || !card.shared || card.needsReview || card.intent == "question")) reason="설명자의 검토와 공유가 필요합니다.";
            if (reason.Length == 0 && (board == null || board.task != card.context)) reason="현재 과업에 적용할 수 없습니다.";
            var target = board?.FindRole(card?.targetRole);
            if (reason.Length == 0 && (target == null || !target.pressable)) reason="대응 대상이 없거나 중복되어 적용을 보류합니다.";
            if (reason.Length == 0 && !board.yellowOn) reason="노란 표시 조건이 충족되지 않아 적용을 보류합니다.";
            return new TransferDecision { allowed=reason.Length==0, reason=reason, target=reason.Length==0?target:null };
        }

        public static Experience DemoCapture(string boardId, TargetDefinition target, string intent, string imagePath)
        {
            bool valid = target.role == "confirm" && intent != "question";
            return new Experience { sourceBoardId=boardId, sourceObjectId=target.id, targetRole=target.role,
                context="indicator-check-v1", goal="상태 확인", conditionKey="yellow_on", actionKey="press_once",
                actionText="확인 버튼을 한 번 누르고 파란 표시를 확인하세요.", expectedOutcome="blue_on",
                transcript=DemoBoards.Explanation, sourceQuote=DemoBoards.Explanation, intent=intent,
                imagePath=imagePath, imageKind="virtual-board", provider="demo",
                needsReview=!valid, reviewReason=valid?"":"지정한 대상 또는 미해결 질문을 확인하세요." };
        }
    }

    [Serializable] public sealed class SessionMetrics
    {
        public string sessionId=Guid.NewGuid().ToString("N");
        public string startedUtc=DateTime.UtcNow.ToString("o");
        public int captures, aiCaptureRequests, wrongSelections, completedTrials, successfulTrials;
        public string participant="P01", condition="relay";
    }

    [Serializable] public sealed class SessionData
    {
        public SessionMetrics metrics = new SessionMetrics();
        public List<Experience> cards = new List<Experience>();
    }
}
