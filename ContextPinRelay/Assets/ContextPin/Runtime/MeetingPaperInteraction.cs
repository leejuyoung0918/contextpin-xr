using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        enum PaperInteraction
        {
            LatestDisplayed, MovingLatest, RecordingEditCommand, Transcribing, AwaitingTarget,
            UploadingTarget, GeneratingCandidates, CandidatesDisplayed, Committing
        }

        bool IntegratedPaperControls => HoldCommandMeeting && PaperState?.Property("edit_lock") != null;
        JObject PaperEditLock => PaperState?["edit_lock"] as JObject;
        bool RoomDragging => PaperState?.Value<bool?>("drag_active") == true;
        string appliedPaperVersion;

        PaperInteraction CurrentPaperInteraction
        {
            get
            {
                if (bundleCommitPending || bundleCommitTarget != null ||
                    appliedPaperVersion == AssemblyAdoptedId && meetingLoadedVersion != AssemblyAdoptedId)
                    return PaperInteraction.Committing;
                var room = PaperEditLock;
                var p = explicitRequest;
                if (p?.ButtonWindow == true && !p.BundleSaved && !p.Conflict &&
                    (room == null || TextValue(room, "request_id") == p.Id) && !HasSavedEditBundle(p))
                {
                    if (p.Selection != null || p.Submission != null || p.Sending) return PaperInteraction.UploadingTarget;
                    if (p.Speech == DesignSpeech.Marking) return PaperInteraction.RecordingEditCommand;
                    if (p.Speech == DesignSpeech.Transcribing) return PaperInteraction.Transcribing;
                    if (p.Speech == DesignSpeech.Final) return PaperInteraction.AwaitingTarget;
                }
                switch (TextValue(room, "status"))
                {
                    case "recording": return PaperInteraction.RecordingEditCommand;
                    case "transcribing": return PaperInteraction.Transcribing;
                    case "awaiting_target": return PaperInteraction.AwaitingTarget;
                    case "generating": case "building": return PaperInteraction.GeneratingCandidates;
                    case "ready": return PaperInteraction.CandidatesDisplayed;
                }
                switch (TextValue(CurrentCandidateSet, "status"))
                {
                    case "generating": case "building": return PaperInteraction.GeneratingCandidates;
                    case "ready": return PaperInteraction.CandidatesDisplayed;
                }
                return latestGrab != null || latestMoveInFlight || RoomDragging
                    ? PaperInteraction.MovingLatest : PaperInteraction.LatestDisplayed;
            }
        }

        bool VisibleAtPaperPose(Pose pose)
        {
            if (!PaperRenderable || latestAnchor == null || meetingLoadedVersion != AssemblyAdoptedId ||
                meetingModel == null || !meetingModel.activeInHierarchy || meetingModel.transform.parent != latestAnchor) return false;
            var shown = WorldToPaperPose(reviewTable, new Pose(latestAnchor.position, latestAnchor.rotation));
            return Vector3.Distance(shown.position, pose.position) <= .0005f && Quaternion.Angle(shown.rotation, pose.rotation) < .1f;
        }

        bool OwnPaperTarget => explicitRequest is { Speech: DesignSpeech.Final, Conflict: false, BundleSaved: false, Selection: null } p &&
            TextValue(PaperEditLock, "participant_id") == meetingParticipant && TextValue(PaperEditLock, "request_id") == p.Id &&
            p.PaperFrame == PaperFrameRevision && p.PoseRevision == LatestPoseRevision &&
            p.FrozenPose is Pose pose && VisibleAtPaperPose(pose);

        void RestoreAwaitingPaperTarget()
        {
            var room = PaperEditLock;
            if (!IntegratedPaperControls || !PaperRenderable || TextValue(room, "status") != "awaiting_target" ||
                TextValue(room, "participant_id") != meetingParticipant) return;
            string id = TextValue(room, "request_id");
            if (explicitRequest?.Id == id || explicitRequest is { ButtonWindow: true, WindowClosed: false } ||
                meetingSnapshot?["design_speech_ranges"]?[id] is not JObject range ||
                TextValue(range, "status") != "final" || TextValue(range, "base_version_id") != AssemblyAdoptedId ||
                !TryPaperPose(range["latest_pose_paper"], out var pose)) return;
            explicitRequest = new PendingDesignRequest(id) {
                ButtonWindow = true, WindowClosed = true, RemoteWindow = OtherDeviceRecordsDesign,
                RangeId = id, BaseId = AssemblyAdoptedId, Revision = range.Value<long>("adopted_revision"),
                PaperFrame = range.Value<int>("paper_frame_revision"), PoseRevision = range.Value<int>("latest_pose_revision"),
                FrozenPose = pose
            };
            ReceiveExplicitSpeech(explicitRequest, range);
        }

        string PaperInteractionHint()
        {
            bool other = PaperEditLock is JObject room && TextValue(room, "participant_id") != meetingParticipant;
            switch (CurrentPaperInteraction)
            {
                case PaperInteraction.MovingLatest:
                    return latestGrab != null && !latestGrab.Released ? "모델 위치 이동 중 · Grip을 떼어 놓으세요." :
                        (MeetingHost ? "Latest 위치 저장 중…" : "진행자가 Latest 위치를 옮기는 중입니다.");
                case PaperInteraction.RecordingEditCommand:
                    return other ? "참가자가 수정 요청을 말하는 중입니다." : "● 수정 요청 듣는 중\nB를 떼면 입력 완료";
                case PaperInteraction.Transcribing: return "수정 내용을 확인하는 중…";
                case PaperInteraction.AwaitingTarget:
                    return other ? "요청한 참가자가 수정할 부위를 선택하는 중입니다." : "수정할 부분을 가리키고 A를 누르세요.";
                case PaperInteraction.UploadingTarget: return explicitRequest?.Selection == null ?
                    "요청 취소 중…" : "선택한 부위와 사진을 보내는 중…";
                case PaperInteraction.GeneratingCandidates: return "수정안 생성 중…";
                case PaperInteraction.CandidatesDisplayed:
                    if (MeetingHost) return BundleCandidateStatus(CurrentCandidateSet);
                    return BundleOnPaper ? "후보를 가리키고 A를 누르면 진행자에게 제안합니다." : "수정안 불러오는 중…";
                case PaperInteraction.Committing: return "채택한 CAD를 Latest 위치에 적용하는 중…";
                default:
                    if (!string.IsNullOrEmpty(appliedPaperVersion) && appliedPaperVersion == meetingLoadedVersion)
                        return $"Latest {TextValue(MeetingVersion(meetingLoadedVersion), "design_version")} 적용됨 · B를 누른 채 다음 요청을 말하세요.";
                    return "B를 누른 채 수정 요청을 말하세요.";
            }
        }
    }
}
