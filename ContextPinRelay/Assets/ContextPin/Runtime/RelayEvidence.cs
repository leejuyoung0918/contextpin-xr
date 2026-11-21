using System;
using System.Collections;
using System.IO;
using System.Text;
using Meta.XR;
using UnityEngine;
using UnityEngine.Networking;

namespace ContextPin
{
    [Serializable] public sealed class RelayConfig
    {
        public string serverUrl="http://127.0.0.1:8787";
        // Optional for providers without speech recognition. Never put an API key here.
        public string suppliedTranscript="";
    }
    [Serializable] public sealed class CaptureRequest
    {
        public string sourceBoardId, sourceObjectId, targetRole, intent, imageKind;
        public string imageBase64, audioBase64, transcript;
        public float u, v;
        public bool consent;
    }

    public sealed class RelayEvidence : MonoBehaviour
    {
        public bool Recording => _clip != null;
        public bool CameraReady => _camera != null && _camera.IsPlaying;
        public AudioClip RecordingClip => _clip;
        public PassthroughCameraAccess CameraSource => _camera;
        MicrophoneBuffer _buffer;
        AudioClip _clip;
        PassthroughCameraAccess _camera;
        GameObject _cameraObject;
        UnityWebRequest _pending;
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
            done("녹음 중 · 핀치 직전 최대 12초만 분석합니다.");
        }

        public byte[] AudioSnapshot()
        {
            return _buffer?.Snapshot(Microphone.GetPosition(null), Time.realtimeSinceStartup);
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
            done(CameraReady?"실물 카메라 준비됨 · 보드 정렬을 확인하세요.":"카메라가 준비되지 않았습니다. 가상 보드 모드를 사용하거나 권한을 확인하세요.");
        }
        public void StopCamera() { _cameraEpoch++; if (_cameraObject!=null) Destroy(_cameraObject); _cameraObject=null; _camera=null; }
        public void Cancel() { _pending?.Abort(); StopMicrophone(); StopCamera(); }
        void OnDestroy() { Cancel(); }

        // Capture and coordinates are calculated from the same camera source.
        public byte[] Snapshot(Camera view, Vector3 worldPoint, bool physical, out Vector2 uv)
        {
            Texture source;
            RenderTexture rendered=null;
            Camera captureCamera=null;
            if (physical)
            {
                if (!CameraReady) throw new InvalidOperationException("실물 카메라가 준비되지 않았습니다.");
                if (Vector3.Dot(worldPoint-_camera.GetCameraPose().position, _camera.GetCameraPose().rotation*Vector3.forward)<=0)
                    throw new InvalidOperationException("대상이 카메라 뒤에 있습니다.");
                uv=_camera.WorldToViewportPoint(worldPoint);
                source=_camera.GetTexture();
            }
            else
            {
                var obj=new GameObject("Snapshot camera"); captureCamera=obj.AddComponent<Camera>();
                captureCamera.CopyFrom(view); captureCamera.transform.SetPositionAndRotation(view.transform.position,view.transform.rotation);
                captureCamera.enabled=false; captureCamera.stereoTargetEye=StereoTargetEyeMask.None;
                captureCamera.aspect=1.6f;
                rendered=RenderTexture.GetTemporary(960,600,24,RenderTextureFormat.ARGB32);
                captureCamera.targetTexture=rendered;
                uv=captureCamera.WorldToViewportPoint(worldPoint);
                captureCamera.Render(); source=rendered;
            }
            try
            {
                if (uv.x<0 || uv.x>1 || uv.y<0 || uv.y>1) throw new InvalidOperationException("대상이 캡처 이미지 밖입니다. 대상을 다시 조준하세요.");
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

        public IEnumerator Analyze(CaptureRequest request, Action<Experience,string> done)
        {
            var url=config.serverUrl.TrimEnd('/')+"/capture";
            using (var web=new UnityWebRequest(url,"POST"))
            {
                _pending=web; web.timeout=100;
                web.uploadHandler=new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(request)));
                web.downloadHandler=new DownloadHandlerBuffer(); web.SetRequestHeader("Content-Type","application/json");
                yield return web.SendWebRequest(); _pending=null;
                if (web.result!=UnityWebRequest.Result.Success) { done(null,"AI 연결 실패 ("+web.responseCode+"). 서버 실행·모델 설정·USB 연결을 확인하세요. "+web.downloadHandler.text); yield break; }
                Experience card=null; string error="";
                try { card=JsonUtility.FromJson<Experience>(web.downloadHandler.text); if(card==null)error="AI 응답이 비어 있습니다."; }
                catch (Exception e) { error="AI 응답 형식 오류: "+e.Message; }
                done(card,error);
            }
        }
    }

    public sealed class MicrophoneBuffer
    {
        readonly AudioClip clip;
        readonly float started;
        public MicrophoneBuffer(AudioClip clip,float started) { this.clip=clip; this.started=started; }
        public byte[] Snapshot(int position,float now)
        {
            if(position<0) return null;
            int frames=Math.Min(Math.Min((int)((now-started)*clip.frequency),clip.samples),clip.frequency*12);
            if (frames<clip.frequency/3) return null;
            var ring=new float[clip.samples*clip.channels]; clip.GetData(ring,0);
            int start=(position-frames+clip.samples)%clip.samples;
            using(var stream=new MemoryStream()) using(var writer=new BinaryWriter(stream))
            {
                int bytes=frames*clip.channels*2;
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));writer.Write(36+bytes);writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);writer.Write((short)1);writer.Write((short)clip.channels);writer.Write(clip.frequency);
                writer.Write(clip.frequency*clip.channels*2);writer.Write((short)(clip.channels*2));writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));writer.Write(bytes);
                for(int i=0;i<frames;i++)for(int ch=0;ch<clip.channels;ch++)
                    writer.Write((short)Mathf.RoundToInt(Mathf.Clamp(ring[((start+i)%clip.samples)*clip.channels+ch],-1,1)*32767));
                return stream.ToArray();
            }
        }
    }
}
