using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        void LinkSavedGuidePhotos()
        {
            if (!TraceData.transferConsent) return;
            var sources = TraceData.notes.Where(note => !string.IsNullOrEmpty(note.audioFile)).ToArray();
            foreach (var photo in TraceData.captures.Where(photo => photo.phase != "setup" &&
                photo.phase != "student_checkpoint" && photo.sessionTimeMs >= 0 && photo.clockUncertaintyMs <= 250))
            {
                // This proposes evidence to the reviewer. A setup photo never becomes current wiring evidence.
                double speechTime = (photo.sessionTimeMs - TraceData.microphoneOffsetMs) / 1000.0;
                var note = sources.Where(candidate => candidate.time >= speechTime - 8 && candidate.time <= speechTime + 4)
                    .OrderBy(candidate => Math.Abs(candidate.time - speechTime)).FirstOrDefault();
                if (note == null) continue;
                note.captureIds = (note.captureIds ?? Array.Empty<string>()).Append(photo.captureId).Distinct().ToArray();
                note.framePaths = (note.framePaths ?? Array.Empty<string>()).Append(photo.path).Distinct().ToArray();
            }
        }

        void RefreshGuideEvidenceCatalogue()
        {
            var guide = ViewGuide;
            if (guide == null || guide.approved) return;
            var evidence = new List<GuideEvidenceRef>();
            foreach (var photo in TraceData.captures.Where(photo => photo.phase != "student_checkpoint"))
                evidence.Add(new GuideEvidenceRef
                {
                    id = photo.captureId,
                    kind = "image",
                    sourceId = photo.captureId,
                    captureId = photo.captureId,
                    provenance = photo.source,
                    claimScope = photo.phase == "setup" ? "board_setup" : "visible_scene_only",
                    createdBy = photo.requester,
                    startSeconds = photo.sessionTimeMs / 1000.0,
                    path = photo.path,
                    sha256 = photo.sha256,
                });
            foreach (var note in GuideSourceNotes())
            {
                evidence.Add(new GuideEvidenceRef
                {
                    id = note.id + "-text", kind = string.IsNullOrEmpty(note.audioFile) && note.author == "instructor" ?
                        "human_resolution" : "transcript_span",
                    sourceId = note.id, sourceRevision = Math.Max(1, note.revision), claimScope = "instruction",
                    provenance = note.sourceMode ?? (guideIsExample ? "synthetic" : "legacy"),
                    createdBy = note.author ?? "unknown", text = note.quote, startSeconds = note.time, endSeconds = note.time,
                });
                if (SourceOf(note) is string audio && !string.IsNullOrEmpty(audio))
                    evidence.Add(new GuideEvidenceRef
                    {
                        id = note.id + "-audio", kind = "audio_span", sourceId = note.id, sourceRevision = Math.Max(1, note.revision),
                        provenance = guideIsExample || guide.sourceMode == "synthetic" ? "synthetic" :
                            guide.sourceMode == "replay" ? "replay" : "recorded_audio", claimScope = "source_audio_chunk",
                        createdBy = "unknown", path = guideIsExample ? audio : Path.GetFileName(audio),
                        sha256 = note.audioSha256, startSeconds = note.time, endSeconds = note.time,
                    });
            }
            guide.evidence = evidence.ToArray();
            foreach (var step in guide.steps ?? Array.Empty<GuideStep>())
            {
                if (step == null) continue;
                var note = GuideEvidenceNote(step);
                if (note == null) continue;
                var links = evidence.Where(item => item.sourceId == note.id).Select(item => item.id);
                if (step.evidenceIds == null || step.evidenceIds.Length == 0)
                    step.evidenceIds = links.Concat(note.captureIds ?? Array.Empty<string>()).Distinct().ToArray();
                else
                    step.evidenceIds = step.evidenceIds.Where(id => evidence.Any(item => item.id == id &&
                        (item.kind == "image" || item.sourceId == note.id))).Concat(links).Distinct().ToArray();
            }
        }

        string AddManualGuideSource(TransferPayload payload)
        {
            string wording = payload?.text?.Trim();
            if (string.IsNullOrEmpty(wording) || wording.Length > 3000) return "3,000자 이내의 설명을 입력하세요.";
            TraceNote prior = null;
            if (!string.IsNullOrEmpty(payload.sourceNoteId))
            {
                prior = TraceData.notes.Find(note => note.id == payload.sourceNoteId);
                if (prior == null) return "선택한 녹음 항목을 찾지 못했어요.";
            }
            var note = new TraceNote
            {
                id = Guid.NewGuid().ToString("N"),
                transcript = prior?.transcript ?? "",
                quote = wording,
                normalizedText = wording,
                time = prior?.time ?? 0,
                revision = (prior?.revision ?? 0) + 1,
                supersedesId = prior?.id,
                sourceMode = prior == null ? "teacher_edit" : "manual_transcript",
                author = "instructor",
                status = "manual",
                audioFile = prior?.audioFile,
                audioSha256 = prior?.audioSha256,
                pinMarks = prior?.pinMarks ?? Array.Empty<string>(),
                framePaths = prior?.framePaths ?? Array.Empty<string>(),
                captureIds = prior?.captureIds ?? Array.Empty<string>(),
            };
            TraceData.notes.Add(note);
            foreach (var step in ViewGuide.steps.Where(step => prior != null && step.sourceNoteId == prior.id))
            {
                step.sourceNoteId = note.id;
                step.sourceQuote = note.quote;
                step.evidenceIds = null;
                GuideRules.RecordDraftEdit(ViewGuide, step.stepId, "source");
            }
            RefreshGuideEvidenceCatalogue();
            Save();
            Draw();
            return "";
        }

        string ApplyTeacherEdits(TransferPayload payload)
        {
            if (payload?.steps == null || payload.steps.Length < 5 || payload.steps.Length > 14 ||
                payload.steps.Any(step => step == null || string.IsNullOrEmpty(step.stepId)) ||
                payload.steps.Select(step => step.stepId).Distinct().Count() != payload.steps.Length)
                return "단계와 식별자를 확인하세요.";
            var actions = payload.steps.Where(step => step.kind != "explain").Select(step => step.kind).ToArray();
            if (actions.Length != GuideRules.Kinds.Count || actions.First() != "prepare" || actions.Last() != "check" ||
                !new HashSet<string>(actions).SetEquals(GuideRules.Kinds) || payload.steps[0].kind != "prepare" ||
                payload.steps.Last().kind != "check")
                return "준비는 첫 단계, 전원 점검은 마지막 단계에 두고 배선 동작은 한 번씩 포함하세요.";

            var sources = GuideSourceNotes().ToDictionary(note => note.id);
            foreach (var step in payload.steps)
            {
                if ((!string.IsNullOrEmpty(step.fromPoint) && GuideDisplayPoint(step.fromPoint) == null) ||
                    (!string.IsNullOrEmpty(step.toPoint) && GuideDisplayPoint(step.toPoint) == null))
                    return "등록된 보드의 구멍만 사용할 수 있어요.";
                if (!string.IsNullOrEmpty(step.sourceNoteId) && !sources.ContainsKey(step.sourceNoteId))
                    return "현재 전사에서 근거를 다시 선택하세요.";
                if (step.kind == "explain" && string.IsNullOrWhiteSpace(step.teacherNote))
                    return "추가 설명을 입력하세요.";
            }

            var prior = ViewGuide.steps.ToDictionary(step => step.stepId);
            for (int index = 0; index < payload.steps.Length; index++)
            {
                var step = payload.steps[index];
                step.prerequisites = index == 0 ? Array.Empty<string>() : new[] { payload.steps[index - 1].stepId };
                if (sources.TryGetValue(step.sourceNoteId ?? "", out var source))
                    step.sourceQuote = source.quote;
                if (prior.TryGetValue(step.stepId, out var old) && old.reason != step.reason || step.kind == "explain")
                    step.rationaleSource = "teacher_edit";
            }
            ViewGuide.steps = payload.steps;
            ViewGuide.kitConfirmed = payload.kitConfirmed;
            ViewGuide.kitConditions = payload.kitConditions?.Trim();
            ViewGuide.draftRevision++;
            guideReviewIndex = 0;
            ResetGuideEvidence();
            RefreshGuideEvidenceCatalogue();
            Save();
            Draw();
            return "";
        }

        void StartNextGuideVersion()
        {
            var previous = ViewGuide;
            var draft = GuideRules.NewDraft(previous);
            if (draft == null) return;
            if (!TraceData.approvedGuides.Any(guide => guide.contentHash == previous.contentHash))
                TraceData.approvedGuides.Add(previous);
            TraceData.guide = draft;
            TraceData.status = "review";
            guideReviewIndex = 0;
            ResetGuideEvidence();
            Save();
            Draw();
        }

        string[] GuidePhotoPaths(GuideStep step, TraceNote note)
        {
            var guide = DisplayGuide;
            if (step?.evidenceIds == null || guide?.evidence == null)
                return note?.framePaths ?? Array.Empty<string>();
            return guide.evidence.Where(item => item.kind == "image" && step.evidenceIds.Contains(item.id))
                .Select(item => item.path).Where(path => !string.IsNullOrEmpty(path)).ToArray();
        }

        void GoToPreviousGuideStep()
        {
            if (!GuideRules.ViewPrevious(guideRun)) return;
            ResetGuideEvidence();
            Save();
            Draw();
        }

        void GoToCurrentGuideStep()
        {
            GuideRules.ViewCurrent(guideRun);
            ResetGuideEvidence();
            Save();
            Draw();
        }

        [Serializable] sealed class ObservationInput
        {
            public string sessionId, captureId, stepId;
            public int registrationRevision;
        }

        [Serializable] sealed class ObservationResult
        {
            public string status, verdict, visiblePart, detail;
        }

        void RequestCoarseComparison()
        {
            if (!transferOnline || transferHealth?.visionConfigured != true || guideRun?.physical != true || !guideRun.registrationValid)
            {
                notice = "사진 비교는 PC 연결, Vision 설정, 새 보드 등록이 필요해요.";
                RefreshStatus();
                return;
            }
            var photo = TraceData.captures.LastOrDefault(item => item.captureId == TraceData.latestCheckpoint);
            var step = GuideRules.Current(guideRun);
            if (photo == null || photo.source != "real_camera" || photo.registrationRevision != guideRun.registrationRevision ||
                photo.trackingEpoch != guideRun.trackingEpoch || photo.stepId != step?.stepId || !uploadHashes.Contains(photo.sha256))
            {
                notice = "현재 단계에서 사진을 새로 찍고 PC 전송이 끝난 뒤 비교하세요.";
                RefreshStatus();
                return;
            }
            notice = "사진에서 보이는 부분을 비교하고 있어요.";
            RefreshStatus();
            StartCoroutine(CompareVisiblePhoto(new ObservationInput
            {
                sessionId = TraceData.id, captureId = photo.captureId, stepId = photo.stepId,
                registrationRevision = photo.registrationRevision,
            }, guideRun.trackingEpoch));
        }

        IEnumerator CompareVisiblePhoto(ObservationInput input, int trackingEpoch)
        {
            ObservationResult result = null;
            yield return RelayJson<ObservationResult>(RelayBaseUrl() + "/transfer/observe", input, transferPairing.deviceToken,
                response => result = response, 32);
            if (TraceData?.id != input.sessionId || guideRun?.registrationRevision != input.registrationRevision ||
                guideRun.trackingEpoch != trackingEpoch || !guideRun.registrationValid ||
                GuideRules.Current(guideRun)?.stepId != input.stepId) yield break;
            notice = result?.verdict == "visible_candidate" ? "사진에서 부품 후보를 찾았어요. 배선과 극성은 직접 확인하세요." :
                "사진에서 극성이나 연결을 확인하기 어려워요. 전원을 끄고 교수 설명과 직접 비교하세요.";
            RefreshStatus();
        }
    }
}
