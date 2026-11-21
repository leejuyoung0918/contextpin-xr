using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        bool assemblyStarting, assemblyStopping, assemblyAdopting, assemblyResultDownload;
        string assemblyFeedback = "";
        MeetingPcmStream assemblyAudio;
        Task assemblyAudioSend;
        readonly List<byte[]> assemblyAudioTail = new List<byte[]>();
        double assemblyLastMicSample;
        int assemblyMicPosition;
        bool assemblyCaptureFrozen;

        bool SpatialAssembly => xr && IsAssemblyMeeting;
        string AssemblySessionStatus => TextValue(meetingSnapshot, "session_status") is string status && status.Length > 0 ? status : "ready";
        string AssemblyRecordingMode => TextValue(meetingSnapshot?["meeting"], "mode");
        string AssemblyResultUrl => TextValue(meetingSnapshot?["result"], "zip_url");
        string AssemblyAdoptedId => TextValue(meetingSnapshot, "adopted_version_id") is string id && id.Length > 0
            ? id : TextValue(meetingSnapshot, "committed_version_id");
        bool AssemblyActive => AssemblySessionStatus is "recording" or "paused";
        bool AssemblyCaptureRequested => (meetingSnapshot?["meeting"] as JObject)?.Value<bool?>("capture_requested") == true;

        IEnumerator RunAssemblyAction(MeetingTransport transport, string type, JObject payload, Action<JObject, string> completed)
        {
            AddMeetingClock(payload);
            var command = new JObject { ["command_id"] = Guid.NewGuid().ToString("N"), ["type"] = type, ["payload"] = payload };
            while (meetingTransport == transport)
            {
                JObject reply = null;
                string error = null;
                long status = 0;
                yield return transport.JsonWithStatus(transport.SessionPath + "/commands", command,
                    (response, issue, responseCode) => { reply = response; error = issue; status = responseCode; });
                if (meetingTransport != transport) yield break;
                if (reply != null || status is >= 400 and < 500 && status != 429)
                {
                    if (reply?["snapshot"] is JObject snapshot) AcceptMeetingSnapshot(snapshot);
                    completed(reply, error);
                    RefreshMeetingSnapshot();
                    Draw();
                    yield break;
                }
                assemblyFeedback = "회의 서버 응답을 다시 확인하고 있습니다.";
                Draw();
                yield return new WaitForSecondsRealtime(1.5f);
            }
        }

        void StartSpatialMeeting()
        {
            if (assemblyStarting || assemblyStopping || meetingTransport == null || assemblyAlignment != AssemblyAlignment.Confirmed) return;
            var mode = TextValue(meetingSnapshot, "provider_mode") == "live" ? "live" : "replay";
            var transport = meetingTransport;
            assemblyStarting = true;
            assemblyFeedback = mode == "live" ? "마이크를 연결하고 있습니다." : "예시 회의를 여는 중입니다.";
            Draw();
            StartCoroutine(RunAssemblyAction(transport, "meeting_start", new JObject { ["mode"] = mode, ["audio_device_id"] = meetingDeviceId },
                (_, error) =>
                {
                    if (error == null && mode == "live" && !assemblyStopping)
                    {
                        StartCoroutine(OpenSpatialMicrophone(transport));
                        return;
                    }
                    assemblyStarting = false;
                    assemblyFeedback = error ?? "";
                }));
        }

        IEnumerator OpenSpatialMicrophone(MeetingTransport transport)
        {
            if (meetingCapture == null) meetingCapture = gameObject.AddComponent<RelayEvidence>();
            assemblyCaptureFrozen = false;
            string status = "";
            yield return meetingCapture.StartMicrophone(value => status = value, 8);
            if (meetingTransport != transport || assemblyStopping || assemblyCaptureFrozen) yield break;
            if (!meetingCapture.Recording)
            {
                assemblyStarting = false;
                assemblyFeedback = string.IsNullOrEmpty(status) ? "마이크를 사용할 수 없습니다. PC에서 회의를 시작하세요." : status;
                Draw();
                yield break;
            }
            var clip = meetingCapture.RecordingClip;
            var until = Time.unscaledTimeAsDouble + 3;
            var position = Microphone.GetPosition(null);
            while (position <= 0 && Time.unscaledTimeAsDouble < until && meetingTransport == transport && !assemblyCaptureFrozen)
            {
                yield return null;
                position = Microphone.GetPosition(null);
            }
            if (meetingTransport != transport || assemblyCaptureFrozen) yield break;
            if (position <= 0)
            {
                AssemblyAudioFailed("마이크에서 샘플을 받지 못했습니다. PC에서 회의를 시작할 수 있어요.");
                yield break;
            }
            var stream = new MeetingPcmStream(clip);
            assemblyAudio = stream;
            assemblyAudioTail.Clear();
            assemblyAudioSend = null;
            assemblyLastMicSample = Time.unscaledTimeAsDouble;
            assemblyMicPosition = -1;
            var id = Guid.NewGuid().ToString("N");
            var hello = new JObject
            {
                ["token"] = transport.Token, ["session_id"] = transport.SessionId, ["device_id"] = meetingDeviceId,
                ["stream_id"] = id, ["codec"] = "pcm_s16le", ["channels"] = 1, ["actual_sample_rate_hz"] = clip.frequency,
                ["stream_start_source_monotonic_ms"] = Time.realtimeSinceStartupAsDouble * 1000 - position * 1000.0 / clip.frequency,
                ["stream_start_sample_index"] = 0, ["bind_view"] = false
            };
            if (meetingClockValid)
            {
                hello["clock_offset_ms"] = meetingClockOffsetMs;
                hello["clock_uncertainty_ms"] = meetingClockUncertaintyMs;
            }
            var opening = stream.Open(transport.SocketEndpoint("/v1/audio-streams/" + id), hello);
            while (!opening.IsCompleted) yield return null;
            if (meetingTransport != transport || stream != assemblyAudio || assemblyCaptureFrozen) yield break;
            assemblyStarting = false;
            if (opening.IsCanceled || opening.IsFaulted)
            {
                AssemblyAudioFailed(opening.Exception?.GetBaseException().Message ?? "마이크 연결이 완료되지 않았습니다.");
                yield break;
            }
            assemblyFeedback = "";
            Draw();
        }

        void UpdateSpatialMicrophone()
        {
            if (assemblyAudio == null || assemblyStarting || assemblyCaptureFrozen) return;
            if (assemblyAudioSend?.IsFaulted == true || assemblyAudioSend?.IsCanceled == true)
            {
                AssemblyAudioFailed("마이크 연결이 끊겼습니다. 기존 전사는 보존됩니다.");
                return;
            }
            var now = Time.unscaledTimeAsDouble;
            var position = Microphone.GetPosition(null);
            if (!Microphone.IsRecording(null) || now - assemblyLastMicSample > 6)
            {
                AssemblyAudioFailed("마이크 샘플에 긴 공백이 생겼습니다. 확보한 전사는 보존됩니다.");
                return;
            }
            if (position != assemblyMicPosition)
            {
                assemblyMicPosition = position;
                assemblyLastMicSample = now;
            }
            try
            {
                if (!assemblyAudio.ObservePosition(position)) assemblyFeedback = "일부 오디오 구간을 받지 못했습니다.";
                if (assemblyAudioSend == null || assemblyAudioSend.IsCompleted)
                {
                    var packet = assemblyAudio.ReadPacket();
                    if (packet != null) assemblyAudioSend = assemblyAudio.Send(packet);
                }
            }
            catch (InvalidOperationException error) { AssemblyAudioFailed(error.Message); }
        }

        void FreezeSpatialMicrophone()
        {
            if (assemblyCaptureFrozen) return;
            assemblyCaptureFrozen = true;
            if (assemblyAudio != null && !assemblyStarting)
            {
                try
                {
                    assemblyAudio.ObservePosition(Microphone.GetPosition(null));
                    while (assemblyAudio.ReadPacket(flush: true) is byte[] packet) assemblyAudioTail.Add(packet);
                }
                catch (InvalidOperationException error) { assemblyFeedback = error.Message; }
            }
            else if (assemblyStarting) ReleaseSpatialAudio();
            meetingCapture?.StopMicrophone();
            assemblyStarting = false;
        }

        IEnumerator CloseSpatialStream()
        {
            var stream = assemblyAudio;
            if (stream == null) yield break;
            var close = stream.Finish(assemblyAudioSend, assemblyAudioTail.ToArray());
            while (!close.IsCompleted) yield return null;
            if (stream != assemblyAudio) yield break;
            if (close.IsFaulted || close.IsCanceled)
                assemblyFeedback = "마지막 오디오 전송을 확인하지 못했습니다. 서버의 결과 상태를 확인하세요.";
            ReleaseSpatialAudio();
            if (SpatialAssembly) Draw();
        }

        void ReleaseSpatialAudio()
        {
            assemblyAudio?.Dispose();
            assemblyAudio = null;
            assemblyAudioSend = null;
            assemblyAudioTail.Clear();
        }

        void AssemblyAudioFailed(string error)
        {
            meetingCapture?.StopMicrophone();
            ReleaseSpatialAudio();
            assemblyStarting = false;
            assemblyCaptureFrozen = true;
            assemblyFeedback = error;
            Draw();
        }

        void EndSpatialMeeting(bool pause = false)
        {
            if (meetingTransport == null || assemblyStopping || (!AssemblyActive && !assemblyStarting && !AssemblyCaptureRequested)) return;
            FreezeSpatialMicrophone();
            assemblyStopping = true;
            assemblyFeedback = "";
            Draw();
            StartCoroutine(FinalizeSpatialMeeting(meetingTransport, pause));
        }

        IEnumerator FinalizeSpatialMeeting(MeetingTransport transport, bool pause)
        {
            while (assemblyAdopting && meetingTransport == transport) yield return null;
            if (meetingTransport != transport) yield break;
            yield return RunAssemblyAction(transport, pause ? "meeting_pause" : "meeting_end", new JObject(), (_, error) =>
            {
                if (error != null) assemblyFeedback = error;
            });
            if (meetingTransport != transport) yield break;
            yield return CloseSpatialStream();
            assemblyStopping = false;
            Draw();
        }

        void SpatialSnapshotUpdated()
        {
            if (!SpatialAssembly) return;
            if (assemblyAdoptedTakesEffect == AssemblyAdoptedId) assemblyAdoptedTakesEffect = null;
            if (AssemblySessionStatus is "finalizing" or "completed" or "save_error" && assemblyAudio != null && !assemblyStopping && !assemblyCaptureFrozen)
            {
                FreezeSpatialMicrophone();
                StartCoroutine(CloseSpatialStream());
            }
            if (AssemblySessionStatus is "finalizing" or "completed" or "save_error")
            {
                assemblyCompare = false;
                meetingViewedVersion = null;
            }
        }

        void AdoptSpatialCandidate(JToken round, JToken candidate)
        {
            if (assemblyAdopting || assemblyStopping || assemblyAlignment != AssemblyAlignment.Confirmed ||
                TextValue(candidate, "version_id") != meetingLoadedVersion || !assemblyCompare) return;
            assemblyAdopting = true;
            assemblyFeedback = "";
            var payload = new JObject
            {
                ["round_id"] = TextValue(round, "round_id"), ["revision"] = round.Value<long?>("revision") ?? 0,
                ["candidate_id"] = TextValue(candidate, "candidate_id"), ["outcome"] = "commit",
                ["expected_committed_version_id"] = AssemblyAdoptedId,
                ["expected_adopted_revision"] = meetingSnapshot.Value<long?>("adopted_revision") ?? 0
            };
            StartCoroutine(RunAssemblyAction(meetingTransport, "decide", payload, (reply, error) =>
            {
                assemblyAdopting = false;
                assemblyFeedback = error ?? "";
                if (reply == null) return;
                assemblyCompare = false;
                assemblyAdoptedTakesEffect = AssemblyAdoptedId == TextValue(candidate, "version_id") ? null : TextValue(candidate, "version_id");
                SetMeetingViewedVersion(null);
            }));
            Draw();
        }

        void SaveSpatialResult()
        {
            var path = AssemblyResultUrl;
            if (assemblyResultDownload || string.IsNullOrEmpty(path)) return;
            var transport = meetingTransport;
            var revision = meetingSnapshot?["result"]?.Value<int?>("revision") ?? 1;
            assemblyResultDownload = true;
            assemblyFeedback = "회의 결과를 받는 중입니다.";
            Draw();
            StartCoroutine(transport.Download(path, 96 * 1024 * 1024, (data, error) =>
            {
                if (meetingTransport != transport) return;
                assemblyResultDownload = false;
                try
                {
                    var folder = Path.Combine(Application.persistentDataPath, "MeetingExports");
                    Directory.CreateDirectory(folder);
                    var file = Path.Combine(folder, transport.SessionId + "-r" + revision + ".zip");
                    if (data == null) assemblyFeedback = error ?? "회의 결과를 받지 못했습니다.";
                    else { File.WriteAllBytes(file, data); assemblyFeedback = "회의 결과 ZIP을 기기에 저장했습니다."; }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { assemblyFeedback = "파일 저장 실패 · " + ex.Message; }
                Draw();
            }));
        }
    }
}
