using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ContextPin.Editor
{
    public static partial class MeetingSmoke
    {
        static string timelineFixtureDir, silentRangeId;
        static double silentEndTime;
        static readonly Dictionary<string, double> talkEndTimes = new Dictionary<string, double>();
        static bool TimelineTest => checkPhoneStand && !string.IsNullOrWhiteSpace(timelineFixtureDir);

        static void ResetTimelineSpeech()
        {
            timelineFixtureDir = Environment.GetEnvironmentVariable("MEETING_TIMELINE_PCM_DIR");
            silentRangeId = null;
            talkEndTimes.Clear();
        }

        // Each clip enters the same clocked microphone that supplied the marked command.
        static void QueueDiscussion(string key)
        {
            if (talkEndTimes.ContainsKey(key)) return;
            var samples = ReadSpeechFixture(Path.Combine(timelineFixtureDir, key + ".pcm"));
            int start = (PcmCursor() + PcmRate / 8) % inputRing.samples;
            Require(inputRing.SetData(samples, start), "Could not enqueue " + key);
            talkEndTimes[key] = Time.realtimeSinceStartupAsDouble + .125 + samples.Length / (double)PcmRate;
        }

        static bool HasLine(JObject state, string contains) =>
            state["utterances"].Any(u => (bool)u["is_final"] && ((string)u["text"]).Contains(contains));

        static bool BeforeTimelineCommand(RelayApp app, JObject state)
        {
            Require((string)state["meeting_audio_status"] == "recording" && report.microphoneOpenCount == 1,
                "A single meeting microphone must already be running.");
            if (silentRangeId == null)
            {
                if (!Click("ExplicitSpeechAction")) return false;
                var pending = AppType.GetField("explicitRequest", AppMembers).GetValue(app);
                silentRangeId = (string)pending.GetType().GetField("RangeId").GetValue(pending);
                silentEndTime = Time.realtimeSinceStartupAsDouble + .45;
                return false;
            }
            var silent = state["design_speech_ranges"]?[silentRangeId];
            if (silent == null) return false;
            if ((string)silent["status"] == "recording" && Time.realtimeSinceStartupAsDouble >= silentEndTime)
            {
                if (Click("ExplicitSpeechAction")) silentEndTime = double.PositiveInfinity;
                return false;
            }
            if ((string)silent["status"] != "no_speech") return false;
            Require(state["edit_requests"]?[silentRangeId] == null && state["candidate_sets"]?[silentRangeId] == null,
                "An empty command must never be submitted for CAD.");
            if (!ExplicitUi("ExplicitDesignStatus").Contains("수정 요청을 듣지 못했습니다")) return false;
            report.silentCommandRejected = true;
            QueueDiscussion("intro");
            return HasLine(state, "크기");
        }

        static void SpeakDuringGeneration(JObject state)
        {
            string status = (string)state["candidate_sets"]?[captureId]?["status"];
            if (status is "generating" or "building")
            {
                QueueDiscussion("generation");
                if (Time.realtimeSinceStartupAsDouble >= talkEndTimes["generation"])
                    report.spokeWhileGenerating = true;
            }
            if (status == "ready")
                Require(report.spokeWhileGenerating, "The first ordinary utterance must finish while the server is producing candidates.");
        }

        static bool WaitForReviewSpeech(JObject state)
        {
            if (!HasLine(state, "기다")) return false;
            QueueDiscussion("review");
            report.spokeWhileComparing = HasLine(state, "좋");
            return report.spokeWhileComparing;
        }

        static bool ReadyToEndWithSpeech(JObject state)
        {
            QueueDiscussion("closing");
            if (Time.realtimeSinceStartupAsDouble < talkEndTimes["closing"] + .03) return false;
            Require(!HasLine(state, "마칠"), "End must be pressed before the final ordinary utterance is published.");
            return true;
        }

        static void CheckArchivedTimeline(JObject state)
        {
            string sid = (string)state["session_id"];
            var lines = state["utterances"].OrderBy(u => (long)u["start_ms"]).ToArray();
            Require(report.microphoneOpenCount == 1 && (string)state["transcript_status"] == "finalized",
                "Review, two commands and meeting end must use exactly one microphone and seal the server timeline.");
            Require(lines.Length == 7 && lines.All(u => (bool)u["is_final"] && (string)u["meeting_id"] == sid &&
                    (string)u["source"] == "stt" && (int)u["revision"] > 0 && (long)u["end_ms"] >= (long)u["start_ms"] &&
                    ((JObject)u).ContainsKey("speaker_id")) && lines.Select(u => (string)u["utterance_id"]).Distinct().Count() == lines.Length,
                "Expected each of the four ordinary and three command utterances exactly once: " +
                string.Join(" / ", lines.Select(u => (string)u["text"])));
            report.timelineTexts = lines.Select(u => (string)u["text"]).ToArray();
            Require(report.timelineTexts[0].Contains("크기") && report.timelineTexts[2].Contains("기다") &&
                    report.timelineTexts[3].Contains("좋") && report.timelineTexts.Last().Contains("마칠"),
                "Ordinary discussion must bracket the commands and include the closing audio.");

            for (int index = 0; index < 2; index++)
            {
                var id = Path.GetFileName(Path.GetDirectoryName(report.captureFiles[index]));
                var bundle = state["edit_requests"][id];
                var window = state["design_speech_ranges"][id];
                var expected = index == 0 ? new[] { lines[1] } : new[] { lines[4], lines[5] };
                var refs = new JArray(expected.Select(u => (string)u["utterance_id"]));
                var decision = state["decisions"].Single(d => (string)d["edit_request_id"] == id);
                var selection = bundle["selection"];
                Require(JToken.DeepEquals(window["command_utterance_ids"], refs) &&
                        JToken.DeepEquals(bundle["command_utterance_ids"], refs) && JToken.DeepEquals(decision["command_utterance_ids"], refs) &&
                        (string)bundle["command_text"] == string.Join(" ", expected.Select(u => (string)u["text"])) &&
                        expected.All(u => (long)u["end_ms"] > (long)window["start_ms"] && (long)u["start_ms"] < (long)window["end_ms"]),
                        "The window, bundle and commit must reference precisely the same overlapping meeting utterances.");
                Require((string)decision["base_version_id"] == (index == 0 ? report.initialVersion : report.middleVersion) &&
                        (string)decision["result_version_id"] == report.committedVersions[index] &&
                        (string)decision["selected_candidate_id"] == report.selectedCandidates[index] &&
                        (string)decision["selection_id"] == (string)selection["selection_id"] &&
                        (string)decision["image_id"] == (string)bundle["visual_evidence"]["image_asset_id"] &&
                        (long)decision["commit_time"] >= (long)window["end_ms"],
                        "The committed version must trace directly to the edit range, ray, image and human decision.");
            }
            var folder = Path.Combine(dataDirectory, "results", sid, "transcript");
            var archived = File.ReadLines(Path.Combine(folder, "transcript.jsonl")).Select(JObject.Parse).ToArray();
            var txt = File.ReadAllText(Path.Combine(folder, "transcript.txt"));
            Require(archived.Select(u => (string)u["utterance_id"]).SequenceEqual(lines.Select(u => (string)u["utterance_id"])) &&
                    archived.Select(u => (string)u["text"]).SequenceEqual(report.timelineTexts) &&
                    report.timelineTexts.All(t => txt.Contains(t)), "The durable timeline must agree with every final line.");
            report.singleTimelineVerified = report.tailFinalized = true;
        }
    }
}
