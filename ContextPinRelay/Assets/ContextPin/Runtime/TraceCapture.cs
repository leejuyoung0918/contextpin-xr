using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Networking;

namespace ContextPin
{
    // The audio clock determines when evidence is collected. Cloud latency
    // never determines which camera images or student pinches go with a quote.
    public sealed class TraceCapture : MonoBehaviour
    {
        sealed class PinMark { public float time; public string role; public bool linked; }
        sealed class Phrase { public string text; public float start, end; }
        sealed class Window
        {
            public TraceTranscript source = new TraceTranscript();
            public readonly List<TraceFrame> frames = new List<TraceFrame>();
            public readonly List<PinMark> pins = new List<PinMark>();
            public readonly List<Phrase> phrases = new List<Phrase>();
            public bool hasSignal;
            public byte[] unsavedAudio;
        }
        [Serializable] sealed class AudioRequest { public string audioBase64; public bool includeMetadata; }
        [Serializable] sealed class AudioReply { public string transcript, provider, providerItemId; }
        [Serializable] sealed class FrameRequest { public string imageBase64; public TraceImagePoint[] points; }
        [Serializable] sealed class ReviewRequest { public string transcript, pinchRole; public FrameRequest[] frames; }
        [Serializable] sealed class ReviewReply { public string candidateRole, reason, quote, status; }
        [Serializable] sealed class PinEntry { public float time; public string role; }
        [Serializable] sealed class ReviewEvidence { public TraceCaptureEvent entry; public TraceFrame[] frames; }
        [Serializable] sealed class AcquiredWindow { public TraceTranscript source; public TraceFrame[] frames; }
        sealed class ReviewJob { public TraceCaptureEvent entry; public TraceFrame[] frames; }

        public bool Recording { get; private set; }
        public bool Busy => active;
        public bool Initializing { get; private set; }
        public IReadOnlyList<TraceTranscript> Transcripts => history;
        readonly List<TraceTranscript> history = new List<TraceTranscript>();
        readonly Queue<Window> audioQueue = new Queue<Window>();
        readonly Queue<ReviewJob> reviewQueue = new Queue<ReviewJob>();
        readonly List<float> samples = new List<float>(100000);
        readonly List<TraceFrame> freshFrames = new List<TraceFrame>();
        readonly List<PinMark> freshPins = new List<PinMark>();
        RelayEvidence device;
        Action<TraceCaptureEvent> emit;
        Action<string> report;
        Action drained;
        Func<string, Vector3?> resolve;
        string[] guideRoles;
        bool eventOnly, deadlineReached;
        int audioSequence;
        long sourceSample, segmentFirstSample;
        float drainDeadline;
        internal string MicrophoneState { get; private set; } = "idle";
        internal float LastRms { get; private set; }
        internal float LastClippingRatio { get; private set; }
        internal double AudioClockOrigin => origin;
        internal RelayEvidence EvidenceDevice()
        {
            if(device==null)device=gameObject.AddComponent<RelayEvidence>();
            return device;
        }
        Window previous, waiting;
        bool active, stopping, audioWorking, reviewWorking, hasSignal, samplingCameraFailed;
        int epoch, cursor;
        float origin, segmentStart, lastVoice, lastSampleClock, nextPoll, nextShot;
        long lastCameraTicks;
        string evidenceDir, stoppedBecause;
        float audioRetryAfter, reviewRetryAfter;
        UnityWebRequest audioHttp, reviewHttp;
        float Now => Time.realtimeSinceStartup - origin;
        static readonly string[] LocationWords = { "여기", "저기", "거기", "이쪽", "저쪽", "그쪽", "이곳", "이부분", "이단자", "이지점", "이핀", "이점", "지점", "입력", "증폭", "접지", "출력", "단자", "테스트포인트", "gnd", "input", "output", "here", "there", "ground" };
        static readonly Regex Sentences = new Regex(@"[^.!?。！？\n]+[.!?。！？]*", RegexOptions.CultureInvariant);
        static readonly Regex ClauseBreak = new Regex(@"[,，]\s*|\s+(?=(?:그리고|마지막으로|그다음(?:으로|에)?|다음으로|이번에는|이어서)\s)|(?<=보고|확인하고|측정하고|연결하고)\s+", RegexOptions.CultureInvariant);

