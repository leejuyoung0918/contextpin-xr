export const byId = id => document.getElementById(id);

export function element(tag, text, className) {
  const node = document.createElement(tag);
  if (typeof text === "string") node.textContent = text;
  if (className) node.className = className;
  return node;
}

export function makeButton(label, mode = "", action, disabled = false) {
  const button = element("button", label, "button small " + mode);
  button.type = "button";
  button.disabled = disabled;
  if (action) button.addEventListener("click", action);
  return button;
}

function setOptions(select, values, selected) {
  for (const [value, label] of values) {
    const option = element("option", label);
    option.value = value;
    select.append(option);
  }
  if (selected !== undefined) select.value = selected;
}

function appendSubmit(form, label, disabled = false) {
  const button = makeButton(label, "", null, disabled);
  button.type = "submit";
  form.append(button);
}

const letter = index => String.fromCharCode(65 + index);
const unitsFormatter = new Intl.NumberFormat("ko-KR", { maximumFractionDigits: 3 });

export function unitValue(value, unit) {
  return (typeof value === "number" ? unitsFormatter.format(value) : String(value)) + (unit ? " " + unit : "");
}

export const versionLabel = (versionId, versions) => {
  const index = versions.findIndex(version => version.version_id === versionId);
  return index < 0 ? "" : "v" + (index + 1);
};

const roundLabels = {
  collecting: "발언 근거를 모으는 중",
  needs_input: "변경 내용을 한 가지 확인해 주세요.",
  generating: "말씀을 해석하는 중",
  planning: "말씀을 해석하는 중",
  building: "3D 형상을 만드는 중",
  reviewable: "후보를 열어 모델과 변경 치수를 확인하세요.",
  committed: "최종 모델이 기록되었습니다.",
  deferred: "검토가 보류되었습니다.",
  rejected: "후보가 기각되었습니다.",
  dismissed: "검토가 종료되었습니다.",
  failed: "후보를 생성하지 못했습니다.",
  superseded: "새 모델이나 조건이 적용된 뒤의 발언으로 다시 요청할 수 있습니다.",
  cancelled: "형상 생성을 취소했습니다.",
  recorded: "발언을 기록했습니다.",
  answered: "발언을 기록했습니다.",
};
const buildStates = {
  planned: "생성 예정", queued: "생성 대기", building: "형상 생성 중", valid: "형상 검사 통과",
  invalid: "형상 검사 실패", failed: "형상 생성 실패", cancelled: "생성 취소", superseded: "이전 요청",
};

export function roundLabel(round) {
  return roundLabels[round?.status] || "모델과 부위를 확인한 뒤 대화에 수정 요청을 입력하세요.";
}

function operationChanges(candidate, round, snapshot) {
  const base = snapshot.versions.find(version => version.version_id === round.edit_base_version_id ||
    version.version_id === round.context?.edit_base_version_id);
  return (candidate.operations || []).map(operation => {
    const id = operation.parameter_id;
    const definition = snapshot.profile.parameters.find(parameter => parameter.parameter_id === id);
    if (!definition) return null;
    const oldValue = base?.parameters?.[id];
    const before = oldValue === undefined ? "" : unitValue(oldValue, definition.unit) + " → ";
    const after = unitValue(operation.value, definition.unit);
    const provenance = snapshot.assembly && operation.source === "AI_PROPOSAL" ? " (비교용 제안값)" : "";
    return (definition.label || id) + " " + before + after + provenance;
  }).filter(Boolean);
}

export function renderRoundsMenu(rounds, focus, label) {
  const select = byId("round-select");
  const options = rounds.map((round, index) => [round.round_id, "라운드 " + (rounds.length - index) + " · " + label(round)]);
  const key = JSON.stringify(options);
  if (key !== select.dataset.key) {
    select.replaceChildren();
    setOptions(select, options);
    select.dataset.key = key;
  }
  if (focus) select.value = focus;
  select.hidden = rounds.length < 2;
}

function renderClarification(round, callbacks) {
  const form = element("form", null, "question");
  const label = element("label", typeof round.question === "string" ? round.question :
    round.question?.text || "바꿀 부위나 치수를 알려주세요.");
  label.htmlFor = "clarification";
  const input = element("input");
  input.id = "clarification";
  input.maxLength = 1200;
  input.required = true;
  form.append(label, input);
  appendSubmit(form, "답변 반영");
  form.addEventListener("submit", event => {
    event.preventDefault();
    callbacks.command("clarify", { round_id: round.round_id, text: input.value.trim() }, form);
  });
  return form;
}

