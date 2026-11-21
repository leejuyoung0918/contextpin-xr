using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        static readonly Color BoardFace = new Color32(24, 64, 59, 255);
        static readonly Color BoardEdge = new Color32(49, 91, 80, 255);
        static readonly Color Silkscreen = new Color32(232, 244, 232, 255);
        static readonly Color BoardCaption = new Color32(191, 216, 204, 255);
        static readonly Color PadGold = new Color32(243, 206, 130, 255);

        void DrawHome()
        {
            RoundBox("HomeArticle",content,36,103,1168,398,Card,18);
            Label("HomeSubject",content,64,119,559,39,"시제품 설계",23,Mint);
            BoldLabel("Title",content,64,167,542,122,"같은 모델을 보며\n설계를 결정하세요",48,Light);
            Label("Description",content,66,300,520,72,
                "수정한 3D 형태를 비교하고, 결정한 모델과 회의 기록을 함께 남기세요.",25,Muted);
            Button("HomeMeeting",64,403,525,66,"3D 설계 회의",EnterMeeting,true);
            DrawGuideBoard(new GuideStep {kind="led",fromPoint="a-2",toPoint="b-1"},
                609,123,572,267,"a-2","b-1","브레드보드 LED 실습",
                false,null,true);
            Button("HomeGuideExample",609,403,279,66,"LED 예시 시연",OpenGuideExample);
            Button("HomeGuideLive",903,403,278,66,"실물 기록",BeginGuideLiveConsent);

            RoundBox("Saved practice card",content,36,519,1168,143,Card,14);
            BoldLabel("Saved title",content,63,532,539,45,"회로 설명 복습과 저장 기록",28,Light);
            Button("HomeDemo",65,588,192,57,"기존 복습 시연",()=>ChooseMode(true));
            Button("HomeLive",268,588,210,57,"복습용 현장 기록",()=>ChooseMode(false));
            Button("HomeDiagnostics",489,588,188,57,"기기·연결 진단",BeginTransferDiagnostics);
            Button("HomeStudentPractice",689,582,224,64,"학생 실습",()=>{guideStudentLibrary=true;libraryIndex=0;Go(ScreenState.Library);},true);
            Button("Library",925,582,257,64,"기록 재생",()=>{guideStudentLibrary=false;libraryIndex=0;Go(ScreenState.Library);});
        }

        void DrawConsent()
        {
            Heading(demo?"데모 준비":"현장 기록 준비",demo
                ?"세 번의 설명 중 두 곳만 선택하고, 나머지 한 곳을 나중에 찾아봅니다."
                :"참여자와 설명자의 동의를 확인한 뒤 기록을 시작하세요.");
            if(demo)
            {
                TeachStep(36,1,"입력 단자","첫 발화를 듣고 보드의 입력 단자를 선택하세요.");
                TeachStep(432,2,"증폭단 출력","다음 발화에서 증폭단 출력을 선택하세요.");
                TeachStep(828,3,"접지 지점","세 번째 발화는 듣기만 하고 기록을 마치세요. 검토 화면에서 위치를 복구합니다.");
                Label("Input instruction",content,43,466,1140,64,
                    xr?"보드 지점을 머리로 조준한 뒤 핀치하거나 트리거를 누르세요.":"클릭해서 지점을 선택하세요. 설명은 ‘다음 발화’로 넘깁니다.",24,Muted);
            }
            else
            {
                RoundBox("Capture permission card",content,36,206,1168,317,Card,15);
                BoldLabel("Capture source",content,65,235,1098,45,"무엇을 기록하나요?",30,Light);
                Label("Capture scope",content,66,289,1090,83,
                    "음성을 짧게 나누어 전사합니다. 지점을 가리키는 발화나 핀치 주변의 사진은 기기에 보관하고, 음성과 선택한 사진을 설정된 AI 서비스로 보냅니다.",25,Light);
                Box("Setup divider",content,66,388,1090,2,Field);
                Label("Device setup",content,66,404,1085,87,xr
                    ?"실물 보드의 네 지점을 먼저 맞춥니다. 기록을 시작하면 마이크와 헤드셋 카메라 권한을 확인하세요."
                    :"PC에서는 마이크로 기록합니다. 확보한 사진이 없을 때는 발화를 보고 위치를 직접 검토할 수 있어요.",24,Muted);
            }
            Button("Back",36,591,198,70,"돌아가기",Home);
            Button("StartRecording",700,587,504,74,demo?"설명 기록 시작":(xr?"동의하고 보드 맞추기":"동의하고 설명 기록 시작"),StartPressed,true);
        }

        void TeachStep(float x,int sequence,string title,string description)
        {
            RoundBox("Step "+sequence,content,x,207,376,232,Card,14);
            Label("Step order",content,x+25,225,326,37,$"발화 {sequence}",21,Mint);
            BoldLabel("Step target",content,x+25,271,326,53,title,33,Light);
            Label("Step action",content,x+25,339,326,83,description,23,Muted,TextAnchor.UpperLeft);
        }

        void DrawAlign()
        {
            Heading("실제 보드 맞추기","실물 보드를 고정하고, 네 지점의 위치를 차례로 등록하세요.");
            int index=worldPoints.Count;
            for(int i=0;i<board.points.Length;i++)
            {
                var point=board.points[i];bool done=worldPoints.ContainsKey(point.id);
                float x=36+i*296;
                RoundBox("Calibration "+point.code,content,x,205,280,86,i==index?Mint:Field,12);
                BoldLabel("Calibration code",content,x+16,219,86,55,point.code,24,i==index?Color.white:Light);
                Label("Calibration state",content,x+102,219,157,55,done?"등록 완료":i==index?"지금 등록":"대기",22,i==index?Color.white:Muted,TextAnchor.MiddleRight);
            }
            RoundBox("Calibration instruction",content,36,315,1168,232,Card,14);
            BoldLabel("Alignment target",content,65,336,1105,61,index<board.points.Length
                ?board.points[index].label+" 위치를 짚어주세요":"네 지점의 등록이 끝났어요",37,Light);
            Label("Alignment method",content,66,413,1090,109,index<board.points.Length
                ?"오른쪽 컨트롤러의 커서를 지점에 맞추고 A 버튼을 누르세요.\n손 추적은 오른쪽 검지 끝을 지점에 놓고 왼손으로 핀치하세요."
                :"표시가 실물 보드 위에 있는지 확인하세요. 위치가 어긋났다면 다시 맞출 수 있어요.",25,Muted);
            Button("AlignBack",36,592,196,68,"돌아가기",()=>{if(alignForReplay)Go(ScreenState.Review);else Go(ScreenState.Consent);});
            if(index>0)Button("AlignReset",246,592,236,68,"다시 맞추기",()=>{worldPoints.Clear();notice="";Draw();});
            if(index==board.points.Length)Button("AlignDone",760,588,444,72,alignForReplay?"확인하고 복습":"확인하고 기록 시작",ConfirmAlignment,true);
            PlaceWorldMarkers(null,false);
        }

        void DrawRecording()
        {
            Heading("설명 듣기",demo?"발화를 듣고 보드에서 해당 지점을 선택하세요.":"실물 지점을 조준한 뒤 핀치하세요. 위치를 놓친 발화도 나중에 검토할 수 있어요.");
            var marked=demo?TraceData?.notes.Find(n=>n.id=="demo-"+demoIndex)?.pinchRole:null;
            DrawBoard(36,199,575,350,marked);
            RoundBox("Lecture",content,630,199,574,350,Card,14);
            Label("Lecture title",content,654,220,524,42,demo?"교수의 설명":"최근 위치 발화",24,Mint);
            if(demo)Label("Speech progress",content,991,220,186,42,$"{demoIndex+1} / {SampleRoles.Length}",26,Muted,TextAnchor.MiddleRight);
            Box("Quote divider",content,655,276,523,2,Field);
            recordSpeech=BoldLabel("Speech",content,654,292,523,146,"",32,Light);
            recordCount=Label("Count",content,655,466,522,60,"",23,Muted);
            if(demo)
            {
                Button("RepeatDemoSpeech",36,590,239,71,"다시 듣기",RepeatDemoSpeech);
                if(demoIndex<SampleRoles.Length-1)
                    Button("NextDemoSpeech",838,586,366,75,"다음 발화",AdvanceDemo,true);
            }
            bool final=demo && demoIndex==SampleRoles.Length-1;
            bool primary=!demo || final;
            Button("EndRecording",primary?838:578,primary?586:590,primary?366:240,primary?75:71,
                primary?"기록 종료 · 검토하기":"지금 기록 종료",EndRecording,primary);
        }

        void RepeatDemoSpeech()
        {
            if(!demo || screen!=ScreenState.Recording)return;
            var clip=Resources.Load<AudioClip>("TraceDemo/"+SampleRoles[demoIndex]+"_voice");
            if(clip==null){notice="재현 음성을 불러올 수 없습니다.";RefreshStatus();return;}
            narration.Stop();narration.PlayOneShot(clip);
            notice=$"발화 {demoIndex+1}을 다시 재생합니다.";RefreshStatus();
        }

        void RefreshStatus()
        {
            if(feedbackText!=null)feedbackText.text=!string.IsNullOrEmpty(storageError)?storageError:notice;
            if(recordCount==null || TraceData==null)return;
            if(guideRecording)
            {
                var latestSource=TraceData.InOrder().LastOrDefault(note=>!string.IsNullOrEmpty(note.quote));
                recordCount.text=$"발화 {TraceData.notes.Count(note=>!string.IsNullOrEmpty(note.quote))}개 · 마이크 " +
                    (capture.Recording ? (capture.LastClippingRatio>.08f?"입력 과다":capture.LastRms>.007f?"신호 있음":"소리 대기") : "대기 또는 꺼짐");
                recordSpeech.text=latestSource?.quote??"전원 차단, 저항, LED, 접지, 점등 점검을 차례로 설명해 주세요.";
                return;
            }
            int warnings=TraceData.notes.Count(n=>string.IsNullOrEmpty(n.pinchRole)||n.status=="mismatch"||n.status=="unknown");
            int linked=TraceData.notes.Count(n=>!string.IsNullOrEmpty(n.pinchRole));
            recordCount.text=$"핀치 기록 {linked}개     ·     위치 검토 {warnings}개";
            var latest=TraceData.InOrder().LastOrDefault();
            recordSpeech.text=demo?SampleSpeech[demoIndex]:string.IsNullOrEmpty(latest?.transcript)
                ?"위치를 언급한 발화를 기다리고 있어요.":latest.transcript;
        }

        void DrawBoard(float x,float y,float w,float h,string selected)
        {
            if(!demo && xr && aligned)
            {
                RoundBox("PhysicalBoardInfo",content,x,y,w,h,Card,14);
                BoldLabel("PhysicalBoardTitle",content,x+26,y+28,w-52,60,"실물 보드",31,Light);
                Label("PhysicalBoardLabel",content,x+26,y+102,w-52,h-126,screen==ScreenState.Quiz
                    ?"보드 위 네 지점 중 한 곳을 선택하세요.":"보드 위의 지점을 조준하고 핀치하세요.",27,Muted);
                PlaceWorldMarkers(selected,true);return;
            }
            var surface=RoundBox("PracticeBoard",content,x,y,w,h,BoardFace,16).transform;
            BoldLabel("BoardLabel",surface,29,14,w-58,43,"실습 보드",26,Silkscreen);
            Box("Board header divider",surface,28,63,w-56,2,BoardEdge);
            foreach(var p in board.points)
            {
                float bw=w*.405f,bh=Mathf.Clamp(h*.225f,62,90);
                float px=Mathf.Clamp(w*p.x-bw/2,15,w-bw-15);
                float py=Mathf.Clamp(70+(h-83)*p.y-bh/2,74,h-bh-13);
                bool active=selected==p.id && screen!=ScreenState.Quiz;
                var hit=Button("Point-"+p.id,px,py,bw,bh,"",()=>ChooseBoardPoint(p.id),false,surface,p.id);
                hit.background.color=hit.resting=active?new Color32(208,236,210,255):BoardEdge;
                var face=hit.transform;
                float radius=10;
                RoundBox("Metal pad",face,13,bh*.5f-radius,2*radius,2*radius,active?BoardFace:PadGold,radius);
                RoundBox("Pad center",face,18,bh*.5f-5,10,10,active?new Color32(208,236,210,255):BoardFace,5);
                BoldLabel("Point code",face,41,3,bw-48,bh*.47f,p.code,26,active?BoardFace:Silkscreen);
                Label("Point name",face,41,bh*.47f,bw-48,bh*.47f,p.label,21,active?BoardFace:BoardCaption);
            }
        }

        void ChooseBoardPoint(string role)
        {
            if(screen==ScreenState.Recording && !demo && capture.Recording)
            {
                // Acknowledge the gesture immediately. The capture service can replace
                // this message with a storage or permission error during the call.
                notice=board.NameOf(role)+" 핀치 · 관련 발화를 찾고 있어요.";
                RefreshStatus();
            }
            SelectPoint(role);
        }

        void PlaceWorldMarkers(string selected,bool interactive)
        {
            worldLayer=new GameObject("Registered board points").transform;
            foreach(var p in board.points)
            {
                if(!worldPoints.TryGetValue(p.id,out var position))continue;
                var obj=new GameObject("Physical "+p.code,typeof(RectTransform),typeof(Canvas));
                var r=(RectTransform)obj.transform;r.SetParent(worldLayer,false);
                r.position=position;r.sizeDelta=new Vector2(120,80);r.pivot=new Vector2(.5f,.5f);
                r.rotation=Quaternion.LookRotation(r.position-view.transform.position,view.transform.up);
                float nearest=worldPoints.Where(k=>k.Key!=p.id).Select(k=>Vector3.Distance(k.Value,position)).DefaultIfEmpty(.10f).Min();
                r.localScale=Vector3.one*Mathf.Clamp(nearest*.68f/120,.00008f,.00048f);
                obj.GetComponent<Canvas>().renderMode=RenderMode.WorldSpace;worldLabels.Add(r);
                if(interactive)Button("Point-"+p.id,0,0,120,80,p.code,()=>ChooseBoardPoint(p.id),p.id==selected && screen!=ScreenState.Quiz,r,p.id);
                else
                {
                    RoundBox("Registered backing",r,0,0,120,80,BoardFace,12);
                    BoldLabel("Registered",r,0,0,120,80,p.code,35,Silkscreen,TextAnchor.MiddleCenter);
                }
            }
        }
    }
}
