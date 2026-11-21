using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        string meetingServerOverride, selectedTargetVersion, selectedTargetPart, selectedTargetImage;
        Vector3 selectedTargetPoint;
        bool selectedInLocalPreview;
        bool targetSnapshotPending, designRequestPending, collaborativeEditing, previewSending;
        long previewRevision, targetSelectionRevision;
        string currentEditLease;
        string seenDraftVersion, selectedSlider;
        JObject sliderValues = new JObject();
        double previewDue = double.PositiveInfinity, leaseRenewAt, pointerDue;
        readonly Dictionary<string, GameObject> participantPointers = new Dictionary<string, GameObject>();
        Material participantPointerMaterial;
        JObject CurrentDraft => meetingSnapshot?["edit_draft"] as JObject;
        bool OwnDraft => TextValue(CurrentDraft, "owner") == meetingParticipant || TextValue(CurrentDraft, "owner_id") == meetingParticipant;
        bool CollaborativeDraftVisible => SpatialAssembly && assemblyCompare && collaborativeEditing && CurrentDraft != null;
        Vector3 WorkspaceOrigin => assemblyReference.position + assemblyReference.rotation * AssemblyPlacementMount.position;
        Quaternion WorkspaceRotation => assemblyReference.rotation * AssemblyPlacementMount.rotation;
        static JArray Coordinates(Vector3 value) => new JArray(value.x, value.y, value.z);
        Vector3 WorkspaceOffset(string name, int index, Vector3 fallback)
        {
            var field = meetingSnapshot?["workspace"]?[name];
            return ReadMeetingVector(index >= 0 ? field?[index] : field) ?? fallback;
        }

        void PositionPhysicalOriginal()
        {
            if (physicalOriginal.Root == null) return;
            if (UsingLocalCandidatePreview)
            {
                PlaceReviewModel(physicalOriginal, ReviewLayoutCount - 1, ReviewLayoutCount);
                return;
            }
            bool visible = !A4PoseApplied && (assemblyAlignment == AssemblyAlignment.Confirmed ||
                (ExplicitDesignMeeting && assemblyAlignment == AssemblyAlignment.Preview));
            if (physicalOriginal.Root.activeSelf != visible) physicalOriginal.Root.SetActive(visible);
            if (!visible) return;
            physicalOriginal.Root.transform.SetPositionAndRotation(WorkspaceOrigin, WorkspaceRotation);
            physicalOriginal.Root.transform.localScale = Vector3.one;
        }

        Transform ModelRootForHit(Transform transform)
        {
            var slots = new[] { meetingPreview, physicalOriginal, assemblyComparison }.Concat(cableComparisons);
            return slots.FirstOrDefault(slot => slot.Root != null && transform.IsChildOf(slot.Root.transform))?.Root.transform;
        }

        void SelectCollaborativeTarget(string version, string part, Vector3 point, Transform source)
        {
            if (ExplicitDesignMeeting) return;
            var root = ModelRootForHit(source);
            if (root == null) return;
            selectedTargetVersion = version;
            selectedTargetPart = part;
            selectedInLocalPreview = UsingLocalCandidatePreview;
            var local = root.InverseTransformPoint(point);
            // glTFast converts the glTF right-handed X axis to Unity at import.
            selectedTargetPoint = new Vector3(-local.x, local.y, local.z);
            selectedTargetImage = null;
            assemblyFeedback = "대상 선택 · " + MeetingPartLabel(part) + " · 등록된 공통 치수 적용";
            var selection = new JObject
            {
                ["base_version_id"] = version,
                ["expected_adopted_revision"] = meetingSnapshot.Value<long?>("adopted_revision") ?? 0,
                ["part_id"] = part,
                ["hit_point_asset_m"] = Coordinates(selectedTargetPoint)
            };
            if (selectedInLocalPreview) selection["tracking_state"] = "local_preview";
            AddMeetingClock(selection);
            StartCoroutine(CaptureSelectedModel(version, part, ++targetSelectionRevision, selection));
        }

        IEnumerator CaptureSelectedModel(string version, string part, long selectionRevision, JObject selection)
        {
            var transport = meetingTransport;
            targetSnapshotPending = true;
            var target = RenderTexture.GetTemporary(640, 480, 24);
            var priorTarget = view.targetTexture;
            var priorActive = RenderTexture.active;
            byte[] jpeg = null;
            Texture2D texture = null;
            try
            {
                view.targetTexture = target;
                view.Render();
                RenderTexture.active = target;
                texture = new Texture2D(640, 480, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, 640, 480), 0, 0);
                texture.Apply();
                jpeg = texture.EncodeToJPG(75);
            }
            catch (Exception error)
            {
                assemblyFeedback = "선택 화면을 저장하지 못했습니다 · " + error.GetType().Name;
            }
            finally
            {
                view.targetTexture = priorTarget;
                RenderTexture.active = priorActive;
                if (texture != null) Destroy(texture);
                RenderTexture.ReleaseTemporary(target);
            }
            if (jpeg == null)
            {
                if (selectionRevision == targetSelectionRevision) targetSnapshotPending = false;
                Draw();
                yield break;
            }
            var stamp = new JObject();
            foreach (var key in new[] { "source_monotonic_ms", "clock_offset_ms", "clock_uncertainty_ms" })
                if (selection[key] != null) stamp[key] = selection[key].DeepClone();
            yield return transport.UploadPhoto(transport.SessionPath + "/media", jpeg, version, part, stamp, (reply, error) =>
            {
                if (meetingTransport != transport || selectedTargetVersion != version || selectedTargetPart != part || targetSelectionRevision != selectionRevision) return;
                selectedTargetImage = TextValue(reply, "evidence_id");
                if (string.IsNullOrEmpty(selectedTargetImage)) selectedTargetImage = TextValue(reply, "asset_id");
                if (error == null && !string.IsNullOrEmpty(selectedTargetImage) && TextValue(meetingSnapshot, "provider_mode") == "live")
                {
                    selection["image_id"] = selectedTargetImage;
                    StartCoroutine(RunAssemblyAction(transport, "design_target", selection, (_, issue) =>
                    {
                        if (selectionRevision != targetSelectionRevision) return;
                        targetSnapshotPending = false;
                        assemblyFeedback = issue ?? "대상을 선택했습니다. 대화 중 ‘수정안 보여줘’라고 말하세요.";
                    }, preserveClock: true));
                }
                else targetSnapshotPending = false;
                if (error != null) assemblyFeedback = "선택 화면 저장 실패 · 다시 선택해 주세요.";
                Draw();
            }, "xr_render");
        }

        bool HasSelectedDesignTarget => !string.IsNullOrEmpty(selectedTargetVersion) && !string.IsNullOrEmpty(selectedTargetImage);
        bool HasRegisteredDesignScope => !string.IsNullOrEmpty(TextValue(meetingSnapshot?["profile"]?["edit_scope"], "part_id"));
        JToken LatestFinalUtterance => Values(meetingSnapshot, "utterances").LastOrDefault(line => TextValue(line, "status") == "final");
        JObject CurrentDesignUtterance
        {
            get
            {
                var eligible = meetingSnapshot?["active_design_utterance"] as JObject;
                return eligible != null && TextValue(eligible, "utterance_id") == TextValue(LatestFinalUtterance, "utterance_id")
                    ? eligible : null;
            }
        }

        void RequestSelectedDesign()
        {
            if (ExplicitDesignMeeting || meetingTransport == null || targetSnapshotPending || designRequestPending) return;
            var speech = CurrentDesignUtterance;
            if (speech == null || (!HasSelectedDesignTarget && !HasRegisteredDesignScope))
            {
                assemblyFeedback = speech == null ? "어떤 부분을 어떻게 바꿀지 말해 주세요." : "수정할 모델 부위를 먼저 선택하세요.";
                Draw();
                return;
            }
            var payload = new JObject
            {
                ["base_version_id"] = HasSelectedDesignTarget ? selectedTargetVersion : AssemblyAdoptedId,
                ["expected_adopted_revision"] = meetingSnapshot.Value<long?>("adopted_revision") ?? 0,
                ["utterance_ids"] = new JArray(TextValue(speech, "utterance_id"))
            };
            if (HasSelectedDesignTarget)
            {
                payload["part_id"] = selectedTargetPart;
                payload["hit_point_asset_m"] = Coordinates(selectedTargetPoint);
                payload["image_id"] = selectedTargetImage;
                if (selectedInLocalPreview) payload["tracking_state"] = "local_preview";
            }
            else payload["use_registered_scope"] = true;

            StartLocalCandidatePreview();
            designRequestPending = true;
            assemblyFeedback = "확정 발언으로 수정안을 요청하고 있습니다.";
            StartCoroutine(RunAssemblyAction(meetingTransport, "design_request", payload, (_, error) =>
            {
                designRequestPending = false;
                assemblyFeedback = error ?? "";
            }));
            Draw();
        }

        void BeginCollaborativeEdit(JToken round, JToken candidate)
        {
            var payload = new JObject
            {
                ["base_version_id"] = TextValue(candidate, "version_id"),
                ["expected_adopted_revision"] = meetingSnapshot.Value<long?>("adopted_revision") ?? 0,
                ["round_id"] = TextValue(round, "round_id"), ["candidate_id"] = TextValue(candidate, "candidate_id"),
            };
            StartCoroutine(RunAssemblyAction(meetingTransport, "edit_begin", payload, (_, error) =>
            {
                assemblyFeedback = error ?? "";
                if (error == null) { collaborativeEditing = true; selectedSlider = null; seenDraftVersion = null; ReceiveCollaborativeDraft(); }
            }));
        }

        string sharedComparisonSeen;
        void ReceiveSharedComparison()
        {
            if (!SpatialAssembly || collaborativeEditing || assemblyHistory) return;
            var shared = meetingSnapshot?["shared_preview"];
            var version = TextValue(shared, "version_id");
            if (version.Length == 0) { sharedComparisonSeen = null; return; }
            string evidence = TextValue(shared, "evidence_id");
            string change = evidence.Length > 0 ? evidence : version;
            if (change == sharedComparisonSeen) return;
            sharedComparisonSeen = change;
            // A snapshot acknowledging an earlier local click can arrive after the next click.
            // Older servers omit the author. In a solo meeting its identity is still unambiguous.
            string author = TextValue(shared, "participant_id");
            if (author == meetingParticipant || (author.Length == 0 && Values(meetingSnapshot, "participants").Count() == 1)) return;
            if (!CanCompareModels) return;
            assemblyCompare = true;
            assemblyCompareRound = TextValue(shared, "round_id");
            meetingViewedVersion = version;
        }

        void ReceiveCollaborativeDraft()
        {
            var draft = CurrentDraft;
            if (draft == null || TextValue(draft, "status") is "cancelled" or "expired" or "committed")
            {
                if (collaborativeEditing) { collaborativeEditing = false; previewDue = double.PositiveInfinity; seenDraftVersion = null; }
                return;
            }
            var lease = TextValue(draft, "lease_id");
            if (SpatialAssembly && CanCompareModels && (lease != currentEditLease || collaborativeEditing))
            {
                collaborativeEditing = assemblyCompare = true;
                assemblyHistory = assemblyClarify = false;
                assemblyCompareRound = TextValue(draft, "round_id");
            }
            if (lease != currentEditLease)
            {
                currentEditLease = lease;
                previewRevision = 0;
                previewDue = double.PositiveInfinity;
                seenDraftVersion = selectedSlider = null;
            }
            var revision = draft.Value<long?>("preview_revision") ?? 0;
            if (!previewSending && double.IsPositiveInfinity(previewDue) && revision >= previewRevision)
            {
                previewRevision = revision;
                sliderValues = new JObject();
                foreach (var field in Values(draft, "fields"))
                    sliderValues[TextValue(field, "parameter_id")] = field["current_value"]?.DeepClone();
            }
            var version = TextValue(draft, "version_id");
            if (version.Length > 0 && version != seenDraftVersion && TextValue(draft, "status") is "ready" or "reviewable")
            {
                seenDraftVersion = version;
                SetMeetingViewedVersion(version);
            }
        }

        void DrawCollaborativeEdit()
        {
            var draft = CurrentDraft;
            RoundBox("SharedEditPanel", content, 0, 58, SpatialWidth, 245, SpatialBackground, 12);
            var fields = Values(draft, "fields").ToArray();
            if (selectedSlider == null && fields.Length > 0) selectedSlider = TextValue(fields[0], "parameter_id");
            var field = fields.FirstOrDefault(item => TextValue(item, "parameter_id") == selectedSlider);
            var owner = TextValue(draft, "owner_id");
            var name = Values(meetingSnapshot, "participants").FirstOrDefault(item => TextValue(item, "participant_id") == owner);
            Label("EditOwner", content, 12, 65, 396, 30, OwnDraft ? "치수 조정 · 채택 전 초안" : TextValue(name, "display_name") + " 님이 조정 중", SpatialFont(19), HeaderText);
            for (int i = 0; i < Math.Min(fields.Length, 3); i++)
            {
                var item = fields[i];
                Button("EditField-" + i, 12 + 133 * i, 103, 125, 35, TextValue(item, "label"), () =>
                { selectedSlider = TextValue(item, "parameter_id"); Draw(); }, selectedSlider == TextValue(item, "parameter_id"));
            }
            if (field != null)
            {
                var key = TextValue(field, "parameter_id");
                var value = sliderValues.Value<double?>(key) ?? field.Value<double?>("current_value") ?? 0;
                var min = field.Value<double?>("min_value") ?? 0;
                var max = field.Value<double?>("max_value") ?? 1;
                var step = field.Value<double?>("step") ?? .1;
                Label("EditValue", content, 12, 145, 396, 32, $"{value:0.###} {TextValue(field, "unit")}  ·  {min:0.###}–{max:0.###}  ·  간격 {step:0.###}", SpatialFont(19), HeaderText);
                var slider = Button("SharedParameterSlider", 16, 182, 388, 34, "", () => { });
                RoundBox("SliderFill", slider.transform, 0, 13, Mathf.Clamp01((float)((value - min) / (max - min))) * 388, 8, Mint, 4);
                if (OwnDraft) slider.dragged = point =>
                {
                    var fraction = Mathf.Clamp01(slider.transform.InverseTransformPoint(point).x / 388);
                    var next = Math.Max(min, Math.Min(max, Math.Round((min + fraction * (max - min)) / step) * step));
                    if (Math.Abs(next - value) < step * .1) return;
                    sliderValues[key] = next;
                    previewDue = Time.unscaledTimeAsDouble + .55;
                    Draw();
                };
            }
            var status = TextValue(draft, "status");
            bool exact = OwnDraft && !previewSending && double.IsPositiveInfinity(previewDue) &&
                status is "ready" or "reviewable" && SpatialComparisonLoadedVersion == TextValue(draft, "version_id") &&
                previewRevision == (draft.Value<long?>("preview_revision") ?? 0);
            if (exact) Button("SharedEditAdopt", 12, 234, 190, 38, "이 치수로 채택", CommitCollaborativeEdit, true);
            else Label("SharedEditStatus", content, 12, 231, 250, 57,
                status == "failed" ? TextValue(draft, "error") : "정확한 형상 확인 중", SpatialFont(17), SpatialMuted);
            if (OwnDraft) Button("SharedEditCancel", 279, 234, 128, 38, "조정 취소", () =>
            {
                previewDue = double.PositiveInfinity;
                StartCoroutine(RunAssemblyAction(meetingTransport, "edit_cancel", new JObject { ["lease_id"] = TextValue(draft, "lease_id") }, (_, error) =>
                { assemblyFeedback = error ?? ""; if (error == null) CloseSpatialComparison(); }));
            });
        }

        void CommitCollaborativeEdit()
        {
            var draft = CurrentDraft;
            StartCoroutine(RunAssemblyAction(meetingTransport, "edit_commit", new JObject
            {
                ["lease_id"] = TextValue(draft, "lease_id"), ["preview_revision"] = previewRevision,
                ["displayed_version_id"] = SpatialComparisonLoadedVersion,
            }, (_, error) => { assemblyFeedback = error ?? ""; if (error == null) CloseSpatialComparison(); }));
        }

        void UpdateMeetingCollaboration()
        {
            UpdateExplicitDesign();
            var now = Time.unscaledTimeAsDouble;
            if (CurrentDraft != null && OwnDraft && collaborativeEditing)
            {
                if (now >= leaseRenewAt)
                {
                    leaseRenewAt = now + 25;
                    SendMeetingCommand("edit_renew", new JObject { ["lease_id"] = TextValue(CurrentDraft, "lease_id") }, false);
                }
                if (now >= previewDue && !previewSending)
                {
                    previewDue = double.PositiveInfinity;
                    previewSending = true;
                    previewRevision++;
                    StartCoroutine(RunAssemblyAction(meetingTransport, "edit_preview", new JObject
                    {
                        ["lease_id"] = TextValue(CurrentDraft, "lease_id"), ["preview_revision"] = previewRevision,
                        ["parameters"] = sliderValues.DeepClone(),
                    }, (_, error) => { previewSending = false; assemblyFeedback = error ?? ""; }));
                }
            }
            UpdateParticipantPointers();
        }

        void UpdateCollaborationPointer(RelayHit hit, Vector3 point, bool clicked)
        {
            bool held = xr ? leftTriggerWasDown || rightTriggerWasDown || wasPressed : Mouse.current?.leftButton.isPressed == true;
            if (held && hit != null) hit.dragged?.Invoke(point);
            if (meetingSnapshot == null || hit == null || string.IsNullOrEmpty(hit.meetingVersion) ||
                SpatialAssembly && assemblyAlignment != AssemblyAlignment.Confirmed || Time.unscaledTimeAsDouble < pointerDue) return;
            var root = ModelRootForHit(hit.transform);
            if (root == null) return;
            pointerDue = Time.unscaledTimeAsDouble + .35;
            var local = root.InverseTransformPoint(point);
            SendMeetingCommand("pointer", new JObject
            {
                ["version_id"] = hit.meetingVersion, ["part_id"] = hit.role,
                ["hit_point_asset_m"] = Coordinates(new Vector3(-local.x, local.y, local.z)),
                ["workspace_position_m"] = Coordinates(Quaternion.Inverse(WorkspaceRotation) * (point - WorkspaceOrigin)),
                ["tracking_state"] = xr ? "tracking" : "desktop_view",
            }, false);
        }

        void UpdateParticipantPointers()
        {
            var pointers = meetingSnapshot?["pointers"] as JObject;
            foreach (var marker in participantPointers.Values) marker.SetActive(false);
            if (!SpatialAssembly || assemblyAlignment != AssemblyAlignment.Confirmed || pointers == null) return;
            foreach (var pair in pointers.Properties())
            {
                if (pair.Name == meetingParticipant || TextValue(pair.Value, "tracking_state") != "tracking") continue;
                var position = ReadMeetingVector(pair.Value["workspace_position_m"]);
                var stamp = pair.Value.Value<long?>("updated_at") ?? 0;
                if (!position.HasValue || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - stamp > 3000) continue;
                if (!participantPointers.TryGetValue(pair.Name, out var marker))
                {
                    marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    Destroy(marker.GetComponent<Collider>());
                    marker.name = "Participant selection";
                    marker.transform.localScale = Vector3.one * .008f;
                    if (participantPointerMaterial == null)
                    {
                        participantPointerMaterial = new Material(Shader.Find("Sprites/Default"));
                        participantPointerMaterial.color = new Color(1, .65f, .15f);
                    }
                    marker.GetComponent<Renderer>().sharedMaterial = participantPointerMaterial;
                    participantPointers.Add(pair.Name, marker);
                }
                marker.transform.position = WorkspaceOrigin + WorkspaceRotation * position.Value;
                marker.SetActive(true);
            }
        }

        void PublishTrackingLoss()
        {
            if (meetingTransport != null && !string.IsNullOrEmpty(AssemblyAdoptedId))
                SendMeetingCommand("pointer", new JObject { ["version_id"] = AssemblyAdoptedId,
                    ["part_id"] = selectedTargetPart ?? "outer_shell", ["hit_point_asset_m"] = new JArray(0, 0, 0), ["tracking_state"] = "lost" }, false);
        }

        const string MostRecentMeeting = "MeetingResume";
        const string MostRecentLiveMeeting = "MeetingLiveResume";

        void WriteMeetingShortcut(string prefix, MeetingTransport transport, string mode)
        {
            PlayerPrefs.SetString(prefix + "Address", transport.Address.AbsoluteUri);
            PlayerPrefs.SetString(prefix + "Session", transport.SessionId);
            PlayerPrefs.SetString(prefix + "Token", transport.Token);
            PlayerPrefs.SetString(prefix + "Participant", meetingParticipant);
            PlayerPrefs.SetString(prefix + "Mode", mode);
        }

        void SaveMeetingResume(MeetingTransport transport, JObject snapshot)
        {
            string mode = TextValue(snapshot, "provider_mode");
            WriteMeetingShortcut(MostRecentMeeting, transport, mode);
            if (mode == "live") WriteMeetingShortcut(MostRecentLiveMeeting, transport, mode);
            PlayerPrefs.Save();
        }

        string LiveResumeShortcut()
        {
            if (PlayerPrefs.HasKey(MostRecentLiveMeeting + "Session")) return MostRecentLiveMeeting;
            if (!PlayerPrefs.HasKey(MostRecentMeeting + "Session")) return null;
            string mode = PlayerPrefs.GetString(MostRecentMeeting + "Mode");
            if (mode == "live") return MostRecentMeeting;
            if (mode.Length > 0) return null;

            // Migrate pre-mode shortcuts only after verifying the server's actual response.
            var old = PlayerPrefs.GetString(MostRecentMeeting + "Address");
            return Uri.TryCreate(old, UriKind.Absolute, out var address) &&
                Uri.TryCreate(RelayEvidence.LoadConfig().replayServerUrl, UriKind.Absolute, out var replay) &&
                address.GetLeftPart(UriPartial.Authority) != replay.GetLeftPart(UriPartial.Authority)
                ? MostRecentMeeting : null;
        }

        void RestoreLocalCandidateReview()
        {
            if (SpatialAssembly && AssemblyActive && MeetingLatestRound != null &&
                TextValue(meetingSnapshot, "provider_mode") == "live" && TextValue(meetingSnapshot?["meeting"], "input_mode") == "manual")
            {
                StartLocalCandidatePreview();
                PresentReadyProposal(MeetingLatestRound);
            }
        }

        void ResumeMeetingSession() => ResumeMeetingShortcut(MostRecentMeeting);

        void ResumeLiveMeeting()
        {
            var shortcut = LiveResumeShortcut();
            if (shortcut == null)
            {
                openSavedDesignAfterResume = false;
                notice = "최근 실시간 회의가 없습니다. 진행자의 참가 코드를 입력하세요.";
                Draw();
                return;
            }
            ResumeMeetingShortcut(shortcut, true);
        }

        void ResumeMeetingShortcut(string prefix, bool requireLive = false)
        {
            if (meetingBusy) return;
            StopJoinQr();
            MeetingTransport transport;
            try { transport = new MeetingTransport(PlayerPrefs.GetString(prefix + "Address")); }
            catch (ArgumentException error) { openSavedDesignAfterResume = false; notice = error.Message; Draw(); return; }
            transport.SessionId = PlayerPrefs.GetString(prefix + "Session");
            transport.Token = PlayerPrefs.GetString(prefix + "Token");
            meetingParticipant = PlayerPrefs.GetString(prefix + "Participant");
            meetingTransport = transport;
            meetingBusy = true;
            StartCoroutine(transport.Json(transport.SessionPath, null, (reply, error) =>
            {
                if (meetingTransport != transport) return;
                meetingBusy = false;
                if (reply == null || requireLive && TextValue(reply, "provider_mode") != "live")
                {
                    transport.Dispose();
                    meetingTransport = null;
                    openSavedDesignAfterResume = false;
                    notice = error ?? "실시간 회의 참가 코드를 입력하세요.";
                    Draw();
                    return;
                }
                SaveMeetingResume(transport, reply);
                meetingHttpAvailable = true;
                AcceptMeetingSnapshot(reply);
                RestoreLocalCandidateReview();
                transport.StartEvents(reply.Value<long?>("last_seq") ?? 0);
                StartCoroutine(SyncMeetingClock(transport));
                TryOpenRequestedSavedDesign();
                Draw();
            }));
        }

        void ContinueSavedMeeting() => OpenSavedFollowUp(false);
        void ContinueSavedDesign() => OpenSavedFollowUp(true);

        void OpenSavedFollowUp(bool withDesign)
        {
            var previous = meetingTransport;
            if (previous == null || savedFollowUpPending || AssemblySessionStatus != "completed") return;
            var body = new JObject
            {
                ["display_name"] = "후속 작업", ["device_id"] = meetingDeviceId, ["capabilities"] = MeetingCapabilities()
            };
            if (withDesign)
            {
                if (meetingSnapshot["saved_design_utterance"] is not JObject) return;
                // Retain the request key across a lost HTTP response or app restart.
                if (PlayerPrefs.GetString("MeetingDesignParent") != previous.SessionId)
                {
                    PlayerPrefs.SetString("MeetingDesignParent", previous.SessionId);
                    PlayerPrefs.SetString("MeetingDesignRequest", Guid.NewGuid().ToString("N"));
                    PlayerPrefs.Save();
                }
                body["request_id"] = PlayerPrefs.GetString("MeetingDesignRequest");
            }
            savedFollowUpPending = true;
            assemblyFeedback = withDesign ? "저장된 발언으로 수정안을 준비합니다." : "후속 작업을 여는 중입니다.";
            Draw();
            var path = previous.SessionPath + (withDesign ? "/continue-design" : "/continue");
            StartCoroutine(previous.Json(path, body, (reply, error) =>
            {
                if (previous != meetingTransport) return;
                savedFollowUpPending = false;
                if (reply?["snapshot"] is not JObject snapshot || string.IsNullOrEmpty(TextValue(reply, "token")))
                {
                    assemblyFeedback = error ?? "후속 작업 정보를 받지 못했습니다. 다시 눌러 주세요.";
                    assemblyMenu = false;
                    Draw();
                    return;
                }
                var address = previous.Address.AbsoluteUri;
                var frame = CaptureConfirmedComparisonFrame();
                CloseMeeting();
                var next = new MeetingTransport(address) { SessionId = TextValue(reply, "session_id"), Token = TextValue(reply, "token") };
                meetingTransport = next;
                meetingParticipant = TextValue(reply, "participant_id");
                SaveMeetingResume(next, snapshot);
                meetingHttpAvailable = true;
                AcceptMeetingSnapshot(snapshot);
                if (SpatialAssembly && !RestoreConfirmedComparisonFrame(frame) && withDesign) StartLocalCandidatePreview();
                if (withDesign) PresentReadyProposal(MeetingLatestRound);
                next.StartEvents(snapshot.Value<long?>("last_seq") ?? 0);
                StartCoroutine(SyncMeetingClock(next));
                Draw();
            }));
        }

        void ReleaseMeetingCollaboration()
        {
            ReleaseExplicitDesign();
            StopJoinQr();
            foreach (var marker in participantPointers.Values) Destroy(marker);
            participantPointers.Clear();
            if (participantPointerMaterial != null) Destroy(participantPointerMaterial);
            selectedTargetVersion = selectedTargetPart = selectedTargetImage = seenDraftVersion = selectedSlider = null;
            targetSnapshotPending = designRequestPending = collaborativeEditing = previewSending = false;
            previewDue = double.PositiveInfinity;
            previewRevision = 0;
            currentEditLease = sharedComparisonSeen = null;
            targetSelectionRevision++;
        }
    }
}
