import { ModelViewer } from "./viewer.js";
import { canRecord, MeetingMicrophone } from "./audio.js";
import { MeetingConnection, randomId, request } from "./edge.js";
import { byId, element, makeButton, renderMinutes, renderRound, renderRoundsMenu, renderTranscripts, unitValue, versionLabel } from "./panels.js";

const state = {
  identity: null, snapshot: null, edge: null, online: false, pending: 0, serviceCaps: {}, viewer: null, view: null, target: null,
  selected: [], loadId: 0, loadedVersion: null, pinnedRound: null, autoPreview: null, partial: null,
  viewReports: Promise.resolve(), mediaUrl: null, audioUrl: null, switching: false, displayedView: null,
};
const storageKey = "contextpin-meeting-participant";
const deviceKey = "contextpin-meeting-device";
const storage = {
  get(key) {
    try { return sessionStorage.getItem(key); } catch { return null; }
  },
  set(key, value) {
    try { sessionStorage.setItem(key, value); } catch { /* optional resume */ }
  },
  clear(key) {
    try { sessionStorage.removeItem(key); } catch { /* optional resume */ }
  },
};

const deviceId = storage.get(deviceKey) || "web-" + randomId();
storage.set(deviceKey, deviceId);
const show = (id, visible = true) => { byId(id).hidden = !visible; };
const write = (id, value) => { byId(id).textContent = value; };

function notice(text = "", error = false) {
  const area = byId("notice");
  area.textContent = text;
  area.classList.toggle("error", error);
  area.hidden = !text;
}

function message(error) {
  if (error?.name === "NotAllowedError") return "마이크 권한을 허용하거나 발언을 직접 입력해 주세요.";
  if (error?.name === "NotFoundError") return "사용할 마이크를 찾지 못했습니다.";
  if (error instanceof TypeError) return "EDGE에 연결할 수 없습니다. 주소와 서버 상태를 확인하세요.";
  return error?.message || "작업을 처리하지 못했습니다.";
}

function setStatus(online, label) {
  state.online = online;
  const pending = state.pending > 0 && !online ? " · 미전송 " + state.pending : "";
  const status = byId("connection");
  status.textContent = label + pending;
  status.classList.toggle("online", online);
}

function reportStage(error) {
  write("view-message", message(error));
}

try {
  state.viewer = new ModelViewer(byId("model-canvas"), {
    onPart: part => {
      if (!state.loadedVersion || state.switching) return;
      setPart(part);
    },
    onZoom: paintZoomLabel,
    onLost: () => write("view-message", "3D 그래픽 연결이 끊겼습니다. 화면을 새로고침해 주세요."),
  });
} catch (error) {
  reportStage(error);
}

function deviceCapabilities() {
  return {
    device_id: deviceId, platform: "browser", app_version: "1",
    microphone: canRecord() ? "available" : "unsupported",
    frame_capture: "unsupported",
    spatial_tracking: "unavailable", world_anchoring: "unavailable",
    ray_selection: state.viewer ? "available" : "unavailable",
    hand_tracking: "unavailable", eye_tracking: "unavailable",
    runtime_glb_loading: state.viewer ? "available" : "unavailable",
    audio_sample_rates: [],
    permission_notes: canRecord() ? "녹음 시작 시 마이크 권한 확인" : "마이크에는 localhost 또는 HTTPS가 필요합니다.",
  };
}

function assembly(snapshot = state.snapshot) {
  return snapshot?.assembly?.mode === "openscad" ? snapshot.assembly : null;
}

function paintZoomLabel(zoom) {
  const base = assembly() ? "원본 기준 배율" : "화면 크기에 맞춤";
  write("viewport-label", zoom === 1 ? base : "화면 배율 " + Math.round(zoom * 100) + "%");
}

function currentPerson(snapshot = state.snapshot) {
  return snapshot?.participants.find(person => person.participant_id === state.identity?.participant_id);
}

function versionById(id) {
  return state.snapshot?.versions.find(version => version.version_id === id);
}

function headModel() {
  return versionById(state.snapshot?.committed_version_id);
}

function candidateRound(id) {
  return state.snapshot?.rounds.find(round => round.round_id === id);
}

