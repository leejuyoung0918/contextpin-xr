import { openSocket, randomId } from "./edge.js";

export function canRecord() {
  return Boolean(isSecureContext && navigator.mediaDevices?.getUserMedia && window.AudioContext && window.AudioWorkletNode);
}

const clipSeconds = 12;

function pcmWave(frames, rate, sampleCount) {
  const header = new DataView(new ArrayBuffer(44));
  function ascii(offset, value) {
    for (let index = 0; index < value.length; index++) header.setUint8(offset + index, value.charCodeAt(index));
  }
  ascii(0, "RIFF");
  header.setUint32(4, 36 + 2 * sampleCount, true);
  ascii(8, "WAVEfmt ");
  header.setUint32(16, 16, true);
  header.setUint16(20, 1, true);
  header.setUint16(22, 1, true);
  header.setUint32(24, rate, true);
  header.setUint32(28, 2 * rate, true);
  header.setUint16(32, 2, true);
  header.setUint16(34, 16, true);
  ascii(36, "data");
  header.setUint32(40, 2 * sampleCount, true);
  return new Blob([header, ...frames], { type: "audio/wav" });
}

export class MeetingMicrophone {
  constructor(session, { onFailure, onClosed, shortClip = false }) {
    this.session = session;
    this.onFailure = onFailure;
    this.onClosed = onClosed;
    this.frames = shortClip ? [] : null;
    this.sequence = 0;
    this.sampleIndex = 0;
  }

