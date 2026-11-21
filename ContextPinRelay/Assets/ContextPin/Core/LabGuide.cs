using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ContextPin
{
    [Serializable]
    public sealed class GuidePoint
    {
        public string id, label, code, node;
        public float x, y;
        public bool canonical;
    }

    public static class GuideBoard
    {
        public const string Id = "led-series-v1";

        // A and B are labels on the practice board's two distinct electrical rows.
        public static readonly IReadOnlyList<GuidePoint> Points = Array.AsReadOnly(new[]
        {
            Contact("plus", 1, "플러스 레일", "+1", .12f, .31f),
            Contact("plus", 2, "플러스 레일", "+2", .12f, .60f),
            Contact("a", 1, "A행", "A1", .39f, .31f),
            Contact("a", 2, "A행", "A2", .60f, .31f),
            Contact("b", 1, "B행", "B1", .39f, .54f),
            Contact("b", 2, "B행", "B2", .60f, .54f),
            Contact("c", 1, "C행", "C1", .39f, .77f),
            Contact("c", 2, "C행", "C2", .60f, .77f),
            Contact("gnd", 1, "접지 레일", "−1", .87f, .31f),
            Contact("gnd", 2, "접지 레일", "−2", .87f, .60f)
        });

        static GuidePoint Contact(string node, int number, string row, string code, float x, float y)
        {
            return new GuidePoint
            {
                id = node + "-" + number, node = node, code = code,
                label = row + " " + number + "번", x = x, y = y, canonical = number == 1
            };
        }

        public static GuidePoint Find(string id) => Points.FirstOrDefault(point => point.id == id);

        public static string NameOf(string id) => Find(id)?.label ?? "미지정";

        public static bool MatchesNode(string pointId, string expectedPointId)
        {
            var point = Find(pointId);
            return point != null && point.node == Find(expectedPointId)?.node;
        }
    }

    [Serializable]
    public sealed class GuideStep
    {
        public string kind, sourceNoteId, sourceQuote;
        public string fromPoint, toPoint, ledDirection;
        public string instruction, reason, completion;
        public string stepId, teacherNote, caution, rationaleSource, confirmationQuestion;
        public string[] prerequisites, evidenceIds, teacherEditedFields, unresolvedFields;

        internal GuideStep Copy()
        {
            var step = (GuideStep)MemberwiseClone();
            step.prerequisites = prerequisites?.ToArray();
            step.evidenceIds = evidenceIds?.ToArray();
            step.teacherEditedFields = teacherEditedFields?.ToArray();
            step.unresolvedFields = unresolvedFields?.ToArray();
            return step;
        }
    }

    [Serializable]
    public sealed class GuideDraft
    {
        public GuideStep[] steps;
    }

    [Serializable]
    public sealed class LabGuide
    {
        public string id, sourceSessionId, boardId, title;
        public bool example, approved;
        public GuideStep[] steps;
        public int schemaVersion, version, draftRevision, boardRevision;
        public string boardHash, contentHash, approvedAt, approvedBy, sourceMode;
        public string demonstrationBoardInstanceId, kitConditions;
        public bool kitConfirmed;
        public LabBoardDefinition boardDefinition;
        public GuideEvidenceRef[] evidence;

        internal LabGuide Copy()
        {
            var snapshot = (LabGuide)MemberwiseClone();
            snapshot.steps = steps?.Select(step => step?.Copy()).ToArray();
            snapshot.boardDefinition = GuideRules.ExplicitBoard(this)?.Copy();
            snapshot.evidence = evidence?.Select(item => item?.Copy()).ToArray();
            return snapshot;
        }
    }

    [Serializable]
    public sealed class GuideConnection
    {
        public string kind, fromPoint, toPoint;
    }

    [Serializable]
    public sealed class GuideRun
    {
        public string guideId, startedUtc, completedUtc;
        public LabGuide guide;
        public bool physical, completed, wiringChecked, lit;
        public int stepIndex, wrongAttempts, hintUses, helpRequests;
        public double correctionSeconds;
        public string errorStartedUtc, fromPoint, toPoint, feedback;
        public List<GuideConnection> connections = new List<GuideConnection>();
        public int schemaVersion, guideVersion;
        public string runId, guideHash, sourceMode, studentBoardInstanceId, completionBasis;
        public int registrationRevision, trackingEpoch;
        public bool registrationValid;
        public int viewedStep = -1;
        public string pausedUtc;
        public double pausedSeconds, errorPausedSeconds;
        public List<GuidePracticeEvent> events = new List<GuidePracticeEvent>();
        public List<GuideVerificationRecord> verifications = new List<GuideVerificationRecord>();
    }

    public static partial class GuideRules
    {
        public const string LedDirection = "anode_to_cathode";
        public static readonly IReadOnlyList<string> Kinds = Array.AsReadOnly(new[]
        {
            "prepare", "resistor", "led", "ground", "check"
        });

        public static bool IsWiringStep(GuideStep step) =>
            step?.kind == "resistor" || step?.kind == "led" || step?.kind == "ground";

        static bool CorrectTopology(string kind, string from, string to)
        {
            var first = GuideBoard.Find(from)?.node;
            var second = GuideBoard.Find(to)?.node;
            switch (kind)
            {
                case "resistor": return (first == "plus" && second == "a") || (first == "a" && second == "plus");
                case "led": return first == "a" && second == "b";
                case "ground": return (first == "b" && second == "gnd") || (first == "gnd" && second == "b");
                default: return false;
            }
        }

        public static string StepIssue(GuideStep step, IEnumerable<string> sourceNoteIds = null, LabGuide guide = null)
        {
            if (step == null || (!Kinds.Contains(step.kind) && step.kind != "explain")) return "실습 단계를 확인하세요.";
            if ((step.unresolvedFields ?? Array.Empty<string>()).Any(value => !string.IsNullOrWhiteSpace(value)))
                return string.IsNullOrWhiteSpace(step.confirmationQuestion) ? "미확정된 항목을 확인하세요." : step.confirmationQuestion;
            bool humanExplanation = !string.IsNullOrWhiteSpace(step.teacherNote) &&
                (step.teacherEditedFields ?? Array.Empty<string>()).Contains("teacherNote");
            if (!humanExplanation && (string.IsNullOrWhiteSpace(step.sourceNoteId) ||
                (sourceNoteIds != null && !sourceNoteIds.Contains(step.sourceNoteId))))
                return "이 단계의 근거가 되는 교수 발화를 선택하세요.";
            if (IsWiringStep(step))
            {
                if (FindPoint(guide, step.fromPoint) == null || FindPoint(guide, step.toPoint) == null)
                    return "보드에서 연결할 양쪽 위치를 지정하세요.";
                if (step.kind == "led" && step.ledDirection != LedDirection)
                    return "LED의 양극(애노드)과 음극(캐소드) 방향을 확인하세요.";
                if (!MatchesTopology(guide, step.kind, step.fromPoint, step.toPoint))
                    return "선택한 위치가 이 실습 회로의 배선과 맞지 않습니다.";
            }
            if (string.IsNullOrWhiteSpace(step.instruction) || string.IsNullOrWhiteSpace(step.reason) ||
                string.IsNullOrWhiteSpace(step.completion))
                return "행동, 이유, 완료 기준을 확인하세요.";
            return "";
        }

        public static string Validate(LabGuide guide, IEnumerable<string> sourceNoteIds = null)
        {
            if (guide == null || string.IsNullOrWhiteSpace(guide.id) ||
                string.IsNullOrWhiteSpace(guide.sourceSessionId) || guide.steps == null)
                return "LED 실습의 다섯 단계와 원본 기록을 확인하세요.";
            if (guide.boardId != GuideBoard.Id && !ValidBoardProfile(guide))
                return "현재 가이드에 지정된 실제 보드 프로필을 확인하세요.";
            if (guide.boardId == GuideBoard.Id && ExplicitBoard(guide) != null)
                return "가이드와 실습 보드의 프로필이 일치해야 합니다.";
            if (ExplicitBoard(guide) != null && (!guide.kitConfirmed || string.IsNullOrWhiteSpace(guide.kitConditions)))
                return "교수가 확인한 저전압·전류 제한 실습 키트 조건을 입력하세요.";
            if (guide.steps.Length < Kinds.Count || guide.steps.Length > 20 ||
                guide.steps.First()?.kind != "prepare" || guide.steps.Last()?.kind != "check" ||
                Kinds.Any(kind => guide.steps.Count(step => step?.kind == kind) != 1))
                return "준비와 최종 전원 점검 사이에 세 가지 연결 단계가 있어야 합니다.";
            if (guide.approved && !string.IsNullOrEmpty(guide.contentHash) && !VerifyApproved(guide))
                return "승인한 가이드의 내용이 변경되었습니다. 새 초안으로 검토하세요.";

            var used = new HashSet<string>(StringComparer.Ordinal);
            var prior = new HashSet<string>(StringComparer.Ordinal);
            var evidenceIds = new HashSet<string>((guide.evidence ?? Array.Empty<GuideEvidenceRef>())
                .Where(item => item != null && !string.IsNullOrEmpty(item.id)).Select(item => item.id), StringComparer.Ordinal);
            if (guide.sourceMode == "real" && (guide.evidence ?? Array.Empty<GuideEvidenceRef>())
                .Any(item => item != null && (item.provenance == "synthetic" || item.provenance == "simulator" || item.provenance == "replay")))
                return "실물 기록의 근거에서 시뮬레이션과 재생 자료를 빼 주세요.";
            for (int index = 0; index < guide.steps.Length; index++)
            {
                var step = guide.steps[index];
                string issue = StepIssue(step, sourceNoteIds, guide);
                if (issue.Length > 0) return $"{index + 1}단계 · {issue}";
                if ((step.prerequisites ?? Array.Empty<string>()).Any(id => !prior.Contains(id)) ||
                    !prior.Add(StepIdentity(step, index)))
                    return $"{index + 1}단계 · 먼저 완료할 단계와 순서를 확인하세요.";
                if ((step.evidenceIds ?? Array.Empty<string>()).Any(id => !evidenceIds.Contains(id)))
                    return $"{index + 1}단계 · 연결된 원문이나 장면이 삭제되었는지 확인하세요.";
                if (IsWiringStep(step) && (!used.Add(FindPoint(guide, step.fromPoint).id) || !used.Add(FindPoint(guide, step.toPoint).id)))
                    return $"{index + 1}단계 · 앞 단계와 다른 구멍을 같은 행에서 골라 주세요.";
            }
            return "";
        }

        public static string Approve(LabGuide guide, IEnumerable<string> sourceNoteIds, DateTime? utc = null,
            string actor = "instructor")
        {
            if (guide?.approved == true && !string.IsNullOrEmpty(guide.contentHash))
                return VerifyApproved(guide) ? "" : "승인한 내용이 변경되었습니다. 새 버전으로 승인하세요.";
            string issue = Validate(guide, sourceNoteIds);
            if (issue.Length == 0) SealApproval(guide, utc ?? DateTime.UtcNow, actor);
            return issue;
        }

        public static GuideRun NewRun(LabGuide guide, bool physical, DateTime utc)
        {
            if (guide == null || !guide.approved) return null;
            Migrate(guide);
            if (!VerifyApproved(guide) || Validate(guide).Length > 0) return null;
            return new GuideRun
            {
                guideId = guide.id, guide = guide.Copy(), physical = physical, startedUtc = Timestamp(utc),
                schemaVersion = TransferSchemaVersion, runId = NewId(), guideVersion = guide.version,
                guideHash = guide.contentHash, sourceMode = physical ? guide.sourceMode : "simulation"
            };
        }

        public static GuideStep Current(GuideRun run)
        {
            var steps = run?.guide?.steps;
            return steps != null && !run.completed && run.stepIndex >= 0 && run.stepIndex < steps.Length
                ? steps[run.stepIndex]
                : null;
        }

        public static bool SelectPoint(GuideRun run, string pointId)
        {
            if (!IsWiringStep(Current(run)) || IsViewingPrevious(run) || run.physical) return false;
            var point = FindPoint(run.guide, pointId);
            if (point == null) return false;
            pointId = point.id;
            if (string.IsNullOrEmpty(run.fromPoint) || !string.IsNullOrEmpty(run.toPoint))
            {
                run.fromPoint = pointId;
                run.toPoint = "";
            }
            else run.toPoint = pointId;
            return true;
        }

        public static void ClearSelection(GuideRun run)
        {
            run.fromPoint = "";
            run.toPoint = "";
        }

        static bool IsOccupied(GuideRun run, string pointId) => run.connections.Any(connection =>
            connection.fromPoint == pointId || connection.toPoint == pointId);

        public static string AvailablePoint(GuideRun run, string expectedPointId)
        {
            if (run == null || run.physical || !IsOccupied(run, expectedPointId)) return expectedPointId;
            return BoardPoints(run.guide).FirstOrDefault(point =>
                SameNet(run.guide, point.id, expectedPointId) && !IsOccupied(run, point.id))?.id ?? expectedPointId;
        }

        public static bool Submit(GuideRun run, DateTime utc, string expectedStepId = null, string eventId = null)
        {
            var step = Current(run);
            if (!IsWiringStep(step) || run.physical || !CanAdvance(run, step, expectedStepId, eventId)) return false;
            if (FindPoint(run.guide, run.fromPoint) == null || FindPoint(run.guide, run.toPoint) == null)
            {
                run.feedback = "보드에서 연결 양끝을 각각 선택하세요.";
                return false;
            }

            string first = PointName(run.guide, AvailablePoint(run, step.fromPoint));
            string second = PointName(run.guide, AvailablePoint(run, step.toPoint));
            if (IsOccupied(run, run.fromPoint) || IsOccupied(run, run.toPoint))
                return RecordError(run, utc, $"이미 사용 중인 구멍입니다. 같은 행의 빈 구멍을 고르세요. 위치 · {first}, {second}", eventId);
            if (step.kind == "led" && SameNet(run.guide, run.fromPoint, step.toPoint) &&
                SameNet(run.guide, run.toPoint, step.fromPoint))
                return RecordError(run, utc, $"LED 방향이 반대예요. 양극을 {first}, 음극을 {second}에 연결하세요.", eventId);
            if (!MatchesTopology(run.guide, step.kind, run.fromPoint, run.toPoint))
                return RecordError(run, utc, $"연결할 전기적 행이 달라요. {first}와 {second}를 연결하세요.", eventId);

            run.connections.Add(new GuideConnection { kind = step.kind, fromPoint = run.fromPoint, toPoint = run.toPoint });
            RecordVerification(run, step, utc, "placement", "simulator_verified", "simulator", "supported", eventId);
            if (step.kind == "led")
                RecordVerification(run, step, utc, "polarity", "simulator_verified", "simulator", "supported");
            Advance(run, utc);
            return true;
        }

        static bool RecordError(GuideRun run, DateTime utc, string feedback, string eventId)
        {
            RecordVerification(run, Current(run), utc, "placement", "simulator_verified", "simulator", "contradicted", eventId);
            run.wrongAttempts++;
            if (string.IsNullOrEmpty(run.errorStartedUtc))
            {
                run.errorStartedUtc = Timestamp(utc);
                run.errorPausedSeconds = run.pausedSeconds;
            }
            run.feedback = feedback;
            return false;
        }

        public static bool Confirm(GuideRun run, DateTime utc, bool observedLed = false,
            string expectedStepId = null, string eventId = null, string actor = "learner")
        {
            var step = Current(run);
            if (step == null || !CanAdvance(run, step, expectedStepId, eventId)) return false;
            if (run.physical && ExplicitBoard(run.guide) != null && step.kind != "prepare" && !run.registrationValid)
            {
                run.feedback = "실습 보드를 다시 맞춘 뒤 이 단계를 확인하세요.";
                return false;
            }
            string basis = run.physical
                ? (actor == "instructor" ? "instructor_confirmed" : "learner_confirmed")
                : "simulator_verified";
            string origin = run.physical ? (actor == "instructor" ? "instructor" : "learner") : "simulator";
            if (step.kind == "check")
            {
                if (!run.wiringChecked)
                {
                    if (observedLed) return false;
                    run.wiringChecked = true;
                    run.feedback = "배선 점검을 기록했습니다.";
                    RecordVerification(run, step, utc, "pre_power_check", basis, origin, "supported", eventId);
                    return true;
                }
                if (!observedLed)
                {
                    run.wiringChecked = false;
                    run.feedback = "전원을 끄고 저항, LED 방향, 접지 연결을 다시 점검하세요.";
                    RecordVerification(run, step, utc, "led_visible_light", basis, origin, "contradicted", eventId);
                    return false;
                }
                run.lit = run.physical || CanLight(run);
                if (!run.lit)
                {
                    run.wiringChecked = false;
                    run.feedback = "시뮬레이션 배선을 다시 확인하세요.";
                    return false;
                }
            }
            else if (observedLed || (IsWiringStep(step) && !run.physical)) return false;
            string claim = step.kind == "check" ? "led_visible_light" : "task_completion";
            RecordVerification(run, step, utc, claim, basis, origin, "supported", eventId);
            if (step.kind == "check") run.completionBasis = basis;
            Advance(run, utc);
            return true;
        }

        static bool CanLight(GuideRun run)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            var wiring = run.guide.steps.Where(IsWiringStep).ToArray();
            if (run.connections.Count != wiring.Length) return false;
            for (int index = 0; index < wiring.Length; index++)
            {
                var connection = run.connections[index];
                if (connection.kind != wiring[index].kind ||
                    !MatchesTopology(run.guide, connection.kind, connection.fromPoint, connection.toPoint) ||
                    !used.Add(connection.fromPoint) || !used.Add(connection.toPoint)) return false;
            }
            return true;
        }

        static void Advance(GuideRun run, DateTime utc)
        {
            if (DateTime.TryParse(run.errorStartedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var errorStart))
                run.correctionSeconds += Math.Max(0, (utc.ToUniversalTime() - errorStart.ToUniversalTime()).TotalSeconds -
                    Math.Max(0, run.pausedSeconds - run.errorPausedSeconds));
            run.errorStartedUtc = "";
            run.feedback = "";
            ClearSelection(run);
            run.viewedStep = -1;
            run.stepIndex++;
            run.completed = run.stepIndex == run.guide.steps.Length;
            if (run.completed) run.completedUtc = Timestamp(utc);
        }

        public static void Hint(GuideRun run, DateTime? utc = null)
        {
            var step = Viewed(run);
            if (step == null) return;
            run.hintUses++;
            AppendPracticeEvent(run, step, "evidence_replayed", utc ?? DateTime.UtcNow);
        }

        public static void RequestHelp(GuideRun run, DateTime? utc = null, string question = "", string[] evidenceIds = null)
        {
            var step = Current(run);
            if (step == null || IsViewingPrevious(run)) return;
            run.helpRequests++;
            AppendPracticeEvent(run, step, "help_requested", utc ?? DateTime.UtcNow, question, evidenceIds);
        }

        static string Timestamp(DateTime utc) => utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

        public static LabGuide DemoDraft()
        {
            var guide = new LabGuide
            {
                id = "led-guide-demo", sourceSessionId = "led-guide-demo", boardId = GuideBoard.Id,
                title = "LED 직렬 회로 만들기", example = true, steps = EmptyDraft().steps
            };
            foreach (var step in guide.steps)
            {
                step.sourceNoteId = "led-demo-" + step.kind;
                step.sourceQuote = DemoSpoken(step.kind);
            }
            guide.steps[1].fromPoint = "plus-1";
            guide.steps[2].fromPoint = "a-2";
            guide.steps[2].toPoint = "b-1";
            guide.steps[2].ledDirection = LedDirection;
            guide.steps[3].fromPoint = "b-2";
            guide.steps[3].toPoint = "gnd-1";
            return guide;
        }

        public static GuideDraft EmptyDraft() => new GuideDraft
        {
            steps = new[]
            {
                Template("prepare", "준비물을 챙기고 전원이 꺼져 있는지 확인하세요.",
                    "전원을 끈 상태에서 배선을 시작합니다.", "준비물을 확인했고 전원이 꺼져 있습니다."),
                Template("resistor", "플러스 전원 레일에서 첫 번째 회로 행까지 저항을 연결하세요.",
                    "저항은 LED에 흐르는 전류를 제한합니다.", "저항의 양끝이 표시된 위치에 들어갔습니다."),
                Template("led", "LED의 양극을 A행에, 음극을 B행에 연결하세요.",
                    "LED는 극성을 맞춰야 전류가 흐릅니다.", "양극은 A행, 음극은 B행에 있습니다."),
                Template("ground", "점퍼선으로 B행과 접지 레일을 연결하세요.",
                    "전류가 전원으로 돌아갈 수 있어야 회로가 완성됩니다.", "점퍼선으로 B행과 접지 레일을 이었습니다."),
                Template("check", "전원을 끈 상태에서 배선을 점검하고, 점검 후 LED 점등을 확인하세요.",
                    "먼저 저항, 극성, 접지 연결을 점검합니다.", "점검을 마쳤고 LED가 켜졌습니다.")
            }
        };

        static GuideStep Template(string kind, string instruction, string reason, string completion) => new GuideStep
        {
            kind = kind, instruction = instruction, reason = reason, completion = completion,
            sourceNoteId = "", sourceQuote = "", fromPoint = "", toPoint = "", ledDirection = ""
        };

        public static string DemoSpoken(string kind)
        {
            switch (kind)
            {
                case "prepare": return "브레드보드와 저항, LED, 점퍼선, 전류 제한 전원을 준비하세요. 연결을 시작하기 전에 전원이 꺼져 있는지 확인하세요.";
                case "resistor": return "저항 한쪽을 플러스 전원 레일에 꽂고, 다른 쪽은 A행에서 제가 짚는 여기에 꽂으세요.";
                case "led": return "LED의 양극을 A행에, 음극을 B행에 꽂으세요. 방향이 반대면 불이 켜지지 않아요.";
                case "ground": return "B행에서 접지 레일까지 점퍼선으로 연결하세요. 이 선이 전원으로 돌아가는 경로를 완성합니다.";
                case "check": return "전원을 켜기 전에 저항과 LED 방향, 접지 배선을 먼저 확인하세요. 확인이 끝났으면 전원을 켜고 LED가 켜지는지 보세요.";
                default: return "";
            }
        }
    }
}
