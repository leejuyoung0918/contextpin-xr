export function randomId() {
  return Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2, "0")).join("");
}

export class ApiError extends Error {
  constructor(status, payload) {
    const message = payload?.detail ?? payload?.error ?? payload?.message;
    super(typeof message === "string" ? message : `EDGE 요청 실패 (${status})`);
    this.status = status;
  }
}

export async function request(path, { token, body, method, responseType = "json", signal } = {}) {
  const url = new URL(path, location.origin);
  if (url.origin !== location.origin) throw new Error("현재 EDGE 주소의 파일만 열 수 있습니다.");
  const headers = new Headers();
  if (token) headers.set("Authorization", "Bearer " + token);
  if (body && !(body instanceof FormData) && !(body instanceof Blob)) {
    headers.set("Content-Type", "application/json");
    body = JSON.stringify(body);
  }
  const response = await fetch(url, { method: method || (body ? "POST" : "GET"), headers, body, signal });
  if (!response.ok) throw new ApiError(response.status, await response.json().catch(() => ({})));
  if (responseType === "json") return response.json();
  return response[responseType]();
}

export function openSocket(path) {
  const url = new URL(path, location.origin);
  if (url.origin !== location.origin) throw new Error("현재 EDGE 주소로만 연결할 수 있습니다.");
  url.protocol = location.protocol === "https:" ? "wss:" : "ws:";
  return new WebSocket(url);
}

export class MeetingConnection {
  constructor(identity, handlers) {
    this.identity = identity;
    this.path = "/v1/sessions/" + encodeURIComponent(identity.session_id);
    this.lastSeq = -1;
    this.handlers = handlers;
  }

  start(snapshot) {
    if (snapshot) this.accept(snapshot);
    this.refresh().catch(error => {
      if (error instanceof ApiError && error.status === 401 && !this.closed) this.handlers.invalid();
    });
    this.connect();
    this.syncClock();
    this.clockTimer = setInterval(() => this.syncClock(), 25000);
  }

  refresh() {
    return this.get(this.path).then(result => this.accept(result.snapshot ?? result));
  }

  get(path, responseType = "json", signal) {
    return request(path, { token: this.identity.token, responseType, signal });
  }

  upload(path, body, signal) {
    return request(path, { token: this.identity.token, body, signal });
  }

  accept(snapshot) {
    if (this.closed || !snapshot?.session_id || snapshot.last_seq < this.lastSeq) return;
    this.lastSeq = snapshot.last_seq;
    this.handlers.snapshot(snapshot);
  }

  connect() {
    if (this.closed) return;
    this.handlers.connection(false, this.lastSeq >= 0 ? "다시 연결 중" : "세션 연결 중");
    const socket = openSocket(this.path + "/events");
    this.socket = socket;
    const handshake = setTimeout(() => socket.close(), 7000);
    socket.addEventListener("open", () => {
      socket.send(JSON.stringify({ token: this.identity.token, after_seq: Math.max(0, this.lastSeq) }));
    });
    socket.addEventListener("message", message => {
      const data = JSON.parse(message.data);
      if (data.type === "snapshot") this.lastEventSeq = data.snapshot.last_seq;
      if (data.event && data.event.server_seq > (this.lastEventSeq ?? -1)) {
        this.lastEventSeq = data.event.server_seq;
        this.handlers.event?.(data.event, data.snapshot);
      }
      if (data.snapshot) {
        clearTimeout(handshake);
        this.handlers.connection(true, "EDGE 연결됨");
        this.accept(data.snapshot);
      }
      if (data.type === "transient" || data.type === "transcript.partial") this.handlers.transient(data);
    });
    socket.addEventListener("close", error => {
      clearTimeout(handshake);
      if (this.closed) return;
      if (error.code === 4401 || error.code === 4403 || error.code === 1008) {
        this.handlers.invalid();
        return;
      }
      this.handlers.connection(false, "연결 끊김 · 재시도 중");
      this.reconnectTimer = setTimeout(() => this.connect(), 1500);
    });
  }

  async syncClock() {
    let best;
    for (let index = 0; index < 3 && !this.closed; index++) {
      const start = performance.now();
      try {
        const result = await this.get(this.path + "/clock");
        const end = performance.now();
        if (!Number.isFinite(result.session_time_ms)) continue;
        const sample = {
          clock_offset_ms: result.session_time_ms - (start + end) / 2,
          clock_uncertainty_ms: (end - start) / 2,
        };
        if (!best || sample.clock_uncertainty_ms < best.clock_uncertainty_ms) best = sample;
      } catch {
        break;
      }
    }
    this.clock = best;
  }

  async command(type, payload) {
    const body = {
      command_id: randomId(),
      type,
      payload: { ...payload, ...this.clock, source_monotonic_ms: performance.now() },
    };
    this.handlers.pending(1);
    try {
      for (const delay of [0, 450, 900, 1800, 3600]) {
        if (delay) await new Promise(resolve => setTimeout(resolve, delay));
        if (this.closed) throw new Error("회의 연결이 종료되었습니다.");
        try {
          const result = await request(this.path + "/commands", { token: this.identity.token, body });
          if (result.snapshot) this.accept(result.snapshot);
          else if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
            this.refresh().catch(() => {});
          }
          return result;
        } catch (error) {
          if (error instanceof ApiError) {
            if (error.status === 409) this.refresh().catch(() => {});
            throw error;
          }
        }
      }
      throw new Error("EDGE에서 처리 여부를 확인하지 못했습니다. 연결 후 회의 상태를 확인하세요.");
    } finally {
      this.handlers.pending(-1);
    }
  }

  close() {
    this.closed = true;
    clearTimeout(this.reconnectTimer);
    clearInterval(this.clockTimer);
    this.socket?.close();
  }
}
