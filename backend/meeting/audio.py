"""Time-aligned PCM framing, short VAD segments and WAV fallback."""
import array
import io
import math
import os
import struct
import sys
import wave

from .contracts import MeetingError, clock_stamp, now_ms

MAX_SECONDS = 15
MAX_WAV = 3 * 1024 * 1024


def encode_wav(pcm, rate):
    with io.BytesIO() as output:
        with wave.open(output, "wb") as writer:
            writer.setnchannels(1)
            writer.setsampwidth(2)
            writer.setframerate(rate)
            writer.writeframes(pcm)
        return output.getvalue()


def inspect_wav(data):
    try:
        with wave.open(io.BytesIO(data), "rb") as reader:
            rate = reader.getframerate()
            count = reader.getnframes()
            if (reader.getnchannels() != 1 or reader.getsampwidth() != 2 or reader.getcomptype() != "NONE"
                    or not 8000 <= rate <= 48000 or not 0 < count <= rate * MAX_SECONDS
                    or len(reader.readframes(count)) != count * 2):
                raise ValueError()
    except (ValueError, EOFError, wave.Error) as error:
        raise MeetingError("최대 15초의 mono PCM16 WAV를 보내 주세요.", 415) from error
    return round(count / rate * 1000)


class AudioCapture:
    def __init__(self, coordinator, stream_id, body):
        self.rate = body.get("actual_sample_rate_hz")
        if (type(self.rate) is not int or not 8000 <= self.rate <= 48000 or body.get("channels") != 1
                or body.get("codec") != "pcm_s16le" or body.get("stream_start_sample_index") != 0
                or body.get("stream_id", stream_id) != stream_id):
            raise MeetingError("PCM16 mono 오디오 연결 정보를 확인해 주세요.")
        self.sid = body.get("session_id")
        if type(self.sid) is not str or type(body.get("token")) is not str:
            raise MeetingError("오디오 참가 인증이 필요합니다.", 401)
        self.coordinator = coordinator
        self.stream_id = stream_id
        self.duration_max = self.rate * MAX_SECONDS
        self.silence_limit = int(self.rate * max(120, min(2000, int(os.environ.get("VAD_END_SILENCE_MS", "700")))) / 1000)
        self.pre_limit = self.rate // 4
        self.buffer = bytearray()
        self.preroll = bytearray()
        self.expected_sequence = 0
        self.expected_sample = 0
        self.segment = 0
        self.start_sample = None
        self.last_voiced = 0
        self.stamp = clock_stamp({**body, "source_monotonic_ms": body.get("stream_start_source_monotonic_ms")}, now_ms())
        self.started_ms = self.stamp["captured_session_time_ms"]
        self.opened = False
        self.reported_sample = 0
        self.actor, self.device = coordinator.audio_start(self.sid, body["token"], stream_id, body.get("device_id"),
                                                          continuous=True, sample_rate=self.rate)
        self.view_actor = self.actor if body.get("bind_view") is True else None

    def add(self, frame):
        if len(frame) < 18:
            raise MeetingError("잘못된 오디오 프레임입니다.")
        seq, sample, count = struct.unpack_from("<IQI", frame)
        if len(frame) != 16 + 2 * count or not 0 < count <= self.rate // 2:
            raise MeetingError("프레임의 샘플 크기가 다릅니다.")
        if seq < self.expected_sequence or sample < self.expected_sample:
            return
        if sample > self.rate * 60 * 60:
            raise MeetingError("한 시간 이상 녹음하려면 새 녹음을 시작해 주세요.")
        if not self.opened:
            self.coordinator.audio_opened(self.sid, self.stream_id, self.started_ms)
            self.opened = True
        if seq != self.expected_sequence or sample != self.expected_sample:
            self.finish(self.expected_sample)
            self.preroll.clear()
            self.coordinator.audio_gap(self.sid, self.stream_id, sample, "non_contiguous_pcm", self.expected_sample)
        self.expected_sequence = seq + 1
        self.expected_sample = sample + count
        if self.expected_sample - self.reported_sample >= self.rate:
            self.coordinator.audio_progress(self.sid, self.stream_id, self.expected_sample)
            self.reported_sample = self.expected_sample
        pcm = frame[16:]
        values = array.array("h", pcm)
        if sys.byteorder != "little":
            values.byteswap()
        voiced = math.sqrt(sum(value * value for value in values) / count) > 280
        if voiced:
            if self.start_sample is None:
                self.start_sample = sample - len(self.preroll) // 2
                self.buffer.extend(self.preroll)
                self.preroll.clear()
            self.last_voiced = self.expected_sample
        if self.start_sample is not None:
            self.buffer.extend(pcm)
            if self.expected_sample - self.last_voiced >= self.silence_limit or self.expected_sample - self.start_sample >= self.duration_max:
                self.finish(self.expected_sample)
        else:
            self.preroll.extend(pcm)
            del self.preroll[:-2 * self.pre_limit]

    def finish(self, end_sample=None):
        if self.start_sample is None:
            return
        end_sample = min(self.expected_sample if end_sample is None else end_sample,
                         self.start_sample + len(self.buffer) // 2, self.start_sample + self.duration_max)
        start_sample = self.start_sample
        pcm = self.buffer[:2 * (end_sample - start_sample)]
        self.start_sample = None
        self.buffer.clear()
        if self.last_voiced - start_sample < self.rate // 8:
            return
        self.segment += 1
        start = self.started_ms + round(start_sample / self.rate * 1000)
        end = self.started_ms + round(end_sample / self.rate * 1000)
        cutoff = self.coordinator.audio_cutoff(self.sid, self.stream_id)
        if cutoff is not None and end > cutoff:
            if start >= cutoff:
                return
            pcm = pcm[:2 * round((cutoff - start) * self.rate / 1000)]
            end = cutoff
        self.coordinator.audio_submit(self.sid, self.stream_id, self.segment, start, end,
                                      encode_wav(pcm, self.rate), self.actor, self.device, self.view_actor,
                                      {k: v for k, v in self.stamp.items() if k not in ("received_at", "captured_session_time_ms")})

    def close(self, eos=False, last_sequence=None):
        try:
            self.finish()
        finally:
            self.coordinator.audio_stop(self.sid, self.stream_id, eos, self.expected_sample,
                                        last_sequence, self.expected_sequence - 1)
