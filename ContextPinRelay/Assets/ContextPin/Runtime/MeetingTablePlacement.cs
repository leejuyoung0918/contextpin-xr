using System;
using System.Threading.Tasks;
using Meta.XR.MRUtilityKit;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        Pose reviewTable;
        Vector2 reviewTableSize;
        MRUKAnchor reviewDeskAnchor;
        bool tableRegistered, tableManual, tableLoadAttempted, tableAreaConfirmed, tableA4Measured;
        int tableSample, deskLoadAttempts;
        Vector3 tableLeft, tableRight;
        double nextDeskCheck;
        Task<MRUK.LoadDeviceResult> roomLoad;
        MRUK.LoadDeviceResult? deskLoadResult;
        string tableFeedback;
        const string DeskTooSmall = "표시할 여유 공간이 부족합니다. 더 넓은 빈 상판을 지정하세요.";
        const int MaxDeskLoadAttempts = 3;
        bool HasReviewTable => tableRegistered;
        bool ReviewTableSampling => tableSample != 0;
        bool CanRequestDeskSetup
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return SpatialAssembly && !HasReviewTable && !ReviewTableSampling &&
                    !DeskNeedsPermission && roomLoad == null;
#else
                return false;
#endif
            }
        }
        static bool DeskNeedsPermission
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return !Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission);
#else
                return false;
#endif
            }
        }

        void RequestDeskAccess()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!DeskNeedsPermission) return;
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ =>
            {
                if (!this) return;
                tableLoadAttempted = false;
                deskLoadAttempts = 0;
                deskLoadResult = null;
                tableFeedback = null;
                Draw();
            };
            callbacks.PermissionDenied += _ => { if (this) Draw(); };
            Permission.RequestUserPermission(OVRPermissionsRequester.ScenePermission, callbacks);
#endif
        }

        void RequestDeskSetup()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!CanRequestDeskSetup) return;
            var mruk = GetMeetingMruk();
            roomLoad = SetupAndReloadDeskRoom(mruk);
            ReportDeskHint("Quest에서 현재 방을 스캔하고 책상을 등록하세요.");
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        static async Task<MRUK.LoadDeviceResult> SetupAndReloadDeskRoom(MRUK mruk)
        {
            if (!await OVRScene.RequestSpaceSetup() || !mruk) return MRUK.LoadDeviceResult.Failure;
            return await mruk.LoadSceneFromDevice(requestSceneCaptureIfNoDataFound: false, sceneModel: MRUK.SceneModel.V1);
        }
#endif

        string TablePlacementHint => tableFeedback ?? (HasReviewTable
            ? (tableA4Measured ? "직접 측정한 A4 영역" : tableAreaConfirmed ? "직접 확인한 비교 영역" : tableManual ? "직접 지정한 책상" : "Quest에서 찾은 책상")
            : DeskNeedsPermission ? "자동 찾기는 공간 권한이 필요합니다." : "가까운 책상을 바라보세요. 직접 지정할 수도 있습니다.");

        MRUK GetMeetingMruk()
        {
            if (MRUK.Instance != null) return MRUK.Instance;
            var host = new GameObject("ContextPin Room and Marker Tracking");
            host.SetActive(false);
            host.transform.SetParent(transform, false);
            var mruk = host.AddComponent<MRUK>();
            mruk.SceneSettings = new MRUK.MRUKSettings { LoadSceneOnStartup = false };
            mruk.EnableWorldLock = false;
            host.SetActive(true);
            return mruk;
        }

        void UpdateReviewTable()
        {
            if (!SpatialAssembly || A4PointSampling || A4PreviewActive) return;
            if (!AssemblyHeadTracked())
            {
                if (tableRegistered || ReviewTableSampling) { ClearReviewTable(); Draw(); }
                return;
            }
            if (ReviewTableSampling || (tableRegistered && tableManual)) return;
            var now = Time.unscaledTimeAsDouble;
            if (now < nextDeskCheck) return;
            nextDeskCheck = now + .4;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission)) return;
