using System;
using System.Linq;
using System.Text.Json;
using ContextPin;

internal static class GuideTransferChecks
{
    public static void Run(Action<bool, string> check)
    {
        var utc = new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);
        LabGuide SourceDraft()
        {
            var draft = GuideRules.DemoDraft();
            draft.id = "guide-fixture";
            draft.example = false;
            draft.sourceMode = "real";
            draft.demonstrationBoardInstanceId = "demo-board";
            draft.boardDefinition = LabBoards.Resolve(LabBoards.PhysicalId);
            draft.boardId = draft.boardDefinition.id;
            draft.steps[1].toPoint = "a-1";
            return draft;
        }

        var guide = SourceDraft();
        string[] notes = guide.steps.Select(step => step.sourceNoteId).ToArray();
        check(GuideRules.Approve(guide, notes, utc).Contains("키트") && !guide.approved,
            "physical draft needs teacher-approved kit conditions");
        guide.kitConfirmed = true;
        guide.kitConditions = "Test fixture teacher-confirmed conditions";
        guide.steps[2].fromPoint = "A1";
        check(GuideRules.Approve(guide, notes, utc).Length > 0,
            "printed terminal alias does not bypass occupied-hole validation");
        guide.steps[2].fromPoint = "C1";
        guide.steps[2].unresolvedFields = new[] { "ledDirection" };
        check(GuideRules.Approve(guide, notes, utc).Length > 0,
            "a specified position does not resolve reported polarity uncertainty");
        check(GuideRules.RecordDraftEdit(guide, guide.steps[2].stepId ?? GuideRules.StepIdentity(guide.steps[2], 2), "ledDirection"),
            "instructor explicitly resolves an uncertain field");
        guide.evidence = new[]
        {
            new GuideEvidenceRef { id = "audio-2", kind = "audio_span", sourceId = notes[1], provenance = "real",
                captureId = "mock-immutable-resource", startSeconds = 5, endSeconds = 9 }
        };
        guide.steps[1].evidenceIds = new[] { "audio-2" };
        check(GuideRules.Approve(guide, notes, utc).Length == 0 && GuideRules.VerifyApproved(guide) &&
            guide.version == 1 && guide.contentHash.Length == 64 && guide.approvedBy == "instructor",
            "physical guide approval seals board, evidence, and actor");
        var options = new JsonSerializerOptions { IncludeFields = true };
        var loaded = JsonSerializer.Deserialize<LabGuide>(JsonSerializer.Serialize(guide, options), options);
        check(GuideRules.VerifyApproved(loaded), "approval is verified after disk-format round trip");
        loaded.evidence[0].startSeconds = Math.BitIncrement(loaded.evidence[0].startSeconds);
        check(GuideRules.VerifyApproved(loaded), "sub-millisecond parsing variation preserves the same evidence identity");

