using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        int replayTextPage, transcriptTextPage;
        string lastReplayNote, lastTranscriptKey;

        void SelectReplayNote(int index)
        {
            replayIndex=index;
            replayTextPage=0;
            notice="";
            Draw();
        }

        void DrawReplay()
        {
            var notes=TraceData.ReplayNotes();
            var note=notes[replayIndex];
            if(lastReplayNote!=note.id){lastReplayNote=note.id;replayTextPage=0;}
            Heading("회로 위에서 다시 보기","지점을 누르면 그곳의 설명을, 아래 시간표를 누르면 해당 시점의 설명을 볼 수 있어요.");
            DrawBoard(36,198,514,306,note.finalRole);
            RoundBox("ReplayCard",content,568,198,636,306,Card);
            Label("ReplayOrder",content,592,215,575,30,$"교수의 설명    {When(note.time)}",21,Mint);
            BoldLabel("ReplayRole",content,592,251,580,46,board.NameOf(note.finalRole),34,Light);
            Box("SpeechDivider",content,592,308,582,2,Field);
            string speech=string.IsNullOrWhiteSpace(note.quote)?note.transcript:note.quote;
            int pageCount=DrawReading("ReplaySource",594,320,584,111,speech,27,replayTextPage,out int actualPage);
            replayTextPage=actualPage;
            SourceAudioButton(note,592,446,210,46);
            if(pageCount>1)
                ReaderArrows("ReplayPassage",825,446,351,46,replayTextPage,pageCount,index=>{replayTextPage=index;Draw();});
            DrawReplayTimes(notes);
            Button("BackReview",36,622,150,62,"기록 검토",MoveToReview);
            if(!demo && xr)Button("Realign",196,622,160,62,"보드 맞추기",()=>StartAlignment(true));
            if(TraceData.QuizNote!=null)
                Button("BeginQuiz",854,622,350,62,"안내 끄고 위치 찾기",BeginQuiz,true);
            else Label("NoQuiz",content,603,617,601,68,"기록 검토에서 ‘먼저 확인할 곳’을 지정하면 위치 찾기를 열 수 있어요.",21,Muted,TextAnchor.MiddleRight);
        }

        void DrawReplayTimes(List<TraceNote> notes)
        {
            const int pageSize=3;
            int start=replayIndex/pageSize*pageSize;
            if(notes.Count<=pageSize)
                Label("TimelineLabel",content,36,518,223,79,$"설명 순서   {replayIndex+1} / {notes.Count}",22,Light);
            else
            {
                Label("TimelineLabel",content,36,505,219,33,"설명 순서",20,Muted);
                ReaderArrows("TimelineGroup",36,543,219,53,start/pageSize,(notes.Count+pageSize-1)/pageSize,
                    page=>SelectReplayNote(page*pageSize));
            }
            if(replayIndex>0)
                Button("PrevReplay",268,529,94,63,"이전",()=>SelectReplayNote(replayIndex-1));
            const int itemWidth=225;
            for(int i=start;i<Math.Min(notes.Count,start+pageSize);i++)
            {
                int index=i;var item=notes[index];
                string caption=When(item.time)+"   "+board.NameOf(item.finalRole);
                Button("ReplayStep-"+index,374+(index-start)*(itemWidth+9),529,itemWidth,63,caption,
                    ()=>SelectReplayNote(index),index==replayIndex);
            }
            if(replayIndex<notes.Count-1)
                Button("NextReplay",1086,529,118,63,"다음 설명",()=>SelectReplayNote(replayIndex+1));
        }

        void DrawQuiz()
        {
            Heading("교수가 먼저 확인하라고 한 곳은?","설명을 떠올리며 회로에서 지점 한 곳을 선택하세요.");
            DrawBoard(36,202,754,382,null);
            RoundBox("QuizProgressCard",content,812,202,392,382,Card);
            Label("QuizSelectionTitle",content,841,233,332,39,"이번 시도",24,Muted);
            BoldLabel("QuizCount",content,837,281,335,89,$"{attempt.choices}회",64,Light);
            Box("QuizDivider",content,840,394,334,2,Field);
            Label("QuizHelp",content,841,418,325,113,attempt.choices==0?
                "첫 선택을 기록합니다. 선택 후에도 다시 시도할 수 있어요.":
                "아직 과제가 진행 중이에요. 다른 지점을 선택해 보세요.",26,Muted);
            Button("QuitQuiz",36,617,236,65,"이번 시도 마치기",()=>{
                attempt.elapsedSeconds=Time.realtimeSinceStartup-attemptStart;Save();notice="";Go(ScreenState.Result);});
        }

        void DrawResult()
        {
            int index=TraceData.attempts.IndexOf(attempt);
            Heading("위치 찾기 결과",attempt.completed?"이 시도에서 교수의 설명과 연결된 지점을 찾았어요.":"이 시도는 지점을 찾기 전에 마쳤어요.");
            RoundBox("OutcomeCard",content,36,200,1168,119,Card);
            Label("AttemptLabel",content,62,217,213,76,$"{index+1}번째 시도",24,Muted);
            BoldLabel("FinalChoice",content,280,212,511,88,attempt.completed?"위치 찾기 완료":"미완료",40,attempt.completed?Mint:Light);
            if(index>0)Button("PreviousAttempt",904,231,134,57,"이전 시도",()=>{attempt=TraceData.attempts[index-1];notice="";Draw();});
            if(index<TraceData.attempts.Count-1)Button("NextAttempt",1048,231,134,57,"다음 시도",()=>{attempt=TraceData.attempts[index+1];notice="";Draw();});

            ResultMetric("FirstChoice",36,"첫 선택",attempt.choices==0?"선택 없음":board.NameOf(attempt.firstRole),
                attempt.choices==0?"이 시도에서 고른 지점이 없어요.":attempt.firstCorrect?"첫 선택에서 찾았어요.":"첫 선택은 다른 지점이었어요.");
            ResultMetric("Choices",433,"총 선택",$"{attempt.choices}회",attempt.completed?"마지막 선택   "+board.NameOf(attempt.answerRole):"기록한 선택 횟수");
            ResultMetric("Elapsed",830,"걸린 시간",$"{attempt.elapsedSeconds:0.0}초","과제 시작부터 이번 시도를 마칠 때까지");
            Button("ResultHome",36,614,173,65,"처음으로",Home);
            Button("ResultLibrary",220,614,185,65,"저장된 실습",()=>{libraryIndex=0;Go(ScreenState.Library);});
            if(TraceData.PendingCount==0 && TraceData.ReplayNotes().Count>0)
                Button("ReplayAgain",704,614,242,65,"설명 다시 보기",()=>{if(!demo && xr && !aligned)BeginReplay();else EnterReplay();});
            if(TraceData.QuizNote!=null && (demo || !xr || aligned))
                Button("RetryQuiz",958,614,246,65,"다시 도전",BeginQuiz,true);
        }

        void ResultMetric(string id,float x,string label,string value,string detail)
        {
            RoundBox(id+"Card",content,x,339,374,222,Card);
            Label(id+"Label",content,x+22,359,329,39,label,24,Muted);
            BoldLabel(id,content,x+20,406,331,66,value,37,Light);
            Label(id+"Detail",content,x+22,483,329,57,detail,21,Muted);
        }

        void DrawLibrary()
        {
            var saved=TraceArchive.All();
            if(guideStudentLibrary)saved=saved.Where(s=>s.IsGuide && (s.guide?.approved==true ||
                s.guideRuns.Any(run=>run.guide?.approved==true))).ToList();
            const int perPage=3;
            int pages=Math.Max(1,(saved.Count+perPage-1)/perPage);
            libraryIndex=Mathf.Clamp(libraryIndex,0,pages-1);
            Heading("저장된 실습",saved.Count==0?"기록을 시작하면 이 기기에서 다시 열 수 있어요.":$"이 기기에 저장된 실습 {saved.Count}개 · 최근 기록부터 표시합니다.");
            if(saved.Count==0)
            {
                RoundBox("EmptyLibraryCard",content,36,207,1168,319,Card);
                BoldLabel("EmptyLibrary",content,86,279,1068,70,"저장된 실습이 아직 없어요",35,Light,TextAnchor.MiddleCenter);
                Label("EmptyLibraryHelp",content,86,360,1068,62,"홈에서 예시 회로 데모를 열거나 현장 설명을 기록하세요.",24,Muted,TextAnchor.MiddleCenter);
            }
            for(int i=0;i<perPage && i+libraryIndex*perPage<saved.Count;i++)
            {
                var session=saved[i+libraryIndex*perPage];
                if(session.IsGuide){DrawGuideLibraryRow(session,i);continue;}
                int pending=session.PendingCount, linked=session.ReplayNotes().Count;
                float y=201+i*126;
                RoundBox("SessionRow"+i,content,36,y,1168,111,Card);
                var validDate=DateTimeOffset.TryParse(session.createdUtc,CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,out var date);
                string title=validDate?date.ToLocalTime().ToString("yyyy.MM.dd   HH:mm"):"시간 미기록";
                BoldLabel("SessionDate"+i,content,59,y+12,359,43,title,28,Light);
                Label("SessionStatus"+i,content,59,y+59,430,38,$"{(session.mode=="demo"?"재현 데모":"현장 기록")}   ·   발화 {session.notes.Count}개",21,Muted);
                string status=pending>0?$"검토 대기 {pending}개":linked>0?$"연결 완료 {linked}개":"연결된 지점 없음";
                BoldLabel("SessionReview"+i,content,466,y+15,229,40,status,23,pending>0?Gold:Mint);
                Label("SessionAttempts"+i,content,466,y+58,229,37,$"위치 찾기 {session.attempts.Count}회",20,Muted);
                float right=1181;
                if(session.attempts.Count>0)
                {
                    right-=130;Button("LastResult-"+session.id,right,y+29,130,55,"최근 결과",()=>{
                        OpenSession(session);attempt=session.attempts[session.attempts.Count-1];Go(ScreenState.Result);});
                    right-=10;
                }
                if(pending==0 && linked>0)
                {
                    right-=127;Button("Resume-"+session.id,right,y+29,127,55,"복습하기",()=>{OpenSession(session);BeginReplay();},true);
                    right-=10;
                }
                Button("Saved-"+session.id,right-164,y+29,164,55,pending>0?"검토 이어가기":"기록 열기",()=>{
                    OpenSession(session);
                    int next=session.InOrder().FindIndex(n=>!n.reviewed);if(next>0)MoveReview(next);
                },pending>0);
            }
            Button("LibraryBack",36,616,183,64,"처음으로",Home);
            if(pages>1)ReaderArrows("Saved",803,616,401,64,libraryIndex,pages,index=>{libraryIndex=index;Draw();});
        }

        void DrawTranscript()
        {
            Heading("전체 전사","시간을 선택해 그때의 발화를 읽을 수 있어요.");
            int count=transcriptLines.Count;
            transcriptIndex=Mathf.Clamp(transcriptIndex,0,Math.Max(0,count-1));
            string key=TraceData.id+"/"+transcriptIndex;
            if(key!=lastTranscriptKey){lastTranscriptKey=key;transcriptTextPage=0;}
            if(count==0)
            {
                RoundBox("EmptyTranscriptCard",content,36,205,1168,364,Card);
                BoldLabel("EmptyTranscript",content,68,284,1104,120,"저장된 전사 구간이 없습니다.",32,Muted,TextAnchor.MiddleCenter);
            }
            else
            {
                const int perPage=4;
                int start=transcriptIndex/perPage*perPage;
                RoundBox("TimeIndex",content,36,198,278,398,Card);
                BoldLabel("IndexTitle",content,54,216,243,44,"기록 시각",25,Light);
                for(int i=start;i<Math.Min(count,start+perPage);i++)
                {
                    int idx=i;ReadTranscriptLine(i,out string timestamp,out _);
                    Button("TranscriptSegment-"+i,54,273+(i-start)*61,242,54,timestamp+"    ·    "+(i+1)+"번째",
                        ()=>{transcriptIndex=idx;transcriptTextPage=0;Draw();},idx==transcriptIndex);
                }
                if(count>perPage)
                    ReaderArrows("Transcript",53,529,244,48,transcriptIndex/perPage,(count+perPage-1)/perPage,
                        newPage=>{transcriptIndex=newPage*perPage;transcriptTextPage=0;Draw();});
                RoundBox("TranscriptText",content,333,198,871,398,Card);
                ReadTranscriptLine(transcriptIndex,out string time,out string body);
                BoldLabel("TranscriptTime",content,360,216,814,42,$"{time}    ·    발화 {transcriptIndex+1} / {count}",24,Mint);
                Box("TranscriptRule",content,360,275,815,2,Field);
                int parts=DrawReading("TranscriptPassage",360,295,815,206,body,29,transcriptTextPage,out int actualPage);
                transcriptTextPage=actualPage;
                if(parts>1)
                    ReaderArrows("Passage",803,529,375,48,actualPage,parts,page=>{transcriptTextPage=page;Draw();});
            }
            Button("ReturnTranscript",36,616,232,65,"기록 검토로",()=>{lastTranscriptKey=null;Go(transcriptReturn);});
        }

        void ReadTranscriptLine(int index,out string timestamp,out string body)
        {
            string raw=transcriptLines[index]??"";
            int split=raw.IndexOf("   ",StringComparison.Ordinal);
            timestamp=split<0?"시각 미확인":raw.Substring(0,split);
            body=split<0?raw:raw.Substring(split+3);
        }

        // The font's wrapped line starts preserve both Korean and English text,
        // including long unbroken strings. Only the current page is assigned to the visible label.
        int DrawReading(string id,float x,float y,float w,float h,string source,int size,int page,out int actualPage)
        {
            string body=string.IsNullOrWhiteSpace(source)?"전사한 내용이 없습니다.":source;
            var t=Label(id,content,x,y,w,h,"",size,Light,TextAnchor.UpperLeft);
            t.resizeTextForBestFit=false;
            var settings=t.GetGenerationSettings(new Vector2(w,h));
            settings.resizeTextForBestFit=false;settings.verticalOverflow=VerticalWrapMode.Overflow;
            var starts=new List<int>{0};
            using(var generator=new TextGenerator())
            {
                generator.Populate(body,settings);
                var lines=generator.lines;
                if(lines.Count>1)
                {
                    float lineAdvance=Mathf.Abs(lines[0].topY-lines[1].topY)/Mathf.Max(.01f,settings.scaleFactor);
                    int perPage=Mathf.Max(1,Mathf.FloorToInt((h-3)/Mathf.Max(size*1.10f,lineAdvance)));
                    for(int line=perPage;line<lines.Count;line+=perPage)
                    {
                        int offset=lines[line].startCharIdx;
                        if(offset>starts[starts.Count-1] && offset<body.Length)starts.Add(offset);
                    }
                }
            }
            actualPage=Mathf.Clamp(page,0,starts.Count-1);
            int first=starts[actualPage],end=actualPage+1<starts.Count?starts[actualPage+1]:body.Length;
            t.text=body.Substring(first,end-first).TrimEnd('\r','\n');
            return starts.Count;
        }

        void ReaderArrows(string id,float x,float y,float width,float height,int current,int pages,Action<int> turn)
        {
            float arrow=Mathf.Min(95,width*.29f);
            if(current>0)Button("Prev"+id,x,y,arrow,height,"이전",()=>turn(current-1));
            Label(id+"Page",content,x+arrow,y,width-2*arrow,height,$"{current+1} / {pages}",20,Muted,TextAnchor.MiddleCenter);
            if(current+1<pages)Button("Next"+id,x+width-arrow,y,arrow,height,"다음",()=>turn(current+1));
        }
    }
}
