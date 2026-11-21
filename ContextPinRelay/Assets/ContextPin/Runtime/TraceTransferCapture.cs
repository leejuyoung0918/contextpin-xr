using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Meta.XR;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace ContextPin
{
    [Serializable]
    public sealed class TransferCapture
    {
        public string captureId, sessionId, requester, triggerKind, phase, stepId, selectedTerminal;
        public string source, path, sha256, quality, imageClockDomain, imageOrientation, metadataAlignment, distortion;
        public string boardInstanceId, rgbEye, noteId;
        public long requestTimeMs, sessionTimeMs, frameUtcTicks;
        public int width, height, bytes, registrationRevision, trackingEpoch, frameSequence;
        public double clockUncertaintyMs, elapsedMs, headMotionDegrees;
        public Vector2 sensorFocal, sensorPrincipal;
        public Vector2Int sensorResolution, frameResolution;
        public Rect sensorCrop;
        public Vector3 cameraPosition;
        public Quaternion cameraRotation;
        public TraceImageProjection[] projectedPoints;
    }

    [Serializable]
    public sealed class TraceImageProjection
    {
        public string terminal;
        public Vector2 uv;
    }

    public sealed partial class RelayApp
    {
        bool transferCapturing;
        string recordingPhase = "after_action";
        string selectedTransferTerminal;
        int captureSequence;
        long lastCaptureFrame;
        double transferClockOrigin;

        long TransferTimeMs => (long)Math.Max(0, (Time.realtimeSinceStartupAsDouble - transferClockOrigin) * 1000);

        void SetRecordingPhase(string phase)
        {
            recordingPhase = phase;
            Draw();
        }

        bool CanTakeTransferPhoto => xr && TraceData?.transferConsent == true &&
            (screen == ScreenState.Recording || screen == ScreenState.GuidePractice ||
             screen == ScreenState.Align && guideAlignment != GuideAlignment.None);

        bool RequestTransferPhoto(string phase, string requester = "student", string triggerId = null)
        {
            if (!CanTakeTransferPhoto || transferCapturing)
            {
                notice = transferCapturing ? "이미 장면을 저장하고 있어요." : "이 화면에서는 실물 사진을 찍을 수 없어요.";
                RefreshStatus();
                return false;
            }
            string id = triggerId ?? Guid.NewGuid().ToString("N");
            if (TraceData.captures.Any(photo => photo.captureId == id)) return true;
            var entry = new TransferCapture
            {
                captureId = id,
                sessionId = TraceData.id,
                requester = requester,
                triggerKind = requester == "instructor" ? "teacher_pc" : "ui",
                phase = phase,
                stepId = GuideRules.Current(guideRun)?.stepId,
                selectedTerminal = selectedTransferTerminal,
                requestTimeMs = TransferTimeMs,
                source = Application.platform == RuntimePlatform.Android ? "real_camera" : "synthetic",
                rgbEye = "left",
            };
            transferCapturing = true;
            TraceData.shutterRequests++;
            notice = "손을 빼고 보드를 봐 주세요. 잠시 뒤 촬영합니다.";
            Draw();
            StartCoroutine(CaptureTransferPhoto(TraceData, entry));
            return true;
        }

        IEnumerator CaptureTransferPhoto(TraceSession session, TransferCapture entry)
        {
            double started = Time.realtimeSinceStartupAsDouble;
            Quaternion startRotation = view.transform.rotation;
            yield return new WaitForSecondsRealtime(.5f);

            var cameraDevice = capture.EvidenceDevice();
            string cameraError = "";
            if (!cameraDevice.CameraReady)
            {
                yield return cameraDevice.StartPassthroughCamera(message => cameraError = message);
                if (!cameraDevice.CameraReady)
                {
                    FinishTransferPhoto(session, entry, "카메라 사진을 얻지 못했어요. " + cameraError);
                    yield break;
                }
            }
            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                FinishTransferPhoto(session, entry, "이 환경에서는 카메라 프레임 비동기 읽기를 사용할 수 없어요.");
                yield break;
            }
            var camera = cameraDevice.CameraSource;
            double until = Time.realtimeSinceStartupAsDouble + 1.3;
            while (camera != null && Time.realtimeSinceStartupAsDouble < until)
            {
                yield return new WaitForEndOfFrame();
                if (camera.IsPlaying && camera.IsUpdatedThisFrame && camera.Timestamp.Ticks != lastCaptureFrame) break;
            }
            if (camera == null || !camera.IsPlaying || !camera.IsUpdatedThisFrame || camera.Timestamp.Ticks == lastCaptureFrame)
            {
                FinishTransferPhoto(session, entry, "새 카메라 프레임을 받지 못했어요. 보드를 보고 다시 촬영하세요.");
                yield break;
            }
            if (session != TraceData)
            {
                FinishTransferPhoto(session, entry, "촬영 중 세션이 바뀌었어요.");
                yield break;
            }

            var texture = camera.GetTexture();
            var cameraPose = camera.GetCameraPose();
            var intrinsics = camera.Intrinsics;
            var registration = ActiveBoardRegistration;
            entry.boardInstanceId = registration?.boardInstanceId ?? physicalBoardInstanceId;
            entry.registrationRevision = registration?.revision ?? 0;
            entry.trackingEpoch = registration?.trackingEpoch ?? PhysicalTrackingEpoch;
            entry.cameraPosition = cameraPose.position;
            entry.cameraRotation = cameraPose.rotation;
            entry.frameUtcTicks = lastCaptureFrame = camera.Timestamp.Ticks;
            entry.frameSequence = ++captureSequence;
            entry.headMotionDegrees = Quaternion.Angle(startRotation, view.transform.rotation);
            entry.imageClockDomain = "mruk_sdk_utc";
            entry.imageOrientation = "top_left";
            entry.metadataAlignment = entry.source == "real_camera" ? "approximate" : "synthetic";
            entry.distortion = "sdk_image_unspecified";
            entry.sensorResolution = intrinsics.SensorResolution;
            entry.sensorFocal = intrinsics.FocalLength;
            entry.sensorPrincipal = intrinsics.PrincipalPoint;
            entry.frameResolution = camera.CurrentResolution;
            entry.sessionTimeMs = -1;
            double age = (DateTime.UtcNow.Ticks - entry.frameUtcTicks) / (double)TimeSpan.TicksPerMillisecond;
            entry.clockUncertaintyMs = Math.Max(50, Math.Abs(age));
            if (age >= -40 && age <= 500) entry.sessionTimeMs = Math.Max(0, TransferTimeMs - (long)Math.Max(0, age));
            if (texture == null || texture.width < 8 || texture.height < 8)
            {
                FinishTransferPhoto(session, entry, "카메라가 준비되지 않았어요. 잠시 뒤 다시 촬영하세요.");
                yield break;
            }

            // MRUK projects into the center crop used by its RGB texture.
            var sensor = (Vector2)entry.sensorResolution;
            if (sensor.x > 0 && sensor.y > 0)
            {
                var crop = (Vector2)entry.frameResolution / sensor;
                crop /= Mathf.Max(crop.x, crop.y);
                var extent = (Vector2.one - crop) * .5f * sensor;
                entry.sensorCrop = new Rect(extent, crop * sensor);
            }
            entry.projectedPoints = registration == null ? Array.Empty<TraceImageProjection>() : physicalGuideBoard.points
                .Select(point => new { point.id, position = ToUnity(registration.ToWorld(physicalGuideBoard.FindTerminal(point.id).boardLocal)) })
                .Where(point => Vector3.Dot(point.position - cameraPose.position, cameraPose.rotation * Vector3.forward) > 0)
                .Select(point =>
                {
                    Vector2 uv = camera.WorldToViewportPoint(point.position, cameraPose);
                    return new TraceImageProjection { terminal = point.id, uv = new Vector2(uv.x, 1 - uv.y) };
                }).ToArray();

            entry.width = texture.width;
            entry.height = texture.height;
            entry.quality = entry.projectedPoints.Length < 3 ? "registration_unavailable" :
                entry.projectedPoints.Any(p => p.uv.x < .025f || p.uv.x > .975f || p.uv.y < .025f || p.uv.y > .975f)
                ? "out_of_frame" : "visibility_unverified";

            // Blit into owned GPU memory, then enqueue the readback behind the copy.
            // The SDK cannot guarantee an exact pixel/pose pairing on the GPU path.
            var copy = RenderTexture.GetTemporary(entry.width, entry.height, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB);
            byte[] pixels = null;
            bool finished = false, abandoned = false;
            try
            {
                Graphics.Blit(texture, copy);
                AsyncGPUReadback.Request(copy, 0, TextureFormat.RGBA32, result =>
                {
                    try { if (!abandoned && !result.hasError) pixels = result.GetData<byte>().ToArray(); }
                    finally { RenderTexture.ReleaseTemporary(copy); finished = true; }
                });
            }
            catch (Exception error)
            {
                RenderTexture.ReleaseTemporary(copy);
                FinishTransferPhoto(session, entry, "카메라 복사 실패 · " + error.GetType().Name);
                yield break;
            }
            until = Time.realtimeSinceStartupAsDouble + 1.6;
            while (!finished && Time.realtimeSinceStartupAsDouble < until) yield return null;
            abandoned = !finished;
            if (!finished || pixels == null)
            {
                FinishTransferPhoto(session, entry, "카메라 읽기 시간이 초과됐거나 실패했어요. 다시 촬영하세요.");
                yield break;
            }
            session.photoReadbacks++;
            string folder = Path.Combine(TraceArchive.Folder(session), "evidence");
            entry.path = entry.captureId + ".jpg";
            entry.elapsedMs = (Time.realtimeSinceStartupAsDouble - started) * 1000;
            var job = Task.Run(() => StoreTransferPhoto(entry, pixels, folder));
            while (!job.IsCompleted) yield return null;
            if (job.IsFaulted)
            {
                FinishTransferPhoto(session, entry, "사진 파일을 저장하지 못했어요.");
                yield break;
            }
            FinishTransferPhoto(session, entry, "");
        }

        static void StoreTransferPhoto(TransferCapture entry, byte[] pixels, string folder)
        {
            int row = entry.width * 4;
            var scratch = new byte[row];
            double brightness = 0;
            for (int y = 0; y < entry.height / 2; y++)
            {
                int top = y * row, bottom = (entry.height - 1 - y) * row;
                Buffer.BlockCopy(pixels, top, scratch, 0, row);
                Buffer.BlockCopy(pixels, bottom, pixels, top, row);
                Buffer.BlockCopy(scratch, 0, pixels, bottom, row);
            }
            int samples = 0;
            for (int pixel = 0; pixel + 3 < pixels.Length; pixel += 4 * 31)
            {
                brightness += (pixels[pixel] + pixels[pixel + 1] + pixels[pixel + 2]) / 3.0;
                samples++;
            }
            double mean = brightness / Math.Max(1, samples);
            if (mean < 16) entry.quality = "dark_or_warmup";
            if (mean > 246) entry.quality = "overexposed";
            byte[] jpeg = ImageConversion.EncodeArrayToJPG(pixels, GraphicsFormat.R8G8B8A8_UNorm,
                (uint)entry.width, (uint)entry.height, (uint)row, 84);
            if (jpeg == null || jpeg.Length == 0) throw new IOException("JPEG encoding failed");
            using (var sha = SHA256.Create()) entry.sha256 = BitConverter.ToString(sha.ComputeHash(jpeg)).Replace("-", "").ToLowerInvariant();
            entry.bytes = jpeg.Length;
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, entry.path);
            File.WriteAllBytes(path + ".part", jpeg);
            if (File.Exists(path)) File.Replace(path + ".part", path, null);
            else File.Move(path + ".part", path);
        }

        void FinishTransferPhoto(TraceSession session, TransferCapture entry, string error)
        {
            transferCapturing = false;
            if (session != TraceData) return;
            if (string.IsNullOrEmpty(error))
            {
                // Archive the frame metadata and its checksum before announcing success.
                session.captures.Add(entry);
                if (entry.phase == "student_checkpoint" && guideRun != null && GuideRules.RecordCheckpoint(guideRun,
                    entry.captureId, entry.captureId, entry.source, entry.registrationRevision, entry.trackingEpoch,
                    DateTime.UtcNow, entry.stepId))
                {
                    session.latestCheckpoint = entry.captureId;
                    if (guideEvidenceOpen) { guideViewingCheckpoint = true; guideEvidenceFrame = 0; }
                    if (session.recentHelp?.stepId == entry.stepId && session.recentHelp.registrationRevision == entry.registrationRevision)
                        session.recentHelp.captureId = entry.captureId;
                }
                Save();
                notice = !string.IsNullOrEmpty(storageError) ? "사진을 썼지만 기록을 저장하지 못했어요." :
                    entry.quality == "visibility_unverified" ? "장면을 저장했어요. 가림 여부를 사진에서 확인하세요." :
                    "장면을 저장했어요. 프레임이나 밝기를 확인하고 필요하면 다시 촬영하세요.";
            }
            else
            {
                session.failedPhotos++;
                Save();
                notice = error;
            }
            if (!guideRecording) capture.EvidenceDevice().StopCamera();
            Draw();
        }
    }
}