function renderCandidates(round, snapshot, activeCandidate, callbacks) {
  const list = element("div", null, "candidate-list");
  for (const [index, candidate] of round.candidates.entries()) {
    const card = element("article", null, candidate.candidate_id === activeCandidate ? "active" : null);
    const heading = element("div", null, "candidate-heading");
    heading.append(element("h3", letter(index) + " · " + (candidate.title || "수정안")));
    if (candidate.status === "valid" && candidate.version_id) {
      heading.append(makeButton(candidate.candidate_id === activeCandidate ? "보고 있음" : "모델 보기",
        "", () => callbacks.view(round, candidate)));
    }
    card.append(heading);
    if (candidate.interpretation) card.append(element("p", candidate.interpretation, "candidate-description"));
    const changes = operationChanges(candidate, round, snapshot);
    if (changes.length) card.append(element("p", changes.join(" · "), "parameters-changed"));
    const message = candidate.error || candidate.failure_reason || buildStates[candidate.status];
    if (message) card.append(element("p", message, "subtle"));
    list.append(card);
  }
  return list;
}

const responseKinds = {
  interpretation: "내가 이해한 뜻",
  preference: "내가 선호하는 디자인",
};

function renderResponse(round, kind, actor, callbacks) {
  const form = element("form", null, "response-form");
  const own = (round.responses || []).findLast(item =>
    item.participant_id === actor && (item.kind === kind || item.kind ===
      (kind === "interpretation" ? "matches_my_interpretation" : "design_preference")));
  const candidates = round.candidates.filter(item => item.status === "valid" && item.version_id);
  const label = element("label", responseKinds[kind]);
  const select = element("select");
  select.id = "answer-" + kind;
  label.htmlFor = select.id;
  const options = [["", "선택해 주세요"],
    ...candidates.map(candidate => [candidate.candidate_id, letter(round.candidates.indexOf(candidate)) + " · " + candidate.title]),
    ["combination", "후보 조합"], ["none", "해당 없음"], ["unsure", "잘 모르겠음"]];
  setOptions(select, options, own?.choice === "candidate" ? own.candidate_ids[0] :
    own?.choice || own?.response || "");
  select.required = true;
  const combinations = element("div", null, "choice-list");
  combinations.hidden = select.value !== "combination";
  for (const candidate of candidates) {
    const option = element("label", letter(round.candidates.indexOf(candidate)));
    const checkbox = element("input");
    checkbox.type = "checkbox";
    checkbox.value = candidate.candidate_id;
    checkbox.checked = Boolean(own?.candidate_ids?.includes(candidate.candidate_id));
    option.prepend(checkbox);
    combinations.append(option);
  }
  select.addEventListener("change", () => {
    combinations.hidden = select.value !== "combination";
  });
  form.append(label, select, combinations);
  appendSubmit(form, "저장");
  form.addEventListener("submit", event => {
    event.preventDefault();
    let choice = select.value;
    let ids = [];
    if (choice === "combination") {
      ids = Array.from(combinations.querySelectorAll(":checked"), item => item.value);
      if (ids.length < 2) {
        callbacks.error("조합할 후보를 두 개 이상 선택하세요.");
        return;
      }
    } else if (candidates.some(candidate => candidate.candidate_id === choice)) {
      ids = [choice];
      choice = "candidate";
    }
    callbacks.command("response", { round_id: round.round_id, kind, choice, candidate_ids: ids }, form);
  });
  return form;
}

