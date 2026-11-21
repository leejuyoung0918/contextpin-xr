import { byId, element, unitValue } from "./panels.js";

export class DraftEditor {
  constructor({ state, command, showVersion, clearView }) {
    Object.assign(this, { state, command, showVersion, clearView });
    this.values = {};
    this.sequence = 0;
    byId("draft-begin").addEventListener("click", () => this.begin());
    byId("draft-cancel").addEventListener("click", () => this.cancel());
    byId("draft-commit").addEventListener("click", () => this.commit());
    this.heartbeat = setInterval(() => {
      const draft = this.state().snapshot?.edit_draft;
      if (this.owns(draft)) this.command("edit_renew", { lease_id: draft.lease_id });
    }, 30000);
  }

  owns(draft) {
    return Boolean(draft?.lease_id && draft.owner_id === this.state().identity?.participant_id);
  }

  async begin() {
    const { snapshot, loadedVersion, displayedView } = this.state();
    const round = snapshot.rounds.find(item => item.round_id === displayedView?.roundId);
    const candidate = round?.candidates.find(item => item.candidate_id === displayedView?.candidateId);
    if (!candidate || candidate.version_id !== loadedVersion?.version_id) return;
    const result = await this.command("edit_begin", {
      base_version_id: candidate.version_id,
      expected_adopted_revision: snapshot.adopted_revision,
      round_id: round.round_id, candidate_id: candidate.candidate_id,
    });
    if (result) byId("draft-reason").value = "";
  }

  paint() {
    const { snapshot, identity, displayedView, loadedVersion, switching } = this.state();
    const draft = snapshot?.edit_draft;
    const round = snapshot?.rounds.find(item => item.round_id === (draft?.round_id || displayedView?.roundId));
    const candidate = round?.candidates.find(item => item.candidate_id === (draft?.candidate_id || displayedView?.candidateId));
    const active = snapshot?.status === "active" && !["finalizing", "completed", "save_error"].includes(snapshot?.session_status);
    const visible = active && Boolean(draft?.lease_id || (round?.status === "reviewable" && candidate?.version_id));
    byId("draft-editor").hidden = !visible;
    if (!visible) {
      clearTimeout(this.timer);
      this.lease = null;
      this.values = {};
      return;
    }
    const owned = this.owns(draft);
    if (this.lease !== draft?.lease_id) {
      clearTimeout(this.timer);
      this.lease = draft?.lease_id;
      this.sequence = draft?.preview_revision || 0;
      this.values = Object.fromEntries((draft?.fields || []).map(field => [field.parameter_id, field.current_value]));
      this.dirty = false;
      this.key = null;
    }
    this.sequence = Math.max(this.sequence, draft?.preview_revision || 0);
    const ready = draft?.status === "ready" && draft.version_id &&
      draft.preview_revision >= this.sequence && !this.dirty && !switching;
    if (ready && displayedView?.versionId !== draft.version_id) this.showVersion(draft.version_id);
    const owner = snapshot.participants.find(person => person.participant_id === draft?.owner_id)?.display_name;
    const status = !draft?.lease_id ? "표시된 후보에서 필요한 치수를 조정합니다." :
      (owner || "참가자") + " 조정 중 · " + ({ ready: "정확한 형상 준비됨", building: "수정안 생성 중",
        failed: "생성 실패", pending: "수정안 생성 대기" }[draft.status] || draft.status) +
      (draft.error ? " · " + draft.error : "");
    byId("draft-status").textContent = this.dirty ? "값 변경됨 · 수정안 생성 대기" : status;
    byId("draft-begin").hidden = Boolean(draft?.lease_id);
    byId("draft-begin").disabled = switching || !candidate;
    byId("draft-commit").hidden = !owned;
    byId("draft-cancel").hidden = !owned;
    byId("draft-reason").disabled = !owned;
    byId("draft-commit").disabled = !ready || loadedVersion?.version_id !== draft?.version_id;
    const ids = draft?.parameter_ids || [...new Set((candidate?.operations || []).map(item => item.parameter_id))];
    const definitions = draft?.fields || snapshot.profile.parameters.filter(item => ids.includes(item.parameter_id) && item.type !== "enum");
    const key = JSON.stringify([draft?.lease_id, draft?.lease_id ? null : candidate?.candidate_id, definitions.map(({ current_value, ...field }) => field), owned]);
    const root = byId("draft-sliders");
    if (key !== this.key) {
      this.key = key;
      root.replaceChildren();
      for (const definition of definitions) {
        const row = element("div");
        const id = definition.parameter_id;
        const input = element("input");
        const output = element("output");
        const label = element("label", definition.label);
        input.id = "draft-" + id;
        input.type = "range";
        input.min = definition.min_value;
        input.max = definition.max_value;
        input.step = definition.step || (definition.type === "integer" ? 1 : .001);
        input.disabled = !owned;
        input.value = this.values[id] ?? definition.current_value ?? snapshot.versions.find(item => item.version_id === candidate?.version_id)?.parameters[id];
        label.htmlFor = input.id;
        output.htmlFor = input.id;
        output.textContent = unitValue(Number(input.value), definition.unit);
        label.append(output);
        row.append(label, input, element("p", unitValue(Number(input.min), definition.unit) + " ~ " +
          unitValue(Number(input.max), definition.unit) + " · 간격 " + unitValue(Number(input.step), definition.unit), "subtle"));
        input.addEventListener("input", () => {
          this.values[id] = Number(input.value);
          output.textContent = unitValue(this.values[id], definition.unit);
          this.dirty = true;
          byId("draft-commit").disabled = true;
          byId("draft-status").textContent = "값 변경됨 · 수정안 생성 대기";
          clearTimeout(this.timer);
          this.timer = setTimeout(() => this.preview(), 350);
        });
        input.addEventListener("change", () => this.preview());
        root.append(row);
      }
    }
    if (!owned) {
      for (const definition of definitions) {
        const input = byId("draft-" + definition.parameter_id);
        if (!input) continue;
        input.value = definition.current_value ?? draft?.parameters?.[definition.parameter_id] ?? input.value;
        input.previousElementSibling.querySelector("output").textContent = unitValue(Number(input.value), definition.unit);
      }
    }
  }

