using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace ContextPin.Editor
{
    public static partial class MeetingSmoke
    {
        const BindingFlags AppMembers = BindingFlags.NonPublic | BindingFlags.Instance;
        const BindingFlags PcmHooks = BindingFlags.NonPublic | BindingFlags.Static;
        const int PcmRate = 16000;
        static AudioClip inputRing;
        static float[] speechSamples, followupSamples;
        static double micStart, stopSpeechAt, recordingObservedAt;
        static readonly Type AppType = typeof(RelayApp);

        static int PcmCursor() => (int)((Time.realtimeSinceStartupAsDouble - micStart) * PcmRate) % inputRing.samples;

        static float[] ReadSpeechFixture(string path)
        {
            Require(File.Exists(path), "Expected a recorded 16 kHz mono s16le PCM file: " + path);
            byte[] bytes = File.ReadAllBytes(path);
            Require(bytes.Length >= PcmRate / 2 && bytes.Length <= 30 * PcmRate * 2 && bytes.Length % 2 == 0,
                "Expected a 0.25–30 second uncompressed mono PCM fixture.");
            var samples = new float[bytes.Length / 2];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2, 2)) / 32768f;
            return samples;
        }

        static void InstallEditorPcmInput()
        {
            if (speechSamples != null) return;
            paperCheck = recoveryCheck = previewChoice = finalRecalibration = 0;
            anchor = null;
            speechSamples = ReadSpeechFixture(Environment.GetEnvironmentVariable("MEETING_EXPLICIT_PCM"));
            string next = Environment.GetEnvironmentVariable("MEETING_FOLLOWUP_PCM");
            followupSamples = string.IsNullOrEmpty(next) ? speechSamples : ReadSpeechFixture(next);
            Func<AudioClip> open = () =>
            {
                report.microphoneOpenCount++;
                micStart = Time.realtimeSinceStartupAsDouble;
                inputRing = AudioClip.Create("Clocked simulator microphone", PcmRate * 190, 1, PcmRate, false);
                return inputRing;
            };
            AppType.GetField("EditorOpenPcmInput", PcmHooks).SetValue(null, open);
            AppType.GetField("EditorPcmPosition", PcmHooks).SetValue(null, (Func<int>)PcmCursor);
        }

        static void ReleaseEditorPcmInput()
        {
            recordingObservedAt = 0;
            AppType.GetField("EditorOpenPcmInput", PcmHooks).SetValue(null, null);
            AppType.GetField("EditorPcmPosition", PcmHooks).SetValue(null, null);
            if (inputRing != null) UnityEngine.Object.Destroy(inputRing);
            inputRing = null;
            speechSamples = followupSamples = null;
        }

        static Vector2 explicitAreaSize;
        static Pose measuredPaper;
        static int paperCheck, recoveryCheck, previewChoice, finalRecalibration;
        static Transform anchor;
        static Pose commitPose;
        static GameObject previousModel;
        static string candidateSignature;
        static double previewClickedAt;

        static bool PaperState(RelayApp app, string property) =>
            (bool)AppType.GetProperty(property, AppMembers).GetValue(app);

        static int PaperCornerCount(RelayApp app) =>
            (int)AppType.GetProperty("A4CornerCount", AppMembers).GetValue(app);

        static void SetPaperFixture(bool portrait)
        {
            var head = Camera.main.transform;
            var right = Vector3.ProjectOnPlane(head.right, Vector3.up).normalized;
            var forward = Vector3.Cross(right, Vector3.up);
            var heading = Quaternion.AngleAxis(portrait ? -18 : 24, Vector3.up) * Quaternion.LookRotation(forward);
            explicitAreaSize = portrait ? new Vector2(.210f, .297f) : new Vector2(.297f, .210f);
            measuredPaper = new Pose(head.position + forward * .66f - Vector3.up * .14f
                + right * (portrait ? -.13f : -.09f), heading);
        }

        // Clockwise around the paper as seen on the desk: front-left, front-right, back-right, back-left.
        static Vector3 PaperCorner(int index)
        {
            float x = (index == 0 || index == 3 ? -.5f : .5f) * explicitAreaSize.x;
            float z = (index < 2 ? -.5f : .5f) * explicitAreaSize.y;
            return measuredPaper.position + measuredPaper.rotation * new Vector3(x, 0, z);
        }

        static void TouchPaper(RelayApp app, Vector3 point, int expectedCount)
        {
            int before = PaperCornerCount(app);
            var down = measuredPaper.rotation * new Vector3(0, -1, 1).normalized;
            Require(PressRightWithLeftActive(app, new Ray(point - .10f * down, down), samplePaper: true),
                "Expected a tracked right-controller A press at the paper corner.");
            Require(PaperCornerCount(app) == expectedCount,
                $"Expected {expectedCount} accepted A4 corners, got {PaperCornerCount(app)} at {point}. " +
                AppType.GetProperty("A4Status", AppMembers).GetValue(app));
            if (before == expectedCount)
                Require(!string.IsNullOrWhiteSpace((string)AppType.GetProperty("A4Status", AppMembers).GetValue(app)),
                    "A rejected corner must explain why the measurement needs correcting.");
            if (expectedCount < 4)
                Require(!PaperState(app, "A4PreviewActive") && !PaperState(app, "HasReviewTable") &&
                    GameObject.Find("AssemblyAlignmentConfirm") == null && GameObject.Find("MeetingPreviewPivot") == null &&
                    ActiveAlternatives().Length == 0,
                    "A partial or invalid sheet must never show the review models or enable the desk.");
        }

        static bool StartPaperFixture(RelayApp app)
        {
            var loaded = AppType.GetField("meetingPreview", AppMembers).GetValue(app);
            if ((GameObject)loaded.GetType().GetField("Root").GetValue(loaded) == null) return false;
            Require(PaperState(app, "A4PointSampling") && PaperCornerCount(app) == 0,
                "The default host workflow must start at the first A4 corner without a QR scan.");
            SetPaperFixture(true);
            return true;
        }

        static Bounds ModelBoundsInPose(GameObject model, Pose space)
        {
            var inverse = Quaternion.Inverse(space.rotation);
            var points = model.transform.GetChild(0).GetComponentsInChildren<MeshFilter>()
                .SelectMany(f => f.sharedMesh.vertices.Select(v => inverse * (f.transform.TransformPoint(v) - space.position)));
            using var vertices = points.GetEnumerator();
            Require(vertices.MoveNext(), "Expected loaded mesh vertices for " + model.name);
            var bounds = new Bounds(vertices.Current, Vector3.zero);
            while (vertices.MoveNext()) bounds.Encapsulate(vertices.Current);
            return bounds;
        }

        static void CheckPaperLatest(GameObject model)
        {
            var bounds = ModelBoundsInPose(model, measuredPaper);
            var basePoint = checkPhoneStand ? RelativePose(model.transform, measuredPaper).position :
                new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
            var expected = checkPhoneStand ? new Vector3(.24f, 0, 0) : new Vector3(explicitAreaSize.x * .25f, 0, -explicitAreaSize.y * .25f);
            Require(Vector3.Distance(basePoint, expected) < .001f && Mathf.Abs(bounds.min.y) < .001f && Quaternion.Angle(model.transform.rotation, measuredPaper.rotation) < .1f,
                $"Latest must sit on the tabletop at paper-relative offset {expected}, measured {basePoint}.");
            RequireAssemblyMesh(model);
        }

        static bool PaperPreview(RelayApp app, GameObject latest)
        {
            if (latest == null || GameObject.Find("AssemblyAlignmentConfirm") == null) return false;
            Require(PaperState(app, "A4PreviewActive") && !PaperState(app, "HasReviewTable") &&
                GameObject.Find("A4PhysicalCenter") != null && GameObject.Find("A4MeasuredOutline") != null &&
                GameObject.Find("PhysicalOriginalPivot") == null && ActiveAlternatives().Length == 0,
                "The preview must show only the current Latest beside the center before desk confirmation.");
            CheckPaperLatest(latest);
            return true;
        }

        static bool CheckPortraitPaper(RelayApp app, GameObject latest)
        {
            // Each exercise uses a separate player update, preserving the equipment's normal UI lifecycle.
            performedFrame = Time.frameCount;
            switch (paperCheck)
            {
                case 0: TouchPaper(app, PaperCorner(0), 1); break;
                case 1: TouchPaper(app, PaperCorner(0), 1); break; // Double-tap one corner.
                case 2: TouchPaper(app, PaperCorner(0) + measuredPaper.rotation * Vector3.right * .10f, 1); break;
                case 3: TouchPaper(app, PaperCorner(1), 2); break;
                case 4: TouchPaper(app, PaperCorner(2) + Vector3.up * .045f, 2); break;
                case 5: TouchPaper(app, PaperCorner(2), 3); break;
                case 6: TouchPaper(app, (PaperCorner(3) + PaperCorner(2)) * .5f, 3); break;
                case 7:
                    TouchPaper(app, PaperCorner(3), 4);
                    report.a4InvalidCornersRejected = true;
                    break;
                case 8:
                    if (!PaperPreview(app, latest) || !Click("A4Undo")) return false;
                    Require(PaperCornerCount(app) == 3 && !PaperState(app, "A4PreviewActive"),
                        "Undo in the preview must reopen the fourth corner.");
                    break;
                case 9: TouchPaper(app, PaperCorner(3), 4); break;
                default:
                    if (!PaperPreview(app, latest) || !Click("AssemblyAlignmentConfirm")) return false;
                    report.a4PortraitMeasured = report.a4CornerRetaken = report.hiddenUntilTable = true;
                    return true;
            }
            paperCheck++;
            return false;
        }

        static void CheckConfirmedPaper(RelayApp app, GameObject latest)
        {
            Require(PaperState(app, "A4ReviewSurfaceActive") && PaperState(app, "HasReviewTable"),
                "A confirmed sheet must provide a bounded review surface.");
            reviewTablePose = (Pose)AppType.GetField("reviewTable", AppMembers).GetValue(app);
            var measured = (Vector2)AppType.GetField("reviewTableSize", AppMembers).GetValue(app);
            Require(Vector3.Distance(reviewTablePose.position, measuredPaper.position) < .001f &&
                Quaternion.Angle(reviewTablePose.rotation, measuredPaper.rotation) < .1f &&
                Vector2.Distance(measured, explicitAreaSize) < .001f,
                "The actual center, size, orientation, and tabletop height must agree with all four sampled corners.");
            CheckPaperLatest(latest);
            CheckTableContact(latest);
        }

        static bool RecoverWithLandscapePaper(RelayApp app, GameObject latest)
        {
            performedFrame = Time.frameCount;
            if (recoveryCheck == 0)
            {
                if (latest == null) return false;
                CheckConfirmedPaper(app, latest);
                AppType.GetMethod("InvalidateAssemblyAlignment", AppMembers).Invoke(app, null);
                Require(!latest.activeInHierarchy && !PaperState(app, "HasReviewTable"),
                    "Head tracking invalidation must erase the paper pose and hide every stale overlay.");
                SetPaperFixture(false);
            }
            else if (recoveryCheck == 1)
            {
                if (!PaperState(app, "A4PointSampling") && !Click("A4Start")) return false;
                Require(PaperCornerCount(app) == 0, "Re-measurement must start with a fresh sheet.");
            }
            else if (recoveryCheck <= 5)
                TouchPaper(app, PaperCorner(recoveryCheck - 2), recoveryCheck - 1);
            else if (recoveryCheck == 6)
            {
                if (!PaperPreview(app, latest) || !Click("AssemblyAlignmentConfirm")) return false;
            }
            else
            {
                if (latest == null) return false;
                CheckConfirmedPaper(app, latest);
                report.a4LandscapeMeasured = report.a4TrackingRecovered = true;
                return true;
            }
            recoveryCheck++;
            return false;
        }

        static void CheckPhysicalScale(JObject state, GameObject model)
        {
            var version = ((JArray)state["versions"]).Single(v => (string)v["version_id"] == Version(model));
            var dims = version["measurements"];
            var size = new Vector3(dims.Value<float>("width_mm"), dims.Value<float>("height_mm"), dims.Value<float>("depth_mm"));
            var bounds = ModelBoundsInPose(model, measuredPaper);
            Require(Vector3.Distance(bounds.size, size * .001f) < .001f,
                $"{model.name} lost the exact server CAD's millimeter dimensions in XR.");
        }

        static Bounds PhysicalProductBounds(JObject state)
        {
            var original = ((JArray)state["versions"]).First["measurements"];
            var physicalSize = new Vector3(original.Value<float>("width_mm"), original.Value<float>("height_mm"), original.Value<float>("depth_mm")) * .001f;
            return new Bounds(Vector3.up * physicalSize.y * .5f, physicalSize);
        }

        static void CheckPaperGallery(JObject state, GameObject latest, GameObject[] candidates)
        {
            var occupied = new[] { PhysicalProductBounds(state), ModelBoundsInPose(latest, measuredPaper) }.ToList();
            foreach (var model in candidates)
            {
                RequireAssemblyMesh(model);
                CheckTableContact(model);
                CheckPhysicalScale(state, model);
                var bounds = ModelBoundsInPose(model, measuredPaper);
                Require(occupied.All(other => bounds.max.x < other.min.x || other.max.x < bounds.min.x ||
                    bounds.max.z < other.min.z || other.max.z < bounds.min.z),
                    $"{model.name} overlaps another preview or Latest at sheet center.");
                occupied.Add(bounds);
            }
            foreach (var model in candidates) CheckCandidateLetter(model, occupied);
            report.a4FullSizeGallery = true;
        }

        static void CheckCandidateLetter(GameObject model, System.Collections.Generic.List<Bounds> models)
        {
            string letter = model.name.Substring(model.name.Length - 1);
            var labels = model.GetComponentsInChildren<TextMesh>();
            Require(labels.Length == 1 && labels[0].text == letter, $"Expected a single {letter} on {model.name}.");
            var text = labels[0];
            var glyph = text.GetComponent<MeshRenderer>().localBounds;
            var width = text.transform.TransformVector(Vector3.right * glyph.size.x).magnitude;
            var height = text.transform.TransformVector(Vector3.up * glyph.size.y).magnitude;
            Require(width > 0 && width <= .011f && height > 0 && height <= .008f,
                $"{letter} rendered glyph is {width*1000:F2} × {height*1000:F2} mm; it must fit 11 × 8 mm.");
            report.maxLetterWidthMm = Mathf.Max(report.maxLetterWidthMm, width * 1000);
            report.maxLetterHeightMm = Mathf.Max(report.maxLetterHeightMm, height * 1000);
            var inverse = Quaternion.Inverse(measuredPaper.rotation);
            var bounds = new Bounds(inverse * (text.transform.TransformPoint(glyph.center) - measuredPaper.position), Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                var corner = glyph.center + Vector3.Scale(glyph.extents, new Vector3(
                    (i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                bounds.Encapsulate(inverse * (text.transform.TransformPoint(corner) - measuredPaper.position));
            }
            Require(bounds.min.x >= -explicitAreaSize.x * .5f && bounds.max.x <= explicitAreaSize.x * .5f &&
                bounds.min.z >= -explicitAreaSize.y * .5f && bounds.max.z <= explicitAreaSize.y * .5f &&
                models.All(other => bounds.max.x < other.min.x || other.max.x < bounds.min.x ||
                    bounds.max.z < other.min.z || other.max.z < bounds.min.z),
                $"The {letter} must be inside the measured sheet, beside the geometry of every visible CAD.");
        }

        static string GeometrySignature(GameObject model)
        {
            using var bytes = new MemoryStream();
            using var data = new BinaryWriter(bytes);
            foreach (var filter in model.transform.GetChild(0).GetComponentsInChildren<MeshFilter>())
            {
                var mesh = filter.sharedMesh;
                data.Write(mesh.vertexCount);
                foreach (var v in mesh.vertices) { data.Write(v.x); data.Write(v.y); data.Write(v.z); }
                foreach (var i in mesh.triangles) data.Write(i);
            }
            data.Flush();
            using var sha = SHA256.Create();
            bytes.Position = 0;
            return BitConverter.ToString(sha.ComputeHash(bytes));
        }

        static void CheckSideLatest(JObject state, GameObject latest, string version, bool stablePose)
        {
            CheckPaperLatest(latest);
            CheckPhysicalScale(state, latest);
            var parent = latest.transform.parent;
            Require(parent != null && parent.name == "LatestAnchor" && latest.transform.localPosition.sqrMagnitude < .000001f &&
                Quaternion.Angle(latest.transform.localRotation, Quaternion.identity) < .1f &&
                Vector3.Distance(latest.transform.lossyScale, Vector3.one) < .00001f && Version(latest) == version &&
                (string)state["adopted_version_id"] == version, "A single unit-scale Latest in its side slot must represent the server commit.");
            if (report.latestAnchorId == 0)
            {
                anchor = parent;
                commitPose = new Pose(anchor.position, anchor.rotation);
                report.latestAnchorId = anchor.GetInstanceID();
            }
            Require(anchor != null && anchor == parent && anchor.GetInstanceID() == report.latestAnchorId &&
                (!stablePose || Vector3.Distance(anchor.position, commitPose.position) < .00001f &&
                Quaternion.Angle(anchor.rotation, commitPose.rotation) < .01f),
                "The persistent LatestAnchor must keep its instance, position, and rotation during previews and commits.");
            Require(GameObject.Find("PhysicalOriginalPivot") == null &&
                parent.GetComponentsInChildren<Transform>().Count(t => t.name.StartsWith("MeetingModel-", StringComparison.Ordinal)) == 1,
                "Only the active committed CAD may occupy the Latest side slot.");
            Require(parent.GetComponentsInChildren<TextMesh>().Length == 0 && GameObject.Find("LatestLabel") == null,
                "The visible Latest CAD must have no active world-space Latest/version lettering.");
            var panelLabel = GameObject.Find("ExplicitLatest")?.GetComponent<Text>();
            if (panelLabel != null)
            {
                var current = ((JArray)state["versions"]).Single(v => (string)v["version_id"] == version);
                Require(panelLabel.text.Contains((string)current["design_version"]), "The meeting panel must show the version of the visible CAD.");
            }
            report.centerHasNoWorldLabel = true;
            var hits = parent.GetComponentsInChildren<RelayHit>();
            Require(hits.Length > 0 && hits.All(h => h.meetingVersion == version &&
                h.GetComponents<MeshCollider>().Length == 1 && h.GetComponent<MeshCollider>().enabled),
                "Latest selectable parts must each have exactly one enabled collider, all for the current version.");
        }

        static bool TryRightRay(GameObject model, out Ray rightRay)
        {
            var eye = Camera.main.transform.position;
            Physics.SyncTransforms();
            foreach (var part in model.GetComponentsInChildren<RelayHit>().Where(h => h.role == "outer_shell"))
            {
                var mesh = part.GetComponent<MeshFilter>().sharedMesh;
                var vertices = mesh.vertices;
                var triangles = mesh.triangles;
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    var center = part.transform.TransformPoint((vertices[triangles[i]] + vertices[triangles[i + 1]] +
                        vertices[triangles[i + 2]]) / 3f);
                    // The test's right-hand aim is a little to the right and below the eye.
                    var origin = eye + Camera.main.transform.right * .09f - Vector3.up * .05f;
                    var ray = new Ray(origin, center - origin);
                    if (!Physics.Raycast(ray, out var hit, 12) || hit.collider.GetComponent<RelayHit>() != part) continue;
                    rightRay = ray;
                    return true;
                }
            }
            rightRay = default;
            return false;
        }

        static bool CapturePaperTarget(RelayApp app, GameObject model)
        {
            if (model == null) return false;
            // The stationary simulator headset does not automatically look down to inspect the desk.
            // Aim the tracked eye at the current committed CAD for this input and the synchronous evidence image.
            var eye = Camera.main.transform;
            var previous = eye.rotation;
            try
            {
                var bounds = ModelBoundsInPose(model, measuredPaper);
                var focus = measuredPaper.position + measuredPaper.rotation * bounds.center;
                eye.rotation = Quaternion.LookRotation(focus - eye.position, Vector3.up);
                if (!TryRightRay(model, out var ray)) return false;
                Debug.Log($"ContextPin smoke A4 evidence: eye {eye.position:F3}, forward {eye.forward:F3}, target {focus:F3}, viewport {Camera.main.WorldToViewportPoint(focus):F3}");
                var press = AppType.GetMethod("HandleExplicitTargetPress", AppMembers);
                bool Press(bool uiHit) => (bool)press.Invoke(app, new object[] { ray, uiHit });
                Require(!Press(true), "A with a UI hit must not select a design target.");
                report.blockedPanelA = true;
                try
                {
                    report.rightButtonEdgeVerified = PressRightWithLeftActive(app, ray);
                    Require(report.rightButtonEdgeVerified, "Right A must capture its renderer frame: " + ExplicitUi("ExplicitDesignStatus"));
                }
                catch (Exception error)
                {
                    // Continue checking real upload, candidate meshes, adoption and export, retaining the input failure in the result.
                    report.controllerInputError = error.GetBaseException().Message;
                    Debug.LogWarning("Explicit controller smoke: " + report.controllerInputError);
                    Require(Press(false), "The same right aim could not capture a simulated frame: " + ExplicitUi("ExplicitDesignStatus"));
                }
                Require(!Press(false), "Immediate duplicate A must keep the same selection/photo.");
                report.duplicateSelectionIgnored = true;
                report.rayActions.Add("active left UI trigger + independent right aim/A · outer_shell; no selection in manual alignment");
                return true;
            }
            finally { eye.rotation = previous; }
        }

        static string ExplicitUi(string id) => GameObject.Find(id)?.GetComponent<Text>()?.text ?? "";

        static bool RunExplicitStep()
        {
            if (step == 0)
            {
                InstallEditorPcmInput();
                return Click("MeetingCreate");
            }
            var state = SpatialSnapshot();
            if (state == null) return false;
            var app = UnityEngine.Object.FindAnyObjectByType<RelayApp>();
            var rounds = (JArray)state["rounds"];
            var latest = GameObject.Find("MeetingPreviewPivot");
            if (step >= 4 && step < 18)
            {
                int index = (step - 4) / 7;
                return ExerciseDesignRound(app, state, latest, index, (step - 4) % 7);
            }
            switch (step)
            {
                case 1:
                    if (state.Value<string>("session_status") != "recording" || inputRing == null) return false;
                    if (recordingObservedAt == 0) recordingObservedAt = Time.realtimeSinceStartupAsDouble;
                    Require(state.Value<string>("design_submit_mode") == "explicit" && state.Value<string>("provider_mode") == "live",
                        "New host must auto-start an explicit, live meeting.");
                    report.initialVersion = state.Value<string>("adopted_version_id");
                    report.explicitHostStarted = true;
                    return StartPaperFixture(app);
                case 2: return CheckPortraitPaper(app, latest);
                case 3:
                    Require(rounds.Count == 0 && ((JArray)state["images"]).Count == 0,
                        "Tabletop alignment must not create a photo or a design round.");
                    report.alignmentWithoutCandidates = true;
                    return RecoverWithLandscapePaper(app, latest);
                case 18: return Click("SpatialMenu");
                case 19: return Click("SpatialRealign");
                case 20: return RecalibrateFinal(app, state, latest);
                case 21: return Click("SpatialSessionAction");
                case 22: return Click("SpatialConfirmEnd");
                case 23: return (string)state["session_status"] == "completed" && Click("SpatialMenu");
                case 24: return Click("SpatialResult");
                case 25: return CheckExplicitArchive(state);
                default: throw new InvalidOperationException("Unexpected explicit smoke step.");
            }
        }

        static bool RecalibrateFinal(RelayApp app, JObject state, GameObject latest)
        {
            performedFrame = Time.frameCount;
            Require((string)state["adopted_version_id"] == report.finalVersion, "Realignment reverted the committed server version.");
            if (finalRecalibration == 0)
            {
                if (!PaperState(app, "A4PointSampling")) return false;
                SetPaperFixture(true);
            }
            if (finalRecalibration < 4) TouchPaper(app, PaperCorner(finalRecalibration), finalRecalibration + 1);
            else if (finalRecalibration == 4)
            {
                if (!PaperPreview(app, latest)) return false;
                CheckSideLatest(state, latest, report.finalVersion, false);
                if (!Click("AssemblyAlignmentConfirm")) return false;
            }
            else
            {
                if (latest == null || !PaperState(app, "A4ReviewSurfaceActive")) return false;
                CheckConfirmedPaper(app, latest);
                CheckSideLatest(state, latest, report.finalVersion, false);
                Require(Quaternion.Angle(anchor.rotation, commitPose.rotation) > 30f &&
                    GeometrySignature(latest) == candidateSignature, "The same V3 geometry must follow the rotated paper into its measured right-front slot.");
                report.v3Recalibrated = true;
                return true;
            }
            finalRecalibration++;
            return false;
        }

        static bool ExerciseDesignRound(RelayApp app, JObject state, GameObject latest, int index, int phase)
        {
            var rounds = (JArray)state["rounds"];
            var images = (JArray)state["images"];
            string baseId = index == 0 ? report.initialVersion : report.middleVersion;
            string label = index == 0 ? "V1" : "V2";
            switch (phase)
            {
                case 0:
                    Require((string)state["session_status"] == "recording", "The live PCM stream disconnected.");
                    if (Time.realtimeSinceStartupAsDouble - recordingObservedAt < 46 || latest == null ||
                        !ExplicitUi("ExplicitLatest").Contains(label)) return false;
                    CheckSideLatest(state, latest, baseId, true);
                    if (index == 0) report.initialWidthM = ModelWidth(latest);
                    if (!Click("ExplicitSpeechAction")) return false;
                    var samples = index == 0 ? speechSamples : followupSamples;
                    int first = PcmCursor() + PcmRate / 8;
                    Require(inputRing.SetData(samples, first), "Could not schedule fixture speech.");
                    stopSpeechAt = micStart + (first + samples.Length) / (double)PcmRate + .18;
                    return true;
                case 1: return Time.realtimeSinceStartupAsDouble >= stopSpeechAt &&
                    SpatialButtonText("ExplicitSpeechAction").Contains("완료") && Click("ExplicitSpeechAction");
                case 2:
                    var ranges = (state["design_speech_ranges"] as JObject)?.Properties().Select(k => k.Value).OrderBy(r => r.Value<long>("start_sample")).ToArray();
                    if (ranges == null || ranges.Length != index + 1) return false;
                    var range = ranges[index];
                    if ((string)range["status"] is "failed" or "no_speech") throw new InvalidOperationException("STT: " + range);
                    if ((string)range["status"] != "final" || !(range["utterances"] is JArray utterances)) return false;
                    var transcript = string.Join(" ", utterances.Select(line => (string)line["text"]));
                    if (string.IsNullOrWhiteSpace(transcript) || !ExplicitUi("ExplicitTranscript").Contains(transcript)) return false;
                    Require(rounds.Count == index && images.Count == index &&
                        range.Value<long>("received_through_sample") >= range.Value<long>("end_sample"),
                        "Speech alone must be fully received and transcribed without a new round or photo.");
                    if (index == 0) { report.finalMarkedSpeech = transcript; report.speechStartSample = range.Value<long>("start_sample"); }
                    else report.followupMarkedSpeech = transcript;
                    Require(report.speechStartSample >= 40L * PcmRate, "Exercise the live microphone beyond the WebSocket heartbeat.");
                    report.speechAloneWaited = true;
                    return true;
                case 3: return CapturePaperTarget(app, latest);
                case 4:
                    if (rounds.Count <= index) return false;
                    Require(rounds.Count == index + 1 && images.Count == index + 1, "A single right-A must produce one round and photo.");
                    var round = rounds[index];
                    if ((string)round["status"] is "failed" or "needs_input")
                        throw new InvalidOperationException("CAD planning: " + (round["error"] ?? round["question"]));
                    if ((string)round["status"] != "reviewable" || GameObject.Find("SpatialComparison") == null) return false;
                    var reference = round["context"]["reference_selection"];
                    Require((string)reference["part_id"] == "outer_shell" && (string)reference["source_mode"] == "xr_render" &&
                        (string)round["edit_base_version_id"] == baseId && (string)reference["reference_version_id"] == baseId &&
                        (string)reference["image_id"] == (string)images[index]["evidence_id"] &&
                        ((JArray)reference["hit_point_asset_m"]).Count == 3 && ((JArray)reference["image_uv"]).Count == 2,
                        "Round must use the photo, semantic hit, and currently committed CAD.");
                    report.capturedRenderedHit = true;
                    var candidates = ((JArray)round["candidates"]).Where(c => (string)c["status"] == "valid").ToArray();
                    Require(index == 0 ? candidates.Length == 3 : candidates.Length >= 1, "Expected valid generated alternatives.");
                    var models = ActiveAlternatives();
                    if (latest == null || !candidates.All(c => models.Any(m => Version(m) == (string)c["version_id"]))) return false;
                    CheckSideLatest(state, latest, baseId, true);
                    CheckPaperGallery(state, latest, models);
                    int[] sequence = index == 0 ? new[] { 0, 2, 1 } : new[] { 0 };
                    if (previewChoice == 0) previousModel = latest;
                    else
                    {
                        var viewed = (string)AppType.GetField("meetingViewedVersion", AppMembers).GetValue(app);
                        var expected = (string)candidates[sequence[previewChoice - 1]]["version_id"];
                        var shared = (string)state["shared_preview"]?["version_id"];
                        Require(previousModel == latest,
                            $"Preview {expected} replaced the committed central mesh {baseId} (old instance {previousModel?.GetInstanceID()}, current {latest.GetInstanceID()}).");
                        if (shared != expected)
                        {
                            Require(Time.realtimeSinceStartupAsDouble - previewClickedAt < 5,
                                $"Server preview acknowledgement missing: expected {expected}, local {viewed}, shared {shared}.");
                            return false;
                        }
                        Require(viewed == expected, $"Acknowledged preview differs locally: expected {expected}, local {viewed}, shared {shared}.");
                    }
                    if (previewChoice < sequence.Length)
                    {
                        var target = candidates[sequence[previewChoice]];
                        if (!Click("SpatialCandidate-" + (string)target["candidate_id"])) return false;
                        var clicked = (string)AppType.GetField("meetingViewedVersion", AppMembers).GetValue(app);
                        Require(clicked == (string)target["version_id"],
                            $"The candidate button highlighted {clicked}; expected {target["version_id"]}.");
                        previewClickedAt = Time.realtimeSinceStartupAsDouble;
                        previewChoice++;
                        performedFrame = Time.frameCount;
                        return false;
                    }
                    var chosen = candidates[sequence.Last()];
                    string selected = (string)chosen["version_id"];
                    if ((string)AppType.GetField("meetingViewedVersion", AppMembers).GetValue(app) != selected) return false;
                    if (index == 0) report.middleVersion = selected;
                    else report.finalVersion = selected;
                    candidateSignature = GeometrySignature(models.Single(m => Version(m) == selected));
                    previewChoice = 0;
                    report.previewKeptLatest = report.explicitCandidateMeshes = true;
                    return true;
                case 5: return Click("SpatialAdopt");
                default:
                    string adopted = index == 0 ? report.middleVersion : report.finalVersion;
                    if ((string)state["adopted_version_id"] != adopted || latest == null || Version(latest) != adopted) return false;
                    string newLabel = index == 0 ? "V2" : "V3";
                    if (!ExplicitUi("ExplicitLatest").Contains(newLabel)) return false;
                    CheckSideLatest(state, latest, adopted, true);
                    Require(previousModel == null || !previousModel.activeInHierarchy,
                        "The old mesh and its hit target must become inactive as soon as the new Latest appears.");
                    if (previousModel != null) return false; // Unity destroys a retired instance at end of frame.
                    Require(GeometrySignature(latest) == candidateSignature && adopted != baseId,
                        "The visible promoted geometry must match the chosen candidate and replace the previous hit target.");
                    if (index == 0) report.v2Replaced = true;
                    else { report.v3Replaced = true; report.finalWidthM = ModelWidth(latest); }
                    report.tableGrounded = report.explicitAdoption = true;
                    return true;
            }
        }

        static bool CheckExplicitArchive(JObject state)
        {
            var name = state.Value<string>("session_id") + "-r" + state["result"].Value<int>("revision") + ".zip";
            var path = Path.Combine(Application.persistentDataPath, "MeetingExports", name);
            if (!File.Exists(path)) return false;
            using var zip = ZipFile.OpenRead(path);
            string ReadText(string entry)
            {
                using var reader = new StreamReader(zip.GetEntry(entry).Open());
                return reader.ReadToEnd();
            }
            string Sha(string entry)
            {
                using var file = zip.GetEntry(entry).Open();
                using var sha = SHA256.Create();
                return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
            }
            var manifest = JObject.Parse(ReadText("manifest.json"));
            var records = JArray.Parse(ReadText("design_requests.json"));
            var history = JArray.Parse(ReadText("design_history.json"));
            var versions = new[] { report.initialVersion, report.middleVersion, report.finalVersion };
            Require(records.Count == 2 && history.Select(row => (string)row["version_id"]).SequenceEqual(versions),
                "Archive must identify the same V1, V2, and V3 in order.");
            foreach (var model in history)
            {
                var server = ((JArray)state["versions"]).Single(v => (string)v["version_id"] == (string)model["version_id"]);
                var directory = (string)model["stage_directory"];
                Require(Sha(directory + "/preview.glb") == (string)server["assets"]["preview.glb"]["sha256"] &&
                    Sha(directory + "/candidate.stl") == (string)server["assets"]["print.stl"]["sha256"] &&
                    JToken.DeepEquals(model["parameters"], server["parameters"]),
                    "Every archived V1/V2/V3 must contain the exact server-identified GLB, STL and parameters.");
            }
            var transcript = ReadText("transcript.txt");
            for (int i = 0; i < 2; i++)
            {
                var record = records.Single(r => (string)r["adopted_version_id"] == versions[i + 1]);
                var photo = record["reference_selection"];
                var lines = (JArray)state["design_speech_ranges"][(string)record["speech_range_id"]]["utterances"];
                var refs = new JArray(lines.Select(u => new JObject { ["utterance_id"] = u["utterance_id"], ["revision"] = u["revision"] }));
                Require((string)record["base_version_id"] == versions[i] && (string)photo["reference_version_id"] == versions[i] &&
                    JToken.DeepEquals(refs, record["utterances"]) && lines.All(u => transcript.Contains((string)u["text"])) &&
                    Sha((string)photo["file"]) == (string)photo["sha256"], "Both design decisions must preserve their actual source CAD, speech and photo.");
            }
            var final = ((JArray)state["versions"]).Single(v => (string)v["version_id"] == report.finalVersion);
            Require((string)manifest["adopted_version_id"] == report.finalVersion && (string)manifest["adopted_version_label"] == "V3" &&
                ReadText("minutes.txt").Contains(report.finalVersion) && ReadText("summary.md").Contains(report.finalVersion) &&
                zip.GetEntry((string)manifest["final_scad"]) != null &&
                Sha((string)manifest["final_stl"]) == (string)final["assets"]["print.stl"]["sha256"] &&
                (string)history.Last["parent_version_id"] == report.middleVersion, "Final downloaded CAD/STL must refer to the exact reviewed V3.");
            report.latestZipAgrees = report.savedMeeting = true;
            report.exportPath = path;
            Finish(string.IsNullOrEmpty(report.controllerInputError) ? null : new InvalidOperationException(report.controllerInputError));
            return true;
        }
    }
}