function startSession(identity) {
  state.identity = identity;
  const { session_id, participant_id, token } = identity;
  storage.set(storageKey, JSON.stringify({ session_id, participant_id, token }));
  show("setup", false);
  show("meeting");
  show("leave-button");
  notice();
  state.edge = new MeetingConnection(identity, {
    snapshot: snapshot => {
      if (state.partial && snapshot.utterances?.at(-1)?.utterance_id !== state.lastUtterance) state.partial = null;
      state.lastUtterance = snapshot.utterances?.at(-1)?.utterance_id;
      const changedProfile = state.snapshot?.profile.object_id !== snapshot.profile.object_id;
      state.snapshot = snapshot;
      if (changedProfile) buildPartButtons();
      if (state.target?.kind === "candidate") {
        const compatible = candidateRound(state.target.roundId);
        if (!compatible || !["reviewable", "building"].includes(compatible.status) ||
          !compatible.candidates.some(candidate =>
            candidate.candidate_id === state.target.candidateId && candidate.status === "valid")) {
          state.target = null;
        }
      }
      selectReadyEdit();
      updateModel();
      paint();
    },
    connection: setStatus,
    event: (event, snapshot) => {
      if (!assembly(snapshot) || event.actor_id !== state.identity?.participant_id) return;
      if (event.event_type === "speech.transcribing") state.transcribing = true;
      if (event.event_type === "speech.failed") {
        state.transcribing = false;
        notice(event.payload?.message || "전사에 실패했습니다. 발언을 직접 입력할 수 있습니다.", true);
      }
      if (event.event_type === "transcript.finalized" && event.payload?.source_mode === "live_capture") {
        state.transcribing = false;
        const round = snapshot.rounds.findLast(item =>
          item.context?.utterance?.utterance_id === event.payload.utterance_id && item.status !== "superseded");
        if (round) state.pinnedRound = round.round_id;
      }
    },
    pending: delta => {
      state.pending += delta;
      state.pending = Math.max(0, state.pending);
      if (!state.online) setStatus(false, "EDGE에 다시 연결 중");
    },
    invalid: () => {
      exitSession();
      notice("회의 접속이 만료되었습니다. 참가 코드를 다시 입력하세요.", true);
    },
    transient: event => {
      if (event.event_type === "transcript.partial" || event.type === "transcript.partial") {
        state.partial = event.payload ?? event;
        if (state.snapshot) renderTranscripts(state.snapshot, state.partial, loadAudio);
      }
    },
  });
  state.edge.start(identity.snapshot);
}

function exitSession() {
  state.mic?.close();
  state.mic = null;
  state.edge?.close();
  state.controller?.abort();
  state.loadId++;
  state.viewer?.clear();
  if (state.mediaUrl) URL.revokeObjectURL(state.mediaUrl);
  releaseAudio();
  Object.assign(state, { identity: null, edge: null, snapshot: null, selected: [], target: null, view: null,
    loadedVersion: null, autoPreview: null, pinnedRound: null, viewReports: Promise.resolve(), partial: null,
    mediaUrl: null, switching: false, displayedView: null, lastViewSignature: null,
    pending: 0, lastUtterance: null, micStarting: false, transcribing: false, failedView: false });
  storage.clear(storageKey);
  byId("invite-dialog").close();
  byId("mic-dialog").close();
  delete byId("model-canvas").dataset.version;
  for (const id of ["round-content", "transcripts", "minutes-list", "parameters", "media-items"]) {
    const area = byId(id);
    area.replaceChildren();
    delete area.dataset.key;
  }
  write("view-message", "");
  show("setup");
  show("meeting", false);
  show("invite-button", false);
  show("leave-button", false);
  notice();
  checkHealth();
}

async function checkHealth() {
  try {
    const info = await request("/healthz");
    if (!state.edge) {
      setStatus(true, "EDGE 연결됨");
      write("setup-description", info.assembly_demo ?
        "조립체에서 물체 2의 외형을 바꾸고, 선택한 수정안과 OpenSCAD 원본을 함께 저장하세요." :
        "모델을 보며 수정안을 만들고, 팀이 선택한 모델과 결정 이유를 함께 남기세요.");
    }
  } catch {
    if (!state.edge) setStatus(false, "EDGE에 연결할 수 없음");
  }
}

async function loadProfiles() {
  try {
    const { profiles } = await request("/v1/profiles");
    const select = byId("profile-select");
    for (const profile of profiles) {
      const option = element("option", profile.display_name);
      option.value = profile.object_id;
      select.append(option);
    }
    select.disabled = !profiles.length;
    byId("create-submit").disabled = !profiles.length;
  } catch (error) {
    notice(message(error), true);
  }
}

function bindEntry(formId, endpoint, payload) {
  const form = byId(formId);
  form.addEventListener("submit", async event => {
    event.preventDefault();
    const button = form.querySelector("button");
    const body = {
      display_name: form.elements.namedItem("name").value.trim(),
      device_id: deviceId,
      capabilities: deviceCapabilities(),
      ...payload(form.elements),
    };
    button.disabled = true;
    notice(endpoint === "/v1/sessions" ? "초기 3D 모델을 만드는 중입니다." : "회의에 연결 중입니다.");
    try {
      const identity = await request(endpoint, { body });
      form.reset();
      startSession(identity);
    } catch (error) {
      notice(message(error), true);
    } finally {
      button.disabled = false;
    }
  });
}

bindEntry("create-form", "/v1/sessions", values => ({ profile_id: values.profile.value }));
bindEntry("join-form", "/v1/join", values => ({ join_code: values.code.value.trim().toUpperCase() }));
byId("leave-button").addEventListener("click", exitSession);

async function runCommand(type, payload, form) {
  const edge = state.edge;
  if (!edge) return null;
  const buttons = form ? Array.from(form.querySelectorAll("button:not(:disabled)")) : [];
  buttons.forEach(button => { button.disabled = true; });
  try {
    if (type !== "view") notice();
    const result = await edge.command(type, payload);
    if (state.edge !== edge) return null;
    if (result.round_id && ["edit", "restore"].includes(type)) {
      state.pinnedRound = result.round_id;
      state.autoPreview = result.round_id;
      selectReadyEdit();
      updateModel();
      paint();
    }
    return result;
  } catch (error) {
    if (state.edge === edge) notice(message(error), true);
    return null;
  } finally {
    buttons.forEach(button => { button.disabled = false; });
  }
}

