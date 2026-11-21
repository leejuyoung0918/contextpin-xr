using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace ContextPin.Editor
{
    // Inject at the sealed microphone-window boundary. Exercise the actual ASR
    // queue, phrase splitting, cross-window pinch matching, and end-of-class flush.
    // Reflection is confined to the editor fixture so the device capture component
    // exposes no API for injecting transcripts, pins, or photographs.
    public static class TraceCorrelationCheck
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly Type Capture = typeof(TraceCapture);

        public static void RunChecks()
        {
            RunScenario(true);
            RunScenario(false);
            RunGuideScenario();
        }

        static void RunScenario(bool cameraAvailable)
        {
            string dir = Path.Combine(Application.temporaryCachePath, "trace-correlation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var host = new GameObject("Trace correlation test");
            try
            {
                var capture = host.AddComponent<TraceCapture>();
                var emitted = new List<TraceCaptureEvent>();
                Set(capture, "evidenceDir", dir);
                Set(capture, "emit", new Action<TraceCaptureEvent>(emitted.Add));
                // Keep evidence jobs pending. Never start a network request.
                Set(capture, "reviewWorking", true);
                Set(capture, "stopping", true);
                var frameTimes = new Dictionary<string, float>();
                byte[] fixture = cameraAvailable ? SmallJpeg() : null;

                string first = "여기 입력을 보세요.";
                string repeated = cameraAvailable ? "다시 여기를 보세요." : "다시 여기를 보고";
                string missed = "마지막으로 여기의 접지를 확인하세요.";
                AddWindow(capture, dir, 0, 2, first, fixture, frameTimes);
                // This pinch is 120ms after the utterance, in the next silent window.
                AddWindow(capture, dir, 2, 3, "", fixture, frameTimes, ("input", 2.12f));
                // A second pinch can refer to the very same point.
                if(cameraAvailable)
                {
                    AddWindow(capture, dir, 3, 5, repeated, fixture, frameTimes, ("input", 4f));
                    AddWindow(capture, dir, 5, 8, missed, fixture, frameTimes);
                }
                else
                    AddWindow(capture, dir, 3, 8, repeated + ", " + missed, fixture, frameTimes, ("input", 4f));
                // The final speech event only materializes when the last ASR window drains.
                TranscribeWithoutIo(capture);

                Require(emitted.Select(e => e.quote).SequenceEqual(new[] { first, repeated, missed }),
                    "Expected three ordered original location phrases, including the final unpinched cue");
                Require(emitted.Count(e => e.pinchRole == "input") == 2 && emitted[0].time == 2.12f,
                    "Late pinch or repeated reference was lost or duplicated");
                var pending = emitted[2];
                Require(pending.pinchRole == "" && pending.candidateRole == "" && pending.status == "unknown",
                    "Unpinched source must survive without a fabricated candidate");
                var finalChunk=capture.Transcripts.Last();
                Require(capture.Transcripts.Count == (cameraAvailable?4:3) && finalChunk.transcript.Contains(missed),
                    "The speech timeline lost the final original excerpt");
                Require(!string.IsNullOrEmpty(pending.audioFile) && pending.audioFile==Path.GetFileName(finalChunk.audioPath)
                        && File.Exists(Path.Combine(dir, pending.audioFile)),
                    "The final unpinched phrase must retain its own playable audio chunk across a timeline flush");
                var jobs = (ICollection)Get(capture, "reviewQueue");
                Require(jobs.Count == 3, "Unpinched speech did not enqueue a review");
                if (!cameraAvailable)
                {
                    Require(emitted.All(e => e.framePaths.Length == 0), "Missing camera produced invented image paths");
                    return;
                }
                Require(pending.framePaths.Length == 3 && pending.framePaths.Distinct().Count() == 3,
                    "Unpinched event lost distinct supporting photographs");
                Require(pending.framePaths.All(File.Exists), "Flush removed evidence still referenced by the unpinched event");
                var times = pending.framePaths.Select(p => frameTimes[p]).ToArray();
                Require(times.First() < pending.time && times.Last() > pending.time &&
                        times.SequenceEqual(times.OrderBy(t => t)),
                    "Camera evidence must straddle speech time and retain chronology");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                Directory.Delete(dir, true); // unique directory owned by this run
            }
        }

        static void RunGuideScenario()
        {
            string dir = Path.Combine(Application.temporaryCachePath, "guide-correlation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var host = new GameObject("Guide correlation test");
            try
            {
                var capture = host.AddComponent<TraceCapture>();
                var emitted = new List<TraceCaptureEvent>();
                Set(capture, "evidenceDir", dir);
                Set(capture, "emit", new Action<TraceCaptureEvent>(emitted.Add));
                Set(capture, "guideRoles", GuideBoard.Points.Select(point => point.id).ToArray());
                Set(capture, "reviewWorking", true);
                Set(capture, "stopping", true);
                var times = new Dictionary<string, float>();
                byte[] jpeg = SmallJpeg();

                string prep = "시작하기 전에 전원을 끄고 준비물을 확인하세요.";
                string wiring = "저항 양끝을 각각 꽂으세요.";
                AddWindow(capture, dir, 0, 2, prep, jpeg, times);
                AddWindow(capture, dir, 2, 5, wiring, jpeg, times, ("plus-1", 2.8f), ("a-1", 3.9f));
                TranscribeWithoutIo(capture);

                Require(emitted.Select(entry => entry.quote).SequenceEqual(new[] { prep, wiring }),
                    "Guide capture must keep the non-spatial preparation speech and one source event for both resistor ends");
                Require(emitted[0].pinMarks.Length == 0 && emitted[1].pinMarks.SequenceEqual(new[] { "plus-1", "a-1" }),
                    "Both connector marks must remain attached to the same exact source phrase");
                Require(emitted.All(entry => entry.framePaths.Length > 0 && entry.framePaths.All(File.Exists) &&
                    File.Exists(Path.Combine(dir, entry.audioFile))), "Guide sources retain their audio chunks and photos");
                Require(((ICollection)Get(capture, "reviewQueue")).Count == 0,
                    "Guide capture must leave diagnosis of the fixed LED template to the guide draft route");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                Directory.Delete(dir, true);
            }
        }

        static void TranscribeWithoutIo(TraceCapture capture)
        {
            var processing = (IEnumerator)Capture.GetMethod("Transcribe", Fields).Invoke(capture, null);
            while (processing.MoveNext())
                Require(processing.Current == null, "Fixture unexpectedly requested external I/O");
        }

        static void AddWindow(TraceCapture capture, string dir, float start, float end, string transcript,
            byte[] fixture, Dictionary<string, float> times, params (string role, float time)[] pins)
        {
            var window = Activator.CreateInstance(Capture.GetNestedType("Window", BindingFlags.NonPublic), true);
            var source = (TraceTranscript)Get(window, "source");
            source.start = start; source.end = end; source.transcript = transcript; source.error = "";
            if(transcript.Length!=0)
            {
                source.audioPath=Path.Combine(dir,Guid.NewGuid().ToString("N")+".wav");
                File.Copy(Path.Combine(Application.dataPath,"Resources","TraceDemo","input_voice.wav"),source.audioPath);
            }
            foreach (var mark in pins)
            {
                var pin = AddListItem(window, "pins");
                Set(pin, "time", mark.time); Set(pin, "role", mark.role);
            }
            if (fixture != null)
            {
                for (float time = start + .25f; time < end; time += .5f)
                {
                    var frame = AddListItem(window, "frames");
                    string path = Path.Combine(dir, "frame-" + times.Count + ".jpg");
                    File.WriteAllBytes(path, fixture);
                    times.Add(path, time);
                    Set(frame, "time", time); Set(frame, "path", path);
                    Set(frame, "cameraTicks", (long)(time * TimeSpan.TicksPerSecond));
                }
            }
            var queue = Get(capture, "audioQueue");
            queue.GetType().GetMethod("Enqueue").Invoke(queue, new[] { window });
        }

        static byte[] SmallJpeg()
        {
            var texture = new Texture2D(8, 8, TextureFormat.RGB24, false);
            try
            {
                texture.SetPixels(Enumerable.Repeat(Color.gray, 64).ToArray());
                texture.Apply();
                return texture.EncodeToJPG();
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }

        static object AddListItem(object window, string name)
        {
            var list = (IList)Get(window, name);
            var item = Activator.CreateInstance(list.GetType().GetGenericArguments()[0], true);
            list.Add(item);
            return item;
        }

        static object Get(object obj, string name) => obj.GetType().GetField(name, Fields).GetValue(obj);
        static void Set(object obj, string name, object value) => obj.GetType().GetField(name, Fields).SetValue(obj, value);
        static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Trace capture timeline: " + message);
        }
    }
}
