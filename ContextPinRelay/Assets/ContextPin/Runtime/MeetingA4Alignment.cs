using UnityEngine;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        const float A4Short = .210f, A4Long = .297f;
        const float A4EdgeTolerance = .022f, A4HeightTolerance = .020f, A4MaxCornerCosine = .18f;
        readonly Vector3[] a4Corners = new Vector3[4];
        readonly Transform[] a4Marks = new Transform[4];
        LineRenderer a4Border, a4CenterCross;
        int a4Count;
        string a4Status;
        bool a4PoseApplied;
        Vector3 a4ViewerRight;
        Pose a4Surface;
        Vector2 a4Size;
        bool A4PointSampling => assemblyAlignment == AssemblyAlignment.CaptureA4;
        bool A4PreviewActive => a4PoseApplied && assemblyAlignment == AssemblyAlignment.Preview;
        bool A4PoseApplied => a4PoseApplied;
        bool A4ReviewSurfaceActive => a4PoseApplied && tableA4Measured && HasReviewTable && assemblyAlignment == AssemblyAlignment.Confirmed;
        int A4CornerCount => a4Count;
        string A4Status => a4Status;

        void BeginA4Alignment()
        {
            if (!SpatialAssembly || !xr || string.IsNullOrEmpty(AssemblyOriginalId)) return;
            StopAssemblyQrTracker();
            InvalidateExplicitSelection();
            ClearReviewTable();
            ClearTabletopAlignment();
            assemblyQrPoseApplied = false;
            assemblyQrStatus = "";
            assemblyAlignment = AssemblyAlignment.CaptureA4;
            assemblyUntrackedSince = -1;
            SetMeetingViewedVersion(AssemblyOriginalId);
            if (OVRManager.IsInsightPassthroughSupported()) SetPassthrough(true);
            if (assemblyRecoveryPanel) RestoreSpatialPanel();
            notice = "";
            Debug.Log("ContextPin A4: waiting for front-left corner");
            PositionMeetingModel();
            Draw();
        }

        bool TryConfirmA4Corner()
        {
            if (!A4PointSampling || movingMeetingPanel || ReviewTableSampling || a4Count >= 4) return false;
            if (view == null || !TryTabletopProbe(out var point))
                return RejectA4Corner("오른쪽 컨트롤러와 헤드셋 추적을 확인한 뒤 A를 누르세요.");
            if (a4Count == 0)
            {
                a4ViewerRight = Vector3.ProjectOnPlane(view.transform.right, Vector3.up).normalized;
                if (a4ViewerRight.sqrMagnitude < .9f) return RejectA4Corner("종이의 앞쪽에서 고개를 바로 하고 다시 짚으세요.");
            }
            var error = ValidateA4Corner(point);
            if (error != null) return RejectA4Corner(error);
            Bounds original = default;
            if (a4Count == 3 && !TryGetTabletopOriginalBounds(out original))
                return RejectA4Corner("원본 모델을 준비 중입니다. 준비되면 뒤왼쪽에서 A를 다시 누르세요.");
            a4Corners[a4Count++] = point;
            a4Status = "";
            if (a4Count == 4)
            {
                MeasureA4Surface();
                var bottomCenter = new Vector3(original.center.x, original.min.y, original.center.z);
                assemblyReference = new Pose(a4Surface.position - a4Surface.rotation * bottomCenter, a4Surface.rotation);
                a4PoseApplied = true;
                assemblyAlignment = AssemblyAlignment.Preview;
                Debug.Log($"ContextPin A4: measured {a4Size.x:F3} x {a4Size.y:F3} m · V1 center preview");
            }
            else Debug.Log($"ContextPin A4: corner {a4Count}/4 accepted");
            UpdateA4Marks();
            PositionMeetingModel();
            Draw();
            return true;
        }

        bool RejectA4Corner(string message)
        {
            a4Status = message;
            Draw();
            return false;
        }

        string ValidateA4Corner(Vector3 point)
        {
            if (!float.IsFinite(point.sqrMagnitude)) return "표시점 위치를 다시 확인하세요.";
            for (int i = 0; i < a4Count; i++)
            {
                if (Mathf.Abs(point.y - a4Corners[i].y) > A4HeightTolerance)
                    return "높이 차이가 큽니다. 표시점을 같은 종이 표면에 대고 다시 짚으세요.";
                if (Horizontal(point - a4Corners[i]).sqrMagnitude < .04f * .04f)
                    return "이미 짚은 점입니다. 다음 모서리를 짚으세요.";
            }
            if (a4Count == 0) return null;
            var front = Horizontal((a4Count == 1 ? point : a4Corners[1]) - a4Corners[0]);
            if (a4Count == 1)
            {
                if (Vector3.Dot(front.normalized, a4ViewerRight) < .35f)
                    return "앞왼쪽에서 오른쪽에 있는 앞오른쪽 모서리를 짚으세요.";
                if (Mathf.Min(Mathf.Abs(front.magnitude - A4Short), Mathf.Abs(front.magnitude - A4Long)) > A4EdgeTolerance)
                    return "A4 변 길이와 맞지 않습니다. 앞오른쪽 모서리에서 다시 짚으세요.";
                return null;
            }
            float width = Mathf.Abs(front.magnitude - A4Short) < Mathf.Abs(front.magnitude - A4Long) ? A4Short : A4Long;
            float depth = width == A4Short ? A4Long : A4Short;
            var side = Horizontal((a4Count == 2 ? point : a4Corners[2]) - a4Corners[1]);
            if (!A4RightTurn(front, side) || Mathf.Abs(side.magnitude - depth) > A4EdgeTolerance)
                return "앞오른쪽에서 종이 뒤쪽으로 직각인 뒤오른쪽 모서리를 짚으세요.";
            if (a4Count == 2) return null;
            var rear = Horizontal(point - a4Corners[2]);
            var left = Horizontal(a4Corners[0] - point);
            float diagonal1 = Horizontal(a4Corners[2] - a4Corners[0]).magnitude;
            float diagonal2 = Horizontal(point - a4Corners[1]).magnitude;
            if (!A4RightTurn(side, rear) || !A4RightTurn(rear, left) || !A4RightTurn(left, front) ||
                Mathf.Abs(rear.magnitude - width) > A4EdgeTolerance || Mathf.Abs(left.magnitude - depth) > A4EdgeTolerance ||
                Mathf.Abs(front.magnitude - rear.magnitude) > .025f || Mathf.Abs(side.magnitude - left.magnitude) > .025f ||
                Mathf.Abs(diagonal1 - diagonal2) > .028f)
                return "네 점이 A4 사각형과 맞지 않습니다. 뒤왼쪽을 다시 짚거나 이전 점을 취소하세요.";
            return null;
        }

        static Vector3 Horizontal(Vector3 delta) => new Vector3(delta.x, 0, delta.z);

        static bool A4RightTurn(Vector3 edge, Vector3 next)
        {
            return Vector3.Cross(edge, next).y < 0 &&
                Mathf.Abs(Vector3.Dot(edge.normalized, next.normalized)) <= A4MaxCornerCosine;
        }

        void MeasureA4Surface()
        {
            var center = (a4Corners[0] + a4Corners[1] + a4Corners[2] + a4Corners[3]) * .25f;
            var right = Horizontal((a4Corners[1] - a4Corners[0]) + (a4Corners[2] - a4Corners[3])).normalized;
            var forward = Vector3.Cross(right, Vector3.up);
            a4Surface = new Pose(center, Quaternion.LookRotation(forward, Vector3.up));
            float width = Vector3.Dot(right, (a4Corners[1] - a4Corners[0]) + (a4Corners[2] - a4Corners[3])) * .5f;
            float depth = Vector3.Dot(forward, (a4Corners[2] - a4Corners[1]) + (a4Corners[3] - a4Corners[0])) * .5f;
            // With hand-sampled corners the quadrilateral can be slightly skew. Inscribe the
            // rectangle in all four measured edges so models never use unmeasured desk space.
            float scale = 1;
            for (int i = 0; i < 4; i++)
            {
                var inward = Vector3.Cross(Horizontal(a4Corners[(i + 1) % 4] - a4Corners[i]), Vector3.up).normalized;
                float radius = (width * Mathf.Abs(Vector3.Dot(inward, right)) + depth * Mathf.Abs(Vector3.Dot(inward, forward))) * .5f;
                scale = Mathf.Min(scale, Vector3.Dot(inward, center - a4Corners[i]) / radius);
            }
            a4Size = new Vector2(width, depth) * scale;
        }

        void UndoA4Corner()
        {
            if ((!A4PointSampling && !A4PreviewActive) || a4Count == 0) return;
            assemblyAlignment = AssemblyAlignment.CaptureA4;
            a4PoseApplied = false;
            a4Count--;
            a4Status = "";
            UpdateA4Marks();
            PositionMeetingModel();
            Draw();
        }

        bool ConfirmA4ReviewArea()
        {
            if (!A4PreviewActive) return true;
            if (!StoreReviewTable(a4Surface, a4Size, true, false, true)) return false;
            HideA4Marks();
            Debug.Log("ContextPin A4: confirmed · measured paper surface");
            return true;
        }

        LineRenderer MakeA4Line(string name, float width)
        {
            var line = new GameObject(name).AddComponent<LineRenderer>();
            line.transform.SetParent(transform, false);
            line.sharedMaterial = probe.GetComponent<Renderer>().sharedMaterial;
            line.useWorldSpace = true;
            line.startWidth = line.endWidth = width;
            return line;
        }

        void UpdateA4Marks()
        {
            var lift = Vector3.up * .002f;
            for (int i = 0; i < a4Marks.Length; i++)
            {
                if (i < a4Count)
                {
                    if (a4Marks[i] == null)
                    {
                        var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                        dot.name = $"A4Corner{i + 1}";
                        dot.transform.SetParent(transform, false);
                        var collider = dot.GetComponent<Collider>();
                        collider.enabled = false;
                        Destroy(collider);
                        dot.GetComponent<Renderer>().sharedMaterial = probe.GetComponent<Renderer>().sharedMaterial;
                        a4Marks[i] = dot.transform;
                    }
                    a4Marks[i].position = a4Corners[i] + lift;
                    a4Marks[i].localScale = Vector3.one * .008f;
                }
                if (a4Marks[i] != null) a4Marks[i].gameObject.SetActive(i < a4Count);
            }
            if (a4Count >= 2)
            {
                if (a4Border == null) a4Border = MakeA4Line("A4MeasuredOutline", .0025f);
                a4Border.positionCount = a4Count;
                a4Border.loop = a4Count == 4;
                for (int i = 0; i < a4Count; i++) a4Border.SetPosition(i, a4Corners[i] + lift);
            }
            if (a4Border != null) a4Border.gameObject.SetActive(a4Count >= 2);
            if (a4Count == 4)
            {
                if (a4CenterCross == null) a4CenterCross = MakeA4Line("A4PhysicalCenter", .003f);
                var center = a4Surface.position + lift;
                var right = a4Surface.rotation * Vector3.right * .028f;
                var back = a4Surface.rotation * Vector3.forward * .028f;
                a4CenterCross.positionCount = 5;
                a4CenterCross.SetPositions(new[] {center - right, center + right, center, center - back, center + back});
            }
            if (a4CenterCross != null) a4CenterCross.gameObject.SetActive(a4Count == 4);
        }

        void HideA4Marks()
        {
            foreach (var mark in a4Marks)
                if (mark != null) mark.gameObject.SetActive(false);
            if (a4Border != null) a4Border.gameObject.SetActive(false);
            if (a4CenterCross != null) a4CenterCross.gameObject.SetActive(false);
        }

        void ClearA4Alignment()
        {
            if (tableA4Measured) ClearReviewTable();
            a4Count = 0;
            a4Status = "";
            a4PoseApplied = false;
            HideA4Marks();
        }
    }
}