function visualContext() {
  const displayed = state.displayedView;
  const round = shownRound();
  const candidateId = round?.round_id === displayed?.roundId ? displayed?.candidateId : null;
  return {
    version_id: state.loadedVersion?.version_id,
    part_ids: state.selected,
    round_id: round?.candidates.length ? round.round_id : null,
    candidate_id: candidateId,
    display_order_candidate_ids: round?.candidates.map(candidate => candidate.candidate_id) || [],
    tracking_state: "desktop_view",
  };
}

function reportView() {
  if (state.switching) return;
  const payload = visualContext();
  const signature = JSON.stringify(payload);
  if (!payload.version_id || signature === state.lastViewSignature) return;
  state.lastViewSignature = signature;
  const edge = state.edge;
  state.viewReports = state.viewReports.catch(() => {}).then(async () => {
    if (state.edge !== edge) return;
    try {
      await edge.command("view", payload);
    } catch (error) {
      if (state.edge === edge) {
        if (state.lastViewSignature === signature) state.lastViewSignature = null;
        notice("부위 선택을 공유하지 못했습니다. " + message(error), true);
      }
      throw error;
    }
  });
  state.viewReports.catch(() => {});
}

function buildPartButtons() {
  const area = byId("parts");
  area.replaceChildren();
  if (!state.snapshot.profile.capabilities?.can_annotate) {
    area.append(element("span", "이 프로필에서 부위 선택을 지원하지 않습니다.", "subtle"));
    return;
  }
  for (const part of state.snapshot.profile.semantic_parts) {
    const button = element("button", part.label, "chip part");
    button.type = "button";
    button.dataset.part = part.part_id;
    button.setAttribute("aria-pressed", "false");
    button.addEventListener("click", () => {
      if (state.loadedVersion) setPart(part.part_id);
    });
    area.append(button);
  }
}

function paintParts() {
  const region = byId("parts");
  const ready = state.loadedVersion && !state.switching && state.snapshot.profile.capabilities?.can_annotate;
  const highlighted = state.selected.length ? state.selected : state.displayedView?.highlight || [];
  for (const button of region.querySelectorAll("button")) {
    button.disabled = !ready;
    button.setAttribute("aria-pressed", String(state.selected.includes(button.dataset.part)));
  }
  state.viewer?.highlight(highlighted);
}

function setPart(partId) {
  if (assembly() || state.switching || !state.snapshot?.profile.capabilities?.can_annotate) return;
  const knownPart = state.snapshot.profile.semantic_parts.some(part => part.part_id === partId);
  state.selected = knownPart && !state.selected.includes(partId) ? [partId] : [];
  paintParts();
  reportView();
}

function selectReadyEdit() {
  const round = candidateRound(state.autoPreview);
  if (!round) return;
  if (["failed", "superseded", "dismissed"].includes(round.status)) {
    state.autoPreview = null;
    return;
  }
  if (round.status === "reviewable") {
    state.autoPreview = null;
    const candidate = round.candidates.find(item => item.status === "valid");
    if (candidate) changeView(round, candidate, false);
  }
}

function changeView(round, candidate, render = true) {
  if (!candidate?.version_id) return;
  state.pinnedRound = round.round_id;
  const changed = candidate.target_part_ids || (candidate.operations || []).map(operation => operation.part_id);
  state.target = {
    kind: ["reviewable", "building"].includes(round.status) ? "candidate" : "history",
    versionId: candidate.version_id, roundId: round.round_id,
    candidateId: candidate.candidate_id, highlight: changed, label: candidate.title,
  };
  if (render) {
    if (state.failedView) state.view = null;
    updateModel();
    paint();
  }
}

function stageReferenceRadius(view, version) {
  if (assembly()) return 0;
  const round = candidateRound(view.roundId) || shownRound();
  if (!round || view.kind === "history") return 0;
  const others = round.candidates.map(candidate => versionById(candidate.version_id));
  const versions = [version, headModel(), ...others].filter(Boolean);
  return Math.max(0, ...versions.map(item => {
    const size = item.bounds_m?.size || item.assets?.["preview.glb"]?.bounds_m?.size;
    return Array.isArray(size) ? Math.hypot(...size) / 2 : 0;
  }));
}

function assemblyPlacement() {
  const scene = assembly();
  if (!scene) return null;
  const original = state.snapshot.versions[0];
  const bounds = original.bounds_m || original.assets?.["preview.glb"]?.bounds_m;
  const mount = scene.mounting;
  if (!mount || !bounds || mount.position_m?.length !== 3 || mount.rotation_xyzw?.length !== 4) {
    throw new Error("원본 모델의 장착 좌표를 읽을 수 없습니다.");
  }
  return { mounting: mount, originalBounds: bounds };
}

async function checkAsset(buffer, version) {
  const hash = version.assets?.["preview.glb"]?.sha256;
  if (!hash || !crypto.subtle) return;
  const bytes = new Uint8Array(await crypto.subtle.digest("SHA-256", buffer));
  const actual = Array.from(bytes, byte => byte.toString(16).padStart(2, "0")).join("");
  if (actual !== hash) throw new Error("모델 파일의 검증값이 저장된 버전과 다릅니다.");
}

