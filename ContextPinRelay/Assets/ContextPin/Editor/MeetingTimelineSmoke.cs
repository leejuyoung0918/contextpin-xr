using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
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
                if (!MarkCommand(app, true)) return false;
                var pending = AppType.GetField("explicitRequest", AppMembers).GetValue(app);
                silentRangeId = (string)pending.GetType().GetField("RangeId").GetValue(pending);
                silentEndTime = Time.realtimeSinceStartupAsDouble + .85;
                return false;
            }
            var silent = state["design_speech_ranges"]?[silentRangeId];
            if (silent == null) return false;
            if ((string)silent["status"] == "recording" && Time.realtimeSinceStartupAsDouble >= silentEndTime)
            {
                if (MarkCommand(app, false)) silentEndTime = double.PositiveInfinity;
                return false;
            }
            if ((string)silent["status"] != "no_speech") return false;
            Require(state["edit_requests"]?[silentRangeId] == null && state["candidate_sets"]?[silentRangeId] == null,
                "An empty command must never be submitted for CAD.");
            Require(!string.IsNullOrEmpty(ExplicitUi("ExplicitDesignStatus")), "An empty B range must show status feedback.");
            report.silentCommandRejected = true;
            QueueDiscussion("intro");
            if (!HasLine(state, "크기")) return false;
            Require(state["edit_requests"]?.HasValues != true && state["candidate_sets"]?.HasValues != true,
                "Ordinary conversation outside B must enter the timeline without creating a CAD request.");
            return true;
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
            int extra = IntegratedTest ? 1 : 0;
            Require(lines.Length == 7 + extra && lines.All(u => (bool)u["is_final"] && (string)u["meeting_id"] == sid &&
                    (string)u["source"] == "stt" && (int)u["revision"] > 0 && (long)u["end_ms"] >= (long)u["start_ms"] &&
                    ((JObject)u).ContainsKey("speaker_id")) && lines.Select(u => (string)u["utterance_id"]).Distinct().Count() == lines.Length,
                "Expected every ordinary and command utterance exactly once: " +
                string.Join(" / ", lines.Select(u => (string)u["text"])));
            report.timelineTexts = lines.Select(u => (string)u["text"]).ToArray();
            Require(report.timelineTexts[0].Contains("크기") && report.timelineTexts[2].Contains("기다") &&
                    report.timelineTexts[3].Contains("좋") && (!IntegratedTest || report.timelineTexts[4].Contains("공간")) &&
                    report.timelineTexts.Last().Contains("마칠"),
                "Ordinary discussion must bracket the commands and include the closing audio.");

            for (int index = 0; index < 2; index++)
            {
                var id = Path.GetFileName(Path.GetDirectoryName(report.captureFiles[index]));
                var bundle = state["edit_requests"][id];
                var window = state["design_speech_ranges"][id];
                var expected = index == 0 ? new[] { lines[1] } : new[] { lines[4+extra], lines[5+extra] };
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

        static bool CheckTimelineMinutes(JObject state)
        {
            string sid = (string)state["session_id"];
            var path = Path.Combine(Application.persistentDataPath, "MeetingExports", $"{sid}-r{state["result"]["revision"]}.zip");
            if (!File.Exists(path)) return false;
            using var archive = ZipFile.OpenRead(path);
            string Read(string name)
            {
                using var reader = new StreamReader(archive.GetEntry(name).Open());
                return reader.ReadToEnd();
            }
            var manifest = JObject.Parse(Read("manifest.json"));
            var minutes = JObject.Parse(Read("minutes/minutes.json"));
            var markdown = Read("minutes/minutes.md");
            Require((string)manifest["minutes_status"] == "generated" && (string)minutes["meeting_id"] == sid &&
                (string)minutes["final_version_id"] == report.finalVersion && minutes["discussion_points"].Any() &&
                minutes["design_decisions"].Count() == 2 && (string)minutes["generation"]["endpoint_mode"] == "local" &&
                (int)minutes["generation"]["calls"] > 0, "Local AI minutes must contain both adopted changes and ordinary discussion.");
            Require(markdown.Contains("미기록") && markdown.Contains("V2") && markdown.Contains("V3"),
                "The readable minutes must show the version history and unrecorded reasons.");
            var versions = new[] { report.initialVersion, report.middleVersion, report.finalVersion };
            var history = JArray.Parse(Read("design_history.json"));
            Require(history.Select(row => (string)row["version_id"]).SequenceEqual(versions), "The package must archive V1, V2 and V3.");
            for (int i = 0; i < 2; i++)
            {
                var actual = state["decisions"][i];
                var logged = minutes["design_decisions"][i];
                Require((string)logged["decision_id"] == (string)actual["decision_id"] &&
                    (string)logged["edit_request_id"] == (string)actual["edit_request_id"] &&
                    (string)logged["base_version_id"] == versions[i] && (string)logged["result_version_id"] == versions[i + 1] &&
                    (string)logged["selected_candidate_id"] == report.selectedCandidates[i] &&
                    JToken.DeepEquals(logged["parameter_diff"], actual["parameter_diff"]) &&
                    JToken.DeepEquals(logged["evidence_utterance_ids"], actual["command_utterance_ids"]) &&
                    logged["reason"]?.Type == JTokenType.Null, "Minutes must trace each choice to its actual edit and command IDs.");
            }
            var files = (JObject)manifest["files"];
            foreach (var entry in files.Properties())
            {
                var file = archive.GetEntry(entry.Name);
                Require(file != null && file.Length == (long)entry.Value["size_bytes"], "Missing or incomplete file: " + entry.Name);
                using var stream = file.Open();
                using var sha = SHA256.Create();
                var hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                Require(hash == (string)entry.Value["sha256"], "The download has a different hash: " + entry.Name);
            }
            var assets = state["versions"].Single(v => (string)v["version_id"] == report.finalVersion)["assets"];
            Require((string)files[(string)manifest["final_scad"]]["sha256"] == (string)assets["candidate.scad"]["sha256"] &&
                (string)files[(string)manifest["final_stl"]]["sha256"] == (string)assets["print.stl"]["sha256"],
                "Final SCAD and STL must be the adopted V3 files.");
            report.exportPath = path;
            report.minutesPackageVerified = report.latestZipAgrees = true;
            return true;
        }
    }
}
