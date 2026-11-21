using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using Meta.XR;

namespace ContextPin
{
    [Serializable] public sealed class TraceCaptureEvent
    {
        public string id, transcript, pinchRole, candidateRole, reason, quote, status;
        public string audioFile;
        public float time;
        public string[] framePaths;
        public string[] pinMarks;
        public string sourceMode, provider, providerItemId, audioSha256;
    }

    [Serializable] public sealed class TraceTranscript
    {
        public float start, end;
        public string transcript, error, audioPath;
        public string segmentId, sha256, provider, providerItemId, timingQuality;
        public int sequence, sampleRate, channels, revision;
        public long firstSample, endSample;
        public float signalRms, clippingRatio;
    }

    [Serializable] internal sealed class TraceImagePoint
    {
        public string role;
        public float x, y;
    }

    [Serializable] internal sealed class TraceFrame
    {
        public float time;
        public long cameraTicks;
        public string path;
        public TraceImagePoint[] points;
        [NonSerialized] public byte[] jpeg;
        [NonSerialized] public bool keep;
    }

    internal static class TraceCaptureMedia
    {
        public static readonly string[] Roles = { "input", "amp", "gnd", "output" };

        // Both the visible image and the projected role coordinates use this
        // physical camera and the pose corresponding to the same camera frame.
        public static TraceFrame Photograph(PassthroughCameraAccess camera, Func<string, Vector3?> resolve, float time,
            IEnumerable<string> roles = null)
        {
            var source = camera.GetTexture();
            if (source == null || source.width < 8 || source.height < 8) return null;
            Pose pose = camera.GetCameraPose();
            var points = new List<TraceImagePoint>();
            foreach (var role in roles ?? Roles)
            {
                Vector3? target = resolve?.Invoke(role);
                if (!target.HasValue || Vector3.Dot(target.Value - pose.position, pose.rotation * Vector3.forward) <= 0) continue;
                var uv = camera.WorldToViewportPoint(target.Value, pose);
                if (uv.x >= 0 && uv.x <= 1 && uv.y >= 0 && uv.y <= 1)
                    points.Add(new TraceImagePoint { role = role, x = uv.x, y = 1f - uv.y });
            }
            const int width = 640;
            int height = Mathf.Max(1, Mathf.RoundToInt((float)width * source.height / source.width));
            var targetTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            Texture2D readable = null;
            try
            {
                Graphics.Blit(source, targetTexture);
                RenderTexture.active = targetTexture;
                readable = new Texture2D(width, height, TextureFormat.RGB24, false);
                readable.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                return new TraceFrame { time = time, points = points.ToArray(), cameraTicks = camera.Timestamp.Ticks, jpeg = readable.EncodeToJPG(74) };
            }
            finally
            {
                RenderTexture.active = previous;
                if (readable != null) UnityEngine.Object.Destroy(readable);
                RenderTexture.ReleaseTemporary(targetTexture);
            }
        }

        public static byte[] PcmWave(List<float> pcm, int channels, int rate)
        {
            int size = checked(pcm.Count * 2);
            using (var stream = new MemoryStream(size + 44))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + size);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                writer.Write((short)1); writer.Write((short)channels); writer.Write(rate);
                writer.Write(rate * channels * 2); writer.Write((short)(channels * 2)); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(size);
                foreach (float sample in pcm) writer.Write((short)Mathf.RoundToInt(Mathf.Clamp(sample, -1, 1) * 32767f));
                return stream.ToArray();
            }
        }
    }
}