        public void StartCapture(string sessionDir, Func<string, Vector3?> resolveWorld, Action<TraceCaptureEvent> onEvent,
            Action<string> onStatus, string[] guidePointIds = null, bool explicitSnapshots = false)
        {
            if (active) { onStatus?.Invoke("이전 기록을 정리하고 있습니다."); return; }
            resolve = resolveWorld; emit = onEvent; report = onStatus; drained = null;
            guideRoles = guidePointIds;
            eventOnly = explicitSnapshots;
            audioSequence = 0; sourceSample = segmentFirstSample = 0; deadlineReached = false;
            LastRms = LastClippingRatio = 0; MicrophoneState = "requesting";
            evidenceDir = Path.Combine(sessionDir, "evidence");
            if (!Store(() => Directory.CreateDirectory(evidenceDir))) return;
            EvidenceDevice();
            history.Clear(); samples.Clear(); freshFrames.Clear(); freshPins.Clear(); audioQueue.Clear(); reviewQueue.Clear();
            previous = null; waiting = null; stopping = false; audioWorking = false; reviewWorking = false;
            samplingCameraFailed = false; lastCameraTicks = 0; hasSignal = false; audioRetryAfter = 0; reviewRetryAfter = 0;
            stoppedBecause = ""; active = true; Initializing = true; origin = Time.realtimeSinceStartup;
            StartCoroutine(Open(++epoch));
        }

        IEnumerator Open(int token)
        {
            Say("마이크 권한을 확인하고 있습니다.");
            string result = "";
            yield return device.StartMicrophone(message => result = message, eventOnly ? 40 : 20);
            if (token != epoch || stopping) yield break;
            if (!device.Recording)
            {
                MicrophoneState = "unavailable";
                if (!eventOnly) active = false;
                Initializing = false;
                Say(eventOnly ? result + " · PC에서 설명을 수동 입력할 수 있어요." : result);
                yield break;
            }
            origin = Time.realtimeSinceStartup;
            cursor = 0; segmentStart = 0; lastVoice = 0; lastSampleClock = 0; nextPoll = 0; nextShot = 0;
            Recording = true;
            MicrophoneState = "recording";
            if (eventOnly)
            {
                Initializing = false;
                Say("마이크 기록 중 · 사진은 장면 저장으로 촬영합니다.");
                yield break;
            }
            Say("마이크 기록 중 · 실물 카메라 준비 중");
            yield return device.StartPassthroughCamera(message => result = message);
            if (token == epoch && Recording)
            {
                Initializing = false;
                Say(device.CameraReady ? "마이크·카메라 기록 중" : "음성 기록 중 · " + result);
            }
        }

        public void Pin(string role)
        {
            if (!Recording || !(guideRoles ?? TraceCaptureMedia.Roles).Contains(role))
            {
                Say(Recording ? "등록된 회로 지점을 선택하세요." : "마이크가 준비되면 지점을 저장할 수 있습니다.");
                return;
            }
            var pin = new PinMark { time = Now, role = role };
            freshPins.Add(pin);
            Append("pins.jsonl", new PinEntry { time = pin.time, role = pin.role });
            if (!eventOnly) SampleCamera();
        }

        void Update()
        {
            if(active && eventOnly && stopping && !deadlineReached && Time.realtimeSinceStartup >= drainDeadline)
            {
                deadlineReached = true;
                audioHttp?.Abort();
                Say("남은 전사를 보류했어요. PC에서 저장된 원음을 듣고 문장을 입력할 수 있어요.");
            }
            if (!Recording) return;
            float time = Now;
            if (!eventOnly && time >= nextShot) { nextShot = time + .48f; SampleCamera(); }
            if (time >= nextPoll) { nextPoll = time + .1f; CollectAudio(); }
        }

