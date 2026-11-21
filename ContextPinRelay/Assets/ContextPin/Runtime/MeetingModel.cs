using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using GLTFast;
using GLTFast.Loading;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        const int MeetingPreviewLimit = 24 * 1024 * 1024;
        sealed class MeetingModelSlot
        {
            public readonly int CableIndex;
            public readonly string GroovePreviewId;
            public string BundleKey, VerifiedHash;
            public CancellationTokenSource Request;
            public GameObject Root;
            public GameObject Label;
            public GltfImport Import;
            public Material Material;
            public Bounds Bounds;
            public string LoadingVersion, FailedVersion, LoadedVersion, TargetVersion;

            public MeetingModelSlot(int cableIndex = -1, string groovePreviewId = null)
            {
                CableIndex = cableIndex;
                GroovePreviewId = groovePreviewId;
            }
        }

        const string MeetingPivotName = "MeetingPreviewPivot";
        const string ComparisonPivotName = "SpatialComparisonPivot";
        readonly MeetingModelSlot meetingPreview = new MeetingModelSlot();
        readonly MeetingModelSlot physicalOriginal = new MeetingModelSlot();
        readonly MeetingModelSlot assemblyComparison = new MeetingModelSlot();
        readonly MeetingModelSlot[] cableComparisons = { new MeetingModelSlot(0), new MeetingModelSlot(1), new MeetingModelSlot(2) };
        GameObject meetingLighting;
        GameObject meetingModel => meetingPreview.Root;
        Bounds meetingAssetBounds => meetingPreview.Bounds;
        // V1 defines the physical landmark throughout this meeting, including after
        // its mesh has been replaced. These are measured in the importer's Unity axes.
        Bounds? sessionOriginalBounds;
        string meetingLoadingVersion => meetingPreview.LoadingVersion;
        string meetingLoadedVersion => meetingPreview.LoadedVersion;
        string meetingFailedVersion { get => meetingPreview.FailedVersion; set => meetingPreview.FailedVersion = value; }
        MeetingModelSlot SelectedSpatialComparison => cableComparisons.FirstOrDefault(slot =>
            slot.TargetVersion != null && slot.TargetVersion == meetingViewedVersion) ?? assemblyComparison;
        string SpatialComparisonLoadedVersion => SelectedSpatialComparison.LoadedVersion;
        string SpatialComparisonLoadingVersion => SelectedSpatialComparison.LoadingVersion;
        string SpatialComparisonFailedVersion => SelectedSpatialComparison.FailedVersion;
        bool SpatialComparisonModelVisible => SelectedSpatialComparison.Root != null && SelectedSpatialComparison.Root.activeInHierarchy;
        bool IsLocalCandidateVisible(string id) =>
            (assemblyComparison.LoadedVersion == id && assemblyComparison.Root != null && assemblyComparison.Root.activeInHierarchy) ||
            Array.Exists(cableComparisons, slot => slot.LoadedVersion == id && slot.Root != null && slot.Root.activeInHierarchy);
        string meetingPartId;
        bool meetingRealSize, meetingLocalPlacement;
        Vector3 meetingLocalPoint;
        Quaternion meetingLocalRotation;
        float meetingYaw = -28, meetingHeight;
        readonly List<(string id, Renderer renderer)> meetingPartRenderers = new List<(string, Renderer)>();
        static readonly int MeetingColor = Shader.PropertyToID("_Color");

        // The EDGE supplies a verified self-contained GLB. Preview loading does not open any
        // referenced filesystem path or alternate host from a glTF buffer/texture URI.
        sealed class EmbeddedPreviewResources : IDownloadProvider
        {
            public Task<IDownload> Request(Uri url) => Task.FromException<IDownload>(new InvalidDataException("External glTF buffer"));
            public Task<ITextureDownload> RequestTexture(Uri url, bool nonReadable) =>
                Task.FromException<ITextureDownload>(new InvalidDataException("External glTF texture"));
        }

        void QueueMeetingModel(JToken version)
        {
            var id = version?.Value<string>("version_id");
            bool paperWorkspace = A4PointSampling || A4PoseApplied;
            if (SpatialAssembly && !paperWorkspace && id != null && id != meetingLoadedVersion)
            {
                var loaded = assemblyComparison.LoadedVersion == id ? assemblyComparison :
                    cableComparisons.FirstOrDefault(slot => slot.LoadedVersion == id);
                if (loaded != null) PromoteSpatialComparison(loaded);
            }
            // On paper, a commit loads the server's exact adopted GLB. The current
            // Latest stays visible while its replacement is downloaded and verified.
            if (VerifiedCommitDescriptor(version)) QueueMeetingModelSlot(meetingPreview, version);
            bool needOriginal = paperWorkspace ? !sessionOriginalBounds.HasValue && (A4PointSampling || A4PreviewActive || CandidateGenerationMeeting)
                : ExplicitDesignMeeting || AssemblyAdoptedId != AssemblyOriginalId;
            QueueComparisonSlot(physicalOriginal, SpatialAssembly && !(SharedPaperMeeting && !MeetingHost) && needOriginal ? MeetingVersion(AssemblyOriginalId) : null);

            bool group = CableComparisonGroup;
            var comparison = group ? null : RequestedSpatialComparison();
            QueueComparisonSlot(assemblyComparison, comparison);
            var candidates = group ? Values(SpatialRound(), "candidates").Take(cableComparisons.Length).ToArray() : Array.Empty<JToken>();
            for (int i = 0; i < cableComparisons.Length; i++)
                QueueComparisonSlot(cableComparisons[i], i < candidates.Length ? ValidCableCandidate(candidates[i]) : null);
            PositionMeetingModel();
            QueueBundleReview();
        }

        bool CableComparisonGroup => !CollaborativeDraftVisible && SpatialViewingShared && CanCompareModels &&
            meetingViewedVersion != AssemblyOriginalId;

        JObject ValidCableCandidate(JToken candidate) => TextValue(candidate, "status") == "valid"
            ? MeetingVersion(TextValue(candidate, "version_id")) : null;

        void QueueComparisonSlot(MeetingModelSlot slot, JToken version)
        {
            if (version != null) { QueueMeetingModelSlot(slot, version); return; }
            CancelMeetingModel(slot);
            ReleaseMeetingInstance(slot);
            slot.TargetVersion = null;
        }

        JObject RequestedSpatialComparison() => SpatialComparisonVisible && CanCompareModels
            ? MeetingVersion(meetingViewedVersion) : null;

        void RetrySpatialComparison()
        {
            SelectedSpatialComparison.FailedVersion = null;
            QueueMeetingModel(RequestedMeetingVersion());
        }

        void PromoteSpatialComparison(MeetingModelSlot from)
        {
            CancelMeetingModel(meetingPreview);
            ReleaseMeetingInstance(meetingPreview);
            meetingPreview.Root = from.Root;
            meetingPreview.Import = from.Import;
            meetingPreview.Material = from.Material;
            meetingPreview.Bounds = from.Bounds;
            meetingPreview.LoadedVersion = from.LoadedVersion;
            meetingPreview.Root.name = MeetingPivotName;
            meetingPreview.Material.color = AssemblyReferenceColor;
            if (from.Label != null)
            {
                from.Label.SetActive(false);
                Destroy(from.Label);
            }
            from.Root = from.Label = null;
            from.Import = null;
            from.Material = null;
            from.LoadedVersion = null;
            meetingPartId = null;
            var partIds = MeetingPartIds();
            foreach (var filter in meetingPreview.Root.GetComponentsInChildren<MeshFilter>(true))
                AddMeetingPart(filter, partIds, meetingPreview.LoadedVersion);
        }

        void QueueMeetingModelSlot(MeetingModelSlot slot, JToken version)
        {
            var id = version?.Value<string>("version_id");
            slot.TargetVersion = id;
            if (id == slot.LoadingVersion) return;
            CancelMeetingModel(slot);
            if (string.IsNullOrEmpty(id) || id == slot.LoadedVersion || id == slot.FailedVersion) return;
            slot.Request = new CancellationTokenSource();
            slot.LoadingVersion = id;
            StartCoroutine(DownloadMeetingModel(meetingTransport, slot, version, id, slot.Request));
        }

        static void CancelMeetingModel(MeetingModelSlot slot)
        {
            slot.Request?.Cancel();
            slot.Request = null;
            slot.LoadingVersion = null;
        }

        IEnumerator DownloadMeetingModel(MeetingTransport transport, MeetingModelSlot slot, JToken version, string id, CancellationTokenSource request)
        {
            var asset = version["assets"]?["preview.glb"];
            var hash = asset?.Value<string>("sha256") ?? version.Value<string>("preview_sha256");
            var path = version.Value<string>("preview_url");
            if (string.IsNullOrEmpty(path) && asset?.Value<string>("asset_id") is string assetId)
                path = "/v1/assets/" + Uri.EscapeDataString(assetId);
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(hash) || hash.Length != 64)
            {
                MeetingLoadFailure(slot, id, request, "미리보기 파일 주소 또는 해시가 없습니다.");
                yield break;
            }
            byte[] bytes = null;
            string error = null;
            yield return transport.Download(path, MeetingPreviewLimit, (data, failure) => { bytes = data; error = failure; });
            if (request.IsCancellationRequested) { request.Dispose(); yield break; }
            if (bytes == null) { MeetingLoadFailure(slot, id, request, error ?? "미리보기 연결이 끊겼습니다."); yield break; }
            var registeredSize = ReadMeetingVector(asset?["bounds_m"]?["size"] ?? version["bounds_m"]?["size"]);
            var partIds = MeetingPartIds();
            _ = ImportMeetingModel(slot, bytes, hash, registeredSize, partIds, id, request);
        }

        HashSet<string> MeetingPartIds() => new HashSet<string>(Values(meetingSnapshot?["profile"], "semantic_parts")
            .Select(part => part.Value<string>("part_id")), StringComparer.Ordinal);

        static Vector3? ReadMeetingVector(JToken token)
        {
            if (token is not JArray array || array.Count != 3 || array.Any(v => v.Type != JTokenType.Float && v.Type != JTokenType.Integer)) return null;
            var vector = new Vector3(array[0].Value<float>(), array[1].Value<float>(), array[2].Value<float>());
            return float.IsFinite(vector.x) && float.IsFinite(vector.y) && float.IsFinite(vector.z) ? vector : null;
        }

        async Task ImportMeetingModel(MeetingModelSlot slot, byte[] bytes, string expectedHash, Vector3? registeredSize,
            HashSet<string> partIds, string id, CancellationTokenSource request)
        {
            GameObject instance = null;
            GameObject pivot = null;
            GltfImport loader = null;
            Material material = null;
            try
            {
                if (bytes.Length < 20 || bytes[0] != 'g' || bytes[1] != 'l' || bytes[2] != 'T' || bytes[3] != 'F')
                    throw new InvalidDataException("GLB 파일 형식이 올바르지 않습니다.");
                string hash = await Task.Run(() =>
                {
                    using var sha = SHA256.Create();
                    return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
                }, request.Token);
                if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("다운로드한 GLB가 등록된 해시와 다릅니다.");
                if (request.IsCancellationRequested) return;
                loader = new GltfImport(downloadProvider: new EmbeddedPreviewResources());
                if (!await loader.Load(bytes, importSettings: new ImportSettings { AnimationMethod = AnimationMethod.None },
                    cancellationToken: request.Token)) throw new InvalidDataException("GLB 가져오기에 실패했습니다.");
                instance = new GameObject("MeetingModel-" + id);
                instance.SetActive(false);
                if (!await loader.InstantiateMainSceneAsync(instance.transform, request.Token))
                    throw new InvalidDataException("GLB 프리뷰 장면을 만들 수 없습니다.");
                var filters = instance.GetComponentsInChildren<MeshFilter>(true)
                    .Where(filter => filter.sharedMesh != null && filter.sharedMesh.vertexCount > 0).ToArray();
                if (filters.Length == 0) throw new InvalidDataException("GLB에 표시 가능한 기하가 없습니다.");
                var bounds = MeetingGeometryBounds(filters);
                if (registeredSize.HasValue && (bounds.size - registeredSize.Value).sqrMagnitude > .0005f * .0005f)
                    throw new InvalidDataException("GLB의 실제 크기가 등록된 미터 단위 치수와 다릅니다.");
                var comparison = slot != meetingPreview;
                bool groovePreview = slot.GroovePreviewId != null;
                var requested = slot.BundleKey != null ? slot.TargetVersion == id : groovePreview ? groovePreviewGallery != null : slot == physicalOriginal ? id == AssemblyOriginalId && SpatialAssembly :
                    slot.CableIndex >= 0 ? CableComparisonGroup && slot.TargetVersion == id :
                    (comparison ? RequestedSpatialComparison() : RequestedMeetingVersion())?.Value<string>("version_id") == id;
                if (request.IsCancellationRequested || slot.Request != request ||
                    !requested) return;
                if (IsAssemblyMeeting) material = CreateAssemblyMeshMaterial(filters, comparison);
                pivot = new GameObject(slot.BundleKey != null ? "Candidate" + slot.BundleKey : groovePreview ? "GroovePreview-" + id : slot == physicalOriginal ? "PhysicalOriginalPivot" :
                    slot.CableIndex >= 0 ? $"CableComparison-{(char)('A' + slot.CableIndex)}" : comparison ? ComparisonPivotName : MeetingPivotName);
                pivot.SetActive(false);
                instance.transform.SetParent(pivot.transform, false);
                if (!IsAssemblyMeeting) instance.transform.localPosition = -bounds.center;
                instance.SetActive(true);
                if (slot.BundleKey != null)
                {
                    foreach (var filter in filters)
                    {
                        if (!filter.TryGetComponent<MeshCollider>(out var collider))
                            collider = filter.gameObject.AddComponent<MeshCollider>();
                        collider.sharedMesh = filter.sharedMesh;
                    }
                }
                else if (!groovePreview)
                    foreach (var filter in filters) AddMeetingPart(filter, partIds, id);
                if (slot == meetingPreview && CandidateGenerationMeeting && A4ReviewSurfaceActive && latestAnchor != null)
                    pivot.transform.SetParent(latestAnchor, false);
                ReleaseMeetingInstance(slot);
                slot.Root = pivot;
                slot.Import = loader;
                slot.Material = material;
                slot.Bounds = bounds;
                if (IsAssemblyMeeting && id == AssemblyOriginalId && float.IsFinite(bounds.center.sqrMagnitude) &&
                    float.IsFinite(bounds.size.sqrMagnitude) && bounds.size.x > 0 && bounds.size.z > 0)
                    sessionOriginalBounds = bounds;
                slot.LoadedVersion = id;
                slot.VerifiedHash = expectedHash;
                slot.LoadingVersion = slot.FailedVersion = null;
                slot.Request = null;
                pivot = null;
                instance = null;
                loader = null;
                material = null;
                if (slot.BundleKey != null)
                {
                    slot.Root.transform.SetParent(bundleContainer.transform, false);
                    CreateModelLabel(slot, slot.BundleKey);
                    slot.Material.color = BundleColor(slot.BundleKey);
                    PositionBundleCandidates();
                    Physics.SyncTransforms();
                    Draw();
                    return;
                }
                if (groovePreview)
                {
                    AttachGroovePreview(slot);
                    PositionGroovePreviews();
                    Draw();
                    return;
                }
                if (!comparison) meetingPartId = null;
                if (!comparison && !SpatialAssembly)
                    foreach (var filter in filters)
                    {
                        var hit = filter.GetComponent<RelayHit>();
                        var renderer = filter.GetComponent<MeshRenderer>();
                        if (hit != null && renderer != null) meetingPartRenderers.Add((hit.role, renderer));
                    }
                if (slot.CableIndex >= 0) CreateCableComparisonLabel(slot);
                if (!IsAssemblyMeeting) slot.Root.SetActive(true);
                PrepareMeetingLighting();
                PositionMeetingModel();
                // A late join can finish calibration before the Latest mesh is ready.
                if (slot == meetingPreview && SharedPaperMeeting) QueueBundleReview();
                Physics.SyncTransforms();
                ShowMeetingSelection();
                Draw();
                if (!comparison || slot == SelectedSpatialComparison || (UsingLocalCandidatePreview && slot.CableIndex >= 0))
                    ReportMeetingView();
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception error)
            {
                Debug.LogException(error);
                if (!request.IsCancellationRequested) MeetingLoadFailure(slot, id, request, "모델 표시 실패 · " + error.Message, false);
            }
            finally
            {
                if (pivot != null) Destroy(pivot);
                else if (instance != null) Destroy(instance);
                if (material != null) Destroy(material);
                loader?.Dispose();
                if (slot.Request == request)
                {
                    slot.Request = null;
                    slot.LoadingVersion = null;
                }
                request.Dispose();
            }
        }

        static Bounds MeetingGeometryBounds(MeshFilter[] filters)
        {
            var bounds = new Bounds(filters[0].transform.TransformPoint(filters[0].sharedMesh.bounds.center), Vector3.zero);
            foreach (var filter in filters)
            {
                var mesh = filter.sharedMesh.bounds;
                for (int x = 0; x < 2; x++) for (int y = 0; y < 2; y++) for (int z = 0; z < 2; z++)
                    bounds.Encapsulate(filter.transform.TransformPoint(new Vector3(x == 0 ? mesh.min.x : mesh.max.x,
                        y == 0 ? mesh.min.y : mesh.max.y, z == 0 ? mesh.min.z : mesh.max.z)));
            }
            return bounds;
        }

        void AddMeetingPart(MeshFilter filter, HashSet<string> partIds, string version)
        {
            var renderer = filter.GetComponent<MeshRenderer>();
            var part = filter.transform;
            while (part != null && !partIds.Contains(part.name)) part = part.parent;
            if (renderer != null)
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
            if (part == null)
            {
                Debug.LogWarning("Model has no registered selectable part: " + filter.name + " expected " + string.Join(",", partIds));
                return;
            }
            string id = part.name;
            var collider = filter.GetComponent<MeshCollider>();
            if (collider == null) collider = filter.gameObject.AddComponent<MeshCollider>();
            collider.sharedMesh = filter.sharedMesh;
            var hit = filter.GetComponent<RelayHit>();
            if (hit == null) hit = filter.gameObject.AddComponent<RelayHit>();
            hit.role = id;
            hit.meetingVersion = version;
            hit.pointClicked = point =>
            {
                if (ExplicitDesignMeeting || (SpatialAssembly && !CanCompareModels)) return;
                SelectCollaborativeTarget(version, id, point, filter.transform);
                if (SpatialAssembly && IsCableDemo) HandleCableDemoTarget(id);
                else { meetingPartId = id; ShowMeetingSelection(); ReportMeetingView(); Draw(); }
            };

        }

        void ShowMeetingSelection()
        {
            var overlay = new MaterialPropertyBlock();
            foreach (var (id, renderer) in meetingPartRenderers)
            {
                if (renderer == null) continue;
                overlay.Clear();
                if (id == meetingPartId) overlay.SetColor(MeetingColor, new Color(.97f, .69f, .25f, IsAssemblyMeeting ? .62f : 1));
                renderer.SetPropertyBlock(overlay);
            }
        }

        void MeetingLoadFailure(MeetingModelSlot slot, string id, CancellationTokenSource request, string error, bool dispose = true)
        {
            if (slot.Request == request)
            {
                slot.LoadingVersion = null;
                slot.FailedVersion = id;
                slot.Request = null;
                if (slot.BundleKey != null) bundleReviewError = "후보 " + slot.BundleKey + " 표시 실패 · " + error;
                if (slot.GroovePreviewId != null) Debug.LogWarning("Groove preview " + id + " · " + error);
                else notice = error;
                Draw();
            }
            if (dispose) request.Dispose();
        }

        void PrepareMeetingLighting()
        {
            if (meetingLighting != null) return;
            meetingLighting = new GameObject("Meeting preview lighting");
            var angles = new[] { new Vector3(34, -33, 0), new Vector3(19, 145, 0) };
            for (int i = 0; i < angles.Length; i++)
            {
                var fill = new GameObject("Preview fill", typeof(UnityEngine.Light)).GetComponent<UnityEngine.Light>();
                fill.transform.SetParent(meetingLighting.transform, false);
                fill.transform.localEulerAngles = angles[i];
                fill.type = LightType.Directional;
                fill.shadows = LightShadows.None;
                fill.intensity = i == 0 ? 1.25f : .65f;
            }
        }

        void PositionMeetingModel()
        {
            if (panel == null) return;
            if (IsAssemblyMeeting)
            {
                if (meetingModel != null) PositionAssemblyPreview();
                PositionPhysicalOriginal();
                PositionSpatialComparison();
                PositionCableComparisons();
                PositionGroovePreviews();
                PositionBundleCandidates();
                DisplayPaperHit();
                return;
            }
            if (meetingModel == null) return;
            var distance = meetingLocalPlacement ? 0f : .34f;
            var position = meetingLocalPlacement ? meetingLocalPoint : panel.TransformPoint(new Vector3(-285, 5, 0)) - panel.forward * distance;
            var basis = meetingLocalPlacement ? meetingLocalRotation : panel.rotation;
            var rotation = basis * Quaternion.Euler(meetingRealSize ? 8 : 21, meetingYaw, 0);
            meetingModel.transform.SetPositionAndRotation(position + Vector3.up * meetingHeight, rotation);
            var extent = meetingAssetBounds.size;
            var max = Mathf.Max(extent.x, Mathf.Max(extent.y, extent.z));
            meetingModel.transform.localScale = Vector3.one * (meetingRealSize ? 1 : Mathf.Min(8, panel.lossyScale.y * 285 / Mathf.Max(.001f, max)));
        }

        void PlaceMeetingModel()
        {
            meetingLocalPlacement = true;
            meetingRealSize = true;
            meetingHeight = 0;
            var forward = Vector3.ProjectOnPlane(view.transform.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < .1f) forward = panel.forward;
            meetingLocalPoint = view.transform.position + forward * .95f - Vector3.up * .3f;
            meetingLocalRotation = Quaternion.LookRotation(forward, Vector3.up);
            SetPassthrough(true);
            PositionMeetingModel();
            Draw();
        }

        void PositionSpatialComparison()
        {
            var comparison = assemblyComparison.Root;
            if (comparison == null) return;
            bool show = CanCompareModels && SpatialComparisonVisible && assemblyComparison.LoadedVersion == meetingViewedVersion;
            if (show) PlaceReviewModel(assemblyComparison, 1, ReviewLayoutCount);
            else if (comparison.activeSelf) comparison.SetActive(false);
        }

        int ReviewLayoutCount
        {
            get
            {
                int last = 0;
                for (int i = 0; i < cableComparisons.Length; i++)
                    if (cableComparisons[i].TargetVersion != null) last = i + 1;
                if (assemblyComparison.TargetVersion != null) last = Mathf.Max(1, last);
                return 1 + last + (UsingLocalCandidatePreview && physicalOriginal.TargetVersion != null ? 1 : 0);
            }
        }

        void PositionCableComparisons()
        {
            bool groupVisible = CableComparisonGroup;
            for (int i = 0; i < cableComparisons.Length; i++)
            {
                var slot = cableComparisons[i];
                if (slot.Root == null) continue;
                if (!groupVisible || slot.LoadedVersion != slot.TargetVersion)
                {
                    if (slot.Root.activeSelf) slot.Root.SetActive(false);
                    continue;
                }
                if (!PlaceReviewModel(slot, i + 1, ReviewLayoutCount)) continue;
                var color = AssemblyReferenceColor;
                color.a = slot.LoadedVersion == meetingViewedVersion ? .9f : .66f;
                if (slot.Material.color != color) slot.Material.color = color;
                if (slot.Label == null) continue;
                var b = slot.Bounds;
                var labelPosition = slot.Root.transform.TransformPoint(new Vector3(b.center.x, b.min.y + .0065f, b.min.z - .009f));
                var direction = labelPosition - view.transform.position;
                if (direction.sqrMagnitude > .001f)
                    slot.Label.transform.SetPositionAndRotation(labelPosition, Quaternion.LookRotation(direction, Vector3.up));
            }
        }

        void PositionLatestLabel()
        {
            if (meetingPreview.Label == null)
            {
                var version = TextValue(MeetingVersion(meetingLoadedVersion), "design_version");
                CreateModelLabel(meetingPreview, "Latest · " + (version.Length > 0 ? version : MeetingVersionLabel(meetingLoadedVersion)));
            }
            if (!meetingPreview.Label.activeSelf) meetingPreview.Label.SetActive(true);
            var position = meetingModel.transform.TransformPoint(meetingAssetBounds.center + Vector3.up * (meetingAssetBounds.extents.y + .028f));
            var direction = position - view.transform.position;
            if (direction.sqrMagnitude > .001f)
                meetingPreview.Label.transform.SetPositionAndRotation(position, Quaternion.LookRotation(direction, Vector3.up));
        }

        void CreateCableComparisonLabel(MeetingModelSlot slot) => CreateModelLabel(slot, ((char)('A' + slot.CableIndex)).ToString());

        void CreateModelLabel(MeetingModelSlot slot, string caption)
        {
            slot.Label = new GameObject(slot == meetingPreview ? "LatestLabel" : "Cable comparison label", typeof(TextMesh));
            var text = slot.Label.GetComponent<TextMesh>();
            text.text = caption;
            text.font = koreanFont != null ? koreanFont : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var renderer = text.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = text.font.material;
            text.alignment = TextAlignment.Center;
            text.anchor = TextAnchor.MiddleCenter;
            text.fontSize = 64;
            text.characterSize = .001f;
            text.color = new Color32(235, 255, 250, 255);
            var glyph = renderer.localBounds.size;
            if (glyph.x > 0 && glyph.y > 0)
            {
                float maxWidth = slot == meetingPreview ? .024f : .008f;
                slot.Label.transform.localScale = Vector3.one * Mathf.Min(maxWidth / glyph.x, .005f / glyph.y);
            }
            slot.Label.transform.SetParent(slot.Root.transform, false);
        }

        void ReleaseMeetingInstance(MeetingModelSlot slot)
        {
            if (slot == meetingPreview) meetingPartRenderers.Clear();
            if (slot.Root != null)
            {
                slot.Root.SetActive(false);
                Destroy(slot.Root);
            }
            slot.Import?.Dispose();
            if (slot.Material != null) Destroy(slot.Material);
            slot.Root = null;
            slot.Label = null;
            slot.Import = null;
            slot.Material = null;
            slot.LoadedVersion = slot.FailedVersion = slot.VerifiedHash = null;
        }

        void ReleaseMeetingPreview()
        {
            sessionOriginalBounds = null;
            latestPaperError = null;
            ReleaseBundleReview();
            if (a4DisplayRoot != null) Destroy(a4DisplayRoot.gameObject);
            a4DisplayRoot = latestAnchor = null;
            ReleaseGroovePreviews();
            CancelMeetingModel(physicalOriginal);
            ReleaseMeetingInstance(physicalOriginal);
            CancelMeetingModel(meetingPreview);
            CancelMeetingModel(assemblyComparison);
            ReleaseMeetingInstance(meetingPreview);
            ReleaseMeetingInstance(assemblyComparison);
            foreach (var slot in cableComparisons) QueueComparisonSlot(slot, null);
            if (meetingLighting != null) Destroy(meetingLighting);
            meetingLighting = null;
            meetingPartId = null;
        }
    }
}
