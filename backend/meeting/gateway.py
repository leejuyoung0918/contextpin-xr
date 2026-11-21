"""Stateless local or separately deployed OpenAI-compatible AI Gateway."""
import base64
import binascii
import json
import os
import secrets
import urllib.error

from fastapi import FastAPI, Header
from fastapi.responses import JSONResponse

from backend.server import InputError, transcribe, upstream
from .contracts import MeetingError
from .planner import Plan, SYSTEM


def provider_ready(kind):
    setting = "AI_TRANSCRIPTION_MODEL" if kind == "speech" else "AI_PLANNER_MODEL"
    base = os.environ.get("AI_BASE_URL", "")
    local = base.startswith(("http://localhost:", "http://127.0.0.1:"))
    return bool(os.environ.get(setting) and (os.environ.get("AI_API_KEY") or local))


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
                   "A request to go a little further in this candidate must refine its parameters.")
        # Only information needed for the suggested edits goes to the provider.
        utterance = context["utterance"]
        related = context["related_utterances"][-5:]
        changed_base = context.get("changed_base_parameters", [])
        context = {k: context[k] for k in ("request_id", "context_hash", "edit_base_version_id",
                   "expected_committed_version_id", "generation_epoch", "object_id", "parameters",
                   "constraints", "part_ids", "clarification_of", "evidence_ids")}
        context.update({"utterance": {"text": utterance["text"], "evidence_id": utterance["evidence_id"]},
                        "related_utterances": [{"text": item["text"], "evidence_id": item["evidence_id"]} for item in related],
                        "changed_base_parameters": changed_base,
                        "preserved_conditions": assembly["preserved_conditions"],
                        "profile": {k: profile[k] for k in ("semantic_parts", "parameters", "edit_capabilities")}})
    prompt += "\nResponse JSON Schema:\n" + json.dumps(Plan.model_json_schema(), ensure_ascii=False)
    messages = [{"role": "system", "content": prompt},
                {"role": "user", "content": json.dumps(context, ensure_ascii=False)}]
    for attempt in range(2):
        response = upstream("/chat/completions", json.dumps({
            "model": os.environ["AI_PLANNER_MODEL"], "response_format": {"type": "json_object"},
            "messages": messages,
        }, ensure_ascii=False).encode())
        try:
            return Plan.model_validate_json(response["choices"][0]["message"]["content"]).model_dump()
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
    return transcribe(wave, allow_silence=True, include_metadata=True)


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
