using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

namespace ContextPin
{
    sealed class MeetingTransport : IDisposable
    {
        public Uri Address { get; }
        public string SessionId { get; set; }
        public string Token { get; set; }
        public bool EventsConnected => eventsConnected;
        public readonly ConcurrentQueue<JObject> Snapshots = new ConcurrentQueue<JObject>();
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        volatile bool eventsConnected;
        long lastSequence;
        Task listening;

        public MeetingTransport(string address)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) ||
                (uri.Scheme != Uri.UriSchemeHttps && (uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback)))
                throw new ArgumentException("회의 서버에 HTTPS 주소 또는 USB 루프백 주소를 입력하세요.");
            Address = new Uri(uri.GetLeftPart(UriPartial.Authority));
        }

        public Uri Endpoint(string path)
        {
            if (string.IsNullOrEmpty(path) || path[0] != '/' || path.StartsWith("//", StringComparison.Ordinal) ||
                !Uri.TryCreate(Address, path, out var endpoint) || endpoint.Authority != Address.Authority || endpoint.Scheme != Address.Scheme)
                throw new ArgumentException("회의 서버에서 받은 주소가 올바르지 않습니다.");
            return endpoint;
        }

        public string SessionPath => "/v1/sessions/" + Uri.EscapeDataString(SessionId);

        UnityWebRequest Request(string path, string method, byte[] body = null, string contentType = null)
        {
            var request = new UnityWebRequest(Endpoint(path), method)
            {
                downloadHandler = new DownloadHandlerBuffer(),
                redirectLimit = 0,
                timeout = 35,
            };
            if (body != null)
            {
                request.uploadHandler = new UploadHandlerRaw(body);
                request.SetRequestHeader("Content-Type", contentType);
            }
            if (!string.IsNullOrEmpty(Token)) request.SetRequestHeader("Authorization", "Bearer " + Token);
            return request;
        }

        public IEnumerator Json(string path, JObject payload, Action<JObject, string> completed)
            => JsonWithStatus(path, payload, (reply, error, _) => completed(reply, error));

        public IEnumerator JsonWithStatus(string path, JObject payload, Action<JObject, string, long> completed)
            => SendJson(path, payload == null ? "GET" : "POST",
                payload == null ? null : Encoding.UTF8.GetBytes(payload.ToString(Formatting.None)), "application/json", completed);

        public IEnumerator UploadWave(string path, byte[] body, Action<JObject, string> completed)
            => SendJson(path, "POST", body, "audio/wav", (reply, error, _) => completed(reply, error));

        public IEnumerator UploadPhoto(string path, byte[] photo, string version, string part, JObject timestamp,
            Action<JObject, string> completed)
        {
            var sections = new List<IMultipartFormSection>
            {
                new MultipartFormFileSection("file", photo, "reference.jpg", "image/jpeg"),
                new MultipartFormDataSection("version_id", version),
                new MultipartFormDataSection("source_mode", "live_capture"),
            };
            if (!string.IsNullOrEmpty(part)) sections.Add(new MultipartFormDataSection("part_id", part));
            foreach (var field in timestamp.Properties())
                sections.Add(new MultipartFormDataSection(field.Name, field.Value.ToString(Formatting.None)));
            var boundary = UnityWebRequest.GenerateBoundary();
            return SendJson(path, "POST", UnityWebRequest.SerializeFormSections(sections, boundary),
                "multipart/form-data; boundary=" + Encoding.ASCII.GetString(boundary), (reply, error, _) => completed(reply, error));
        }

        IEnumerator SendJson(string path, string method, byte[] body, string contentType, Action<JObject, string, long> completed)
        {
            using var request = Request(path, method, body, contentType);
            yield return request.SendWebRequest();
            if (lifetime.IsCancellationRequested) yield break;
            JObject result = null;
            try { result = JObject.Parse(request.downloadHandler.text); }
            catch (JsonException) { }
            if (request.result == UnityWebRequest.Result.Success && result != null)
            {
                completed(result, null, request.responseCode);
                yield break;
            }
            var detail = result?["detail"];
            completed(null, detail?.Type == JTokenType.String ? detail.Value<string>() :
                (detail as JObject)?.Value<string>("message") ?? request.error ?? "서버 응답을 읽을 수 없습니다.", request.responseCode);
        }

        public Uri SocketEndpoint(string path) => new UriBuilder(Endpoint(path))
        { Scheme = Address.Scheme == Uri.UriSchemeHttps ? "wss" : "ws", Port = Address.Port }.Uri;

        public IEnumerator Download(string path, int limit, Action<byte[], string> completed)
        {
            using var request = Request(path, "GET");
            var sent = request.SendWebRequest();
            while (!sent.isDone && !lifetime.IsCancellationRequested && request.downloadedBytes <= (ulong)limit) yield return null;
            if (lifetime.IsCancellationRequested) yield break;
            if (request.downloadedBytes > (ulong)limit)
            {
                request.Abort();
                completed(null, "파일이 기기의 다운로드 한도를 초과합니다.");
            }
            else if (request.result != UnityWebRequest.Result.Success) completed(null, request.error);
            else completed(request.downloadHandler.data, null);
        }

        public void StartEvents(long sequence)
        {
            if (listening != null) return;
            lastSequence = sequence;
            listening = Listen(lifetime.Token);
        }

        async Task Listen(CancellationToken stop)
        {
            var endpoint = new UriBuilder(Endpoint(SessionPath + "/events"))
            { Scheme = Address.Scheme == Uri.UriSchemeHttps ? "wss" : "ws", Port = Address.Port }.Uri;
            var buffer = new byte[16384];
            while (!stop.IsCancellationRequested)
            {
                using var socket = new ClientWebSocket();
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    deadline.CancelAfter(TimeSpan.FromSeconds(5));
                    await socket.ConnectAsync(endpoint, deadline.Token).ConfigureAwait(false);
                    var hello = new JObject { ["token"] = Token, ["after_seq"] = lastSequence };
                    var auth = new ArraySegment<byte>(Encoding.UTF8.GetBytes(hello.ToString(Formatting.None)));
                    await socket.SendAsync(auth, WebSocketMessageType.Text, true, stop).ConfigureAwait(false);
                    while (socket.State == WebSocketState.Open)
                    {
                        using var message = new MemoryStream();
                        WebSocketReceiveResult received;
                        do
                        {
                            received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), stop).ConfigureAwait(false);
                            if (received.MessageType == WebSocketMessageType.Close) break;
                            message.Write(buffer, 0, received.Count);
                            if (message.Length > 8 * 1024 * 1024) throw new IOException("Meeting event exceeds the message limit");
                        } while (!received.EndOfMessage);
                        if (received.MessageType == WebSocketMessageType.Close) break;
                        if (received.MessageType != WebSocketMessageType.Text) continue;
                        var parsed = JObject.Parse(Encoding.UTF8.GetString(message.ToArray()));
                        if (parsed["snapshot"] is not JObject snapshot) continue;
                        lastSequence = Math.Max(lastSequence, snapshot.Value<long?>("last_seq") ?? 0);
                        eventsConnected = true;
                        // Snapshots are complete. Only the newest one is useful if the render thread is busy.
                        while (Snapshots.TryDequeue(out _)) { }
                        Snapshots.Enqueue(snapshot);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception error) when (error is OperationCanceledException || error is WebSocketException || error is IOException ||
                    error is JsonException || error is NotSupportedException || error is InvalidOperationException) { }
                finally { eventsConnected = false; }
                try { await Task.Delay(1500, stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        public void Dispose()
        {
            lifetime.Cancel();
            if (listening == null) lifetime.Dispose();
            else listening.ContinueWith(_ => lifetime.Dispose(), TaskScheduler.Default);
        }
    }
}
