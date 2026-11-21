using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.IO.Compression;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR;

namespace ContextPin.Editor
{
    public static partial class MeetingSmoke
    {
        [Serializable]
        sealed class Report
        {
            public bool success, simulator, assembly, fixedMount, syntheticReference, invalidatedOverlay;
            public bool compactSpatialPanel, replayMeeting, quietProposal, explicitAdoption, savedMeeting, syntheticPcm;
            public bool besideComparison, stableComparisonSlot, comparisonDismissed, comparisonHiddenOnLoss, syntheticQr;
            public bool cableDemo, cableFirstOptions, cableSecondOptions, cableDesignHistory;
            public bool sharedSlider, physicalIdentity, selectionSnapshot;
            public bool panelDragged, automaticComparison, serverMinutesCode;
            public bool savedSpeechDesign, localPreviewVisible, activeDesignButton;
            public bool alignmentWithoutCandidates, neutralSpeechWithoutCandidates, spokenEnlargement, tableGrounded, hiddenUntilTable;
            public bool rejectedUnevenTablePoint, measuredTable;
            public bool detectedRotatedDeskVolume, rejectedNonDeskAnchors;
            public bool groovePreviewRow, groovePreviewToggle, groovePreviewHiddenDuringMeeting;
            public bool explicitHostStarted, speechAloneWaited, blockedPanelA, duplicateSelectionIgnored;
            public bool capturedRenderedHit, explicitCandidateMeshes, physicalV1AndLatest, latestZipAgrees;
            public bool rightButtonEdgeVerified;
            public bool a4InvalidCornersRejected, a4CornerRetaken, a4PortraitMeasured;
            public bool a4LandscapeMeasured, a4TrackingRecovered, a4FullSizeGallery;
            public bool previewKeptLatest, v2Replaced, v3Replaced, v3Recalibrated;
            public bool centerHasNoWorldLabel;
            public float maxLetterWidthMm, maxLetterHeightMm;
            public bool rejectedEarlyCapture, rejectedForeignCollider, savedV1Capture, savedV2Capture;
            public bool captureHadNoNewRound, frozenTargetVerified, savedBundleMediaVerified;
            public bool bundleModelsPlaced, bundleRaySelection, bundleCommit, secondEditRound;
            public bool fixedPaperSlots, singleCandidateKeptSlot, oversizedCandidateRejected;
            public bool legacyCenteredLayoutRejected;
            public bool microphoneWaitedForStart, silentCommandRejected, spokeWhileGenerating, spokeWhileComparing;
            public bool singleTimelineVerified, tailFinalized;
            public bool minutesGeneratingShown, minutesSavedShown, minutesPackageVerified;
            public bool numberedCornersOppositeView, paperCandidatesAgree, paperLatestAgrees, paperHitAgrees;
            public bool paperCommitReceived, paperLateJoin, paperLostHidden, paperRealigned;
            public int microphoneOpenCount;
            public string[] timelineTexts;
            public float minimumPaperClearanceMm;
            public string[] selectedCandidates, committedVersions;
            public string[] captureFiles;
            public string cornerResult, grooveCandidateSetId;
            public string[] candidatePreviewUrls, candidatePreviewHashes;
            public int latestAnchorId;
            public string middleVersion, followupMarkedSpeech;
            public long speechStartSample;
            public string finalMarkedSpeech, exportPath, controllerInputError;
            public string runtime, initialVersion, finalVersion, error;
            public float initialWidthM, finalWidthM, stlWidthMm;
            public int renderViews;
            public List<string> rayActions = new List<string>();
        }

        static Report report;
        static bool checkSavedDesign, checkLiveAlignment, checkExplicit, checkCaptureOnly, checkCandidateSet, checkBundleAdoption, checkPhoneStand;
        static Pose reviewTablePose;
        static string savedSessionId;
        static double deadline;
        static int step, performedFrame;
        static string candidateControl, comparisonFirstVersion, cableVersionTwo;
        static int sliderStep, groovePreviewStep;
        static string desktopParameter;
        static double desktopRequestedValue;
        static Vector3 originalPosition, panelPosition, comparisonPosition;
        static Quaternion originalRotation;
        static Vector3[] originalVertices;
        static readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();

        public static void Begin(bool simulator)
        {
            report = new Report { simulator = simulator, assembly = Environment.GetEnvironmentVariable("MEETING_SMOKE_ASSEMBLY") == "1" };
            checkSavedDesign = Environment.GetEnvironmentVariable("MEETING_SAVED_DESIGN_SMOKE") == "1";
            checkLiveAlignment = Environment.GetEnvironmentVariable("MEETING_LIVE_ALIGNMENT_SMOKE") == "1";
            checkExplicit = Environment.GetEnvironmentVariable("MEETING_EXPLICIT_SMOKE") == "1";
            checkBundleAdoption = Environment.GetEnvironmentVariable("MEETING_CANDIDATE_COMMIT_SMOKE") == "1";
            checkPhoneStand = checkBundleAdoption && Environment.GetEnvironmentVariable("MEETING_PHONE_STAND_SMOKE") == "1";
            checkCandidateSet = checkBundleAdoption || Environment.GetEnvironmentVariable("MEETING_CANDIDATE_SET_SMOKE") == "1";
            checkCaptureOnly = checkCandidateSet || Environment.GetEnvironmentVariable("MEETING_CAPTURE_ONLY_SMOKE") == "1";
            UnityEditor.SessionState.SetBool("ContextPinLegacyCandidateWorkflow", checkExplicit && !checkCaptureOnly);
            UnityEditor.SessionState.SetBool("ContextPinCaptureOnlyWorkflow", checkCaptureOnly && !checkCandidateSet);
            step = 0;
            groovePreviewStep = -1;
            performedFrame = -1;
            deadline = EditorApplication.timeSinceStartup + (checkCandidateSet ? 600 : checkExplicit || checkCaptureOnly ? 240 : report.assembly && simulator ? 125 : 65);
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            try
            {
                if (EditorApplication.timeSinceStartup > deadline)
                    throw new TimeoutException($"Meeting smoke step {step} timed out. UI: " + GameObject.Find("Notice")?.GetComponent<Text>()?.text);
                if (performedFrame == Time.frameCount || Camera.main == null) return;
                if (report.simulator)
                {
                    SubsystemManager.GetSubsystems(displays);
                    report.renderViews = 0;
                    foreach (var display in displays.Where(reported => reported.running))
                        for (int i = 0; i < display.GetRenderPassCount(); i++)
                        { display.GetRenderPass(i, out var pass); report.renderViews += pass.GetRenderParameterCount(); }
                    report.runtime = OpenXRRuntime.name;
                    if (report.renderViews < 2 || report.runtime.IndexOf("simulat", StringComparison.OrdinalIgnoreCase) < 0) return;
                }
                bool advance = ColocationTest && step >= 11 ? RunPaperGuestStep() : checkCaptureOnly ? RunCaptureOnlyStep() : checkExplicit ? RunExplicitStep() : checkLiveAlignment ? RunLiveAlignmentStep() : checkSavedDesign ? RunSavedDesignStep()
                    : report.assembly ? (report.simulator ? RunSpatialStep() : RunAssemblyStep()) : RunStep();
                if (advance) { step++; performedFrame = Time.frameCount; }
            }
            catch (Exception error) { Finish(error); }
        }