        void SampleCamera()
        {
            if (!device.CameraReady || samplingCameraFailed) return;
            var camera = device.CameraSource;
            long stamp = camera.Timestamp.Ticks;
            if (stamp == 0 || stamp == lastCameraTicks) return;
            try
            {
                var frame = TraceCaptureMedia.Photograph(camera, resolve, Now, guideRoles);
                if (frame != null) { freshFrames.Add(frame); lastCameraTicks = stamp; }
            }
            catch (Exception e)
            {
                samplingCameraFailed = true;
                Say("사진 확보 실패 · 음성과 핀치는 계속 저장합니다. " + e.GetType().Name);
            }
        }

        void CollectAudio()
        {
            var clip = device.RecordingClip;
            if (clip == null) return;
            float now = Now;
            int position = Microphone.GetPosition(null);
            if (position < 0 || !Microphone.IsRecording(null))
            {
                if (now - lastSampleClock > 3 && !stopping)
                {
                    FinishInput("마이크 연결이 끊겼습니다.");
                }
                return;
            }
            if (now - lastSampleClock >= clip.length)
            {
                Seal(lastSampleClock);
                Append("gaps.jsonl", new TraceTranscript { start = lastSampleClock, end = now, error = "마이크 버퍼 범위를 넘은 전사 공백" });
                cursor = position; segmentStart = now; lastSampleClock = now;
                Say("마이크 기록에 공백이 있습니다. 기록을 재개합니다."); return;
            }
            int count = (position - cursor + clip.samples) % clip.samples;
            if (count == 0)
            {
                if (now - lastSampleClock > 4 && !Initializing && !stopping)
                {
                    FinishInput("마이크에 새 샘플이 들어오지 않습니다.");
                }
                return;
            }
            var block = new float[count * clip.channels];
            if (!clip.GetData(block, cursor)) { Say("마이크 샘플을 읽을 수 없습니다."); return; }
            cursor = position; lastSampleClock = now;
            samples.AddRange(block);
            sourceSample += count;
            // Only exact silence is omitted from transcription. Quiet speech
            // is still saved and sent; energy is used only to choose a break.
            double energy = 0;
            int clipped = 0;
            foreach (float value in block)
            {
                energy += value * value;
                if (Mathf.Abs(value) > 1f/32768) hasSignal = true;
                if (Mathf.Abs(value) >= .99f) clipped++;
            }
            LastRms = (float)Math.Sqrt(energy / block.Length);
            LastClippingRatio = (float)clipped / block.Length;
            if (LastRms > .007) lastVoice = now;
            float elapsed = now - segmentStart;
            if (elapsed >= 5 || (elapsed >= 1.7f && now - lastVoice >= .5f)) Seal(now);
        }

        void Seal(float end)
        {
            if (samples.Count == 0 && freshPins.Count == 0 && freshFrames.Count == 0) return;
            var window = new Window { hasSignal = hasSignal };
            window.source.start = segmentStart; window.source.end = Mathf.Max(segmentStart, end);
            window.source.transcript = ""; window.source.error = ""; window.source.audioPath = "";
            window.source.segmentId = Guid.NewGuid().ToString("N");
            window.source.sequence = ++audioSequence;
            window.source.firstSample = segmentFirstSample;
            window.source.endSample = sourceSample;
            window.source.timingQuality = "segment_bounds";
            window.source.signalRms = LastRms;
            window.source.clippingRatio = LastClippingRatio;
            if (samples.Count != 0 && device.RecordingClip != null)
            {
                var clip = device.RecordingClip;
                byte[] wave = TraceCaptureMedia.PcmWave(samples, clip.channels, clip.frequency);
                window.source.sampleRate = clip.frequency;
                window.source.channels = clip.channels;
                using (var sha = SHA256.Create()) window.source.sha256 = BitConverter.ToString(sha.ComputeHash(wave)).Replace("-", "").ToLowerInvariant();
                string file = NewPath(".wav");
                if (Store(() => File.WriteAllBytes(file, wave))) window.source.audioPath = file;
                else window.unsavedAudio = wave;
            }
            window.pins.AddRange(freshPins);
            // Spill each pending window to private storage before waiting for
            // transcription, so long requests don't retain a video in memory.
            foreach (var frame in freshFrames)
            {
                frame.path = NewPath(".jpg");
                if (Store(() => File.WriteAllBytes(frame.path, frame.jpeg))) frame.jpeg = null;
                else frame.path = "";
                window.frames.Add(frame);
            }
            Append("chunks.jsonl", new AcquiredWindow { source = window.source, frames = window.frames.ToArray() });
            freshFrames.Clear(); freshPins.Clear(); samples.Clear();
            segmentStart = end; hasSignal = false;
            segmentFirstSample = sourceSample;
            audioQueue.Enqueue(window);
            if (!audioWorking) { audioWorking = true; StartCoroutine(Transcribe()); }
        }

