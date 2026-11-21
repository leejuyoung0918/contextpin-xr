using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.Hands;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        readonly LabBoardDefinition physicalGuideBoard = LabBoards.Resolve(LabBoards.PhysicalId);
        readonly List<BoardVector> physicalReferenceSamples = new List<BoardVector>(5);
        BoardRegistration physicalTrial, physicalRegistration;
        Transform physicalBoardRoot;
        Material physicalRegionMaterial;
        Text physicalViewHint;
#if UNITY_ANDROID && !UNITY_EDITOR
        OVRSpatialAnchor spatialBoardAnchor;
        double spatialAnchorRequestedAt, spatialAnchorLastTrackedAt;
#endif
        string physicalBoardInstanceId;
        string markerSourceCaptureId;
        float markerResidualPixels;
        int markerRequestSerial;
        bool markerRequestBusy;
        bool physicalProfileConfirmed;
        int physicalRevision, physicalTrackingEpoch, physicalBoardRetries;
        double physicalBoardStarted;

        BoardRegistration ActiveBoardRegistration
        {
            get
            {
                if (physicalRegistration == null || physicalBoardRoot == null) return null;
                var snapshot = physicalRegistration.Copy();
                snapshot.origin = ToBoardVector(physicalBoardRoot.position);
                snapshot.axisX = ToBoardVector(physicalBoardRoot.right);
                snapshot.axisY = ToBoardVector(physicalBoardRoot.up);
                snapshot.axisZ = ToBoardVector(physicalBoardRoot.forward);
                return snapshot;
            }
        }
        int PhysicalTrackingEpoch => physicalTrackingEpoch;
        string PendingPhysicalBoardId => guideAlignment != GuideAlignment.None ? physicalBoardInstanceId : null;

        static string SpatialInputSource
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return "real";
#else
                return "simulation";
