import { canRecord, MeetingMicrophone } from "./audio.js";
import { byId } from "./panels.js";
import { markResultLoading, paintMeetingResult } from "./results.js";

const captureStates = new Set(["paused", "finalizing", "completed", "save_error"]);

function show(id, visible) {
  byId(id).hidden = !visible;
}

function write(id, text) {
  byId(id).textContent = text;
}

function preservedText(values) {
  if (typeof values === "string") return values;
  return (values || []).map(value => typeof value === "string" ? value : value?.label).filter(Boolean).join(" · ");
}

export class MeetingLifecycle {
  constructor(deviceId, { edge, caps, beforeUtterance, pendingDecision, notice, errorMessage, download }) {
    Object.assign(this, { deviceId, edge, caps, beforeUtterance, pendingDecision, notice, errorMessage, download });
    byId("meeting-manual").addEventListener("click", () => this.send("meeting_start", { mode: "live", input_mode: "manual" }));
    byId("meeting-main").addEventListener("click", () => this.main());
    byId("meeting-pause").addEventListener("click", () =>
      this.end(this.snapshot?.session_status === "paused" ? "meeting_end" : "meeting_pause"));
    byId("prepare-pc-mic").addEventListener("click", () => this.askMic(false));
    byId("pc-mic-allow").addEventListener("click", () => this.prepare());
    byId("result-close").addEventListener("click", () => {
      show("meeting-result", false);
      byId("meeting-main").focus();
    });
    byId("result-minutes").addEventListener("click", () => {
      const revision = this.result?.manifest?.revision;
      if (revision) this.download(this.edge().path + "/minutes.txt?revision=" + revision, this.snapshot.session_id + "-r" + revision + "-minutes.txt");
    });
    byId("result-download").addEventListener("click", () => {
      if (!this.result?.zip_url) return;
      const manifest = this.result.manifest || {};
      const version = manifest.export_revision || manifest.revision || this.snapshot.result?.revision;
      const filename = this.snapshot.session_id + (version ? "-r" + version : "") + ".zip";
      this.download(this.result.zip_url, filename);
    });
  }

  enabled(snapshot = this.snapshot) {
    return Boolean(snapshot?.assembly && snapshot.session_status);
  }

  observesLocalRequest() {
    const { meeting, session_status: status } = this.snapshot || {};
    return meeting?.mode === "live" && meeting.audio_device_id === this.deviceId &&
      (meeting.capture_requested ?? status === "ready");
  }

  observe(snapshot) {
    const previous = this.snapshot;
    this.snapshot = snapshot;
    if (!this.enabled()) {
      this.paint();
      return;
    }
    if (captureStates.has(snapshot.session_status) && !this.observesLocalRequest() &&
        (this.mic?.begun || previous?.session_status === "recording")) {
      this.mic?.quiesce();
      this.mic?.finish();
    }
    if (this.observesLocalRequest() && this.prepared()) this.begin();
    if (snapshot.result?.api_url && this.requestedResult === snapshot.session_id) {
      this.requestedResult = null;
      this.openResult();
    } else if (snapshot.result?.revision !== previous?.result?.revision && !byId("meeting-result").hidden) {
      this.openResult(false);
    }
    this.paint();
  }

  prepared() {
    return this.mic?.prepared && !this.mic.closed && !this.mic.begun;
  }

  micAvailable() {
    return canRecord() && this.caps().microphone === true;
  }

