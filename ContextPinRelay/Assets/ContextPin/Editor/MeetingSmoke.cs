using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR;

namespace ContextPin.Editor
{
    public static class MeetingSmoke
    {
        [Serializable]
        sealed class Report
        {
            public bool success, simulator, assembly, fixedMount, syntheticReference, invalidatedOverlay;
            public string runtime, initialVersion, finalVersion, error;
            public float initialWidthM, finalWidthM, stlWidthMm;
            public int renderViews;
            public List<string> rayActions = new List<string>();
        }

        static Report report;
        static double deadline;
        static int step, performedFrame;
        static string candidateControl;
        static Vector3 originalPosition;
        static Quaternion originalRotation;
        static Vector3[] originalVertices;
        static readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();

        public static void Begin(bool simulator)
        {
            report = new Report { simulator = simulator, assembly = Environment.GetEnvironmentVariable("MEETING_SMOKE_ASSEMBLY") == "1" };
            step = 0;
            performedFrame = -1;
            deadline = EditorApplication.timeSinceStartup + 65;
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
                if (report.assembly ? RunAssemblyStep() : RunStep()) { step++; performedFrame = Time.frameCount; }
            }
            catch (Exception error) { Finish(error); }
        }

        static bool RunStep()
        {
            var pivot = GameObject.Find("MeetingPreviewPivot");
            switch (step)
            {
                case 0: return Click("HomeMeeting");
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
                    if (!Click("HomeDemo")) return false;
                    Require(GameObject.Find("StartRecording") != null, "Legacy review entry must still work.");
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
                case 0: return Click("HomeMeeting");
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
                    Require(Mathf.Abs(report.initialWidthM - .1f) < .0001f, "OpenSCAD reference width must be 0.1m in Unity.");
                    return Click("MeetingParameterPlus");
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
                    if (report.simulator) return Click("AssemblyAlignmentStart");
                    return Click("MeetingAdopt");
                case 6:
                    if (!report.simulator) return Click("MeetingExportStl");
                    if (!CheckSyntheticAssemblyReference()) return false;
                    Finish(null);
                    return true;
                case 7:
                    var path = Path.Combine(Application.persistentDataPath, "MeetingExports", report.finalVersion + ".stl");
                    if (!File.Exists(path)) return false;
                    report.stlWidthMm = StlWidth(path);
                    Require(Mathf.Abs(report.stlWidthMm - 100) < .01f && Mathf.Abs(report.stlWidthMm / 1000 - report.finalWidthM) < .0001f,
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
            var meshes = pivot.GetComponentsInChildren<MeshRenderer>();
            Require(meshes.Length > 0 && meshes.All(renderer => renderer.sharedMaterial?.shader?.name == "ContextPin/Assembly Overlay" &&
                renderer.sharedMaterial.color.a < 1f), "Assembly meshes must render with the translucent overlay material.");
        }

        static bool CheckSyntheticAssemblyReference()
        {
            // No real contact points exist in the simulator. Exercise the registration and
            // hide-on-invalidation code by providing known test measurements.
            var pivot = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(candidate => candidate.name == "MeetingPreviewPivot" && Version(candidate.gameObject) == report.initialVersion);
            if (pivot == null) return false;
            var app = UnityEngine.Object.FindAnyObjectByType<RelayApp>();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var recordPoint = typeof(RelayApp).GetMethod("RecordAssemblyPoint", flags);
            var invalidate = typeof(RelayApp).GetMethod("InvalidateRegisteredPoses", flags);
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
            invalidate.Invoke(app, null);
            report.invalidatedOverlay = !pivot.gameObject.activeInHierarchy &&
                GameObject.Find("MeetingDrawingMode")?.GetComponent<Text>()?.text.Contains("추적 끊김") == true;
            Require(report.invalidatedOverlay, "Invalidated registration must hide the overlay and report the lost state.");
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
            report.success = error == null;
            report.error = error?.ToString() ?? "";
            var output = Environment.GetEnvironmentVariable("RELAY_WORKSPACE") ?? Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            var check = report.assembly ? "assembly" : "meeting";
            var file = Path.Combine(output, "artifacts", $"{check}-{(report.simulator ? "simulator" : "smoke")}-report.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, JsonUtility.ToJson(report, true));
            if (error != null) Debug.LogException(error);
            Debug.Log("ContextPin meeting smoke: " + file);
            SessionState.SetBool("TraceSimulatorExit", true);
            SessionState.SetInt("TraceSimulatorExitCode", error == null ? 0 : 2);
            EditorApplication.isPlaying = false;
        }
    }
}