  async preview() {
    clearTimeout(this.timer);
    const draft = this.state().snapshot?.edit_draft;
    if (!this.owns(draft) || !this.dirty) return;
    this.dirty = false;
    const revision = ++this.sequence;
    const lease = draft.lease_id;
    const result = await this.command("edit_preview", {
      lease_id: lease, preview_revision: revision, parameters: { ...this.values },
    });
    if (!result && this.lease === lease && revision === this.sequence) {
      this.dirty = true;
      byId("draft-status").textContent = "수정안 요청 실패 · 값을 다시 조정해 재시도하세요.";
      byId("draft-commit").disabled = true;
    }
  }

  async cancel() {
    clearTimeout(this.timer);
    const draft = this.state().snapshot?.edit_draft;
    if (!this.owns(draft)) return;
    if (await this.command("edit_cancel", { lease_id: draft.lease_id })) this.clearView();
  }

  async commit() {
    const { snapshot, loadedVersion, switching } = this.state();
    const draft = snapshot?.edit_draft;
    if (!this.owns(draft) || this.dirty || switching || draft.status !== "ready" ||
        draft.preview_revision !== this.sequence || draft.version_id !== loadedVersion?.version_id) return;
    const result = await this.command("edit_commit", { lease_id: draft.lease_id,
      preview_revision: draft.preview_revision, displayed_version_id: loadedVersion.version_id,
      reason: byId("draft-reason").value.trim() || undefined });
    if (result) this.clearView();
  }

  reset() {
    clearTimeout(this.timer);
    this.lease = null;
    this.values = {};
    this.key = null;
    this.dirty = false;
  }
}