  paint() {
    const snapshot = this.snapshot;
    const visible = this.enabled();
    show("meeting-controls", visible);
    if (!visible) return;
    const status = snapshot.session_status;
    const saved = Boolean(snapshot.result?.api_url);
    const waitingForMic = this.observesLocalRequest() && !this.prepared() && !this.beginning;
    const recording = status === "recording" && snapshot.capture_state === "recording";
    const replay = snapshot.meeting?.mode === "replay";
    const noVoice = !this.micAvailable() && !snapshot.meeting?.mode;
    const replayAvailable = snapshot.provider_mode === "fixture";
    const manual = snapshot.meeting?.input_mode === "manual";
    const ready = status === "ready" || status === "paused";
    const stateTitle = {
      ready: this.beginning ? "마이크 연결 중" : this.busy === "meeting_start" ? "회의 시작 처리 중" : "회의 준비",
      recording: recording ? "녹음 중" : replay ? "예시 회의 진행 중" : "회의 진행 중",
      paused: "일시정지",
      finalizing: "결과 저장 중",
      completed: !saved ? "이전 회의 기록" :
        snapshot.result?.missing?.length ? "일부 항목이 누락된 결과가 저장됨" : "저장됨",
      save_error: "결과 저장 실패",
    };
    write("meeting-state", stateTitle[status] || "회의 상태 확인 중");
    const note = this.busy === "meeting_end" && status !== "finalizing" ?
      "녹음을 멈추고 채택·종료 처리를 확인하고 있습니다." : ({
        ready: waitingForMic ? "지정한 PC 마이크를 준비한 뒤 회의를 이어가세요." :
          this.prepared() ? "PC 마이크가 준비되었습니다. 시작하면 녹음합니다." :
            (noVoice ? (replayAvailable ? "직접 입력과 예시 발화로 회의를 시작할 수 있습니다." :
              "전사 서비스 또는 지정 마이크를 확인한 뒤 회의를 시작하세요.") :
              "회의 중 한 대의 지정 마이크만 사용합니다."),
        recording: replay ? "예시 입력 모드 · 대본과 직접 입력의 출처를 기록합니다." : manual ? "직접 입력 모드 · 발언을 기록하고 수정안 요청을 눌러 후보를 만듭니다." : "부위를 선택하고 대화를 이어가세요. 비교할 때 “수정안 보여줘”라고 말하거나 수정안 보기를 누르세요.",
        paused: waitingForMic ? "PC 마이크를 준비하면 녹음을 재개합니다." : "현재까지의 전사와 채택 기록을 보존했습니다.",
        finalizing: "마지막 전사와 지정된 CAD 파일을 확인하고 있습니다.",
        completed: saved ? "전체 전사, 상세 요약, 최종 SCAD와 STL을 확인할 수 있습니다." :
          "아래에서 기존 회의록과 모델 파일을 확인할 수 있습니다.",
        save_error: "고정한 채택본과 확보한 기록으로 저장을 다시 시도할 수 있습니다.",
      })[status];
    write("meeting-state-note", snapshot.meeting?.edit_request_output === "bundle" && status === "recording"
      ? "Quest에서 수정 요청을 녹음하고, 최종 전사를 확인한 뒤 오른쪽 앞 가상 CAD를 가리켜 A를 누르세요." : note || "");
    const buttonText = {
      ready: waitingForMic ? "PC 마이크 연결" : "회의 시작", recording: "회의 종료",
      paused: waitingForMic ? "PC 마이크 연결" : "회의 재개",
      finalizing: "결과 저장 중", completed: "결과 열기", save_error: "저장 다시 시도",
    };
    write("meeting-main", buttonText[status] || "회의 상태 확인 중");
    show("meeting-main", status !== "completed" || saved);
    byId("meeting-main").disabled = Boolean(this.busy || this.preparing || this.beginning || status === "finalizing" ||
      (status === "paused" && snapshot.capture_state !== "off") ||
      (ready && !replayAvailable && this.caps().microphone !== true));
    show("meeting-manual", ready && !replayAvailable && !snapshot.meeting?.capture_requested);
    byId("meeting-manual").disabled = Boolean(this.busy);
    show("prepare-pc-mic", ready && this.micAvailable() && !this.prepared() && !waitingForMic);
    byId("prepare-pc-mic").disabled = Boolean(this.busy || this.preparing || this.beginning);
    show("meeting-pause", status === "recording" || status === "paused");
    write("meeting-pause", status === "paused" ? "회의 종료" : "일시정지");
    byId("meeting-pause").disabled = Boolean(this.busy);
    const summary = snapshot.summary || {};
    const proposals = summary.proposal_count ? " · 수정안 " + summary.proposal_count + "개 준비됨" : "";
    write("meeting-discussion", (summary.discussion || "아직 없음") + proposals);
    write("meeting-preserved", preservedText(summary.preserved || snapshot.assembly.preserved_conditions) || "아직 없음");
    write("meeting-decision", summary.decision || "아직 미정");
    const shared = snapshot.shared_preview;
    const round = snapshot.rounds.find(item => item.round_id === shared?.round_id);
    const candidate = round?.candidates.findIndex(item => item.candidate_id === shared?.candidate_id);
    const label = Number.isInteger(candidate) && candidate >= 0 ? "후보 " + String.fromCharCode(65 + candidate) : "";
    write("shared-model", shared ? "공유 미리보기 · " + [label, shared.version_id].filter(Boolean).join(" · ") :
      "공유 형상 · " + (!snapshot.decisions.some(item => item.selected_model_version_id) ? "원본 모델" : "현재 채택본"));
  }

  askMic(startWhenReady) {
    if (!this.micAvailable()) {
      this.notice("PC 마이크는 localhost 또는 인증서가 신뢰된 HTTPS에서 전사 서비스가 연결되면 사용할 수 있습니다.", true);
      return;
    }
    this.startWhenReady = startWhenReady;
    write("pc-mic-consent", "회의 참가자의 녹음·전사 동의를 확인해 주세요. " +
      (this.snapshot.raw_audio_saved ? "현재 설정은 원음을 함께 보관합니다." :
        "현재 설정은 전사만 보관합니다.") + " 마이크를 준비한 뒤 회의를 시작하면 음성을 수집합니다.");
    byId("pc-mic-dialog").showModal();
  }

