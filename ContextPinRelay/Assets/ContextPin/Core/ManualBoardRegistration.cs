using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ContextPin
{
    [Serializable]
    public sealed class BoardRegistration
    {
        public string id, boardInstanceId, profileId, profileHash, role, source, method, status, precision;
        public int profileRevision, revision, trackingEpoch;
        public int retryCount;
        public long elapsedMs;
        public string registeredUtc, verifiedBy;
        public string anchorId, anchorStatus, sourceCaptureId;
        public BoardVector origin, axisX, axisY, axisZ;
        public float widthErrorMeters, lengthErrorMeters, skewDegrees;
        public float cornerErrorMeters = -1, middleErrorMeters = -1;
        public float reprojectionErrorPixels = -1;

        public bool Matches(LabBoardDefinition board) => board != null && board.IsValidVersion &&
            profileId == board.id && profileRevision == board.revision && profileHash == board.contentHash;

        public BoardVector ToWorld(BoardVector local) => new BoardVector(origin.Vector +
            axisX.Vector * local.x + axisY.Vector * local.y + axisZ.Vector * local.z);

        public BoardVector ToBoard(BoardVector world)
        {
            var relative = world.Vector - origin.Vector;
            return new BoardVector(Vector3.Dot(relative, axisX.Vector), Vector3.Dot(relative, axisY.Vector),
                Vector3.Dot(relative, axisZ.Vector));
        }

        public BoardRegistration Copy() => (BoardRegistration)MemberwiseClone();

        public bool IsCurrent(string instanceId, int expectedRevision, int epoch) => status == "valid" &&
            boardInstanceId == instanceId && revision == expectedRevision && trackingEpoch == epoch;
    }

    public static class ManualBoardRegistration
    {
        // Development acceptance limits for a region overlay. These are setup
        // consistency thresholds and are not measured input-device precision.
        public const float MaxCornerErrorMeters = .010f;
        public const float MaxSpanErrorMeters = .006f;
        const float MinimumNormalUp = .7071068f;
        const float MaxAbsoluteCosOfSpanAngle = .2164396f;

        public static bool TryFit(LabBoardDefinition board, IReadOnlyList<BoardVector> samples,
            out BoardRegistration registration, out string issue)
        {
            registration = null;
            issue = CheckInputs(board, samples);
            if (issue.Length > 0) return false;

            var origin = samples[0].Vector;
            var across = samples[1].Vector - origin;
            var along = samples[2].Vector - origin;
            float expectedWidth = board.references[1].boardLocal.x;
            float expectedLength = board.references[2].boardLocal.z;
            float widthError = Math.Abs(across.Length() - expectedWidth);
            float lengthError = Math.Abs(along.Length() - expectedLength);
            if (widthError > MaxSpanErrorMeters || lengthError > MaxSpanErrorMeters)
            {
                issue = "기준점 간격이 프로필과 맞지 않아요. A1, J1, A30과 보드 규격을 확인하세요.";
                return false;
            }
            var axisX = Vector3.Normalize(across);
            var measuredAlong = Vector3.Normalize(along);
            float cosine = Vector3.Dot(axisX, measuredAlong);
            if (Math.Abs(cosine) > MaxAbsoluteCosOfSpanAngle)
            {
                issue = "기준점이 직각을 이루지 않아요. 보드의 첫 줄과 마지막 줄 기준점을 확인하세요.";
                return false;
            }
            var axisY = Vector3.Normalize(Vector3.Cross(measuredAlong, axisX));
            if (Vector3.Dot(axisY, Vector3.UnitY) < MinimumNormalUp)
            {
                issue = "보드 윗면을 확인하세요. 1번 줄을 몸 쪽, 30번 줄을 반대쪽으로 두고 다시 맞추세요.";
                return false;
            }

            var fitted = new BoardRegistration
            {
                profileId = board.id, profileHash = board.contentHash, profileRevision = board.revision,
                method = "manual_index_contact", status = "requires_confirmation", precision = "REGION",
                origin = new BoardVector(origin), axisX = new BoardVector(axisX),
                axisY = new BoardVector(axisY), axisZ = new BoardVector(Vector3.Cross(axisX, axisY)),
                widthErrorMeters = widthError, lengthErrorMeters = lengthError,
                skewDegrees = (float)Math.Abs(Math.Asin(cosine) * 180 / Math.PI),
            };
            if (samples.Count >= 4) fitted.cornerErrorMeters = ErrorAt(board, samples, fitted, 3);
            if (samples.Count >= 5) fitted.middleErrorMeters = ErrorAt(board, samples, fitted, 4);
            if (fitted.cornerErrorMeters > MaxCornerErrorMeters || fitted.middleErrorMeters > MaxCornerErrorMeters)
            {
                issue = "검증점이 화면 참고 위치와 어긋나요. 보드를 고정하고 기준점을 다시 짚어 주세요.";
                return false;
            }
            registration = fitted;
            return true;
        }

        static float ErrorAt(LabBoardDefinition board, IReadOnlyList<BoardVector> samples, BoardRegistration fitted, int index) =>
            Vector3.Distance(samples[index].Vector, fitted.ToWorld(board.references[index].boardLocal).Vector);

        static string CheckInputs(LabBoardDefinition board, IReadOnlyList<BoardVector> samples)
        {
            if (board?.id != LabBoards.PhysicalId || !board.IsValidVersion)
                return "지원하는 실물 보드 프로필을 선택하세요.";
            if (samples == null || samples.Count < 3 || samples.Count > board.references.Length)
                return "기준점 세 곳부터 차례대로 짚어 주세요.";
            return samples.Any(sample => !Finite(sample.x) || !Finite(sample.y) || !Finite(sample.z))
                ? "손 추적 위치를 사용할 수 없어요. 다시 시도하세요." : "";
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public static bool Confirm(BoardRegistration registration, string instanceId, string role, string source,
            int revision, int epoch, DateTime utc)
        {
            if (registration == null || registration.status != "requires_confirmation" ||
                registration.cornerErrorMeters < 0 || registration.middleErrorMeters < 0 ||
                registration.cornerErrorMeters > MaxCornerErrorMeters || registration.middleErrorMeters > MaxCornerErrorMeters ||
                string.IsNullOrWhiteSpace(instanceId) || (role != "demonstration" && role != "practice") ||
                (source != "real" && source != "simulation") || revision <= 0 || epoch < 0)
                return false;

            registration.id = Guid.NewGuid().ToString("N");
            registration.boardInstanceId = instanceId;
            registration.role = role;
            registration.source = source;
            registration.status = "valid";
            registration.revision = revision;
            registration.trackingEpoch = epoch;
            registration.registeredUtc = utc.ToUniversalTime().ToString("O");
            registration.verifiedBy = role == "demonstration" ? "demonstrator_confirmation" : "learner_confirmation";
            return true;
        }
    }
}