function renderDecision(round, snapshot, activeCandidate, reasonDraft, callbacks) {
  const form = element("form", null, "decision-form");
  const label = element("label", "결정 이유");
  label.htmlFor = "decision-reason";
  const reason = element("input");
  reason.id = label.htmlFor;
  reason.maxLength = 600;
  reason.placeholder = "선택 사항";
  if (reasonDraft?.text) reason.value = reasonDraft.text;
  let source;
  if (snapshot.session_status) {
    const recent = [...new Map((snapshot.utterances || []).filter(item => item.status === "final")
      .map(item => [item.utterance_id, item])).values()].slice(-6).reverse();
    const select = element("select");
    select.id = "decision-source";
    const sourceLabel = element("label", "결정 이유로 연결할 최근 발언");
    sourceLabel.htmlFor = select.id;
    setOptions(select, [["", "발언 선택 안 함"], ...recent.map(item => [item.utterance_id,
      item.text.length > 72 ? item.text.slice(0, 71) + "…" : item.text])], reasonDraft?.source || "");
    if (recent.length) {
      form.append(label, reason, sourceLabel, select);
      source = select;
    }
  }
  const candidate = round.candidates.find(item =>
    item.candidate_id === activeCandidate && item.status === "valid" && item.version_id);
  const actions = element("div", null, "file-actions");
  const save = outcome => callbacks.command("decide", {
    round_id: round.round_id,
    revision: round.revision,
    expected_committed_version_id: snapshot.committed_version_id,
    expected_adopted_revision: snapshot.adopted_revision,
    expected_constraint_revision: snapshot.constraint_revision,
    candidate_id: outcome === "commit" ? candidate?.candidate_id : undefined,
    reason: reason.value.trim() || undefined,
    reason_utterance_id: reason.value.trim() ? undefined : source?.value || undefined,
    outcome,
  }, form);
  if (snapshot.assembly) {
    actions.append(makeButton(snapshot.session_status ? "이 안 채택" : "이 모델로 결정", "primary", () => save("commit"), !candidate),
      makeButton("새 수정 요청", "", () => callbacks.refine(false)));
    if (candidate) actions.append(makeButton("이 후보에서 후속 수정", "", () => callbacks.refine(true)));
  } else {
    actions.append(makeButton("이 모델로 결정", "primary", () => save("commit"), !candidate),
      makeButton("보류", "", () => save("defer")), makeButton("기각", "", () => save("reject")));
  }
  if (!source) form.append(label, reason);
  form.append(actions);
  if (!candidate) form.append(element("p", "채택하려는 후보의 모델을 먼저 여세요.", "subtle"));
  form.addEventListener("submit", event => event.preventDefault());
  return form;
}

export function renderRound(round, snapshot, { actor, presenter, candidateId, interactive = true }, callbacks) {
  byId("round-status").textContent = roundLabel(round);
  const destination = byId("round-content");
  const key = JSON.stringify([round?.round_id, round?.revision, round?.status, round?.question, round?.candidates,
    round?.responses, round?.acks, candidateId, presenter, interactive, snapshot.committed_version_id, snapshot.constraint_revision,
    snapshot.session_status ? snapshot.utterances.slice(-6).map(item => [item.utterance_id, item.revision]) : null]);
  if (key === destination.dataset.key) return;
  const oldReason = destination.dataset.round === round?.round_id ? {
    text: byId("decision-reason")?.value, source: byId("decision-source")?.value,
  } : null;
  destination.dataset.key = key;
  destination.dataset.round = round?.round_id || "";
  destination.replaceChildren();
  if (!round) return;
  if (round.error || round.message) destination.append(element("p", round.error || round.message, "subtle"));
  const cited = round.context?.utterance?.text ?? round.context?.text ?? round.context?.transcript;
  if (typeof cited === "string") destination.append(element("p", "발언 · " + cited, "subtle"));
  const constraints = round.context?.constraints || round.request_constraints || [];
  if (constraints.length) {
    const values = constraints.map(value => {
      const field = snapshot.profile.measured_properties?.find(property => property.property_id === value.property_id);
      return (field?.label || value.property_id) + " " + unitValue(value.value, value.unit);
    });
    destination.append(element("p", "유지 조건 · " + values.join(", "), "subtle"));
  }
  if (round.status === "needs_input" && interactive) {
    if (round.actor_id === actor) destination.append(renderClarification(round, callbacks));
    else destination.append(element("p", typeof round.question === "string" ? round.question : round.question?.text, "question"));
  }
  if (round.candidates?.length) destination.append(renderCandidates(round, snapshot, candidateId, callbacks));
  if (!interactive) return;
  if (round.status === "reviewable") {
    if (snapshot.assembly) {
      if (round.candidates.filter(candidate => candidate.status === "valid").length > 1) {
        const interpretation = element("details");
        interpretation.append(element("summary", "원래 뜻에 가까운 후보를 기록할 수 있습니다."));
        interpretation.append(renderResponse(round, "interpretation", actor, callbacks));
        destination.append(interpretation);
      }
      if (presenter) destination.append(renderDecision(round, snapshot, candidateId, oldReason, callbacks));
      return;
    }
    if (round.candidates.some(candidate => candidate.status === "valid")) {
      destination.append(renderResponse(round, "interpretation", actor, callbacks),
        renderResponse(round, "preference", actor, callbacks));
      const interpretations = new Map((round.responses || [])
        .filter(item => ["interpretation", "matches_my_interpretation"].includes(item.kind))
        .map(item => [item.participant_id, item]));
      const distinct = new Set(Array.from(interpretations.values(), item =>
        JSON.stringify([item.choice || item.response, [...(item.candidate_ids || [])].sort()])));
      if (distinct.size > 1) destination.append(element("p", "참가자가 선택한 해석이 다릅니다.", "subtle"));
    }
    const active = round.candidates.find(candidate => candidate.candidate_id === candidateId &&
      candidate.status === "valid");
    if (active) {
      const acknowledged = round.acks?.[actor] === active.candidate_id;
      destination.append(makeButton(acknowledged ? "검토 확인함" : "이 모델 검토 확인", "",
        () => callbacks.command("ack", { round_id: round.round_id, candidate_id: active.candidate_id }), acknowledged));
    }
    if (presenter) destination.append(renderDecision(round, snapshot, candidateId, oldReason, callbacks));
  }
  if (snapshot.assembly && round.status === "failed") {
    destination.append(makeButton("새 수정 요청", "", () => callbacks.refine(false)));
  }
  if (presenter && ["generating", "planning", "building", "needs_input"].includes(round.status)) {
    destination.append(makeButton("검토 취소", "", () => callbacks.command("cancel", { round_id: round.round_id })));
  }
}

