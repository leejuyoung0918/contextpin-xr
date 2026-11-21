using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
        public TraceSession TraceData { get; private set; }
        public string CurrentScreen => screen.ToString();
        public int CurrentReviewIndex => reviewIndex;

        enum ScreenState { Home, Consent, Align, Recording, Finishing, Review, Replay, Quiz, Result, Library, Transcript,
            GuideReview, GuidePractice, GuideResult, Diagnostics, Meeting }
        ScreenState screen=ScreenState.Home, transcriptReturn;
        TraceBoard board;
        TraceCapture capture;
        Camera view;
        OVRCameraRig xrRig;
        OVRHand[] hands=Array.Empty<OVRHand>();
        readonly List<XRHandSubsystem> handSystems=new List<XRHandSubsystem>();
        readonly Dictionary<string,Vector3> worldPoints=new Dictionary<string,Vector3>();
        readonly List<Transform> worldLabels=new List<Transform>();
        readonly Dictionary<string,TraceFrame> frameInfo=new Dictionary<string,TraceFrame>();
        Transform panel, content, worldLayer, reticle, probe;
        RelayHit hovered;
        AudioSource narration;
        Texture2D ownedPhoto;
        bool xr, demo=true, aligned, alignForReplay, wasPressed, wasMarking, editingLocation, useForQuiz;
        bool awaitingTrackedHead;
        float fittedAspect;
        int demoIndex, reviewIndex, replayIndex, frameIndex, libraryIndex, transcriptIndex;
        TraceAttempt attempt;
        float attemptStart;
        string notice="", storageError="";
        List<string> transcriptLines=new List<string>();
        static readonly string[] SampleRoles={"input","amp","gnd"};
        static readonly string[] SampleSpeech={
            "왼쪽의 입력 단자로 신호가 들어옵니다.",
            "여기 증폭단 출력에서 노이즈가 보일 수 있어요.",
            "그럴 때는 마지막으로 가리킨 여기, 접지 지점을 먼저 확인하세요."
        };
        static readonly float[] SampleTimes={1.2f,7.4f,14.1f};

        IEnumerator Start()
        {
            board=JsonUtility.FromJson<TraceBoard>(Resources.Load<TextAsset>("trace-board").text);
            if(board?.points==null || board.points.Length!=4) throw new InvalidOperationException("trace-board.json requires four points.");
            capture=gameObject.AddComponent<TraceCapture>();
            narration=gameObject.AddComponent<AudioSource>();narration.playOnAwake=false;narration.spatialBlend=0;
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
            var root=new GameObject("ContextPin Trace",typeof(RectTransform),typeof(Canvas));
            var rect=(RectTransform)root.transform;rect.sizeDelta=new Vector2(W,H);rect.pivot=new Vector2(.5f,.5f);
            root.GetComponent<Canvas>().renderMode=RenderMode.WorldSpace;panel=rect;
            root.AddComponent<CanvasScaler>().dynamicPixelsPerUnit=xr?1.6f:2f;
            Recenter();
            reticle=Dot("Aim",Mint);probe=Dot("Board calibration cursor",Gold);probe.gameObject.SetActive(false);
            Draw();
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

        void MovePanelBesideBoard()
        {
            if(!xr || !aligned || worldPoints.Count==0)return;
            var center=Vector3.zero;foreach(var p in worldPoints.Values)center+=p;center/=worldPoints.Count;
            var forward=Vector3.ProjectOnPlane(center-view.transform.position,Vector3.up).normalized;
            if(forward.sqrMagnitude<.1f)forward=Vector3.ProjectOnPlane(view.transform.forward,Vector3.up).normalized;
            var side=Quaternion.AngleAxis(45,Vector3.up)*forward;
            panel.localScale=Vector3.one*.00105f;
            panel.SetPositionAndRotation(view.transform.position+side*1.4f+Vector3.up*.08f,Quaternion.LookRotation(side,Vector3.up));
        }

        void Update()
        {
            if(panel==null || reticle==null) return;
            if(!xr && Mathf.Abs(view.aspect-fittedAspect)>.002f)FitDesktopPanel();
            UpdateSourceAudio();
            if(awaitingTrackedHead && InputDevices.GetDeviceAtXRNode(XRNode.Head).TryGetFeatureValue(UnityEngine.XR.CommonUsages.isTracked,out var tracked) && tracked)
            {awaitingTrackedHead=false;Recenter();}
            if(Keyboard.current?.rKey.wasPressedThisFrame==true && !RecallSpatialPanel())Recenter();
            if(xr && screen==ScreenState.Meeting && SpatialAssembly && OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch))
                RecallSpatialPanel();
            if(screen==ScreenState.Meeting)UpdateMeeting();
            UpdateGuideTracking();
            UpdatePhysicalBoardVisibility();
            if(!xr && Mouse.current?.rightButton.isPressed==true)
            {
                Vector2 d=Mouse.current.delta.ReadValue();var a=view.transform.eulerAngles;
                view.transform.rotation=Quaternion.Euler(Mathf.Clamp(Mathf.DeltaAngle(0,a.x)-d.y*.1f,-70,70),a.y+d.x*.1f,0);
            }
            foreach(var label in worldLabels)label.rotation=Quaternion.LookRotation(label.position-view.transform.position,view.transform.up);
            var ray=ReadUiPointer(out bool clicked,out bool controllerPointer);
            if(PlaceSpatialPanelFromInput(ray,clicked))clicked=false;
            bool hitSomething=Physics.Raycast(ray,out var r,12);
            var hit=hitSomething?r.collider.GetComponent<RelayHit>():null;
            if(hit!=hovered){if(hovered!=null)hovered.Hover(false);hovered=hit;if(hit!=null)hit.Hover(true);}
            reticle.position=hitSomething?r.point-ray.direction*.003f:ray.GetPoint(controllerPointer?2.25f:1.25f);
            reticle.localScale=Vector3.one*Mathf.Clamp(Vector3.Distance(reticle.position,view.transform.position)*.003f,.0012f,.009f);
            ShowControllerBeam(controllerPointer,ray.origin,reticle.position,hit!=null);
            bool marking=false;
            if(xr && screen==ScreenState.Align)
            {
                bool located=guideAlignment==GuideAlignment.None ? TryProbe(out var p) : TryPhysicalGuideContact(out p);
                probe.gameObject.SetActive(located);
                if(located){probe.position=p;probe.localScale=Vector3.one*.009f;}
                marking=(guideAlignment==GuideAlignment.None && OVRInput.Get(OVRInput.Button.One,OVRInput.Controller.RTouch))||
                    hands.Any(h=>h.GetHand()==OVRPlugin.Hand.HandLeft && Pinching(h));
                if(marking && !wasMarking && hit==null)
                {
                    if(located)MarkWorldPoint(p);
                    else
                    {
                        notice=guideAlignment==GuideAlignment.None ? "오른손 검지 또는 오른쪽 컨트롤러의 추적을 확인하세요." :
                            "오른손 검지가 헤드셋 카메라에 보이게 하고 왼손으로 핀치하세요.";
                        Draw();
                    }
                }
            }
            else if(xr && screen==ScreenState.Meeting && IsAssemblyMeeting && AssemblySampling)
                marking=UpdateAssemblyProbe(hit!=null);
            else if(probe.gameObject.activeSelf)probe.gameObject.SetActive(false);
            if(clicked && hit!=null)hit.clicked?.Invoke();
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
            {p=xrRig.trackingSpace.TransformPoint(OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch));return true;}
            p=default;return false;
        }

        void MarkWorldPoint(Vector3 point)
        {
            if(guideAlignment!=GuideAlignment.None){MarkPhysicalGuideReference(point);return;}
            if(worldPoints.Count>=board.points.Length)return;
            if(worldPoints.Values.Any(v=>Vector3.Distance(v,point)<.018f))
            {notice="여러 지점이 겹칩니다. 실제 위치로 커서를 옮겨 다시 지정하세요.";Draw();return;}
            worldPoints[board.points[worldPoints.Count].id]=point;
            notice="";Draw();
        }

        void ChooseMode(bool demoMode)
        {
            ResetGuideView();
            demo=demoMode;notice="";aligned=false;worldPoints.Clear();
            SetPassthrough(!demo && xr);Go(ScreenState.Consent);
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
        void StartPressed()
        {
            notice="";
            if(guideRecording){StartGuideRecording();return;}
            if(!demo && xr && !aligned){StartAlignment(false);return;}
            StartSession();
        }
        void StartAlignment(bool replay)
        {
            alignForReplay=replay;aligned=false;worldPoints.Clear();SetPassthrough(true);Go(ScreenState.Align);
        }
        void ConfirmAlignment()
        {
            if(guideAlignment!=GuideAlignment.None){ConfirmPhysicalGuideAlignment();return;}
            if(worldPoints.Count!=board.points.Length)return;
            aligned=true;notice="";MovePanelBesideBoard();
            if(alignForReplay)EnterReplay();else StartSession();
        }
        Vector3? ResolveWorld(string role) => guideRecording ? ResolvePhysicalGuidePoint(role) :
            aligned && worldPoints.TryGetValue(role,out var p)?p:(Vector3?)null;

        void StartSession()
        {
            TraceData=TraceSession.New(demo);reviewIndex=0;demoIndex=0;notice="";storageError="";
            Save();Go(ScreenState.Recording);
            if(demo)ReceiveSample();else
                capture.StartCapture(TraceArchive.Folder(TraceData),ResolveWorld,OnLiveEvent,OnLiveStatus);
        }
        void OnLiveStatus(string value)
        {
            notice=value??"";TraceData.lastCaptureMessage=notice;Save();
            if(screen==ScreenState.Recording||screen==ScreenState.Finishing)RefreshStatus();
        }
        void OnLiveEvent(TraceCaptureEvent e)
        {
            if(TraceData==null || string.IsNullOrEmpty(e.id))return;
            var n=TraceData.notes.Find(item=>item.id==e.id);
            if(n==null){n=new TraceNote{id=e.id};TraceData.notes.Add(n);}
            n.time=e.time;n.transcript=e.transcript;n.quote=e.quote;n.pinchRole=e.pinchRole;
            n.candidateRole=board.Contains(e.candidateRole)?e.candidateRole:"";
            n.reason=e.reason;n.status=e.status;n.framePaths=e.framePaths??Array.Empty<string>();n.audioFile=e.audioFile;
            n.pinMarks=e.pinMarks??Array.Empty<string>();
            n.sourceMode=e.sourceMode;n.provider=e.provider;n.providerItemId=e.providerItemId;n.audioSha256=e.audioSha256;
            n.revision=string.IsNullOrWhiteSpace(n.quote)?0:1;
            if(guideRecording)TraceData.microphoneOffsetMs=(long)((capture.AudioClockOrigin-transferClockOrigin)*1000);
            Save();
            if(screen==ScreenState.Recording || screen==ScreenState.Finishing)RefreshStatus();
        }
        void ReceiveSample()
        {
            var role=SampleRoles[demoIndex];var source=SampleSpeech[demoIndex];
            TraceData.notes.Add(new TraceNote{id="demo-"+demoIndex,time=SampleTimes[demoIndex],transcript=source,quote=source,
                candidateRole=role,status="suggested",reason="재현 장면에서 포인터가 "+board.NameOf(role)+"을 가리킵니다.",
                framePaths=Enumerable.Range(0,3).Select(i=>"res:TraceDemo/"+role+"_"+i).ToArray()});
            narration.Stop();var clip=Resources.Load<AudioClip>("TraceDemo/"+role+"_voice");
            if(clip!=null)narration.PlayOneShot(clip);
            Save();Draw();
        }
        void AdvanceDemo()
        {
            if(screen!=ScreenState.Recording || !demo || demoIndex>=SampleRoles.Length-1)return;
            demoIndex++;notice="";ReceiveSample();
        }
        void SelectPoint(string role)
        {
            if(!board.Contains(role))return;
            if(screen==ScreenState.Recording)
            {
                if(!demo){capture.Pin(role);return;}
                var n=TraceData.notes.Find(item=>item.id=="demo-"+demoIndex);
                n.pinchRole=role;
                bool match=n.candidateRole==role;
                n.status=match?"matched":"mismatch";
                n.reason=match?"선택한 지점과 재현 장면의 포인터가 일치합니다.":"선택한 지점과 재현 장면의 포인터가 다릅니다.";
                notice=board.NameOf(role)+" 핀치 저장";Save();Draw();
            }
            else if(screen==ScreenState.Replay)
            {
                var notes=TraceData.ReplayNotes();
                int idx=notes.FindIndex(replayIndex+1,n=>n.finalRole==role);
                if(idx<0)idx=notes.FindIndex(n=>n.finalRole==role);
                if(idx<0){notice="이 지점에 확인된 설명이 없습니다.";Draw();return;}
                replayIndex=idx;notice="";Draw();
            }
            else if(screen==ScreenState.Quiz)Answer(role);
        }
        void EndRecording()
        {
            if(screen!=ScreenState.Recording)return;
            if(guideRecording && transferCapturing){notice="사진 저장이 끝난 뒤 기록을 마칠 수 있어요.";RefreshStatus();return;}
            narration.Stop();
            if(demo){EndReviewStart();return;}
            Go(ScreenState.Finishing);capture.StopCapture(EndReviewStart);
        }
        void EndReviewStart()
        {
            if(guideRecording){EndGuideRecording();return;}
            TraceData.status="review";Save();reviewIndex=0;frameIndex=0;editingLocation=false;useForQuiz=false;LoadFrameInfo();
            notice=demo?"":TraceData.lastCaptureMessage;Go(ScreenState.Review);
        }
        TraceNote CurrentNote()
        {
            var list=TraceData.InOrder();return reviewIndex>=0 && reviewIndex<list.Count?list[reviewIndex]:null;
        }
        void MoveReview(int index)
        {
            int count=TraceData.notes.Count;reviewIndex=Mathf.Clamp(index,0,Mathf.Max(0,count-1));
            frameIndex=0;editingLocation=false;useForQuiz=false;notice="";Draw();
        }
        void Decide(string role)
        {
            var n=CurrentNote();if(n==null || (!string.IsNullOrEmpty(role)&&!board.Contains(role)))return;
            n.finalRole=role??"";n.reviewed=true;editingLocation=false;notice="";
            if(TraceData.quizNoteId==n.id && string.IsNullOrEmpty(n.finalRole))TraceData.quizNoteId="";
            bool assignQuiz=demo ? string.IsNullOrEmpty(TraceData.quizNoteId) && ReferenceForQuiz(n) : useForQuiz;
            if(assignQuiz && !string.IsNullOrEmpty(n.finalRole))
                TraceData.quizNoteId=n.id;
            Save();MoveReview(Mathf.Min(reviewIndex+1,TraceData.notes.Count-1));
        }
        void BeginReplay()
        {
            if(TraceData.PendingCount!=0||TraceData.ReplayNotes().Count==0)return;
            if(!demo && xr){StartAlignment(true);return;}
            EnterReplay();
        }
        static bool ReferenceForQuiz(TraceNote n)
        {
            string quote=n.quote??"";
            return quote.Contains("먼저") && quote.Contains("확인") && (n.transcript??"").Contains(quote);
        }
        void ToggleLiveQuizReference(TraceNote n)
        {
            if(TraceData.quizNoteId==n.id)
            {
                TraceData.quizNoteId="";useForQuiz=false;Save();
                notice="이 설명을 위치 찾기 과제에서 해제했어요.";
            }
            else if(n.reviewed && board.Contains(n.finalRole))
            {
                TraceData.quizNoteId=n.id;useForQuiz=false;Save();
                notice="교수가 먼저 확인하라고 한 지점으로 지정했어요.";
            }
            else
            {
                useForQuiz=!useForQuiz;
                notice=useForQuiz?"교수가 먼저 확인하라고 한 곳이라면 위 원문을 읽고 위치를 연결해 주세요.":"과제 지점 지정을 취소했어요.";
            }
            Draw();
        }
        void EnterReplay()
        {
            TraceData.status="shared";Save();replayIndex=0;notice="";Go(ScreenState.Replay);
        }
        void BeginQuiz()
        {
            var question=TraceData.QuizNote;if(question==null)return;
            attempt=new TraceAttempt{startedUtc=DateTime.UtcNow.ToString("o"),answerRole=question.finalRole};
            TraceData.attempts.Add(attempt);attemptStart=Time.realtimeSinceStartup;notice="";Save();Go(ScreenState.Quiz);
        }
        void Answer(string role)
        {
            if(attempt==null||attempt.completed)return;
            bool correct=role==attempt.answerRole;
            if(attempt.choices==0){attempt.firstRole=role;attempt.firstCorrect=correct;}
            attempt.choices++;attempt.elapsedSeconds=Time.realtimeSinceStartup-attemptStart;
            attempt.completed=correct;Save();
            if(correct){notice="";Go(ScreenState.Result);}else{notice="다른 지점을 선택해 보세요.";Draw();}
        }
        void Home(){ ResetGuideView();guideStudentLibrary=false;notice="";SetPassthrough(false);Recenter();Go(ScreenState.Home); }
        void OpenSession(TraceSession saved)
        {
            if(saved.IsGuide){OpenGuideSession(saved);return;}
            ResetGuideView();
            TraceData=saved;demo=saved.mode=="demo";aligned=false;worldPoints.Clear();
            reviewIndex=0;frameIndex=0;editingLocation=false;useForQuiz=false;LoadFrameInfo();SetPassthrough(false);notice="";
            Go(ScreenState.Review);
        }
        [Serializable] sealed class FrameEvidence { public TraceFrame[] frames=Array.Empty<TraceFrame>(); }
        void LoadFrameInfo()
        {
            frameInfo.Clear();
            if(demo)return;
            var path=Path.Combine(TraceArchive.Folder(TraceData),"evidence","events.jsonl");
            if(!File.Exists(path))return;
            try
            {
                foreach(var line in File.ReadLines(path))
                {
                    try
                    {
                        var packet=JsonUtility.FromJson<FrameEvidence>(line);
                        if(packet?.frames!=null)foreach(var frame in packet.frames)
                            if(!string.IsNullOrEmpty(frame?.path))frameInfo[frame.path]=frame;
                    }catch(ArgumentException){/* Ignore a partial last event after power loss. */}
                }
            }catch(IOException e){Debug.LogWarning("Evidence coordinates: "+e.Message);}
        }
        void ViewTranscript()
        {
            transcriptReturn=screen;transcriptIndex=0;transcriptLines.Clear();
            if(demo)transcriptLines.AddRange(TraceData.InOrder().Select(n=>When(n.time)+"   "+n.transcript));
            else
            {
                string path=Path.Combine(TraceArchive.Folder(TraceData),"evidence","transcript.jsonl");
                try
                {
                    if(File.Exists(path))foreach(var line in File.ReadLines(path))
                    {
                        try{
                            var t=JsonUtility.FromJson<TraceTranscriptLine>(line);
                            if(t!=null)transcriptLines.Add(When(t.start)+"   "+(string.IsNullOrEmpty(t.transcript)?(string.IsNullOrEmpty(t.error)?"(말소리 없음)":"전사 실패 · "+t.error):t.transcript));
                        }catch(ArgumentException){/* Incomplete trailing chunk after an interrupted write. */}
                    }
                }catch(IOException e){notice="전사 기록을 열 수 없습니다. "+e.Message;}
            }
            Go(ScreenState.Transcript);
        }
        [Serializable] sealed class TraceTranscriptLine {public float start=0;public string transcript="",error="";}
        static string When(float time){int t=Mathf.Max(0,Mathf.FloorToInt(time));return $"{t/60:00}:{t%60:00}";}
        void Save()
        {
            try{TraceArchive.Save(TraceData);storageError="";}
            catch(Exception e){storageError="기기 저장 실패 · "+e.Message;Debug.LogError(storageError);}
        }
        void Go(ScreenState next){screen=next;Draw();}
        void OnApplicationPause(bool pause)
        {
            if(pause)InvalidateAssemblyAlignment();
            if(pause && screen==ScreenState.Recording && !demo && capture!=null && capture.Recording && !capture.Initializing)EndRecording();
            if(pause && guideRun?.physical==true)
            {
                ResetPendingPhysicalPowerCheck();
                InvalidateGuideAlignment();
                if(screen==ScreenState.GuidePractice)Draw();
            }
        }
        void OnApplicationQuit(){if(TraceData!=null)Save();}
        void OnDestroy(){CloseMeeting();ReleaseGuideTracking();ReleasePhysicalGuideRegistration();StopTransferRelay();StopSourceAudio();ReleaseControllerBeam();if(ownedPhoto!=null)Destroy(ownedPhoto);}
    }
}
