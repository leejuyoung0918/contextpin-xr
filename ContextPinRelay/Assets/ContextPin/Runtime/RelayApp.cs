using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.Hands;

namespace ContextPin
{
    public sealed partial class RelayApp : MonoBehaviour
    {
        public GameObject xrRigPrefab;
        public Font koreanFont;
        Camera view;
        OVRCameraRig xrRig;
        OVRHand[] hands = Array.Empty<OVRHand>();
        readonly List<XRHandSubsystem> handSystems = new List<XRHandSubsystem>();
        Transform panel, content, reticle, probe;
        RelayHit hovered;
        OVRDisplay meetingDisplay;
        bool xr, wasPressed, wasMarking, awaitingTrackedHead;
        float fittedAspect;
        string notice = "";

        IEnumerator Start()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // Quest may report the first tracked pose after the rig has been created.
            // The Android app always uses the XR rig, even after a slow wake from sleep.
            xr=xrRigPrefab!=null;
#else
            bool simulatorRequested=false;
#if UNITY_EDITOR
            simulatorRequested=UnityEditor.SessionState.GetBool("ContextPinExplicitSimulator",false);
#endif
            // A camera must render for OpenXR's onBeforeRender callback to advance IDLE to READY.
            // Give an explicitly selected simulator its actual rig from the first frame.
            if(simulatorRequested && xrRigPrefab==null)throw new InvalidOperationException("Simulator preview requires the registered XR rig prefab.");
            if(!simulatorRequested)
            {
                float until=Time.realtimeSinceStartup+2.5f;
                while(!XRSettings.isDeviceActive && Time.realtimeSinceStartup<until)yield return null;
            }
            xr=simulatorRequested || (XRSettings.isDeviceActive && xrRigPrefab!=null);
#endif
            if(xr)
            {
                xrRig=Instantiate(xrRigPrefab).GetComponent<OVRCameraRig>();
                view=xrRig.centerEyeAnchor.GetComponent<Camera>();
                hands=xrRig.GetComponentsInChildren<OVRHand>(true);
                xrRig.GetComponent<OVRManager>().trackingOriginType=OVRManager.TrackingOrigin.FloorLevel;
                yield return null; yield return null;
                awaitingTrackedHead=true;
            }
            else
            {
                var cameraObject=new GameObject("Desktop Preview Camera",typeof(Camera),typeof(AudioListener));
                view=cameraObject.GetComponent<Camera>(); view.transform.position=new Vector3(0,1.55f,0);view.fieldOfView=54;
            }
            view.tag="MainCamera";view.clearFlags=CameraClearFlags.SolidColor;
            view.backgroundColor=Stage;view.nearClipPlane=.025f;view.farClipPlane=40;
            var root=new GameObject("ContextPin XR",typeof(RectTransform),typeof(Canvas));
            var rect=(RectTransform)root.transform;rect.sizeDelta=new Vector2(W,H);rect.pivot=new Vector2(.5f,.5f);
            root.GetComponent<Canvas>().renderMode=RenderMode.WorldSpace;panel=rect;
            root.AddComponent<CanvasScaler>().dynamicPixelsPerUnit=xr?1.6f:2f;
            Recenter();
            reticle=Dot("Aim",Mint);probe=Dot("Assembly alignment cursor",Gold);probe.gameObject.SetActive(false);
            EnterMeeting();
#if UNITY_ANDROID && !UNITY_EDITOR
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            using var intent = activity.Call<AndroidJavaObject>("getIntent");
            if (intent != null)
            {
                bool openSaved = intent.Call<bool>("getBooleanExtra", "contextpin_open_saved_design", false);
                bool hasJoinCode = intent.Call<bool>("hasExtra", "contextpin_join_code");
                string joinCode = hasJoinCode ? intent.Call<string>("getStringExtra", "contextpin_join_code") : null;
                intent.Call("removeExtra", "contextpin_join_code");
                intent.Call("removeExtra", "contextpin_open_saved_design");
                if (hasJoinCode)
                {
                    if (joinCode != null && joinCode.Length == 6 && Array.TrueForAll(joinCode.ToCharArray(), c => c >= '0' && c <= '9'))
                    {
                        meetingJoinCode = joinCode;
                        BeginMeetingSession(true);
                    }
                    else { notice = "여섯 자리 참가 코드를 확인하세요."; Draw(); }
                }
                else if (openSaved)
                {
                    openSavedDesignAfterResume = true;
                    ResumeLiveMeeting();
                }
            }
#endif
        }