        public void StopCapture(Action onDrained)
        {
            if (!active) { onDrained?.Invoke(); return; }
            drained += onDrained;
            if (stopping) return;
            stopping = true; Initializing = false; ++epoch;
            drainDeadline = Time.realtimeSinceStartup + 16;
            if (Recording)
            {
                CollectAudio(); if(!eventOnly)SampleCamera(); Seal(Now);
            }
            Recording = false;
            if (MicrophoneState == "recording") MicrophoneState = "stopped";
            device.StopMicrophone(); device.StopCamera();
            if (!audioWorking) CloseTimeline();
            FinishIfDrained();
        }

        void FinishInput(string reason)
        {
            stoppedBecause = reason;
            Append("gaps.jsonl", new TraceTranscript { start = lastSampleClock, end = Now, error = reason });
            Say(reason + " 확보한 자료를 정리합니다.");
            StopCapture(null);
        }

        IEnumerator Transcribe()
        {
            // Let Seal and StopCapture finish mutating the input window before
            // completing silent/failed requests synchronously.
            yield return null;
            while (audioQueue.Count > 0)
            {
                var w = audioQueue.Dequeue();
                if (deadlineReached) w.source.error = "final_pending_after_deadline";
                else if (w.hasSignal)
                {
                    byte[] bytes = ReadBytes(w.source.audioPath, w.unsavedAudio);
                    if (bytes == null) w.source.error = "보관된 오디오를 읽지 못했습니다.";
                    else
                    {
                        var body = new AudioRequest { audioBase64 = Convert.ToBase64String(bytes), includeMetadata=eventOnly };
                        yield return Post<AudioReply>("/trace/transcribe", body, false, (value, error) => {
                            w.source.transcript = value?.transcript ?? "";
                            w.source.provider = value?.provider;
                            w.source.providerItemId = value?.providerItemId;
                            w.source.revision = value == null ? 0 : 1;
                            w.source.error = value != null && value.transcript == null ? "전사 응답에 원문이 없습니다." : error;
                        });
                    }
                }
                history.Add(w.source); Append("transcript.jsonl", w.source);
                if (!string.IsNullOrEmpty(w.source.error)) Say("전사 실패 · 확보한 오디오와 사진을 보관합니다. " + w.source.error);
                MakePhrases(w);
                if (waiting != null) { Evaluate(previous, waiting, w); Release(previous); previous = waiting; }
                waiting = w;
            }
            audioWorking = false;
            if (stopping) { CloseTimeline(); FinishIfDrained(); }
        }