#endif
            }
        }

        void BeginPhysicalGuideRegistration(string instanceId = null, int previousRevision = 0, int previousEpoch = 0)
        {
            physicalRevision = Math.Max(previousRevision, physicalRevision);
            physicalTrackingEpoch = Math.Max(previousEpoch, physicalTrackingEpoch);
            ResetPhysicalGuideAlignment(false);
            physicalBoardInstanceId = string.IsNullOrWhiteSpace(instanceId) ? Guid.NewGuid().ToString("N") : instanceId;
            physicalBoardStarted = Time.realtimeSinceStartupAsDouble;
            physicalBoardRetries = 0;
        }

        void ResetPhysicalGuideAlignment(bool lost)
        {
            physicalReferenceSamples.Clear();
            physicalTrial = null;
            physicalProfileConfirmed = false;
            ClearMarkerCandidate();
            physicalTrackingEpoch++;
            if (physicalRegistration != null && lost) physicalRegistration.status = "stale";
            physicalRegistration = null;
            ClearPhysicalBoardRoot();
        }

        void ClearPhysicalBoardRoot()
        {
            if (physicalBoardRoot == null) return;
            physicalBoardRoot.gameObject.SetActive(false);
            Destroy(physicalBoardRoot.gameObject);
            physicalBoardRoot = null;
#if UNITY_ANDROID && !UNITY_EDITOR
            spatialBoardAnchor = null;
#endif
        }

        void DrawPhysicalGuideAlign()
        {
            var references = physicalGuideBoard.references;
            int index = physicalReferenceSamples.Count;
            bool ready = physicalProfileConfirmed && index == references.Length && physicalTrial != null;
            Heading("실물 LED 보드 등록", "보드를 단단히 고정하고 전원을 끈 상태에서 기준점을 짚으세요.");

            var diagram = RoundBox("Physical reference board", content, 36, 207, 590, 371, BoardFace, 15).transform;
            BoldLabel("Physical reference diagram", diagram, 20, 11, 550, 42, "30줄 브레드보드 기준점", 27, Silkscreen);
            // On the physical board the first row faces the learner. The local
            // diagram puts that near edge at the bottom of the panel.
            RoundBox("Reference face", diagram, 134, 78, 320, 258, Field, 11);
            Box("Central trench", diagram, 270, 91, 43, 231, BoardEdge);
            Label("Reference near edge", diagram, 184, 335, 220, 31, "1번 줄 · 몸 쪽", 19, BoardCaption, TextAnchor.MiddleCenter);
            for (int slot = 0; slot < references.Length; slot++)
            {
                var marker = references[slot];
                bool selected = physicalProfileConfirmed && index == slot;
                bool captured = index > slot;
                float x = 155 + (marker.boardLocal.x / references[1].boardLocal.x) * 201;
                float y = 101 + (1 - marker.boardLocal.z / references[2].boardLocal.z) * 181;
                RoundBox("Registration reference " + marker.address, diagram, x, y, 72, 38, selected ? Lime : Card, 8);
                BoldLabel("Registration reference label", diagram, x, y, 72, 38,
                    marker.address + (captured ? " ✓" : ""), 21, Light, TextAnchor.MiddleCenter);
            }

            RoundBox("Physical instructions", content, 644, 207, 560, 371, Card, 15);
            string title = !physicalProfileConfirmed ? "보드 규격부터 확인하세요" :
                (ready ? "보드 위 표시를 확인하세요" : references[index].address + "를 검지로 짚으세요");
            string detail;
            if (!physicalProfileConfirmed)
                detail = "A1–J1 간격은 27.94 mm, A1–A30 간격은 73.66 mm인 보드용입니다. " +
                    "1번 줄을 몸 쪽에 두고, 교수 확인을 마친 가까운 전원 레일 두 쌍에 +1·+2, −1·−2 라벨을 붙이세요.";
            else if (ready)
                detail = $"측정점 차이 · 모서리 {physicalTrial.cornerErrorMeters * 1000:0.0} mm, 가운데 {physicalTrial.middleErrorMeters * 1000:0.0} mm\n" +
                    "실물의 표시와 방향이 맞으면 계속하세요. 공간 안내는 작업 영역을 보여 줍니다.";
            else
                detail = markerRequestBusy ? "마커 사진을 확인하는 동안 보드를 고정하세요. 직접 기준점을 짚어 수동으로 계속할 수 있어요." :
                    index < 3 ? "오른손 검지 끝을 지정한 위치에 대고 왼손으로 핀치하세요." :
                    !string.IsNullOrEmpty(markerSourceCaptureId) ? "마커 사진에서 찾은 위치예요. 실제 검증점 두 곳을 오른손 검지로 짚고 왼손으로 핀치하세요." :
                    "가상 표시가 있는 부근의 실제 검증점을 찾아 오른손 검지를 댄 뒤 왼손으로 핀치하세요.";

            BoldLabel("Physical registration title", content, 667, 233, 511, 77, title, 31, Light);
            Label("Physical registration detail", content, 668, 320, 510, 150, detail, 23, Muted);
            Label("Physical registration progress", content, 668, 485, 510, 63,
                !physicalProfileConfirmed ? "교수가 규격과 연결 구성을 확인한 키트를 사용하세요."
                    : $"기준점 확인  {index} / {references.Length}  ·  작업 영역 표시",
                22, Mint);

            Button("GuideAlignBack", 36, 610, 214, 62, "돌아가기", CancelGuideAlignment);
            if (!physicalProfileConfirmed)
                Button("PhysicalProfileConfirm", 717, 610, 487, 62, "교사가 확인한 보드로 등록", () =>
                {
                    physicalProfileConfirmed = true;
                    notice = "";
                    Draw();
                }, true);
            if (index > 0)
                Button("GuideAlignReset", 261, 610, 218, 62, "다시 맞추기", () =>
                {
                    physicalReferenceSamples.Clear();
                    physicalTrial = null;
                    ClearMarkerCandidate();
                    physicalBoardRetries++;
                    ClearPhysicalBoardRoot();
                    notice = "";
                    Draw();
                });
            if (ready)
                Button("GuideAlignDone", 724, 610, 480, 62, "보드 일치 확인 · 계속", ConfirmPhysicalGuideAlignment, true);
            else if (physicalProfileConfirmed && index == 0 && SpatialInputSource == "real" && transferOnline &&
                CanTakeTransferPhoto && !markerRequestBusy)
                Button("MarkerAlign", 808, 610, 396, 62, "마커 사진으로 위치 찾기", BeginMarkerSuggestion);
            if (physicalTrial != null)
                DrawPhysicalRegistrationPreview();
        }

        bool TryPhysicalGuideContact(out Vector3 point)
        {
            SubsystemManager.GetSubsystems(handSystems);
            foreach (var system in handSystems)
            {
                if (system.running && system.rightHand.isTracked &&
                    system.rightHand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var pose))
                {
                    point = xrRig.trackingSpace.TransformPoint(pose.position);
                    return true;
                }
            }
            point = default;
            return false;
        }

        void MarkPhysicalGuideReference(Vector3 point)
        {
            if (!physicalProfileConfirmed || physicalReferenceSamples.Count == physicalGuideBoard.references.Length)
                return;
            if (markerRequestBusy) ClearMarkerCandidate();

            physicalReferenceSamples.Add(new BoardVector(point.x, point.y, point.z));
            if (physicalReferenceSamples.Count >= 3 && !ManualBoardRegistration.TryFit(physicalGuideBoard,
                physicalReferenceSamples, out physicalTrial, out string issue))
            {
                physicalReferenceSamples.Clear();
                ClearMarkerCandidate();
                physicalBoardRetries++;
                ClearPhysicalBoardRoot();
                notice = issue;
            }
            else
            {
                if (physicalTrial != null) SetPhysicalBoardPose(physicalTrial);
                notice = "";
            }
            Draw();
        }

        void ConfirmPhysicalGuideAlignment()
        {
            var target = guideAlignment;
            if (target == GuideAlignment.None || !physicalProfileConfirmed || physicalTrial == null) return;
            string role = target == GuideAlignment.Recording ? "demonstration" : "practice";
            var registration = physicalTrial.Copy();
            if (!ManualBoardRegistration.Confirm(registration, physicalBoardInstanceId, role, SpatialInputSource,
                physicalRevision + 1, physicalTrackingEpoch, DateTime.UtcNow))
                return;
            registration.elapsedMs = (long)((Time.realtimeSinceStartupAsDouble - physicalBoardStarted) * 1000.0);
            registration.retryCount = physicalBoardRetries;
            registration.anchorStatus = SpatialInputSource == "real" ? "requested" : "simulator_local_pose";
            if (!string.IsNullOrEmpty(markerSourceCaptureId))
            {
                registration.method = "marker_assisted_contact";
                registration.sourceCaptureId = markerSourceCaptureId;
                registration.reprojectionErrorPixels = markerResidualPixels;
            }
            if (!AcceptGuideBoardRegistration(target, registration)) return;

            physicalRegistration = registration;
            physicalRevision++;
            guideAlignment = GuideAlignment.None;
            aligned = true;
            worldPoints.Clear();
            SetPhysicalBoardPose(registration);
#if UNITY_ANDROID && !UNITY_EDITOR
            // Use an in-session anchor. A future session always reacquires the
            // board profile and current pose independently.
            spatialBoardAnchor = physicalBoardRoot.gameObject.AddComponent<OVRSpatialAnchor>();
            spatialAnchorRequestedAt = Time.realtimeSinceStartupAsDouble;
            spatialAnchorLastTrackedAt = -1;
#endif
            foreach (var point in physicalGuideBoard.points)
                worldPoints[point.id] = ToUnity(registration.ToWorld(physicalGuideBoard.FindTerminal(point.id).boardLocal));
            notice = "";
            MovePanelBesideBoard();
            if (target == GuideAlignment.Recording) StartGuideRecording();
            else EnterGuidePractice();
        }

        void SetPhysicalBoardPose(BoardRegistration registration)
        {
            if (physicalBoardRoot == null) physicalBoardRoot = new GameObject("Registered LED Board").transform;
            physicalBoardRoot.SetPositionAndRotation(ToUnity(registration.origin),
                Quaternion.LookRotation(ToUnity(registration.axisZ), ToUnity(registration.axisY)));
        }

        static Vector3 ToUnity(BoardVector value) => new Vector3(value.x, value.y, value.z);
        static BoardVector ToBoardVector(Vector3 value) => new BoardVector(value.x, value.y, value.z);

        [Serializable]
        sealed class MarkerPoseRequest
        {
            public string sessionId, captureId;
            public int registrationRevision;
        }

        [Serializable]
        sealed class MarkerPoseResult
        {
            public string status, source, captureId, boardInstanceId, layoutId;
            public int registrationRevision, trackingEpoch;
            public Vector3 cameraOrigin, cameraAxisX, cameraAxisY, cameraAxisZ;
            public float reprojectionErrorPixels;
        }

        void ClearMarkerCandidate()
        {
            markerRequestSerial++;
            markerSourceCaptureId = null;
            markerResidualPixels = -1;
            markerRequestBusy = false;
        }

        void BeginMarkerSuggestion()
        {
            if (markerRequestBusy || transferPairing == null || SpatialInputSource != "real")
            {
                notice = "마커 사진 등록에는 Quest 카메라와 교수 PC가 필요합니다. 기준점을 직접 짚어 계속하세요.";
                RefreshStatus();
                return;
            }
            string captureId = Guid.NewGuid().ToString("N");
            markerRequestBusy = true;
            if (RequestTransferPhoto("setup", "student", captureId))
                StartCoroutine(FetchMarkerSuggestion(TraceData, captureId, ++markerRequestSerial, physicalTrackingEpoch));
            else markerRequestBusy = false;
        }

        bool MarkerRequestIsCurrent(TraceSession session, int serial, int epoch) =>
            screen == ScreenState.Align && guideAlignment != GuideAlignment.None && session == TraceData &&
            serial == markerRequestSerial && epoch == physicalTrackingEpoch && physicalReferenceSamples.Count == 0;

        IEnumerator FetchMarkerSuggestion(TraceSession session, string captureId, int serial, int epoch)
        {
            TransferCapture photo = null;
            while (transferCapturing && MarkerRequestIsCurrent(session, serial, epoch)) yield return null;
            if (!MarkerRequestIsCurrent(session, serial, epoch)) yield break;
            photo = session.captures.FirstOrDefault(item => item.captureId == captureId);
            if (photo == null || photo.phase != "setup" || photo.source != "real_camera" ||
                photo.sessionTimeMs < 0 || photo.headMotionDegrees > 12 || photo.trackingEpoch != epoch ||
                photo.boardInstanceId != physicalBoardInstanceId)
            {
                FinishMarkerSuggestion("현재 사진에서 마커 위치를 확인할 수 없어요. 머리를 고정하고 다시 찍거나 직접 등록하세요.");
                yield break;
            }

            // Upload and publish first. The relay picks the bytes/metadata
            // from this archived snapshot, never from whichever picture is latest.
            double until = Time.realtimeSinceStartupAsDouble + 8;
            string publishedKey = "\"captureId\":\"" + captureId + "\"";
            while (MarkerRequestIsCurrent(session, serial, epoch) && Time.realtimeSinceStartupAsDouble < until &&
                (!uploadHashes.Contains(photo.sha256) || transferLastState?.Contains(publishedKey) != true))
                yield return null;
            if (!MarkerRequestIsCurrent(session, serial, epoch)) yield break;
            if (transferPairing == null || !uploadHashes.Contains(photo.sha256) || transferLastState?.Contains(publishedKey) != true)
            {
                FinishMarkerSuggestion("마커 사진을 교수 PC에 전달하지 못했어요. 기준점을 직접 짚어 계속하세요.");
                yield break;
            }
            MarkerPoseResult result = null;
            var request = new MarkerPoseRequest { sessionId = session.id, captureId = captureId,
                registrationRevision = photo.registrationRevision };
            yield return RelayJson<MarkerPoseResult>(RelayBaseUrl() + "/transfer/marker-pose", request,
                transferPairing.deviceToken, response => result = response, 8);
            if (!MarkerRequestIsCurrent(session, serial, epoch)) yield break;
            if (result?.status != "candidate" || result.source != photo.source || result.captureId != photo.captureId ||
                result.boardInstanceId != photo.boardInstanceId || result.registrationRevision != photo.registrationRevision ||
                result.trackingEpoch != epoch || result.layoutId != physicalGuideBoard.markerLayoutId)
            {
                FinishMarkerSuggestion("마커 두 개의 위치를 확인하지 못했어요. 인쇄물이나 PC 설정을 확인하거나 직접 등록하세요.");
                yield break;
            }

            // The backend has already converted OpenCV camera axes. Use the
            // saved RGB-camera world pose once, without applying a lens offset.
            var matrix = Matrix4x4.TRS(photo.cameraPosition, photo.cameraRotation, Vector3.one);
            Vector3 origin = matrix.MultiplyPoint3x4(result.cameraOrigin);
            Vector3 axisX = matrix.MultiplyVector(result.cameraAxisX);
            Vector3 axisY = matrix.MultiplyVector(result.cameraAxisY);
            Vector3 axisZ = matrix.MultiplyVector(result.cameraAxisZ);
            if (Vector3.Dot(Vector3.Cross(axisX, axisY), axisZ) < .95f)
            {
                FinishMarkerSuggestion("마커 방향을 확인하지 못했어요. 기준점을 직접 짚어 주세요.");
                yield break;
            }
            var predicted = physicalGuideBoard.references.Take(3).Select(item => ToBoardVector(origin +
                axisX * item.boardLocal.x + axisY * item.boardLocal.y + axisZ * item.boardLocal.z)).ToArray();
            if (!ManualBoardRegistration.TryFit(physicalGuideBoard, predicted, out physicalTrial, out _))
            {
                FinishMarkerSuggestion("마커 위치가 보드 규격과 맞지 않아요. 기준점을 직접 짚어 주세요.");
                yield break;
            }
            physicalReferenceSamples.AddRange(predicted);
            markerSourceCaptureId = photo.captureId;
            markerResidualPixels = result.reprojectionErrorPixels;
            SetPhysicalBoardPose(physicalTrial);
            FinishMarkerSuggestion("마커 후보를 표시했어요. 실물 검증점 두 곳을 확인하세요.");
        }

        void FinishMarkerSuggestion(string message)
        {
            markerRequestBusy = false;
            notice = message;
            Draw();
        }

        Vector3? ResolvePhysicalGuidePoint(string terminalId)
        {
            var terminal = physicalGuideBoard.FindTerminal(terminalId);
            return physicalRegistration?.status == "valid" && physicalBoardRoot != null && terminal != null
                ? physicalBoardRoot.TransformPoint(ToUnity(terminal.boardLocal)) : (Vector3?)null;
        }

        void DrawPhysicalRegistrationPreview()
        {
            if (physicalBoardRoot == null) return;
            worldLayer = new GameObject("Physical board registration preview").transform;
            worldLayer.SetParent(physicalBoardRoot, false);
            var refs = physicalGuideBoard.references;
            foreach (int index in new[] {0, 1, 2, 3, 4})
            {
                var item = refs[index];
                var local = ToUnity(item.boardLocal);
                DrawPhysicalRegionRing(local, .006f, Color.yellow);
                if (index == physicalReferenceSamples.Count || physicalReferenceSamples.Count == refs.Length)
                    DrawPhysicalRegionLabel(local, item.address, index > 2 ? .028f : .015f, null);
            }
        }

        void PlacePhysicalGuideTargets(IEnumerable<string> pointIds, Action<string> select = null)
        {
            if (physicalRegistration?.status != "valid" || physicalBoardRoot == null) return;
            var targets = pointIds.Select(physicalGuideBoard.FindTerminal).Where(terminal => terminal != null)
                .GroupBy(terminal => terminal.netId).Select(group => group.First()).ToArray();
            if (targets.Length == 0) return;
            worldLayer = new GameObject("Current LED work regions").transform;
            worldLayer.SetParent(physicalBoardRoot, false);
            foreach (var target in targets)
            {
                var regionTerminals = physicalGuideBoard.terminals.Where(terminal => terminal.netId == target.netId).ToArray();
                var local = regionTerminals.Select(terminal => ToUnity(terminal.boardLocal))
                    .Aggregate(Vector3.zero, (sum, part) => sum + part) / regionTerminals.Length;
                DrawPhysicalRegionRing(local, .0075f, new Color(.55f, .94f, .49f));
                string region = physicalGuideBoard.Find(target.id)?.label ?? target.address;
                Action handler = select == null ? null : () => select(target.id);
                DrawPhysicalRegionLabel(local, region, .027f, handler);
            }
            UpdatePhysicalBoardVisibility();
        }

        void DrawPhysicalRegionRing(Vector3 point, float radius, Color color)
        {
            var ring = new GameObject("Board region outline").AddComponent<LineRenderer>();
            ring.transform.SetParent(worldLayer, false);
            ring.useWorldSpace = false;
            ring.loop = true;
            ring.widthMultiplier = .0013f;
            ring.positionCount = 32;
            for (int index = 0; index < ring.positionCount; index++)
            {
                float angle = index * 2 * Mathf.PI / ring.positionCount;
                ring.SetPosition(index, point + new Vector3(radius * Mathf.Cos(angle), .001f, radius * Mathf.Sin(angle)));
            }
            if (physicalRegionMaterial == null)
            {
                var shader = Shader.Find("Sprites/Default");
                if (shader != null) physicalRegionMaterial = new Material(shader);
            }
            if (physicalRegionMaterial != null) ring.sharedMaterial = physicalRegionMaterial;
            ring.startColor = ring.endColor = color;
        }

        void DrawPhysicalRegionLabel(Vector3 local, string label, float height, Action select)
        {
            var obj = new GameObject("Board region label", typeof(RectTransform), typeof(Canvas));
            var face = (RectTransform)obj.transform;
            face.SetParent(worldLayer, false);
            face.localPosition = local + Vector3.up * height;
            face.localScale = Vector3.one * .00034f;
            face.sizeDelta = new Vector2(110, 38);
            face.pivot = new Vector2(.5f, 0);
            face.rotation = Quaternion.LookRotation(face.position - view.transform.position, view.transform.up);
            obj.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            worldLabels.Add(face);
            if (select != null) Button("BoardRegionSelect", 0, 0, 110, 38, label, select, true, face);
            else
            {
                RoundBox("Region pill", face, 0, 0, 110, 38, Mint, 7);
                BoldLabel("Region name", face, 0, 0, 110, 38, label, 23, Card, TextAnchor.MiddleCenter);
            }
        }

        void SetPhysicalViewHint(float x, float y, float width, float height)
        {
            physicalViewHint = Label("Board visibility", content, x, y, width, height, "", 20, Gold);
            UpdatePhysicalBoardVisibility();
        }

        void UpdatePhysicalBoardVisibility()
        {
            if (physicalRegistration?.status != "valid" || physicalBoardRoot == null) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (spatialBoardAnchor == null && physicalRegistration.anchorStatus == "requested")
                physicalRegistration.anchorStatus = "local_tracking_fallback";
            if (spatialBoardAnchor != null)
            {
                double now = Time.realtimeSinceStartupAsDouble;
                if (spatialBoardAnchor.IsTracked)
                {
                    spatialAnchorLastTrackedAt = now;
                    if (physicalRegistration.anchorStatus != "tracked")
                    {
                        physicalRegistration.anchorStatus = "tracked";
                        physicalRegistration.anchorId = spatialBoardAnchor.Uuid.ToString("N");
                    }
                }
                else if (spatialAnchorLastTrackedAt >= 0 && now - spatialAnchorLastTrackedAt > .35)
                {
                    InvalidateGuideAlignment();
                    return;
                }
                else if (spatialAnchorLastTrackedAt < 0 && now - spatialAnchorRequestedAt > 4)
                {
                    // An unavailable optional anchor leaves the explicitly
                    // verified pose in this tracking epoch, rather than
                    // multiplying a failed anchor transform into the board.
                    physicalRegistration.anchorStatus = "local_tracking_fallback";
                    Destroy(spatialBoardAnchor);
                    spatialBoardAnchor = null;
                }
            }
#endif
            var boardCenter = physicalBoardRoot.TransformPoint(new Vector3(.014f, 0, .037f));
            var viewport = view.WorldToViewportPoint(boardCenter);
            bool visible = viewport.z > 0 && viewport.x >= 0 && viewport.x <= 1 && viewport.y >= 0 && viewport.y <= 1;
            if (physicalViewHint != null)
                physicalViewHint.text = visible ? "" : "보드를 바라보면 작업 영역을 표시해요.";
            if (worldLayer != null && worldLayer.parent == physicalBoardRoot)
                worldLayer.gameObject.SetActive(visible);
        }

        void ReleasePhysicalGuideRegistration()
        {
            ClearPhysicalBoardRoot();
            if (physicalRegionMaterial != null) Destroy(physicalRegionMaterial);
        }
    }
}