function transcriptText(value) {
  return value?.text || value?.transcript || (typeof value === "string" ? value : "");
}

export function renderTranscripts(snapshot, partial, listen) {
  const utterances = snapshot.utterances || [];
  const root = byId("transcripts");
  const key = JSON.stringify([utterances, partial]);
  if (key === root.dataset.key) return;
  root.dataset.key = key;
  root.replaceChildren();
  const name = id => snapshot.participants.find(person => person.participant_id === id)?.display_name || "화자 미확인";
  const items = utterances.slice(-35);
  if (partial && transcriptText(partial)) items.push({ ...partial, status: "partial" });
  if (!items.length) root.append(element("li", "회의 중 나온 발언이나 직접 입력한 문장이 여기에 표시됩니다.", "subtle"));
  for (const item of items) {
    const li = element("li", null, "transcript" + (item.status === "partial" ? " partial" : ""));
    const source = ({
      replay: "예시 발화",
      manual: "직접 입력",
      live_capture: "녹음 전사",
    })[item.source_mode] || "발화";
    const context = snapshot.rounds.findLast(round => round.context?.utterance.utterance_id === item.utterance_id &&
      round.context.utterance.revision === item.revision)?.context;
    const part = snapshot.profile.semantic_parts.find(part => part.part_id === context?.part_ids?.[0]);
    const meta = (item.status === "partial" ? "전사 중" : source) + " · " + name(
      item.source_mode === "live_capture" ? item.speaker_id : item.participant_id) +
      (part ? " · " + part.label : "") + (item.revision > 1 ? " · 정정 " + (item.revision - 1) : "");
    li.append(element("p", meta, "subtle"), element("p", transcriptText(item)));
    if (item.processing_notice) li.append(element("p", item.processing_notice, "subtle"));
    if (item.audio_url && listen) li.append(makeButton("원음 듣기", "", () => listen(item, li)));
    root.append(li);
  }
  root.scrollTop = root.scrollHeight;
}

const decisionLabels = { commit: "채택", adopt: "채택", adopt_for_test: "출력 검토용 채택", defer: "보류", reject: "기각" };
const consensusLabels = { confirmed: "전원 확인", partial: "일부 참가자 확인", not_checked: "참가자 확인 미기록", disputed: "의견 차이 기록" };

