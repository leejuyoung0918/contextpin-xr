"use strict";

const byId = (id) => document.getElementById(id);
const phases = {
    setup: "보드 등록", before_action: "동작 전", after_action: "동작 후",
    clarification: "보충 근거", student_checkpoint: "학생 확인 사진"
};
const stages = {
    registration: "보드 등록", recording: "교수 시연 기록", review: "검토 대기",
    approved: "승인 완료", practice: "학생 실습", complete: "실습 완료"
};
const provenance = {
    real: "실물 기록", synthetic: "예시 입력", replay: "기록 재생",
    real_camera: "헤드셋 카메라", manual: "수동 입력"
};
const commands = {
    capture: "장면 저장", saveDraft: "수정안 적용", approveDraft: "가이드 승인",
    manualTranscript: "수동 전사"
};

let pair = null;
let report = null;
let draft = null;
let editingRevision = -1;
let stepIndex = 0;
let selectedPhoto = "";
let selectedEndpoint = "fromPoint";
let dirty = false;
let polling = false;
let sending = false;
let lastRequest = null;
let pollTimer = null;

function notify(text, error = false) {
    byId("feedback").textContent = text;
    byId("feedback").classList.toggle("error", error);
}

async function message(path, body, withToken = true) {
    const headers = {"Content-Type": "application/json"};
    if (withToken && pair) headers.Authorization = "Bearer " + pair.teacherToken;
    const response = await fetch(path, {method: "POST", headers, body: JSON.stringify(body)});
    const value = await response.json();
    if (!response.ok) throw new Error(value.error || "PC 연결 상태를 확인하세요.");
    return value;
}

function options(select, values, selected = "") {
    select.replaceChildren(...values.map(([value, label]) => new Option(label, value)));
    select.value = selected || "";
    if (select.selectedIndex === -1) select.selectedIndex = 0;
}

function formatTime(time) {
    if (!Number.isFinite(time) || time < 0) return "시각 미기록";
    const seconds = Math.floor(time);
    return String(Math.floor(seconds / 60)).padStart(2, "0") + ":" + String(seconds % 60).padStart(2, "0");
}

function readOnly() {
    return report.snapshot.stage !== "review";
}

function currentStep() {
    return draft?.steps?.[stepIndex];
}

function stepLabel(step) {
    const labels = {prepare: "준비", resistor: "저항", led: "LED", ground: "접지",
                    check: "점검·점등", explain: "보충 설명"};
    return step.title || labels[step.kind] || step.kind || "단계";
}

function isConnection(step) {
    return ["resistor", "led", "ground"].includes(step?.kind);
}

function noteFor(id) {
    return report.snapshot.notes?.find((note) => note.id === id);
}

function noteText(note) {
    return note?.quote || note?.transcript || "";
}

function commitDraft() {
    if (!draft || readOnly()) return;
    const step = currentStep();
    if (step) {
        const source = byId("step-source").value;
        if (source !== (step.sourceNoteId || "")) {
            step.sourceNoteId = source;
            step.sourceQuote = noteText(noteFor(source));
            rememberEdit(step, "source");
        }
        const action = byId("step-action").value;
        if (action !== (step.instruction || "")) {
            step.instruction = action;
            rememberEdit(step, "instruction");
        }
        const reason = byId("step-reason").value;
        if (reason !== (step.reason || "")) {
            step.reason = reason;
            step.rationaleSource = "teacher_edit";
            rememberEdit(step, "reason");
        }
        const caution = byId("step-caution").value;
        if (caution !== (step.caution || "")) {
            step.caution = caution;
            rememberEdit(step, "caution");
        }
        if (isConnection(step)) {
            for (const [attr, id] of [["fromPoint", "step-from"], ["toPoint", "step-to"]]) {
                const value = byId(id).value;
                if (value !== (step[attr] || "")) {
                    step[attr] = value;
                    rememberEdit(step, attr);
                }
            }
            if (step.kind === "led") {
                const direction = byId("polarity").checked ? "anode_to_cathode" : "";
                if (direction !== (step.ledDirection || "")) rememberEdit(step, "ledDirection");
                step.ledDirection = direction;
            }
        }
    }
    draft.kitConditions = byId("kit-conditions").value;
    draft.kitConfirmed = byId("kit-confirmed").checked;
}

