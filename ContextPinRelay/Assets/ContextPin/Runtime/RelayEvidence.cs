using System;
using System.Collections;
using System.IO;
using System.Text;
using Meta.XR;
using UnityEngine;
using UnityEngine.Rendering;

namespace ContextPin
{
    [Serializable] public sealed class RelayConfig
    {
        public string replayServerUrl="http://127.0.0.1:8788";
        public string meetingServerUrl="http://127.0.0.1:8789";
        public float meetingPanelWidthMeters=.32f, meetingPanelDistanceMeters=1.15f, meetingPanelTextScale=1f;
    }
    public sealed class RelayEvidence : MonoBehaviour
    {
        public bool Recording => _clip != null;
        public bool CameraReady => _camera != null && _camera.IsPlaying;
        public AudioClip RecordingClip => _clip;
        public PassthroughCameraAccess CameraSource => _camera;
        public bool TryRecentCameraFrame(long selectedAtMs, out long capturedAtMs)
        {
            capturedAtMs = 0;
            if (!CameraReady || _camera.Timestamp == default) return false;
            capturedAtMs = new DateTimeOffset(_camera.Timestamp.ToUniversalTime()).ToUnixTimeMilliseconds();
            return capturedAtMs <= selectedAtMs && selectedAtMs - capturedAtMs <= 150;
        }
        MicrophoneBuffer _buffer;
        AudioClip _clip;
        PassthroughCameraAccess _camera;
        GameObject _cameraObject;
        int _micEpoch, _cameraEpoch;
        public RelayConfig config = new RelayConfig();

        void Awake() { config=LoadConfig(); }

        internal static RelayConfig LoadConfig()
        {
            var local=Resources.Load<TextAsset>("relay-local");
            if (local != null)
            {
                try { return JsonUtility.FromJson<RelayConfig>(local.text)??new RelayConfig(); }
                catch (Exception e) { Debug.LogWarning("Invalid relay-local config: "+e.Message); }
            }
            return new RelayConfig();
        }

        public IEnumerator StartMicrophone(Action<string> done, int ringSeconds = 20)
        {
            int epoch=++_micEpoch;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
            {
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
                float until=Time.realtimeSinceStartup+20;
                while (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone) && Time.realtimeSinceStartup<until) yield return null;
                if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone)) { done("마이크 권한이 필요합니다."); yield break; }
            }
#else
            if (!Application.HasUserAuthorization(UserAuthorization.Microphone)) yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);
            if (!Application.HasUserAuthorization(UserAuthorization.Microphone)) { done("시스템 설정에서 마이크 권한을 허용하세요."); yield break; }