        static void MakePhrases(Window w)
        {
            string text = w.source.transcript;
            if (string.IsNullOrWhiteSpace(text)) return;
            float duration = w.source.end - w.source.start;
            foreach (Match match in Sentences.Matches(text))
            {
                int start = match.Index;
                int sentenceEnd = match.Index + match.Length;
                // Two distinct locations can occur in one sentence. Split at
                // natural clause boundaries only when both sides refer to a
                // place; retain the exact original substring for review.
                foreach (Match separator in ClauseBreak.Matches(match.Value))
                {
                    int separatorStart = match.Index + separator.Index;
                    int next = separatorStart + separator.Length;
                    string left = text.Substring(start, separatorStart - start);
                    string right = text.Substring(next, sentenceEnd - next);
                    if (!Spatial(left) || !Spatial(right)) continue;
                    AddPhrase(w, text, start, separatorStart, duration);
                    start = next;
                }
                AddPhrase(w, text, start, sentenceEnd, duration);
            }
        }

        static void AddPhrase(Window w, string text, int start, int end, float duration)
        {
            while (start < end && char.IsWhiteSpace(text[start])) start++;
            while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
            if (end <= start) return;
            // Word timestamps are unavailable. Preserve exact clip boundaries
            // and approximate only the position of a clause within that clip.
            w.phrases.Add(new Phrase { text = text.Substring(start, end - start),
                start = w.source.start + duration * start / text.Length, end = w.source.start + duration * end / text.Length });
        }

        void CloseTimeline()
        {
            if (waiting != null) Evaluate(previous, waiting, null);
            Release(previous); Release(waiting); previous = null; waiting = null;
        }

        static bool Spatial(string text)
        {
            var compact = Regex.Replace(text.ToLowerInvariant(), @"\s+", "");
            return LocationWords.Any(word => compact.Contains(word));
        }

        void Evaluate(Window before, Window current, Window after)
        {
            var nearby = new[] { before, current, after }.Where(x => x != null).ToArray();
            var phrases = nearby.SelectMany(x => x.phrases).ToList();
            var assignments = new Dictionary<Phrase, List<PinMark>>();
            foreach (var pin in nearby.SelectMany(x => x.pins).Where(p => !p.linked))
            {
                var closest = phrases.Select(p => new { phrase = p, distance = Mathf.Max(0, Mathf.Max(p.start - pin.time, pin.time - p.end)) })
                    .Where(x => x.distance <= 1.25f).OrderBy(x => x.distance).ThenBy(x => Mathf.Abs((x.phrase.start + x.phrase.end)/2 - pin.time)).FirstOrDefault();
                if (closest == null)
                {
                    if (current.pins.Contains(pin)) { CreateEntry(nearby, current, pin.time, "", pin); pin.linked = true; }
                    continue;
                }
                if (!current.phrases.Contains(closest.phrase))
                {
                    // A late pinch can have a short silent window between it
                    // and the last sentence. That sentence has already been
                    // evaluated. Preserve this manual mark as a separate entry.
                    if (current.pins.Contains(pin) && before != null && before.phrases.Contains(closest.phrase))
                    {
                        CreateEntry(nearby, before, pin.time, closest.phrase.text, pin); pin.linked = true;
                    }
                    continue;
                }
                if (!assignments.TryGetValue(closest.phrase, out var list)) assignments[closest.phrase] = list = new List<PinMark>();
                list.Add(pin); pin.linked = true;
            }
            foreach (var p in current.phrases)
            {
                if (guideRoles != null)
                {
                    assignments.TryGetValue(p, out var marks);
                    CreateEntry(nearby, current, (p.start + p.end) / 2, p.text, null, marks);
                }
                else if (assignments.TryGetValue(p, out var pins))
                    foreach (var pin in pins) CreateEntry(nearby, current, pin.time, p.text, pin);
                else if (Spatial(p.text)) CreateEntry(nearby, current, (p.start + p.end)/2, p.text, null);
            }
            if (eventOnly && current.phrases.Count == 0 && !string.IsNullOrEmpty(current.source.audioPath) &&
                (current.hasSignal || !string.IsNullOrEmpty(current.source.error)))
                CreateEntry(nearby, current, (current.source.start + current.source.end)/2, "", null);
            else if (!string.IsNullOrEmpty(current.source.error) && !current.pins.Any(p => p.linked))
                CreateEntry(nearby, current, (current.source.start + current.source.end)/2, "", null);
        }