function updateModel() {
  const head = headModel();
  if (!head) return;
  const view = state.target ?? { kind: "head", versionId: head.version_id, label: "확정 모델" };
  const version = versionById(view.versionId);
  if (!version) return;
  if (state.view?.versionId === view.versionId && state.view.kind === view.kind &&
      state.view.candidateId === view.candidateId) return;
  state.view = view;
  state.failedView = false;
  state.controller?.abort();
  const loadId = ++state.loadId;
  if (state.loadedVersion?.version_id === view.versionId) {
    state.displayedView = view;
    state.switching = false;
    write("view-message", "");
    return;
  }

  if (!state.viewer) {
    write("view-message", "이 브라우저에서 WebGL2를 열 수 없습니다.");
    paintParameters(version);
    return;
  }
  const controller = new AbortController();
  state.controller = controller;
  state.switching = true;
  paintParts();
  write("view-message", "3D 모델을 불러오는 중");
  const edge = state.edge;
  edge.get(version.preview_url, "arrayBuffer", controller.signal)
    .then(async buffer => {
      if (loadId !== state.loadId) return;
      await checkAsset(buffer, version);
      if (loadId !== state.loadId) return;
      state.viewer.load(buffer, stageReferenceRadius(view, version), assemblyPlacement());
      state.loadedVersion = version;
      state.displayedView = view;
      state.selected = [];
      state.switching = false;
      byId("model-canvas").dataset.version = version.version_id;
      write("view-message", "");
      paint();
    })
    .catch(error => {
      if (loadId !== state.loadId || error.name === "AbortError") return;
      state.switching = false;
      state.failedView = true;
      reportStage(error);
      paint();
    });
}

function paintModelHeader() {
  const version = state.loadedVersion;
  const targetVersion = state.view ? versionById(state.view.versionId) : null;
  const display = version || targetVersion;
  const isOriginal = display?.version_id === state.snapshot.versions[0]?.version_id;
  const isHead = display?.version_id === state.snapshot.committed_version_id;
  const scene = assembly();
  const label = scene && isOriginal ? "원본 모델" : isHead ? "확정 모델"
    : state.displayedView?.kind === "history" ? "회의록 보기" : "검토 모델";
  write("model-heading", label);
  const size = display?.bounds_mm?.size;
  const dimensions = size ? size.map(value => unitValue(value, "")).join(" × ") + " mm (폭 × 높이 × 깊이)" : "";
  const build = display?.build_mode === "generated" ? (scene ? "OpenSCAD 생성" : "생성된 형상")
    : display?.build_mode === "fixture" ? "예시 형상" : "";
  write("model-meta", [display ? versionLabel(display.version_id, state.snapshot.versions) : "", dimensions, build]
    .filter(Boolean).join(" · "));
  show("head-view", state.view?.kind !== "head");
  show("original-view", Boolean(scene && !isOriginal));
  show("part-controls", !scene);
  show("assembly-view-hint", Boolean(scene));
  show("model-files", Boolean(version?.stl_url && state.snapshot.profile.capabilities?.can_export_stl));
  show("download-scad", Boolean(version?.assets?.["object_2.scad"]?.url));
  show("download-zip", Boolean(version?.export_url));
  write("download-zip", scene ? "SCAD · 매개변수 · STL" : "버전 내보내기");
  show("media-label", !scene && Boolean(version) && state.snapshot.status === "active");
}

async function download(path, filename) {
  try {
    const blob = await state.edge.get(path, "blob");
    const url = URL.createObjectURL(blob);
    const link = element("a");
    link.href = url;
    link.download = filename;
    document.body.append(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1500);
  } catch (error) {
    notice(message(error), true);
  }
}

function exportVersion(version) {
  if (version?.export_url) download(version.export_url, "contextpin-" + version.version_id + ".zip");
}

byId("original-view").addEventListener("click", () => {
  const original = state.snapshot?.versions[0];
  if (!original) return;
  state.target = { kind: "history", versionId: original.version_id };
  updateModel();
  paint();
});
byId("head-view").addEventListener("click", () => {
  state.target = null;
  if (state.failedView) state.view = null;
  updateModel();
  paint();
});
byId("zoom-in").addEventListener("click", () => state.viewer?.zoomBy(1.25));
byId("zoom-out").addEventListener("click", () => state.viewer?.zoomBy(.8));
byId("reset-view").addEventListener("click", () => state.viewer?.reset());
byId("download-stl").addEventListener("click", () => {
  const version = state.loadedVersion;
  if (version?.stl_url) download(version.stl_url, "contextpin-" + version.version_id + ".stl");
});
byId("download-scad").addEventListener("click", () => {
  const asset = state.loadedVersion?.assets?.["object_2.scad"];
  if (asset?.url) download(asset.url, "object_2.scad");
});
byId("download-zip").addEventListener("click", () => exportVersion(state.loadedVersion));
byId("download-minutes").addEventListener("click", () => {
  if (state.edge) download(state.edge.path + "/minutes", "contextpin-meeting-minutes.json");
});

