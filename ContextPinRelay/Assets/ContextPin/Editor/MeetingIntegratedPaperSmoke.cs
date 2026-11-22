using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using GLTFast;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ContextPin.Editor
{
    public static partial class MeetingSmoke
    {
        // With both fixtures enabled, the simulator remains the host and owns the
        // microphone for the whole meeting. A separately authenticated runtime
        // transport and GLB consumer remain connected in another paper frame.
        static bool IntegratedTest => ColocationTest && TimelineTest;
        static readonly JObject[] capturedPlacements = new JObject[2];
        static readonly int[] capturedRevisions = new int[2], capturedFrames = new int[2], versionsBefore = new int[2];
        static readonly bool[] observedInitial = new bool[2];
        static readonly HashSet<string> probedStates = new HashSet<string>();
        static readonly Dictionary<string, Task<GameObject>> peerMeshes = new Dictionary<string, Task<GameObject>>();
        static readonly List<GltfImport> peerImports = new List<GltfImport>();
        static IDisposable peer;
        static ConcurrentQueue<JObject> peerSnapshots;
        static JObject peerState;
        static Pose peerFrame;
        static string peerId, peerFault, peerSession;
        static bool peerJoining;

        static void ResetIntegratedChecks()
        {
            Array.Clear(observedInitial, 0, observedInitial.Length);
            Array.Clear(integratedGrips, 0, integratedGrips.Length);
            probedStates.Clear();
            peerJoining = false;
            peerId = peerFault = peerSession = null;
            peerState = null;
        }

        static void StopPeerObserver()
        {
            peer?.Dispose();
            peer = null;
            foreach (var mesh in peerMeshes.Values)
                if (mesh.Status == TaskStatus.RanToCompletion && mesh.Result != null)
                    UnityEngine.Object.Destroy(mesh.Result);
            foreach (var gltf in peerImports) gltf.Dispose();
            peerMeshes.Clear();
            peerImports.Clear();
        }

        static IEnumerator PeerJson(string endpoint, JObject payload, Action<JObject> onSuccess)
        {
            Action<JObject, string> reply = (value, err) => { if (err != null) peerFault = err; else onSuccess(value); };
            return (IEnumerator)peer.GetType().GetMethod("Json").Invoke(peer, new object[] { endpoint, payload, reply });
        }

        static IEnumerator ConnectPeer(RelayApp host, JObject state)
        {
            peerJoining = true;
            var hostTransport = AppType.GetField("meetingTransport", AppMembers).GetValue(host);
            var type = hostTransport.GetType();
            peer = (IDisposable)Activator.CreateInstance(type, type.GetProperty("Address").GetValue(hostTransport).ToString());
            var join = new JObject { ["device_id"] = "integrated-guest-" + Guid.NewGuid().ToString("N"),
                ["display_name"] = "Simulator desk guest", ["join_code"] = state["join_code"],
                ["capabilities"] = (JObject)AppType.GetMethod("MeetingCapabilities", AppMembers).Invoke(host, null) };
            yield return PeerJson("/v1/join", join, reply =>
            {
                peerSession = (string)reply["session_id"];
                peerId = (string)reply["participant_id"];
                peerState = (JObject)reply["snapshot"];
                type.GetProperty("SessionId").SetValue(peer, peerSession);
                type.GetProperty("Token").SetValue(peer, (string)reply["token"]);
                peerSnapshots = (ConcurrentQueue<JObject>)type.GetField("Snapshots").GetValue(peer);
                type.GetMethod("StartEvents").Invoke(peer, new object[] { (long)peerState["last_seq"] });
            });
            if (peerFault != null) yield break;
            while (!(bool)type.GetProperty("EventsConnected").GetValue(peer)) yield return null;
            foreach (string status in new[] { "calibrating", "aligned" })
            {
                var payload = new JObject { ["paper_frame_revision"] = state["spatial"]["paper_frame_revision"], ["status"] = status };
                yield return PeerJson("/v1/sessions/" + peerSession + "/commands", new JObject {
                    ["command_id"] = Guid.NewGuid().ToString("N"), ["type"] = "paper_alignment", ["payload"] = payload },
                    reply => { });
                if (peerFault != null) yield break;
            }
        }

        static Pose MeasureGuestFrame()
        {
            var alternate = new Pose(measuredPaper.position + new Vector3(2.4f, .35f, -.8f),
                Quaternion.AngleAxis(162f, Vector3.up) * measuredPaper.rotation);
            var fixture = new GameObject("Guest calibration samples");
            fixture.SetActive(false);
            try
            {
                // A second receiver fits these four numbered samples using the normal A4 routine.
                var receiver = fixture.AddComponent<RelayApp>();
                var corners = (Vector3[])AppType.GetField("a4Corners", AppMembers).GetValue(receiver);
                for (int i = 0; i < 4; i++)
                {
                    float x = i == 0 || i == 3 ? -.5f : .5f;
                    float z = i < 2 ? -.5f : .5f;
                    corners[i] = alternate.position + alternate.rotation * new Vector3(x * explicitAreaSize.x, 0, z * explicitAreaSize.y);
                }
                AppType.GetMethod("MeasureA4Surface", AppMembers).Invoke(receiver, null);
                var result = (Pose)AppType.GetField("a4Surface", AppMembers).GetValue(receiver);
                Require(Vector3.Distance(result.position, alternate.position) < .0005f &&
                    Quaternion.Angle(result.rotation, alternate.rotation) < .12f, "Guest A4 fit lost its independent tracking frame.");
                report.guestMeasuredSeparatePaper = true;
                return result;
            }
            finally { UnityEngine.Object.Destroy(fixture); }
        }

        static bool ReadPeer(RelayApp host, JObject state)
        {
            if (!peerJoining)
            {
                // Alternate tracking origin, opposite viewpoint, same numbered sheet.
                peerFrame = MeasureGuestFrame();
                host.StartCoroutine(ConnectPeer(host, state));
                return false;
            }
            Require(peerFault == null, peerFault);
            if (peerSnapshots == null) return false;
            while (peerSnapshots.TryDequeue(out var next)) peerState = next;
            var s = peerState["spatial"];
            if (!s["participants"].Any(p => (string)p["participant_id"] == peerId && (string)p["status"] == "aligned")) return false;
            Require(peerSession == (string)state["session_id"] && peerId != (string)s["host_participant_id"], "Expected a separate aligned guest in the host meeting.");
            report.integratedMeetingId = peerSession;
            report.integratedGuestId = peerId;
            return true;
        }

        static async Task<GameObject> LoadPeerMesh(RelayApp host, string version, JToken asset)
        {
            var done = new TaskCompletionSource<byte[]>();
            Action<byte[], string> downloaded = (bytes, err) => {
                if (err != null) done.SetException(new InvalidOperationException(err)); else done.SetResult(bytes);
            };
            host.StartCoroutine((IEnumerator)peer.GetType().GetMethod("Download").Invoke(peer,
                new object[] { (string)asset["url"], 16 * 1024 * 1024, downloaded }));
            var glb = await done.Task;
            using var sha = SHA256.Create();
            string digest = BitConverter.ToString(sha.ComputeHash(glb)).Replace("-", "").ToLowerInvariant();
            Require(digest == (string)asset["sha256"], "Guest downloaded another version's GLB.");
            var gltf = new GltfImport();
            peerImports.Add(gltf);
            Require(await gltf.Load(glb), "Guest GLB import failed for " + version);
            var root = new GameObject("IntegratedGuest-" + version);
            var model = new GameObject("Mesh");
            model.transform.SetParent(root.transform, false);
            Require(await gltf.InstantiateMainSceneAsync(model.transform), "Guest GLB scene did not instantiate.");
            return root;
        }

        static bool CheckPeerModel(RelayApp host, GameObject hostModel, string id, JToken asset, JToken paper)
        {
            if (paper == null || asset == null) return false;
            if (!peerMeshes.TryGetValue(id, out var load))
                peerMeshes.Add(id, load = LoadPeerMesh(host, id, asset));
            if (!load.IsCompleted) return false;
            var model = load.GetAwaiter().GetResult();
            model.SetActive(true);
            var q = paper["rotation_xyzw"];
            var received = new Pose(ReadWorldVector(paper["position"]), new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]));
            var world = (Pose)AppType.GetMethod("PaperToWorldPose", PcmHooks).Invoke(null, new object[] { peerFrame, received });
            model.transform.SetPositionAndRotation(world.position, world.rotation);
            var hostPose = RelativePose(hostModel.transform, measuredPaper);
            var guestPose = RelativePose(model.transform, peerFrame);
            var hostBounds = ModelBoundsInPose(hostModel, measuredPaper);
            var guestBounds = ModelBoundsInPose(model, peerFrame);
            Require(Vector3.Distance(hostModel.transform.position, model.transform.position) > 1f &&
                    Vector3.Distance(hostPose.position, guestPose.position) < .0005f && Quaternion.Angle(hostPose.rotation, guestPose.rotation) < .12f &&
                    Vector3.Distance(hostBounds.min, guestBounds.min) < .0005f && Vector3.Distance(hostBounds.max, guestBounds.max) < .0005f,
                    "The independently downloaded guest mesh differs in the physical frame: " + id);
            return true;
        }

        static bool PeerLatest(RelayApp host, JObject state, GameObject hostModel)
        {
            if (!ReadPeer(host, state)) return false;
            var spatial = peerState["spatial"];
            if ((string)spatial["latest_version_id"] != Version(hostModel) ||
                !JToken.DeepEquals(spatial["latest_pose_revision"], state["spatial"]["latest_pose_revision"])) return false;
            return CheckPeerModel(host, hostModel, Version(hostModel), spatial["latest_model_asset"], spatial["latest_pose_paper"]);
        }

        static bool CheckRoundDrag(RelayApp app, JObject state, GameObject model, int index)
        {
            if (!observedInitial[index])
            {
                if (!PeerLatest(app, state, model)) return false;
                versionsBefore[index] = state["versions"].Count();
                observedInitial[index] = true;
            }
            if (!CheckHostGrip(app, state, model, index) || !PeerLatest(app, state, model)) return false;
            Require(versionsBefore[index] == state["versions"].Count(), "Grip must not create design history or a new CAD version.");
            report.sameMeetingTwoGrips = index == 1;
            return true;
        }

        static void SavePlacementAtB(JObject state, int index)
        {
            var spatial = state["spatial"];
            capturedPlacements[index] = (JObject)spatial["latest_pose_paper"].DeepClone();
            capturedRevisions[index] = (int)spatial["latest_pose_revision"];
            capturedFrames[index] = (int)spatial["paper_frame_revision"];
        }

        static void VerifyFrozenPlacement(JToken owner, int index, string version)
        {
            Require((string)owner["base_version_id"] == version && JToken.DeepEquals(owner["latest_pose_paper"], capturedPlacements[index]) &&
                (int)owner["paper_frame_revision"] == capturedFrames[index] && (int)owner["latest_pose_revision"] == capturedRevisions[index],
                "Request and candidate set must carry the exact base and placement observed at B down.");
        }

        static bool CheckPeerGallery(RelayApp app, JObject state, GameObject latest, JToken set, int index)
        {
            if (!PeerLatest(app, state, latest)) return false;
            var spatial = peerState["spatial"];
            if ((string)spatial["active_candidate_set_id"] != (string)set["candidate_set_id"]) return false;
            var guestSet = peerState["candidate_sets"]?[captureId];
            if (guestSet == null || (string)guestSet["status"] != "ready") return false;
            VerifyFrozenPlacement(guestSet, index, Version(latest));
            foreach (var c in guestSet["candidates"])
            {
                var id = (string)c["candidate_id"];
                var pose = spatial["candidate_poses_paper"]?[id];
                if (pose == null) return false;
                var hostModel = GameObject.Find("Candidate" + c["candidate_key"]);
                if (hostModel == null || !CheckPeerModel(app, hostModel, (string)c["version_id"], c["model_asset"], pose)) return false;
            }
            report.peerGalleryChecks = index + 1;
            return true;
        }

        static bool CheckPeerCommit(RelayApp app, JObject state, GameObject model, int index)
        {
            if (!PeerLatest(app, state, model)) return false;
            foreach (var other in peerMeshes.Where(entry => entry.Key != Version(model) && entry.Value.IsCompleted))
                other.Value.GetAwaiter().GetResult().SetActive(false);
            Require(JToken.DeepEquals(state["spatial"]["latest_pose_paper"], capturedPlacements[index]) &&
                (int)state["spatial"]["latest_pose_revision"] == capturedRevisions[index], "Committing geometry must preserve the frozen anchor.");
            report.peerModelChecks = index + 1;
            if (index == 1)
            {
                report.sameMeetingTwoCommands = report.guestHostBoundsAgree = true;
                report.inputStateChecks = probedStates.Count == 5;
                Require(report.inputStateChecks, "Skipped an interaction state: " + string.Join(",", probedStates));
            }
            return true;
        }

        static void ProbeRejectedB(RelayApp app)
        {
            var previous = PendingRangeId(app);
            var cursor = (downBefore, downAfter, upBefore, upAfter);
            SetTestB(app, true);
            SetTestB(app, false);
            (downBefore, downAfter, upBefore, upAfter) = cursor;
            Require(PendingRangeId(app) == previous, "A blocked B must not start a request.");
        }

        static void ProbeBusyInput(RelayApp app, GameObject latest, string phase, bool pressB, bool pressA)
        {
            if (probedStates.Contains(phase)) return;
            string actual = AppType.GetProperty("CurrentPaperInteraction", AppMembers).GetValue(app).ToString();
            Require(actual == phase, $"Expected {phase} when testing controller mappings, got {actual}.");
            if (pressB) ProbeRejectedB(app);
            CheckBlockedGrip(app, latest, phase);
            if (pressA)
            {
                var request = AppType.GetField("explicitRequest", AppMembers).GetValue(app);
                var selection = request?.GetType().GetField("Selection");
                var old = selection?.GetValue(request);
                var ray = GripRay(RelativePose(latest.transform, measuredPaper).position, .35f);
                PressRightWithLeftActive(app, ray, selectCandidate: true);
                Require(selection?.GetValue(request) == old, "A on Latest must not select a new target during " + phase);
            }
            probedStates.Add(phase);
        }
    }
}
