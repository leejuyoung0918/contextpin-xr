using System;

namespace ContextPin
{
    public sealed partial class RelayApp
    {
        bool joiningByQr;

        void BeginJoinQr()
        {
            if (!xr || meetingBusy || joiningByQr) return;
            joiningByQr = true;
            assemblyAlignment = AssemblyAlignment.ScanQr;
            SetPassthrough(true);
            RequestQrTracker();
        }

        void UpdateJoinQr()
        {
            if (!joiningByQr || !RefreshQrTrackables()) return;
            var hint = QrScanHint;
            foreach (var item in assemblyQrTrackables)
            {
                if (item == null || (!item.IsTracked && !newlyObservedQr.Contains(item)) || item.TrackableType != OVRAnchor.TrackableType.QRCode) continue;
                var payload = ReadQrPayload(item.MarkerPayloadString, item.MarkerPayloadBytes);
                if (!TryReadMeetingJoin(payload, out var address, out var code))
                {
                    hint = string.IsNullOrEmpty(payload) ? "QR 내용을 읽는 중입니다. 잠시 그대로 바라보세요." :
                        payload.StartsWith("ContextPin:assembly:", StringComparison.Ordinal)
                            ? "받침 정렬 카드를 읽었습니다. 먼저 회의를 열고 ‘QR로 자동 정렬’을 누르세요."
                            : "참가 링크가 없는 QR입니다. PC 회의 화면의 참가 QR을 바라보세요.";
                    continue;
                }
                AcceptJoinQr(address, code);
                return;
            }
            SetAssemblyQrStatus(hint);
        }

        void AcceptJoinQr(string address, string code)
        {
            UnityEngine.Debug.Log("ContextPin QR [join] 참가 링크 판독 완료");
            StopJoinQr();
            meetingServerOverride = address;
            meetingJoinCode = code;
            BeginMeetingSession(true);
        }

        static bool TryReadMeetingJoin(string payload, out string address, out string code)
        {
            address = code = null;
            if (!Uri.TryCreate(payload, UriKind.Absolute, out var uri) || uri.IsLoopback || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath.TrimEnd('/') != "/meeting") return false;
            try { using var transport = new MeetingTransport(uri.GetLeftPart(UriPartial.Authority)); }
            catch (ArgumentException) { return false; }
            foreach (var part in uri.Query.TrimStart('?').Split('&'))
            {
                var pair = part.Split('=');
                if (pair.Length != 2 || pair[0] != "join") continue;
                var value = Uri.UnescapeDataString(pair[1]);
                if (code != null || value.Length != 6 || !Array.TrueForAll(value.ToCharArray(), ch => ch >= '0' && ch <= '9')) return false;
                code = value;
            }
            if (code == null) return false;
            address = uri.GetLeftPart(UriPartial.Authority);
            return true;
        }

        void StopJoinQr()
        {
            if (!joiningByQr) return;
            joiningByQr = false;
            notice = "";
            assemblyQrStatus = "";
            StopAssemblyQrTracker();
            assemblyAlignment = AssemblyAlignment.Unaligned;
        }
    }
}
