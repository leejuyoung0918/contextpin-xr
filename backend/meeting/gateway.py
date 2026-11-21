"""Stateless local or separately deployed OpenAI-compatible AI Gateway."""
import base64
import binascii
import json
import io
import importlib.util
from functools import lru_cache
from pathlib import Path
import os
import secrets
import time
import urllib.error

from fastapi import FastAPI, Header
from fastapi.responses import JSONResponse

from .provider_http import InputError, transcribe, upstream
from .contracts import MeetingError
from .planner import Plan, SYSTEM


def configure_provider():
    """Reuse an existing OpenAI credential only for its own provider endpoint."""
    base = os.environ.get("AI_BASE_URL", "https://api.openai.com/v1").rstrip("/")
    if base == "https://api.openai.com/v1" and os.environ.get("OPENAI_API_KEY"):
        os.environ.setdefault("AI_API_KEY", os.environ["OPENAI_API_KEY"])
    if base == "https://api.openai.com/v1" and os.environ.get("AI_API_KEY"):
        os.environ.setdefault("AI_PLANNER_MODEL", "gpt-4.1-mini")
        os.environ.setdefault("AI_TRANSCRIPTION_MODEL", "gpt-4o-mini-transcribe")


def local_model_dir():
    default = Path(__file__).resolve().parents[2] / "artifacts/tools/faster-whisper-base"
    return Path(os.environ.get("MEETING_WHISPER_DIR", default)).expanduser()


@lru_cache(maxsize=1)
def load_whisper(path):
    from faster_whisper import WhisperModel
    return WhisperModel(path, device="cpu", compute_type="int8", local_files_only=True, cpu_threads=4)


def provider_ready(kind):
    if kind == "planner" and os.environ.get("MEETING_PLAN_BACKEND") == "registered":
        return True
    if kind == "speech" and os.environ.get("MEETING_STT_BACKEND") == "local":
        return importlib.util.find_spec("faster_whisper") is not None and (local_model_dir() / "model.bin").is_file()
    setting = "AI_TRANSCRIPTION_MODEL" if kind == "speech" else "AI_PLANNER_MODEL"
    base = os.environ.get("AI_BASE_URL", "")
    local = base.startswith(("http://localhost:", "http://127.0.0.1:"))
    return bool(os.environ.get(setting) and (os.environ.get("AI_API_KEY") or local))


def provider_failure(error):
    try:
        code = json.loads(error.read(8192)).get("error", {}).get("code")
    except (ValueError, AttributeError):
        code = None
    if code in {"insufficient_quota", "billing_hard_limit_reached", "credit_balance_exhausted"}:
        return MeetingError("AI 제공자 사용 한도 또는 결제 설정을 확인해 주세요. 수동 입력과 치수 조정은 계속할 수 있습니다.", 503)
    if error.code == 429:
        return MeetingError("AI 요청 한도에 도달했습니다. 잠시 후 다시 요청해 주세요.", 429)
    if error.code in {401, 403}:
        return MeetingError("AI 제공자 인증 또는 모델 접근 권한을 확인해 주세요.", 503)
    return MeetingError("AI 제공자 요청이 실패했습니다. 다시 요청하거나 수동 입력을 사용해 주세요.", 502)


