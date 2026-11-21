using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace ContextPin
{
    public static class RelayStore
    {
        public static string Root => Application.isEditor && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RELAY_TEST_DATA"))
            ? Environment.GetEnvironmentVariable("RELAY_TEST_DATA") : Path.Combine(Application.persistentDataPath,"Relay");
        public static string SessionPath(SessionData data) => Path.Combine(Root,data.metrics.sessionId);
        public static SessionData Load()
        {
            try
            {
                var path=Path.Combine(Root,"active.json");
                if(File.Exists(path))
                {
                    var data=JsonUtility.FromJson<SessionData>(File.ReadAllText(path));
                    if(data?.metrics!=null && data.cards!=null) return data;
                }
            }
            catch(Exception e){Debug.LogWarning("Session could not be restored: "+e.Message);}
            return new SessionData();
        }
        public static void Save(SessionData data)
        {
            Directory.CreateDirectory(SessionPath(data));
            var json=JsonUtility.ToJson(data,true);
            WriteAtomic(Path.Combine(SessionPath(data),"session.json"),json);
            WriteAtomic(Path.Combine(Root,"active.json"),json);
        }
        static void WriteAtomic(string path,string text)
        {
            File.WriteAllText(path+".tmp",text,Encoding.UTF8);
            File.Copy(path+".tmp",path,true);File.Delete(path+".tmp");
        }
        public static string SaveImage(SessionData data,byte[] bytes)
        {
            Directory.CreateDirectory(SessionPath(data));
            var path=Path.Combine(SessionPath(data),Guid.NewGuid().ToString("N")+".jpg");
            File.WriteAllBytes(path,bytes);return path;
        }
        static string Csv(string v)=>"\""+(v??"").Replace("\"","\"\"")+"\"";
        public static string Export(SessionData data)
        {
            Save(data);
            var b=new StringBuilder("session,card,provider,participant,condition,board,selected_role,success,seconds,verification,observed_state,feedback\n");
            foreach(var card in data.cards)foreach(var o in card.outcomes)
                b.AppendLine(string.Join(",",Csv(data.metrics.sessionId),Csv(card.id),Csv(card.provider),Csv(o.participant),Csv(o.condition),
                    Csv(o.boardId),Csv(o.selectedRole),o.success?"1":"0",o.elapsedSeconds.ToString("F3",CultureInfo.InvariantCulture),Csv(o.verification),Csv(o.observedState),Csv(o.feedback)));
            var path=Path.Combine(SessionPath(data),"outcomes.csv");File.WriteAllText(path,b.ToString(),Encoding.UTF8);return path;
        }
    }
}