        bool openSavedDesignAfterResume;
        void TryOpenRequestedSavedDesign()
        {
            if (!openSavedDesignAfterResume) return;
            openSavedDesignAfterResume = false;
            if (SpatialAssembly && AssemblySessionStatus == "completed" && AssemblyRecordingMode == "live")
                ContinueSavedDesign();
        }

        Transform Dot(string name,Color color)
        {
            var obj=GameObject.CreatePrimitive(PrimitiveType.Sphere);obj.name=name;
            var col=obj.GetComponent<Collider>();col.enabled=false;Destroy(col);
            var shader=Shader.Find("Unlit/Color")??Shader.Find("Sprites/Default");
            if(shader!=null) obj.GetComponent<Renderer>().material=new Material(shader){color=color};
            return obj.transform;
        }

        public void Recenter()
        {
            if(panel==null || view==null) return;
            Vector3 forward=Vector3.ProjectOnPlane(view.transform.forward,Vector3.up).normalized;
            if(forward.sqrMagnitude<.1f)forward=Vector3.forward;
            float distance=xr?1.45f:2.2f;
            if(xr)panel.localScale=Vector3.one*.00125f;
            else FitDesktopPanel();
            panel.SetPositionAndRotation(view.transform.position+forward*distance,Quaternion.LookRotation(forward,Vector3.up));
        }
        void FitDesktopPanel()
        {
            fittedAspect=Mathf.Max(.1f,view.aspect);
            float frustumHeight=4.4f*Mathf.Tan(view.fieldOfView*Mathf.Deg2Rad*.5f);
            panel.localScale=Vector3.one*(frustumHeight*.94f*Mathf.Min(1f/H,fittedAspect/W));
        }

        void Update()
        {
            if(panel==null || reticle==null) return;
            if(!xr && Mathf.Abs(view.aspect-fittedAspect)>.002f)FitDesktopPanel();
            if (xr && meetingDisplay == null && OVRManager.display != null)
            {
                meetingDisplay = OVRManager.display;
                meetingDisplay.RecenteredPose += InvalidateRegisteredPoses;
            }
            if(awaitingTrackedHead && InputDevices.GetDeviceAtXRNode(XRNode.Head).TryGetFeatureValue(UnityEngine.XR.CommonUsages.isTracked,out var tracked) && tracked)
            {awaitingTrackedHead=false;Recenter();}
            if(Keyboard.current?.rKey.wasPressedThisFrame==true && !RecallSpatialPanel())Recenter();
            if(xr && SpatialAssembly && OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch))
                RecallSpatialPanel();
            UpdateMeeting();

            if(!xr && Mouse.current?.rightButton.isPressed==true)
            {
                Vector2 d=Mouse.current.delta.ReadValue();var a=view.transform.eulerAngles;
                view.transform.rotation=Quaternion.Euler(Mathf.Clamp(Mathf.DeltaAngle(0,a.x)-d.y*.1f,-70,70),a.y+d.x*.1f,0);
            }