        // Exercise the saved result screen with an isolated live server and manual test
        // speech. Real microphone transcription is exercised by the separate audio test.
        static bool CreateLegacyMeeting()
        {
            if (GameObject.Find("MeetingCreate") == null) return false;
            var app = UnityEngine.Object.FindAnyObjectByType<RelayApp>();
            typeof(RelayApp).GetMethod("BeginMeetingSession", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(app, new object[] { false, false });
            return true;
        }

        static bool RunSavedDesignStep()
        {
            if (step == 0) return CreateLegacyMeeting();
            var state = SpatialSnapshot();
            if (state == null) return false;
            var app = UnityEngine.Object.FindAnyObjectByType<RelayApp>();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var rounds = state["rounds"] as JArray;
            void Submit(string type, JObject body) =>
                typeof(RelayApp).GetMethod("SendMeetingCommand", flags).Invoke(app, new object[] { type, body, false, null });

            switch (step)
            {
                case 1:
                    Require((string)state["provider_mode"] == "live", "Use an isolated live cable server for this smoke.");
                    savedSessionId = (string)state["session_id"];
                    Submit("meeting_start", new JObject { ["mode"] = "live", ["input_mode"] = "manual" });
                    return true;
                case 2:
                    if ((string)state["session_status"] != "recording") return false;
                    Submit("text", new JObject { ["text"] = "충전선이 너무 헐거워." });
                    return true;
                case 3:
                    if ((state["utterances"] as JArray)?.Count != 1) return false;
                    report.activeDesignButton = GameObject.Find("SpatialDesignRequest") != null;
                    Require(report.activeDesignButton, "An unaligned live meeting must expose the design action.");
                    return Click("SpatialSessionAction");
                case 4: return Click("SpatialConfirmEnd");
                case 5:
                    if ((string)state["session_status"] != "completed" || rounds == null) return false;
                    Require(rounds.Count == 0, "Saved parent should contain the transcript and no proposals.");
                    if (GameObject.Find("SpatialSavedDesign") == null) { Click("SpatialMenu"); return false; }
                    return Click("SpatialSavedDesign");
                case 6:
                    if ((string)state["session_id"] == savedSessionId || rounds?.Count != 1) return false;
                    var round = rounds.Last;
                    if ((string)round["status"] == "failed" || (string)round["status"] == "needs_input")
                        throw new InvalidOperationException("Unexpected saved proposal state: " + (round["error"] ?? round["question"]));
                    if ((string)round["status"] != "reviewable") return false;
                    var candidates = (JArray)round["candidates"];
                    Require(candidates.Count == 3, "The loose cable request should yield three different choices.");
                    if (GameObject.Find("SpatialComparison") == null) return false;
                    if (!candidates.All(c => GameObject.Find("SpatialCandidate-" + (string)c["candidate_id"]) != null)) return false;
                    report.finalVersion = (string)candidates[1]["version_id"];
                    Require(GameObject.Find("MeetingPreviewPivot") == null && ActiveAlternatives().Length == 0,
                        "CAD must stay hidden until a physical location or review table has been registered.");
                    report.hiddenUntilTable = true;
                    UseSyntheticReviewTable();
                    return true;
                case 7:
                    Require(rounds?.Count == 1, "Registering a table must preserve the existing proposal.");
                    var second = rounds.Last["candidates"][1];
                    return Click("SpatialCandidate-" + (string)second["candidate_id"]);
                case 8:
                    if ((string)typeof(RelayApp).GetProperty("SpatialComparisonLoadedVersion", flags).GetValue(app) != report.finalVersion)
                        return false;
                    report.localPreviewVisible = (bool)typeof(RelayApp).GetProperty("SpatialComparisonModelVisible", flags).GetValue(app);
                    Require(report.localPreviewVisible, "The selected candidate must be visible on the registered table without QR.");
                    Require(GameObject.Find("PhysicalOriginalPivot") == null, "Virtual-only review must keep the physical overlay hidden.");
                    var tabletopModels = ActiveAlternatives();
                    Require(tabletopModels.Any(model => Version(model) == report.finalVersion), "The selected GLB must match candidate B.");
                    foreach (var model in tabletopModels) CheckTableContact(model);
                    var tabletopOriginal = GameObject.Find("MeetingPreviewPivot");
                    if (tabletopOriginal != null) CheckTableContact(tabletopOriginal);
                    report.tableGrounded = true;
                    return Click("SpatialAdopt");
                case 9:
                    if ((string)state["adopted_version_id"] != report.finalVersion) return false;
                    report.savedSpeechDesign = report.explicitAdoption = true;
                    Finish(null);
                    return true;
                default: throw new InvalidOperationException("Unexpected saved design step.");
            }
        }

        static GameObject[] ActiveAlternatives() =>
            new[] { "SpatialComparisonPivot", "CableComparison-A", "CableComparison-B", "CableComparison-C" }
                .Select(GameObject.Find).Where(model => model != null).ToArray();

        static void RequireQuietAlignment(JObject state)
        {
            Require(((JArray)state["rounds"]).Count == 0 && ActiveAlternatives().Length == 0 &&
                GameObject.Find("SpatialComparison") == null && GameObject.Find("GroovePreviewGallery") == null &&
                GameObject.Find("SpatialGroovePreviewToggle") == null,
                "Alignment, tabletop registration and casual speech must not produce a live proposal or a sample gallery.");
        }

        static readonly Vector2 SmokeTableSize = new Vector2(1.8f, .7f);

        static void CheckSceneDeskGeometry()
        {
            var read = typeof(RelayApp).GetMethod("TryReadDeskTop", BindingFlags.Static | BindingFlags.NonPublic);
            var anchorType = read.GetParameters()[0].ParameterType;
            var fixture = new GameObject("Inactive MRUK desk test fixture");
            fixture.SetActive(false);
            try
            {
                var anchor = fixture.AddComponent(anchorType);
                var label = anchorType.GetProperty("Label");
                var volume = anchorType.GetProperty("VolumeBounds");
                var plane = anchorType.GetProperty("PlaneRect");
                var args = new object[] { anchor, default(Pose), Vector2.zero };
                bool Read() => (bool)read.Invoke(null, args);
                void LabelAs(string name) => label.SetValue(anchor, Enum.Parse(label.PropertyType, name));
                LabelAs("TABLE");
                var t = fixture.transform;
                var bounds = new Bounds(new Vector3(.08f, -.06f, .10f), new Vector3(.54f, 1.2f, .36f));
                volume.SetValue(anchor, bounds);
                // Local Z points down, so the upper surface is on the negative local Z face.
                t.SetPositionAndRotation(new Vector3(1.2f, .75f, 2f),
                    Quaternion.AngleAxis(35, Vector3.up) * Quaternion.AngleAxis(90, Vector3.right));
                Require(Read(), "TABLE volumes with vertical local Z must yield a world tabletop.");
                var top = (Pose)args[1];
                report.detectedRotatedDeskVolume =
                    Vector3.Distance(top.position, t.TransformPoint(bounds.center) + Vector3.up * .18f) < .001f &&
                    Vector3.Dot(top.rotation * Vector3.up, Vector3.up) > .999f &&
                    Mathf.Abs(Vector3.Dot(top.rotation * Vector3.right, t.up)) > .999f &&
                    Vector2.Distance((Vector2)args[2], new Vector2(1.2f, .54f)) < .001f;
                Require(report.detectedRotatedDeskVolume, "Detected tabletop must use the upper face, its world orientation and both surface dimensions.");
                LabelAs("FLOOR");
                Require(!Read(), "Floor semantics must reject even an otherwise valid desk-shaped volume.");
                LabelAs("TABLE");
                volume.SetValue(anchor, null);
                plane.SetValue(anchor, new Rect(-.4f, -.2f, .8f, .4f));
                t.rotation = Quaternion.identity;
                Require(!Read(), "A vertical TABLE plane cannot support a CAD model.");
                t.rotation = Quaternion.AngleAxis(-90, Vector3.right);
                Require(Read(), "The same TABLE plane must be accepted when horizontal.");
                report.rejectedNonDeskAnchors = true;
            }
            finally { UnityEngine.Object.Destroy(fixture); }
        }

        static void UseSyntheticReviewTable(bool measureCorners = false)
        {
            if (!report.detectedRotatedDeskVolume) CheckSceneDeskGeometry();
            var camera = Camera.main.transform;
            var forward = Vector3.ProjectOnPlane(camera.forward, Vector3.up).normalized;
            reviewTablePose = new Pose(camera.position + forward * .95f - Vector3.up * .4f,
                Quaternion.LookRotation(forward, Vector3.up));
            var app = UnityEngine.Object.FindAnyObjectByType<RelayApp>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var appType = typeof(RelayApp);
            if (!measureCorners)
            {
                var useTable = appType.GetMethod("TryUseReviewTable", flags);
                Require((bool)useTable.Invoke(app, new object[] { reviewTablePose, SmokeTableSize, true }),
                    "The review tabletop must accept a measured horizontal surface.");
                return;
            }

            // Exercise the same measurement handlers invoked by a controller's A button.
            appType.GetMethod("BeginTablePlacement", flags).Invoke(app, null);
            var record = appType.GetMethod("RecordTablePoint", flags);
            void PressAt(Vector3 point) => record.Invoke(app, new object[] { point });
            var across = reviewTablePose.rotation * Vector3.right * SmokeTableSize.x;
            var back = reviewTablePose.rotation * Vector3.forward * SmokeTableSize.y;
            var frontLeft = reviewTablePose.position - .5f * (across + back);
            var frontRight = frontLeft + across;
            PressAt(frontLeft);
            PressAt(frontRight + Vector3.up * .08f);
            report.rejectedUnevenTablePoint = (int)appType.GetField("tableSample", flags).GetValue(app) == 2 &&
                !(bool)appType.GetProperty("HasReviewTable", flags).GetValue(app);
            Require(report.rejectedUnevenTablePoint, "A point eight centimetres above the first corner must be rejected and retryable.");
            PressAt(frontRight);
            PressAt(frontRight + back);
            var surface = (Pose)appType.GetField("reviewTable", flags).GetValue(app);
            var area = (Vector2)appType.GetField("reviewTableSize", flags).GetValue(app);
            report.measuredTable = (bool)appType.GetProperty("HasReviewTable", flags).GetValue(app) &&
                !(bool)appType.GetProperty("ReviewTableSampling", flags).GetValue(app) &&
                Vector3.Distance(surface.position, reviewTablePose.position) < .001f &&
                Quaternion.Angle(surface.rotation, reviewTablePose.rotation) < .1f && Vector2.Distance(area, SmokeTableSize) < .001f;
            Require(report.measuredTable, "Correcting the second corner and measuring the third must register the real tabletop bounds.");
        }

        static void CheckTableContact(GameObject model)
        {
            var inverse = Quaternion.Inverse(reviewTablePose.rotation);
            var vertices = model.transform.GetChild(0).GetComponentsInChildren<MeshFilter>().SelectMany(filter =>
                filter.sharedMesh.vertices.Select(vertex => inverse * (filter.transform.TransformPoint(vertex) - reviewTablePose.position))).ToArray();
            float lowest = vertices.Min(vertex => vertex.y);
            Require(Mathf.Abs(lowest) < .002f, $"{model.name} bottom is {lowest:F4} m above the table.");
            // The phone stand uses the paper to measure the tabletop pose, not its extents.
            if (checkPhoneStand) return;
            var area = checkExplicit || checkCaptureOnly ? explicitAreaSize : SmokeTableSize;
            Require(vertices.All(vertex => Mathf.Abs(vertex.x) <= area.x * .5f + .001f &&
                Mathf.Abs(vertex.z) <= area.y * .5f + .001f), $"{model.name} must fit inside the table edges.");
        }

        static bool RunLiveAlignmentStep()
        {
            const string request = "케이블 홈 크기를 좀 키워줘.";
            if (step == 0) return CreateLegacyMeeting();
            var state = SpatialSnapshot();
            if (state == null) return false;
            var app = UnityEngine.Object.FindAnyObjectByType<RelayApp>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            void Submit(string type, JObject body) =>
                typeof(RelayApp).GetMethod("SendMeetingCommand", flags).Invoke(app, new object[] { type, body, false, null });
            switch (step)
            {
                case 1:
                    Require((string)state["provider_mode"] == "live", "Start an isolated live cable server for the live alignment smoke.");
                    return Click("AssemblyAlignmentStart");
                case 2: return CheckSyntheticAssemblyReference() && CheckSyntheticQrReference();
                case 3: return Click("AssemblyAlignmentConfirm");
                case 4:
                    var original = GameObject.Find("MeetingPreviewPivot");
                    if (original == null || GameObject.Find("SpatialSummary") == null) return false;
                    RequireAssemblyMesh(original);
                    report.initialVersion = Version(original);
                    RequireQuietAlignment(state);
                    report.alignmentWithoutCandidates = true;
                    UseSyntheticReviewTable();
                    return true;
                case 5:
                    RequireQuietAlignment(state);
                    Submit("meeting_start", new JObject { ["mode"] = "live", ["input_mode"] = "manual" });
                    return true;
                case 6:
                    if ((string)state["session_status"] != "recording") return false;
                    Submit("text", new JObject { ["text"] = "안녕하세요. 회의를 시작했어요." });
                    return true;
                case 7:
                    if (((JArray)state["utterances"]).Count != 1) return false;
                    RequireQuietAlignment(state);
                    report.neutralSpeechWithoutCandidates = true;
                    Submit("text", new JObject { ["text"] = request });
                    return true;
                case 8:
                    var round = ((JArray)state["rounds"]).SingleOrDefault();
                    if (round == null) return false;
                    if ((string)round["status"] == "failed" || (string)round["status"] == "needs_input")
                        throw new InvalidOperationException("Unexpected live request result: " + (round["error"] ?? round["question"]));
                    if ((string)round["status"] != "reviewable") return false;
                    var versions = ((JArray)state["versions"]).ToDictionary(version => (string)version["version_id"]);
                    var candidates = (JArray)round["candidates"];
                    Require(candidates.Count == 3 && round["context"]["selected_utterances"].Any(u => (string)u["text"] == request),
                        "The three designs must be linked to the submitted enlargement request.");
                    Require((string)state["adopted_version_id"] == report.initialVersion,
                        "Displaying interpretations must leave the original adopted.");
                    var ids = candidates.Select(c => (string)c["version_id"]).ToArray();
                    var loaded = ActiveAlternatives();
                    if (GameObject.Find("SpatialComparison") == null || !ids.All(id => loaded.Any(model => Version(model) == id))) return false;
                    Require(GameObject.Find("SpatialProposalSource")?.GetComponent<Text>()?.text.Contains(request) == true,
                        "The proposal panel must show the actual request that produced these CAD versions.");
                    var baseParams = versions[report.initialVersion]["parameters"];
                    var interpretations = new HashSet<(bool entrance, bool interior)>();
                    foreach (var candidate in candidates)
                    {
                        var p = versions[(string)candidate["version_id"]]["parameters"];
                        bool entrance = p.Value<float>("throat_width") > baseParams.Value<float>("throat_width") ||
                            p.Value<float>("mouth_width") > baseParams.Value<float>("mouth_width");
                        bool interior = p.Value<float>("groove_diameter") > baseParams.Value<float>("groove_diameter");
                        Require((entrance || interior) && interpretations.Add((entrance, interior)),
                            "Each live interpretation must enlarge the requested opening, seat or both.");
                        Require(GameObject.Find("SpatialCandidate-" + (string)candidate["candidate_id"]) != null,
                            "Every generated version must have a selectable control.");
                    }
                    foreach (var model in loaded) { RequireAssemblyMesh(model); CheckTableContact(model); }
                    report.tableGrounded = true;
                    report.spokenEnlargement = true;
                    report.finalVersion = ids[1];
                    return Click("SpatialCandidate-" + (string)candidates[1]["candidate_id"]);
                case 9:
                    if ((string)typeof(RelayApp).GetProperty("SpatialComparisonLoadedVersion", flags).GetValue(app) != report.finalVersion)
                        return false;
                    Require((bool)typeof(RelayApp).GetProperty("SpatialComparisonModelVisible", flags).GetValue(app),
                        "The selected candidate must be displayed.");
                    Finish(null);
                    return true;
                default: throw new InvalidOperationException("Unexpected live alignment smoke step.");
            }
        }

        static bool RunStep()
        {
            var pivot = GameObject.Find("MeetingPreviewPivot");
            switch (step)
            {
                case 0: return GameObject.Find("MeetingCreate") != null && GameObject.Find("HomeMeeting") == null;
                case 1: return Click("MeetingCreate");
                case 2:
                    if (pivot == null) return false;
                    report.initialVersion = Version(pivot);
                    report.initialWidthM = ModelWidth(pivot);
                    Require(Mathf.Abs(report.initialWidthM - .1f) < .0001f, "Default GLB model must be 0.1m wide.");
                    Require(pivot.GetComponentsInChildren<MeshRenderer>().All(r => r.sharedMaterial != null && r.sharedMaterial.shader != null &&
                        r.sharedMaterial.shader.name != "Hidden/InternalErrorShader"), "Rendered GLB must have materials.");
                    return Click("MeetingScale");
                case 3:
                    Require(pivot != null && pivot.transform.lossyScale == Vector3.one, "Real-size mode must leave the GLB at unit scale.");
                    return ClickModel(pivot);
                case 4:
                    if (GameObject.Find("MeetingSelectedPart")?.GetComponent<Text>()?.text.StartsWith("선택한 부위") != true) return false;
                    return Click("MeetingParameterPlus");
                case 5: return Click("MeetingApplyEdit");
                case 6:
                    var possible = UnityEngine.Object.FindObjectsByType<RelayHit>(FindObjectsSortMode.None)
                        .FirstOrDefault(hit => hit.name.StartsWith("MeetingCandidate-", StringComparison.Ordinal));
                    if (possible == null) return false;
                    candidateControl = possible.name;
                    return Click(candidateControl);
                case 7:
                    if (pivot == null || Version(pivot) == report.initialVersion) return false;
                    report.finalVersion = Version(pivot);
                    report.finalWidthM = ModelWidth(pivot);
                    Require(Mathf.Abs(report.finalWidthM - .101f) < .0001f, "The +1 mm control must produce a different, 0.101m wide GLB.");
                    return Click("MeetingInterpretationVote");
                case 8: return Click("MeetingPreferenceVote");
                case 9: return Click("MeetingAdopt");
                case 10: return Click("MeetingExportStl");
                case 11:
                    var file = Path.Combine(Application.persistentDataPath, "MeetingExports", report.finalVersion + ".stl");
                    if (!File.Exists(file)) return false;
                    report.stlWidthMm = StlWidth(file);
                    Require(Mathf.Abs(report.stlWidthMm - 101) < .01f && Mathf.Abs(report.stlWidthMm / 1000 - report.finalWidthM) < .0001f,
                        "The saved STL in mm must match the selected GLB in meters.");
                    return Click("MeetingExportZip");
                case 12:
                    if (!File.Exists(Path.Combine(Application.persistentDataPath, "MeetingExports", report.finalVersion + ".zip"))) return false;
                    return Click("MeetingHome");
                case 13:
                    if (GameObject.Find("MeetingCreate") == null) return false;
                    Require(GameObject.Find("HomeDemo") == null && GameObject.Find("MeetingPanelHandle") != null,
                        "Leaving a meeting must return directly to the movable meeting lobby.");
                    Finish(null);
                    return true;
                default: throw new InvalidOperationException("Unknown smoke step");
            }
        }

        static string Version(GameObject pivot) => pivot.transform.GetChild(0).name.Substring("MeetingModel-".Length);

        static bool RunAssemblyStep()
        {
            var pivot = GameObject.Find("MeetingPreviewPivot");
            switch (step)
            {
                case 0: return GameObject.Find("MeetingCreate") != null && GameObject.Find("HomeMeeting") == null;
                case 1: return Click("MeetingCreate");
                case 2:
                    if (pivot == null) return false;
                    Require(GameObject.Find("MeetingOriginal") != null && GameObject.Find("MeetingScale") == null,
                        "Assembly mode must preserve meter scale and offer the original model.");
                    RequireAssemblyMesh(pivot);
                    report.initialVersion = Version(pivot);
                    report.initialWidthM = ModelWidth(pivot);
                    originalPosition = pivot.transform.position;
                    originalRotation = pivot.transform.rotation;
                    originalVertices = pivot.GetComponentsInChildren<MeshFilter>().SelectMany(f => f.sharedMesh.vertices).ToArray();
                    var initial = SpatialSnapshot();
                    var profile = initial["profile"];
                    report.cableDemo = profile.Value<string>("adapter_id") == "openscad_cable_organizer";
                    var expectedWidth = report.cableDemo ? .084f : .100f;
                    Require(Mathf.Abs(report.initialWidthM - expectedWidth) < .0001f,
                        $"Reference mesh must be {expectedWidth * 1000:0}mm wide in the registered sample.");
                    var field = profile["parameters"].First(p => p.Value<string>("type") == "number");
                    desktopParameter = field.Value<string>("parameter_id");
                    var source = initial["versions"].First(v => v.Value<string>("version_id") == report.initialVersion);
                    var value = source["parameters"].Value<double>(desktopParameter);
                    var maximum = field.Value<double>("max_value");
                    bool increment = value < maximum;
                    desktopRequestedValue = increment ? Math.Min(value + 1, maximum) : Math.Max(value - 1, field.Value<double>("min_value"));
                    Require(desktopRequestedValue != value, "The selected parameter needs a distinct permitted test value.");
                    return Click(increment ? "MeetingParameterPlus" : "MeetingParameterMinus");
                case 3: return Click("MeetingApplyEdit");
                case 4:
                    var candidate = UnityEngine.Object.FindObjectsByType<RelayHit>(FindObjectsSortMode.None)
                        .FirstOrDefault(hit => hit.name.StartsWith("MeetingCandidate-", StringComparison.Ordinal));
                    return candidate != null && Click(candidate.name);
                case 5:
                    if (pivot == null || Version(pivot) == report.initialVersion) return false;
                    RequireAssemblyMesh(pivot);
                    report.finalVersion = Version(pivot);
                    report.finalWidthM = ModelWidth(pivot);
                    report.fixedMount = Vector3.Distance(pivot.transform.position, originalPosition) < .00001f &&
                        Quaternion.Angle(pivot.transform.rotation, originalRotation) < .001f;
                    Require(report.fixedMount, "Original and OpenSCAD candidate must use the exact same placement.");
                    Require(!originalVertices.SequenceEqual(pivot.GetComponentsInChildren<MeshFilter>().SelectMany(f => f.sharedMesh.vertices)),
                        "The manual parameter edit must change generated exterior geometry.");
                    var shown = SpatialSnapshot()["versions"].First(v => v.Value<string>("version_id") == report.finalVersion);
                    Require(Math.Abs(shown["parameters"].Value<double>(desktopParameter) - desktopRequestedValue) < .00001,
                        "The loaded candidate must use the value requested by the desktop control.");
                    return Click("MeetingAdopt");
                case 6: return Click("MeetingExportStl");
                case 7:
                    var path = Path.Combine(Application.persistentDataPath, "MeetingExports", report.finalVersion + ".stl");
                    if (!File.Exists(path)) return false;
                    report.stlWidthMm = StlWidth(path);
                    Require(Mathf.Abs(report.stlWidthMm - report.initialWidthM * 1000) < .01f && Mathf.Abs(report.stlWidthMm / 1000 - report.finalWidthM) < .0001f,
                        "Selected STL has the same meter-to-millimeter dimensions as the candidate GLB.");
                    return Click("MeetingExportZip");
                case 8:
                    if (!File.Exists(Path.Combine(Application.persistentDataPath, "MeetingExports", report.finalVersion + ".zip"))) return false;
                    return Click("MeetingOriginal");
                case 9:
                    if (pivot == null || Version(pivot) != report.initialVersion) return false;
                    RequireAssemblyMesh(pivot);
                    Finish(null);
                    return true;
                default: throw new InvalidOperationException("Unknown assembly smoke step");
            }
        }

        static void RequireAssemblyMesh(GameObject pivot)
        {
            Require(pivot.transform.localScale == Vector3.one && pivot.transform.GetChild(0).localPosition == Vector3.zero,
                "Assembly loading must retain the CAD origin at unit scale.");
            var meshes = pivot.transform.GetChild(0).GetComponentsInChildren<MeshRenderer>();
            Require(meshes.Length > 0 && meshes.All(renderer => renderer.sharedMaterial?.shader?.name == "ContextPin/Assembly Overlay" &&
                renderer.sharedMaterial.color.a < 1f), "Assembly meshes must render with the translucent overlay material.");
        }

        static bool RunSpatialStep()
        {
            var pivot = GameObject.Find("MeetingPreviewPivot");
            var comparison = GameObject.Find("SpatialComparisonPivot");
            if (step >= 6 && report.cableDemo) return RunSpatialCableStep(pivot, comparison);
            switch (step)
            {
                case 0: return GameObject.Find("MeetingCreate") != null && GameObject.Find("HomeMeeting") == null && GameObject.Find("MeetingPanelHandle") != null;
                case 1: return Click("MeetingExample");
                case 2: return GameObject.Find("MeetingPanelHandle") != null && Click("AssemblyAlignmentStart");
                case 3: return CheckSyntheticAssemblyReference() && CheckSyntheticQrReference();
                case 4:
                    Require(GameObject.Find("GroovePreviewGallery") == null, "Groove previews must wait for alignment confirmation.");
                    return Click("AssemblyAlignmentConfirm");
                case 5:
                    if (pivot == null || GameObject.Find("SpatialSummary") == null) return false;
                    if (!report.syntheticPcm) CheckSpatialPcmPacket();
                    report.compactSpatialPanel = GameObject.Find("MeetingModelTab") == null && GameObject.Find("Masthead") == null;
                    Require(report.compactSpatialPanel, "Spatial view must omit desk navigation.");
                    RequireAssemblyMesh(pivot);
                    report.cableDemo = SpatialSnapshot()?["cable_demo"] is JObject;
                    if (!report.measuredTable)
                    {
                        UseSyntheticReviewTable(measureCorners: true);
                        Require(((JArray)SpatialSnapshot()["rounds"]).Count == 0 && GameObject.Find("GroovePreviewGallery") == null,
                            "Registering an empty tabletop must not start a proposal or open the example gallery.");
                    }
                    if (report.cableDemo && !CheckGroovePreviews(pivot)) return false;
                    if (!report.panelDragged && !CheckPanelDrag()) return false;
                    originalPosition = pivot.transform.position;
                    originalRotation = pivot.transform.rotation;
                    panelPosition = GameObject.Find("SpatialAssemblyMeeting").transform.parent.position;
                    report.initialVersion = Version(pivot);
                    report.initialWidthM = ModelWidth(pivot);
                    return Click("SpatialSessionAction");
                case 6:
                    report.replayMeeting = SpatialButtonText("SpatialSessionAction").Contains("예시 진행");
                    return report.replayMeeting && Click("SpatialMenu");
                case 7: return Click("SpatialReplayUtterance");
                case 8:
                    if (GameObject.Find("SpatialComparison") == null || pivot == null) return false;
                    report.quietProposal = Version(pivot) == report.initialVersion;
                    Require(report.quietProposal, "A prepared proposal cannot change the shown model.");
                    report.automaticComparison = true;
                    return GameObject.Find("MeetingPanelHandle") != null;
                case 9:
                    var candidate = UnityEngine.Object.FindObjectsByType<RelayHit>(FindObjectsSortMode.None)
                        .FirstOrDefault(hit => hit.name.StartsWith("SpatialCandidate-", StringComparison.Ordinal));
                    if (candidate == null) return false;
                    candidateControl = candidate.name;
                    return Click(candidateControl);
                case 10:
                    if (pivot == null || comparison == null || GameObject.Find("SpatialAdopt") == null) return false;
                    RequireAssemblyMesh(comparison);
                    RequireAssemblyMesh(pivot);
                    comparisonFirstVersion = Version(comparison);
                    comparisonPosition = comparison.transform.position;
                    report.fixedMount = Version(pivot) == report.initialVersion &&
                        Vector3.Distance(pivot.transform.position, originalPosition) < .00001f &&
                        Quaternion.Angle(pivot.transform.rotation, originalRotation) < .1f &&
                        Vector3.Distance(GameObject.Find("SpatialAssemblyMeeting").transform.parent.position, panelPosition) < .00001f;
                    Require(report.fixedMount, "A loaded candidate must leave the aligned model and panel at their existing poses.");
                    report.besideComparison = comparisonFirstVersion != report.initialVersion &&
                        Quaternion.Angle(comparison.transform.rotation, reviewTablePose.rotation) < .1f &&
                        Mathf.Abs(ModelWidth(comparison) - report.initialWidthM) < .0001f;
                    CheckTableContact(comparison);
                    Require(report.besideComparison, "The full-size candidate must be oriented to the measured table.");
                    CheckComparisonTrackingGate(pivot, comparison);
                    var alternate = UnityEngine.Object.FindObjectsByType<RelayHit>(FindObjectsSortMode.None)
                        .FirstOrDefault(hit => hit.name.StartsWith("SpatialCandidate-", StringComparison.Ordinal) && hit.name != candidateControl);
                    Require(alternate != null, "The sample must offer another candidate to prove stable side placement.");
                    return Click(alternate.name);
                case 11:
                    if (comparison == null || Version(comparison) == comparisonFirstVersion || GameObject.Find("SpatialAdopt") == null) return false;
                    report.stableComparisonSlot = Vector3.Distance(comparison.transform.position, comparisonPosition) < .0001f;
                    Require(report.stableComparisonSlot && pivot != null && Version(pivot) == report.initialVersion,
                        "Switching candidates keeps the same spot and leaves the aligned model unchanged.");
                    return Click("SpatialCloseComparison");
                case 12:
                    report.comparisonDismissed = comparison == null && pivot != null && Version(pivot) == report.initialVersion;
                    Require(report.comparisonDismissed, "Closing comparison must remove the side model without adopting it.");
                    return Click("SpatialOpenComparison");
                case 13: return Click(candidateControl);
                case 14:
                    if (comparison == null || Version(comparison) != comparisonFirstVersion || GameObject.Find("SpatialAdopt") == null) return false;
                    report.finalVersion = Version(comparison);
                    report.finalWidthM = ModelWidth(comparison);
                    return Click("SpatialAdopt");
                case 15:
                    if (GameObject.Find("SpatialSummary") == null || pivot == null || Version(pivot) != report.finalVersion) return false;
                    report.explicitAdoption = GameObject.Find("SpatialValue결정")?.GetComponent<Text>()?.text.Contains("채택") == true;
                    if (!report.explicitAdoption) return false;
                    var physical = GameObject.Find("PhysicalOriginalPivot");
                    if (physical == null) return false;
                    CheckTableContact(pivot);
                    Require(comparison == null && Version(physical) == report.initialVersion &&
                        Vector3.Distance(physical.transform.position, originalPosition) < .0001f,
                        "Adoption must keep V1 on the physical mount and display the selected CAD on the table.");
                    var app = UnityEngine.Object.FindAnyObjectByType<RelayApp>();
                    typeof(RelayApp).GetMethod("InvalidateRegisteredPoses", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(app, null);
                    report.invalidatedOverlay = GameObject.Find("MeetingPreviewPivot") == null &&
                        GameObject.Find("SpatialAlignmentStatus")?.GetComponent<Text>()?.text.Contains("추적 끊김") == true;
                    Require(report.invalidatedOverlay, "Lost tracking must hide overlay.");
                    return true;
                case 16: return Click("SpatialSessionAction");
                case 17: return Click("SpatialMinutesDownload");
                case 18: return CheckMinutesCodeAndOpenMenu();
                case 19: return Click("SpatialResult");
                case 20: return FinishSpatialResult();
                default: throw new InvalidOperationException("Unknown spatial smoke step");
            }
        }

        static JObject SpatialSnapshot()
        {
            var session = UnityEngine.Object.FindAnyObjectByType<RelayApp>();
            return (JObject)typeof(RelayApp).GetField("meetingSnapshot", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(session);
        }

        static bool RunSpatialCableStep(GameObject pivot, GameObject comparison)
        {
            var stage = SpatialSnapshot()?["cable_demo"]?.Value<int?>("stage") ?? 0;
            switch (step)
            {
                case 6:
                    Require(Mathf.Abs(report.initialWidthM - .084f) < .0001f, "V1 must match the 84mm physical organizer.");
                    report.replayMeeting = SpatialButtonText("SpatialSessionAction").Contains("예시 진행");
                    if (!report.replayMeeting) return false;
                    report.groovePreviewHiddenDuringMeeting = GameObject.Find("GroovePreviewGallery") == null;
                    Require(report.groovePreviewHiddenDuringMeeting, "Mock CAD previews must clear when the cable meeting begins.");
                    return ClickCablePart(pivot);
                case 7:
                    if (GameObject.Find("SpatialComparison") == null || GameObject.Find("MeetingPanelHandle") == null) return false;
                    report.selectionSnapshot = (SpatialSnapshot()["images"] as JArray)?.Any(image => image.Value<string>("source_mode") == "xr_render") == true;
                    if (!report.selectionSnapshot) return false;
                    report.quietProposal = pivot != null && Version(pivot) == report.initialVersion;
                    Require(report.quietProposal, "Proposals must leave V1 mounted while they are being prepared.");
                    report.automaticComparison = true;
                    return true;
                case 8:
                    if (!CheckCableChoices(pivot, .084f, .084f, .084f)) return false;
                    report.cableFirstOptions = true;
                    cableVersionTwo = Version(GameObject.Find("CableComparison-A"));
                    return ClickCableChoice("A");
                case 9: return Click("SpatialAdopt");
                case 10:
                    if (stage != 2 || pivot == null || Version(pivot) != cableVersionTwo) return false;
                    Require(GameObject.Find("CableComparison-A") == null, "The adopted A model replaces the V1 overlay only after acknowledgement.");
                    return ClickCablePart(pivot);
                case 11: return GameObject.Find("SpatialComparison") != null;
                case 12:
                    if (!CheckCableChoices(pivot, .066f, .060f, .054f)) return false;
                    report.cableSecondOptions = true;
                    var selected = GameObject.Find("CableComparison-B");
                    report.finalVersion = Version(selected);
                    report.finalWidthM = ModelWidth(selected);
                    return ClickCableChoice("B");
                case 13:
                    return Environment.GetEnvironmentVariable("MEETING_COLLABORATION_SMOKE") == "1"
                        ? RunSharedSlider() : Click("SpatialAdopt");
                case 14:
                    if (stage != 3 || pivot == null || Version(pivot) != report.finalVersion) return false;
                    report.explicitAdoption = true;
                    return Click("SpatialHistory");
                case 15:
                    if (comparison == null || Version(comparison) != report.initialVersion) return false;
                    return Click("SpatialHistory-V2");
                case 16:
                    if (comparison == null || Version(comparison) != cableVersionTwo) return false;
                    return Click("SpatialHistory-V3");
                case 17:
                    if (pivot == null || comparison == null || Version(comparison) != report.finalVersion) return false;
                    report.cableDesignHistory = Version(pivot) == report.finalVersion &&
                        SpatialSnapshot()["cable_demo"]["history"].OfType<JObject>()
                            .Select(entry => entry.Value<string>("label")).SequenceEqual(new[] { "V1", "V2", "V3" });
                    Require(report.cableDesignHistory, "Previewing each history item must leave V3 at the physical mount.");
                    return Click("SpatialHistoryClose");
                case 18: return Click("SpatialSessionAction");
                case 19: return Click("SpatialMinutesDownload");
                case 20: return CheckMinutesCodeAndOpenMenu();
                case 21: return Click("SpatialResult");
                case 22: return FinishSpatialResult();
                default: throw new InvalidOperationException("Unknown cable assembly smoke step");
            }
        }

        static bool CheckMinutesCodeAndOpenMenu()
        {
            var label = GameObject.Find("SpatialMinutesCode")?.GetComponent<Text>()?.text;
            if (string.IsNullOrEmpty(label)) return false;
            var code = label.Split(' ').Last();
            Require(code.Length == 10 && code.All(char.IsLetterOrDigit), "Completion must display the server-issued minutes code.");
            report.serverMinutesCode = true;
            return Click("SpatialMenu");
        }

        static bool CheckPanelDrag()
        {
            var handle = GameObject.Find("MeetingPanelHandle")?.GetComponent<RelayHit>();
            if (handle == null) return false;
            var origin = Camera.main.transform.position;
            var ray = new Ray(origin, handle.Center - origin);
            Physics.SyncTransforms();
            if (!Physics.Raycast(ray, out var hit, 12) || hit.collider.GetComponent<RelayHit>() != handle) return false;
            var app = UnityEngine.Object.FindAnyObjectByType<RelayApp>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var process = typeof(RelayApp).GetMethod("PlaceSpatialPanelFromInput", flags);
            var trigger = typeof(RelayApp).GetField("rightTriggerWasDown", flags);
            var panel = GameObject.Find("SpatialAssemblyMeeting").transform.parent;
            var initial = panel.position;
            var model = GameObject.Find("MeetingPreviewPivot").transform;
            var mount = model.position;
            // Inject a tracked trigger hold, move, release and subsequent free ray at the normal input boundary.
            process.Invoke(app, new object[] { ray, true });
            handle.pointClicked(hit.point);
            trigger.SetValue(app, true);
            var moved = new Ray(origin + Camera.main.transform.right * .025f, ray.direction);
            try
            {
                Require((bool)process.Invoke(app, new object[] { moved, false }), "Panel drag must capture the held trigger.");
                Require(Vector3.Distance(panel.position, initial + Camera.main.transform.right * .025f) < .0001f,
                    "The grabbed panel must follow the ray without snapping.");
            }
            finally { trigger.SetValue(app, false); }
            Require((bool)process.Invoke(app, new object[] { moved, false }), "Release must end the drag without clicking another target.");
            Require(!(bool)process.Invoke(app, new object[] { ray, false }) && Vector3.Distance(mount, model.position) < .0001f,
                "After release the panel stays put and the CAD mount remains independent.");
            report.panelDragged = true;
            report.rayActions.Add("MeetingPanelHandle drag/release");
            return true;
        }

        static bool RunSharedSlider()
        {
            var snapshot = SpatialSnapshot();
            var draft = snapshot["edit_draft"] as JObject;
            if (sliderStep == 0)
            {
                if (!Click("SpatialAdjustCandidate")) return false;
                sliderStep++;
                return false;
            }
            if (sliderStep == 1)
            {
                var target = GameObject.Find("SharedParameterSlider")?.GetComponent<RelayHit>();
                var field = (draft?["fields"] as JArray)?.FirstOrDefault();
                if (target?.dragged == null || field == null) return false;
                Require(field.Value<string>("parameter_id") == "body_length", "Length adjustment must use a physical length field.");
                var min = field.Value<float>("min_value");
                var max = field.Value<float>("max_value");
                target.dragged(target.transform.TransformPoint(new Vector3((57.1f - min) / (max - min) * 388, -17, 0)));
                target = GameObject.Find("SharedParameterSlider").GetComponent<RelayHit>();
                target.dragged(target.transform.TransformPoint(new Vector3((57.3f - min) / (max - min) * 388, -17, 0)));
                sliderStep++;
                return false;
            }
            if (sliderStep == 2)
            {
                var preview = GameObject.Find("SpatialComparisonPivot");
                if (draft?.Value<string>("status") != "ready" || preview == null || Math.Abs(ModelWidth(preview) - .0573f) > .0001f ||
                    GameObject.Find("SharedEditAdopt") == null) return false;
                var model = (snapshot["versions"] as JArray).First(v => v.Value<string>("version_id") == Version(preview));
                Require(Math.Abs(model["parameters"].Value<float>("slot_pitch") - 9.825f) < .001f &&
                    Math.Abs(model["parameters"].Value<float>("throat_width") - 3.4f) < .001f,
                    "Latest slider geometry must be regenerated at 57.3mm while preserving the adopted entrance.");
                var original = GameObject.Find("PhysicalOriginalPivot");
                report.physicalIdentity = original != null && Version(original) == report.initialVersion;
                Require(report.physicalIdentity, "The physical V1 proxy must retain its original identity.");
                report.sharedSlider = true;
                report.finalVersion = Version(preview);
                report.finalWidthM = ModelWidth(preview);
                if (!Click("SharedEditAdopt")) return false;
                sliderStep++;
                return true;
            }
            return false;
        }

        static bool CheckCableChoices(GameObject pivot, params float[] widths)
        {
            var choices = new[] { GameObject.Find("CableComparison-A"), GameObject.Find("CableComparison-B"), GameObject.Find("CableComparison-C") };
            if (pivot == null || choices.Any(choice => choice == null)) return false;
            for (int i = 0; i < choices.Length; i++)
            {
                var choice = choices[i];
                RequireAssemblyMesh(choice);
                CheckTableContact(choice);
                Require(Mathf.Abs(ModelWidth(choice) - widths[i]) < .0001f && choice.GetComponentInChildren<TextMesh>() != null &&
                    Quaternion.Angle(choice.transform.rotation, reviewTablePose.rotation) < .1f,
                    "Every cable option must have its expected dimensions and letter on the measured table.");
                for (int j = 0; j < i; j++)
                    Require(Vector3.Distance(choice.transform.position, choices[j].transform.position) > .14f,
                        "Cable choices must occupy separate positions.");
            }
            return true;
        }

        static bool CheckGroovePreviews(GameObject pivot)
        {
            const string toggle = "SpatialGroovePreviewToggle";
            var gallery = GameObject.Find("GroovePreviewGallery");
            if (groovePreviewStep == -1)
            {
                Require(gallery == null, "Even in example mode, confirmation alone must not open the CAD gallery.");
                if (!Click(toggle)) return false;
                groovePreviewStep = 0;
                return false;
            }
            if (groovePreviewStep == 1)
            {
                if (gallery != null || !Click(toggle)) return false;
                groovePreviewStep = 2;
                return false;
            }
            if (groovePreviewStep == 3) return true;
            if (gallery == null) return false;

            var choices = new[] { "A", "B", "C", "D" }.Select(key => gallery.transform.Find("GroovePreview-" + key)).ToArray();
            if (choices.Any(choice => choice == null || choice.GetComponentInChildren<MeshFilter>()?.sharedMesh == null)) return false;
            if (groovePreviewStep == 2)
            {
                report.groovePreviewToggle = true;
                groovePreviewStep = 3;
                return true;
            }

            Vector3 spacing = choices[1].position - choices[0].position;
            float width = ModelWidth(pivot);
            Require(spacing.magnitude > width, "Adjacent groove previews must have enough space for their full-size meshes.");
            var meshes = new List<Vector3[]>();
            for (int i = 0; i < choices.Length; i++)
            {
                var choice = choices[i];
                CheckTableContact(choice.gameObject);
                Require(Mathf.Abs(ModelWidth(choice.gameObject) - width) < .0001f &&
                    Quaternion.Angle(choice.rotation, reviewTablePose.rotation) < .1f,
                    "Every groove preview must keep the cable organizer's physical width on the measured tabletop.");
                Require(Vector3.Distance(choice.position, choices[0].position + spacing * i) < .0001f,
                    "Groove previews must occupy a uniformly spaced row.");
                var vertices = choice.GetComponentsInChildren<MeshFilter>().SelectMany(filter => filter.sharedMesh.vertices).ToArray();
                Require(meshes.All(previous => !previous.SequenceEqual(vertices)), "Each groove preview must load distinct CAD geometry.");
                meshes.Add(vertices);
            }
            report.groovePreviewRow = true;
            if (!Click(toggle)) return false;
            groovePreviewStep = 1;
            return false;
        }

        static bool ClickCableChoice(string label)
        {
            var round = (SpatialSnapshot()["rounds"] as JArray)?.Last;
            var candidate = round?["candidates"]?[label[0] - 'A'];
            return candidate != null && Click("SpatialCandidate-" + (string)candidate["candidate_id"]);
        }

        static bool ClickCablePart(GameObject pivot)
        {
            if (pivot == null) return false;
            var from = Camera.main.transform.position;
            Physics.SyncTransforms();
            foreach (var target in pivot.GetComponentsInChildren<RelayHit>().Where(hit => hit.role == "outer_shell"))
            {
                var mesh = target.GetComponent<MeshFilter>().sharedMesh;
                var vertices = mesh.vertices;
                var triangles = mesh.triangles;
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    var center = target.transform.TransformPoint((vertices[triangles[i]] + vertices[triangles[i + 1]] +
                        vertices[triangles[i + 2]]) / 3f);
                    if (!Physics.Raycast(from, center - from, out var hit, 12) || hit.collider.GetComponent<RelayHit>() != target) continue;
                    target.pointClicked?.Invoke(hit.point);
                    target.clicked?.Invoke();
                    report.rayActions.Add("semantic part: " + target.role);
                    return true;
                }
            }
            return false;
        }

        static bool FinishSpatialResult()
        {
            var snapshot = SpatialSnapshot();
            var name = snapshot.Value<string>("session_id") + "-r" + (snapshot["result"]?.Value<int?>("revision") ?? 1) + ".zip";
            var path = Path.Combine(Application.persistentDataPath, "MeetingExports", name);
            if (!File.Exists(path)) return false;
            using var zip = ZipFile.OpenRead(path);
            using var manifest = new StreamReader(zip.GetEntry("manifest.json").Open());
            var file = JObject.Parse(manifest.ReadToEnd());
            report.savedMeeting = file.ToString().Contains(report.finalVersion) && zip.GetEntry("cad/final.stl") != null &&
                zip.GetEntry("summary.md") != null && zip.GetEntry("transcript.jsonl") != null;
            Require(report.savedMeeting, "The completed meeting ZIP must refer to the adopted model.");
            if (report.cableDemo) Require(zip.GetEntry("design_history.json") != null, "The cable ZIP must preserve V1 through V3.");
            Finish(null);
            return true;
        }

        static void CheckComparisonTrackingGate(GameObject pivot, GameObject comparison)
        {
            var app = UnityEngine.Object.FindAnyObjectByType<RelayApp>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var alignment = typeof(RelayApp).GetField("assemblyAlignment", flags);
            var previous = alignment.GetValue(app);
            var position = typeof(RelayApp).GetMethod("PositionMeetingModel", flags);
            try
            {
                alignment.SetValue(app, Enum.Parse(alignment.FieldType, "Lost"));
                position.Invoke(app, null);
                report.comparisonHiddenOnLoss = !pivot.activeInHierarchy && !comparison.activeInHierarchy;
                Require(report.comparisonHiddenOnLoss, "Tracking loss must hide both the real overlay and side comparison.");
            }
            finally
            {
                alignment.SetValue(app, previous);
                position.Invoke(app, null);
            }
        }

        static bool CheckSyntheticQrReference()
        {
            var pivot = GameObject.Find("MeetingPreviewPivot");
            if (pivot == null) return false;
            var app = UnityEngine.Object.FindAnyObjectByType<RelayApp>();
            var type = typeof(RelayApp);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var alignment = type.GetField("assemblyAlignment", flags);
            var input = type.GetMethod("TryPreviewAssemblyQrPose", flags);
            var target = (string)type.GetField("assemblyQrPayload", flags).GetValue(app);
            var mount = (Pose)type.GetField("assemblyQrMount", flags).GetValue(app);
            Require(!string.IsNullOrEmpty(target), "The test assembly must register its expected QR payload and mounting offset.");
            const BindingFlags helpers = BindingFlags.Static | BindingFlags.NonPublic;
            var decode = type.GetMethod("ReadQrPayload", helpers);
            Require((string)decode.Invoke(null, new object[] { null, System.Text.Encoding.UTF8.GetBytes(target + "\0") }) == target,
                "A byte-mode placement QR must match the registered card.");
            var center = type.GetMethod("TryQrCenter", helpers);
            var corners = new List<Vector2> { new(-.01f, -.02f), new(.07f, -.02f), new(.07f, .06f), new(-.01f, .06f) };
            object[] polygon = { null, corners, Vector2.zero };
            Require((bool)center.Invoke(null, polygon) && Vector2.Distance((Vector2)polygon[2], new Vector2(.03f, .02f)) < .00001f,
                "QR corner geometry must retain the physical center when the rectangle is unavailable.");
            Require(!(bool)center.Invoke(null, new object[] { null, null, Vector2.zero }),
                "A decoded string without position geometry must wait for a located card.");
            var tracked = new Pose(pivot.transform.position + Vector3.left * .03f, Quaternion.Euler(12, 26, 0));
            alignment.SetValue(app, Enum.Parse(alignment.FieldType, "ScanQr"));
            Require(!(bool)input.Invoke(app, new object[] { "unrelated QR", tracked }), "An unrelated QR payload must not align the model.");
            report.syntheticQr = (bool)input.Invoke(app, new object[] { target, tracked }) &&
                Vector3.Distance(pivot.transform.position, tracked.position + tracked.rotation * mount.position) < .0001f &&
                Quaternion.Angle(pivot.transform.rotation, tracked.rotation * mount.rotation) < .1f &&
                GameObject.Find("AssemblyAlignmentConfirm") != null;
            Require(report.syntheticQr, "The expected QR must use its own offset and orientation before confirmation.");
            return true;
        }

        static string SpatialButtonText(string id) => GameObject.Find(id)?.GetComponentInChildren<Text>()?.text ?? "";

        static void CheckSpatialPcmPacket()
        {
            var clip = AudioClip.Create("PCM smoke data", 4000, 2, 16000, false);
            try
            {
                var data = Enumerable.Range(0, 8000).Select(i => i % 2 == 0 ? .5f : -.25f).ToArray();
                Require(clip.SetData(data, 0), "Synthetic PCM test clip could not be written.");
                var type = typeof(RelayApp).Assembly.GetType("ContextPin.MeetingPcmStream");
                using var stream = (IDisposable)Activator.CreateInstance(type, new object[] { clip });
                var observe = type.GetMethod("ObservePosition");
                var read = type.GetMethod("ReadPacket");
                observe.Invoke(stream, new object[] { 2000 });
                var first = (byte[])read.Invoke(stream, new object[] { false });
                observe.Invoke(stream, new object[] { 2500 });
                var tail = (byte[])read.Invoke(stream, new object[] { true });
                report.syntheticPcm = first.Length == 4016 && tail.Length == 1016 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(0, 4)) == 0 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(0, 4)) == 1 &&
                    BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(4, 8)) == 2000 &&
                    BinaryPrimitives.ReadInt16LittleEndian(first.AsSpan(16, 2)) == 4096;
                Require(report.syntheticPcm, "Synthetic stereo clip must become a continuous ordered mono stream with a final partial packet.");
            }
            finally { UnityEngine.Object.Destroy(clip); }
        }

