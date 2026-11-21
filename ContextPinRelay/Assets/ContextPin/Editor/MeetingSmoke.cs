using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            public bool success, simulator;
            public string runtime, initialVersion, finalVersion, error;
            public float initialWidthM, finalWidthM, stlWidthMm;
            public int renderViews;
            public List<string> rayActions = new List<string>();
        }

        static Report report;
        static double deadline;
        static int step, performedFrame;
        static string candidateControl;
        static readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();

        public static void Begin(bool simulator)
        {
            report = new Report { simulator = simulator };
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
                if (RunStep()) { step++; performedFrame = Time.frameCount; }
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
            var file = Path.Combine(output, "artifacts", report.simulator ? "meeting-simulator-report.json" : "meeting-smoke-report.json");
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
