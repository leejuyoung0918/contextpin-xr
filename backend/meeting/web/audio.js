import { openSocket, randomId } from "./edge.js";

export function canRecord() {
  return Boolean(isSecureContext && navigator.mediaDevices?.getUserMedia && window.AudioContext && window.AudioWorkletNode);
}

export class MeetingMicrophone {
  constructor(session, onFailure) {
    this.session = session;
    this.onFailure = onFailure;
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
      for (const track of this.stream.getAudioTracks()) {
        track.addEventListener("ended", () => this.fail("마이크 사용이 중단되었습니다."), { once: true });
      }
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
      this.processor.port.onmessage = message => {
        if (message.data === "flushed") this.flush();
        else this.send(message.data);
      };
      this.processor.connect(this.silent).connect(this.context.destination);
      this.source.connect(this.processor);
    } catch (error) {
      if (this.closed) this.releaseDevice();
      else this.close();
      throw error;
    }
  }

  send(samples) {
    if (this.closed || this.socket.readyState !== WebSocket.OPEN) return;
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
    this.processor?.port.postMessage("flush");
    this.flushTimer = setTimeout(() => this.flush(), 300);
  }

  flush() {
    if (this.closed || this.flushed) return;
    this.flushed = true;
    clearTimeout(this.flushTimer);
    this.releaseDevice();
    if (this.socket.readyState === WebSocket.OPEN) {
      this.socket.send(JSON.stringify({ type: "stop" }));
      this.stopTimer = setTimeout(() => this.close(), 8000);
    } else this.close();
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
    if (!this.flushed) this.releaseDevice();
    this.socket?.close();
  }
}