        void CreateEntry(Window[] nearby, Window current, float time, string quote, PinMark pin, List<PinMark> marks = null)
        {
            // Pick distinct camera timestamps on both sides of the event. At
            // session boundaries retain as many genuine photographs as exist.
            var available = nearby.SelectMany(w => w.frames).Where(f => Mathf.Abs(f.time - time) <= 3.2f).ToList();
            var chosen = new List<TraceFrame>();
            foreach (float offset in new[] { -.65f, 0f, .65f })
            {
                var frame = available.OrderBy(f => Mathf.Abs(f.time - time - offset)).FirstOrDefault();
                if (frame != null) { chosen.Add(frame); available.Remove(frame); frame.keep = true; }
            }
            var frames = chosen.OrderBy(f => f.time).ToArray();
            string transcript = string.Join("\n", nearby.Where(w => w == current || (w.source.end >= time - 2f && w.source.start <= time + 2f))
                .Select(w => w.source.transcript).Where(s => !string.IsNullOrWhiteSpace(s)));
            var entry = new TraceCaptureEvent { id = Guid.NewGuid().ToString("N"), time = time,
                audioFile=Path.GetFileName(current.source.audioPath),
                transcript = transcript, quote = quote, pinchRole = pin?.role ?? "", candidateRole = "", status = "unknown",
                framePaths = frames.Where(f => !string.IsNullOrEmpty(f.path)).Select(f => f.path).ToArray(),
                pinMarks = marks?.Select(mark => mark.role).Distinct().ToArray() ?? (pin == null ? Array.Empty<string>() : new[] {pin.role}) };
            entry.sourceMode = eventOnly ? "recorded_audio" : "legacy";
            entry.provider = current.source.provider;
            entry.providerItemId = current.source.providerItemId ?? current.source.segmentId;
            entry.audioSha256 = current.source.sha256;
            if (!string.IsNullOrEmpty(current.source.error)) entry.reason = current.source.error;
            else if (quote.Length == 0) entry.reason = "이 핀치와 연결된 발화를 찾지 못했습니다.";
            else entry.reason = "원문과 당시 사진을 검토 중입니다.";
            WriteEvent(entry, frames);
            emit?.Invoke(entry);
            if (quote.Length != 0 && guideRoles == null)
            {
                reviewQueue.Enqueue(new ReviewJob { entry = entry, frames = frames });
                if (!reviewWorking) { reviewWorking = true; StartCoroutine(Review()); }
            }
        }

        IEnumerator Review()
        {
            yield return null;
            while (reviewQueue.Count != 0)
            {
                var job = reviewQueue.Dequeue(); var entry = job.entry;
                var images = new List<FrameRequest>();
                foreach (var f in job.frames)
                {
                    byte[] bytes = ReadBytes(f.path, f.jpeg);
                    if (bytes != null) images.Add(new FrameRequest { imageBase64 = Convert.ToBase64String(bytes), points = f.points });
                }
                // Ground the model in the candidate phrase, not an entire
                // multi-target paragraph. The full original stays in the card.
                var request = new ReviewRequest { transcript = entry.quote, pinchRole = entry.pinchRole, frames = images.ToArray() };
                yield return Post<ReviewReply>("/trace/review", request, true, (value,error) => {
                    bool ok = value != null && !string.IsNullOrEmpty(value.quote) && request.transcript.Contains(value.quote)
                        && TraceCaptureMedia.Roles.Contains(value.candidateRole);
                    if (ok && (value.status == "suggested" || value.status == "matched" || value.status == "mismatch"))
                    {
                        entry.candidateRole = value.candidateRole; entry.quote = value.quote;
                        entry.status = value.status; entry.reason = value.reason;
                    }
                    else { entry.status = "unknown"; entry.reason = !string.IsNullOrEmpty(error) ? "후보 분석 실패 · " + error :
                        value?.reason ?? "위치를 확인할 근거가 부족합니다."; }
                });
                WriteEvent(entry, job.frames); emit?.Invoke(entry);
            }
            reviewWorking = false; FinishIfDrained();
        }

