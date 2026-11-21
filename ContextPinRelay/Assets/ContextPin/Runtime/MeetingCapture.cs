using System;
using System.Buffers.Binary;
using System.Collections;
using System.Globalization;
using Meta.XR;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        RelayEvidence meetingCapture;
        bool meetingRequestingMicrophone, meetingRequestingPhoto, meetingPhotoCaptured;
        double meetingClockOffsetMs, meetingClockUncertaintyMs;
        bool meetingClockValid;
        float meetingAutoSendAt;

        void DrawMeetingCaptureButton()
        {
            if (meetingCapture != null && meetingCapture.Recording)
                Button("MeetingSendAudio", 474, 614, 168, 49, "녹음 보내기", SendMeetingAudio, true);
            else if (!meetingRequestingMicrophone)
                Button("MeetingStartAudio", 474, 614, 168, 49, "맥락과 녹음", BeginMeetingAudio, true);
            if (xr && meetingLocalPlacement && PassthroughCameraAccess.IsSupported && !meetingRequestingPhoto)
                Button("MeetingPhoto", 459, 250, 171, 40, "참고 사진 추가", BeginMeetingPhoto);
        }

        JObject MeetingCapabilities()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            bool micAllowed = UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone);
#else
            bool micAllowed = Application.HasUserAuthorization(UserAuthorization.Microphone);
