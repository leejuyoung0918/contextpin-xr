using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        MeetingTransport meetingTransport;
        JObject meetingSnapshot;
        string meetingJoinCode = "", meetingDeviceId, meetingParticipant, meetingViewedVersion;
        string meetingSelectedParameter;
        double meetingParameterValue;
        bool meetingBusy, meetingRefresh, meetingEventsConnected, meetingHttpAvailable;
        float meetingRefreshAt;
        MeetingPage meetingPage;
        int meetingParameterPage, meetingDecisionPage;
        enum MeetingPage { Model, Candidates, Minutes }

        static IEnumerable<JToken> Values(JToken source, string name) => (source as JObject)?[name] as JArray ?? new JArray();
        static string TextValue(JToken value, string key) => (value as JObject)?.Value<string>(key) ?? "";
        JObject MeetingVersion(string id) => Values(meetingSnapshot, "versions").OfType<JObject>().FirstOrDefault(v => TextValue(v, "version_id") == id);
        JObject MeetingLatestRound => Values(meetingSnapshot, "rounds").OfType<JObject>().LastOrDefault();
        JObject RequestedMeetingVersion()
        {
            if (SpatialAssembly && (ExplicitDesignMeeting || A4PointSampling || A4PreviewActive))
                return MeetingVersion(AssemblyAdoptedId);
            if (IsAssemblyMeeting && (AssemblySampling || AssemblyQrScanning || assemblyAlignment == AssemblyAlignment.Preview))
                return MeetingVersion(AssemblyOriginalId);
            if (SpatialAssembly) return MeetingVersion(AssemblyAdoptedId);
            return MeetingVersion(meetingViewedVersion ?? TextValue(meetingSnapshot, "committed_version_id"));
        }
        JToken MeetingCandidate(string version) => Values(MeetingLatestRound, "candidates").LastOrDefault(c => TextValue(c, "version_id") == version);
        string MeetingVersionLabel(string value) => string.IsNullOrEmpty(value) ? "" : "v·" + value.Substring(Math.Max(0, value.Length - 7));
        string MeetingPartLabel(string id) => Values(meetingSnapshot?["profile"], "semantic_parts")
            .Where(part => TextValue(part, "part_id") == id).Select(part => TextValue(part, "label")).FirstOrDefault() ?? id;

        void EnterMeeting()
        {
            CloseMeeting();
            meetingServerOverride = null;
            notice = "";
            meetingPage = MeetingPage.Model;
            meetingLocalPlacement = meetingRealSize = false;
            meetingYaw = -28;
            meetingHeight = 0;
            meetingParameterPage = meetingDecisionPage = 0;
            meetingJoinCode = "";
            meetingDeviceId = PlayerPrefs.GetString("ContextPinMeetingDevice", "");
            if (string.IsNullOrEmpty(meetingDeviceId))
            {
                meetingDeviceId = "unity-" + Guid.NewGuid().ToString("N");
                PlayerPrefs.SetString("ContextPinMeetingDevice", meetingDeviceId);
            }
            Draw();
        }

        string MeetingAddress()
        {
#if UNITY_EDITOR
            var address = Environment.GetEnvironmentVariable("MEETING_EDGE_URL");
            if (!string.IsNullOrWhiteSpace(address)) return address;
#endif
            return meetingServerOverride ?? RelayEvidence.LoadConfig().meetingServerUrl ?? RelayConfig.DefaultMeetingServerUrl;
        }

        void BeginExampleMeeting()
        {
            meetingServerOverride = RelayEvidence.LoadConfig().replayServerUrl;
            BeginMeetingSession(false);
        }

        void BeginMeetingSession(bool join, bool startOnCreate = false)
        {
            if (meetingBusy || (join && meetingJoinCode.Length != 6)) return;
            StopJoinQr();
            try { meetingTransport = new MeetingTransport(MeetingAddress()); }
            catch (ArgumentException error) { notice = error.Message; Draw(); return; }
            var transport = meetingTransport;
            var body = new JObject
            {
                ["display_name"] = (xr ? "Quest " : "Unity ") + meetingDeviceId.Substring(meetingDeviceId.Length - 4),
                ["device_id"] = meetingDeviceId,
                ["capabilities"] = MeetingCapabilities(),
            };
            if (join) body["join_code"] = meetingJoinCode;
            meetingBusy = true;
            notice = join ? "회의에 연결 중입니다." : "새 회의와 초기 모델을 준비합니다.";
            Draw();
            StartCoroutine(transport.Json(join ? "/v1/join" : "/v1/sessions", body, (reply, error) =>
            {
                if (meetingTransport != transport) return;
                meetingBusy = false;
                var snapshot = reply?["snapshot"] as JObject;
                if (snapshot == null)
                {
                    notice = error ?? "초기 회의 상태를 받지 못했습니다.";
                    meetingTransport.Dispose();
                    meetingTransport = null;
                    meetingServerOverride = null;
                    Draw();
                    return;
                }
                transport.SessionId = TextValue(reply, "session_id");
                transport.Token = TextValue(reply, "token");
                meetingParticipant = TextValue(reply, "participant_id");
                SaveMeetingResume(transport, snapshot);
                meetingHttpAvailable = true;
                notice = "회의에 연결했습니다.";
                AcceptMeetingSnapshot(snapshot);
                if (join) RestoreLocalCandidateReview();
                transport.StartEvents(snapshot.Value<long?>("last_seq") ?? 0);
                StartCoroutine(SyncMeetingClock(transport));
                meetingRefreshAt = Time.unscaledTime + 2;
                Draw();
                if (!join && startOnCreate && SpatialAssembly && TextValue(snapshot, "provider_mode") == "live")
                {
                    if (PreparingLivePaperMeeting)
                    {
#if UNITY_ANDROID && !UNITY_EDITOR
                        // Android's permission dialog suspends tracking. Ask before the first
                        // paper point; only the subsequent meeting-start press opens the mic.
                        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
                            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
#endif
                        BeginA4Alignment();
                    }
                    else StartSpatialMeetingWithMode(true);
                }
            }));
        }

        void UpdateMeeting()
        {
            var transport = meetingTransport;
            if (meetingSnapshot == null)
            {
                UpdateJoinQr();
                if (!xr && !meetingBusy && Keyboard.current != null) ReadMeetingCodeKeys();
                return;
            }
            if (transport == null) return;
            bool connected = transport.EventsConnected;
            bool changed = false;
            while (transport.Snapshots.TryDequeue(out var snapshot)) changed |= AcceptMeetingSnapshot(snapshot);
            if (connected != meetingEventsConnected) { meetingEventsConnected = connected; changed = true; }
            if (changed) Draw();
            if (!connected && !meetingRefresh && !meetingBusy && Time.unscaledTime >= meetingRefreshAt) RefreshMeetingSnapshot();
            if (SpatialAssembly) UpdateSpatialMicrophone();
            else if (meetingCapture != null && meetingCapture.Recording && Time.unscaledTime >= meetingAutoSendAt) SendMeetingAudio();
            if (SpatialAssembly && ExplicitDesignMeeting && AssemblyActive && !assemblyStarting && !assemblyPrepOpened)
            {
                assemblyPrepOpened = true;
                if (assemblyAlignment == AssemblyAlignment.Unaligned) BeginA4Alignment();
                if (assemblyAlignment == AssemblyAlignment.Confirmed) PrepareExplicitCamera();
            }
            UpdateAssemblyTracking();
            UpdateSpatialPanelPosition();
            PositionMeetingModel();
            UpdatePaperSync();
            UpdateMeetingCollaboration();
        }

        void ReadMeetingCodeKeys()
        {
            var keys = Keyboard.current;
            if (keys.digit0Key.wasPressedThisFrame) AddMeetingDigit("0");
            if (keys.digit1Key.wasPressedThisFrame) AddMeetingDigit("1");
            if (keys.digit2Key.wasPressedThisFrame) AddMeetingDigit("2");
            if (keys.digit3Key.wasPressedThisFrame) AddMeetingDigit("3");
            if (keys.digit4Key.wasPressedThisFrame) AddMeetingDigit("4");
            if (keys.digit5Key.wasPressedThisFrame) AddMeetingDigit("5");
            if (keys.digit6Key.wasPressedThisFrame) AddMeetingDigit("6");
            if (keys.digit7Key.wasPressedThisFrame) AddMeetingDigit("7");
            if (keys.digit8Key.wasPressedThisFrame) AddMeetingDigit("8");
            if (keys.digit9Key.wasPressedThisFrame) AddMeetingDigit("9");
            if (keys.backspaceKey.wasPressedThisFrame) RemoveMeetingDigit();
            if (keys.enterKey.wasPressedThisFrame && meetingJoinCode.Length == 6) BeginMeetingSession(true);
        }

        void RefreshMeetingSnapshot()
        {
            var transport = meetingTransport;
            if (transport == null || meetingRefresh) return;
            meetingRefresh = true;
            meetingRefreshAt = Time.unscaledTime + 2.5f;
            StartCoroutine(transport.Json(transport.SessionPath, null, (reply, error) =>
            {
                if (meetingTransport != transport) return;
                meetingRefresh = false;
                if (reply != null)
                {
                    bool becameAvailable = !meetingHttpAvailable;
                    meetingHttpAvailable = true;
                    if (becameAvailable) notice = "서버에서 회의 상태를 다시 받았습니다.";
                    if (AcceptMeetingSnapshot(reply["snapshot"] as JObject ?? reply) || becameAvailable) Draw();
                }
                else if (meetingFailureDisplayed != error)
                {
                    meetingHttpAvailable = false;
                    meetingFailureDisplayed = error;
                    notice = "서버 연결 대기 · 마지막으로 받은 모델을 표시합니다.";
                    Draw();
                }
            }));
        }
        string meetingFailureDisplayed;

        bool AcceptMeetingSnapshot(JObject snapshot)
        {
            long seq = snapshot.Value<long?>("last_seq") ?? 0;
            if (meetingSnapshot != null && seq <= (meetingSnapshot.Value<long?>("last_seq") ?? 0)) return false;
            bool ongoingSession = meetingSnapshot != null;
            var previousRound = MeetingLatestRound;
            var oldCommitted = TextValue(meetingSnapshot, "committed_version_id");
            meetingSnapshot = snapshot;
            if (IsAssemblyMeeting) ReadAssemblyMount();
            ReceivePaperState();
            SpatialSnapshotUpdated();
            ReceiveSharedComparison();
            ReceiveCollaborativeDraft();
            if (oldCommitted != TextValue(meetingSnapshot, "committed_version_id"))
            {
                if (oldCommitted.Length > 0 && IsAssemblyMeeting)
                {
                    targetSelectionRevision++;
                    selectedTargetVersion = selectedTargetPart = selectedTargetImage = null;
                    targetSnapshotPending = false;
                }
                if (SpatialAssembly)
                {
                    assemblyCompare = assemblyClarify = false;
                    assemblyCompareRound = null;
                }
                SetMeetingViewedVersion(null);
            }
            if (meetingFailureDisplayed != null)
            {
                meetingFailureDisplayed = null;
                notice = "회의 상태를 다시 받았습니다.";
            }
            QueueMeetingModel(RequestedMeetingVersion());
            // A new server result opens review. Alignment changes and unrelated snapshots never open it.
            var round = MeetingLatestRound;
            if (ongoingSession && round != null && (TextValue(previousRound, "round_id") != TextValue(round, "round_id") ||
                TextValue(previousRound, "status") != TextValue(round, "status")))
                PresentReadyProposal(round);
            return true;
        }

        void SendMeetingCommand(string type, JObject payload, bool foreground = true, Action completed = null)
        {
            var transport = meetingTransport;
            if (transport == null || (foreground && meetingBusy)) return;
            AddMeetingClock(payload);
            var command = new JObject
            {
                ["command_id"] = Guid.NewGuid().ToString("N"),
                ["type"] = type,
                ["payload"] = payload,
            };
            if (foreground) { meetingBusy = true; notice = "변경 사항을 보내는 중입니다."; Draw(); }
            StartCoroutine(transport.Json(transport.SessionPath + "/commands", command, (reply, error) =>
            {
                if (meetingTransport != transport) return;
                if (foreground) meetingBusy = false;
                if (error != null)
                {
                    if (type == "audio_host") meetingRequestingMicrophone = false;
                    notice = error; Draw(); RefreshMeetingSnapshot(); return;
                }
                if (reply?["snapshot"] is JObject snapshot) AcceptMeetingSnapshot(snapshot);
                if (foreground) notice = "회의에 반영했습니다.";
                completed?.Invoke();
                if (!transport.EventsConnected && foreground) RefreshMeetingSnapshot();
                if (foreground || reply?["snapshot"] != null) Draw();
            }));
        }

        void ReportMeetingView()
        {
            var comparison = RequestedSpatialComparison();
            var shown = comparison != null ? SpatialComparisonLoadedVersion : meetingLoadedVersion;
            var requested = comparison ?? RequestedMeetingVersion();
            if (string.IsNullOrEmpty(shown) || shown != TextValue(requested, "version_id")) return;
            if (comparison != null && !SpatialComparisonModelVisible) return;
            if (comparison == null && (A4PointSampling || A4PoseApplied) &&
                (meetingModel == null || !meetingModel.activeInHierarchy)) return;
            var nonSpatialCandidate = SpatialAssembly ? null : MeetingCandidate(shown);
            var round = comparison != null ? SpatialRound() : !SpatialAssembly &&
                (meetingPage == MeetingPage.Candidates || nonSpatialCandidate != null) ? MeetingLatestRound : null;
            var candidate = Values(round, "candidates").LastOrDefault(c => TextValue(c, "version_id") == shown);
            var displayed = Values(round, "candidates").Where(c => !string.IsNullOrEmpty(TextValue(c, "version_id")) &&
                    (!UsingLocalCandidatePreview || IsLocalCandidateVisible(TextValue(c, "version_id"))))
                .Select(c => TextValue(c, "candidate_id"));
            SendMeetingCommand("view", new JObject
            {
                ["version_id"] = shown,
                ["part_ids"] = new JArray(meetingPartId == null ? Array.Empty<string>() : new[] { meetingPartId }),
                ["round_id"] = round == null ? null : TextValue(round, "round_id"),
                ["candidate_id"] = candidate == null ? null : TextValue(candidate, "candidate_id"),
                ["display_order_candidate_ids"] = new JArray(displayed),
                ["tracking_state"] = UsingLocalCandidatePreview ? "local_preview" : null,
                ["share"] = (comparison != null && SpatialViewingShared) || SpatialSharesCommitted,
            }, false);
        }

        void OpenMeetingPage(MeetingPage page)
        {
            meetingPage = page;
            ReportMeetingView();
            Draw();
        }

        bool MeetingHost => TextValue(meetingSnapshot, "host_id") == meetingParticipant;

        void SetMeetingViewedVersion(string version)
        {
            var prior = TextValue(RequestedMeetingVersion(), "version_id");
            meetingViewedVersion = version;
            var next = RequestedMeetingVersion();
            if (prior != TextValue(next, "version_id") || version == null)
            {
                meetingPartId = null;
                meetingSelectedParameter = null;
                ShowMeetingSelection();
            }
            QueueMeetingModel(next);
            if (meetingLoadedVersion == TextValue(next, "version_id")) ReportMeetingView();
        }

        void SaveMeetingParameter()
        {
            var version = RequestedMeetingVersion();
            if (version == null || string.IsNullOrEmpty(meetingSelectedParameter) || meetingBusy) return;
            SendMeetingCommand("edit", new JObject
            {
                ["base_version_id"] = TextValue(version, "version_id"),
                ["parameters"] = new JObject { [meetingSelectedParameter] = meetingParameterValue },
            }, completed: () => meetingPage = MeetingPage.Candidates);
        }

        void SaveMeetingResponse(string kind, string choice, JToken candidate)
        {
            var round = MeetingLatestRound;
            if (round == null) return;
            SendMeetingCommand("response", new JObject
            {
                ["round_id"] = TextValue(round, "round_id"),
                ["kind"] = kind,
                ["choice"] = choice,
                ["candidate_ids"] = candidate == null ? new JArray() : new JArray(TextValue(candidate, "candidate_id")),
            });
        }

        void ConfirmMeetingCandidate(JToken candidate)
        {
            var round = MeetingLatestRound;
            if (round == null) return;
            SendMeetingCommand("ack", new JObject
            {
                ["round_id"] = TextValue(round, "round_id"),
                ["candidate_id"] = TextValue(candidate, "candidate_id"),
            });
        }

        void SaveMeetingDecision(string outcome, JToken candidate)
        {
            var round = MeetingLatestRound;
            if (round == null) return;
            SendMeetingCommand("decide", new JObject
            {
                ["round_id"] = TextValue(round, "round_id"),
                ["revision"] = round.Value<long?>("revision") ?? 0,
                ["candidate_id"] = candidate == null ? null : TextValue(candidate, "candidate_id"),
                ["outcome"] = outcome,
                ["expected_committed_version_id"] = TextValue(meetingSnapshot, "committed_version_id"),
            }, completed: () => { meetingPage = MeetingPage.Minutes; SetMeetingViewedVersion(null); });
        }

        void SaveMeetingExport(bool package)
        {
            if (meetingBusy || meetingTransport == null) return;
            var version = RequestedMeetingVersion();
            var path = TextValue(version, package ? "export_url" : "stl_url");
            if (string.IsNullOrEmpty(path) && !package && version?["assets"]?["print.stl"]?.Value<string>("asset_id") is string assetId)
                path = "/v1/assets/" + Uri.EscapeDataString(assetId);
            if (string.IsNullOrEmpty(path)) { notice = "아직 이 버전을 내보낼 수 없습니다."; Draw(); return; }
            var id = TextValue(version, "version_id");
            var transport = meetingTransport;
            meetingBusy = true;
            notice = "선택한 모델 버전의 파일을 받는 중입니다.";
            Draw();
            StartCoroutine(transport.Download(path, 32 * 1024 * 1024, (data, error) =>
            {
                if (meetingTransport != transport) return;
                meetingBusy = false;
                if (data == null) notice = "내보내기 실패 · " + error;
                else
                {
                    try
                    {
                        var folder = Path.Combine(Application.persistentDataPath, "MeetingExports");
                        Directory.CreateDirectory(folder);
                        var safeId = new string(id.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').Take(80).ToArray());
                        var file = Path.Combine(folder, safeId + (package ? ".zip" : ".stl"));
                        File.WriteAllBytes(file, data);
                        notice = "내보냈습니다 · " + file;
                        Debug.Log("ContextPin meeting export " + file);
                    }
                    catch (Exception failure) when (failure is IOException || failure is UnauthorizedAccessException)
                    { notice = "파일 저장 실패 · " + failure.Message; }
                }
                Draw();
            }));
        }

        void CloseMeeting()
        {
            ResetPaperState();
            ReleaseMeetingCollaboration();
            ReleaseMeetingCapture();
            var transport = meetingTransport;
            meetingTransport = null;
            transport?.Dispose();
            ReleaseMeetingPreview();
            ResetAssemblyPreview();
            ResetSpatialAssembly();
            meetingSnapshot = null;
            meetingViewedVersion = meetingSelectedParameter = meetingFailureDisplayed = null;
            meetingBusy = meetingRefresh = meetingEventsConnected = meetingHttpAvailable = false;
        }

        void LeaveMeeting() { EnterMeeting(); Recenter(); }
    }
}