function paintParameters(version = state.loadedVersion) {
  const profile = state.snapshot.profile;
  const editor = byId("parameter-form");
  const destination = byId("parameters");
  const host = currentPerson()?.role === "host";
  const canEdit = Boolean(version && !state.switching && profile.capabilities?.can_parametric_edit &&
    state.snapshot.status === "active");
  const locks = (state.snapshot.constraints || []).filter(item => item.status === "active");
  const key = JSON.stringify([version?.version_id, state.snapshot.constraint_revision, canEdit, host]);
  if (destination.dataset.key === key) return;
  destination.dataset.key = key;
  destination.replaceChildren();
  byId("edit-submit").disabled = !canEdit;
  const versionName = version ? versionLabel(version.version_id, state.snapshot.versions) : "";
  write("parameter-status", canEdit ? versionName + (assembly() ? "의 변경 가능한 외형 값만 수정할 수 있습니다." : "에서 바꿀 치수를 입력하세요.") :
    versionName ? "현재 모델의 등록 치수" : "모델을 불러오는 중입니다.");
  if (!version) return;
  const head = headModel();
  for (const definition of profile.parameters) {
    const row = element("div", null, "parameter-row");
    const parameterId = definition.parameter_id;
    const label = element("label", definition.label + (definition.unit ? " (" + definition.unit + ")" : ""));
    const input = definition.type === "enum" ? element("select") : element("input");
    label.htmlFor = "param-" + parameterId;
    input.id = label.htmlFor;
    input.name = parameterId;
    input.disabled = !canEdit;
    if (definition.type === "enum") {
      for (const value of definition.allowed_values || []) {
        const option = element("option", String(value));
        option.value = value;
        input.append(option);
      }
    } else {
      input.type = "number";
      input.step = definition.type === "integer" ? "1" : "any";
      if (definition.min_value !== undefined) input.min = definition.min_value;
      if (definition.max_value !== undefined) input.max = definition.max_value;
      input.required = true;
    }
    input.value = version.parameters[parameterId];
    row.append(label, input);
    const measured = profile.measured_properties?.find(property => property.property_id === parameterId);
    if (measured && !assembly()) row.append(lockInput(measured, head, locks, host));
    destination.append(row);
  }
  for (const measured of assembly() ? [] : profile.measured_properties || []) {
    if (profile.parameters.some(field => field.parameter_id === measured.property_id)) continue;
    const row = element("div", null, "parameter-row");
    row.append(element("h3", measured.label), lockInput(measured, head, locks, host));
    destination.append(row);
  }
  editor.dataset.base = version.version_id;
}

function lockInput(measured, head, locks, host) {
  const active = locks.find(lock => lock.property_id === measured.property_id);
  const label = element("label", "확정값 유지 · " +
    unitValue(active?.value ?? head.measurements[measured.property_id], measured.unit), "lock");
  const checkbox = element("input");
  checkbox.type = "checkbox";
  checkbox.checked = Boolean(active);
  checkbox.disabled = !host || state.snapshot.status !== "active" || active?.source_kind === "object_profile";
  checkbox.addEventListener("change", async () => {
    const enabled = checkbox.checked;
    checkbox.disabled = true;
    const result = await runCommand("constraint", { parameter_id: measured.property_id, enabled });
    checkbox.checked = result ? enabled : !enabled;
    checkbox.disabled = false;
  });
  label.prepend(checkbox);
  return label;
}

byId("parameter-form").addEventListener("submit", async event => {
  event.preventDefault();
  const form = event.currentTarget;
  const version = versionById(form.dataset.base);
  if (!version) return;
  const params = {};
  for (const definition of state.snapshot.profile.parameters) {
    const id = definition.parameter_id;
    const field = form.elements[id];
    if (!field) continue;
    const value = field.type === "number" ? field.valueAsNumber : field.value;
    if (value !== version.parameters[id]) params[id] = value;
  }
  if (!Object.keys(params).length) {
    notice("수정할 치수를 하나 이상 입력하세요.", true);
    return;
  }
  await runCommand("edit", { base_version_id: version.version_id, parameters: params }, form);
});

function roundShort(round) {
  return ({ reviewable: "검토 가능", committed: "채택", deferred: "보류", dismissed: "기각",
    needs_input: "확인 필요", failed: "실패", superseded: "이전 검토", answered: "기록됨" })[round.status] || "작업 중";
}

function shownRound() {
  const rounds = state.snapshot.rounds;
  return candidateRound(state.pinnedRound) ||
    [...rounds].reverse().find(round => round.status !== "answered" && round.status !== "superseded") || rounds.at(-1);
}