#endif
            if (MRUK.Instance == null) { GetMeetingMruk(); return; }
            var mruk = MRUK.Instance;
            if (roomLoad != null)
            {
                if (!roomLoad.IsCompleted) return;
                deskLoadResult = roomLoad.IsCompletedSuccessfully ? roomLoad.Result : MRUK.LoadDeviceResult.Failure;
                if (roomLoad.IsFaulted) _ = roomLoad.Exception;
                roomLoad = null;
                tableLoadAttempted = true;
                Debug.Log($"ContextPin desk query: {deskLoadResult}");
                if (DeskLoadCanRetry(deskLoadResult.Value) && deskLoadAttempts < MaxDeskLoadAttempts)
                {
                    tableLoadAttempted = false;
                    nextDeskCheck = now + 1;
                    ReportDeskHint("공간 정보를 다시 확인하고 있습니다.");
                    return;
                }
            }
            if (!tableLoadAttempted && mruk.Rooms.Count == 0 && roomLoad == null)
            {
                tableLoadAttempted = true;
                deskLoadAttempts++;
                roomLoad = mruk.LoadSceneFromDevice(requestSceneCaptureIfNoDataFound: false, sceneModel: MRUK.SceneModel.V1);
                ReportDeskHint("저장된 공간 정보를 확인하고 있습니다.");
                return;
            }
            var room = mruk.Rooms.Count == 0 ? null : mruk.GetCurrentRoom();
            if (tableRegistered)
            {
                if (room != null && reviewDeskAnchor != null && room.Anchors.Contains(reviewDeskAnchor) &&
                    TryReadDeskTop(reviewDeskAnchor, out var tracked, out var size))
                {
                    // Keep the same left/right orientation for a moving observer.
                    if (Vector3.Dot(tracked.rotation * Vector3.right, reviewTable.rotation * Vector3.right) < 0)
                        tracked.rotation *= Quaternion.Euler(0, 180, 0);
                    reviewTable = tracked;
                    reviewTableSize = size;
                    return;
                }
                ClearReviewTable();
            }
            if (room == null || view == null)
            {
                if (mruk.Rooms.Count > 0) ReportDeskHint("현재 위치에 해당하는 방을 찾지 못했습니다.");
                else if (deskLoadResult == MRUK.LoadDeviceResult.NoRoomsFound)
                    ReportDeskHint("등록된 방이 없습니다. 스캔 또는 직접 지정");
                else if (tableLoadAttempted)
                    ReportDeskHint("공간을 읽지 못했습니다. 스캔 또는 직접 지정");
                return;
            }
            MRUKAnchor closest = null;
            Pose best = default;
            Vector2 bestSize = default;
            float nearest = 2f;
            bool hasTableLabel = false, hasUsableTop = false;
            foreach (var anchor in room.Anchors)
            {
                if (!anchor || !anchor.HasAnyLabel(MRUKAnchor.SceneLabels.TABLE)) continue;
                hasTableLabel = true;
                if (!TryReadDeskTop(anchor, out var top, out var size)) continue;
                hasUsableTop = true;
                var delta = top.position - view.transform.position;
                if (Vector3.Dot(view.transform.forward, delta) <= 0 || delta.magnitude >= nearest) continue;
                nearest = delta.magnitude;
                closest = anchor;
                best = top;
                bestSize = size;
            }
            if (closest != null && TryUseReviewTable(best, bestSize, false)) reviewDeskAnchor = closest;
            else if (!hasTableLabel) ReportDeskHint("이 방에 책상으로 등록된 가구가 없습니다.");
            else if (!hasUsableTop) ReportDeskHint("책상 상판을 읽지 못했습니다. 직접 지정");
            else ReportDeskHint("가까운 등록 책상을 바라보세요.");
        }

        static bool DeskLoadCanRetry(MRUK.LoadDeviceResult result) =>
            result == MRUK.LoadDeviceResult.DiscoveryOngoing || result == MRUK.LoadDeviceResult.NotInitialized ||
            result == MRUK.LoadDeviceResult.FailureInsufficientView || result == MRUK.LoadDeviceResult.FailureRateLimited;

        void ReportDeskHint(string hint)
        {
            if (tableFeedback == hint) return;
            tableFeedback = hint;
            Draw();
        }

        // MRUK furniture volumes can use any local axis as the surface normal.
        static bool TryReadDeskTop(MRUKAnchor anchor, out Pose pose, out Vector2 size)
        {
            pose = default;
            size = default;
            if (!anchor || !anchor.HasAnyLabel(MRUKAnchor.SceneLabels.TABLE)) return false;
            var t = anchor.transform;
            Vector3 center, across, normal;
            if (anchor.VolumeBounds is Bounds box)
            {
                int top = 0;
                float alignment = 0;
                for (int i = 0; i < 3; i++)
                {
                    float vertical = Mathf.Abs(Vector3.Dot(t.TransformDirection(DeskAxis(i)), Vector3.up));
                    if (vertical > alignment) { top = i; alignment = vertical; }
                }
                if (alignment < .995f) return false;
                var axis = DeskAxis(top);
                var sign = Mathf.Sign(Vector3.Dot(t.TransformDirection(axis), Vector3.up));
                normal = t.TransformDirection(axis * sign);
                center = t.TransformPoint(box.center + axis * (box.extents[top] * sign));
                int width = (top + 1) % 3, depth = (top + 2) % 3;
                if (box.size[width] < box.size[depth]) (width, depth) = (depth, width);
                across = t.TransformVector(DeskAxis(width) * box.size[width]);
                size = new Vector2(across.magnitude, t.TransformVector(DeskAxis(depth) * box.size[depth]).magnitude);
            }
            else if (anchor.PlaneRect is Rect rect && Mathf.Abs(t.forward.y) >= .995f)
            {
                normal = t.forward * Mathf.Sign(t.forward.y);
                center = t.TransformPoint(rect.center);
                across = t.TransformVector(Vector3.right * rect.width);
                size = new Vector2(across.magnitude, t.TransformVector(Vector3.up * rect.height).magnitude);
                // Nonrectangular boundaries need a measured clear rectangle.
                if (anchor.PlaneBoundary2D != null && anchor.PlaneBoundary2D.Count > 0)
                    for (int x = -1; x <= 1; x += 2)
                        for (int y = -1; y <= 1; y += 2)
                            if (!anchor.IsPositionInBoundary(rect.center + Vector2.Scale(rect.size * .495f, new Vector2(x,y)))) return false;
            }
            else return false;
            pose = new Pose(center, Quaternion.LookRotation(Vector3.Cross(across, normal), normal));
            return size.x >= .20f && size.y >= .15f;
        }

        static Vector3 DeskAxis(int i) => i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward;

        void BeginTablePlacement()
        {
            if (!SpatialAssembly) return;
            tableSample = 1;
            tableA4Measured = tableAreaConfirmed = false;
            tableRegistered = false;
            reviewDeskAnchor = null;
            tableFeedback = "1/3 · 표시점을 빈 상판 앞왼쪽에 맞추고 A";
            PositionMeetingModel();
            Draw();
        }

        void CancelTablePlacement()
        {
            ClearReviewTable();
            Draw();
        }

        void RecordTablePoint(Vector3 point)
        {
            if (tableSample == 1)
            {
                tableLeft = point;
                tableSample = 2;
                tableFeedback = "2/3 · 같은 높이의 앞오른쪽에서 A";
            }
            else if (tableSample == 2)
            {
                if (Mathf.Abs(point.y - tableLeft.y) > .018f || Vector3.ProjectOnPlane(point - tableLeft, Vector3.up).magnitude < .20f)
                    tableFeedback = "앞왼쪽과 같은 높이에서 오른쪽으로 20cm 이상 짚으세요.";
                else
                {
                    tableRight = point;
                    tableSample = 3;
                    tableFeedback = "3/3 · 앞오른쪽 바로 뒤, 상판 뒤오른쪽에서 A";
                }
            }
            else if (tableSample == 3)
            {
                var edge = Vector3.ProjectOnPlane(tableRight - tableLeft, Vector3.up);
                var right = edge.normalized;
                var forward = Vector3.Cross(right, Vector3.up);
                var side = point - tableRight;
                float depth = Vector3.Dot(side, forward);
                if (Mathf.Abs(point.y - tableLeft.y) > .018f || Mathf.Abs(point.y - tableRight.y) > .018f ||
                    depth < .15f || Mathf.Abs(Vector3.Dot(side, right)) > Mathf.Min(.05f, depth * .18f))
                    tableFeedback = "같은 높이에서 앞오른쪽 뒤로 15cm 이상, 직각으로 짚으세요.";
                else
                {
                    var center = tableLeft + (edge + depth * forward) * .5f;
                    center.y = (tableLeft.y + tableRight.y + point.y) / 3f;
                    TryUseReviewTable(new Pose(center, Quaternion.LookRotation(forward)), new Vector2(edge.magnitude, depth), true);
                    return;
                }
            }
            Draw();
        }

        bool TryUseReviewTable(Pose tableTop, Vector2 size, bool manual) => StoreReviewTable(tableTop, size, manual, false);

        bool StoreReviewTable(Pose tableTop, Vector2 size, bool manual, bool confirmedArea, bool a4 = false)
        {
            float upDot = Vector3.Dot(tableTop.rotation * Vector3.up, Vector3.up);
            if (!SpatialAssembly || !float.IsFinite(tableTop.position.sqrMagnitude) || !float.IsFinite(size.sqrMagnitude) ||
                size.x < (a4 ? .15f : .20f) || size.y < .15f || !float.IsFinite(Quaternion.Dot(tableTop.rotation, tableTop.rotation)) ||
                Mathf.Abs(Quaternion.Dot(tableTop.rotation, tableTop.rotation) - 1) > .01f ||
                (a4 ? upDot <= 0 : upDot < .995f)) return false;
            if (!manual && view && Vector3.Dot(tableTop.rotation * Vector3.right, view.transform.right) < 0)
                tableTop.rotation *= Quaternion.Euler(0, 180, 0);
            reviewTable = tableTop;
            reviewTableSize = size;
            tableManual = manual;
            tableAreaConfirmed = confirmedArea;
            tableA4Measured = a4;
            if (confirmedArea || a4) reviewDeskAnchor = null;
            tableRegistered = true;
            tableSample = 0;
            tableFeedback = null;
            Debug.Log("ContextPin desk: " + (a4 ? "measured A4" : confirmedArea ? "user-confirmed comparison area" : manual ? "manual" : "Quest TABLE"));
            if (assemblyCompare && !CanCompareModels && !TabletopPointSampling && !TabletopPreviewActive && !A4PointSampling && !A4PreviewActive) StartLocalCandidatePreview();
            PositionMeetingModel();
            Draw();
            return true;
        }

        // Meshes are already in metres in CAD-local coordinates. Only the display
        // transform moves. The physical overlay keeps its original mounting transform.
        bool PlaceReviewModel(MeetingModelSlot slot, int index, int count)
        {
            Vector3 local = default;
            bool fits = false;
            if (tableRegistered)
            {
                // Reserve A/B/C next to Latest so opening a candidate does not move Latest.
                if (ExplicitDesignMeeting) fits = TryDeskCell(slot, index, 4, out local);
                if (!fits) fits = TryDeskCell(slot, index, count, out local);
                if (!fits && ExplicitDesignMeeting && slot == meetingPreview) fits = TryDeskCell(slot, 0, 1, out local);
            }
            if (slot.Root.activeSelf != fits) slot.Root.SetActive(fits);
            if (!fits)
            {
                if (tableRegistered) tableFeedback = DeskTooSmall;
                return false;
            }
            slot.Root.transform.SetPositionAndRotation(reviewTable.position + reviewTable.rotation * local, reviewTable.rotation);
            slot.Root.transform.localScale = Vector3.one;
            return true;
        }

        bool TryDeskCell(MeetingModelSlot slot, int index, int count, out Vector3 position)
        {
            position = default;
            // All slots in one comparison use the same cells, even with different CAD origins.
            Vector3 span = Vector3.Max(meetingAssetBounds.size, physicalOriginal.Bounds.size);
            span = Vector3.Max(span, assemblyComparison.Bounds.size);
            foreach (var candidate in cableComparisons) span = Vector3.Max(span, candidate.Bounds.size);
            foreach (var example in groovePreviews) span = Vector3.Max(span, example.Bounds.size);
            if (A4ReviewSurfaceActive) return TryA4Cell(slot.Bounds, index, span, out position);
            float pitchX = Mathf.Max(.15f, span.x + .025f);
            float pitchZ = Mathf.Max(.10f, span.z + .025f);
            var area = new Rect(-reviewTableSize * .5f, reviewTableSize);
            if (assemblyAlignment == AssemblyAlignment.Confirmed)
            {
                var obstacle = OriginalFootprintOnTable(.025f);
                if (area.Overlaps(obstacle))
                {
                    var whole = area;
                    area = default;
                    ConsiderDeskRegion(ref area, UnityEngine.Rect.MinMaxRect(whole.xMin, whole.yMin, obstacle.xMin, whole.yMax), span, count, pitchX, pitchZ);
                    ConsiderDeskRegion(ref area, UnityEngine.Rect.MinMaxRect(obstacle.xMax, whole.yMin, whole.xMax, whole.yMax), span, count, pitchX, pitchZ);
                    ConsiderDeskRegion(ref area, UnityEngine.Rect.MinMaxRect(whole.xMin, whole.yMin, whole.xMax, obstacle.yMin), span, count, pitchX, pitchZ);
                    ConsiderDeskRegion(ref area, UnityEngine.Rect.MinMaxRect(whole.xMin, obstacle.yMax, whole.xMax, whole.yMax), span, count, pitchX, pitchZ);
                }
            }
            int columns = DeskColumns(area, span, count, pitchX, pitchZ);
            if (columns == 0 || index < 0 || index >= count) return false;
            int rows = (count + columns - 1) / columns;
            var b = slot.Bounds;
            position = new Vector3(area.center.x + (index % columns - (columns - 1) * .5f) * pitchX - b.center.x,
                -b.min.y, area.center.y + (index / columns - (rows - 1) * .5f) * pitchZ - b.center.z);
            return true;
        }

        Rect OriginalFootprintOnTable(float clearance)
        {
            var bounds = meetingLoadedVersion == AssemblyOriginalId ? meetingAssetBounds : physicalOriginal.Bounds;
            if (A4ReviewSurfaceActive) bounds = sessionOriginalBounds ?? bounds;
            var origin = A4ReviewSurfaceActive && OffsetPaperGallery ? reviewTable.position : WorkspaceOrigin;
            return FootprintOnTable(bounds, origin, WorkspaceRotation, clearance);
        }

        Rect FootprintOnTable(Bounds bounds, Vector3 origin, Quaternion rotation, float clearance)
            => SurfaceFootprint(reviewTable, bounds, origin, rotation, clearance);

        static Rect SurfaceFootprint(Pose surface, Bounds bounds, Vector3 origin, Quaternion rotation, float clearance)
        {
            var toTable = Quaternion.Inverse(surface.rotation);
            var relative = toTable * rotation;
            var center = toTable * (origin - surface.position) + relative * bounds.center;
            var extent = bounds.extents;
            var x = relative * (Vector3.right * extent.x);
            var y = relative * (Vector3.up * extent.y);
            var z = relative * (Vector3.forward * extent.z);
            float dx = Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x) + clearance;
            float dz = Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z) + clearance;
            return new Rect(center.x - dx, center.z - dz, dx * 2, dz * 2);
        }

        bool TryA4Cell(Bounds bounds, int index, Vector3 span, out Vector3 position)
        {
            position = default;
            if (index < 0 || index >= 4) return false;
            // Latest occupies front-right, A front-left, B back-left and C back-right.
            const float clearance = .005f;
            var quadrant = reviewTableSize * .5f;
            if (span.x + clearance * 2 > quadrant.x || span.z + clearance * 2 > quadrant.y) return false;
            float x = (index == 0 || index == 3 ? .5f : -.5f) * quadrant.x;
            float z = (index < 2 ? -.5f : .5f) * quadrant.y;
            var footprint = new Rect(x - span.x * .5f, z - span.z * .5f, span.x, span.z);
            if (footprint.Overlaps(OriginalFootprintOnTable(clearance))) return false;
            if (index != 0 && latestAnchor != null &&
                footprint.Overlaps(FootprintOnTable(meetingAssetBounds, latestAnchor.position, latestAnchor.rotation, clearance))) return false;
            position = new Vector3(x - bounds.center.x, -bounds.min.y, z - bounds.center.z);
            return true;
        }

        static int DeskColumns(Rect area, Vector3 span, int count, float pitchX, float pitchZ)
        {
            int cols = Mathf.Min(count, 1 + Mathf.FloorToInt((area.width - span.x - .012f) / pitchX));
            return cols > 0 && ((count - 1) / cols) * pitchZ + span.z + .012f <= area.height ? cols : 0;
        }

        static void ConsiderDeskRegion(ref Rect best, Rect region, Vector3 span, int count, float pitchX, float pitchZ)
        {
            if (region.width > 0 && region.height > 0 && region.width * region.height > best.width * best.height &&
                DeskColumns(region, span, count, pitchX, pitchZ) > 0) best = region;
        }

        void ClearReviewTable()
        {
            tableRegistered = tableManual = tableLoadAttempted = tableAreaConfirmed = tableA4Measured = false;
            tableSample = 0;
            deskLoadAttempts = 0;
            deskLoadResult = null;
            reviewDeskAnchor = null;
            tableFeedback = null;
            nextDeskCheck = Time.unscaledTimeAsDouble + .5;
        }
    }
}
