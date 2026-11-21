using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ContextPin.Editor
{
    public static partial class MeetingSmoke
    {
        static int captureCorner, captureRoundBaseline, captureVersionBaseline, speechRingStart;
        static string captureId, captureTranscript, dataDirectory;
        static byte[] frozenJpeg;
        static Ray capturedAim;
        static Pose capturedEye, capturedLatest;
        static Vector3 capturedPoint, capturedLocal, capturedNormal;
        static long buttonEarliestMs, buttonLatestMs;
        static int previewsRequested, previewsReceived;
        static string previewError;
        static bool startRequested;

        static bool RunCaptureOnlyStep()
        {
            if (step == 0)
            {
                dataDirectory = Environment.GetEnvironmentVariable("MEETING_CAPTURE_DATA_DIR");
                Require(Directory.Exists(dataDirectory), "Set MEETING_CAPTURE_DATA_DIR to the isolated live server's EDGE_DATA_DIR.");
                InstallEditorPcmInput();
                startRequested = false;
                ResetTimelineSpeech();
                report.captureFiles = new string[2];
                report.selectedCandidates = new string[2];
                report.committedVersions = new string[2];
                return Click("MeetingCreate");
            }
            var app = UnityEngine.Object.FindAnyObjectByType<RelayApp>();
            var state = SpatialSnapshot();
            if (state == null) return false;
            var latest = GameObject.Find("MeetingPreviewPivot");
            if (step is >= 5 and <= 10) return CheckCaptureSequence(app, state, latest, 0, step - 5);
            if (step is >= 14 and <= 19) return CheckCaptureSequence(app, state, latest, 1, step - 14);
            var rounds = (JArray)state["rounds"];
            if (checkBundleAdoption && (step is >= 11 and <= 13 || step >= 20))
                return ReviewAndCommitStep(app, state, latest);
            switch (step)
            {
                case 1:
                    if (checkPhoneStand)
                        Require((string)state["session_status"] == "ready" && inputRing == null &&
                            report.microphoneOpenCount == 0 && !state["utterances"].Any(),
                            "Creating an unaligned phone meeting must not open a microphone or produce speech.");
                    else if ((string)state["session_status"] != "recording" || inputRing == null) return false;
                    if (!StartPaperFixture(app)) return false;
                    if (checkPhoneStand)
                        Require((string)state["profile"]["adapter_id"] == "openscad_phone_stand" &&
                            (string)state["profile"]["review_layout"] == "center_latest_side_candidates",
                            "The phone stand smoke requires the registered phone stand profile and its paper-relative review slots.");
                    previewsRequested = previewsReceived = 0;
                    previewError = null;
                    captureCorner = 0;
                    bool portrait = Environment.GetEnvironmentVariable("MEETING_SMOKE_PORTRAIT") == "1";
                    SetPaperFixture(portrait);
                    report.initialVersion = (string)state["adopted_version_id"];
                    return true;
                case 2:
                    TouchPaper(app, PaperCorner(captureCorner), captureCorner + 1);
                    performedFrame = Time.frameCount;
                    return ++captureCorner == 4;
                case 3: return PaperPreview(app, latest) && Click("AssemblyAlignmentConfirm");
                case 4:
                    if (latest == null || !PaperState(app,"A4ReviewSurfaceActive")) return false;
                    CheckConfirmedPaper(app, latest);
                    if (checkPhoneStand && !startRequested)
                    {
                        Require(report.microphoneOpenCount == 0 && (string)state["session_status"] == "ready",
                            "Completing four corners and confirming Latest must leave the microphone stopped.");
                        startRequested = Click("SpatialSessionAction");
                        report.microphoneWaitedForStart = startRequested;
                        return false;
                    }
                    if ((string)state["session_status"] != "recording" || inputRing == null) return false;
                    Require((string)state["design_submit_mode"] == "explicit" && (string)state["meeting"]["edit_request_output"] == "bundle",
                        "The new host must use EditRequestBundle.");
                    Require(((string)state["meeting"]["candidate_generation"] == "ai") == checkCandidateSet,
                        "The server must acknowledge the requested candidate generation mode.");
                    report.a4PortraitMeasured = explicitAreaSize.x < explicitAreaSize.y;
                    report.a4LandscapeMeasured = !report.a4PortraitMeasured;
                    Require(rounds.Count == 0, "A4 placement must not generate a candidate round.");
                    return true;
                // Independent, existing PC manual-CAD edit/commit. Its one round is explicitly
                // outside both capture intervals and lets the second request address a real V2.
                case 11:
                    SeedCommand(app, "edit", new JObject { ["base_version_id"] = report.initialVersion,
                        ["parameters"] = new JObject { ["slot_pitch"] = 14 } });
                    return true;
                case 12:
                    if (rounds.Count == 0) return false;
                    var seed = rounds.Single();
                    if ((string)seed["status"] == "failed") throw new InvalidOperationException("V2 fixture edit failed: " + seed["error"]);
                    if ((string)seed["status"] != "reviewable") return false;
                    var candidate = seed["candidates"].Single();
                    report.finalVersion = (string)candidate["version_id"];
                    SeedCommand(app, "decide", new JObject { ["round_id"] = seed["round_id"], ["revision"] = seed["revision"],
                        ["candidate_id"] = candidate["candidate_id"], ["outcome"] = "commit",
                        ["expected_committed_version_id"] = report.initialVersion, ["expected_adopted_revision"] = state["adopted_revision"] });
                    return true;
                case 13:
                    if (latest == null || Version(latest) != report.finalVersion || (string)state["adopted_version_id"] != report.finalVersion) return false;
                    Require(rounds.Count == 1 && (string)rounds[0]["status"] == "committed", "The independent setup may create only the one V2 round.");
                    return true;
                case 20: return Click("SpatialSessionAction");
                case 21: return Click("SpatialConfirmEnd");
                case 22:
                    if ((string)state["session_status"] != "completed") return false;
                    Require(rounds.Count == 1 && ((JObject)state["edit_requests"]).Count == 2 &&
                        ((JArray)state["images"]).Count == 2 && report.captureFiles.All(File.Exists),
                        "The completed meeting must preserve both captures and only the independent V2 setup round.");
                    Finish(null);
                    return true;
                default: throw new InvalidOperationException("Unexpected capture-only smoke step.");
            }
        }

        static void SeedCommand(RelayApp app, string type, JObject payload) =>
            AppType.GetMethod("SendMeetingCommand", AppMembers).Invoke(app, new object[] { type, payload, false, null });

        static bool PressCapture(RelayApp app, Ray ray) =>
            (bool)AppType.GetMethod("HandleExplicitTargetPress", AppMembers).Invoke(app, new object[] { ray, false });

        static bool CheckCaptureSequence(RelayApp app, JObject state, GameObject latest, int index, int phase)
        {
            string version = index == 0 ? report.initialVersion : checkBundleAdoption ? report.middleVersion : report.finalVersion;
            var rounds = (JArray)state["rounds"];
            var versions = (JArray)state["versions"];
            var images = (JArray)state["images"];
            var samples = index == 0 ? speechSamples : followupSamples;
            if (phase > 0)
                Require(rounds.Count == captureRoundBaseline && (checkCandidateSet || versions.Count == captureVersionBaseline),
                    "A bundle must not create a legacy round, and capture-only mode must not build CAD.");
            if (latest == null || Version(latest) != version) return false;
            switch (phase)
            {
                case 0:
                    if (!ExplicitUi("ExplicitLatest").Contains(index == 0 ? "V1" : "V2")) return false;
                    if (index == 0 && TimelineTest && !BeforeTimelineCommand(app, state)) return false;
                    CheckSideLatest(state, latest, version, true);
                    captureRoundBaseline = rounds.Count;
                    captureVersionBaseline = versions.Count;
                    if (!Click("ExplicitSpeechAction")) return false;
                    var pending = AppType.GetField("explicitRequest", AppMembers).GetValue(app);
                    captureId = (string)pending.GetType().GetField("RangeId").GetValue(pending);
                    speechRingStart = PcmCursor() + PcmRate / 8;
                    Require(inputRing.SetData(samples, speechRingStart), "Could not enqueue test speech.");
                    stopSpeechAt = Time.realtimeSinceStartupAsDouble + .125 + samples.Length / (double)PcmRate + .18;
                    return true;
                case 1:
                    if (!TryRightRay(latest, out var early)) return false;
                    Require(!PressCapture(app, early) && images.Count == index, "A during recording must not freeze or upload evidence.");
                    if (Time.realtimeSinceStartupAsDouble < stopSpeechAt || !Click("ExplicitSpeechAction")) return false;
                    Require(!PressCapture(app, early), "A before final STT, after stopping the mic range, must still be ignored.");
                    report.rejectedEarlyCapture = true;
                    return true;
                case 2:
                    var range = state["design_speech_ranges"]?[captureId];
                    if ((string)range?["status"] is "failed" or "no_speech") throw new InvalidOperationException("STT: " + range);
                    if ((string)range?["status"] != "final") return false;
                    captureTranscript = string.Join(" ", ((JArray)range["utterances"]).Select(u => (string)u["text"]));
                    if (string.IsNullOrWhiteSpace(captureTranscript) || !ExplicitUi("ExplicitTranscript").Contains(captureTranscript)) return false;
                    Require(images.Count == index && (state["edit_requests"]?[captureId] == null), "Final STT alone must not persist a capture.");
                    if (index == 0) report.finalMarkedSpeech = captureTranscript; else report.followupMarkedSpeech = captureTranscript;
                    RejectForeignCapture(app, version);
                    return true;
                case 3: return FreezeTrackedCapture(app, latest, index);
                case 4:
                    if (state["edit_requests"]?[captureId] == null) return false;
                    if (TimelineTest && index == 0) SpeakDuringGeneration(state);
                    if (checkBundleAdoption) return VerifyReviewModels(app, state, latest, index);
                    return checkCandidateSet ? CheckBundleCandidates(app, state, index, version) : ExplicitUi("ExplicitDesignStatus").Contains("저장 완료");
                default:
                    Require(images.Count == index + 1, "One A press must produce exactly one evidence image.");
                    ValidateSavedCapture(state, index, samples, version);
                    if (index == 0) report.savedV1Capture = true; else report.savedV2Capture = true;
                    report.captureHadNoNewRound = true;
                    return true;
            }
        }

        static void RejectForeignCapture(RelayApp app, string version)
        {
            // Give a comparison collider legitimate semantics and even the same version ID.
            // It still has to be rejected because the currently displayed Latest owns the selection.
            var slot = AppType.GetField("assemblyComparison", AppMembers).GetValue(app);
            var root = slot.GetType().GetField("Root");
            var previous = root.GetValue(slot);
            var preview = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                preview.transform.position = Camera.main.transform.position + Vector3.right * 5;
                preview.transform.localScale = Vector3.one * .02f;
                var hit = preview.AddComponent<RelayHit>();
                hit.role = "outer_shell";
                hit.meetingVersion = version;
                root.SetValue(slot, preview);
                Physics.SyncTransforms();
                var ray = new Ray(preview.transform.position - Vector3.forward * .1f, Vector3.forward);
                Require(Physics.Raycast(ray, out var found, .2f) && found.collider.gameObject == preview && !PressCapture(app, ray),
                    "A valid semantic collider from a comparison slot must be rejected.");
                report.rejectedForeignCollider = true;
            }
            finally { root.SetValue(slot, previous); preview.SetActive(false); UnityEngine.Object.Destroy(preview); }
        }

        static bool FreezeTrackedCapture(RelayApp app, GameObject latest, int index)
        {
            var eye = Camera.main.transform;
            var previousRotation = eye.rotation;
            try
            {
                var center = ModelBoundsInPose(latest, measuredPaper).center;
                eye.rotation = Quaternion.LookRotation(measuredPaper.position + measuredPaper.rotation * center - eye.position);
                if (checkCandidateSet)
                {
                    var pose = latest.transform;
                    var localBounds = ModelBoundsInPose(latest, new Pose(pose.position, pose.rotation));
                    var target = index == 0
                        ? new Vector3(localBounds.max.x - .0018f, localBounds.max.y, localBounds.min.z + .0018f)
                        : new Vector3(localBounds.center.x + .0026f, localBounds.max.y, localBounds.center.z);
                    // Ray onto the side rail (beside the window), then the retaining lip (beside its notch).
                    if (checkPhoneStand) target = new Vector3(.030f, localBounds.max.y, index == 0 ? .008f : .062f);
                    capturedAim = new Ray(pose.TransformPoint(target) + pose.up * .09f, -pose.up);
                }
                else if (!TryRightRay(latest, out capturedAim)) return false;
                if (!Physics.Raycast(capturedAim, out var hit, 12)) return false;
                Require(hit.collider.transform.IsChildOf(latest.transform), "The chosen ray must hit the visible Latest mesh.");
                capturedEye = new Pose(eye.position, eye.rotation);
                capturedLatest = new Pose(latest.transform.position, latest.transform.rotation);
                capturedPoint = hit.point;
                capturedNormal = hit.normal;
                capturedLocal = latest.transform.InverseTransformPoint(hit.point);
                if (checkPhoneStand)
                    Require(index == 0 ? capturedLocal.y > .045f : Mathf.Abs(capturedLocal.y - .022f) < .001f,
                        "The ray must hit the inclined rail, then the top of the retaining lip.");
                // The fixture moves the actual XR controller and gaze away after the button frame.
                buttonEarliestMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Require(PressRightWithLeftActive(app, capturedAim), "A must freeze a tracked Latest hit: " + ExplicitUi("ExplicitDesignStatus"));
                buttonLatestMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var pending = AppType.GetField("explicitRequest", AppMembers).GetValue(app);
                var selection = pending.GetType().GetField("Selection").GetValue(pending);
                frozenJpeg = (byte[])selection.GetType().GetField("Image").GetValue(selection);
                Require(frozenJpeg != null && frozenJpeg.Length > 256 && !PressCapture(app, capturedAim),
                    "Repeated A must not recapture this request.");
            }
            finally { eye.rotation = previousRotation; }
            return true;
        }

        static bool CheckBundleCandidates(RelayApp app, JObject state, int index, string version)
        {
            var set = state["candidate_sets"]?[captureId];
            if (set == null) return false;
            Require((string)set["request_id"] == captureId && (string)set["base_version_id"] == version &&
                (string)state["adopted_version_id"] == version, "The set must address this bundle's unmodified adopted base.");
            string status = (string)set["status"];
            if (status is "building" or "generating") return false;
            if (index == 0)
            {
                Require((status is "clarification" or "unsupported_specific_region") && !set["candidates"].Any() &&
                    !string.IsNullOrWhiteSpace((string)set["reason"]), "The registered corner radius is fixed: " + set);
                report.cornerResult = status;
                return ExplicitUi("ExplicitDesignStatus").Contains("확인 필요") || ExplicitUi("ExplicitDesignStatus").Contains("미지원");
            }
            Require(status == "ready" && set["candidates"] is JArray {Count: 2}, "Expected two validated groove models: " + set);
            if (!ExplicitUi("ExplicitDesignStatus").Contains("수정안 2개 준비됨")) return false;
            var candidates = (JArray)set["candidates"];
            if (previewsRequested == 0)
            {
                var baseline = state["versions"].Single(v => (string)v["version_id"] == version)["parameters"] as JObject;
                Require(Math.Abs((double)baseline["slot_pitch"] - 14) < .001, "The independent edit must be the V2 fixture.");
                report.grooveCandidateSetId = (string)set["candidate_set_id"];
                report.candidatePreviewUrls = new string[2];
                report.candidatePreviewHashes = new string[2];
                var transport = AppType.GetField("meetingTransport", AppMembers).GetValue(app);
                foreach (var item in candidates)
                {
                    var diff = item["parameter_diff"] as JArray;
                    Require(!string.IsNullOrEmpty((string)item["candidate_id"]) && !string.IsNullOrWhiteSpace((string)item["interpretation"]) &&
                        diff?.Count > 0 && diff.All(change => ((string)change["parameter_id"] is "throat_width" or "mouth_width" or "groove_diameter") &&
                            (double)change["after"] > (double)change["before"]), "The widening request must produce a real groove change.");
                    foreach (var field in baseline.Properties())
                    {
                        var patch = diff.SingleOrDefault(entry => (string)entry["parameter_id"] == field.Name);
                        var expected = (patch?["after"] ?? field.Value).Value<double>();
                        Require(Math.Abs(item["full_parameters"][field.Name].Value<double>() - expected) < .00001,
                            "Every candidate must inherit the entire V2 plus its validated patch.");
                    }
                    var asset = item["model_asset"];
                    int i = previewsRequested++;
                    string url = (string)asset["url"];
                    string hash = (string)asset["sha256"];
                    report.candidatePreviewUrls[i] = url;
                    report.candidatePreviewHashes[i] = hash;
                    Require(!string.IsNullOrEmpty((string)item["stl"]?["url"]) && !string.IsNullOrEmpty((string)item["scad"]?["url"]),
                        "Each built preview must have the associated STL and SCAD URLs.");
                    Action<byte[], string> verify = (bytes, error) => {
                        if (error != null || bytes == null || bytes.Length < 12 || Encoding.ASCII.GetString(bytes, 0, 4) != "glTF")
                            previewError = error ?? "Could not download a binary glTF candidate.";
                        else
                        {
                            using var sha = SHA256.Create();
                            if (BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() != hash)
                                previewError = "Downloaded candidate does not match the declared GLB hash.";
                        }
                        previewsReceived++;
                    };
                    app.StartCoroutine((IEnumerator)transport.GetType().GetMethod("Download").Invoke(transport,
                        new object[] {url, 16 * 1024 * 1024, verify}));
                }
                Require(report.candidatePreviewHashes.Distinct().Count() == 2 &&
                    candidates.Select(c => (string)c["stl"]["sha256"]).Distinct().Count() == 2 &&
                    candidates.Select(c => (string)c["candidate_id"]).Distinct().Count() == 2,
                    "The two candidate IDs, STL files and preview meshes must be distinct.");
            }
            Require(previewError == null, previewError);
            return previewsReceived == 2;
        }

        static Vector3 ReadWorldVector(JToken value) => new Vector3((float)value[0], (float)value[1], (float)value[2]);

        static void ValidateSavedCapture(JObject state, int index, float[] samples, string version)
        {
            var sid = (string)state["session_id"];
            var directory = Path.Combine(dataDirectory, "edit_requests", sid, captureId);
            var jsonPath = Path.Combine(directory, "request.json");
            var bundle = JObject.Parse(File.ReadAllText(jsonPath));
            var selection = bundle["selection"];
            var visual = bundle["visual_evidence"];
            var speech = bundle["transcript"];
            Require(JToken.DeepEquals(bundle, state["edit_requests"][captureId]) &&
                (string)bundle["request_id"] == captureId && (string)bundle["meeting_id"] == sid &&
                (string)bundle["base_version_id"] == version && (string)selection["base_version_id"] == version &&
                (string)selection["selection_id"] == (string)visual["selection_id"] &&
                (string)selection["object_id"] == (string)state["profile"]["object_id"],
                "The stored request must bind this meeting, current version, captured collider and single selection ID.");
            long stamp = new DateTimeOffset(selection.Value<DateTime>("timestamp").ToUniversalTime()).ToUnixTimeMilliseconds();
            Require(stamp >= buttonEarliestMs && stamp <= buttonLatestMs && selection.Value<long>("workspace_revision") > 0,
                "The evidence timestamp must be inside the observed button frame and carry a confirmed workspace revision.");
            bool Near(JToken vector, Vector3 expected) => Vector3.Distance(ReadWorldVector(vector), expected) < .001f;
            var rotation = visual["camera_pose"]["rotation_xyzw"];
            var cameraOrientation = new Quaternion((float)rotation[0], (float)rotation[1], (float)rotation[2], (float)rotation[3]);
            Require(Near(selection["ray_origin_world"], capturedAim.origin) && Near(selection["ray_direction_world"], capturedAim.direction) &&
                Near(selection["hit_world_position"], capturedPoint) && Near(selection["hit_model_local_position"], capturedLocal) &&
                Near(selection["hit_normal_world"], capturedNormal) && Near(visual["camera_pose"]["position"], capturedEye.position) &&
                Near(selection["workspace_pose"]["position"], measuredPaper.position) && Near(selection["latest_model_pose"]["position"], capturedLatest.position) &&
                Quaternion.Angle(cameraOrientation, capturedEye.rotation) < .1f, "The stored pose, ray, local hit and normal must match the A frame after controller motion.");
            report.frozenTargetVerified = true;
            Require((string)visual["image_source"] == "rendered_debug" && (string)visual["actual_rgb"] == "unavailable" &&
                !string.IsNullOrEmpty((string)visual["image_asset_id"]) && visual["capture_timestamp"] != null,
                "A simulator rendering must identify its actual source and unavailable RGB capture.");
            var jpeg = File.ReadAllBytes(Path.Combine(directory, "target.jpg"));
            Require(jpeg.SequenceEqual(frozenJpeg) && jpeg[0] == 0xff && jpeg[1] == 0xd8 && jpeg[jpeg.Length-2] == 0xff && jpeg[jpeg.Length-1] == 0xd9,
                "Saved evidence must be the exact renderer JPEG captured at A, not a later frame.");
            var transcriptFile = JToken.Parse(File.ReadAllText(Path.Combine(directory, "transcript.json")));
            Require((string)speech["source"] == "stt" && (string)speech["text"] == captureTranscript &&
                !string.IsNullOrEmpty((string)speech["utterance_id"]) && speech["start_time"] != null && speech["end_time"] != null &&
                JToken.DeepEquals(transcriptFile, speech), "The final transcribed range must preserve its text, evidence ID, times and STT origin.");
            ValidateCaptureWave(File.ReadAllBytes(Path.Combine(directory, "command.wav")), samples);
            report.savedBundleMediaVerified = true;
            report.captureFiles[index] = jsonPath;
        }

        static void ValidateCaptureWave(byte[] wav, float[] fixture)
        {
            using var reader = new BinaryReader(new MemoryStream(wav));
            string Tag() => Encoding.ASCII.GetString(reader.ReadBytes(4));
            Require(Tag() == "RIFF" && reader.ReadUInt32() + 8 == wav.Length && Tag() == "WAVE", "Expected a complete WAV container.");
            Require(Tag() == "fmt " && reader.ReadUInt32() == 16 && reader.ReadUInt16() == 1 && reader.ReadUInt16() == 1 &&
                reader.ReadInt32() == PcmRate, "Saved command must be mono PCM16 at 16 kHz.");
            reader.ReadInt32();
            Require(reader.ReadUInt16() == 2 && reader.ReadUInt16() == 16 && Tag() == "data", "Invalid WAV PCM data.");
            int frames = checked((int)reader.ReadUInt32() / 2);
            Require(frames >= fixture.Length && reader.BaseStream.Position + frames * 2 == wav.Length, "The WAV must cover the spoken fixture.");
            // Check a known voiced passage using exactly the production signed-PCM conversion.
            int peak = Array.IndexOf(fixture, fixture.Max());
            peak = Mathf.Clamp(peak - 32, 0, fixture.Length - 64);
            var expected = fixture.Skip(peak).Take(64).Select(v => (short)Mathf.RoundToInt(v * 32767f)).ToArray();
            var voice = new short[frames];
            for (int i = 0; i < frames; i++) voice[i] = reader.ReadInt16();
            Require(expected.Any(v => v != 0) && Enumerable.Range(0, frames-63)
                .Any(i => voice[i] == expected[0] && expected.SequenceEqual(new ArraySegment<short>(voice, i, 64))),
                "The stored WAV must contain the original spoken PCM samples.");
        }
    }
}