function rememberEdit(step, field) {
    step.teacherEditedFields = Array.from(new Set([...(step.teacherEditedFields || []), field]));
}

function clearMedia(holder) {
    if (holder.dataset.url) URL.revokeObjectURL(holder.dataset.url);
    delete holder.dataset.url;
    delete holder.dataset.source;
    holder.replaceChildren();
}

async function showMedia(holder, hash, kind, unavailable) {
    const id = pair?.sessionId;
    const key = id + "/" + (hash || "-");
    if (holder.dataset.source === key) return;
    clearMedia(holder);
    holder.dataset.source = key;
    if (!hash || !id) {
        holder.textContent = unavailable;
        return;
    }
    holder.textContent = "자료를 여는 중입니다.";
    try {
        const response = await fetch("/transfer/evidence/" + encodeURIComponent(id) + "/" + encodeURIComponent(hash), {
            headers: {Authorization: "Bearer " + pair.teacherToken}
        });
        if (!response.ok) throw new Error("자료를 아직 PC에서 열 수 없습니다.");
        const blob = await response.blob();
        if (holder.dataset.source !== key) return;
        holder.dataset.url = URL.createObjectURL(blob);
        const media = document.createElement(kind);
        media.src = holder.dataset.url;
        if (kind === "img") {
            media.alt = "저장된 실습 장면";
        } else {
            media.controls = true;
            media.preload = "metadata";
        }
        holder.replaceChildren(media);
    } catch (error) {
        if (holder.dataset.source === key) holder.textContent = error.message;
    }
}

function renderReference(note) {
    byId("source-text").textContent = noteText(note) || "연결할 발화가 지정되지 않았습니다.";
    showMedia(byId("audio"), note?.audioSha256, "audio",
              "이 발화에는 재생할 원음이 없습니다.");
}

function allCaptures() {
    const captures = [...(report.snapshot.captures || [])];
    const note = noteFor(currentStep()?.sourceNoteId);
    if (note) {
        for (const hash of (note.photoSha256s || [])) {
            if (!captures.some((item) => item.sha256 === hash)) {
                captures.push({captureId: hash, sha256: hash, phase: "clarification",
                              source: note.provenance || report.snapshot.sourceMode, alongsideNote: true});
            }
        }
    }
    return captures.filter((item) => item.sha256);
}

function photoEvidence(captureId) {
    return (draft?.evidence || []).find((item) => item.kind === "image" && item.captureId === captureId);
}

function renderPhoto() {
    const captures = allCaptures();
    const step = currentStep();
    if (!captures.some((item) => item.captureId === selectedPhoto)) {
        selectedPhoto = captures.find((item) => step?.evidenceIds?.includes(photoEvidence(item.captureId)?.id))?.captureId
            || captures.find((item) => item.alongsideNote)?.captureId || (!step ? captures.at(-1)?.captureId : "") || "";
    }
    options(byId("capture-list"), [["", "사진 선택"], ...captures.map((item) => {
        const label = [phases[item.phase] || "장면",
                       formatTime(item.sessionTimeMs / 1000),
                       provenance[item.source] || item.source].filter(Boolean).join(" · ");
        return [item.captureId, label];
    })], selectedPhoto);
    const capture = captures.find((item) => item.captureId === selectedPhoto);
    const evidence = photoEvidence(selectedPhoto);
    const linked = evidence && step?.evidenceIds?.includes(evidence.id);
    byId("photo-detail").textContent = capture
        ? [capture.quality, linked ? "이 단계의 사진 근거" : "", provenance[capture.source] || capture.source].filter(Boolean).join(" · ")
        : "실제 저장된 사진만 이 목록에 나타납니다.";
    byId("photo-tools").hidden = readOnly() || !evidence || !step;
    byId("link-photo").disabled = Boolean(linked);
    byId("unlink-photo").disabled = !linked;
    showMedia(byId("photo"), capture?.sha256, "img", "선택한 사진이 없습니다.");
}