  async start(deviceId, bindView) {
    this.context = new AudioContext({ latencyHint: "interactive", sampleRate: 48000 });
    try {
      await this.context.resume();
      this.stream = await navigator.mediaDevices.getUserMedia({
        audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true },
      });
      if (this.closed) throw new Error("이 회의의 마이크 연결이 종료되었습니다.");
      await this.context.audioWorklet.addModule("/meeting/pcm.js");
      if (this.closed) throw new Error("이 회의의 마이크 연결이 종료되었습니다.");
      this.source = this.context.createMediaStreamSource(this.stream);
      this.processor = new AudioWorkletNode(this.context, "meeting-pcm");
      this.silent = this.context.createGain();
      this.silent.gain.value = 0;
      this.bindView = bindView;
      if (!this.frames) await this.startSocket(deviceId, bindView);
      if (this.closed) throw new Error("이 회의의 마이크 연결이 종료되었습니다.");
      for (const track of this.stream.getAudioTracks()) {
        track.addEventListener("ended", () => this.fail("마이크 사용이 중단되었습니다."), { once: true });
      }
      this.processor.port.onmessage = message => {
        if (message.data === "flushed") this.flush();
        else this.send(message.data);
      };
      this.processor.connect(this.silent).connect(this.context.destination);
      const offset = this.session.clock?.clock_offset_ms;
      if (Number.isFinite(offset)) this.startMs = Math.round(performance.now() + offset);
      this.source.connect(this.processor);
      if (this.frames) this.clipTimer = setTimeout(() => this.stop(), clipSeconds * 1000);
    } catch (error) {
      if (this.closed) this.releaseDevice();
      else this.close();
      throw error;
    }
  }

  async startSocket(deviceId, bindView) {
    const streamId = randomId();
    const socket = openSocket("/v1/audio-streams/" + streamId);
    this.socket = socket;
    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("마이크 연결 시간이 초과되었습니다.")), 6000);
      socket.addEventListener("open", () => { clearTimeout(timer); resolve(); }, { once: true });
      socket.addEventListener("error", () => { clearTimeout(timer); reject(new Error("EDGE에 마이크를 연결할 수 없습니다.")); }, { once: true });
    });
    const authenticated = new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("EDGE에서 마이크를 열지 못했습니다.")), 6000);
      const check = event => {
        const data = JSON.parse(event.data);
        if (data.type !== "ready" && data.type !== "error") return;
        clearTimeout(timer);
        socket.removeEventListener("message", check);
        if (data.type === "ready") resolve();
        else reject(new Error(data.message || "음성 연결이 거절되었습니다."));
      };
      socket.addEventListener("message", check);
      socket.addEventListener("close", () => {
        clearTimeout(timer);
        reject(new Error("마이크 연결이 끊겼습니다."));
      }, { once: true });
    });
    socket.addEventListener("close", () => {
      if (!this.stopping && !this.closed) this.fail("마이크 연결이 끊겼습니다.");
      this.close();
    });
    socket.send(JSON.stringify({
      token: this.session.identity.token,
      session_id: this.session.identity.session_id,
      device_id: deviceId,
      stream_id: streamId,
      bind_view: bindView,
      codec: "pcm_s16le",
      channels: 1,
      actual_sample_rate_hz: this.context.sampleRate,
      stream_start_source_monotonic_ms: performance.now(),
      stream_start_sample_index: 0,
      ...this.session.clock,
    }));
    await authenticated;
    if (this.closed) throw new Error("이 회의의 마이크 연결이 종료되었습니다.");
    socket.addEventListener("message", event => {
      const data = JSON.parse(event.data);
      if (data.type === "error") this.fail(data.message || "음성 처리가 중단되었습니다.");
      if (data.type === "stopped") this.close();
    });

  }

  send(samples) {
    if (this.closed) return;
    if (this.frames) {
      const limit = this.context.sampleRate * clipSeconds;
      const segment = samples.subarray(0, limit - this.sampleIndex);
      if (segment.length) this.frames.push(segment);
      this.sampleIndex += segment.length;
      if (this.sampleIndex >= limit) this.stop();
      return;
    }
    if (this.socket.readyState !== WebSocket.OPEN) return;
    if (this.socket.bufferedAmount > 500000) {
      this.fail("음성 전송이 밀려 녹음을 중단했습니다.");
      return;
    }
    const frame = new ArrayBuffer(16 + samples.length * 2);
    const view = new DataView(frame);
    view.setUint32(0, this.sequence++, true);
    view.setBigUint64(4, BigInt(this.sampleIndex), true);
    view.setUint32(12, samples.length, true);
    for (let index = 0; index < samples.length; index++) view.setInt16(16 + index * 2, samples[index], true);
    this.sampleIndex += samples.length;
    this.socket.send(frame);
  }

  stop() {
    if (this.closed || this.stopping) return;
    this.stopping = true;
    clearTimeout(this.clipTimer);
    this.processor?.port.postMessage("flush");
    this.flushTimer = setTimeout(() => this.flush(), 300);
  }

  flush() {
    if (this.closed || this.flushed) return;
    this.flushed = true;
    clearTimeout(this.flushTimer);
    this.releaseDevice();
    if (this.frames) {
      this.submitClip();
    } else if (this.socket.readyState === WebSocket.OPEN) {
      this.socket.send(JSON.stringify({ type: "stop" }));
      this.stopTimer = setTimeout(() => this.close(), 8000);
    } else this.close();
  }

  async submitClip() {
    if (!this.sampleIndex) {
      this.fail("녹음된 음성이 없습니다.");
      return;
    }
    const query = new URLSearchParams({ bind_view: String(this.bindView) });
    if (this.startMs !== undefined) query.set("start_ms", this.startMs);
    const wave = pcmWave(this.frames, this.context.sampleRate, this.sampleIndex);
    this.frames = [];
    this.upload = new AbortController();
    try {
      await this.session.upload(this.session.path + "/audio?" + query, wave, this.upload.signal);
      this.close();
    } catch (error) {
      if (!this.closed) this.fail(error.message || "녹음을 보낼 수 없습니다.");
    }
  }

  releaseDevice() {
    this.source?.disconnect();
    this.processor?.disconnect();
    this.stream?.getTracks().forEach(track => track.stop());
    if (this.context?.state !== "closed") this.context?.close().catch(() => {});
  }

  fail(message) {
    if (this.closed) return;
    this.close();
    this.onFailure(message);
  }

  close() {
    if (this.closed) return;
    this.closed = true;
    clearTimeout(this.stopTimer);
    clearTimeout(this.flushTimer);
    clearTimeout(this.clipTimer);
    this.upload?.abort();
    if (!this.flushed) this.releaseDevice();
    this.socket?.close();
    this.onClosed?.();
  }
}
