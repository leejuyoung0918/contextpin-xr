using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ContextPin
{
    [Serializable]
    public sealed class TracePoint
    {
        public string id, label, code;
        public float x, y;
    }

    [Serializable]
    public sealed class TraceBoard
    {
        public TracePoint[] points;
        public string NameOf(string id) => points.FirstOrDefault(p => p.id == id)?.label ?? "위치 미확인";
        public bool Contains(string id) => points.Any(p => p.id == id);
    }

    [Serializable]
    public sealed class TraceNote
    {
        public string id, transcript, pinchRole, candidateRole, reason, quote, status;
        public string audioFile;
        public float time;
        public string[] framePaths = Array.Empty<string>();
        public string[] pinMarks = Array.Empty<string>();
        public int revision;
        public string sourceMode, provider, providerItemId, normalizedText, author, audioSha256, supersedesId;
        public string[] captureIds = Array.Empty<string>();
        // An empty final role may mean deliberately unresolved. Keep that decision distinct from pending.
        public bool reviewed;
        public string finalRole = "";
    }

    [Serializable]
    public sealed class TraceAttempt
    {
        public string startedUtc, firstRole, answerRole;
        public bool firstCorrect, completed;
        public int choices;
        public float elapsedSeconds;
    }

    [Serializable]
    public sealed class TraceSession
    {
        public string id, createdUtc, mode, status, lastCaptureMessage;
        public List<TraceNote> notes = new List<TraceNote>();
        public List<TraceAttempt> attempts = new List<TraceAttempt>();
        public string quizNoteId;
        public LabGuide guide;
        public List<GuideRun> guideRuns = new List<GuideRun>();
        public int schemaVersion = 2, relayRevision;
        public bool transferConsent;
        public string transferMode, transferBoardId, latestCheckpoint;
        public long microphoneOffsetMs;
        public int shutterRequests, photoReadbacks, failedPhotos;
        public List<TransferCapture> captures = new List<TransferCapture>();
        public List<BoardRegistration> registrations = new List<BoardRegistration>();
        public List<LabGuide> approvedGuides = new List<LabGuide>();
        public List<TransferCommandReceipt> commandReceipts = new List<TransferCommandReceipt>();
        public TransferHelp recentHelp;
        public bool IsGuide => mode == "guide-live" || mode == "guide-example";
        public TraceNote QuizNote => notes.Find(n => n.id == quizNoteId && n.reviewed && !string.IsNullOrEmpty(n.finalRole));
        public List<TraceNote> InOrder() => notes.OrderBy(n => n.time).ThenBy(n => n.id,StringComparer.Ordinal).ToList();
        public List<TraceNote> ReplayNotes() => InOrder().Where(n => n.reviewed && !string.IsNullOrEmpty(n.finalRole)).ToList();
        public int PendingCount => notes.Count(n => !n.reviewed);
        public static TraceSession New(bool demo) => new TraceSession
        {
            id=Guid.NewGuid().ToString("N"), createdUtc=DateTime.UtcNow.ToString("o"),
            mode=demo?"demo":"live", status="recording"
        };
    }

    public static class TraceArchive
    {
        public static string Root => Path.Combine(
            Application.isEditor && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RELAY_TEST_DATA"))
                ? Environment.GetEnvironmentVariable("RELAY_TEST_DATA") : Application.persistentDataPath,"Trace");
        public static string Folder(TraceSession session) => Path.Combine(Root,session.id);
        public static void Save(TraceSession session)
        {
            var folder=Folder(session);
            Directory.CreateDirectory(folder);
            var path=Path.Combine(folder,"session.json");
            var temporary=path+".new";
            File.WriteAllText(temporary,JsonUtility.ToJson(session,true));
            if(File.Exists(path)) File.Replace(temporary,path,path+".previous");
            else File.Move(temporary,path);
        }

        public static List<TraceSession> All()
        {
            var sessions=new List<TraceSession>();
            if(!Directory.Exists(Root)) return sessions;
            foreach(var folder in Directory.GetDirectories(Root))
            {
                foreach(var file in new[]{"session.json","session.json.previous","session.json.new"})
                {
                    var path=Path.Combine(folder,file);
                    if(!File.Exists(path)) continue;
                    try
                    {
                        var s=JsonUtility.FromJson<TraceSession>(File.ReadAllText(path));
                        if(s==null || s.id!=Path.GetFileName(folder) || s.notes==null || s.attempts==null) continue;
                        if(s.guideRuns==null)s.guideRuns=new List<GuideRun>();
                        s.captures??=new List<TransferCapture>();
                        s.registrations??=new List<BoardRegistration>();
                        s.approvedGuides??=new List<LabGuide>();
                        s.commandReceipts??=new List<TransferCommandReceipt>();
                        sessions.Add(s);
                        break;
                    }
                    catch(Exception e){Debug.LogWarning("Trace archive: "+e.Message);}
                }
            }
            return sessions.OrderByDescending(s => s.createdUtc,StringComparer.Ordinal).ToList();
        }
    }
}
