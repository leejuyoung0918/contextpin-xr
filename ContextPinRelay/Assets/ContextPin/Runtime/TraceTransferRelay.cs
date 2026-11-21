using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace ContextPin
{
    [Serializable]
    public sealed class TransferCommandReceipt
    {
        public string commandId, status, detail;
    }

    [Serializable]
    public sealed class TransferHelp
    {
        public string stepId, question, sourceQuote, captureId, attempted, createdAt, boardInstanceId;
        public int registrationRevision, trackingEpoch;
        public string[] selectedTerminals;
    }

    public sealed partial class RelayApp
    {
        [Serializable] sealed class TransferPairing
        {
            public string sessionId, sourceMode, deviceToken, pairingCode, deviceLabel;
            public bool consent;
        }

        [Serializable] sealed class TransferSessionRequest { public string sessionId; }

        [Serializable] sealed class TransferCapability
        {
            public string device, camera, microphone, speech, vision;
        }

        [Serializable] sealed class RelayHealth
        {
            public bool speechConfigured, visionConfigured;
        }

        [Serializable] sealed class RelayNote
        {
            public string id, transcript, quote, audioSha256, sourceMode, author;
            public float time;
            public string[] photoSha256s;
        }

        [Serializable] sealed class RelayMetrics
        {
            public int shutterRequests, photoReadbacks, failedPhotos, savedPhotos, uploadedPhotos;
        }

        [Serializable] sealed class TransferState
        {
            public string sessionId, stage, approvalIssue;
            public int revision, registrationRevision;
            public LabGuide guide;
            public GuidePoint[] points;
            public RelayNote[] notes;
            public TransferCapture[] captures;
            public TransferHelp help;
            public TransferCapability capabilities;
            public BoardRegistration registration;
            public GuideRun[] runs;
            public RelayMetrics metrics;
        }

        [Serializable] sealed class TransferPayload
        {
            public string phase, text, sourceNoteId, kitConditions;
            public bool kitConfirmed;
            public GuideStep[] steps;
        }

        [Serializable] sealed class TransferCommand
        {
            public string id, kind;
            public int expectedRevision;
            public TransferPayload payload;
        }

        [Serializable] sealed class TransferCommands { public TransferCommand[] commands; }
        [Serializable] sealed class TransferAck { public string sessionId, commandId, status, detail; }
        [Serializable] sealed class TransferBlobPacket { public string sessionId, sha256, mime, base64; }

        sealed class RelayAsset
        {
            public string file, mime, hash;
        }

        Coroutine transferRoutine;
        bool guideStudentLibrary;
        TransferPairing transferPairing;
        RelayHealth transferHealth;
        RelayHealth diagnosticHealth;
        string diagnosticStatus;
        bool transferOnline;
        string transferLastState;
        int transferEpoch;
        readonly HashSet<string> uploadHashes = new HashSet<string>();

        string TransferPairingHint => transferOnline && transferPairing != null
            ? "교수 PC에서 127.0.0.1:8787/teacher · 코드 " + transferPairing.pairingCode
            : "교수 PC 연결 대기 · USB relay를 확인하세요.";
        string TransferSpeechHint => transferHealth?.speechConfigured == true ? "STT 설정됨" : "STT 미설정 · PC 수동 입력";
        bool CanShareGuide => TraceData?.IsGuide == true && TraceData.transferConsent && !guideIsExample;

        void BeginTransferDiagnostics()
        {
            diagnosticHealth = null;
            diagnosticStatus = "교수 PC 연결 확인 중";
            Go(ScreenState.Diagnostics);
            StartCoroutine(CheckTransferDiagnostics());
        }

        IEnumerator CheckTransferDiagnostics()
        {
            string url = RelayBaseUrl();
            if (!string.IsNullOrEmpty(url))
                yield return RelayJson<RelayHealth>(url + "/health", null, null, reply => diagnosticHealth = reply);
            if (screen != ScreenState.Diagnostics) yield break;
            diagnosticStatus = diagnosticHealth == null ? "교수 PC 연결을 확인하세요" : "교수 PC 연결됨";
            Draw();
        }

        void DrawTransferDiagnostics()
        {
            var device = capture.EvidenceDevice();
            string supported = xr && Meta.XR.PassthroughCameraAccess.IsSupported ? "API 지원 보고" : "현재 환경 카메라 경로 미지원";
            string permission = "권한은 실물 기기에서 확인";
#if UNITY_ANDROID && !UNITY_EDITOR
            permission = OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.PassthroughCameraAccess)
                ? "카메라 권한 있음" : "카메라 권한 대기";
#endif
            Heading("기기·연결 진단", "실제 프레임과 마이크 신호는 기록을 시작한 뒤 표시됩니다.");
            RoundBox("Transfer diagnostics", content, 36, 203, 1168, 334, Card, 14);
            var rows = new[]
            {
                "기기 · " + (xr && Application.platform == RuntimePlatform.Android ? OVRPlugin.GetSystemHeadsetType().ToString() : xr ? "시뮬레이터" : "데스크톱"),
                "카메라 · " + supported + " · " + permission + (device.CameraReady ? " · 프레임 수신" : " · 수신 대기"),
                "마이크 · " + (capture.Recording ? (capture.LastRms > .007f ? "신호 있음" : "신호 대기") : "기록 대기"),
                diagnosticStatus,
                "STT · " + (diagnosticHealth?.speechConfigured == true ? "설정됨" : "미설정") +
                    "     사진 비교 · " + (diagnosticHealth?.visionConfigured == true ? "설정됨" : "미설정"),
            };
            for (int index = 0; index < rows.Length; index++)
                Label("Diagnostic-" + index, content, 62, 221 + index * 57, 1105, 49, rows[index], 25, Light);
            Button("DiagnosticsBack", 36, 607, 200, 62, "처음으로", Home);
            Button("DiagnosticsRetry", 912, 607, 292, 62, "다시 확인", BeginTransferDiagnostics, true);
        }

        void StartTransferRelay()
        {
            StopTransferRelay();
            if (!CanShareGuide) return;
            transferRoutine = StartCoroutine(TransferLoop(TraceData, transferEpoch));
        }

        void StopTransferRelay()
        {
            transferEpoch++;
            if (transferRoutine != null) StopCoroutine(transferRoutine);
            transferRoutine = null;
            transferPairing = null;
            transferHealth = null;
            transferOnline = false;
            transferLastState = null;
            uploadHashes.Clear();
        }

        static string PairingCode(byte[] bytes)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            return new string(bytes.Select(value => alphabet[value % alphabet.Length]).ToArray());
        }

        TransferPairing ReadOrCreatePairing(TraceSession session)
        {
            string directory = Path.Combine(Application.persistentDataPath, "TransferPairings");
            string path = Path.Combine(directory, session.id + ".json");
            if (File.Exists(path))
            {
                var saved = JsonUtility.FromJson<TransferPairing>(File.ReadAllText(path));
                if (saved?.sessionId == session.id && !string.IsNullOrEmpty(saved.deviceToken)) return saved;
            }
            var secret = new byte[32];
            var pin = new byte[10];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(secret);
                random.GetBytes(pin);
            }
            var pairing = new TransferPairing
            {
                sessionId = session.id,
                sourceMode = session.transferMode,
                consent = true,
                deviceLabel = session.transferMode == "real" ? "Quest / 실제 기록" : "예시·시뮬레이터 입력",
                pairingCode = PairingCode(pin),
                deviceToken = Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            };
            Directory.CreateDirectory(directory);
            File.WriteAllText(path + ".new", JsonUtility.ToJson(pairing));
            if (File.Exists(path)) File.Replace(path + ".new", path, null);
            else File.Move(path + ".new", path);
            return pairing;
        }

        string RelayBaseUrl()
        {
            var raw = RelayEvidence.LoadConfig().serverUrl;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return "";
            if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) return "";
            return raw.TrimEnd('/');
        }

        IEnumerator TransferLoop(TraceSession session, int epoch)
        {
            yield return null;
            string baseUrl = RelayBaseUrl();
            if (string.IsNullOrEmpty(baseUrl))
            {
                notice = "교수 PC 주소에는 USB 루프백 또는 HTTPS 주소를 사용하세요.";
                RefreshStatus();
                yield break;
            }
            TransferPairing credentials;
            try { credentials = ReadOrCreatePairing(session); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
            {
                notice = "페어링 파일을 열 수 없어요.";
                RefreshStatus();
                yield break;
            }

            float backoff = 1;
            while (TraceData == session && epoch == transferEpoch)
            {
                if (transferPairing == null)
                {
                    TransferPairing paired = null;
                    yield return RelayJson<TransferPairing>(baseUrl + "/transfer/session", credentials, credentials.deviceToken, value => paired = value);
                    if (paired?.deviceToken == null)
                    {
                        SetTransferOnline(false);
                        yield return new WaitForSecondsRealtime(backoff);
                        backoff = Mathf.Min(backoff * 2, 10);
                        continue;
                    }
                    transferPairing = credentials;
                    yield return RelayJson<RelayHealth>(baseUrl + "/health", null, null, value => transferHealth = value);
                    SetTransferOnline(true);
                    backoff = 1;
                }

                string stage = RelayStage();
                // Record audio and photos locally first. A busy recorder only publishes controls and
                // counters; the review screen starts uploading the preserved evidence files.
                var state = BuildTransferState(session, stage, out var assets);
                foreach (var asset in assets)
                {
                    if (uploadHashes.Contains(asset.hash)) continue;
                    var encode = Task.Run(() => Convert.ToBase64String(File.ReadAllBytes(asset.file)));
                    while (!encode.IsCompleted && TraceData == session) yield return null;
                    if (encode.IsFaulted || !encode.IsCompletedSuccessfully) continue;
                    var blob = new TransferBlobPacket
                    {
                        sessionId = session.id, sha256 = asset.hash, mime = asset.mime, base64 = encode.Result,
                    };
                    bool uploaded = false;
                    yield return RelayJson<TransferSessionRequest>(baseUrl + "/transfer/blob", blob, credentials.deviceToken,
                        value => uploaded = value != null);
                    if (!uploaded) break;
                    uploadHashes.Add(asset.hash);
                }
                state.metrics.uploadedPhotos = state.captures.Count(item => uploadHashes.Contains(item.sha256));
                string comparison = JsonUtility.ToJson(state);
                if (comparison != transferLastState)
                {
                    state.revision = ++session.relayRevision;
                    Save();
                    bool sent = false;
                    yield return RelayJson<TransferSessionRequest>(baseUrl + "/transfer/state", state, credentials.deviceToken,
                        value => sent = value != null);
                    if (sent) transferLastState = comparison;
                    else
                    {
                        transferPairing = null;
                        SetTransferOnline(false);
                        yield return new WaitForSecondsRealtime(backoff);
                        backoff = Mathf.Min(backoff * 2, 10);
                        continue;
                    }
                }

                TransferCommands reply = null;
                yield return RelayJson<TransferCommands>(baseUrl + "/transfer/poll",
                    new TransferSessionRequest { sessionId = session.id }, credentials.deviceToken, value => reply = value);
                if (reply == null)
                {
                    transferPairing = null;
                    SetTransferOnline(false);
                    yield return new WaitForSecondsRealtime(backoff);
                    continue;
                }
                foreach (var command in reply.commands ?? Array.Empty<TransferCommand>())
                {
                    if (command == null || string.IsNullOrEmpty(command.id) || TraceData != session) continue;
                    var previous = session.commandReceipts.Find(receipt => receipt.commandId == command.id);
                    if (previous == null)
                    {
                        // Re-read the material draft before accepting a command from the PC.
                        string current = JsonUtility.ToJson(BuildTransferState(session, RelayStage(), out _));
                        bool reviewCommand = command.kind == "saveDraft" || command.kind == "approveDraft" || command.kind == "manualTranscript";
                        bool stale = reviewCommand && (command.expectedRevision != session.relayRevision || current != comparison);
                        previous = stale ? Reject(command, "기기의 기록이 바뀌었어요. PC 화면을 새로 고친 뒤 다시 적용하세요.") :
                            ApplyTeacherCommand(command);
                        session.commandReceipts.Add(previous);
                        Save();
                    }
                    yield return RelayJson<TransferSessionRequest>(baseUrl + "/transfer/ack", new TransferAck
                    {
                        sessionId = session.id, commandId = previous.commandId, status = previous.status, detail = previous.detail,
                    }, credentials.deviceToken, _ => { });
                }
                yield return new WaitForSecondsRealtime(1.1f);
            }
        }

        void SetTransferOnline(bool online)
        {
            bool changed = transferOnline != online;
            transferOnline = online;
            if (changed && (screen == ScreenState.Recording || screen == ScreenState.GuideReview || screen == ScreenState.GuidePractice))
                Draw();
        }

        string RelayStage()
        {
            if (screen == ScreenState.Align) return guideAlignment == GuideAlignment.Recording ? "registration" : "practice";
            if (screen == ScreenState.Recording || screen == ScreenState.Finishing) return "recording";
            if (screen == ScreenState.GuidePractice) return "practice";
            if (screen == ScreenState.GuideResult) return "complete";
            return ViewGuide?.approved == true ? "approved" : "review";
        }

        TransferState BuildTransferState(TraceSession session, string stage, out List<RelayAsset> assets)
        {
            bool includeNotes = stage != "registration" && stage != "recording";
            assets = new List<RelayAsset>();
            var notes = new List<RelayNote>();
            string folder = Path.Combine(TraceArchive.Folder(session), "evidence");
            var photos = session.captures.ToArray();
            foreach (var photo in photos)
            {
                if (photo.sha256 == null || string.IsNullOrEmpty(photo.path)) continue;
                string file = Path.Combine(folder, Path.GetFileName(photo.path));
                if (File.Exists(file)) assets.Add(new RelayAsset { file = file, hash = photo.sha256, mime = "image/jpeg" });
            }
            if (includeNotes)
            {
                foreach (var note in session.InOrder())
                {
                    if (string.IsNullOrWhiteSpace(note.quote) && string.IsNullOrEmpty(note.audioFile)) continue;
                    string hash = note.audioSha256;
                    if (!string.IsNullOrEmpty(hash) && !string.IsNullOrEmpty(note.audioFile))
                    {
                        string file = Path.Combine(folder, Path.GetFileName(note.audioFile));
                        if (File.Exists(file)) assets.Add(new RelayAsset { file = file, hash = hash, mime = "audio/wav" });
                    }
                    notes.Add(new RelayNote
                    {
                        id = note.id, transcript = note.transcript, quote = note.quote, audioSha256 = hash,
                        sourceMode = note.sourceMode, author = note.author, time = note.time,
                        photoSha256s = photos.Where(photo => (note.captureIds ?? Array.Empty<string>()).Contains(photo.captureId))
                            .Select(photo => photo.sha256).ToArray(),
                    });
                }
            }
            return new TransferState
            {
                sessionId = session.id,
                stage = stage,
                approvalIssue = stage == "review" && ViewGuide != null ? GuideValidationMessage : "",
                guide = includeNotes ? ViewGuide : null,
                notes = notes.ToArray(),
                points = GuideDisplayPoints.ToArray(),
                captures = photos,
                registration = ActiveBoardRegistration,
                registrationRevision = ActiveBoardRegistration?.revision ?? 0,
                capabilities = new TransferCapability
                {
                    device = session.transferMode,
                    camera = xr && capture.EvidenceDevice().CameraReady ? "frame_received" : xr ? "unverified" : "unavailable",
                    microphone = capture.MicrophoneState,
                    speech = transferHealth?.speechConfigured == true ? "configured" : "unconfigured",
                    vision = transferHealth?.visionConfigured == true ? "configured" : "unconfigured",
                },
                help = string.IsNullOrEmpty(session.recentHelp?.question) ? null : session.recentHelp,
                runs = session.guideRuns.ToArray(),
                metrics = new RelayMetrics
                {
                    shutterRequests = session.shutterRequests, photoReadbacks = session.photoReadbacks,
                    failedPhotos = session.failedPhotos, savedPhotos = photos.Length,
                    uploadedPhotos = photos.Count(item => uploadHashes.Contains(item.sha256)),
                },
            };
        }

        TransferCommandReceipt ApplyTeacherCommand(TransferCommand command)
        {
            string detail = "";
            switch (command.kind)
            {
                case "capture":
                    string phase = command.payload?.phase ?? "after_action";
                    if (!new[] {"setup", "before_action", "after_action", "clarification", "student_checkpoint"}.Contains(phase))
                        return Reject(command, "사진 목적이 올바르지 않아요.");
                    if (!RequestTransferPhoto(phase, "instructor", command.id))
                        return Reject(command, "기기 카메라나 촬영 상태를 확인하세요.");
                    detail = "기기가 촬영을 접수했어요. 실제 저장은 사진 목록에서 확인하세요.";
                    break;
                case "manualTranscript":
                    if (screen != ScreenState.GuideReview || ViewGuide?.approved != false || guideGenerating)
                        return Reject(command, "기기의 교수 검토 화면에서 전사를 입력할 수 있어요.");
                    detail = AddManualGuideSource(command.payload);
                    if (detail != "") return Reject(command, detail);
                    break;
                case "saveDraft":
                case "approveDraft":
                    if (screen != ScreenState.GuideReview || ViewGuide?.approved != false || guideGenerating)
                        return Reject(command, "현재 기기에서 편집할 초안이 없어요.");
                    detail = ApplyTeacherEdits(command.payload);
                    if (detail != "") return Reject(command, detail);
                    if (command.kind == "approveDraft")
                    {
                        ApproveGuide();
                        if (!ViewGuide.approved) return Reject(command, "승인 대기 · " + GuideValidationMessage);
                    }
                    break;
                default:
                    return Reject(command, "지원하지 않는 교수 요청입니다.");
            }
            return new TransferCommandReceipt { commandId = command.id, status = "applied", detail = detail };
        }

        static TransferCommandReceipt Reject(TransferCommand command, string detail) =>
            new TransferCommandReceipt { commandId = command.id, status = "rejected", detail = detail };

        IEnumerator RelayJson<T>(string url, object body, string token, Action<T> done, int timeout = 5) where T : class
        {
            using (var request = body == null ? UnityWebRequest.Get(url) : new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                if (body != null)
                {
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(body)));
                    request.SetRequestHeader("Content-Type", "application/json");
                }
                if (!string.IsNullOrEmpty(token)) request.SetRequestHeader("Authorization", "Bearer " + token);
                request.timeout = timeout;
                yield return request.SendWebRequest();
                T result = null;
                if (request.result == UnityWebRequest.Result.Success)
                {
                    try { result = JsonUtility.FromJson<T>(request.downloadHandler.text); }
                    catch (ArgumentException) { }
                }
                done(result);
            }
        }
    }
}
