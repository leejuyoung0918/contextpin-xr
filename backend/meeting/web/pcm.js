class MeetingPcmProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this.limit = Math.max(1, Math.round(sampleRate * .04));
    this.chunk = new Int16Array(this.limit);
    this.used = 0;
    this.stopped = false;
    this.port.onmessage = message => {
      if (message.data !== "flush") return;
      this.stopped = true;
      this.send();
      this.port.postMessage("flushed");
    };
  }

  send() {
    if (!this.used) return;
    const frame = this.used === this.limit ? this.chunk : this.chunk.slice(0, this.used);
    this.port.postMessage(frame, [frame.buffer]);
    this.chunk = new Int16Array(this.limit);
    this.used = 0;
  }

  process(inputs) {
    if (this.stopped) return false;
    const channels = inputs[0];
    for (let index = 0; index < (channels[0]?.length || 0); index++) {
      let sample = 0;
      for (const channel of channels) sample += channel[index];
      sample = Math.max(-1, Math.min(1, sample / channels.length));
      this.chunk[this.used++] = Math.round(sample * (sample < 0 ? 32768 : 32767));
      if (this.used === this.limit) this.send();
    }
    return true;
  }
}

registerProcessor("meeting-pcm", MeetingPcmProcessor);