export function renderMinutes(snapshot, callbacks) {
  const root = byId("minutes-list");
  const decisions = snapshot.decisions || [];
  const key = JSON.stringify([decisions, (snapshot.utterances || []).filter(item => item.revision > 1), Boolean(callbacks.readopt)]);
  if (root.dataset.key === key) return;
  root.dataset.key = key;
  root.replaceChildren();
  if (!decisions.length) root.append(element("p", "채택하거나 보류한 후보와 결정 이유가 여기에 남습니다.", "subtle"));
  for (const decision of [...decisions].reverse()) {
    const card = element("article", null, "minute-card");
    const version = snapshot.versions.find(item =>
      item.version_id === (decision.selected_model_version_id ?? decision.version_id));
    const outcome = decisionLabels[decision.outcome ?? decision.type] || "결정";
    const versionName = version ? " · " + versionLabel(version.version_id, snapshot.versions) : "";
    const heading = element("h3", outcome + versionName +
      (snapshot.assembly && decision.candidate_title ? " · " + decision.candidate_title : ""));
    card.append(heading, element("p", decision.reason || decision.rationale_text || "이유 미기록"));
    if (decision.consensus_status && !snapshot.assembly) {
      const confirmed = (decision.confirmed_by || []).map(id =>
        snapshot.participants.find(participant => participant.participant_id === id)?.display_name || id);
      const consensus = consensusLabels[decision.consensus_status] || "";
      card.append(element("p", consensus + (confirmed.length ? " · " + confirmed.join(", ") : ""), "subtle"));
    }
    const round = snapshot.rounds.find(item => item.round_id === decision.round_id);
    const candidate = round?.candidates.find(item => item.version_id === version?.version_id);
    if (candidate) {
      const changes = operationChanges(candidate, round, snapshot);
      if (changes.length) card.append(element("p", changes.join(" · "), "subtle"));
    }
    const evidence = decision.evidence_refs || round?.context?.evidence_refs || [];
    const source = round?.context?.utterance || snapshot.utterances.find(utterance =>
      evidence.some(item => (item.evidence_id ?? item) === (utterance.evidence_id ?? utterance.utterance_id)));
    const quote = decision.source_text || transcriptText(source);
    if (quote) {
      const disclosure = element("details");
      disclosure.append(element("summary", "근거 발언"), element("p", quote, "source-quote"));
      if (source?.audio_url && callbacks.audio) disclosure.append(makeButton("원음 듣기", "",
        () => callbacks.audio(source, disclosure)));
      const correction = snapshot.utterances.findLast(item =>
        item.utterance_id === source?.utterance_id && item.revision > source?.revision);
      if (correction) {
        disclosure.append(element("p", "결정 뒤 추가된 정정 발언", "subtle"),
          element("p", transcriptText(correction), "source-quote"));
      }
      card.append(disclosure);
    }
    const preserved = decision.assembly_record?.preserved_conditions || [];
    if (preserved.length) card.append(element("p", "유지 조건 · " + preserved.map(condition => typeof condition === "string"
      ? condition : condition.label).filter(Boolean).join(" · "), "subtle"));
    const votes = decision.responses || [];
    if (votes.length && round) {
      const replies = element("details");
      replies.append(element("summary", "개인 해석과 선호"));
      const latestVotes = new Map(votes.map(vote => [vote.participant_id + ":" + vote.kind, vote]));
      for (const vote of latestVotes.values()) {
        const participant = snapshot.participants.find(person => person.participant_id === vote.participant_id);
        const names = (vote.candidate_ids || []).map(id => {
          const index = round.candidates.findIndex(candidate => candidate.candidate_id === id);
          return index < 0 ? id : letter(index);
        });
        const reply = names.length ? names.join(" + ") : vote.choice === "none" ? "해당 없음" : "잘 모르겠음";
        replies.append(element("p", [participant?.display_name, responseKinds[vote.kind], reply].filter(Boolean).join(" · "), "subtle"));
      }
      card.append(replies);
    }
    if (version && version.preview_url) {
      const actions = element("div", null, "file-actions");
      actions.append(makeButton("결정 모델 보기", "", () => callbacks.view(version)));
      if (version.export_url) actions.append(makeButton("내보내기", "", () => callbacks.export(version)));
      if (callbacks.readopt && version.version_id !== snapshot.adopted_version_id) {
        actions.append(makeButton("이 버전 다시 채택", "", () => callbacks.readopt(version)));
      }
      card.append(actions);
    }
    root.append(card);
  }
}
