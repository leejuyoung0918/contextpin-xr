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
        TraceNote expandedPhoto, expandedSource, expandedReason;
        bool photoMarks = true;
        int sourcePage;
        static readonly Color PhotoMat = new Color32(26, 43, 42, 255);
        static readonly Color PhotoPin = new Color32(88, 234, 210, 255);
        static readonly Color PhotoCandidate = new Color32(255, 203, 108, 255);

        void DrawReview()
        {
            var note = CurrentNote();
            if (note == null)
            {
                Heading("기록 검토", "연결할 위치가 있는 설명을 찾지 못했어요. 전체 전사에서 저장한 내용을 볼 수 있어요.");
                Button("ReviewHome",36,610,210,64,"처음으로",Home);
                Button("FullTranscript",922,610,282,64,"전체 전사 보기",ViewTranscript,true);
                return;
            }
            if (expandedPhoto == note) { DrawExpandedPhoto(note); return; }
            if (expandedSource == note || expandedReason == note) { DrawExpandedSource(note,expandedReason==note); return; }

            var total = TraceData.notes.Count;
            BoldLabel("Title",content,36,87,470,58,"기록 검토",43,Light);
            Label("ReviewPosition",content,505,94,380,44,$"설명 {reviewIndex+1} / {total}    ·    {When(note.time)}",23,Muted,TextAnchor.MiddleRight);
            var verified = total - TraceData.PendingCount;
            BoldLabel("ReviewCount",content,906,91,296,36,$"검토 완료   {verified} / {total}",21,Mint,TextAnchor.MiddleRight);
            RoundBox("ReviewTrack",content,969,132,233,7,Field,3);
            if (verified>0) RoundBox("ReviewFill",content,969,132,233f*verified/total,7,Mint,3);
            DrawPhoto(note);
            DrawReviewSource(note);
            DrawDecisionRow(note);
            DrawReviewNavigation(total);
        }

        static string ReviewQuote(TraceNote n)
        {
            if (!string.IsNullOrWhiteSpace(n.quote)) return n.quote.Trim();
            return (n.transcript ?? "").Trim();
        }

        static string PreviewText(string text, int max)
        {
            if (text.Length<=max && text.Count(c=>c=='\n')<=2) return text;
            var oneLine = text.Replace('\n',' ').Replace('\r',' ');
            int length = Mathf.Min(max,oneLine.Length);
            if (length<oneLine.Length && length>0 && char.IsHighSurrogate(oneLine[length-1])) length--;
            return oneLine.Substring(0,length).TrimEnd()+(length<oneLine.Length?"…":"");
        }

        void DrawReviewSource(TraceNote n)
        {
            RoundBox("Source card",content,679,159,525,373,Card);
            Label("Quote label",content,699,174,270,40,"교수의 발화",23,Muted);
            SourceAudioButton(n,997,170,188,48);
            string quote=ReviewQuote(n);
            string preview=PreviewText(quote,58);
            bool hasMore=preview!=quote || (!string.IsNullOrWhiteSpace(n.transcript) && n.transcript.Trim()!=quote);
            BoldLabel("OriginalSpeech",content,699,220,485,hasMore?78:100,quote.Length==0?"전사된 문장이 없어요.":preview,28,Light);
            if(hasMore) Button("ReadReviewSource",700,299,190,39,"원문 전체 읽기",()=>{sourcePage=0;expandedSource=n;Draw();});
            Box("Source divider",content,700,344,484,2,Field);
            Label("Pin label",content,699,352,226,29,"학생의 핀치",19,Muted);
            Label("Candidate label",content,950,352,234,29,"검토할 후보",19,Muted);
            BoldLabel("OriginalPin",content,699,384,237,43,board.Contains(n.pinchRole)?board.NameOf(n.pinchRole):"핀치 없음",26,Light);
            BoldLabel("Candidate",content,950,384,235,43,board.Contains(n.candidateRole)?board.NameOf(n.candidateRole):"위치 미확인",26,Gold);
            string status = n.reviewed ? (board.Contains(n.finalRole)?"연결한 위치  ·  "+board.NameOf(n.finalRole):"위치 미확인으로 보관") :
                n.status=="mismatch"?"핀치와 후보가 달라요" :
                string.IsNullOrEmpty(n.pinchRole)?"핀치 없이 기록된 발화" :
                n.status=="matched"?"핀치와 후보가 일치해요":"위치를 확인해 주세요";
            bool locationKnown = n.reviewed ? board.Contains(n.finalRole) : n.status=="matched";
            RoundBox("Decision status",content,695,436,493,82,locationKnown?new Color32(225,240,233,255):new Color32(250,239,216,255),9);
            var reason=n.reason??"";
            var excerpt=PreviewText(reason,32);
            bool longReason=excerpt!=reason;
            BoldLabel("ReviewStatus",content,708,440,longReason?325:467,32,status,21,locationKnown?Mint:Gold);
            if(longReason)
                Button("ReadReviewReason",1041,437,132,34,"근거 전체",()=>{sourcePage=0;expandedReason=n;Draw();});
            Label("Reason",content,708,470,467,46,excerpt,19,Light);
        }

        void DrawDecisionRow(TraceNote n)
        {
            var actions = new List<(string id,string name,Action act,bool primary)>();
            if(editingLocation)
            {
                foreach(var point in board.points)
                    actions.Add(("Choose-"+point.id,point.label,()=>Decide(point.id),false));
                actions.Add(("CancelEdit","취소",()=>{editingLocation=false;Draw();},false));
            }
            else
            {
                if(board.Contains(n.candidateRole))
                    actions.Add(("UseCandidate",board.NameOf(n.candidateRole)+" 연결",()=>Decide(n.candidateRole),true));
                if(board.Contains(n.pinchRole)&&n.pinchRole!=n.candidateRole)
                    actions.Add(("KeepPin","원래 핀치 유지",()=>Decide(n.pinchRole),false));
                actions.Add(("EditLocation","다른 위치 선택",()=>{editingLocation=true;Draw();},!board.Contains(n.candidateRole)));
                actions.Add(("MarkUnknown","위치 미확인",()=>Decide(""),false));
                if(!demo)actions.Add(("SetQuizReference",TraceData.quizNoteId==n.id||useForQuiz?"과제 지점 해제":"과제 지점 지정",()=>ToggleLiveQuizReference(n),false));
            }
            float w=(1168f-12*(actions.Count-1))/actions.Count;
            for(int i=0;i<actions.Count;i++)
            {
                var action=actions[i];
                Button(action.id,36+i*(w+12),548,w,64,action.name,action.act,action.primary);
            }
        }

        void DrawReviewNavigation(int total)
        {
            if(reviewIndex>0) Button("PrevReview",36,632,139,52,"이전 설명",()=>MoveReview(reviewIndex-1));
            if(reviewIndex+1<total) Button("NextReview",184,632,139,52,"다음 설명",()=>MoveReview(reviewIndex+1));
            Button("FullTranscript",335,632,165,52,"전체 전사",ViewTranscript);
            Button("ReviewHome",511,632,150,52,"처음으로",Home);
            if(TraceData.PendingCount==0)
            {
                if(TraceData.ReplayNotes().Count>0) Button("BeginReplay",831,626,373,62,"학생 B 복습 시작",BeginReplay,true);
                else Label("ReviewProgress",content,728,636,476,43,"복습할 위치를 하나 이상 연결해 주세요.",21,Muted,TextAnchor.MiddleRight);
            }
            else
            {
                var ordered=TraceData.InOrder();
                int next=-1;
                for(int i=1;i<total;i++)
                {
                    int target=(reviewIndex+i)%total;
                    if(!ordered[target].reviewed){next=target;break;}
                }
                if(next>=0) Button("NextPending",873,632,331,52,$"미검토 설명 보기  ·  {TraceData.PendingCount}개",()=>MoveReview(next));
                else Label("ReviewProgress",content,739,636,465,43,"이 설명을 검토하면 복습을 시작할 수 있어요.",21,Muted,TextAnchor.MiddleRight);
            }
        }

        void DrawPhoto(TraceNote note)
        {
            RoundBox("EvidenceBack",content,36,159,627,373,Card);
            PaintFrame(note,47,170,605,292,true);
            int count=note.framePaths?.Length??0;
            if(count==0)return;
            DrawFrameNavigation(count,49,471,408);
            Button("ExpandEvidence",467,471,180,52,"크게 보기",()=>{photoMarks=true;expandedPhoto=note;Draw();});
        }

        void DrawExpandedPhoto(TraceNote note)
        {
            BoldLabel("Title",content,36,86,660,59,"장면 크게 보기",42,Light);
            Button("CloseEvidence",951,90,253,53,"검토로 돌아가기",()=>{expandedPhoto=null;Draw();},true);
            PaintFrame(note,36,158,824,448,photoMarks);
            RoundBox("Photo context",content,878,158,326,448,Card);
            string timing=$"설명 {reviewIndex+1}    ·    {When(note.time)}";
            if(!demo && frameInfo.TryGetValue(note.framePaths[frameIndex],out var timedFrame))
                timing=$"발화 {When(note.time)}   ·   촬영 {When(timedFrame.time)}";
            Label("Photo time",content,898,180,286,44,timing,22,Mint);
            BoldLabel("Photo quote",content,898,234,286,142,PreviewText(ReviewQuote(note),63),26,Light);
            Box("Photo divider",content,898,386,286,2,Field);
            Label("Photo candidate label",content,898,397,280,36,"검토할 위치",21,Muted);
            BoldLabel("Photo candidate value",content,898,435,281,52,board.NameOf(note.candidateRole),28,Gold);
            Label("Photo reason",content,898,491,281,101,PreviewText(note.reason??"",76),20,Muted);
            if(!demo && frameInfo.TryGetValue(note.framePaths[frameIndex],out var frame) && frame.points!=null &&
                frame.points.Any(p=>p!=null && board.Contains(p.role) && (p.role==note.candidateRole || p.role==note.pinchRole)))
                Button("TogglePhotoMarks",898,628,286,52,photoMarks?"위치 표시 숨기기":"위치 표시 보기",()=>{photoMarks=!photoMarks;Draw();});
            DrawFrameNavigation(note.framePaths.Length,164,628,572);
        }

        void DrawFrameNavigation(int count,float x,float y,float w)
        {
            float arrow=w<450?96:132;
            if(count>1)
            {
                Button("PreviousFrame",x,y,arrow,52,"이전",()=>{frameIndex=(frameIndex+count-1)%count;Draw();});
                Button("NextFrame",x+w-arrow,y,arrow,52,"다음",()=>{frameIndex=(frameIndex+1)%count;Draw();});
            }
            var text=(demo?"재현 장면":"촬영 장면")+$"  {frameIndex+1} / {count}";
            BoldLabel("FrameNumber",content,x+arrow,y,w-2*arrow,52,text,21,Light,TextAnchor.MiddleCenter);
        }

        void PaintFrame(TraceNote note,float x,float y,float w,float h,bool showMarks)
        {
            RoundBox("Photo well",content,x,y,w,h,PhotoMat,8);
            int count=note.framePaths?.Length??0;
            frameIndex=Mathf.Clamp(frameIndex,0,Mathf.Max(0,count-1));
            string path=count>0?note.framePaths[frameIndex]:null;
            Texture2D tex=LoadEvidenceTexture(path);
            if(tex==null)
            {
                Label(count==0?"NoImage":"UnavailableImage",content,x+24,y+12,w-48,h-24,
                    count==0?"이 발화에 저장된 사진이 없어요.":"이 장면 파일을 열 수 없어요.",24,Color.white,TextAnchor.MiddleCenter);
                return;
            }
            float scale=Mathf.Min((w-6)/tex.width,(h-6)/tex.height);
            float width=tex.width*scale,height=tex.height*scale;
            var image=Rect("Evidence image",content,x+(w-width)/2,y+(h-height)/2,width,height).gameObject.AddComponent<RawImage>();
            image.texture=tex;image.raycastTarget=false;
            if(!showMarks||!frameInfo.TryGetValue(path,out var f)||f.points==null)return;
            foreach(var p in f.points)
            {
                if(p==null || !board.Contains(p.role))continue;
                bool candidate=p.role==note.candidateRole;
                if(!candidate && p.role!=note.pinchRole)continue;
                float px=Mathf.Clamp(p.x*width,8,width-8),py=Mathf.Clamp(p.y*height,8,height-8);
                var c=candidate?PhotoCandidate:PhotoPin;
                Box("Point horizontal edge",image.transform,px-8,py-3,16,6,PhotoMat);
                Box("Point vertical edge",image.transform,px-3,py-8,6,16,PhotoMat);
                Box("Point horizontal",image.transform,px-7,py-1,14,2,c);
                Box("Point vertical",image.transform,px-1,py-7,2,14,c);
                if(expandedPhoto!=note)continue;
                float left=Mathf.Clamp(px+9,0,width-105),top=Mathf.Clamp(py-31,0,height-29);
                var tag=RoundBox("Photo legend",image.transform,left,top,105,29,PhotoMat,4);
                Label("Photo point",tag.transform,3,0,99,29,(candidate?"후보 ":"핀치 ")+board.points.First(b=>b.id==p.role).code,17,c,TextAnchor.MiddleCenter);
            }
        }

        Texture2D LoadEvidenceTexture(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                if (path.StartsWith("res:", StringComparison.Ordinal)) return Resources.Load<Texture2D>(path.Substring(4));
                string local = Path.Combine(TraceArchive.Folder(TraceData), "evidence", Path.GetFileName(path));
                string source = File.Exists(local) ? local : path;
                if (!File.Exists(source)) return null;
                ownedPhoto = new Texture2D(2, 2, TextureFormat.RGB24, false);
                return ownedPhoto.LoadImage(File.ReadAllBytes(source)) ? ownedPhoto : null;
            }
            catch (Exception error)
            {
                Debug.LogWarning("Evidence image: " + error.Message);
                return null;
            }
        }

        void DrawExpandedSource(TraceNote note,bool reason)
        {
            BoldLabel("Title",content,36,87,690,59,reason?"후보를 제안한 근거":"발화 원문",42,Light);
            Button("CloseReviewSource",950,90,254,52,"검토로 돌아가기",()=>{expandedSource=null;expandedReason=null;Draw();},true);
            RoundBox("Source page",content,36,160,1168,443,Card);
            Label("Source time",content,70,185,638,45,$"설명 {reviewIndex+1}    ·    {When(note.time)}",23,Mint);
            SourceAudioButton(note,966,184,204,50);
            var text=reason?note.reason:string.IsNullOrWhiteSpace(note.transcript)?ReviewQuote(note):note.transcript;
            int pages=DrawReading("CompleteSource",70,257,1097,314,text,30,sourcePage,out int actualPage);
            sourcePage=actualPage;
            if(pages>1)
            {
                if(sourcePage>0)Button("PrevReviewSource",36,625,193,56,"이전 쪽",()=>{sourcePage--;Draw();});
                if(sourcePage+1<pages)Button("NextReviewSource",1006,625,198,56,"다음 쪽",()=>{sourcePage++;Draw();});
                Label("SourcePage",content,364,625,512,56,$"{sourcePage+1} / {pages}",23,Muted,TextAnchor.MiddleCenter);
            }
        }
    }
}