#endif
            var microphone = !micAllowed ? "denied" : Microphone.devices.Length > 0 ? "available" : "unsupported";
            var camera = !xr || !PassthroughCameraAccess.IsSupported ? "unsupported" : meetingPhotoCaptured ? "available" : "denied";
            var spatial = xr && XRSettings.isDeviceActive ? "available" : "unavailable";
            return new JObject
            {
                ["device_id"] = meetingDeviceId,
                ["platform"] = Application.platform.ToString(),
                ["app_version"] = Application.version,
                ["microphone"] = microphone,
                ["frame_capture"] = camera,
                ["spatial_tracking"] = spatial,
                ["world_anchoring"] = "unavailable",
                ["ray_selection"] = xr ? spatial : Mouse.current != null ? "available" : "unavailable",
                ["hand_tracking"] = Array.Exists(hands, hand => hand != null && hand.IsTracked) ? "available" : "unavailable",
                ["eye_tracking"] = "unavailable",
                ["runtime_glb_loading"] = "available",
                ["audio_sample_rates"] = meetingCapture != null && meetingCapture.RecordingClip != null
                    ? new JArray(meetingCapture.RecordingClip.frequency) : new JArray(),
                ["permission_notes"] = "마이크는 접근 권한을 확인합니다. 프레임은 실제 RGB 캡처에 성공한 뒤 사용 가능으로 표시합니다.",
            };
        }

        IEnumerator SyncMeetingClock(MeetingTransport transport)
        {
            double best = double.PositiveInfinity;
            for (int i = 0; i < 3 && meetingTransport == transport; i++)
            {
                var start = Time.realtimeSinceStartupAsDouble * 1000;
                yield return transport.Json(transport.SessionPath + "/clock", null, (result, _) =>
                {
                    var end = Time.realtimeSinceStartupAsDouble * 1000;
                    var uncertainty = (end - start) * .5;
                    if (uncertainty >= best || uncertainty > 500 || result?.Value<double?>("session_time_ms") is not double serverTime) return;
                    best = uncertainty;
                    meetingClockOffsetMs = serverTime - (start + end) * .5;
                    meetingClockUncertaintyMs = uncertainty;
                    meetingClockValid = true;
                });
            }
        }

        void AddMeetingClock(JObject payload)
        {
            if (!meetingClockValid) return;
            payload["source_monotonic_ms"] = Time.realtimeSinceStartupAsDouble * 1000;
            payload["clock_offset_ms"] = meetingClockOffsetMs;
            payload["clock_uncertainty_ms"] = meetingClockUncertaintyMs;
        }

        void BeginMeetingAudio()
        {
            if (meetingRequestingMicrophone || meetingBusy || meetingTransport == null) return;
            if (TextValue(meetingSnapshot, "provider_mode") != "live")
            {
                notice = "예시 모드입니다. AI 연결을 설정하거나 수정 후보에서 예시 발화를 사용하세요.";
                Draw();
                return;
            }
            meetingRequestingMicrophone = true;
            SendMeetingCommand("audio_host", new JObject(), completed: () => StartCoroutine(StartMeetingMicrophone(meetingTransport)));
        }

        IEnumerator StartMeetingMicrophone(MeetingTransport transport)
        {
            if (transport == null) yield break;
            yield return SyncMeetingClock(transport);
            if (meetingTransport != transport) yield break;
            if (meetingCapture == null) meetingCapture = gameObject.AddComponent<RelayEvidence>();
            yield return meetingCapture.StartMicrophone(status =>
            {
                if (meetingTransport != transport) return;
                meetingRequestingMicrophone = false;
                notice = meetingCapture.Recording ? "녹음 중 · 현재 모델·선택과 연결합니다. 최대 12초 뒤 전사를 요청합니다." : status;
                meetingAutoSendAt = Time.unscaledTime + 11.5f;
                SendMeetingCommand("capabilities", new JObject { ["capabilities"] = MeetingCapabilities() }, false);
                Draw();
            });
        }

        void SendMeetingAudio()
        {
            var transport = meetingTransport;
            if (transport == null || meetingCapture == null || !meetingCapture.Recording) return;
            var wave = meetingCapture.AudioSnapshot(mono: true);
            var capturedMs = Time.realtimeSinceStartupAsDouble * 1000;
            meetingCapture.StopMicrophone();
            if (wave == null || wave.Length < 44)
            {
                notice = "전사에 필요한 녹음 길이가 너무 짧습니다.";
                Draw();
                return;
            }
            var rateBytes = BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(28, 4));
            var duration = (wave.Length - 44) * 1000.0 / rateBytes;
            var path = transport.SessionPath + "/audio?bind_view=true";
            if (meetingClockValid) path += "&start_ms=" + ((long)(capturedMs + meetingClockOffsetMs - duration)).ToString(CultureInfo.InvariantCulture);
            notice = "녹음을 보냈습니다. 전사를 기다리는 중입니다.";
            Draw();
            StartCoroutine(transport.UploadWave(path, wave, (reply, error) =>
            {
                if (meetingTransport != transport) return;
                if (reply?["snapshot"] is JObject snapshot) AcceptMeetingSnapshot(snapshot);
                notice = error ?? "전사 요청이 접수되었습니다.";
                if (error == null) meetingPage = MeetingPage.Candidates;
                Draw();
            }));
        }

        void BeginMeetingPhoto()
        {
            if (meetingTransport == null || meetingRequestingPhoto || meetingModel == null) return;
            meetingRequestingPhoto = true;
            var version = meetingLoadedVersion;
            var part = meetingPartId;
            var center = meetingModel.transform.position;
            notice = "현재 선택한 모델에 참고 사진을 연결합니다.";
            Draw();
            StartCoroutine(CaptureMeetingPhoto(meetingTransport, version, part, center));
        }

        IEnumerator CaptureMeetingPhoto(MeetingTransport transport, string version, string part, Vector3 center)
        {
            if (meetingCapture == null) meetingCapture = gameObject.AddComponent<RelayEvidence>();
            string status = "";
            yield return meetingCapture.StartPassthroughCamera(value => status = value);
            float deadline = Time.unscaledTime + 2;
            while (meetingTransport == transport && meetingCapture.CameraReady &&
                !meetingCapture.CameraSource.IsUpdatedThisFrame && Time.unscaledTime < deadline)
                yield return null;
            if (meetingTransport != transport) yield break;
            byte[] jpeg = null;
            var timestamp = new JObject();
            try
            {
                if (meetingCapture.CameraReady && meetingCapture.CameraSource.IsUpdatedThisFrame)
                {
                    // Wait for a real RGB frame from the camera; the passthrough layer alone is not evidence.
                    AddMeetingClock(timestamp);
                    jpeg = meetingCapture.Snapshot(view, center, true, out _);
                    meetingPhotoCaptured = jpeg != null && jpeg.Length > 0;
                }
            }
            catch (InvalidOperationException error) { status = error.Message; }
            finally { meetingCapture.StopCamera(); }
            SendMeetingCommand("capabilities", new JObject { ["capabilities"] = MeetingCapabilities() }, false);
            if (jpeg == null || jpeg.Length == 0)
            {
                meetingRequestingPhoto = false;
                notice = string.IsNullOrEmpty(status) || status.StartsWith("실물 카메라 준비됨", StringComparison.Ordinal)
                    ? "촬영할 수 있는 새 RGB 프레임이 없습니다." : status;
                Draw();
                yield break;
            }
            yield return transport.UploadPhoto(transport.SessionPath + "/media", jpeg, version, part, timestamp, (_, error) =>
            {
                if (meetingTransport != transport) return;
                meetingRequestingPhoto = false;
                notice = error ?? "참고 사진을 이 모델의 근거에 추가했습니다.";
                Draw();
            });
        }

        void ReleaseMeetingCapture()
        {
            meetingCapture?.Cancel();
            meetingRequestingMicrophone = meetingRequestingPhoto = meetingPhotoCaptured = false;
            meetingClockValid = false;
        }
    }
}
