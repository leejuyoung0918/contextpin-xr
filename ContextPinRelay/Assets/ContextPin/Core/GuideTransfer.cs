using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ContextPin
{
    [Serializable]
    public sealed class GuideEvidenceRef
    {
        public string id, kind, sourceId, provenance, claimScope, createdBy, captureId, path, sha256, text;
        public int sourceRevision;
        public double startSeconds, endSeconds;

        internal GuideEvidenceRef Copy() => (GuideEvidenceRef)MemberwiseClone();
    }

    [Serializable]
    public sealed class GuidePracticeEvent
    {
        public string eventId, kind, utc, stepId, question, sourceMode, boardInstanceId, fromPoint, toPoint;
        public string[] evidenceIds;
        public int registrationRevision, trackingEpoch;
    }

    [Serializable]
    public sealed class GuideVerificationRecord
    {
        public string verificationId, eventId, stepId, utc, claimType, basis, source, verdict, provenance;
        public string[] evidenceIds;
        public int registrationRevision, trackingEpoch;
    }

    public static partial class GuideRules
    {
        public const int TransferSchemaVersion = 2;

        internal static string NewId() => Guid.NewGuid().ToString("N");

        internal static LabBoardDefinition ExplicitBoard(LabGuide guide)
        {
            var profile = guide?.boardDefinition;
            if (profile == null) return null;
            // JsonUtility serializes a missing nested class as default-valued
            // fields. Only the completely empty simulation sentinel is ignored.
            bool empty = profile.revision == 0 && profile.widthMeters == 0 && profile.heightMeters == 0 &&
                (profile.points?.Length ?? 0) == 0 && (profile.terminals?.Length ?? 0) == 0 &&
                (profile.references?.Length ?? 0) == 0 && (profile.markers?.Length ?? 0) == 0 &&
                (profile.knownComponents?.Length ?? 0) == 0 &&
                (profile.targets?.Length ?? 0) == 0 &&
                string.IsNullOrEmpty(profile.id) && string.IsNullOrEmpty(profile.contentHash) &&
                string.IsNullOrEmpty(profile.kind) && string.IsNullOrEmpty(profile.displayName) &&
                string.IsNullOrEmpty(profile.units) && string.IsNullOrEmpty(profile.axes) &&
                string.IsNullOrEmpty(profile.geometrySource) && string.IsNullOrEmpty(profile.netSource) &&
                string.IsNullOrEmpty(profile.placementPolicy) && string.IsNullOrEmpty(profile.internalConnections) &&
                string.IsNullOrEmpty(profile.markerLayoutId) && string.IsNullOrEmpty(profile.markerDictionary);
            return guide.boardId == GuideBoard.Id && empty ? null : profile;
        }

        public static IReadOnlyList<GuidePoint> BoardPoints(LabGuide guide) =>
            (IReadOnlyList<GuidePoint>)ExplicitBoard(guide)?.points ?? GuideBoard.Points;

        public static GuidePoint FindPoint(LabGuide guide, string id)
        {
            var profile = ExplicitBoard(guide);
            return profile == null ? GuideBoard.Find(id) : profile.Find(id);
        }

        public static string PointName(LabGuide guide, string id) => FindPoint(guide, id)?.label ?? "미지정";

        static bool SameNet(LabGuide guide, string left, string right) =>
            ExplicitBoard(guide)?.SameNet(left, right) ?? GuideBoard.MatchesNode(left, right);

        static bool MatchesTopology(LabGuide guide, string kind, string from, string to) =>
            ExplicitBoard(guide)?.IsExpectedConnection(kind, from, to) ?? CorrectTopology(kind, from, to);

        static bool ValidBoardProfile(LabGuide guide)
        {
            var profile = guide.boardDefinition;
            return profile != null && profile.id == guide.boardId && profile.IsValidVersion &&
                (guide.boardRevision == 0 || profile.revision == guide.boardRevision) &&
                (string.IsNullOrEmpty(guide.boardHash) || profile.contentHash == guide.boardHash);
        }

        public static string StepIdentity(GuideStep step, int index) =>
            string.IsNullOrEmpty(step?.stepId) ? (step?.kind ?? "step") + "-" + (index + 1) : step.stepId;

        static void NormalizeDraft(LabGuide guide)
        {
            guide.schemaVersion = TransferSchemaVersion;
            if (guide.version < 1) guide.version = 1;
            if (guide.draftRevision < 1) guide.draftRevision = 1;
            var profile = ExplicitBoard(guide);
            if (guide.boardRevision < 1) guide.boardRevision = profile?.revision ?? 1;
            if (string.IsNullOrEmpty(guide.boardHash))
                guide.boardHash = profile?.contentHash ?? LabBoards.KnownVersion(guide.boardId)?.contentHash;
            if (string.IsNullOrEmpty(guide.sourceMode))
                guide.sourceMode = guide.example ? "synthetic" : "legacy";
            guide.evidence ??= Array.Empty<GuideEvidenceRef>();
            for (int index = 0; index < (guide.steps?.Length ?? 0); index++)
            {
                var step = guide.steps[index];
                if (step == null) continue;
                step.stepId = StepIdentity(step, index);
                step.prerequisites ??= index == 0 ? Array.Empty<string>() : new[] { StepIdentity(guide.steps[index - 1], index - 1) };
                step.evidenceIds ??= Array.Empty<string>();
                step.teacherEditedFields ??= Array.Empty<string>();
                step.unresolvedFields ??= Array.Empty<string>();
                if (string.IsNullOrEmpty(step.rationaleSource)) step.rationaleSource = "suggested_explanation";
            }
        }

        static void SealApproval(LabGuide guide, DateTime utc, string actor)
        {
            NormalizeDraft(guide);
            guide.approvedAt = Timestamp(utc);
            guide.approvedBy = string.IsNullOrWhiteSpace(actor) ? "instructor" : actor;
            guide.approved = true;
            guide.contentHash = ComputeContentHash(guide);
        }

        public static bool VerifyApproved(LabGuide guide) =>
            guide?.approved == true && !string.IsNullOrEmpty(guide.contentHash) &&
            guide.schemaVersion == TransferSchemaVersion && guide.version >= 1 &&
            (guide.boardId == GuideBoard.Id ? ExplicitBoard(guide) == null : ValidBoardProfile(guide)) &&
            string.Equals(guide.contentHash, ComputeContentHash(guide), StringComparison.Ordinal);

        public static LabGuide NewDraft(LabGuide approved)
        {
            if (!VerifyApproved(approved)) return null;
            var draft = approved.Copy();
            draft.approved = false;
            draft.approvedAt = "";
            draft.approvedBy = "";
            draft.contentHash = "";
            draft.version++;
            draft.draftRevision = 1;
            return draft;
        }

        public static bool RecordDraftEdit(LabGuide draft, string stepId, string field)
        {
            if (draft == null || draft.approved || string.IsNullOrEmpty(field)) return false;
            NormalizeDraft(draft);
            var step = draft.steps?.FirstOrDefault(item => item?.stepId == stepId);
            if (step == null) return false;
            step.teacherEditedFields = step.teacherEditedFields.Append(field).Distinct(StringComparer.Ordinal).ToArray();
            step.unresolvedFields = step.unresolvedFields.Where(value => value != field).ToArray();
            if (step.unresolvedFields.Length == 0) step.confirmationQuestion = "";
            if (field == "reason") step.rationaleSource = "teacher_edit";
            draft.draftRevision++;
            return true;
        }

        public static bool MoveDraftStep(LabGuide draft, int from, int to)
        {
            if (draft?.approved != false || draft.steps == null || from <= 0 || to <= 0 ||
                from >= draft.steps.Length - 1 || to >= draft.steps.Length - 1 || from == to) return false;
            NormalizeDraft(draft);
            var steps = draft.steps.ToList();
            var step = steps[from];
            steps.RemoveAt(from);
            steps.Insert(to, step);
            draft.steps = steps.ToArray();
            UpdateDraftOrder(draft);
            return true;
        }

        public static bool SplitDraftStep(LabGuide draft, int index, string explanation, string[] evidenceIds)
        {
            if (draft?.approved != false || draft.steps == null || draft.steps.Length >= 20 ||
                index <= 0 || index >= draft.steps.Length || string.IsNullOrWhiteSpace(explanation)) return false;
            NormalizeDraft(draft);
            var extra = new GuideStep
            {
                stepId = NewId(), kind = "explain", instruction = explanation, reason = explanation,
                completion = "교수 설명을 확인했습니다.", teacherNote = explanation, rationaleSource = "teacher_edit",
                teacherEditedFields = new[] { "teacherNote" }, evidenceIds = evidenceIds?.ToArray() ?? Array.Empty<string>()
            };
            var steps = draft.steps.ToList();
            steps.Insert(index, extra);
            draft.steps = steps.ToArray();
            UpdateDraftOrder(draft);
            return true;
        }

        public static bool MergeDraftExplanation(LabGuide draft, int explanationIndex)
        {
            if (draft?.approved != false || draft.steps == null || explanationIndex <= 0 ||
                explanationIndex >= draft.steps.Length - 1 || draft.steps[explanationIndex]?.kind != "explain") return false;
            NormalizeDraft(draft);
            var extra = draft.steps[explanationIndex];
            var target = draft.steps[explanationIndex + 1];
            if (target == null) return false;
            target.teacherNote = string.Join("\n", new[] { extra.teacherNote, target.teacherNote }.Where(item => !string.IsNullOrWhiteSpace(item)));
            target.teacherEditedFields = target.teacherEditedFields.Append("teacherNote").Distinct(StringComparer.Ordinal).ToArray();
            target.evidenceIds = target.evidenceIds.Concat(extra.evidenceIds).Distinct(StringComparer.Ordinal).ToArray();
            draft.steps = draft.steps.Where((_, index) => index != explanationIndex).ToArray();
            UpdateDraftOrder(draft);
            return true;
        }

        static void UpdateDraftOrder(LabGuide draft)
        {
            for (int index = 0; index < draft.steps.Length; index++)
                draft.steps[index].prerequisites = index == 0 ? Array.Empty<string>() : new[] { draft.steps[index - 1].stepId };
            draft.draftRevision++;
        }

        public static bool Migrate(LabGuide guide)
        {
            if (guide == null) return false;
            bool removedEmpty = guide.boardDefinition != null && ExplicitBoard(guide) == null;
            if (removedEmpty) guide.boardDefinition = null;
            if (guide.schemaVersion >= TransferSchemaVersion || !string.IsNullOrEmpty(guide.contentHash)) return removedEmpty;
            NormalizeDraft(guide);
            if (guide.approved && string.IsNullOrEmpty(Validate(guide)))
            {
                // An old approval had no timestamp, actor or evidence digest. Preserve those
                // as unknown and seal the existing content for subsequent version checks.
                guide.approvedAt = "";
                guide.approvedBy = "legacy_unrecorded";
                guide.contentHash = ComputeContentHash(guide);
            }
            return true;
        }

        public static bool Migrate(GuideRun run)
        {
            if (run == null) return false;
            bool guideChanged = Migrate(run.guide);
            if (run.schemaVersion >= TransferSchemaVersion) return guideChanged;
            run.schemaVersion = TransferSchemaVersion;
            run.runId = NewId();
            run.guideVersion = run.guide?.version ?? 0;
            run.guideHash = run.guide?.contentHash;
            run.sourceMode = run.physical ? run.guide?.sourceMode ?? "legacy" : "simulation";
            run.registrationValid = false;
            run.viewedStep = -1;
            run.events ??= new List<GuidePracticeEvent>();
            run.verifications ??= new List<GuideVerificationRecord>();
            return true;
        }

        public static bool IsCurrentVersion(GuideRun run, LabGuide guide) => run != null && guide != null &&
            VerifyApproved(guide) && run.guideId == guide.id && run.guideVersion == guide.version && run.guideHash == guide.contentHash;

        public static bool BindPracticeBoard(GuideRun run, string instanceId, int revision, int epoch,
            string boardId, int boardRevision, string boardHash, DateTime? utc = null)
        {
            if (run?.physical != true || ExplicitBoard(run.guide) == null || !VerifyApproved(run.guide) ||
                string.IsNullOrWhiteSpace(instanceId) || instanceId == run.guide.demonstrationBoardInstanceId ||
                boardId != run.guide.boardId || boardRevision != run.guide.boardRevision || boardHash != run.guide.boardHash ||
                epoch < run.trackingEpoch || revision <= 0 || revision < run.registrationRevision ||
                (revision == run.registrationRevision && !run.registrationValid)) return false;
            if (!string.IsNullOrEmpty(run.studentBoardInstanceId) && run.studentBoardInstanceId != instanceId &&
                (run.stepIndex > 1 || revision == run.registrationRevision)) return false;
            if (run.registrationValid && run.studentBoardInstanceId == instanceId && run.trackingEpoch == epoch &&
                run.registrationRevision == revision) return true;

            run.studentBoardInstanceId = instanceId;
            run.registrationRevision = revision;
            run.trackingEpoch = epoch;
            run.registrationValid = true;
            run.wiringChecked = false;
            AppendPracticeEvent(run, Current(run), "practice_board_bound", utc ?? DateTime.UtcNow);
            return true;
        }

        public static void InvalidatePracticeBoard(GuideRun run, int epoch, DateTime? utc = null)
        {
            if (run?.physical != true || (epoch <= run.trackingEpoch && !run.registrationValid)) return;
            run.registrationValid = false;
            run.trackingEpoch = Math.Max(epoch, run.trackingEpoch);
            if (!run.completed) run.wiringChecked = false;
            AppendPracticeEvent(run, Current(run), "practice_board_stale", utc ?? DateTime.UtcNow);
        }

        public static GuideStep Viewed(GuideRun run) =>
            IsViewingPrevious(run) ? run.guide.steps[run.viewedStep] : Current(run);

        public static bool IsViewingPrevious(GuideRun run) => run?.guide?.steps != null &&
            run.viewedStep >= 0 && run.viewedStep < run.stepIndex && run.viewedStep < run.guide.steps.Length;

        public static bool ViewPrevious(GuideRun run)
        {
            if (Current(run) == null) return false;
            int index = IsViewingPrevious(run) ? run.viewedStep : run.stepIndex;
            if (index <= 0) return false;
            run.viewedStep = index - 1;
            return true;
        }

        public static void ViewCurrent(GuideRun run)
        {
            if (run != null) run.viewedStep = -1;
        }

        public static void Pause(GuideRun run, DateTime utc)
        {
            if (run != null && !run.completed && string.IsNullOrEmpty(run.pausedUtc)) run.pausedUtc = Timestamp(utc);
        }

        public static void Resume(GuideRun run, DateTime utc)
        {
            if (run == null || !DateTime.TryParse(run.pausedUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var paused)) return;
            run.pausedSeconds += Math.Max(0, (utc.ToUniversalTime() - paused.ToUniversalTime()).TotalSeconds);
            run.pausedUtc = "";
        }

        static bool CanAdvance(GuideRun run, GuideStep step, string expectedStepId, string eventId) =>
            !IsViewingPrevious(run) && string.IsNullOrEmpty(run.pausedUtc) && VerifyApproved(run.guide) &&
            run.guideHash == run.guide.contentHash &&
            run.guideVersion == run.guide.version &&
            (string.IsNullOrEmpty(expectedStepId) || expectedStepId == step.stepId) &&
            (string.IsNullOrEmpty(eventId) || !run.verifications.Any(record => record.eventId == eventId));

        static void AppendPracticeEvent(GuideRun run, GuideStep step, string kind, DateTime utc,
            string question = "", string[] evidenceIds = null, string eventId = null)
        {
            run.events ??= new List<GuidePracticeEvent>();
            run.events.Add(new GuidePracticeEvent
            {
                eventId = eventId ?? NewId(), kind = kind, utc = Timestamp(utc), stepId = step?.stepId,
                question = question ?? "", evidenceIds = evidenceIds?.ToArray() ?? step?.evidenceIds?.ToArray() ?? Array.Empty<string>(),
                sourceMode = run.sourceMode, boardInstanceId = run.studentBoardInstanceId,
                fromPoint = string.IsNullOrEmpty(run.fromPoint) ? step?.fromPoint : run.fromPoint,
                toPoint = string.IsNullOrEmpty(run.toPoint) ? step?.toPoint : run.toPoint,
                registrationRevision = run.registrationRevision, trackingEpoch = run.trackingEpoch
            });
        }

        public static bool RecordCheckpoint(GuideRun run, string eventId, string captureId,
            string provenance, int registrationRevision, int trackingEpoch, DateTime utc, string expectedStepId = null)
        {
            var step = Current(run);
            if (step == null || IsViewingPrevious(run) || !run.physical || !run.registrationValid ||
                provenance != "real_camera" || run.sourceMode != "real" ||
                (!string.IsNullOrEmpty(expectedStepId) && step.stepId != expectedStepId) ||
                registrationRevision != run.registrationRevision || trackingEpoch != run.trackingEpoch ||
                string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(captureId) ||
                run.events.Any(item => item.eventId == eventId)) return false;
            AppendPracticeEvent(run, step, "student_checkpoint", utc, evidenceIds: new[] { captureId }, eventId: eventId);
            return true;
        }

        static void RecordVerification(GuideRun run, GuideStep step, DateTime utc, string claim, string basis,
            string source, string verdict, string eventId = null)
        {
            run.verifications ??= new List<GuideVerificationRecord>();
            run.verifications.Add(new GuideVerificationRecord
            {
                verificationId = NewId(), eventId = eventId, stepId = step.stepId, utc = Timestamp(utc),
                claimType = claim, basis = basis, source = source, verdict = verdict,
                provenance = run.sourceMode, evidenceIds = step.evidenceIds?.ToArray() ?? Array.Empty<string>(),
                registrationRevision = run.registrationRevision, trackingEpoch = run.trackingEpoch
            });
        }

        static string ComputeContentHash(LabGuide guide)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(guide.schemaVersion);
                writer.Write(guide.version);
                writer.Write(guide.boardRevision);
                writer.Write(guide.example);
                writer.Write(guide.kitConfirmed);
                WriteStrings(writer, new[] { guide.id, guide.sourceSessionId, guide.boardId, guide.title, guide.boardHash,
                    guide.approvedAt, guide.approvedBy, guide.sourceMode, guide.demonstrationBoardInstanceId, guide.kitConditions });
                var steps = guide.steps ?? Array.Empty<GuideStep>();
                writer.Write(steps.Length);
                foreach (var step in steps)
                {
                    writer.Write(step != null);
                    if (step == null) continue;
                    WriteStrings(writer, new[] { step.stepId, step.kind, step.sourceNoteId, step.sourceQuote, step.fromPoint,
                        step.toPoint, step.ledDirection, step.instruction, step.reason, step.completion, step.teacherNote,
                        step.caution, step.rationaleSource, step.confirmationQuestion });
                    WriteStrings(writer, step.prerequisites);
                    WriteStrings(writer, step.evidenceIds);
                    WriteStrings(writer, step.teacherEditedFields);
                    WriteStrings(writer, step.unresolvedFields);
                }
                var evidence = guide.evidence ?? Array.Empty<GuideEvidenceRef>();
                writer.Write(evidence.Length);
                foreach (var item in evidence)
                {
                    writer.Write(item != null);
                    if (item == null) continue;
                    WriteStrings(writer, new[] { item.id, item.kind, item.sourceId, item.provenance, item.claimScope,
                        item.createdBy, item.captureId, item.path, item.sha256, item.text });
                    writer.Write(item.sourceRevision);
                    // Capture timing is millisecond-scoped. Unity's JSON reader
                    // can round a stored double by one bit on restoration.
                    writer.Write(EvidenceMilliseconds(item.startSeconds));
                    writer.Write(EvidenceMilliseconds(item.endSeconds));
                }
            }

            using var digest = SHA256.Create();
            return BitConverter.ToString(digest.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
        }

        static void WriteStrings(BinaryWriter writer, string[] values)
        {
            writer.Write(values?.Length ?? 0);
            if (values == null) return;
            foreach (string value in values) writer.Write(value ?? "");
        }

        static long EvidenceMilliseconds(double seconds) =>
            double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0 || seconds > long.MaxValue / 1000.0
                ? -1 : (long)Math.Round(seconds * 1000.0, MidpointRounding.AwayFromZero);
    }
}