        static bool CheckSyntheticAssemblyReference()
        {
            // No real contact points exist in the simulator. Exercise the registration and
            // hide-on-invalidation code by providing known test measurements.
            var pivot = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(candidate => candidate.name == "MeetingPreviewPivot");
            if (pivot == null) return false;
            var app = UnityEngine.Object.FindAnyObjectByType<RelayApp>();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var recordPoint = typeof(RelayApp).GetMethod("RecordAssemblyPoint", flags);
            var camera = Camera.main.transform;
            var origin = camera.position + camera.forward * .8f - Vector3.up * .17f;
            var rotation = Quaternion.Euler(14, 32, 0);
            recordPoint.Invoke(app, new object[] { origin });
            recordPoint.Invoke(app, new object[] { origin + rotation * Vector3.right * .08f });
            recordPoint.Invoke(app, new object[] { origin + rotation * Vector3.up * .08f });
            Require(pivot.gameObject.activeInHierarchy && Vector3.Distance(pivot.position, origin) < .00001f &&
                Quaternion.Angle(pivot.rotation, rotation) < .1f && GameObject.Find("AssemblyAlignmentConfirm") != null,
                "The synthetic reference must place the original at its recorded origin and axes before confirmation.");
            report.syntheticReference = true;
            return true;
        }

