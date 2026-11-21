"""OpenAI-compatible HTTP transport used by the meeting server."""
import json
import os
import urllib.request
import uuid
from urllib.parse import urlsplit


class InputError(ValueError):
    pass


class LocalModelRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise InputError("로컬 모델 주소가 다른 URL로 리디렉션되었습니다.")


def upstream(path, payload, content_type="application/json", *, base_url=None, api_key=None, timeout=45):
    base = (base_url or os.environ.get("AI_BASE_URL", "https://api.openai.com/v1")).rstrip("/")
    key = os.environ.get("AI_API_KEY", "") if api_key is None else api_key
    if os.environ.get("MEETING_CANDIDATE_AI"):
        from .bundle_provider import settings
        _, allowed_url, _, allowed_key = settings()
        if base_url is None or base != allowed_url or api_key != allowed_key or path != "/chat/completions":
            raise InputError("현장 회의 서버는 선택한 설계 모델만 호출할 수 있습니다.")
    # A local compatible model is allowed, but remote credentials never go over HTTP.
    if not base.startswith("https://") and not base.startswith(("http://127.0.0.1:", "http://localhost:")):
        raise InputError("AI_BASE_URL must use HTTPS, except for a local model")
    request = urllib.request.Request(base + path, data=payload, headers={"Content-Type": content_type}, method="POST")
    if key:
        request.add_unredirected_header("Authorization", "Bearer " + key)
    open_request = urllib.request.urlopen
    if urlsplit(base).hostname in ("127.0.0.1", "localhost"):
        # A machine-wide proxy or redirect must never send the local model's images off the PC.
        open_request = urllib.request.build_opener(urllib.request.ProxyHandler({}), LocalModelRedirect()).open
    with open_request(request, timeout=timeout) as response:
        return json.loads(response.read(4 * 1024 * 1024))


def transcribe(wave, allow_silence=False, include_metadata=False):
    model = os.environ.get("AI_TRANSCRIPTION_MODEL", "")
    if not model:
        raise InputError("Set AI_TRANSCRIPTION_MODEL for microphone input")
    boundary = "relay-" + uuid.uuid4().hex
    body = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"model\"\r\n\r\n{model}\r\n"
            f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"event.wav\"\r\n"
            "Content-Type: audio/wav\r\n\r\n").encode() + wave + f"\r\n--{boundary}--\r\n".encode()
    result = upstream("/audio/transcriptions", body, "multipart/form-data; boundary=" + boundary)
    transcript = result.get("text", "")
    if not isinstance(transcript, str) or "text" not in result or (not allow_silence and not transcript.strip()):
        raise InputError("No speech was transcribed")
    if include_metadata:
        item = result.get("id")
        return {"transcript": transcript.strip(), "provider": "openai_compatible", "model": model,
                "providerItemId": item if isinstance(item, str) and len(item) <= 160 else "",
                "timingQuality": "segment_bounds", "speaker": "unknown", "status": "final"}
    return transcript