def plan_live(context):
    if not provider_ready("planner"):
        raise MeetingError("AI_PLANNER_MODEL과 AI_API_KEY를 설정해 주세요.", 503)
    prompt = SYSTEM
    profile = context.get("profile", {})
    assembly = profile.get("assembly")
    if assembly:
        prompt += ("\nThis is a fixed two-object assembly. Edit only the editable exterior of object_2. "
                   "The reference object, coupling region, clearances and mounting datum are fixed. "
                   "Do not propose changes to them. Do not invent interference or fit results. "
                   "A request to go a little further in this candidate must refine its parameters. "
                   "Conversation, preferences, negative requests and recalls are not adoption or edits. "
                   "Return context_version and a brief Korean discussion label in summary_patch for a proposed change only.")
        # Only information needed for the suggested edits goes to the provider.
        utterance = context["utterance"]
        related = context["related_utterances"][-5:]
        changed_base = context.get("changed_base_parameters", [])
        screenshot = context.get("screenshot_data_url")
        keys = ("request_id", "context_hash", "edit_base_version_id", "expected_committed_version_id",
                "generation_epoch", "object_id", "parameters", "constraints", "part_ids", "clarification_of",
                "evidence_ids", "session_id", "context_version", "adopted_version_id", "adopted_parameters",
                "source_version", "preview_candidate_id", "candidate_set_id", "active_candidates", "unresolved_question",
                "request_actor_id", "scad_code", "effective_parameters", "fixed_parameters", "parameter_relations",
                "selection_snapshot", "selected_position_model_m", "screenshot", "screenshot_status", "edit_scope",
                "relevant_decisions", "selected_utterances", "source_utterance_ids", "request_user_id",
                "reference_selection", "reference_mapping", "request_transcript", "source_utterance_refs")
        context = {k: context[k] for k in keys if k in context}
        context.update({"utterance": {"text": utterance["text"], "evidence_id": utterance["evidence_id"]},
                        "related_utterances": [{"text": item["text"], "evidence_id": item["evidence_id"]} for item in related],
                        "changed_base_parameters": changed_base,
                        "preserved_conditions": assembly["preserved_conditions"],
                        "profile": {k: profile[k] for k in ("semantic_parts", "parameters", "edit_capabilities",
                                                           "derived_parameters", "parameter_constraints") if k in profile}})
        prompt += ("\nThe current SCAD source and all effective parameters are supplied. "
                   "You may use a declared derived parameter such as body_length as an operation. "
                   "Only apply edits to edit_base_version_id. reference_selection identifies the photographed mesh. "
                   "semantic_part_only maps only the registered semantic part: never transfer a coordinate to the edit base. "
                   "Use only parameters for the selected part; ask if a local edit is unsupported. "
                   "The server converts its array_span to pitch using the fixed slot count and margins. "
                   "For an exact total length, return one candidate with body_length at that exact value. "
                   "All five slots use the same shared parameters when edit_scope says so. "
                   "Do not claim one individual hole changed, or cable holding force was tested. "
                   "Suggest values within the declared limits. Keep every unrequested parameter unchanged. "
                   "For ambiguous loose-fit methods, compare entrance-only, inside-only, and both when feasible. "
                   "Distinguish tentative comparison values from explicit requested values.")
    else:
        screenshot = context.get("screenshot_data_url")
    prompt += "\nResponse JSON Schema:\n" + json.dumps(Plan.model_json_schema(), ensure_ascii=False)
    text_context = {key: value for key, value in context.items() if key != "screenshot_data_url"}
    content = [{"type": "text", "text": json.dumps(text_context, ensure_ascii=False)}]
    if screenshot and screenshot.startswith(("data:image/png;base64,", "data:image/jpeg;base64,")):
        content.append({"type": "image_url", "image_url": {"url": screenshot, "detail": "low"}})
    messages = [{"role": "system", "content": prompt},
                {"role": "user", "content": content if len(content) > 1 else content[0]["text"]}]
    started = time.monotonic()
    for attempt in range(2):
        try:
            response = upstream("/chat/completions", json.dumps({
                "model": os.environ["AI_PLANNER_MODEL"], "response_format": {"type": "json_object"},
                "messages": messages,
            }, ensure_ascii=False).encode())
        except urllib.error.HTTPError as error:
            raise provider_failure(error) from error
        try:
            plan = Plan.model_validate_json(response["choices"][0]["message"]["content"]).model_dump()
            plan["proposal_provenance"] = {"source": "provider", "provider": "openai_compatible",
                                           "model": os.environ["AI_PLANNER_MODEL"],
                                           "provider_request_id": response.get("id"),
                                           "elapsed_ms": round((time.monotonic() - started) * 1000),
                                           "images_sent": len(content) - 1}
            return plan
        except (ValueError, KeyError, IndexError, TypeError):
            if attempt == 0:
                messages.append({"role": "user", "content": "Return a corrected JSON object matching the supplied schema."})
    raise MeetingError("공급자가 올바른 후보 응답을 반환하지 않았습니다.", 502)