            var ray=ReadUiPointer(out bool clicked,out bool controllerPointer);
            bool panelOwnsPointer = PlaceSpatialPanelFromInput(ray, clicked);
            if (panelOwnsPointer) clicked = false;
            bool hitSomething=Physics.Raycast(ray,out var r,12);
            var hit=hitSomething?r.collider.GetComponent<RelayHit>():null;
            if(hit!=hovered){if(hovered!=null)hovered.Hover(false);hovered=hit;if(hit!=null)hit.Hover(true);}
            reticle.position=hitSomething?r.point-ray.direction*.003f:ray.GetPoint(controllerPointer?2.25f:1.25f);
            reticle.localScale=Vector3.one*Mathf.Clamp(Vector3.Distance(reticle.position,view.transform.position)*.003f,.0012f,.009f);
            ShowControllerBeam(controllerPointer,ray.origin,reticle.position,hit!=null);
            // Includes the first trigger-down frame on a move/alignment button, before its callback runs.
            bool uiAction = clicked && hit != null && hit.transform.IsChildOf(panel);
            bool blockAction = panelOwnsPointer || uiAction;
            bool movingLatest = xr && ReadLatestGrip(blockAction);
            if (movingLatest) { clicked = false; blockAction = true; }
            bool marking = false;
            if (xr && IsAssemblyMeeting && (A4PointSampling || TabletopPointSampling) && !ReviewTableSampling)
                ReadTabletopPlacementButton(blockAction);
            else if (xr && IsAssemblyMeeting && (AssemblySampling || ReviewTableSampling))
                marking = UpdateAssemblyProbe(blockAction || (hit != null && (!ReviewTableSampling || hit.transform.IsChildOf(panel))));
            else if ((movingLatest || !ReadExplicitTargetButton(blockAction)) && probe.gameObject.activeSelf)
                probe.gameObject.SetActive(false);
            if(!panelOwnsPointer)
                UpdateCollaborationPointer(hit, hitSomething ? r.point : Vector3.zero, clicked);
            if(clicked && hit!=null)
            {
                hit.pointClicked?.Invoke(r.point);
                hit.clicked?.Invoke();
            }
            wasMarking=marking;
        }

        static bool Pinching(OVRHand h) => h!=null && h.IsTracked && h.GetFingerIsPinching(OVRHand.HandFinger.Index);
        bool TryProbe(out Vector3 p)
        {
            var hand=hands.FirstOrDefault(h=>h.GetHand()==OVRPlugin.Hand.HandRight && h.IsTracked);
            if(hand!=null)
            {
                SubsystemManager.GetSubsystems(handSystems);
                foreach(var system in handSystems)
                    if(system.running && system.rightHand.isTracked &&
                       system.rightHand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var pose))
                    {p=xrRig.trackingSpace.TransformPoint(pose.position);return true;}
            }
            if(OVRInput.GetControllerPositionTracked(OVRInput.Controller.RTouch))
            {
                var point = OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch);
                if (ReviewTableSampling)
                    point += OVRInput.GetLocalControllerRotation(OVRInput.Controller.RTouch) * Vector3.forward * .10f;
                p = xrRig.trackingSpace.TransformPoint(point);
                return true;
            }
            p=default;return false;
        }

        void SetPassthrough(bool enabled)
        {
            if(xrRig==null)return;
            var manager=xrRig.GetComponent<OVRManager>();manager.isInsightPassthroughEnabled=enabled;
            var layer=xrRig.GetComponent<OVRPassthroughLayer>();
            if(enabled && layer==null)layer=xrRig.gameObject.AddComponent<OVRPassthroughLayer>();
            if(layer!=null)layer.enabled=enabled;
            view.backgroundColor=enabled?Color.clear:Stage;
        }
        void InvalidateRegisteredPoses() => InvalidateAssemblyAlignment();
        void OnApplicationPause(bool paused)
        {
            if (paused) InvalidateRegisteredPoses();
        }
        void OnDestroy()
        {
            if (meetingDisplay != null) meetingDisplay.RecenteredPose -= InvalidateRegisteredPoses;
            CloseMeeting();
            ReleaseControllerBeam();
        }
    }
}
