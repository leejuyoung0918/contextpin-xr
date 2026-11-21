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
        static bool VerifyReviewModels(RelayApp app, JObject state, GameObject latest, int index)
        {
            var set = state["candidate_sets"]?[captureId];
            string status = (string)set?["status"];
            if (status is null or "generating" or "building") return false;
            var rows = set["candidates"] as JArray;
            Require(status == "ready" && rows?.Count == 2, $"Round {index + 1} must offer two real files: {status} · {set["reason"]}");
            Require((string)set["base_version_id"] == Version(latest) &&
                    (string)set["proposal_provenance"]?["source"] == "provider" &&
                    (int)set["proposal_provenance"]["images_sent"] == 2, "The selected base and both actual image inputs must reach the AI.");
            var error = (string)AppType.GetField("bundleReviewError", AppMembers).GetValue(app);
            Require(string.IsNullOrEmpty(error), error);
            var container = GameObject.Find("CandidateContainer");
            if (container == null) return false;
            var occupied = new List<Bounds> { PhysicalProductBounds(state) };
            void CheckClearance(Bounds bounds)
            {
                if (occupied.Count > 0)
                {
                    var gap = occupied.Min(o => Mathf.Max(o.min.x - bounds.max.x, bounds.min.x - o.max.x,
                        o.min.z - bounds.max.z, bounds.min.z - o.max.z));
                    Require(gap >= .0098f, $"Insufficient space between visible model footprints: {gap * 1000:F2} mm.");
                    report.minimumPaperClearanceMm = report.minimumPaperClearanceMm == 0 ? gap * 1000 :
                        Mathf.Min(report.minimumPaperClearanceMm, gap * 1000);
                }
                occupied.Add(bounds);
            }
            CheckClearance(ModelBoundsInPose(latest, measuredPaper));
            foreach (var row in rows)
            {
                string key = (string)row["candidate_key"];
                var root = container.transform.Find("Candidate" + key);
                if (root == null || !root.gameObject.activeInHierarchy) return false;
                Require(Version(root.gameObject) == (string)row["version_id"] &&
                        root.GetComponentInChildren<MeshCollider>() != null, "The selectable mesh must belong to this candidate.");
                CheckPhysicalScale(state, root.gameObject);
                var b = ModelBoundsInPose(root.gameObject, measuredPaper);
                Require(Quaternion.Angle(root.rotation, latest.transform.rotation) < .1f && Vector3.Distance(root.lossyScale, Vector3.one) < .0001f &&
                        b.min.y >= -.0002f && b.min.y < .005f, "Each candidate must be full size, upright and on the tabletop.");
                var slotPosition = new Vector2(b.center.x, b.center.z);
                var expectedCenter = new Vector2(key == "A" ? -.25f : .25f, .25f) * explicitAreaSize;
                if (checkPhoneStand)
                {
                    expectedCenter = new Vector2(key == "A" ? -.14f : .14f, .24f);
                    var pivot = RelativePose(root, measuredPaper).position;
                    slotPosition = new Vector2(pivot.x, pivot.z);
                    Require(Mathf.Abs(pivot.y) < .0005f, "The phone stand CAD pivot must rest on the tabletop.");
                    Require(b.min.z > explicitAreaSize.y * .5f, "The full-size phone previews can sit beyond the far edge of A4.");
                }
                else Require(Mathf.Max(-b.min.x, b.max.x) <= explicitAreaSize.x * .5f &&
                    Mathf.Max(-b.min.z, b.max.z) <= explicitAreaSize.y * .5f, "Cable previews must fit within the paper.");
                Require(Vector2.Distance(slotPosition, expectedCenter) < .001f,
                    $"Candidate {key} must stay at its dedicated slot {expectedCenter} across edits.");
                CheckClearance(b);
                var diff = (JArray)row["parameter_diff"];
                var (parameter, direction) = index == 0 ? ("upper_corner_nx_py", 1) : ("slot_pitch", -1);
                if (checkPhoneStand)
                    (parameter, direction) = index == 0 ? ("viewing_angle", -1) : ("lip_height", 1);
                Require(diff.Count == 1 && (string)diff[0]["parameter_id"] == parameter &&
                    Math.Sign((double)diff[0]["after"] - (double)diff[0]["before"]) == direction,
                    $"Expected a change to {parameter} at the pointed region, got {diff}.");
            }
            Require(rows.Select(c => (string)c["stl"]["sha256"]).Distinct().Count() == 2 &&
                    rows.Select(c => (string)c["model_asset"]["sha256"]).Distinct().Count() == 2, "The two geometries must differ.");
            if (!report.singleCandidateKeptSlot) CheckLimitedGallery(app, occupied[0], occupied[1], rows);
            CheckSideLatest(state, latest, Version(latest), true);
            report.fixedPaperSlots = report.bundleModelsPlaced = report.a4FullSizeGallery = true;
            return true;
        }

        // Exercise the one-valid-candidate case with the actual B mesh from this set.
        static void CheckLimitedGallery(RelayApp app, Bounds product, Bounds latest, JArray rows)
        {
            var candidates = AppType.GetField("bundleCandidates", AppMembers);
            var all = candidates.GetValue(app);
            var slots = (Array)AppType.GetField("bundleSlots", AppMembers).GetValue(app);
            var first = slots.GetValue(0);
            var second = slots.GetValue(1);
            var boundsField = second.GetType().GetField("Bounds");
            var bounds = (Bounds)boundsField.GetValue(second);
            var placement = AppType.GetMethod("TryBundlePositions", AppMembers);
            Rect Footprint(Bounds b) => Rect.MinMaxRect(b.min.x, b.min.z, b.max.x, b.max.z);
            var args = new object[] { Footprint(product), Footprint(latest), Vector3.zero, Vector3.zero };
            try
            {
                candidates.SetValue(app, new[] { (JObject)rows.Single(c => (string)c["candidate_key"] == "B") });
                slots.SetValue(second, 0);
                Require((bool)placement.Invoke(app, args), "B alone must fit.");
                var offset = (Vector3)args[2];
                var expected = checkPhoneStand ? new Vector2(.14f, .24f) : explicitAreaSize * .25f;
                var point = checkPhoneStand ? offset : offset + bounds.center;
                Require(Vector2.Distance(new Vector2(point.x, point.z), expected) < .001f,
                        "B alone must stay in its back-right slot.");
                if (checkPhoneStand)
                {
                    boundsField.SetValue(second, new Bounds(bounds.center + new Vector3(.013f, 0, -.021f), bounds.size));
                    Require((bool)placement.Invoke(app, args) && Vector3.Distance((Vector3)args[2], offset) < .0005f,
                        "An asymmetric edit must leave the CAD base pivot fixed at its registered slot.");
                }
                report.singleCandidateKeptSlot = true;
                boundsField.SetValue(second, new Bounds(bounds.center, new Vector3(1, bounds.size.y, 1)));
                Require(!(bool)placement.Invoke(app, args), "An oversized mesh must be rejected before placement.");
                report.oversizedCandidateRejected = true;
            }
            finally
            {
                boundsField.SetValue(second, bounds);
                slots.SetValue(first, 0);
                candidates.SetValue(app, all);
            }
        }

        static bool ReviewAndCommitStep(RelayApp app, JObject state, GameObject latest)
        {
            int index = step < 20 ? 0 : 1;
            string key = index == 0 ? "B" : "A";
            var set = state["candidate_sets"]?[captureId];
            if (step == 11 || step == 20)
            {
                var root = GameObject.Find("Candidate" + key);
                if (root == null) return false;
                if (TimelineTest && index == 0 && !WaitForReviewSpeech(state)) return false;
                var row = set["candidates"].Single(c => (string)c["candidate_key"] == key);
                var bounds = ModelBoundsInPose(root, measuredPaper);
                // The solid base extends beyond the grooves or backrest.
                var local = new Vector3(bounds.max.x - .005f, bounds.max.y + .025f, bounds.center.z);
                var ray = new Ray(measuredPaper.position + measuredPaper.rotation * local, measuredPaper.rotation * Vector3.down);
                Physics.SyncTransforms();
                Require(Physics.Raycast(ray, out var hit, bounds.size.y + .04f) && hit.transform.IsChildOf(root.transform), "Candidate ray must hit its mesh.");
                Require(PressRightWithLeftActive(app, ray, selectCandidate: true), "The right A edge must select the pointed candidate.");
                Require(Version(latest) == (string)set["base_version_id"], "A local button press cannot replace Latest ahead of commit.");
                report.selectedCandidates[index] = (string)row["candidate_id"];
                report.committedVersions[index] = (string)row["version_id"];
                report.bundleRaySelection = true;
                return true;
            }
            if (step == 12 || step == 21)
            {
                var error = (string)AppType.GetField("bundleReviewError", AppMembers).GetValue(app);
                Require(string.IsNullOrEmpty(error), error);
                var id = report.committedVersions[index];
                if ((string)set["status"] != "committed" || (string)state["adopted_version_id"] != id ||
                    latest == null || Version(latest) != id || GameObject.Find("CandidateContainer") != null) return false;
                var row = set["candidates"].Single(c => (string)c["candidate_id"] == report.selectedCandidates[index]);
                var model = state["versions"].Single(v => (string)v["version_id"] == id);
                var bundle = state["edit_requests"][captureId];
                Require((string)model["design_version"] == (index == 0 ? "V2" : "V3") &&
                        (string)model["parent_version_id"] == (string)set["base_version_id"] &&
                        (string)set["selected_candidate_id"] == (string)row["candidate_id"] &&
                        (string)model["source_request_id"] == captureId && (string)model["reason"] == "not_stated" &&
                        (string)model["evidence"]["selection_id"] == (string)bundle["selection"]["selection_id"] &&
                        (string)model["evidence"]["image_asset_id"] == (string)bundle["visual_evidence"]["image_asset_id"] &&
                        (string)model["assets"]["preview.glb"]["sha256"] == (string)row["model_asset"]["sha256"],
                        "The displayed version must commit the exact reviewed file, provenance and parent.");
                CheckSideLatest(state, latest, id, true);
                if (index == 0) { report.middleVersion = id; report.v2Replaced = true; }
                else
                {
                    var previous = state["versions"].Single(v => (string)v["version_id"] == report.middleVersion);
                    string kept = checkPhoneStand ? "viewing_angle" : "upper_corner_nx_py";
                    var original = state["versions"].First;
                    Require(JToken.DeepEquals(model["parameters"][kept], previous["parameters"][kept]) &&
                            !JToken.DeepEquals(model["parameters"][kept], original["parameters"][kept]),
                            $"V3 must retain the V2 edit to {kept}.");
                    report.finalVersion = id;
                    report.secondEditRound = report.v3Replaced = true;
                }
                report.bundleCommit = true;
                return true;
            }
            if (step == 13) return true;
            if (step == 22) return Click("SpatialSessionAction");
            if (step == 23) return (!TimelineTest || ReadyToEndWithSpeech(state)) && Click("SpatialConfirmEnd");
            if ((string)state["session_status"] == "save_error")
                throw new InvalidOperationException("Server could not finalize the meeting: " + state["result"]);
            if (TimelineTest && (string)state["session_status"] == "finalizing" && ExplicitUi("SpatialSessionStatus") == "회의 정리 중…")
                report.minutesGeneratingShown = true;
            if ((string)state["session_status"] != "completed") return false;
            Require(report.captureFiles.All(File.Exists) && state["decisions"].Count() == 2, "Both requests and decisions must be archived.");
            if (TimelineTest)
            {
                if (step == 24)
                {
                    Require((string)state["result"]["minutes_status"] == "generated", "Check the server minutes export: " + state["result"]);
                    if (ExplicitUi("SpatialSessionStatus") != "회의록 저장 완료") return false;
                    report.minutesSavedShown = true;
                    CheckArchivedTimeline(state);
                    return Click("SpatialMenu");
                }
                if (step == 25) return Click("SpatialResult");
                if (!CheckTimelineMinutes(state)) return false;
            }
            report.savedMeeting = true;
            Finish(null);
            return true;
        }
    }
}
