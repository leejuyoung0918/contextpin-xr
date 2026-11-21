import { byId, element } from "./panels.js";

function appendSummary(area, text) {
  let list;
  area.replaceChildren();
  for (const line of text.split("\n")) {
    const value = line.trim();
    if (/^[-*] /.test(value)) {
      if (!list) {
        list = element("ul");
        area.append(list);
      }
      list.append(element("li", value.slice(2)));
    } else {
      list = null;
      if (value.startsWith("#")) area.append(element("h4", value.replace(/^#+\s*/, "")));
      else if (value) area.append(element("p", value));
    }
  }
}

const missingLabels = {
  audio_end_not_confirmed: "마지막 오디오 수신 범위 미확인",
  audio_gap: "수신하지 못한 오디오 구간",
  transcription_pending: "마감 시각까지 미전사된 음성 구간",
  transcription_failed: "전사 오류가 발생한 음성 구간",
  reviewed_candidate_assets: "일부 검토 후보 파일",
  result_files: "결과 파일",
};

function arrayText(values) {
  return values.map(item => typeof item === "string" ? missingLabels[item] || item :
    item?.message || missingLabels[item?.kind] || item?.path || item?.kind).filter(Boolean).join(" · ");
}

export function markResultLoading() {
  byId("result-meta").textContent = "저장된 결과를 불러오는 중입니다.";
  byId("result-missing").hidden = true;
  byId("result-download").disabled = true;
  byId("result-summary").replaceChildren();
  byId("result-decisions").replaceChildren();
  byId("result-transcript").textContent = "";
  byId("result-scad").textContent = "";
}

export function paintMeetingResult(data) {
  const manifest = data.manifest || {};
  const date = Number.isFinite(manifest.ended_at) ? new Date(manifest.ended_at).toLocaleString("ko-KR") : "";
  const meta = [date, manifest.adopted_version_id || manifest.final_version_id, manifest.revision &&
    "저장본 " + manifest.revision].filter(Boolean);
  byId("result-meta").textContent = meta.join(" · ");
  const missing = manifest.missing || data.missing || [];
  const missingLabel = arrayText(missing);
  const alert = byId("result-missing");
  alert.hidden = !missing.length;
  alert.textContent = "이 저장본의 누락 항목" + (missingLabel ? " · " + missingLabel : "");
  appendSummary(byId("result-summary"), data.summary_markdown || "상세 요약을 불러올 수 없습니다.");
  byId("result-transcript").textContent = data.transcript_text || "저장된 확정 전사가 없습니다.";
  const decisions = byId("result-decisions");
  decisions.replaceChildren();
  const adopted = (data.decisions || []).filter(item => item.selected_model_version_id);
  if (!adopted.length) decisions.append(element("li", "변경 채택 없음"));
  for (const item of adopted) {
    const row = element("li");
    const label = item.candidate_label || item.candidate_title || "이전 버전";
    row.append(element("strong", label + " · " + item.selected_model_version_id),
      element("p", item.rationale_text || "선택 이유 미기록"));
    decisions.append(row);
  }
  byId("result-scad").textContent = data.final_scad || "이 저장본에서 최종 SCAD를 찾을 수 없습니다.";
  byId("result-download").disabled = !data.zip_url;
}
