"""Zero external inference fees for the meeting PC and its CAD edit bundles."""
import os
from urllib.parse import urlsplit

LOCAL_URL = "http://127.0.0.1:18905/v1"
TEAM_URL = "https://ai.uvrlab.org/litellm/v1"
LOCAL_ONLY = "이 회의 앱은 외부 API 사용료 0원 모드입니다. 로컬 모델을 사용하세요."


def configure_pc(env, mode="local"):
    """Keep both real and replay meetings on local inference even with old cloud settings."""
    if mode != "local":
        raise ValueError(LOCAL_ONLY)
    env.update(MEETING_CANDIDATE_AI=mode, MEETING_STT_BACKEND="local", MEETING_PLAN_BACKEND="registered")
    for key in ("OPENAI_API_KEY", "AI_API_KEY", "AI_GATEWAY_URL", "AI_GATEWAY_TOKEN", "AI_BASE_URL",
                "AI_TRANSCRIPTION_MODEL", "AI_PLANNER_MODEL"):
        env.pop(key, None)
    if env.get("MEETING_BUNDLE_BASE_URL", "").rstrip("/") == TEAM_URL:
        env.pop("MEETING_BUNDLE_BASE_URL", None)
        env.pop("MEETING_BUNDLE_MODEL", None)
    env.pop("MEETING_BUNDLE_API_KEY", None)
    env.pop("MEETING_TEAM_API_KEY", None)
    env.pop("MEETING_TEAM_MODEL", None)


def settings():
    from .contracts import MeetingError
    mode = os.getenv("MEETING_CANDIDATE_AI", "local")
    if mode != "local":
        raise MeetingError(LOCAL_ONLY, 503)
    base = os.getenv("MEETING_BUNDLE_BASE_URL", LOCAL_URL).strip().rstrip("/")
    model = os.getenv("MEETING_BUNDLE_MODEL", "qwen3-vl-2b").strip()
    try:
        url = urlsplit(base)
        valid = (url.scheme == "http" and url.hostname in ("127.0.0.1", "localhost") and url.port
                 and not (url.username or url.password or url.query or url.fragment) and "@" not in url.netloc)
    except ValueError:
        valid = False
    if not valid or not model:
        raise MeetingError("로컬 비전 모델명과 현장 PC의 루프백 HTTP 주소·포트를 확인하세요.", 503)
    return mode, base, model, ""