function paint() {
  const snapshot = state.snapshot;
  if (!snapshot) return;
  const presenter = currentPerson()?.role === "host";
  const scene = assembly();
  write("object-title", scene?.display_name || snapshot.profile.display_name);
  write("participants", scene ? "" : snapshot.participants
    .map(person => person.display_name + (person.role === "host" ? " (진행자)" : "")).join(" · "));
  show("invite-button", presenter && snapshot.status === "active");
  write("invite-button", scene ? "Quest 연결" : "참가 코드");
  write("invite-hint", scene ? "Quest의 조립체 검토 메뉴에서 PC EDGE에 연결한 뒤 이 참가 코드를 입력하세요." :
    "검토자가 이 화면의 주소를 열고 아래 코드를 입력하면 같은 모델을 볼 수 있습니다.");
  show("assembly-summary", Boolean(scene));
  if (scene) {
    const target = scene.object_2?.display_name || snapshot.profile.semantic_parts[0]?.label || "물체 2";
    write("assembly-target", (scene.is_demo ? "예제 조립체 · " : "") + target + " 외형 수정");
    const items = (scene.preserved_conditions || []).map(condition => typeof condition === "string"
      ? condition : condition.label).filter(Boolean);
    write("assembly-protection", items.length ? "유지하는 결합 조건 · " + items.join(" · ") : "");
  }
  write("parameter-summary", scene ? "외형 직접 수정" : "치수 직접 수정과 유지 조건");
  byId("speech-text").placeholder = scene ?
    "더 곡선적으로 해보고 싶다면 유지할 결합 조건과 함께 적어 주세요." :
    "변경하고 싶은 부위와 유지할 조건을 함께 적어보세요.";
  paintZoomLabel(state.viewer?.zoom ?? 1);
  paintModelHeader();
  paintParts();
  paintParameters();
  const rounds = [...snapshot.rounds].reverse();
  const round = shownRound();
  renderRoundsMenu(rounds, round?.round_id, roundShort);
  const candidateId = !state.switching && state.displayedView?.roundId === round?.round_id ?
    state.displayedView?.candidateId : null;
  renderRound(round, snapshot, { actor: state.identity.participant_id, presenter: presenter || Boolean(scene), candidateId }, {
    view: changeView, command: runCommand, error: text => notice(text, true),
    refine: current => {
      const input = byId("speech-text");
      if (current && !input.value.trim()) input.value = "이 후보에서 ";
      input.focus();
      input.setSelectionRange(input.value.length, input.value.length);
    },
  });
  renderTranscripts(snapshot, state.partial, loadAudio);
  renderMinutes(snapshot, {
    view: version => {
      state.target = { kind: "history", versionId: version.version_id, label: "회의록 보기" };
      updateModel();
      paint();
    },
    export: exportVersion, audio: loadAudio,
  });
  renderMedia(snapshot);
  paintStatus(round);
  paintDebug(round);
  if (scene) show("print-panel", false);
  else paintPrintResults(snapshot);
  byId("text-submit").disabled = snapshot.status !== "active" || state.switching;
  reportView();
}

function paintStatus(round) {
  const snapshot = state.snapshot;
  const evidenceMode = round?.context?.utterance?.source_mode;
  const fixture = evidenceMode === "replay" || (round?.provider_mode || snapshot.provider_mode) === "fixture";
  const live = (round?.provider_mode || snapshot.provider_mode) === "live";
  const source = byId("source-mode");
  source.dataset.tone = fixture ? "replay" : live ? "live" : "";
  source.textContent = fixture ? "REPLAY · 예시 플래너" : live ?
    "LIVE · " + (state.serviceCaps.planner === true && snapshot.cloud_state === "online" ?
      "AI 연결" : "AI 연결 확인 필요") : "직접 수정";
  const scene = assembly();
  const local = state.mic && !state.mic.closed;
  const recording = snapshot.capture_state === "recording" || (scene && local && !state.mic.stopping);
  const uploading = scene && local && state.mic.stopping;
  const capture = byId("capture-status");
  capture.dataset.tone = recording ? "live" : "";
  capture.textContent = (uploading ? "녹음 전송 중" : recording ? "녹음 중" :
    state.transcribing ? "발화 전사 중" : "마이크 꺼짐") +
    (snapshot.raw_audio_saved ? " · 원음 저장" : " · 원음 미보관");
  const active = snapshot.status === "active";
  const host = snapshot.audio_host_device_id === deviceId;
  const available = canRecord();
  const canSpeech = available && state.serviceCaps.microphone === true;
  show("audio-host", active && !recording && !host && canSpeech);
  show("record-button", active && host && canSpeech);
  write("record-button", uploading ? "음성 보내는 중" : local && !state.mic.stopping ? "녹음 종료" :
    scene ? "짧게 녹음" : "녹음 시작");
  byId("record-button").disabled = Boolean(state.micStarting || uploading ||
    (recording && (!state.mic || state.mic.stopping)));
  write("mic-note", available ?
    (snapshot.provider_mode === "fixture" ? "예시 발화와 직접 입력을 사용할 수 있습니다." :
      (!canSpeech ? "전사 서비스가 연결되면 브라우저 마이크를 사용할 수 있습니다." :
        (host ? (scene ? "최대 12초 동안 발언을 녹음해 연결된 모델의 수정 요청으로 사용합니다." : "") :
        "회의 마이크가 다른 장치에 지정되어 있습니다."))) :
    "마이크를 쓰려면 localhost 또는 인증서를 신뢰한 HTTPS에서 화면을 열어 주세요.");
  show("sample-button", active && snapshot.provider_mode === "fixture" && Boolean(snapshot.profile.demo_utterance));
}

function paintDebug(round) {
  const snapshot = state.snapshot;
  const pairs = [
    ["모델 표시", state.viewer ? "WebGL2 · glTF 미리보기" : "브라우저 GPU 연결 확인 필요"],
    ["마이크", canRecord() ? "권한 확인 후 사용" : "보안 연결 또는 브라우저 지원 필요"],
    ["공간 추적", "데스크톱 회전 뷰"],
    ["시선·자동 화자 식별", "지원하지 않음"],
    ["사진", "직접 추가한 사진만 사용"],
    ["확정 버전", snapshot.committed_version_id],
  ];
  if (round?.context) {
    const context = round.context;
    const partIds = context.part_ids || context.view?.part_ids || [];
    pairs.push(["근거 부위", partIds.map(id => snapshot.profile.semantic_parts.find(part => part.part_id === id)?.label || id)
      .join(", ") || "미확인"], ["편집 기준", round.edit_base_version_id], ["요청", context.request_id],
      ["AI에 사진 전달", context.images_used ? "사용됨" : "사용되지 않음"]);
    if (round.planner_started_at && round.planner_finished_at) {
      pairs.push(["후보 해석 시간", unitValue(round.planner_finished_at - round.planner_started_at, "ms")]);
    }
    const constraints = round.request_constraints || [];
    if (constraints.length) pairs.push(["요청 유지 조건", constraints.map(item => {
      const name = snapshot.profile.measured_properties?.find(property => property.property_id === item.property_id)?.label ||
        item.property_id;
      return name + " " + unitValue(item.value, item.unit);
    }).join(", ")]);
  }
  const dest = byId("device-info");
  const key = JSON.stringify(pairs);
  if (dest.dataset.key === key) return;
  dest.dataset.key = key;
  dest.replaceChildren();
  for (const [label, value] of pairs) {
    if (value) dest.append(element("dt", label), element("dd", String(value)));
  }
}