def transcribe_live(payload):
    if not provider_ready("speech"):
        raise MeetingError("AI_TRANSCRIPTION_MODEL과 AI_API_KEY를 설정해 주세요.", 503)
    try:
        wave = base64.b64decode(payload["wav_base64"], validate=True)
    except (KeyError, binascii.Error, TypeError) as error:
        raise MeetingError("WAV 오디오를 확인해 주세요.") from error
    if not 44 < len(wave) <= 3 * 1024 * 1024:
        raise MeetingError("오디오 크기 제한을 초과했습니다.", 413)
    if os.environ.get("MEETING_STT_BACKEND") == "local":
        from .audio import inspect_wav
        inspect_wav(wave)
        try:
            model = load_whisper(str(local_model_dir()))
            import wave as pcm_wave
            import numpy as np
            with pcm_wave.open(io.BytesIO(wave), "rb") as pcm:
                rate = pcm.getframerate()
                samples = np.frombuffer(pcm.readframes(pcm.getnframes()), dtype="<i2").astype(np.float32) / 32768
            if rate != 16000:
                # Inputs are validated mono PCM16. Resample before handing raw samples to Whisper.
                import av
                frame = av.AudioFrame.from_ndarray(samples[None, :], format="flt", layout="mono")
                frame.sample_rate = rate
                resampler = av.AudioResampler(format="flt", layout="mono", rate=16000)
                frames = [*resampler.resample(frame), *resampler.resample(None)]
                samples = np.concatenate([f.to_ndarray().reshape(-1) for f in frames])
            hints = payload.get("hotwords")
            segments, _ = model.transcribe(samples, language="ko", beam_size=3,
                                            hotwords=hints[:240] if isinstance(hints, str) else None,
                                            condition_on_previous_text=False, vad_filter=True)
            line = " ".join(s.text.strip() for s in segments if s.text.strip())
        except (OSError, RuntimeError, ValueError, ImportError) as error:
            raise MeetingError("로컬 음성 모델을 읽지 못했습니다. 직접 입력하거나 PC 모델 설정을 확인하세요.", 503) from error
        return {"transcript": line, "status": "final" if line else "no_speech", "provider": "local_whisper"}
    try:
        return transcribe(wave, allow_silence=True, include_metadata=True)
    except urllib.error.HTTPError as error:
        raise provider_failure(error) from error


app = FastAPI(title="ContextPin AI Gateway", docs_url=None, redoc_url=None, openapi_url=None)


def authorize(authorization):
    token = os.environ.get("AI_GATEWAY_TOKEN", "")
    if not token or not secrets.compare_digest(authorization.encode(), ("Bearer " + token).encode()):
        raise MeetingError("Gateway 인증이 필요합니다.", 401)


@app.middleware("http")
async def request_limits(request, call_next):
    if request.method == "POST":
        try:
            cap = 4_250_000 if request.url.path == "/v1/transcriptions" else 1_000_000
            if not 0 < int(request.headers.get("content-length", "0")) <= cap:
                raise ValueError()
        except ValueError:
            return JSONResponse({"error": "Gateway 요청 크기를 확인해 주세요."}, status_code=413)
    return await call_next(request)


@app.exception_handler(MeetingError)
async def meeting_error(_, exc):
    return JSONResponse({"error": str(exc)}, status_code=exc.status)


@app.get("/healthz")
def health():
    return {"service": "contextpin-ai-gateway", "status": "ready"}


@app.get("/v1/capabilities")
def capabilities(authorization: str = Header(default="")):
    authorize(authorization)
    return {kind: provider_ready(kind) for kind in ("planner", "speech")}


@app.post("/v1/plans")
def plans(payload: dict, authorization: str = Header(default="")):
    authorize(authorization)
    try:
        return plan_live(payload)
    except (urllib.error.URLError, TimeoutError, InputError) as error:
        raise MeetingError("설계 AI에 연결하지 못했습니다.", 502) from error


@app.post("/v1/transcriptions")
def transcriptions(payload: dict, authorization: str = Header(default="")):
    authorize(authorization)
    try:
        return transcribe_live(payload)
    except (urllib.error.URLError, TimeoutError, InputError) as error:
        raise MeetingError("음성 전사에 연결하지 못했습니다.", 502) from error
