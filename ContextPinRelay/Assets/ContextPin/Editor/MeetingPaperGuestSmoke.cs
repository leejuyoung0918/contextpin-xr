using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace ContextPin.Editor
{
    public static partial class MeetingSmoke
    {
        static bool ColocationTest => Environment.GetEnvironmentVariable("MEETING_COLOCATION_SMOKE") == "1";
        static readonly Dictionary<string, Pose> hostCandidatePaper = new();
        static Pose hostLatestPaper, hostPaper;
        static Vector3 hitOnPaper;
        static string hostCommandUrl, hostToken, roomCode, guestId, testHostId, guestRound;
        static string hostCommandError;
        static IDisposable hostEvents;
        static int guestCorner, digitIndex;
        static bool enteringGuest, guestPointed, guestSuggested, hostPointedV2;
        static Vector2 sheetSize;
        static Vector3 v2Hit;
        static JObject candidateB;

        // Independent ground truth. The test fixtures for the second headset deliberately
        // use different tracking origins and rotated coordinate axes.
        static Pose RelativePose(Transform model, Pose frame)
        {
            var inv = Quaternion.Inverse(frame.rotation);
            return new Pose(inv * (model.position - frame.position), inv * model.rotation);
        }

        static void RequireSamePhysicalPose(Transform model, Pose expected, string what)
        {
            var actual = RelativePose(model, measuredPaper);
            Require(Vector3.Distance(actual.position, expected.position) < .0005f &&
                    Quaternion.Angle(actual.rotation, expected.rotation) < .12f,
                    $"{what} differs in the physical paper frame. Expected {expected}, measured {actual}.");
        }

        static void JoinAsNewViewer(RelayApp app, int index)
        {
            AppType.GetMethod("EnterMeeting", AppMembers).Invoke(app, null);
            AppType.GetField("meetingDeviceId", AppMembers).SetValue(app, $"sim-paper-guest-{index}-{Guid.NewGuid():N}");
            enteringGuest = true;
            digitIndex = 0;
            guestCorner = 0;
        }

        static void SetGuestTrackingOrigin(float heading, Vector3 shift)
        {
            explicitAreaSize = sheetSize;
            var camera = Camera.main.transform;
            var right = Vector3.ProjectOnPlane(camera.right, Vector3.up).normalized;
            var forward = Vector3.Cross(right, Vector3.up);
            measuredPaper = new Pose(camera.position + forward * .55f - Vector3.up * .24f + shift,
                Quaternion.AngleAxis(heading, Vector3.up) * Quaternion.LookRotation(forward));
            Require(Vector3.Dot(camera.right, measuredPaper.rotation * Vector3.right) < -.8f &&
                    Vector3.Distance(measuredPaper.position, hostPaper.position) > .12f,
                    "Guest must be looking from the opposite numbered edge, in a different world origin.");
        }

        static bool AlignGuest(RelayApp app)
        {
            if (!PaperState(app, "A4PointSampling")) return false;
            TouchPaper(app, PaperCorner(guestCorner), guestCorner + 1);
            performedFrame = Time.frameCount;
            return ++guestCorner == 4;
        }

        static IEnumerator RemoteHostCommand(string type, JObject payload)
        {
            var body = new JObject { ["command_id"] = Guid.NewGuid().ToString("N"), ["type"] = type, ["payload"] = payload };
            using var r = new UnityWebRequest(hostCommandUrl, "POST");
            r.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body.ToString(Formatting.None)));
            r.downloadHandler = new DownloadHandlerBuffer();
            r.SetRequestHeader("Authorization", "Bearer " + hostToken);
            r.SetRequestHeader("Content-Type", "application/json");
            r.timeout = 20;
            yield return r.SendWebRequest();
            if (r.result != UnityWebRequest.Result.Success)
                hostCommandError = type + " " + r.responseCode + " " + r.downloadHandler.text;
        }

        static void CheckLegacyPaperLayout(RelayApp app, JObject snapshot)
        {
            var profile = (JObject)snapshot["profile"];
            var layout = profile.Value<string>("review_layout");
            var field = AppType.GetField("paperLatestPose", AppMembers);
            var canonical = (Pose)field.GetValue(app);
            Require(PaperState(app, "PaperRenderable") && !PaperState(app, "PaperLayoutNeedsReset"),
                "A host aligned to current paper slots must be able to render.");
            try
            {
                // Stored meetings keep their original profile and canonical pose from the host.
                profile["review_layout"] = "center_latest_side_candidates";
                field.SetValue(app, new Pose(canonical.position - Vector3.right * .24f, canonical.rotation));
                Require(PaperState(app, "PaperLayoutNeedsReset") && !PaperState(app, "PaperRenderable"),
                    "The legacy centre pose must block old models until the host republishes the offset layout.");
                field.SetValue(app, canonical);
                Require(!PaperState(app, "PaperLayoutNeedsReset") && PaperState(app, "PaperRenderable"),
                    "A migrated room must accept its offset pose even though the saved profile retains the legacy name.");
                report.legacyCenteredLayoutRejected = true;
            }
            finally
            {
                profile["review_layout"] = layout;
                field.SetValue(app, canonical);
            }
        }

        static void CheckUnadoptedFollowup(RelayApp app, JObject spatial)
        {
            var request = AppType.GetField("explicitRequest", AppMembers);
            var hint = AppType.GetField("explicitHint", AppMembers);
            var priorRequest = request.GetValue(app);
            var priorHint = hint.GetValue(app);
            var active = spatial["active_request_id"].DeepClone();
            try
            {
                spatial["active_request_id"] = null;
                Require(AppType.GetProperty("CurrentCandidateSet", AppMembers).GetValue(app) == null,
                    "Clearing the active set must not resurrect a previous local request.");
                spatial["active_request_id"] = active;
                var draft = Activator.CreateInstance(request.FieldType, true);
                var speech = request.FieldType.GetField("Speech");
                speech.SetValue(draft, Enum.Parse(speech.FieldType, "Final"));
                request.FieldType.GetField("BaseId").SetValue(draft, report.initialVersion);
                request.SetValue(app, draft);
                hint.SetValue(app, null);
                var guidance = (string)AppType.GetProperty("ExplicitDesignStatus", AppMembers).GetValue(app);
                Require(guidance.Contains("수정할 곳"), "The new command should ask for its target while the old A/B set is still visible.");
                var candidate = GameObject.Find("CandidateB");
                var b = ModelBoundsInPose(candidate, measuredPaper);
                var ray = new Ray(measuredPaper.position + measuredPaper.rotation * new Vector3(b.max.x-.005f, b.max.y+.025f, b.center.z), Vector3.down);
                Require(Physics.Raycast(ray, out var hit, 1) && hit.transform.IsChildOf(candidate.transform),
                    "The follow-up input must actually point at candidate B.");
                Require(!PressRightWithLeftActive(app, ray, selectCandidate:true) &&
                    !(bool)AppType.GetField("bundleCommitPending", AppMembers).GetValue(app),
                    "An A press on the old candidate must not adopt it during a new target selection.");
            }
            finally
            {
                spatial["active_request_id"] = active;
                request.SetValue(app, priorRequest);
                hint.SetValue(app, priorHint);
            }
        }

        static bool RunPaperGuestStep()
        {
            var app = UnityEngine.Object.FindAnyObjectByType<RelayApp>();
            if (enteringGuest)
            {
                string button = GameObject.Find("MeetingShowCode") != null ? "MeetingShowCode" :
                    digitIndex < roomCode.Length ? "MeetingDigit-" + roomCode[digitIndex] : "MeetingJoin";
                if (Click(button))
                {
                    if (button == "MeetingJoin") enteringGuest = false;
                    else if (button != "MeetingShowCode") digitIndex++;
                    performedFrame = Time.frameCount;
                }
                return false;
            }
            var s = SpatialSnapshot();
            if (s == null) return false;
            Require(hostCommandError == null, hostCommandError);
            var sp = (JObject)s["spatial"];
            var latest = GameObject.Find("MeetingPreviewPivot");
            var participant = (string)AppType.GetField("meetingParticipant", AppMembers).GetValue(app);
            switch (step)
            {
                case 11:
                    if (latest == null || sp?["candidate_poses_paper"] is not JObject slots || slots.Count != 2 ||
                        sp["shared_selection"] is not JObject) return false;
                    hostPaper = measuredPaper;
                    sheetSize = explicitAreaSize;
                    hostLatestPaper = RelativePose(latest.transform, measuredPaper);
                    var set = s["candidate_sets"][captureId];
                    hostCandidatePaper.Clear();
                    foreach (var c in set["candidates"])
                    {
                        var root = GameObject.Find("Candidate" + (string)c["candidate_key"]);
                        Require(root != null && Version(root) == (string)c["version_id"], "Host candidate identity mismatch.");
                        hostCandidatePaper[(string)c["candidate_id"]] = RelativePose(root.transform, measuredPaper);
                    }
                    candidateB = (JObject)set["candidates"].Single(c => (string)c["candidate_key"] == "B").DeepClone();
                    report.middleVersion = (string)candidateB["version_id"];
                    hitOnPaper = Quaternion.Inverse(measuredPaper.rotation) * (capturedPoint - measuredPaper.position);
                    roomCode = (string)s["join_code"];
                    Require(ExplicitUi("SpatialJoinCode").Contains(roomCode), "Host panel must show this room code.");
                    testHostId = participant;
                    var transport = AppType.GetField("meetingTransport", AppMembers).GetValue(app);
                    hostCommandUrl = ((Uri)transport.GetType().GetProperty("Address").GetValue(transport)).GetLeftPart(UriPartial.Authority)
                        + (string)transport.GetType().GetProperty("SessionPath").GetValue(transport) + "/commands";
                    hostToken = (string)transport.GetType().GetProperty("Token").GetValue(transport);
                    // The first headset remains aligned and connected while the simulator
                    // renders the guest viewpoint. Both identities keep their own WebSocket.
                    var transportType = transport.GetType();
                    if (hostEvents == null)
                    {
                        hostEvents = (IDisposable)Activator.CreateInstance(transportType, new object[] { ((Uri)transportType.GetProperty("Address").GetValue(transport)).ToString() });
                        transportType.GetProperty("Token").SetValue(hostEvents, hostToken);
                        transportType.GetProperty("SessionId").SetValue(hostEvents, (string)s["session_id"]);
                        transportType.GetMethod("StartEvents").Invoke(hostEvents, new object[] { (long)s["last_seq"] });
                    }
                    if (!(bool)transportType.GetProperty("EventsConnected").GetValue(hostEvents)) return false;
                    CheckLegacyPaperLayout(app, s);
                    CheckUnadoptedFollowup(app, sp);
                    JoinAsNewViewer(app, 1);
                    return true;
                case 12:
                case 21:
                    if (participant == testHostId || (step == 21 && participant == guestId)) return false;
                    if (!StartPaperFixture(app)) return false;
                    Require(latest == null && GameObject.Find("CandidateContainer") == null && GameObject.Find("SharedPaperHit") == null,
                        "A new guest must see no shared content before calibration.");
                    if (step == 12) guestId = participant;
                    else Require((string)s["adopted_version_id"] == report.finalVersion && (string)sp["latest_version_id"] == report.finalVersion,
                        "A late join must receive V3 in its initial snapshot.");
                    SetGuestTrackingOrigin(step == 12 ? 167f : 186f, new Vector3(.19f, 0, -.04f));
                    return true;
                case 13: case 22: case 25: return AlignGuest(app);
                case 14:
                    if (latest == null || !PaperState(app, "PaperRenderable") || Version(latest) != report.initialVersion) return false;
                    RequireSamePhysicalPose(latest.transform, hostLatestPaper, "Guest V1");
                    CheckPaperLatest(latest);
                    CheckPhysicalScale(s, latest);
                    report.numberedCornersOppositeView = report.paperLatestAgrees = true;
                    foreach (var c in s["candidate_sets"][captureId]["candidates"])
                    {
                        var root = GameObject.Find("Candidate" + (string)c["candidate_key"]);
                        if (root == null) return false;
                        Require(Version(root) == (string)c["version_id"], "Guest candidate identity mismatch.");
                        RequireSamePhysicalPose(root.transform, hostCandidatePaper[(string)c["candidate_id"]], "Guest candidate " + c["candidate_key"]);
                    }
                    var marker = GameObject.Find("SharedPaperHit");
                    if (marker == null) return false;
                    Require(Vector3.Distance(marker.transform.position, measuredPaper.position + measuredPaper.rotation * hitOnPaper) < .0005f,
                        "The remote marker must land on the host's actual recorded model ray hit.");
                    report.paperCandidatesAgree = report.paperHitAgrees = true;
                    return true;
                case 15:
                    if (!guestSuggested)
                    {
                        var b = ModelBoundsInPose(GameObject.Find("CandidateB"), measuredPaper);
                        var above = measuredPaper.position + measuredPaper.rotation * new Vector3(b.max.x-.005f, b.max.y+.025f, b.center.z);
                        Require(PressRightWithLeftActive(app, new Ray(above, Vector3.down), selectCandidate:true),
                            "Guest A on candidate B must send a suggestion.");
                        guestSuggested = true;
                        return false;
                    }
                    if ((string)sp["selected_candidate_id"] != (string)candidateB["candidate_id"]) return false;
                    Require((string)sp["selected_by_participant_id"] == guestId && (string)s["adopted_version_id"] == report.initialVersion,
                        "A guest suggestion must be attributed to the guest and leave V1 displayed.");
                    app.StartCoroutine(RemoteHostCommand("commit_edit_candidate", new JObject {
                        ["meeting_id"] = s["session_id"], ["paper_frame_revision"] = sp["paper_frame_revision"], ["base_version_id"] = report.initialVersion,
                        ["expected_latest_version_id"] = report.initialVersion, ["request_id"] = captureId,
                        ["candidate_set_id"] = sp["active_candidate_set_id"], ["candidate_id"] = candidateB["candidate_id"] }));
                    return true;
                case 16:
                    if (latest == null || Version(latest) != report.middleVersion || (string)s["adopted_version_id"] != report.middleVersion ||
                        GameObject.Find("CandidateContainer") != null) return false;
                    RequireSamePhysicalPose(latest.transform, hostLatestPaper, "Guest V2 after remote commit");
                    CheckPaperLatest(latest);
                    CheckPhysicalScale(s, latest);
                    report.paperCommitReceived = true;
                    // Shoot the actual V2 base from above. Feed the same physical ray to the
                    // runtime's host-world conversion and compare the guest marker against the hit.
                    if (!hostPointedV2)
                    {
                        var rayGuest = new Ray(latest.transform.TransformPoint(.03f,.21f,.062f), -latest.transform.up);
                        Physics.SyncTransforms();
                        Require(Physics.Raycast(rayGuest, out var hit, .5f) && hit.transform.IsChildOf(latest.transform),
                            "Shared ray fixture must hit the V2 mesh.");
                        v2Hit = hit.point;
                        var fromGuest = Quaternion.Inverse(measuredPaper.rotation);
                        Vector3 ToHostPoint(Vector3 p) => hostPaper.position + hostPaper.rotation * (fromGuest * (p - measuredPaper.position));
                        var hostRay = new Ray(ToHostPoint(rayGuest.origin), hostPaper.rotation * (fromGuest * rayGuest.direction));
                        var payload = (JObject)AppType.GetMethod("PaperSelectionPayload", PcmHooks).Invoke(null,
                            new object[] { hostPaper, (int)sp["paper_frame_revision"], report.middleVersion, hostRay, ToHostPoint(v2Hit) });
                        app.StartCoroutine(RemoteHostCommand("paper_selection", payload));
                        hostPointedV2 = true;
                        return false;
                    }
                    var v2Marker = GameObject.Find("SharedPaperHit");
                    if ((string)(sp["shared_selection"] as JObject)?["model_version_id"] != report.middleVersion || v2Marker == null) return false;
                    if (!guestPointed) Require(Vector3.Distance(v2Marker.transform.position, v2Hit) < .0005f, "V2 remote marker missed the physical mesh.");
                    if (!guestPointed)
                    {
                        var ray = new Ray(latest.transform.TransformPoint(-.03f,.21f,.062f), -latest.transform.up);
                        Require(PressRightWithLeftActive(app, ray, selectCandidate:true), "Guest A must share a point on Latest.");
                        guestPointed = true;
                        return false;
                    }
                    if ((string)(sp["shared_selection"] as JObject)?["participant_id"] != guestId) return false;
                    app.StartCoroutine(RemoteHostCommand("edit", new JObject { ["base_version_id"] = report.middleVersion,
                        ["parameters"] = new JObject { ["lip_height"] = 17 } }));
                    return true;
                case 17:
                    var round = s["rounds"].LastOrDefault(r => (string)r["edit_base_version_id"] == report.middleVersion);
                    if (round == null) return false;
                    Require((string)round["status"] != "failed", "Manual V3 fixture build failed: " + round["error"]);
                    if ((string)round["status"] != "reviewable") return false;
                    var row = round["candidates"].Single();
                    report.finalVersion = (string)row["version_id"];
                    guestRound = (string)round["round_id"];
                    app.StartCoroutine(RemoteHostCommand("decide", new JObject { ["round_id"] = guestRound,
                        ["revision"] = round["revision"], ["candidate_id"] = row["candidate_id"], ["outcome"] = "commit",
                        ["expected_committed_version_id"] = report.middleVersion, ["expected_adopted_revision"] = s["adopted_revision"] }));
                    return true;
                case 18:
                    if (latest == null || Version(latest) != report.finalVersion) return false;
                    RequireSamePhysicalPose(latest.transform, hostLatestPaper, "Guest V3 after remote commit");
                    CheckPaperLatest(latest);
                    CheckPhysicalScale(s, latest);
                    return true;
                case 19: JoinAsNewViewer(app, 2); return true;
                case 20: return participant != guestId;
                case 23:
                    if (latest == null || Version(latest) != report.finalVersion || !PaperState(app, "PaperRenderable")) return false;
                    RequireSamePhysicalPose(latest.transform, hostLatestPaper, "Late-joining V3");
                    CheckPaperLatest(latest);
                    CheckPhysicalScale(s, latest);
                    report.paperLateJoin = true;
                    AppType.GetMethod("InvalidateRegisteredPoses", AppMembers).Invoke(app, null);
                    return true;
                case 24:
                    if (latest != null || GameObject.Find("CandidateContainer") != null || GameObject.Find("SharedPaperHit") != null) return false;
                    report.paperLostHidden = true;
                    if (!PaperState(app, "A4PointSampling"))
                        AppType.GetMethod("BeginA4Alignment", AppMembers).Invoke(app, null);
                    SetGuestTrackingOrigin(189f, new Vector3(-.2f, .12f, -.08f));
                    guestCorner = 0;
                    return true;
                case 26:
                    if (latest == null || Version(latest) != report.finalVersion || !PaperState(app, "PaperRenderable")) return false;
                    RequireSamePhysicalPose(latest.transform, hostLatestPaper, "Recovered V3");
                    CheckPaperLatest(latest);
                    CheckPhysicalScale(s, latest);
                    report.paperRealigned = true;
                    hostToken = null;
                    Finish(null);
                    return true;
                default: throw new InvalidOperationException("Unknown paper smoke step " + step);
            }
        }
    }
}
