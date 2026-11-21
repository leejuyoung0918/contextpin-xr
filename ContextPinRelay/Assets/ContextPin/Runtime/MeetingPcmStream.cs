using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ContextPin
{
    sealed class MeetingPcmStream : IDisposable
    {
        readonly ClientWebSocket socket = new ClientWebSocket();
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        readonly AudioClip clip;
        readonly int packetFrames;
        Task<string> stopReply;
        public bool ServerEnded => stopReply?.IsCompleted == true;
        int writePosition, readPosition, available;
        uint sequence;
        ulong sample;

        public MeetingPcmStream(AudioClip clip)
        {
            this.clip = clip;
            packetFrames = clip.frequency / 8;
        }

        public async Task Open(Uri endpoint, JObject handshake)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            await socket.ConnectAsync(endpoint, deadline.Token).ConfigureAwait(false);
            await SendText(handshake, deadline.Token).ConfigureAwait(false);
            await Expect("ready", deadline.Token).ConfigureAwait(false);
            // A pending receive lets Mono process WebSocket pings throughout the recording.
            // The next application reply after "ready" is "stopped" or a server error.
            stopReply = ReceiveStopReply(lifetime.Token);
        }

        async Task<string> ReceiveStopReply(CancellationToken token)
        {
            try
            {
                await Expect("stopped", token).ConfigureAwait(false);
                return null;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { return error.Message; }
        }

        public bool ObservePosition(int position)
        {
            if (position < 0 || position >= clip.samples) return false;
            available += (position - writePosition + clip.samples) % clip.samples;
            writePosition = position;
            if (available < clip.samples) return true;
            var skip = available - clip.samples + packetFrames;
            readPosition = (readPosition + skip) % clip.samples;
            sample += (ulong)skip;
            available -= skip;
            return false;
        }

        public byte[] ReadPacket(bool flush = false)
        {
            if (available == 0 || (!flush && available < packetFrames)) return null;
            int frames = Math.Min(available, packetFrames);
            var interleaved = new float[frames * clip.channels];
            if (!clip.GetData(interleaved, readPosition)) throw new InvalidOperationException("마이크 샘플을 읽을 수 없습니다.");
            var packet = new byte[16 + frames * 2];
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0, 4), sequence++);
            BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(4, 8), sample);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), (uint)frames);
            for (int frame = 0; frame < frames; frame++)
            {
                float value = 0;
                for (int channel = 0; channel < clip.channels; channel++) value += interleaved[frame * clip.channels + channel];
                var pcm = (short)Mathf.RoundToInt(Mathf.Clamp(value / clip.channels, -1f, 1f) * 32767f);
                BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(16 + 2 * frame, 2), pcm);
            }
            available -= frames;
            readPosition = (readPosition + frames) % clip.samples;
            sample += (ulong)frames;
            return packet;
        }

        public async Task Send(byte[] packet)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            await socket.SendAsync(new ArraySegment<byte>(packet), WebSocketMessageType.Binary, true, deadline.Token).ConfigureAwait(false);
        }

        // Called on Unity's main thread to freeze the microphone cursor at the button press.
        // Only this PCM owner reads the ring. Network sends remain serialized with ordinary frames.
        public Task MarkSpeech(Task inFlight, string rangeId, bool start, int microphonePosition, string baseVersionId = null)
        {
            if (microphonePosition < 0 || microphonePosition >= clip.samples)
                throw new InvalidOperationException("마이크의 현재 샘플 위치를 읽을 수 없습니다.");
            ObservePosition(microphonePosition);
            var tail = new List<byte[]>();
            while (ReadPacket(flush: true) is byte[] packet) tail.Add(packet);
            var marker = new JObject
            {
                ["type"] = "design_speech_boundary", ["range_id"] = rangeId,
                ["edge"] = start ? "start" : "end", ["sample_index"] = (long)sample
            };
            if (!string.IsNullOrEmpty(baseVersionId)) marker["base_version_id"] = baseVersionId;
            return SendBoundary(inFlight, tail, marker);
        }

        async Task SendBoundary(Task inFlight, List<byte[]> tail, JObject marker)
        {
            if (inFlight != null) await inFlight.ConfigureAwait(false);
            foreach (var packet in tail) await Send(packet).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            await SendText(marker, deadline.Token).ConfigureAwait(false);
        }

        public async Task Finish(Task inFlight, IReadOnlyList<byte[]> tail)
        {
            var token = lifetime.Token;
            lifetime.CancelAfter(TimeSpan.FromSeconds(12));
            if (inFlight != null) await inFlight.ConfigureAwait(false);
            foreach (var packet in tail)
                await socket.SendAsync(new ArraySegment<byte>(packet), WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
            var end = new JObject { ["type"] = "stop" };
            if (sequence > 0) end["last_sequence"] = sequence - 1;
            await SendText(end, token).ConfigureAwait(false);
            var error = await stopReply.ConfigureAwait(false);
            if (error != null) throw new IOException(error);
        }

        Task SendText(JObject value, CancellationToken token) => socket.SendAsync(
            new ArraySegment<byte>(Encoding.UTF8.GetBytes(value.ToString(Formatting.None))), WebSocketMessageType.Text, true, token);

        async Task Expect(string type, CancellationToken token)
        {
            var buffer = new byte[2048];
            using var output = new MemoryStream();
            WebSocketReceiveResult message;
            do
            {
                message = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                if (message.MessageType != WebSocketMessageType.Text || output.Length + message.Count > 8192)
                    throw new IOException("회의 오디오 서버에 연결할 수 없습니다.");
                output.Write(buffer, 0, message.Count);
            } while (!message.EndOfMessage);
            var result = JObject.Parse(Encoding.UTF8.GetString(output.ToArray()));
            if (result.Value<string>("type") != type)
                throw new IOException(result.Value<string>("message") ?? "회의 오디오 서버의 확인 응답이 없습니다.");
        }

        public void Dispose()
        {
            lifetime.Cancel();
            socket.Dispose();
            lifetime.Dispose();
        }
    }
}