function renderBoard(step, points) {
    const grid = byId("points");
    const pieces = points.map((point) => {
        const button = document.createElement("button");
        button.type = "button";
        button.textContent = point.code || point.label || point.id;
        button.title = point.label || point.id;
        button.disabled = readOnly() || !isConnection(step);
        if (point.id === step?.fromPoint) button.dataset.selection = "from";
        if (point.id === step?.toPoint) button.dataset.selection = "to";
        button.addEventListener("click", () => {
            const from = selectedEndpoint === "fromPoint";
            const field = from ? "step-from" : "step-to";
            byId(field).value = point.id;
            dirty = true;
            commitDraft();
            selectedEndpoint = from ? "toPoint" : "fromPoint";
            renderBoard(step, points);
        });
        return button;
    });
    grid.replaceChildren(...pieces);
    byId("selection-hint").textContent = isConnection(step) && !readOnly()
        ? (selectedEndpoint === "fromPoint" ? "보드에서 첫 위치를 지정할 수 있어요." : "보드에서 둘째 위치를 지정할 수 있어요.")
        : "단자 표시는 승인 가이드의 보드 프로필을 따릅니다.";
}

function renderDraft() {
    const step = currentStep();
    const editable = !readOnly();
    const notes = report.snapshot.notes || [];
    const points = report.snapshot.points || [];
    const entries = (draft?.steps || []).map((item, index) => {
        const li = document.createElement("li");
        const button = document.createElement("button");
        const missing = (!item.sourceNoteId && item.kind !== "explain") ||
            (isConnection(item) && (!item.fromPoint || !item.toPoint || (item.kind === "led" && !item.ledDirection)));
        button.textContent = (index + 1) + ". " + stepLabel(item) + (missing ? " · 확인 필요" : "");
        if (index === stepIndex) button.setAttribute("aria-current", "step");
        button.addEventListener("click", () => {
            commitDraft();
            stepIndex = index;
            selectedPhoto = "";
            selectedEndpoint = "fromPoint";
            renderDraft();
        });
        li.append(button);
        return li;
    });
    byId("step-list").replaceChildren(...entries);
    byId("reorder-tools").hidden = !editable;
    byId("split-tools").hidden = !editable;
    const steps = draft?.steps || [];
    byId("step-up").disabled = !step || stepIndex <= 1 || stepIndex >= steps.length - 1;
    byId("step-down").disabled = !step || stepIndex === 0 || stepIndex >= steps.length - 2;
    byId("step-split").disabled = !step || stepIndex === 0;
    byId("step-merge").disabled = step?.kind !== "explain" || stepIndex >= steps.length - 1;
    byId("step-fields").hidden = !step;
    byId("manual-box").hidden = !editable;
    const noteOptions = notes.map((note) => [note.id, formatTime(note.time) + " · " + (noteText(note).slice(0, 72) || "전사 대기")]);
    const pointOptions = points.map((point) => [point.id, point.label || point.code || point.id]);
    options(byId("step-source"), [["", "발화 선택"], ...noteOptions], step?.sourceNoteId);
    options(byId("manual-note"), [["", "교수가 나중에 입력한 설명"], ...noteOptions], byId("manual-note").value);
    options(byId("step-from"), [["", "미정"], ...pointOptions], step?.fromPoint);
    options(byId("step-to"), [["", "미정"], ...pointOptions], step?.toPoint);
    byId("from-label").textContent = step?.kind === "led" ? "양극 위치" : "첫 연결 위치";
    byId("to-label").textContent = step?.kind === "led" ? "음극 위치" : "둘째 연결 위치";
    byId("step-action").value = step?.instruction || "";
    byId("step-reason").value = step?.reason || "";
    byId("step-caution").value = step?.caution || "";
    byId("polarity").checked = step?.ledDirection === "anode_to_cathode";
    byId("polarity-label").hidden = step?.kind !== "led";
    byId("connection-fields").hidden = !isConnection(step);
    for (const id of ["step-source", "step-from", "step-to", "step-action", "step-reason", "step-caution", "polarity"]) {
        byId(id).disabled = !editable;
    }
    byId("kit-conditions").value = draft?.kitConditions || "";
    byId("kit-confirmed").checked = draft?.kitConfirmed === true;
    renderReference(noteFor(step?.sourceNoteId));
    renderBoard(step, points);
    renderPhoto();
}