async function beforeUtterance() {
  // Use the model/selection the participant has already reported to the canonical session.
  try {
    await state.viewReports;
  } catch {
    throw new Error(assembly() ? "현재 모델의 연결을 확인한 뒤 다시 시도하세요." :
      "선택 부위가 아직 공유되지 않았습니다. 부위를 다시 선택한 뒤 입력하세요.");
  }
  if (!state.edge || !state.loadedVersion || state.switching) throw new Error("모델이 열린 뒤 부위를 확인해 주세요.");
}

byId("round-select").addEventListener("change", event => {
  state.pinnedRound = event.currentTarget.value;
  paint();
});

byId("text-form").addEventListener("submit", async event => {
  event.preventDefault();
  const form = event.currentTarget;
  const input = byId("speech-text");
  const text = input.value.trim();
  if (!text) return;
  try {
    await beforeUtterance();
    const result = await runCommand("text", { text }, form);
    if (result?.round_id) {
      if (input.value.trim() === text) input.value = "";
      // Preserve a candidate comparison already opened; other rounds remain selectable.
      if (assembly() || state.view?.kind !== "candidate") {
        state.pinnedRound = result.round_id;
        paint();
      }
    }
  } catch (error) {
    notice(message(error), true);
  }
});

byId("sample-button").addEventListener("click", async event => {
  const button = event.currentTarget;
  button.disabled = true;
  try {
    await beforeUtterance();
    const result = await runCommand("replay", { sample: "curves" });
    if (result?.round_id) {
      state.pinnedRound = result.round_id;
      paint();
    }
  } catch (error) {
    notice(message(error), true);
  } finally {
    button.disabled = false;
  }
});

byId("invite-button").addEventListener("click", () => {
  const snapshot = state.snapshot;
  if (!snapshot) return;
  const code = snapshot.join_expires_at > Date.now() ? snapshot.join_code : null;
  write("invite-code", code || "코드 만료");
  write("copy-code", code ? "코드 복사" : "새 코드 만들기");
  byId("invite-dialog").showModal();
});
byId("copy-code").addEventListener("click", async () => {
  let code = state.snapshot?.join_expires_at > Date.now() ? state.snapshot.join_code : null;
  if (!code) {
    const result = await runCommand("renew_code", {});
    if (!result) return;
    code = state.snapshot.join_code;
    write("invite-code", code);
    write("copy-code", "코드 복사");
    return;
  }
  try {
    await navigator.clipboard.writeText(code);
    write("copy-code", "복사됨");
  } catch {
    const range = document.createRange();
    range.selectNodeContents(byId("invite-code"));
    const selection = window.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
  }
});

byId("audio-host").addEventListener("click", () => runCommand("audio_host", {}));
byId("record-button").addEventListener("click", () => {
  if (state.mic && !state.mic.closed) {
    state.mic.stop();
    paintStatus(shownRound());
  } else {
    write("mic-consent-note", "회의 참가자의 녹음·전사 동의를 확인해 주세요. " +
      (state.snapshot.raw_audio_saved ? "현재 EDGE는 원음을 저장합니다." : "전사가 끝나면 원음은 보관하지 않습니다."));
    byId("bind-voice-view").checked = Boolean(assembly());
    write("bind-voice-label", assembly() ? "현재 미리보기의 원본 또는 후보를 발언에 연결합니다." :
      "이 마이크에서 내 발언만 수집하고 내 부위 선택을 연결합니다.");
    byId("mic-dialog").showModal();
  }
});

byId("allow-record").addEventListener("click", async () => {
  const edge = state.edge;
  byId("mic-dialog").close();
  if (!edge) return;
  state.micStarting = true;
  paintStatus(shownRound());
  try {
    await beforeUtterance();
    if (state.edge !== edge) return;
    state.mic = new MeetingMicrophone(edge, {
      shortClip: Boolean(assembly()),
      onFailure: error => {
        if (state.edge !== edge) return;
        notice(error, true);
        paintStatus(shownRound());
      },
      onClosed: () => {
        if (state.edge === edge) paintStatus(shownRound());
      },
    });
    await state.mic.start(deviceId, byId("bind-voice-view").checked);
  } catch (error) {
    if (state.edge === edge) notice(message(error), true);
  } finally {
    if (state.edge === edge) {
      state.micStarting = false;
      paintStatus(shownRound());
    }
  }
});