#endif
            if(epoch!=_micEpoch)yield break;
            if (Microphone.devices.Length==0) { done("사용 가능한 마이크가 없습니다."); yield break; }
            StopMicrophone();
            _clip=Microphone.Start(null,true,ringSeconds,16000);
            if (_clip==null) { done("마이크를 시작하지 못했습니다."); yield break; }
            _buffer=new MicrophoneBuffer(_clip,Time.realtimeSinceStartup);
            done("회의 마이크에서 음성을 받고 있습니다.");
        }

        public byte[] AudioSnapshot(bool mono=false)
        {
            return _buffer?.Snapshot(Microphone.GetPosition(null), Time.realtimeSinceStartup, mono);
        }
        public void StopMicrophone()
        {
            _micEpoch++;
            if (_clip!=null) { Microphone.End(null); Destroy(_clip); }
            _clip=null; _buffer=null;
        }
        public IEnumerator StartPassthroughCamera(Action<string> done)
        {
            int epoch=++_cameraEpoch;
            if (OVRManager.instance==null || !PassthroughCameraAccess.IsSupported) { done("카메라 기능은 지원되는 Quest 런타임에서 사용할 수 있습니다."); yield break; }
            OVRManager.instance.isInsightPassthroughEnabled=true;
            OVRPermissionsRequester.Request(new[] { OVRPermissionsRequester.Permission.PassthroughCameraAccess });
            float until=Time.realtimeSinceStartup+20;
            while (!OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.PassthroughCameraAccess) && Time.realtimeSinceStartup<until) yield return null;
            if(epoch!=_cameraEpoch)yield break;
            if (!OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.PassthroughCameraAccess)) { done("카메라 권한이 필요합니다."); yield break; }
            if (_cameraObject==null)
            {
                _cameraObject=new GameObject("Event camera"); _cameraObject.SetActive(false);
                _camera=_cameraObject.AddComponent<PassthroughCameraAccess>();
                _camera.CameraPosition=PassthroughCameraAccess.CameraPositionType.Left;
                _camera.RequestedResolution=new Vector2Int(1280,960);
                _cameraObject.SetActive(true);
            }
            until=Time.realtimeSinceStartup+12;
            while (!CameraReady && Time.realtimeSinceStartup<until && epoch==_cameraEpoch) yield return null;
            if(epoch!=_cameraEpoch)yield break;
            done(CameraReady?"실물 RGB 카메라 준비됨":"실물 RGB 카메라가 준비되지 않았습니다. 카메라 권한을 확인하고 다시 시도하세요.");
        }
        public void StopCamera() { _cameraEpoch++; if (_cameraObject!=null) Destroy(_cameraObject); _cameraObject=null; _camera=null; }
        public void Cancel() { StopMicrophone(); StopCamera(); }
        void OnDestroy() { Cancel(); }

        internal readonly struct SelectionImage
        {
            public readonly byte[] Jpeg;
            public readonly Vector2 Uv;
            public readonly Pose CameraPose;
            public readonly long CapturedAtMs;
            public readonly long? SensorTimestampUs;
            public readonly string FallbackReason;
            public bool ActualRgb => SensorTimestampUs.HasValue;

            public SelectionImage(byte[] jpeg, Vector2 uv, Pose pose, long timeMs, long? sensorUs, string fallbackReason)
            {
                Jpeg = jpeg;
                Uv = uv;
                CameraPose = pose;
                CapturedAtMs = timeMs;
                SensorTimestampUs = sensorUs;
                FallbackReason = fallbackReason;
            }
        }

        internal SelectionImage CaptureSelectionImage(Camera view, Vector3 worldPoint, long selectedAtMs, bool useSensor)
        {
            var debugPose = new Pose(view.transform.position, view.transform.rotation);
            string reason = useSensor ? "no_recent_rgb_frame" : "simulator";
            if (useSensor && TryRecentCameraFrame(selectedAtMs, out long capturedAtMs))
            {
                try
                {
                    var timestamp = _camera.Timestamp;
                    var pose = _camera.GetCameraPose();
                    var uv = PhysicalViewportPoint(worldPoint, pose);
                    if (!InViewport(uv)) throw new InvalidOperationException("target_outside_rgb_image");
                    // The SDK schedules the newest texture on the render thread. A Blit here
                    // can read the previous frame. Queue and finish this readback on the A event.
                    var source = _camera.GetTexture();
                    if (source == null || source.width <= 0 || source.height <= 0)
                        throw new InvalidOperationException("rgb_texture_unavailable");
                    var readback = AsyncGPUReadback.Request(source, 0, TextureFormat.RGBA32);
                    readback.WaitForCompletion();
                    if (readback.hasError || _camera.Timestamp != timestamp)
                        throw new InvalidOperationException("rgb_frame_readback_failed");
                    var texture = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                    try
                    {
                        texture.SetPixelData(readback.GetData<Color32>(), 0);
                        texture.Apply(false, false);
                        uv.y = 1 - uv.y;
                        long sensorUs = (timestamp.ToUniversalTime().Ticks - DateTime.UnixEpoch.Ticks) / 10;
                        return new SelectionImage(texture.EncodeToJPG(80), uv, pose, capturedAtMs, sensorUs, null);
                    }
                    finally { Destroy(texture); }
                }
                catch (Exception error)
                {
                    reason = "rgb_capture_failed";
                    Debug.LogWarning("ContextPin capture: using a debug render · " + error.Message);
                }
            }
            var jpeg = Snapshot(view, worldPoint, false, out var renderUv, debugPose);
            return new SelectionImage(jpeg, renderUv, debugPose, selectedAtMs, null, reason);
        }

        static bool InViewport(Vector2 uv) => float.IsFinite(uv.sqrMagnitude) && uv.x >= 0 &&
            uv.x <= 1 && uv.y >= 0 && uv.y <= 1;

        Vector2 PhysicalViewportPoint(Vector3 worldPoint, Pose pose)
        {
            float norm = Quaternion.Dot(pose.rotation, pose.rotation);
            if (!float.IsFinite(pose.position.sqrMagnitude) || !float.IsFinite(norm) || Mathf.Abs(norm - 1f) > .02f)
                throw new InvalidOperationException("촬영 프레임의 카메라 위치를 확인할 수 없습니다.");
            if (Vector3.Dot(worldPoint - pose.position, pose.rotation * Vector3.forward) <= 0)
                throw new InvalidOperationException("대상이 카메라 뒤에 있습니다.");
            return _camera.WorldToViewportPoint(worldPoint, pose);
        }

        // Capture and coordinates are calculated from the same camera source.
        public byte[] Snapshot(Camera view, Vector3 worldPoint, bool physical, out Vector2 uv) =>
            Snapshot(view, worldPoint, physical, out uv, new Pose(view.transform.position, view.transform.rotation));

        byte[] Snapshot(Camera view, Vector3 worldPoint, bool physical, out Vector2 uv, Pose renderPose)
        {
            Texture source;
            RenderTexture rendered=null;
            Camera captureCamera=null;
            if (physical)
            {
                if (!CameraReady) throw new InvalidOperationException("실물 카메라가 준비되지 않았습니다.");
                uv = PhysicalViewportPoint(worldPoint, _camera.GetCameraPose());
                source=_camera.GetTexture();
            }
            else
            {
                var obj=new GameObject("Snapshot camera"); captureCamera=obj.AddComponent<Camera>();
                captureCamera.CopyFrom(view); captureCamera.transform.SetPositionAndRotation(renderPose.position, renderPose.rotation);
                captureCamera.enabled=false; captureCamera.stereoTargetEye=StereoTargetEyeMask.None;
                captureCamera.aspect=1.6f;
                rendered=RenderTexture.GetTemporary(960,600,24,RenderTextureFormat.ARGB32);
                captureCamera.targetTexture=rendered;
                var projected = captureCamera.WorldToViewportPoint(worldPoint);
                uv = projected.z > 0 ? (Vector2)projected : new Vector2(-1, -1);
                source = rendered;
            }
            try
            {
                if (source == null || source.width <= 0 || source.height <= 0)
                    throw new InvalidOperationException("캡처할 이미지 프레임이 없습니다.");
                if (captureCamera != null) captureCamera.Render();
                if (!InViewport(uv)) throw new InvalidOperationException("대상이 캡처 이미지 밖입니다. 대상을 다시 조준하세요.");
                // JSON image coordinates use top-left, unlike Unity's bottom-left viewport.
                uv.y=1-uv.y;
                var copy=RenderTexture.GetTemporary(960,Mathf.RoundToInt(960f*source.height/source.width),0,RenderTextureFormat.ARGB32);
                var previous=RenderTexture.active;
                Texture2D image=null;
                try
                {
                    Graphics.Blit(source,copy); RenderTexture.active=copy;
                    image=new Texture2D(copy.width,copy.height,TextureFormat.RGB24,false);
                    image.ReadPixels(new Rect(0,0,copy.width,copy.height),0,0); image.Apply();
                    return image.EncodeToJPG(80);
                }
                finally { RenderTexture.active=previous; if (image!=null) Destroy(image); RenderTexture.ReleaseTemporary(copy); }
            }
            finally
            {
                if (captureCamera!=null) { captureCamera.targetTexture=null; Destroy(captureCamera.gameObject); }
                if (rendered!=null) RenderTexture.ReleaseTemporary(rendered);
            }
        }

    }
    public sealed class MicrophoneBuffer
    {
        readonly AudioClip clip;
        readonly float started;
        public MicrophoneBuffer(AudioClip clip,float started) { this.clip=clip; this.started=started; }
        public byte[] Snapshot(int position,float now,bool mono=false)
        {
            if(position<0) return null;
            int frames=Math.Min(Math.Min((int)((now-started)*clip.frequency),clip.samples),clip.frequency*12);
            if (frames<clip.frequency/3) return null;
            var ring=new float[clip.samples*clip.channels]; clip.GetData(ring,0);
            int start=(position-frames+clip.samples)%clip.samples;
            using(var stream=new MemoryStream()) using(var writer=new BinaryWriter(stream))
            {
                int channels=mono?1:clip.channels;
                int bytes=frames*channels*2;
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));writer.Write(36+bytes);writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);writer.Write((short)1);writer.Write((short)channels);writer.Write(clip.frequency);
                writer.Write(clip.frequency*channels*2);writer.Write((short)(channels*2));writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));writer.Write(bytes);
                for(int frame=0;frame<frames;frame++)
                {
                    int offset=((start+frame)%clip.samples)*clip.channels;
                    if(mono)
                    {
                        float average=0;
                        for(int channel=0;channel<clip.channels;channel++)average+=ring[offset+channel];
                        writer.Write(Pcm16(average/clip.channels));
                    }
                    else
                        for(int channel=0;channel<channels;channel++)writer.Write(Pcm16(ring[offset+channel]));
                }
                return stream.ToArray();
            }
        }
        static short Pcm16(float value) => (short)Mathf.RoundToInt(Mathf.Clamp(value,-1,1)*32767);
    }
}