function renderInfo() {
    const state = report.snapshot;
    byId("guide-title").textContent = state.guide?.title || "교수 시연";
    byId("session-status").textContent = [provenance[state.sourceMode], stages[state.stage], report.deviceLabel].filter(Boolean).join(" · ");
    const caps = state.capabilities || {};
    const capLabels = {unverified: "미확인", unavailable: "사용 불가", frame_received: "프레임 수신",
                       configured: "설정됨", unconfigured: "미설정"};
    const describe = (label, value) => value ? label + " " + (capLabels[value] || value) : "";
    byId("capabilities").textContent = caps.summary ||
        [describe("카메라", caps.camera), describe("마이크", caps.microphone),
         describe("STT", caps.speech || caps.stt), describe("사진 비교", caps.vision)].filter(Boolean).join(" · ");
    const available = {
        registration: ["setup"], recording: ["before_action", "after_action", "clarification"],
        practice: ["student_checkpoint", "clarification"]
    }[state.stage] || [];
    byId("capture-panel").hidden = state.sourceMode !== "real" || available.length === 0;
    if (available.join() !== byId("capture-phase").dataset.phases) {
        byId("capture-phase").dataset.phases = available.join();
        options(byId("capture-phase"), available.map((phase) => [phase, phases[phase]]));
    }
    byId("review-panel").hidden = !draft && state.stage !== "review" && !state.captures?.length;
    byId("approval-panel").hidden = state.stage !== "review" || !draft?.steps?.length;
    byId("approval-issue").textContent = state.approvalIssue || "";
    const commandsList = report.commands.slice(0, 5).map((item) => {
        const li = document.createElement("li");
        const status = {pending: "헤드셋 응답 대기", applied: "기기에서 처리됨", rejected: "기기에서 거절됨"}[item.status];
        li.textContent = [commands[item.kind] || item.kind, status, item.detail].filter(Boolean).join(" · ");
        return li;
    });
    byId("command-panel").hidden = !commandsList.length;
    byId("command-list").replaceChildren(...commandsList);
    const help = Array.isArray(state.help) ? state.help.at(-1) : state.help;
    const activeHelp = help?.question || help?.stepId;
    byId("help-panel").hidden = !activeHelp;
    const content = byId("help-card");
    const helpKey = JSON.stringify(help || {});
    if (activeHelp && content.dataset.key !== helpKey) {
        const old = content.querySelector(".media");
        if (old) clearMedia(old);
        content.dataset.key = helpKey;
        content.replaceChildren();
        for (const text of [help.stepId, help.question, help.sourceQuote, help.attempted, help.createdAt]) {
            if (typeof text !== "string" || !text) continue;
            const paragraph = document.createElement("p");
            paragraph.textContent = text;
            content.append(paragraph);
        }
        const photo = document.createElement("div");
        photo.className = "media";
        content.append(photo);
        const capture = (state.captures || []).find((item) => item.captureId === help.captureId);
        showMedia(photo, capture?.sha256, "img", "학생의 확인 사진이 아직 없습니다.");
    }
    const waiting = report.commands.some((item) => item.status === "pending");
    for (const id of ["ask-capture", "save-draft", "approve-draft", "save-manual"]) {
        byId(id).disabled = waiting || sending;
    }
    byId("download-report").hidden = false;
}

async function refresh() {
    if (!pair || polling) return;
    polling = true;
    const active = pair;
    try {
        const newest = await message("/transfer/teacher/state", {sessionId: pair.sessionId});
        if (active !== pair) return;
        report = newest;
        const changed = editingRevision !== report.snapshot.revision;
        if (changed && (!dirty || readOnly())) {
            draft = report.snapshot.guide ? structuredClone(report.snapshot.guide) : null;
            editingRevision = report.snapshot.revision;
            stepIndex = Math.max(0, Math.min(stepIndex, (draft?.steps?.length || 1) - 1));
            selectedPhoto = "";
            dirty = false;
            renderDraft();
        } else if (changed) {
            notify("헤드셋에서 새 수정이 들어왔습니다. 연결을 다시 열어 최신 초안에서 검토하세요.", true);
        }
        renderInfo();
    } catch (error) {
        if (active === pair) notify(error.message, true);
    } finally {
        polling = false;
    }
}

