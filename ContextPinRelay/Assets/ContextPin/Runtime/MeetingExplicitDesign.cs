using System;
using System.Collections;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        enum DesignSpeech { Waiting, Marking, Transcribing, Final, Failed }

        sealed class PendingDesignRequest
        {
            public readonly string Id = Guid.NewGuid().ToString("N");
            public string BaseId;
            public long Revision;
            public string RangeId, Transcript, TranscriptSource, SpeechError;
            public DesignSpeech Speech;
            public JArray UtteranceRefs;
            public DesignSelection Selection;
            public JObject Submission;
            public bool Sending, Conflict, RetryTransfer, BundleSaved;
            public string Error, RoundId;
            public JObject ObservedSnapshot;
            public double RangeMarkedAt;
        }

        sealed class DesignSelection
        {
            public readonly string Id = Guid.NewGuid().ToString("N");
            public string Source;
            public JObject Reference, Stamp, TargetCommand, BundleEvidence;
            public byte[] Image;
            public bool Uploaded, Published, Sending;
        }

        PendingDesignRequest explicitRequest;
        bool explicitCameraStarting;
        string explicitCameraMessage, explicitHint;

        bool ExplicitDesignMeeting => SpatialAssembly && TextValue(meetingSnapshot, "design_submit_mode") == "explicit";
        bool CaptureBundleMeeting => ExplicitDesignMeeting && TextValue(meetingSnapshot?["meeting"], "edit_request_output") == "bundle";
        bool CandidateGenerationMeeting => CaptureBundleMeeting && TextValue(meetingSnapshot?["meeting"], "candidate_generation") == "ai";
        bool CapturingPaperEdit => SharedPaperMeeting && CaptureBundleMeeting && MeetingHost && explicitRequest != null &&
            !explicitRequest.BundleSaved && !explicitRequest.Conflict &&
            explicitRequest.Speech is (DesignSpeech.Marking or DesignSpeech.Transcribing or DesignSpeech.Final);
        JObject CurrentCandidateSet
        {
            get
            {
                if (!CaptureBundleMeeting || meetingSnapshot["candidate_sets"] is not JObject sets || !sets.HasValues) return null;
                if (SharedPaperMeeting)
                {
                    var active = TextValue(PaperState, "active_request_id");
                    return active.Length > 0 ? sets[active] as JObject : null;
                }
                var requests = meetingSnapshot["edit_requests"] as JObject;
                var id = explicitRequest?.Id;
                if (id == null && requests != null)
                {
                    // Reopening a session restores the newest request on this Latest.
                    id = requests.Properties().Where(r => TextValue(r.Value, "base_version_id") == AssemblyAdoptedId)
                        .OrderByDescending(r => r.Value.Value<DateTime?>("created_at")).FirstOrDefault()?.Name;
                }
                if (id == null || requests?[id] is not JObject request || sets[id] is not JObject set) return null;
                return TextValue(set, "request_id") == id && TextValue(set, "base_version_id") == TextValue(request, "base_version_id")
                    ? set : null;
            }
        }

        string BundleCandidateStatus(JObject set)
        {
            if (!string.IsNullOrEmpty(bundleReviewError)) return bundleReviewError;
            if (bundleCommitPending) return "선택한 후보를 서버에 저장하는 중…";
            if (bundleCommitTarget != null) return $"채택한 파일을 확인해 {PaperLatestPosition} Latest를 교체하는 중…";
            switch (TextValue(set, "status"))
            {
                case "generating": case "building": return "수정안 생성 중…";
                case "ready":
                    int count = Values(set, "candidates").Count();
                    if (count == 0) return "사용할 수 있는 수정안 파일이 없습니다.";
                    return $"수정안 {count}개 준비됨 · " + (BundleOnPaper ? "채택할 모형을 가리키고 오른쪽 A" : "모형 불러오는 중…");
                case "committed": return $"{PaperLatestPosition} Latest에 반영됐습니다. 다음 수정 요청을 시작할 수 있습니다.";
                case "unsupported_specific_region": return "선택한 부위의 개별 수정 미지원";
                case "clarification": return "수정 요청 확인 필요";
                case "superseded": return "후속 요청이 접수됐습니다.";
                case "failed": return "수정안을 만들지 못했습니다.";
                default: return "저장한 요청의 생성 상태를 확인하고 있습니다.";
            }
        }
        bool BundleLatestReady => A4ReviewSurfaceActive && (!SharedPaperMeeting || PaperRenderable) &&
            meetingModel != null && meetingModel.activeInHierarchy && meetingLoadedVersion == AssemblyAdoptedId;
        string PaperLatestPosition => CenteredPaperLatest ? "종이 중앙" : "종이 오른쪽 앞";
        bool OtherDeviceRecordsDesign => !string.IsNullOrEmpty(TextValue(meetingSnapshot?["meeting"], "audio_device_id")) &&
            TextValue(meetingSnapshot["meeting"], "audio_device_id") != meetingDeviceId;
        bool ExplicitDesignReady => ExplicitDesignMeeting && AssemblySessionStatus == "recording" && !assemblyStopping &&
            assemblyAlignment == AssemblyAlignment.Confirmed && AssemblyHeadTracked() && HasReviewTable &&
            !ReviewTableSampling && (CaptureBundleMeeting ? BundleLatestReady : A4PoseApplied
                ? meetingModel != null && meetingModel.activeInHierarchy && meetingLoadedVersion == AssemblyAdoptedId
                : physicalOriginal.Root != null && physicalOriginal.Root.activeInHierarchy);

        bool ExplicitSimulator
        {
            get
            {
#if UNITY_EDITOR
                return xr && UnityEditor.SessionState.GetBool("ContextPinExplicitSimulator", false);
#else
                return false;
#endif
            }
        }

        bool ExplicitCameraReady => ExplicitSimulator || (meetingCapture != null &&
            meetingCapture.TryRecentCameraFrame(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), out _));
        string ExplicitCameraStatus => CaptureBundleMeeting && explicitRequest?.Selection is DesignSelection selection
            ? (selection.Source == "live_capture" ? "선택 이미지 · 실물 RGB" : "선택 이미지 · XR 렌더 (실물 RGB 없음)") :
            ExplicitSimulator ? "시뮬레이터 · XR 렌더 사진" :
            ExplicitCameraReady ? "실물 RGB 카메라 준비됨" :
            CaptureBundleMeeting ? "RGB 사용 불가 시 XR 렌더 저장" :
            explicitCameraStarting ? "실물 카메라 권한과 첫 프레임 확인 중" :
            meetingCapture != null && meetingCapture.CameraReady ? "최근 RGB 프레임 없음 · 카메라를 다시 준비하세요." :
            explicitCameraMessage ?? "실물 일치 확인 후 카메라를 준비합니다.";

        string ExplicitTranscript => explicitRequest?.Transcript ?? "";
        string ExplicitSpeechAction
        {
            get
            {
                var p = explicitRequest;
                if (p?.Conflict == true || p?.Sending == true || bundleCommitPending || bundleCommitTarget != null) return "";
                if (p?.Speech == DesignSpeech.Marking) return CaptureBundleMeeting ? "발언 완료" : "수정 발언 완료";
                if (!ExplicitDesignReady || OtherDeviceRecordsDesign || assemblyStarting || assemblyAudio == null || assemblyCaptureFrozen) return "";
                if (p == null || p.RoundId != null || p.BundleSaved) return CaptureBundleMeeting ? "수정 요청" : "수정 발언 시작";
                if (p.Speech == DesignSpeech.Failed) return "다시 말하기";
                return p.Speech == DesignSpeech.Waiting ? (CaptureBundleMeeting ? "수정 요청" : "수정 발언 시작") : "";
            }
        }

        bool CanRetryExplicitRequest => explicitRequest != null && (explicitRequest.Conflict || explicitRequest.RetryTransfer);
        string ExplicitDesignStatus
        {
            get
            {
                var p = explicitRequest;
                if (p?.Conflict == true || !string.IsNullOrEmpty(p?.Error)) return p.Error;
                if (A4ReviewSurfaceActive && latestPaperError != null) return latestPaperError;
                if (!string.IsNullOrWhiteSpace(explicitHint)) return explicitHint;
                if (CaptureBundleMeeting)
                {
                    var set = CurrentCandidateSet;
                    if (set != null && !CapturingPaperEdit && !(p?.Speech == DesignSpeech.Failed && !p.BundleSaved))
                        return BundleCandidateStatus(set);
                    if (SharedPaperMeeting && p?.BundleSaved == true && p.BaseId != AssemblyAdoptedId)
                        return "현재 채택본을 기준으로 다음 수정 요청을 시작할 수 있습니다.";
                    if (CandidateGenerationMeeting && p?.BundleSaved == true) return "저장한 요청의 생성 상태를 확인하고 있습니다.";
                    if (p?.BundleSaved == true) return "수정 요청 저장 완료 · 발언 원음과 선택 이미지를 기록했습니다.";
                    if (p?.Selection != null) return "발언 원음·전사·선택 위치·이미지를 서버에 저장하는 중입니다.";
                    if (p?.Speech == DesignSpeech.Marking) return "수정할 내용을 말하고 ‘발언 완료’를 누르세요.";
                    if (p?.Speech == DesignSpeech.Transcribing) return "지정한 발언의 확정 전사를 기다립니다.";
                    if (p?.Speech == DesignSpeech.Final) return PaperLatestPosition + "의 Latest에서 수정할 곳을 가리키고 오른쪽 A를 누르세요.";
                    if (p?.Speech == DesignSpeech.Failed) return p.SpeechError + " PC에서 이 구간의 전사를 입력할 수도 있습니다.";
                    if (!A4ReviewSurfaceActive) return "메뉴의 ‘A4로 다시 맞추기’로 종이 위치를 확인하세요.";
                    if (ExplicitDesignReady && !OtherDeviceRecordsDesign && assemblyAudio != null)
                        return "회의 전체를 전사 중입니다. 수정할 내용은 ‘수정 요청’을 누르고 말하세요.";
                }
                if (p?.RoundId != null)
                {
                    var round = Values(meetingSnapshot, "rounds").FirstOrDefault(r => TextValue(r,"round_id") == p.RoundId);
                    return TextValue(round, "status") switch
                    {
                        "reviewable" => "수정안이 준비됐습니다. 실제 모델을 비교하고 채택하세요.",
                        "needs_input" => TextValue(round, "question"),
                        "failed" => "후보 생성 실패 · 발언과 대상을 다시 지정하세요.",
                        "committed" => "선택한 버전을 서버에 저장했습니다.",
                        "answered" => TextValue(round, "message"),
                        "deferred" => "후보 채택을 보류했습니다.",
                        "dismissed" => "이번 후보를 채택하지 않기로 했습니다.",
                        "cancelled" => "이 수정 요청이 취소되었습니다.",
                        "superseded" or "stale" => "이 수정 요청이 종료되었습니다.",
                        "collecting" or "generating" => "확정 발언과 부위로 수정안을 해석하고 있습니다.",
                        "building" => "OpenSCAD로 후보를 생성하고 있습니다.",
                        _ => "서버에서 수정 요청 상태를 확인하고 있습니다."
                    };
                }
                if (p?.Sending == true) return "확정 전사·선택 지점·사진을 제출하고 있습니다.";
                if (!ExplicitDesignReady) return "실물 일치 확인과 책상 준비를 마쳐 주세요.";
                if (OtherDeviceRecordsDesign) return "다른 기기가 회의 마이크를 사용 중입니다. 그 기기에서 수정 발언과 부위를 지정하세요.";
                if (p?.Speech == DesignSpeech.Failed) return p.SpeechError;
                if (p?.Speech == DesignSpeech.Marking) return "수정할 내용을 말한 뒤 ‘수정 발언 완료’를 누르세요.";
                if (p?.Speech == DesignSpeech.Transcribing) return "지정한 구간의 마지막 음성과 최종 전사를 기다립니다.";
                if (p?.Selection?.Sending == true) return p.Selection.Uploaded ? "선택 위치를 등록하고 있습니다." : "선택 순간의 사진을 업로드하고 있습니다.";
                if (p?.Speech == DesignSpeech.Final) return "확정 전사 준비됨 · 수정할 부위를 오른쪽 광선으로 가리키고 A";
                if (assemblyAudio == null || assemblyStarting) return "이 기기의 회의 마이크 연결을 기다립니다.";
                if (p?.Selection != null) return MeetingPartLabel(TextValue(p.Selection.Reference, "part_id")) +
                    " · 위치와 사진 준비됨. 수정 발언을 시작하세요.";
                return "수정 발언을 지정하고 오른쪽 광선으로 부위를 가리켜 A를 누르세요.";
            }
        }

        void PrepareExplicitCamera() => StartExplicitCamera(false);

        void StartExplicitCamera(bool userRequested)
        {
            if (SharedPaperMeeting && !MeetingHost || !ExplicitDesignMeeting || ExplicitCameraReady || explicitCameraStarting || meetingTransport == null) return;
            // Android's first permission dialog suspends tracking. A4 placement can finish
            // without RGB, and the existing camera button lets the wearer opt in later.
            if (CaptureBundleMeeting && !userRequested &&
                !OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.PassthroughCameraAccess)) return;
            explicitCameraStarting = true;
            StartCoroutine(WarmExplicitCamera(meetingTransport));
        }

        IEnumerator WarmExplicitCamera(MeetingTransport transport)
        {
            if (meetingCapture == null) meetingCapture = gameObject.AddComponent<RelayEvidence>();
            if (meetingCapture.CameraSource != null && !ExplicitCameraReady)
            {
                meetingCapture.StopCamera();
                yield return null;
                if (meetingTransport != transport) yield break;
            }
            yield return meetingCapture.StartPassthroughCamera(status => explicitCameraMessage = status);
            if (meetingTransport != transport) yield break;
            explicitCameraStarting = false;
            Draw();
        }

        void ShowExplicitHint(string message)
        {
            explicitHint = message;
            Draw();
        }

        PendingDesignRequest PreparePendingDesign()
        {
            if (explicitRequest == null || explicitRequest.RoundId != null || explicitRequest.BundleSaved)
                explicitRequest = new PendingDesignRequest { BaseId = AssemblyAdoptedId,
                    Revision = meetingSnapshot.Value<long>("adopted_revision") };
            return explicitRequest;
        }

        void ToggleExplicitSpeech()
        {
            if (ExplicitSpeechAction.Length == 0) return;
            // A failed speech window cannot be reused as a new server-authoritative request ID.
            if (CaptureBundleMeeting && explicitRequest?.Speech == DesignSpeech.Failed) explicitRequest = null;
            var p = PreparePendingDesign();
            bool start = p.Speech != DesignSpeech.Marking;
            string id = CaptureBundleMeeting ? p.Id : start ? Guid.NewGuid().ToString("N") : p.RangeId;
            if (!TryMarkDesignSpeech(id, start, out var error))
            {
                if (CaptureBundleMeeting && start) explicitRequest = null;
                ShowExplicitHint(error);
                return;
            }
            p.RangeId = id;
            p.RangeMarkedAt = Time.realtimeSinceStartupAsDouble;
            p.Speech = start ? DesignSpeech.Marking : DesignSpeech.Transcribing;
            p.SpeechError = p.Transcript = p.TranscriptSource = null;
            p.UtteranceRefs = null;
            p.ObservedSnapshot = null;
            ShowExplicitHint(null);
        }

        bool HandleExplicitTargetPress(Ray rightAim, bool panelOwnsPointer)
        {
            if (!ExplicitDesignReady || OtherDeviceRecordsDesign || AssemblySampling || AssemblyQrScanning || ReviewTableSampling ||
                movingMeetingPanel || panelOwnsPointer || explicitRequest?.Conflict == true ||
                explicitRequest?.Sending == true || (explicitRequest?.Submission != null && explicitRequest.RoundId == null)) return false;
            if (CaptureBundleMeeting && (explicitRequest?.Speech != DesignSpeech.Final || explicitRequest.BundleSaved ||
                explicitRequest.Selection != null || !BundleLatestReady)) return false;
            if (!Physics.Raycast(rightAim, out var hit, 12) || !hit.collider.TryGetComponent<RelayHit>(out var part) ||
                string.IsNullOrEmpty(part.meetingVersion) || ModelRootForHit(part.transform) is not Transform root ||
                !Values(meetingSnapshot["profile"], "semantic_parts").Any(s => TextValue(s,"part_id") == part.role))
            {
                ShowExplicitHint(CaptureBundleMeeting ? PaperLatestPosition + "의 Latest 표면을 가리키고 A를 누르세요." :
                    "오른쪽 광선을 정렬한 모델의 수정 부위에 맞추고 A를 눌러 주세요.");
                return false;
            }
            var p = PreparePendingDesign();
            if (p.Selection != null) return false; // A double-press must not replace the frozen evidence.
            if (CaptureBundleMeeting)
            {
                if (root != meetingModel.transform || part.meetingVersion != p.BaseId ||
                    p.BaseId != AssemblyAdoptedId || p.Revision != meetingSnapshot.Value<long>("adopted_revision"))
                {
                    ShowExplicitHint(PaperLatestPosition + "의 Latest에서 수정할 곳을 다시 가리키세요.");
                    return false;
                }
                return FreezeEditBundle(rightAim, hit, part, root, p);
            }
            long selectedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long capturedAt = selectedAt;
            var photo = new DesignSelection { Stamp = new JObject(), Source = ExplicitSimulator ? "xr_render" : "live_capture" };
            AddMeetingClock(photo.Stamp);
            try
            {
                if (meetingCapture == null) meetingCapture = gameObject.AddComponent<RelayEvidence>();
                if (!ExplicitSimulator)
                {
                    // Read the SDK timestamp of the latest sensor image on the selection frame.
                    // Snapshot synchronously reads its texture and timestamped camera pose.
                    if (!meetingCapture.TryRecentCameraFrame(selectedAt, out capturedAt))
                    {
                        PrepareExplicitCamera();
                        ShowExplicitHint("실물 RGB 카메라가 준비되면 부위를 다시 가리키고 A를 눌러 주세요.");
                        return false;
                    }
                }
                photo.Image = meetingCapture.Snapshot(view, hit.point, !ExplicitSimulator, out var uv);
                if (photo.Image == null || photo.Image.Length == 0) throw new InvalidOperationException("사진 촬영 실패 · 다시 A를 눌러 주세요.");
                var local = root.InverseTransformPoint(hit.point);
                photo.Reference = new JObject
                {
                    ["selection_id"] = photo.Id, ["reference_version_id"] = part.meetingVersion, ["part_id"] = part.role,
                    ["hit_point_asset_m"] = Coordinates(new Vector3(-local.x, local.y, local.z)),
                    ["image_uv"] = new JArray(uv.x, uv.y), ["captured_at_ms"] = capturedAt,
                    ["selected_at_ms"] = selectedAt, ["source_mode"] = photo.Source, ["tracking_state"] = "tracking"
                };
            }
            catch (Exception error)
            {
                ShowExplicitHint("사진을 다시 촬영해 주세요. " + error.Message);
                return false;
            }
            if (photo.Source == "live_capture" && !meetingPhotoCaptured)
            {
                meetingPhotoCaptured = true;
                SendMeetingCommand("capabilities", new JObject { ["capabilities"] = MeetingCapabilities() }, false);
            }
            photo.Stamp["client_request_id"] = p.Id;
            photo.Stamp["selection_id"] = photo.Id;
            photo.Stamp["captured_at_ms"] = capturedAt;
            p.Selection = photo;
            p.Error = explicitHint = null;
            StartCoroutine(PublishExplicitTarget(p, photo));
            return true;
        }

        bool FreezeEditBundle(Ray rightAim, RaycastHit hit, RelayHit part, Transform root, PendingDesignRequest p)
        {
            if (!TryCaptureDesignSelection(rightAim, hit, part, root, p, out var photo, out var error))
            {
                ShowExplicitHint(error);
                return false;
            }
            if (PaperRenderable)
            {
                var shared = PaperSelectionPayload(reviewTable, PaperFrameRevision, p.BaseId, rightAim, hit.point);
                foreach (string field in new[] { "paper_frame_revision", "ray_origin_paper", "ray_direction_paper", "hit_position_paper" })
                    photo.BundleEvidence[field] = shared[field].DeepClone();
                shared["selection_id"] = photo.Id;
                SendPaperCommand("paper_selection", shared);
            }
            photo.Reference.Merge(photo.BundleEvidence);
            p.Selection = photo;
            p.Error = explicitHint = null;
            if (photo.Source == "live_capture" && !meetingPhotoCaptured)
            {
                meetingPhotoCaptured = true;
                SendMeetingCommand("capabilities", new JObject { ["capabilities"] = MeetingCapabilities() }, false);
            }
            StartCoroutine(PublishExplicitTarget(p, photo));
            return true;
        }

        static JObject FrozenDesignCommand(string kind, JObject body) => new JObject
        {
            ["command_id"] = Guid.NewGuid().ToString("N"), ["type"] = kind, ["payload"] = body
        };

        JObject ExplicitPayload(PendingDesignRequest p) => new JObject
        {
            ["client_request_id"] = p.Id, ["base_version_id"] = p.BaseId,
            ["expected_adopted_revision"] = p.Revision, ["reference_selection"] = p.Selection.Reference.DeepClone()
        };

        bool CurrentSelection(PendingDesignRequest p, DesignSelection selection) =>
            explicitRequest == p && p.Selection == selection && !p.Conflict && (CaptureBundleMeeting ||
                ExplicitDesignReady && p.BaseId == AssemblyAdoptedId && p.Revision == meetingSnapshot.Value<long>("adopted_revision"));

        IEnumerator PublishExplicitTarget(PendingDesignRequest p, DesignSelection selection)
        {
            var transport = meetingTransport;
            selection.Sending = true;
            Draw();
            long status = 0;
            if (!selection.Uploaded)
            {
                JObject receipt = null;
                string uploadError = null;
                yield return transport.UploadPhotoWithStatus(transport.SessionPath + "/media", selection.Image,
                    TextValue(selection.Reference,"reference_version_id"), TextValue(selection.Reference,"part_id"), selection.Stamp,
                    (response, error, code) => { receipt = response; uploadError = error; status = code; }, selection.Source);
                if (!CurrentSelection(p, selection))
                {
                    if (explicitRequest == p && p.Selection == selection) InvalidateExplicitSelection();
                    yield break;
                }
                string id = TextValue(receipt, "evidence_id");
                if (uploadError != null || id.Length == 0)
                {
                    TargetTransferFailed(p, status, uploadError ?? "서버가 사진 ID를 반환하지 않았습니다.");
                    yield break;
                }
                selection.Reference["image_id"] = id;
                selection.Uploaded = true;
                selection.Image = null;
            }
            if (!CurrentSelection(p, selection))
            {
                if (explicitRequest == p && p.Selection == selection) InvalidateExplicitSelection();
                yield break;
            }
            selection.TargetCommand ??= FrozenDesignCommand("design_target", ExplicitPayload(p));
            JObject reply = null;
            string issue = null;
            yield return transport.JsonWithStatus(transport.SessionPath + "/commands", selection.TargetCommand,
                (response, error, code) => { reply = response; issue = error; status = code; });
            if (!CurrentSelection(p, selection))
            {
                if (explicitRequest == p && p.Selection == selection) InvalidateExplicitSelection();
                yield break;
            }
            if (reply?["snapshot"] is JObject snapshot) AcceptMeetingSnapshot(snapshot);
            selection.Sending = false;
            if (issue != null) { TargetTransferFailed(p, status, issue); yield break; }
            selection.Published = true;
            UpdateExplicitDesign();
            Draw();
        }

        void TargetTransferFailed(PendingDesignRequest p, long status, string reason)
        {
            p.Selection.Sending = false;
            if (status is 400 or 409 or 413 or 415 or 422)
            {
                if (CaptureBundleMeeting)
                {
                    p.Conflict = true;
                    p.Error = reason;
                    Draw();
                    return;
                }
                // A rejected reference needs a new selection, while the marked speech stays usable.
                p.Selection = null;
                p.RetryTransfer = false;
                ShowExplicitHint(reason + " · 오른쪽 A로 다시 지정하세요.");
            }
            else TransferFailed(p, reason);
        }

        void TransferFailed(PendingDesignRequest p, string reason)
        {
            p.Error = reason;
            p.RetryTransfer = true;
            Draw();
        }

        void RetryExplicitRequest()
        {
            var p = explicitRequest;
            if (p == null || !CanRetryExplicitRequest) return;
            if (p.Conflict)
            {
                explicitRequest = null;
                ShowExplicitHint("새 Latest에서 수정 발언과 부위를 다시 지정하세요.");
                return;
            }
            p.RetryTransfer = false;
            p.Error = null;
            explicitHint = null;
            if (!CaptureBundleMeeting && !ExplicitDesignReady) { InvalidateExplicitSelection(); return; }
            if (p.Selection != null && !p.Selection.Published)
                StartCoroutine(PublishExplicitTarget(p, p.Selection));
            else UpdateExplicitDesign();
            Draw();
        }

        void UpdateExplicitDesign()
        {
            if (!ExplicitDesignMeeting || explicitRequest == null) return;
            var p = explicitRequest;
            if (p.RoundId != null || p.Conflict || p.BundleSaved) return;
            bool frozenBundle = CaptureBundleMeeting && p.Selection != null;
            // After dispatch the immutable envelope can be retried across a lost response.
            // Before dispatch an obsolete reference cannot be applied to the new head.
            if (!frozenBundle && p.Submission == null && (p.BaseId != AssemblyAdoptedId || p.Revision != meetingSnapshot.Value<long>("adopted_revision")))
            {
                if (p.Speech == DesignSpeech.Marking) TryMarkDesignSpeech(p.RangeId, false, out _);
                p.Conflict = true;
                p.Error = "준비 중 다른 채택본이 저장됐습니다. 새 Latest를 확인한 뒤 다시 지정하세요.";
                Draw();
                return;
            }
            if (!frozenBundle && p.Submission == null && p.Selection != null && !ExplicitDesignReady) InvalidateExplicitSelection();
            if (p.ObservedSnapshot != meetingSnapshot)
            {
                p.ObservedSnapshot = meetingSnapshot;
                if (p.Submission != null && CaptureBundleMeeting && HasSavedEditBundle(p))
                {
                    MarkEditBundleSaved(p);
                    return;
                }
                if (p.Submission != null && !CaptureBundleMeeting)
                {
                    var seen = Values(meetingSnapshot, "rounds").FirstOrDefault(r =>
                        TextValue(r, "client_request_id") == p.Id || TextValue(r["context"], "client_request_id") == p.Id);
                    if (seen != null)
                    {
                        p.RoundId = TextValue(seen, "round_id");
                        p.Sending = p.RetryTransfer = false;
                        p.Error = explicitHint = null;
                        Draw();
                        return;
                    }
                }
                if (p.RangeId != null && meetingSnapshot["design_speech_ranges"]?[p.RangeId] is JObject range)
                {
                    var status = TextValue(range, "status");
                    bool corrected = CaptureBundleMeeting && p.Speech == DesignSpeech.Final && p.Submission == null &&
                        status == "final" && range["utterances"] is JArray rows &&
                        !rows.Select(row => row.Value<int>("revision")).SequenceEqual(p.UtteranceRefs.Select(row => row.Value<int>("revision")));
                    if (p.Speech == DesignSpeech.Transcribing || p.Speech == DesignSpeech.Marking && status == "failed" ||
                        CaptureBundleMeeting && p.Speech == DesignSpeech.Failed && status == "final" || corrected)
                        ReceiveExplicitSpeech(p, range);
                }
            }
            if ((p.Speech is DesignSpeech.Marking or DesignSpeech.Transcribing) &&
                (assemblyCaptureFrozen || AssemblySessionStatus != "recording" || assemblyAudio == null) &&
                meetingSnapshot["design_speech_ranges"]?[p.RangeId]?["end_sample"]?.Type != JTokenType.Integer &&
                Time.realtimeSinceStartupAsDouble - p.RangeMarkedAt > 3)
            {
                p.Speech = DesignSpeech.Failed;
                p.SpeechError = "수정 발언의 마지막 샘플 수신을 확인하지 못했습니다. 마이크 연결 후 다시 녹음하세요.";
                Draw();
            }
            if ((!frozenBundle && !ExplicitDesignReady) || p.Speech != DesignSpeech.Final || p.Selection?.Published != true ||
                p.Sending || p.RetryTransfer) return;
            if (p.Submission == null)
            {
                var payload = ExplicitPayload(p);
                payload["speech_range_id"] = p.RangeId;
                payload["utterance_refs"] = p.UtteranceRefs.DeepClone();
                if (CaptureBundleMeeting) payload["output"] = "bundle";
                p.Submission = FrozenDesignCommand("design_request", payload);
            }
            p.Sending = true;
            StartCoroutine(SubmitExplicitDesign(p));
        }

        bool HasSavedEditBundle(PendingDesignRequest p) => meetingSnapshot?["edit_requests"]?[p.Id] is JObject;

        void MarkEditBundleSaved(PendingDesignRequest p)
        {
            p.BundleSaved = true;
            p.Sending = p.RetryTransfer = false;
            p.Error = explicitHint = null;
            Draw();
        }

        void ReceiveExplicitSpeech(PendingDesignRequest p, JObject range)
        {
            var state = TextValue(range, "status");
            if (state is "recording" or "transcribing") return;
            if (state == "final" && range["utterances"] is JArray items && items.Count > 0 && (CaptureBundleMeeting || items.Count <= 5) &&
                items.All(item => !string.IsNullOrEmpty(TextValue(item,"utterance_id")) && item["revision"]?.Type == JTokenType.Integer &&
                    !string.IsNullOrWhiteSpace(TextValue(item,"text"))))
            {
                p.Transcript = string.Join(" ", items.Select(item => TextValue(item, "text")));
                p.TranscriptSource = TextValue(range, "transcript_source");
                p.SpeechError = null;
                p.UtteranceRefs = new JArray(items.Select(item => new JObject
                {
                    ["utterance_id"] = TextValue(item,"utterance_id"), ["revision"] = item.Value<int>("revision")
                }));
                p.Speech = DesignSpeech.Final;
            }
            else
            {
                p.Speech = DesignSpeech.Failed;
                p.SpeechError = state == "no_speech" ? "수정 요청을 듣지 못했습니다. 다시 말해 주세요." : TextValue(range, "error");
                if (string.IsNullOrWhiteSpace(p.SpeechError)) p.SpeechError = "지정한 구간을 전사하지 못했습니다. 다시 말해 주세요.";
            }
            explicitHint = null;
            Draw();
        }

        IEnumerator SubmitExplicitDesign(PendingDesignRequest p)
        {
            var transport = meetingTransport;
            JObject reply = null;
            string issue = null;
            long status = 0;
            Draw();
            yield return transport.JsonWithStatus(transport.SessionPath + "/commands", p.Submission,
                (response, error, code) => { reply = response; issue = error; status = code; });
            if (explicitRequest != p || transport != meetingTransport) yield break;
            if (reply?["snapshot"] is JObject snapshot) AcceptMeetingSnapshot(snapshot);
            p.Sending = false;
            if (p.BundleSaved) yield break;
            if (CaptureBundleMeeting && HasSavedEditBundle(p))
            {
                MarkEditBundleSaved(p);
                yield break;
            }
            if (p.RoundId != null) yield break;
            var roundId = TextValue(reply, "round_id");
            if (issue != null || roundId.Length == 0 || CaptureBundleMeeting)
            {
                if (status is 400 or 409 or 422)
                {
                    p.Conflict = true;
                    p.Error = (issue ?? "요청이 변경됐습니다.") + " · 발언과 대상을 다시 지정하세요.";
                    Draw();
                }
                else TransferFailed(p, issue ?? "서버의 저장 결과를 확인하지 못했습니다. 같은 요청으로 다시 시도하세요.");
                yield break;
            }
            p.RoundId = roundId;
            p.Error = explicitHint = null;
            Draw();
        }

        void InvalidateExplicitSelection()
        {
            var p = explicitRequest;
            if (p == null || p.Submission != null || p.Selection == null || CaptureBundleMeeting) return;
            p.Selection = null;
            p.RetryTransfer = false;
            if (!p.Conflict) p.Error = null;
            explicitHint = "정렬과 책상을 다시 확인한 뒤 오른쪽 A로 위치와 사진을 다시 지정하세요.";
            Draw();
        }

        void ReleaseExplicitDesign()
        {
            explicitRequest = null;
            explicitCameraStarting = false;
            explicitHint = explicitCameraMessage = null;
        }
    }
}