byId("media-input").addEventListener("change", async event => {
  const field = event.currentTarget;
  const file = field.files[0];
  const version = state.loadedVersion;
  if (!file || !version || !state.edge) return;
  const form = new FormData();
  form.set("file", file);
  form.set("version_id", version.version_id);
  if (state.selected.length) form.set("part_id", state.selected[0]);
  form.set("source_mode", "manual");
  form.set("source_monotonic_ms", String(performance.now()));
  for (const [key, value] of Object.entries(state.edge.clock || {})) form.set(key, String(value));
  try {
    await state.edge.upload(state.edge.path + "/media", form);
    notice("현재 모델 버전에 사진을 추가했습니다.");
    byId("media-panel").open = true;
  } catch (error) {
    notice(message(error), true);
  } finally {
    field.value = "";
  }
});

function renderMedia(snapshot) {
  const area = byId("media-items");
  const items = (snapshot.images || []).filter(image => image.version_id === state.loadedVersion?.version_id);
  show("media-panel", items.length > 0);
  const key = items.map(image => image.evidence_id).join(",");
  if (area.dataset.key === key) return;
  area.dataset.key = key;
  area.replaceChildren();
  if (state.mediaUrl) {
    URL.revokeObjectURL(state.mediaUrl);
    state.mediaUrl = null;
  }
  for (const image of items) {
    const part = snapshot.profile.semantic_parts.find(item => item.part_id === image.part_ids?.[0]);
    const label = (image.source_mode === "live_capture" ? "현장 촬영" : "직접 추가") + (part ? " · " + part.label : "");
    area.append(makeButton(label, "", async () => {
      const edge = state.edge;
      try {
        const blob = await edge.get(image.url, "blob");
        if (state.edge !== edge || state.loadedVersion?.version_id !== image.version_id) return;
        if (state.mediaUrl) URL.revokeObjectURL(state.mediaUrl);
        state.mediaUrl = URL.createObjectURL(blob);
        const tag = byId("reference-image");
        tag.src = state.mediaUrl;
        tag.alt = label;
        tag.hidden = false;
      } catch (error) {
        notice(message(error), true);
      }
    }));
  }
  const preview = byId("reference-image");
  preview.hidden = true;
  preview.removeAttribute("src");
}

function releaseAudio() {
  if (state.playback) {
    state.playback.pause();
    state.playback.remove();
    state.playback = null;
  }
  if (state.audioUrl) {
    URL.revokeObjectURL(state.audioUrl);
    state.audioUrl = null;
  }
}

async function loadAudio(utterance, container) {
  const edge = state.edge;
  if (!edge || !utterance.audio_url) return;
  try {
    const blob = await edge.get(utterance.audio_url, "blob");
    if (state.edge !== edge || !container.isConnected) return;
    releaseAudio();
    state.audioUrl = URL.createObjectURL(blob);
    const player = element("audio");
    player.controls = true;
    player.src = state.audioUrl;
    state.playback = player;
    container.append(player);
    await player.play().catch(() => {});
  } catch (error) {
    notice(message(error), true);
  }
}

function paintPrintResults(snapshot) {
  const version = state.loadedVersion;
  const key = version?.version_id;
  const form = byId("print-form");
  const results = (snapshot.print_results || []).filter(observation => observation.version_id === key);
  show("print-panel", Boolean(version?.stl_url));
  form.hidden = snapshot.status !== "active";
  if (form.dataset.version !== key) {
    form.reset();
    form.dataset.version = key || "";
  }
  const output = byId("print-results");
  const recordsKey = JSON.stringify([key, results]);
  if (recordsKey === output.dataset.key) return;
  output.dataset.key = recordsKey;
  output.replaceChildren();
  if (!results.length) {
    output.append(element("p", "이 버전의 실제 출력 기록이 없습니다.", "subtle"));
    return;
  }
  for (const result of results) {
    const name = snapshot.participants.find(person => person.participant_id === result.reported_by)?.display_name;
    const stamp = Number.isFinite(result.printed_at) ? new Date(result.printed_at).toLocaleString("ko-KR") : "";
    output.append(element("p", [name, stamp, result.slicer_scale_confirmed ? "슬라이서 배율 확인됨" : "슬라이서 배율 확인 미기록"].filter(Boolean).join(" · "), "subtle"));
    if (result.fit_observations) output.append(element("p", result.fit_observations));
    if (result.issues) output.append(element("p", result.issues));
  }
}

byId("print-form").addEventListener("submit", async event => {
  event.preventDefault();
  const form = event.currentTarget;
  const versionId = form.dataset.version;
  if (!versionId || state.loadedVersion?.version_id !== versionId) return;
  const result = await runCommand("printed", {
    version_id: versionId, slicer_scale_confirmed: byId("print-scale").checked,
    fit_observations: byId("fit-notes").value.trim(), issues: byId("print-issues").value.trim(),
  }, form);
  if (result) form.reset();
});

window.addEventListener("pagehide", () => {
  state.mic?.close();
  releaseAudio();
});
let savedIdentity;
try { savedIdentity = JSON.parse(storage.get(storageKey)); } catch { storage.clear(storageKey); }
if (savedIdentity?.token) startSession(savedIdentity);
else checkHealth();
loadProfiles();

request("/v1/capabilities").then(capabilities => {
  state.serviceCaps = capabilities;
  if (state.snapshot) paintStatus(shownRound());
}).catch(() => {});