  async prepare() {
    byId("pc-mic-dialog").close();
    const edge = this.edge();
    if (!edge || this.preparing) return;
    this.preparing = true;
    this.paint();
    const mic = new MeetingMicrophone(edge, {
      onFailure: error => {
        if (this.edge() === edge) this.notice(error, true);
      },
      onClosed: () => this.paint(),
    });
    this.mic = mic;
    try {
      await mic.prepare();
      if (this.edge() !== edge) { mic.close(); return; }
      if (this.observesLocalRequest()) await this.begin();
      else if (this.startWhenReady) await this.start("live", this.deviceId);
    } catch (error) {
      if (this.edge() === edge) this.notice(this.errorMessage(error), true);
    } finally {
      if (this.edge() === edge) {
        this.preparing = false;
        this.paint();
      }
    }
  }

  async begin() {
    if (!this.prepared() || this.beginning) return;
    const mic = this.mic;
    this.beginning = true;
    this.paint();
    try {
      await this.beforeUtterance();
      // A shared room microphone leaves the speaker unset. EDGE uses explicit shared views.
      await mic.begin(this.deviceId, false);
    } catch (error) {
      mic.close();
      if (this.mic === mic) this.notice(this.errorMessage(error), true);
    } finally {
      if (this.mic === mic) {
        this.beginning = false;
        this.paint();
      }
    }
  }

  async main() {
    if (!this.enabled() || this.busy) return;
    const { session_status: status, meeting } = this.snapshot;
    if (status === "completed") return this.openResult();
    if (status === "save_error") return this.send("meeting_retry");
    if (status === "recording") return this.end("meeting_end");
    if (status !== "ready" && status !== "paused") return;
    if (this.observesLocalRequest()) {
      if (this.prepared()) return this.begin();
      return this.askMic(false);
    }
    const mode = meeting?.mode || (this.snapshot.provider_mode === "fixture" ? "replay" : "live");
    if (mode === "replay") return this.start("replay");
    if (meeting?.input_mode === "manual") return this.send("meeting_start", { mode: "live", input_mode: "manual" });
    const designated = this.prepared() ? this.deviceId : meeting?.audio_device_id || this.snapshot.audio_host_device_id;
    if (designated === this.deviceId && !this.prepared()) return this.askMic(true);
    return this.start("live", designated);
  }

  async start(mode, audioDeviceId) {
    const result = await this.send("meeting_start", { mode, audio_device_id: audioDeviceId });
    if (result && mode === "live" && this.prepared() && audioDeviceId === this.deviceId) await this.begin();
  }

  async end(type) {
    const edge = this.edge();
    if (!edge || this.busy) return;
    const mic = this.mic;
    mic?.quiesce();
    await this.send(type, {}, this.pendingDecision());
    if (this.edge() === edge) mic?.finish();
  }

  async send(type, payload = {}, pending = null) {
    const edge = this.edge();
    if (!edge || this.busy) return null;
    this.busy = type;
    this.paint();
    const stickyEnd = type === "meeting_end";
    try {
      await pending;
      const body = stickyEnd ? (this.endRequest ??= edge.commandBody(type, payload)) : undefined;
      return await edge.command(type, payload, body);
    } catch (error) {
      if (this.edge() === edge) this.notice(this.errorMessage(error), true);
      return null;
    } finally {
      if (this.edge() === edge) {
        this.busy = null;
        this.paint();
      }
    }
  }

  async openResult(focus = true) {
    const edge = this.edge();
    const ready = this.snapshot?.result;
    if (!edge || !ready?.api_url) return;
    this.resultLoad?.abort();
    const controller = new AbortController();
    this.resultLoad = controller;
    this.result = null;
    show("meeting-result", true);
    markResultLoading();
    if (focus) byId("meeting-result").focus();
    try {
      const review = await edge.get(ready.api_url, "json", controller.signal);
      if (controller.signal.aborted || this.edge() !== edge) return;
      this.result = { ...review, zip_url: review.zip_url || ready.zip_url };
      paintMeetingResult(this.result);
    } catch (error) {
      if (error.name !== "AbortError" && this.edge() === edge) {
        write("result-meta", this.errorMessage(error));
      }
    }
  }

  reset() {
    this.resultLoad?.abort();
    this.requestedResult = null;
    this.mic?.close();
    Object.assign(this, { snapshot: null, mic: null, preparing: false, beginning: false, busy: null, endRequest: null, result: null });
    byId("pc-mic-dialog").close();
    show("meeting-controls", false);
    show("meeting-result", false);
  }
}