        static float ModelWidth(GameObject pivot)
        {
            var geometry = pivot.GetComponentsInChildren<MeshFilter>();
            Require(geometry.Length > 0, "A GLB must instantiate mesh geometry.");
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            foreach (var filter in geometry)
            {
                var bounds = filter.sharedMesh.bounds;
                for (int x = 0; x < 2; x++) for (int y = 0; y < 2; y++) for (int z = 0; z < 2; z++)
                {
                    var world = filter.transform.TransformPoint(new Vector3(x == 0 ? bounds.min.x : bounds.max.x,
                        y == 0 ? bounds.min.y : bounds.max.y, z == 0 ? bounds.min.z : bounds.max.z));
                    var local = pivot.transform.InverseTransformPoint(world);
                    min = Math.Min(min, local.x);
                    max = Math.Max(max, local.x);
                }
            }
            return max - min;
        }

        static float StlWidth(string file)
        {
            using var reader = new BinaryReader(File.OpenRead(file));
            reader.BaseStream.Seek(80, SeekOrigin.Begin);
            uint triangles = reader.ReadUInt32();
            Require(triangles > 0 && reader.BaseStream.Length == 84 + 50L * triangles, "Expected a valid binary STL.");
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            for (uint i = 0; i < triangles; i++)
            {
                reader.BaseStream.Seek(12, SeekOrigin.Current);
                for (int vertex = 0; vertex < 3; vertex++)
                {
                    var x = reader.ReadSingle();
                    min = Math.Min(min, x);
                    max = Math.Max(max, x);
                    reader.BaseStream.Seek(8, SeekOrigin.Current);
                }
                reader.ReadUInt16();
            }
            return max - min;
        }

