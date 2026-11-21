using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        AudioSource sourcePlayer;
        AudioClip localSourceClip;
        Coroutine sourceLoad;
        string audioNoteKey, sourceCaption, archiveAudioId;
        Text sourceButtonText;
        bool sourcePlaying;
        readonly List<TraceTranscript> archivedAudio=new List<TraceTranscript>();

        string LocalWave(string name)
        {
            if(string.IsNullOrEmpty(name)||!string.Equals(Path.GetExtension(name),".wav",StringComparison.OrdinalIgnoreCase))return null;
            string path=Path.Combine(TraceArchive.Folder(TraceData),"evidence",Path.GetFileName(name));
            return File.Exists(path)?path:null;
        }

        string SourceOf(TraceNote note)
        {
            if(note==null)return null;
            if(TraceData?.mode=="guide-example")
                return ViewGuide?.steps?.FirstOrDefault(step=>"led-demo-"+step.kind==note.id) is GuideStep sample
                    ? "res:TraceGuideDemo/"+sample.kind : null;
            if(demo)
            {
                for(int i=0;i<SampleRoles.Length;i++)
                    if(note.id=="demo-"+i)return "res:TraceDemo/"+SampleRoles[i]+"_voice";
                return null;
            }
            if(!string.IsNullOrEmpty(note.audioFile))return LocalWave(note.audioFile);
            // For older sessions, the transcript journal can identify the clip
            // only when a single nearby chunk contains the exact quotation.
            if(string.IsNullOrWhiteSpace(note.quote))return null;
            if(archiveAudioId!=TraceData.id)
            {
                archiveAudioId=TraceData.id;archivedAudio.Clear();
                var journal=Path.Combine(TraceArchive.Folder(TraceData),"evidence","transcript.jsonl");
                if(File.Exists(journal))
                {
                    try
                    {
                        foreach(var line in File.ReadLines(journal))
                        {
                            try { var chunk=JsonUtility.FromJson<TraceTranscript>(line); if(chunk!=null)archivedAudio.Add(chunk); }
                            catch(ArgumentException){ /* Incomplete journal entry. */ }
                        }
                    }
                    catch(IOException e){Debug.LogWarning("Audio journal unavailable: "+e.Message);}
                    catch(UnauthorizedAccessException e){Debug.LogWarning("Audio journal unavailable: "+e.Message);}
                }
            }
            var nearby=archivedAudio.Where(c=>c.start-1.25f<=note.time && c.end+1.25f>=note.time && c.transcript?.Contains(note.quote)==true).Take(2).ToArray();
            return nearby.Length==1?LocalWave(nearby[0].audioPath):null;
        }

        void SourceAudioButton(TraceNote note,float x,float y,float w,float h,
            string id="PlaySourceAudio",string caption=null,Action onPlay=null)
        {
            var source=SourceOf(note);
            if(source==null)return;
            sourceCaption=caption??(demo?"재현 음성 듣기":"현장 음성 듣기");
            var control=Button(id,x,y,w,h,sourcePlaying?"재생 중지":sourceCaption,()=>
            {
                if(sourcePlaying){StopSourceAudio();return;}
                onPlay?.Invoke();
                PlaySource(source);
            });
            sourceButtonText=control.GetComponentInChildren<Text>();
        }

        void PlaySource(string source)
        {
            StopSourceAudio();
            if(sourcePlayer==null){sourcePlayer=gameObject.AddComponent<AudioSource>();sourcePlayer.playOnAwake=false;sourcePlayer.spatialBlend=0;}
            sourcePlaying=true;
            if(sourceButtonText!=null)sourceButtonText.text="재생 중지";
            if(source.StartsWith("res:",StringComparison.Ordinal))
                StartSourceClip(Resources.Load<AudioClip>(source.Substring(4)));
            else sourceLoad=StartCoroutine(ReadSourceWave(source));
        }

        IEnumerator ReadSourceWave(string path)
        {
            // Captured audio stays on the device during replay.
            using(var request=UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri,AudioType.WAV))
            {
                yield return request.SendWebRequest();
                sourceLoad=null;
                if(request.result==UnityWebRequest.Result.Success)
                {
                    try { localSourceClip=DownloadHandlerAudioClip.GetContent(request); }
                    catch(Exception e){Debug.LogWarning("Saved audio could not be decoded: "+e.Message);}
                }
                StartSourceClip(localSourceClip);
            }
        }
        void StartSourceClip(AudioClip clip)
        {
            if(clip!=null){sourcePlayer.clip=clip;sourcePlayer.Play();return;}
            StopSourceAudio();
            notice="이 구간의 음성을 재생할 수 없습니다.";RefreshStatus();
        }
        void UpdateSourceAudio()
        {
            if(sourcePlaying && sourceLoad==null && sourcePlayer!=null && !sourcePlayer.isPlaying)StopSourceAudio();
        }
        void StopSourceAudio()
        {
            if(sourceLoad!=null){StopCoroutine(sourceLoad);sourceLoad=null;}
            if(sourcePlayer!=null){sourcePlayer.Stop();sourcePlayer.clip=null;}
            if(localSourceClip!=null){Destroy(localSourceClip);localSourceClip=null;}
            sourcePlaying=false;
            if(sourceButtonText!=null)sourceButtonText.text=sourceCaption;
        }
        void SyncSourceAudio()
        {
            TraceNote active=null;
            if(screen==ScreenState.Review && TraceData!=null)active=CurrentNote();
            if(screen==ScreenState.Replay && TraceData!=null)
            {
                var notes=TraceData.ReplayNotes();
                if(replayIndex>=0 && replayIndex<notes.Count)active=notes[replayIndex];
            }
            if(screen==ScreenState.GuideReview)active=GuideEvidenceNote(CurrentGuideReviewStep);
            if(screen==ScreenState.GuidePractice)active=GuideEvidenceNote(GuideRules.Current(guideRun));
            var key=active==null?null:TraceData.id+"/"+screen+"/"+active.id;
            if(key!=audioNoteKey)StopSourceAudio();
            audioNoteKey=key;sourceButtonText=null;
        }
    }
}