async function command(kind, payload) {
    if (!pair || !report || sending) return false;
    const expectedRevision = report.snapshot.revision;
    if (dirty && expectedRevision !== editingRevision && kind !== "capture") {
        notify("헤드셋과 PC의 초안이 다릅니다. 최신 상태에서 다시 확인하세요.", true);
        return false;
    }
    const requestKey = JSON.stringify([kind, expectedRevision, payload]);
    if (!lastRequest || lastRequest.key !== requestKey) {
        lastRequest = {key: requestKey, id: crypto.randomUUID()};
    }
    sending = true;
    renderInfo();
    const active = pair;
    try {
        const value = await message("/transfer/teacher/command", {sessionId: pair.sessionId,
            commandId: lastRequest.id, kind, expectedRevision, payload});
        if (active !== pair) return false;
        if (value.status === "rejected") {
            notify(value.detail || "헤드셋이 이전 요청을 거절했습니다.", true);
            return false;
        }
        if (kind === "approveDraft" || kind === "saveDraft") dirty = false;
        notify("헤드셋에 요청을 보냈습니다. 아래에서 처리 상태를 확인하세요.");
        await refresh();
        return true;
    } catch (error) {
        if (active === pair) notify(error.message, true);
        return false;
    } finally {
        sending = false;
        if (active === pair) renderInfo();
    }
}

function rebuildDependencies() {
    draft.steps.forEach((step, index) => {
        if (!step.stepId) step.stepId = step.kind + "-" + (index + 1);
    });
    draft.steps.forEach((step, index) => {
        step.prerequisites = index > 0 ? [draft.steps[index - 1].stepId] : [];
    });
}

function reorder(offset) {
    commitDraft();
    const target = stepIndex + offset;
    if (Math.min(stepIndex, target) < 1 || Math.max(stepIndex, target) >= draft.steps.length - 1) return;
    [draft.steps[stepIndex], draft.steps[target]] = [draft.steps[target], draft.steps[stepIndex]];
    stepIndex = target;
    rebuildDependencies();
    dirty = true;
    renderDraft();
}

function splitExplanation() {
    commitDraft();
    const source = currentStep();
    if (!source || stepIndex === 0 || draft.steps.length >= 20) return;
    const instruction = source.teacherNote || source.caution || source.reason;
    if (!instruction) {
        notify("주의 또는 이유 내용을 입력한 후 설명 단계를 나누세요.", true);
        return;
    }
    draft.steps.splice(stepIndex, 0, {kind: "explain", stepId: "explain-" + crypto.randomUUID(),
        instruction, reason: "", completion: "설명을 확인했습니다.", teacherNote: instruction, rationaleSource: "teacher_edit",
        evidenceIds: source.evidenceIds || [], teacherEditedFields: ["instruction"],
        sourceNoteId: source.sourceNoteId || "", sourceQuote: source.sourceQuote || ""});
    rebuildDependencies();
    dirty = true;
    renderDraft();
}

function mergeExplanation() {
    commitDraft();
    const source = currentStep();
    const next = draft?.steps?.[stepIndex + 1];
    if (source?.kind !== "explain" || !next) return;
    next.teacherNote = [source.teacherNote || source.instruction, next.teacherNote].filter(Boolean).join("\n");
    next.evidenceIds = Array.from(new Set([...(source.evidenceIds || []), ...(next.evidenceIds || [])]));
    rememberEdit(next, "teacherNote");
    draft.steps.splice(stepIndex, 1);
    rebuildDependencies();
    dirty = true;
    renderDraft();
}

function linkPhoto(include) {
    commitDraft();
    const evidence = photoEvidence(selectedPhoto);
    const step = currentStep();
    if (!evidence || !step) return;
    const ids = new Set(step.evidenceIds || []);
    include ? ids.add(evidence.id) : ids.delete(evidence.id);
    step.evidenceIds = Array.from(ids);
    rememberEdit(step, "evidenceIds");
    dirty = true;
    renderPhoto();
}