        static bool Click(string name)
        {
            if (GameObject.Find("Notice")?.GetComponent<Text>()?.text == "변경 사항을 보내는 중입니다.") return false;
            var target = GameObject.Find(name)?.GetComponent<RelayHit>();
            if (target == null) return false;
            var from = Camera.main.transform.position;
            Physics.SyncTransforms();
            if (!Physics.Raycast(from, target.Center - from, out var result, 12) || result.collider.GetComponent<RelayHit>() != target) return false;
            target.clicked?.Invoke();
            report.rayActions.Add(name);
            return true;
        }

        static bool ClickModel(GameObject pivot)
        {
            var from = Camera.main.transform.position;
            Physics.SyncTransforms();
            if (!Physics.Raycast(from, pivot.transform.position - from, out var result, 12)) return false;
            var target = result.collider.GetComponent<RelayHit>();
            if (target == null || string.IsNullOrEmpty(target.role) || !target.transform.IsChildOf(pivot.transform)) return false;
            target.pointClicked?.Invoke(result.point);
            target.clicked?.Invoke();
            report.rayActions.Add("semantic part: " + target.role);
            return true;
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        static void Finish(Exception error)
        {
            EditorApplication.update -= Tick;
            hostEvents?.Dispose();
            hostEvents = null;
            report.success = error == null;
            report.error = error?.ToString() ?? "";
            var output = Environment.GetEnvironmentVariable("RELAY_WORKSPACE") ?? Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            if (checkExplicit || checkCaptureOnly) ReleaseEditorPcmInput();
            UnityEditor.SessionState.EraseBool("ContextPinLegacyCandidateWorkflow");
            UnityEditor.SessionState.EraseBool("ContextPinCaptureOnlyWorkflow");
            var check = ColocationTest ? "colocation" : checkBundleAdoption ? "candidate-commit" : checkCandidateSet ? "candidate-set" : checkCaptureOnly ? "capture-only" : checkExplicit ? "explicit" : checkLiveAlignment ? "live-alignment" : checkSavedDesign ? "saved-design" : report.assembly ? "assembly" : "meeting";
            var file = Path.Combine(output, "artifacts", $"{check}-{(report.simulator ? "simulator" : "smoke")}-report.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, JsonUtility.ToJson(report, true));
            if (error != null) Debug.LogException(error);
            Debug.Log("ContextPin meeting smoke: " + file);
            SessionState.SetBool("MeetingSmokeExit", true);
            SessionState.SetInt("MeetingSmokeExitCode", error == null ? 0 : 2);
            EditorApplication.isPlaying = false;
        }
    }
}