        var learner = GuideRules.NewRun(guide, true, utc);
        check(learner != null && !learner.registrationValid && GuideRules.Confirm(learner, utc) &&
            !GuideRules.Confirm(learner, utc.AddSeconds(1)), "physical progress needs an independent board registration");
        check(!GuideRules.BindPracticeBoard(learner, "demo-board", 1, 0, guide.boardId, guide.boardRevision, guide.boardHash) &&
            !GuideRules.BindPracticeBoard(learner, "student-board", 1, 0, GuideBoard.Id, 1, guide.boardHash),
            "demonstration pose and mismatched profile cannot bind to practice");
        check(GuideRules.BindPracticeBoard(learner, "student-board", 1, 0, guide.boardId, guide.boardRevision, guide.boardHash, utc),
            "compatible student board binds by instance and version");
        var step = GuideRules.Current(learner);
        check(!GuideRules.RecordCheckpoint(learner, "event-1", "capture-1", "replay", 1, 0, utc, step.stepId) &&
            !GuideRules.RecordCheckpoint(learner, "event-1", "capture-1", "real_camera", 1, 0, utc, "wrong-step") &&
            GuideRules.RecordCheckpoint(learner, "event-1", "capture-1", "real_camera", 1, 0, utc, step.stepId) &&
            !GuideRules.RecordCheckpoint(learner, "event-1", "capture-1", "real_camera", 1, 0, utc, step.stepId) &&
            GuideRules.Current(learner) == step,
            "checkpoint is explicitly sourced, step-bound, deduplicated, and cannot complete a step");
        GuideRules.RequestHelp(learner, utc.AddSeconds(2), "Which strip?", new[] { "audio-2", "capture-1" });
        check(learner.events.Last().stepId == step.stepId && learner.events.Last().registrationRevision == 1 &&
            learner.events.Last().boardInstanceId == "student-board", "help card preserves board and current step context");
        check(GuideRules.Confirm(learner, utc.AddSeconds(3), expectedStepId: step.stepId, eventId: "checked-resistor") &&
            !GuideRules.Confirm(learner, utc.AddSeconds(3), expectedStepId: step.stepId, eventId: "checked-resistor") &&
            learner.verifications.Last().basis == "learner_confirmed" && learner.connections.Count == 0,
            "learner acknowledgement does not claim installed contacts or electrical verification");
        check(GuideRules.ViewPrevious(learner) && GuideRules.Viewed(learner) == step &&
            !GuideRules.Confirm(learner, utc.AddSeconds(4)), "previous explanation is read-only");
        GuideRules.ViewCurrent(learner);
        GuideRules.InvalidatePracticeBoard(learner, 1, utc.AddSeconds(5));
        check(!GuideRules.BindPracticeBoard(learner, "student-board", 1, 0, guide.boardId, guide.boardRevision, guide.boardHash) &&
            !GuideRules.RecordCheckpoint(learner, "old-camera", "capture-2", "real_camera", 1, 0, utc) &&
            !GuideRules.Confirm(learner, utc.AddSeconds(6)), "tracking epoch invalidates old pictures and learner alignment");
        check(GuideRules.BindPracticeBoard(learner, "student-board", 2, 1, guide.boardId, guide.boardRevision, guide.boardHash, utc) &&
            GuideRules.Confirm(learner, utc.AddSeconds(6)) && GuideRules.Confirm(learner, utc.AddSeconds(7)) &&
            GuideRules.Confirm(learner, utc.AddSeconds(8)) &&
            GuideRules.Confirm(learner, utc.AddSeconds(9), observedLed: true, actor: "instructor") &&
            learner.lit && learner.completionBasis == "instructor_confirmed" &&
            learner.verifications.Last().claimType == "led_visible_light" &&
            !learner.verifications.Any(item => item.claimType == "electrical_continuity"),
            "final confirmation records localized LED light with actor and zero electrical-verification claims");

        var second = GuideRules.NewDraft(guide);
        check(second.version == 2 && !second.approved && GuideRules.NewRun(second, true, utc) == null,
            "an edit creates a new unpublished version");
        check(GuideRules.SplitDraftStep(second, 2, "교수가 입력한 보충 설명", new[] { "audio-2" }) &&
            GuideRules.MoveDraftStep(second, 2, 1) && GuideRules.MergeDraftExplanation(second, 1) &&
            second.steps[1].teacherEditedFields.Contains("teacherNote") &&
            GuideRules.Approve(second, notes, utc.AddMinutes(1)).Length == 0 &&
            second.version == 2 && second.contentHash != guide.contentHash &&
            GuideRules.IsCurrentVersion(learner, guide) && !GuideRules.IsCurrentVersion(learner, second),
            "teacher ordering and explanation edits create a separate approved revision while run stays pinned");
        guide.evidence[0].endSeconds = 10;
        check(!GuideRules.VerifyApproved(guide) && GuideRules.NewRun(guide, true, utc) == null &&
            GuideRules.VerifyApproved(learner.guide),
            "tampering with source evidence cannot replace the pinned run snapshot");

        var legacy = GuideRules.DemoDraft();
        legacy.steps[1].toPoint = "a-1";
        legacy.approved = true;
        check(GuideRules.Migrate(legacy) && legacy.approvedBy == "legacy_unrecorded" &&
            legacy.approvedAt == "" && legacy.sourceMode == "synthetic" && GuideRules.VerifyApproved(legacy),
            "legacy migration preserves approval without inventing actor, time, or real provenance");
        legacy.boardDefinition = new LabBoardDefinition
        {
            id = "", revision = 0, points = Array.Empty<GuidePoint>(), terminals = Array.Empty<BoardTerminal>()
        };
        check(GuideRules.VerifyApproved(legacy) && GuideRules.BoardPoints(legacy).Count == GuideBoard.Points.Count &&
            GuideRules.Validate(legacy).Length == 0,
            "Unity default-valued nested board survives simulator archive resume");
        legacy.boardDefinition.id = "wrong-profile";
        check(!GuideRules.VerifyApproved(legacy) && GuideRules.Validate(legacy).Length > 0,
            "simulator rejects a nonempty mismatched board profile");
    }
}