byId("pair-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    const button = event.currentTarget.querySelector("button");
    button.disabled = true;
    try {
        pair = await message("/transfer/teacher/pair", {pairingCode: byId("pair-code").value}, false);
        byId("pair-code").value = "";
        byId("pair-panel").hidden = true;
        byId("session").hidden = false;
        notify("");
        await refresh();
        pollTimer = setInterval(refresh, 1200);
    } catch (error) {
        notify(error.message, true);
    } finally {
        button.disabled = false;
    }
});

byId("disconnect").addEventListener("click", () => {
    clearInterval(pollTimer);
    pair = report = draft = null;
    editingRevision = -1;
    stepIndex = 0;
    selectedPhoto = "";
    selectedEndpoint = "fromPoint";
    dirty = false;
    clearMedia(byId("audio"));
    clearMedia(byId("photo"));
    const help = byId("help-card");
    if (help.querySelector(".media")) clearMedia(help.querySelector(".media"));
    help.replaceChildren();
    delete help.dataset.key;
    byId("pair-panel").hidden = false;
    byId("session").hidden = true;
    notify("");
});

byId("ask-capture").addEventListener("click", () => command("capture", {phase: byId("capture-phase").value}));
byId("save-draft").addEventListener("click", () => {
    commitDraft();
    command("saveDraft", {steps: draft.steps, kitConfirmed: draft.kitConfirmed, kitConditions: draft.kitConditions});
});
byId("approve-draft").addEventListener("click", () => {
    commitDraft();
    command("approveDraft", {steps: draft.steps, kitConfirmed: draft.kitConfirmed, kitConditions: draft.kitConditions});
});
byId("save-manual").addEventListener("click", async () => {
    const text = byId("manual-text").value.trim();
    if (!text) {
        notify("교수 보충이나 전사할 내용을 입력하세요.", true);
        return;
    }
    const sourceNoteId = byId("manual-note").value;
    const payload = sourceNoteId ? {text, sourceNoteId} : {text};
    if (await command("manualTranscript", payload)) byId("manual-text").value = "";
});
for (const id of ["step-source", "step-action", "step-reason", "step-caution", "step-from", "step-to", "polarity",
                  "kit-conditions", "kit-confirmed"]) {
    byId(id).addEventListener("input", () => {
        dirty = true;
        commitDraft();
        if (id === "step-source") {
            renderReference(noteFor(currentStep()?.sourceNoteId));
            selectedPhoto = "";
            renderPhoto();
        }
        if (id === "step-from" || id === "step-to") renderBoard(currentStep(), report.snapshot.points || []);
    });
}
for (const [id, endpoint] of [["step-from", "fromPoint"], ["step-to", "toPoint"]]) {
    byId(id).addEventListener("focus", () => {
        selectedEndpoint = endpoint;
        renderBoard(currentStep(), report.snapshot.points || []);
    });
}
byId("capture-list").addEventListener("change", (event) => {
    selectedPhoto = event.target.value;
    renderPhoto();
});
byId("step-up").addEventListener("click", () => reorder(-1));
byId("step-down").addEventListener("click", () => reorder(1));
byId("step-split").addEventListener("click", splitExplanation);
byId("step-merge").addEventListener("click", mergeExplanation);
byId("link-photo").addEventListener("click", () => linkPhoto(true));
byId("unlink-photo").addEventListener("click", () => linkPhoto(false));
let reportUrl = null;
byId("download-report").addEventListener("click", (event) => {
    if (reportUrl) URL.revokeObjectURL(reportUrl);
    reportUrl = URL.createObjectURL(new Blob([JSON.stringify(report, null, 2)], {type: "application/json"}));
    event.currentTarget.href = reportUrl;
});

fetch("/health/capabilities").then((result) => result.json()).then((status) => {
    byId("health").textContent = status.speechProvider?.configured
        ? "PC 연결됨 · 자동 전사 제공자가 설정돼 있습니다."
        : "PC 연결됨 · 자동 전사 설정이 비어 있어 수동 전사를 사용할 수 있습니다.";
}).catch(() => {
    byId("health").textContent = "PC 서버에 연결할 수 없습니다.";
});
