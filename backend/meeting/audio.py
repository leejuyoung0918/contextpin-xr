"""Time-aligned PCM framing, short VAD segments and WAV fallback."""
import array
import io
import math
import os
import re
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
        self.first_voiced = 0
        self.last_voiced = 0
        self.stamp = clock_stamp({**body, "source_monotonic_ms": body.get("stream_start_source_monotonic_ms")}, now_ms())
        self.started_ms = self.stamp["captured_session_time_ms"]
        self.opened = False
        self.reported_sample = 0
        self.open_design_range = None
        self.command_pcm = None
        self.design_marks = {}
        self.speech_overlap = body.get("edit_marker_alignment") == "speech_overlap"
        self.actor, self.device = coordinator.audio_start(self.sid, body["token"], stream_id, body.get("device_id"),
                                                          continuous=True, sample_rate=self.rate,
                                                          marker_overlap=self.speech_overlap, clock_origin_ms=self.started_ms)
        self.view_actor = self.actor if body.get("bind_view") is True else None

    def add(self, frame):
        if len(frame) < 18:
            raise MeetingError("잘못된 오디오 프레임입니다.")
        seq, sample, count = struct.unpack_from("<IQI", frame)
        if len(frame) != 16 + 2 * count or not 0 < count <= self.rate // 2:
            raise MeetingError("프레임의 샘플 크기가 다릅니다.")
        if seq < self.expected_sequence or sample < self.expected_sample:
            return
        if sample - self.expected_sample > self.rate * 3600:
            raise MeetingError("PCM 시각이 마지막 수신 샘플에서 한 시간 넘게 건너뛰었습니다.")
        if not self.opened:
            self.started_ms = self.coordinator.audio_opened(self.sid, self.stream_id, self.started_ms)
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
        if self.command_pcm is not None:
            if len(self.command_pcm) + len(pcm) <= 2 * self.rate * MAX_SECONDS * 5:
                self.command_pcm.extend(pcm)
            else:
                self.command_pcm = None
        # A packet can straddle the 15-second STT limit. Feed the remainder to the next segment.
        while pcm:
            buffered = len(self.buffer) + len(self.preroll)
            take = min(len(pcm), self.duration_max * 2 - buffered)
            self._append_pcm(sample, pcm[:take])
            sample += take // 2
            pcm = pcm[take:]

    def _append_pcm(self, sample, pcm):
        count = len(pcm) // 2
        through = sample + count
        values = array.array("h", pcm)
        if sys.byteorder != "little":
            values.byteswap()
        voiced = math.sqrt(sum(value * value for value in values) / count) > 280
        if voiced:
            if self.start_sample is None:
                self.start_sample = sample - len(self.preroll) // 2
                self.first_voiced = sample
                self.buffer.extend(self.preroll)
                self.preroll.clear()
            self.last_voiced = through
        if self.start_sample is not None:
            self.buffer.extend(pcm)
            if through - self.last_voiced >= self.silence_limit or through - self.start_sample >= self.duration_max:
                self.finish(through)
        else:
            self.preroll.extend(pcm)
            del self.preroll[:-2 * self.pre_limit]

    def design_boundary(self, packet):
        rid, sample, edge = packet.get("range_id"), packet.get("sample_index"), packet.get("edge")
        if (not isinstance(rid, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,100}", rid)
                or type(sample) is not int or edge not in ("start", "end")):
            raise MeetingError("수정 발언 구간 정보를 확인하세요.")
        key = (rid, edge)
        if key in self.design_marks and self.design_marks[key] == sample:
            return
        if (sample != self.expected_sample or key in self.design_marks
                or (edge == "start" and self.open_design_range is not None)
                or (edge == "end" and self.open_design_range != rid)):
            raise MeetingError("발언 경계와 서버가 수신한 PCM 샘플이 일치하지 않습니다.", 409)
        sequences = self.segment
        if self.speech_overlap:
            # A marker has no effect on the VAD or its STT input. An utterance still
            # in progress when the window closes may finish with later PCM or EOS.
            if edge == "end" and self.start_sample is not None and self.last_voiced > self.design_marks[(rid, "start")]:
                sequences += 1
        else:
            # Older headsets require an immediate result without sending trailing PCM.
            self.finish(sample)
            self.preroll.clear()
            sequences = self.segment
        wav = encode_wav(self.command_pcm, self.rate) if edge == "end" and self.command_pcm else None
        bundle = self.coordinator.audio_boundary(self.sid, self.stream_id, self.actor, rid, edge == "start",
                                                  sample, sequences, wav, packet.get("base_version_id"))
        self.command_pcm = bytearray() if bundle and edge == "start" else None
        self.open_design_range = rid if edge == "start" else None
        self.design_marks[key] = sample

    def finish(self, end_sample=None):
        if self.start_sample is None:
            return
        end_sample = min(self.expected_sample if end_sample is None else end_sample,
                         self.start_sample + len(self.buffer) // 2, self.start_sample + self.duration_max)
        start_sample = self.start_sample
        pcm = self.buffer[:2 * (end_sample - start_sample)]
        self.start_sample = None
        self.buffer.clear()
        if not self.speech_overlap and self.last_voiced - start_sample < self.rate // 8:
            return
        start = self.started_ms + round(start_sample / self.rate * 1000)
        end = self.started_ms + round(end_sample / self.rate * 1000)
        cutoff = self.coordinator.audio_cutoff(self.sid, self.stream_id)
        if cutoff is not None and end > cutoff:
            if start >= cutoff:
                return
            pcm = pcm[:2 * round((cutoff - start) * self.rate / 1000)]
            end = cutoff
        stamp = {k: v for k, v in self.stamp.items() if k not in ("received_at", "captured_session_time_ms")}
        self.segment += 1
        stamp["start_sample"] = start_sample
        stamp["end_sample"] = start_sample + len(pcm) // 2
        stamp["voice_start_sample"] = self.first_voiced
        stamp["voice_end_sample"] = min(self.last_voiced, stamp["end_sample"])
        stamp["voice_onset_ms"] = self.started_ms + round(self.first_voiced / self.rate * 1000)
        wav = encode_wav(pcm, self.rate)
        if self.speech_overlap:
            start = stamp["voice_onset_ms"]
            end = self.started_ms + round(stamp["voice_end_sample"] / self.rate * 1000)
            if stamp["voice_end_sample"] - self.first_voiced < self.rate // 8:
                # Close the reserved sequence even when the only sound was a short click.
                wav = None
        self.coordinator.audio_submit(self.sid, self.stream_id, self.segment, start, end,
                                      wav, self.actor, self.device, self.view_actor, stamp)

    def close(self, eos=False, last_sequence=None, pending_request=None, pending_selection=None):
        try:
            self.finish()
        finally:
            self.coordinator.audio_stop(self.sid, self.stream_id, eos, self.expected_sample,
                                        last_sequence, self.expected_sequence - 1, pending_request, pending_selection)
