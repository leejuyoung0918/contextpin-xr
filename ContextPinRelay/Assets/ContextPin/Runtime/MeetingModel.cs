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
        CancellationTokenSource meetingModelRequest;
        GameObject meetingModel, meetingLighting;
        GltfImport meetingImport;
        Bounds meetingAssetBounds;
        string meetingLoadingVersion, meetingFailedVersion, meetingLoadedVersion;
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
            if (id == meetingLoadingVersion) return;
            meetingModelRequest?.Cancel();
            meetingModelRequest = null;
            meetingLoadingVersion = null;
            if (string.IsNullOrEmpty(id) || id == meetingLoadedVersion || id == meetingFailedVersion) return;
            meetingModelRequest = new CancellationTokenSource();
            meetingLoadingVersion = id;
            StartCoroutine(DownloadMeetingModel(meetingTransport, version, id, meetingModelRequest));
        }

        IEnumerator DownloadMeetingModel(MeetingTransport transport, JToken version, string id, CancellationTokenSource request)
        {
            var asset = version["assets"]?["preview.glb"];
            var hash = asset?.Value<string>("sha256") ?? version.Value<string>("preview_sha256");
            var path = version.Value<string>("preview_url");
            if (string.IsNullOrEmpty(path) && asset?.Value<string>("asset_id") is string assetId)
                path = "/v1/assets/" + Uri.EscapeDataString(assetId);
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(hash) || hash.Length != 64)
            {
                MeetingLoadFailure(id, request, "미리보기 파일 주소 또는 해시가 없습니다.");
                yield break;
            }
            byte[] bytes = null;
            string error = null;
            yield return transport.Download(path, MeetingPreviewLimit, (data, failure) => { bytes = data; error = failure; });
            if (request.IsCancellationRequested) { request.Dispose(); yield break; }
            if (bytes == null) { MeetingLoadFailure(id, request, error ?? "미리보기 연결이 끊겼습니다."); yield break; }
            var registeredSize = ReadMeetingVector(asset?["bounds_m"]?["size"] ?? version["bounds_m"]?["size"]);
            var partIds = new HashSet<string>(Values(meetingSnapshot?["profile"], "semantic_parts")
                .Select(part => part.Value<string>("part_id")), StringComparer.Ordinal);
            _ = ImportMeetingModel(bytes, hash, registeredSize, partIds, id, request);
        }

        static Vector3? ReadMeetingVector(JToken token)
        {
            if (token is not JArray array || array.Count != 3 || array.Any(v => v.Type != JTokenType.Float && v.Type != JTokenType.Integer)) return null;
            var vector = new Vector3(array[0].Value<float>(), array[1].Value<float>(), array[2].Value<float>());
            return float.IsFinite(vector.x) && float.IsFinite(vector.y) && float.IsFinite(vector.z) ? vector : null;
        }

        async Task ImportMeetingModel(byte[] bytes, string expectedHash, Vector3? registeredSize,
            HashSet<string> partIds, string id, CancellationTokenSource request)
        {
            GameObject instance = null;
            GltfImport loader = null;
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
                if (request.IsCancellationRequested || meetingModelRequest != request || screen != ScreenState.Meeting ||
                    RequestedMeetingVersion()?.Value<string>("version_id") != id) return;
                ReleaseMeetingInstance();
                meetingModel = instance;
                meetingImport = loader;
                instance = null;
                loader = null;
                meetingAssetBounds = bounds;
                var pivot = new GameObject("MeetingPreviewPivot");
                meetingModel.transform.SetParent(pivot.transform, false);
                if (!IsAssemblyMeeting) meetingModel.transform.localPosition = -bounds.center;
                meetingModel = pivot;
                meetingPartRenderers.Clear();
                foreach (var filter in filters) AddMeetingPart(filter, partIds, id);
                if (IsAssemblyMeeting) SetAssemblyMeshMaterial(filters);
                meetingModel.SetActive(true);
                meetingModel.transform.GetChild(0).gameObject.SetActive(true);
                PrepareMeetingLighting();
                PositionMeetingModel();
                Physics.SyncTransforms();
                meetingLoadedVersion = id;
                meetingLoadingVersion = meetingFailedVersion = meetingPartId = null;
                meetingModelRequest = null;
                ShowMeetingSelection();
                Draw();
                ReportMeetingView();
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (!request.IsCancellationRequested) MeetingLoadFailure(id, request, "모델 표시 실패 · " + error.Message, false);
            }
            finally
            {
                if (instance != null) Destroy(instance);
                loader?.Dispose();
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
            if (part == null || SpatialAssembly) return;
            string id = part.name;
            filter.gameObject.AddComponent<MeshCollider>().sharedMesh = filter.sharedMesh;
            var hit = filter.gameObject.AddComponent<RelayHit>();
            hit.role = id;
            hit.clicked = () =>
            {
                if (meetingLoadedVersion != version || RequestedMeetingVersion()?.Value<string>("version_id") != version) return;
                meetingPartId = id;
                ShowMeetingSelection();
                ReportMeetingView();
                Draw();
            };
            if (renderer != null) meetingPartRenderers.Add((id, renderer));
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

        void MeetingLoadFailure(string id, CancellationTokenSource request, string error, bool dispose = true)
        {
            if (meetingModelRequest == request && screen == ScreenState.Meeting)
            {
                meetingLoadingVersion = null;
                meetingFailedVersion = id;
                meetingModelRequest = null;
                notice = error;
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
            if (meetingModel == null || panel == null) return;
            if (IsAssemblyMeeting) { PositionAssemblyPreview(); return; }
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

        void ReleaseMeetingInstance()
        {
            meetingPartRenderers.Clear();
            if (meetingModel != null)
            {
                meetingModel.SetActive(false);
                Destroy(meetingModel);
            }
            meetingModel = null;
            meetingImport?.Dispose();
            meetingImport = null;
            if (assemblyMaterial != null) Destroy(assemblyMaterial);
            assemblyMaterial = null;
        }

        void ReleaseMeetingPreview()
        {
            meetingModelRequest?.Cancel();
            meetingModelRequest = null;
            ReleaseMeetingInstance();
            if (meetingLighting != null) Destroy(meetingLighting);
            meetingLighting = null;
            meetingLoadedVersion = meetingLoadingVersion = meetingFailedVersion = meetingPartId = null;
        }
    }
}