        IEnumerator Post<T>(string path, object body, bool review, Action<T,string> complete) where T : class
        {
            if (Time.realtimeSinceStartup < (review ? reviewRetryAfter : audioRetryAfter))
            {
                complete(null, "AI 서버 연결 지연 · 원본은 기기에 보관합니다."); yield break;
            }
            string url = (device.config.serverUrl ?? "").TrimEnd('/') + path;
            UnityWebRequest web = null; UnityWebRequestAsyncOperation operation = null;
            string error = "";
            try
            {
                web = new UnityWebRequest(url, "POST");
                web.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(body)));
                web.downloadHandler = new DownloadHandlerBuffer(); web.SetRequestHeader("Content-Type","application/json"); web.timeout = 52;
                if (review) reviewHttp = web; else audioHttp = web;
                operation = web.SendWebRequest();
            }
            catch (Exception ex) { error = ex.Message; }
            if (operation != null) yield return operation;
            T result = null;
            try
            {
                if (operation != null && web.result == UnityWebRequest.Result.Success) result = JsonUtility.FromJson<T>(web.downloadHandler.text);
                else if (error.Length == 0) error = "로컬 AI 서버 연결·모델 설정 확인 (" + (web?.responseCode ?? 0) + ")";
                if (result == null && error.Length == 0) error = "AI 응답을 읽지 못했습니다.";
            }
            catch (Exception ex) { error = ex.Message; }
            finally { if (review) reviewHttp = null; else audioHttp = null; web?.Dispose(); }
            if (result == null)
            {
                if (review) reviewRetryAfter = Time.realtimeSinceStartup + 8;
                else audioRetryAfter = Time.realtimeSinceStartup + 8;
            }
            complete(result, error);
        }

        void FinishIfDrained()
        {
            if (!active || !stopping || Recording || reviewWorking || audioWorking || waiting != null) return;
            active = false;
            Say(string.IsNullOrEmpty(stoppedBecause) ? "확보한 기록을 정리했습니다." : stoppedBecause + " 확보한 기록을 정리했습니다.");
            var callback = drained; drained = null; callback?.Invoke();
        }
        string NewPath(string ext) => Path.Combine(evidenceDir, Guid.NewGuid().ToString("N") + ext);
        void Say(string text) { if (!string.IsNullOrEmpty(text)) report?.Invoke(text); }
        void WriteEvent(TraceCaptureEvent e, TraceFrame[] frames) => Append("events.jsonl", new ReviewEvidence { entry = e, frames = frames });
        void Append(string name, object record) => Store(() => File.AppendAllText(Path.Combine(evidenceDir, name), JsonUtility.ToJson(record) + "\n", new UTF8Encoding(false)));
        bool Store(Action action)
        {
            try { action(); return true; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            { Say("기기 저장소에 기록하지 못했습니다. " + e.Message); return false; }
        }
        byte[] ReadBytes(string path, byte[] fallback)
        {
            if (fallback != null) return fallback;
            if (string.IsNullOrEmpty(path)) return null;
            byte[] data = null; Store(() => data = File.ReadAllBytes(path)); return data;
        }
        void Release(Window w)
        {
            if (w == null) return;
            foreach (var f in w.frames)
                if (!f.keep && !string.IsNullOrEmpty(f.path)) Store(() => File.Delete(f.path));
        }
        void OnDestroy()
        {
            if (Recording && device != null)
            {
                // On abrupt scene/app exit preserve the untranscribed tail.
                // Do not start new network coroutines during destruction.
                stopping = true; audioWorking = true; CollectAudio(); Seal(Now); Recording = false;
            }
            audioHttp?.Abort(); reviewHttp?.Abort();
            if (device != null) { device.StopMicrophone(); device.StopCamera(); }
        }
    }
}
