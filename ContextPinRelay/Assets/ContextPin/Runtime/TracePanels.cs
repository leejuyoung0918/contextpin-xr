using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        const int W=1240,H=760;
        // The paper surfaces are neutral so the recorded circuit and its markings retain their own colors.
        static readonly Color Ink=new Color32(246,244,235,255),Card=new Color32(255,254,249,255),
            Field=new Color32(223,233,225,255),Light=new Color32(23,45,42,255),Muted=new Color32(68,90,82,255),
            Mint=new Color32(0,100,80,255),Gold=new Color32(132,76,15,255),Deep=new Color32(20,57,49,255),
            Stage=new Color32(20,40,38,255),Divider=new Color32(195,208,194,255),
            HeaderText=new Color32(247,248,235,255),Lime=new Color32(202,232,145,255);
        Text feedbackText,recordCount,recordSpeech;

        RectTransform Rect(string name,Transform parent,float x,float y,float w,float h)
        {
            var t=(RectTransform)new GameObject(name,typeof(RectTransform)).transform;t.SetParent(parent,false);
            t.anchorMin=t.anchorMax=t.pivot=new Vector2(0,1);t.anchoredPosition=new Vector2(x,-y);t.sizeDelta=new Vector2(w,h);
            return t;
        }
        Image Box(string name,Transform parent,float x,float y,float w,float h,Color color)
        {
            var image=Rect(name,parent,x,y,w,h).gameObject.AddComponent<Image>();image.color=color;image.raycastTarget=false;return image;
        }
        Text Label(string name,Transform parent,float x,float y,float w,float h,string value,int size,Color color,TextAnchor anchor=TextAnchor.MiddleLeft)
        {
            var t=Rect(name,parent,x,y,w,h).gameObject.AddComponent<Text>();
            t.font=koreanFont!=null?koreanFont:Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.raycastTarget=false;t.supportRichText=false;t.color=color;t.text=value??"";t.fontSize=size;t.alignment=anchor;
            t.verticalOverflow=VerticalWrapMode.Truncate;t.horizontalOverflow=HorizontalWrapMode.Wrap;
            t.resizeTextForBestFit=true;t.resizeTextMinSize=Mathf.Min(20,size);t.resizeTextMaxSize=size;return t;
        }
        Text BoldLabel(string name,Transform parent,float x,float y,float w,float h,string value,int size,Color color,TextAnchor anchor=TextAnchor.MiddleLeft)
        {
            var text=Label(name,parent,x,y,w,h,value,size,color,anchor);
            text.fontStyle=FontStyle.Bold;return text;
        }
        Image RoundBox(string name,Transform parent,float x,float y,float w,float h,Color color,float radius=12)
        {
            var im=Box(name,parent,x,y,w,h,color);
            im.sprite=TraceSurface.Rounded;im.type=Image.Type.Sliced;
            im.pixelsPerUnitMultiplier=16f/Mathf.Clamp(radius,1,Mathf.Min(w,h)/2);
            return im;
        }
        RelayHit Button(string id,float x,float y,float w,float h,string label,Action action,bool primary=false,Transform parent=null,string role=null)
        {
            var im=RoundBox(id,parent??content,x,y,w,h,primary?Mint:Field,10);
            var collider=im.gameObject.AddComponent<BoxCollider>();collider.size=new Vector3(w,h,3);collider.center=new Vector3(w/2,-h/2,-1.5f);
            var hit=im.gameObject.AddComponent<RelayHit>();hit.background=im;hit.resting=im.color;hit.clicked=action;hit.role=role;
            hit.focus=RoundBox("Focus",im.transform,4,7,4,h-14,primary?Lime:Mint,2).gameObject;hit.focus.SetActive(false);
            BoldLabel("Caption",im.transform,9,2,w-18,h-4,label,23,primary?Card:Light,TextAnchor.MiddleCenter);
            return hit;
        }
        void ClearLayer(ref Transform layer)
        {
            if(layer==null)return;
            layer.gameObject.SetActive(false);Destroy(layer.gameObject);layer=null;
        }
        void Draw()
        {
            if(panel==null)return;
            hovered=null;worldLabels.Clear();ClearLayer(ref content);ClearLayer(ref worldLayer);
            if(ownedPhoto!=null){Destroy(ownedPhoto);ownedPhoto=null;}
            feedbackText=recordCount=recordSpeech=null;
            SyncSourceAudio();
            content=Rect("Screen-"+screen,panel,0,0,W,H);
            RoundBox("Background",content,0,0,W,H,Ink,18);
            DrawHeader();
            switch(screen)
            {
                case ScreenState.Home: DrawHome();break;
                case ScreenState.Consent: if(guideRecording) DrawGuideConsent();else DrawConsent();break;
                case ScreenState.Align: if(guideAlignment!=GuideAlignment.None) DrawGuideAlign();else DrawAlign();break;
                case ScreenState.Recording: if(guideRecording) DrawGuideRecording();else DrawRecording();break;
                case ScreenState.Finishing:
                    Heading("기록 마무리","남은 발화와 장면을 처리하고 있어요. 확보한 기록은 기기에 저장합니다.");
                    break;
                case ScreenState.Review: DrawReview();break;
                case ScreenState.Replay: DrawReplay();break;
                case ScreenState.Quiz: DrawQuiz();break;
                case ScreenState.Result: DrawResult();break;
                case ScreenState.Library: DrawLibrary();break;
                case ScreenState.Transcript: DrawTranscript();break;
                case ScreenState.GuideReview: DrawGuideReview();break;
                case ScreenState.GuidePractice: DrawGuidePractice();DrawGuideStudentMarkers();break;
                case ScreenState.GuideResult: DrawGuideResult();break;
                case ScreenState.Diagnostics: DrawTransferDiagnostics();break;
                case ScreenState.Meeting: DrawMeeting();break;
            }
            Box("FooterRule",content,36,688,W-72,2,Divider);
            feedbackText=Label("Notice",content,36,696,960,52,"",20,Gold);
            if(!xr)Label("RecenterHint",content,1002,699,199,44,"R   화면 맞춤",18,Muted,TextAnchor.MiddleRight);
            else if(screen==ScreenState.Home)
                Label("ControllerHint",content,426,699,775,44,"컨트롤러로 가리킨 뒤 검지 트리거를 누르세요",19,Muted,TextAnchor.MiddleRight);
            RefreshStatus();Physics.SyncTransforms();
        }
        void Heading(string title,string description)
        {
            BoldLabel("Title",content,36,86,1168,60,title,43,Light);
            Label("Description",content,38,147,1164,43,description,23,Muted);
        }
        void DrawHeader()
        {
            RoundBox("Masthead",content,0,0,W,70,Deep,18);
            Box("Masthead base",content,0,45,W,25,Deep);
            // Two circuit pads joined by one trace form the app mark.
            RoundBox("Brand mark",content,36,17,36,36,Lime,9);
            Box("Brand trace",content,47,33,14,3,Deep);
            RoundBox("Brand pad 1",content,43,30,10,10,Deep,5);
            RoundBox("Brand pad 2",content,57,30,10,10,Deep,5);
            BoldLabel("Brand",content,86,13,420,45,screen==ScreenState.Meeting || screen==ScreenState.Home?"ContextPin XR":"ContextPin Trace",28,HeaderText);
            if(guideRecording || guideAlignment!=GuideAlignment.None || screen==ScreenState.GuideReview ||
                screen==ScreenState.GuidePractice || screen==ScreenState.GuideResult)
            {
                Label("Mode",content,730,16,472,38,"회로 실습 가이드",20,HeaderText,TextAnchor.MiddleRight);
                return;
            }
            int step=screen==ScreenState.Consent || screen==ScreenState.Align && !alignForReplay || screen==ScreenState.Recording || screen==ScreenState.Finishing ? 0 :
                screen==ScreenState.Review || screen==ScreenState.Transcript ? 1 :
                screen==ScreenState.Replay || screen==ScreenState.Quiz || screen==ScreenState.Result || screen==ScreenState.Align ? 2 : -1;
            if(step<0)return;
            var stages=new[]{"기록","검토","복습"};
            for(int i=0;i<stages.Length;i++)
            {
                float x=645+i*88;
                if(i==step)RoundBox("CurrentStage",content,x,15,78,40,new Color32(54,87,69,255),9);
                BoldLabel("Stage"+i,content,x,15,78,40,stages[i],20,i==step?Lime:new Color32(198,216,203,255),TextAnchor.MiddleCenter);
            }
            Box("Mode rule",content,923,19,1,32,new Color32(94,125,105,255));
            Label("Mode",content,932,16,270,38,demo?"데모 · 재현 자료":"라이브 · 현장 기록",20,HeaderText,TextAnchor.MiddleRight);
        }
        void MoveToReview(){reviewIndex=0;frameIndex=0;editingLocation=false;notice="";Go(ScreenState.Review);}
    }
}
