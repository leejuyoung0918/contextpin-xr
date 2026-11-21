using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.XR;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        enum AssemblyAlignment { Unaligned, CaptureOrigin, CaptureRight, CaptureUp, Preview, Confirmed, Lost }
        AssemblyAlignment assemblyAlignment;
        Pose assemblyReference, assemblyMount;
        Vector3 assemblyOrigin, assemblyRight;
        bool assemblyHasMount;
        double assemblyUntrackedSince = -1;
        Material assemblyMaterial;
        bool IsAssemblyMeeting => TextValue(meetingSnapshot?["assembly"], "mode") == "openscad";
        bool AssemblySampling => assemblyAlignment is AssemblyAlignment.CaptureOrigin or AssemblyAlignment.CaptureRight or AssemblyAlignment.CaptureUp;
        bool AssemblyOverlayAligned => assemblyAlignment is AssemblyAlignment.Preview or AssemblyAlignment.Confirmed;
        string AssemblyOriginalId => TextValue(Values(meetingSnapshot, "versions").FirstOrDefault(), "version_id");
        string AssemblyFixedPartId => TextValue(meetingSnapshot?["assembly"], "fixed_part_id");
        static readonly Color AssemblyReferenceColor = new Color(.16f, .8f, .86f, .5f);

        void ReadAssemblyMount()
        {
            var mounting = meetingSnapshot?["assembly"]?["mounting"] as JObject;
            var position = ReadMeetingVector(mounting?["position_m"]);
            assemblyHasMount = false;
            if (position is not Vector3 point || mounting?["rotation_xyzw"] is not JArray rotation ||
                rotation.Count != 4 || rotation.Any(value => value.Type != JTokenType.Float && value.Type != JTokenType.Integer)) return;
            var q = new Quaternion(rotation[0].Value<float>(), rotation[1].Value<float>(),
                rotation[2].Value<float>(), rotation[3].Value<float>());
            var magnitude = Quaternion.Dot(q, q);
            if (!float.IsFinite(magnitude) || Mathf.Abs(magnitude - 1f) > .02f) return;
            assemblyMount = new Pose(point, q.normalized);
            assemblyHasMount = true;
        }

        void BeginAssemblyAlignment()
        {
            if (!xr || !assemblyHasMount || string.IsNullOrEmpty(AssemblyOriginalId)) return;
            assemblyAlignment = AssemblyAlignment.CaptureOrigin;
            assemblyUntrackedSince = -1;
            SetMeetingViewedVersion(AssemblyOriginalId);
            SetPassthrough(true);
            MoveAssemblyPanel(view.transform.position + view.transform.forward);
            notice = "고정된 기준물의 원점부터 짚으세요.";
            Draw();
        }

        bool AssemblyHeadTracked() => InputDevices.GetDeviceAtXRNode(XRNode.Head)
            .TryGetFeatureValue(UnityEngine.XR.CommonUsages.isTracked, out var tracked) && tracked;

        bool UpdateAssemblyProbe(bool onControl)
        {
            Vector3 point = default;
            bool located = AssemblyHeadTracked() && TryProbe(out point);
            probe.gameObject.SetActive(located);
            if (located)
            {
                probe.position = point;
                probe.localScale = Vector3.one * .009f;
            }
            bool mark = OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.RTouch) ||
                hands.Any(hand => hand.GetHand() == OVRPlugin.Hand.HandLeft && Pinching(hand));
            if (!mark || wasMarking || onControl) return mark;
            if (!located)
            {
                notice = "헤드셋과 오른손 검지 또는 오른쪽 컨트롤러 추적을 확인하세요.";
                Draw();
                return true;
            }
            RecordAssemblyPoint(point);
            return true;
        }

        void RecordAssemblyPoint(Vector3 point)
        {
            switch (assemblyAlignment)
            {
                case AssemblyAlignment.CaptureOrigin:
                    assemblyOrigin = point;
                    MoveAssemblyPanel(point);
                    assemblyAlignment = AssemblyAlignment.CaptureRight;
                    notice = "기준 원점에서 +X 방향 점을 짚으세요.";
                    break;
                case AssemblyAlignment.CaptureRight:
                    var across = point - assemblyOrigin;
                    if (across.magnitude < .025f) { notice = "+X 점을 원점에서 25mm 이상 떨어진 곳에 지정하세요."; break; }
                    assemblyRight = across.normalized;
                    assemblyAlignment = AssemblyAlignment.CaptureUp;
                    notice = "기준 원점에서 +Y 방향 점을 짚으세요.";
                    break;
                case AssemblyAlignment.CaptureUp:
                    var along = point - assemblyOrigin;
                    if (along.magnitude < .025f || Mathf.Abs(Vector3.Dot(along.normalized, assemblyRight)) > .28f)
                    {
                        notice = "+X와 직각인 +Y 방향의 점을 다시 짚으세요.";
                        break;
                    }
                    var forward = Vector3.Cross(assemblyRight, along).normalized;
                    assemblyReference = new Pose(assemblyOrigin, Quaternion.LookRotation(forward, Vector3.Cross(forward, assemblyRight)));
                    assemblyAlignment = AssemblyAlignment.Preview;
                    notice = "반투명 원본의 결합 위치와 방향을 실물에 대조하세요.";
                    break;
            }
            PositionMeetingModel();
            Draw();
        }

        void ConfirmAssemblyAlignment()
        {
            if (!AssemblyHeadTracked() || assemblyAlignment != AssemblyAlignment.Preview || meetingLoadedVersion != AssemblyOriginalId) return;
            assemblyAlignment = AssemblyAlignment.Confirmed;
            notice = "원본 정렬을 확인했습니다. 모든 후보를 이 장착 기준에서 봅니다.";
            Draw();
        }

        void UpdateAssemblyTracking()
        {
            if (!xr || !IsAssemblyMeeting || (!AssemblySampling && !AssemblyOverlayAligned)) return;
            if (AssemblyHeadTracked()) { assemblyUntrackedSince = -1; return; }
            if (assemblyUntrackedSince < 0) assemblyUntrackedSince = Time.unscaledTimeAsDouble;
            if (Time.unscaledTimeAsDouble - assemblyUntrackedSince < .4) return;
            InvalidateAssemblyAlignment();
        }

        void InvalidateAssemblyAlignment()
        {
            if (screen != ScreenState.Meeting || !IsAssemblyMeeting || (!AssemblySampling && !AssemblyOverlayAligned)) return;
            assemblyAlignment = AssemblyAlignment.Lost;
            notice = "추적이 끊겨 실물 위 표시를 숨겼습니다. 원본을 다시 맞추세요.";
            PositionMeetingModel();
            Draw();
        }

        void PositionAssemblyPreview()
        {
            bool visible = assemblyHasMount && !AssemblySampling && assemblyAlignment != AssemblyAlignment.Lost;
            if (meetingModel.activeSelf != visible) meetingModel.SetActive(visible);
            if (!visible) return;
            var reference = AssemblyOverlayAligned ? assemblyReference : new Pose(
                panel.TransformPoint(new Vector3(-285, 5, 0)) - panel.forward * .44f, panel.rotation);
            // The GLB importer has already converted glTF axes. This Unity-local mount
            // and the manually registered reference are common to every model version.
            meetingModel.transform.SetPositionAndRotation(reference.position + reference.rotation * assemblyMount.position,
                reference.rotation * assemblyMount.rotation);
            meetingModel.transform.localScale = Vector3.one;
        }

        void MoveAssemblyPanel(Vector3 referencePoint)
        {
            var forward = Vector3.ProjectOnPlane(referencePoint - view.transform.position, Vector3.up).normalized;
            if (forward.sqrMagnitude < .1f) return;
            var side = Quaternion.AngleAxis(48, Vector3.up) * forward;
            panel.localScale = Vector3.one * .00105f;
            panel.SetPositionAndRotation(view.transform.position + side * 1.35f + Vector3.up * .08f,
                Quaternion.LookRotation(side, Vector3.up));
        }

        void SetAssemblyMeshMaterial(MeshFilter[] filters, string id)
        {
            var shader = Resources.Load<Shader>("MeetingAssemblyOverlay");
            if (shader == null) throw new InvalidOperationException("원본과 수정안의 반투명 표시를 준비하지 못했습니다.");
            assemblyMaterial = new Material(shader)
            {
                color = id == AssemblyOriginalId ? AssemblyReferenceColor : new Color(1f, .68f, .21f, .53f),
            };
            foreach (var filter in filters)
                if (filter.TryGetComponent<MeshRenderer>(out var renderer)) renderer.sharedMaterial = assemblyMaterial;
        }

        string AssemblyAlignmentText()
        {
            if (!assemblyHasMount) return "장착 변환 설정이 필요합니다.";
            if (!xr) return "미터 단위 모델 비교 · 데스크톱 보기";
            return assemblyAlignment switch
            {
                AssemblyAlignment.CaptureOrigin => "1/3 · 기준 원점",
                AssemblyAlignment.CaptureRight => "2/3 · 기준 +X 방향 점",
                AssemblyAlignment.CaptureUp => "3/3 · 기준 +Y 방향 점",
                AssemblyAlignment.Preview => "실물에 원본이 맞는지 확인하세요",
                AssemblyAlignment.Confirmed => "수동 정렬 확인 · 동일 장착 기준",
                AssemblyAlignment.Lost => "추적 끊김 · 실물 겹쳐 보기 숨김",
                _ => "가상 보기 · 실물 정렬 전",
            };
        }

        void DrawAssemblyControls()
        {
            var original = AssemblyOriginalId;
            if (!string.IsNullOrEmpty(original) && !AssemblySampling)
                Button("MeetingOriginal", 36, 548, 194, 50, "원본 모델", () => { SetMeetingViewedVersion(original); Draw(); },
                    meetingLoadedVersion == original);
            if (!xr || !assemblyHasMount) return;
            if (AssemblySampling)
            {
                Label("AssemblyRegistrationInstruction", content, 55, 268, 566, 134,
                    "고정된 기준물의 표시를 오른손 검지로 짚고 왼손을 핀치하세요. 컨트롤러는 추적 점을 기준에 맞춘 뒤 A를 누르세요.", 23, Light);
                Button("AssemblyAlignmentRetry", 242, 548, 204, 50, "원점부터 다시", BeginAssemblyAlignment);
                Button("AssemblyAlignmentCancel", 458, 548, 184, 50, "정렬 취소", () =>
                {
                    assemblyAlignment = AssemblyAlignment.Unaligned;
                    SetPassthrough(false);
                    Recenter();
                    notice = "";
                    Draw();
                });
                return;
            }
            Button("AssemblyAlignmentStart", 242, 548, 204, 50,
                assemblyAlignment == AssemblyAlignment.Unaligned ? "원본 맞추기" : "다시 맞추기", BeginAssemblyAlignment);
            if (assemblyAlignment == AssemblyAlignment.Preview && meetingLoadedVersion == original)
                Button("AssemblyAlignmentConfirm", 458, 548, 184, 50, "실물 일치 확인", ConfirmAssemblyAlignment, true);
        }

        void ResetAssemblyPreview()
        {
            assemblyAlignment = AssemblyAlignment.Unaligned;
            assemblyUntrackedSince = -1;
            assemblyHasMount = false;
        }
    }
}
