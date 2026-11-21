"""Wire commands and the errors returned by the EDGE."""
import hashlib
import json
import math
import time
import uuid
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field


class MeetingError(ValueError):
    def __init__(self, message, status=400, **details):
        super().__init__(message)
        self.status = status
        self.details = details


class Identity(BaseModel):
    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)
    display_name: str = Field(min_length=1, max_length=48)
    device_id: str = Field(min_length=1, max_length=80)
    capabilities: dict = Field(default_factory=dict)


class CreateSession(Identity):
    profile_id: str | None = None


class JoinSession(Identity):
    join_code: str = Field(min_length=6, max_length=6)


class Command(BaseModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)
    command_id: str = Field(min_length=1, max_length=100)
    type: Literal[
        "view", "edit", "text", "clarify", "response", "decide", "cancel",
        "replay", "audio_host", "constraint", "ack", "printed",
        "restore", "end", "transcript_correction", "renew_code", "capabilities",
        "meeting_start", "meeting_pause", "meeting_end", "meeting_retry", "meeting_readopt",
    ]
    payload: dict = Field(default_factory=dict)


def uid(prefix):
    return f"{prefix}_{uuid.uuid4().hex}"


def now_ms():
    return time.time_ns() // 1_000_000


def canonical(value):
    return json.dumps(value, sort_keys=True, ensure_ascii=False, allow_nan=False, separators=(",", ":"))


def digest(value):
    return hashlib.sha256(canonical(value).encode()).hexdigest()


def clock_stamp(payload, received):
    source = payload.get("source_monotonic_ms")
    offset = payload.get("clock_offset_ms")
    uncertainty = payload.get("clock_uncertainty_ms")
    numbers = (source, offset, uncertainty)
    synced = all(type(item) in (int, float) and math.isfinite(item) for item in numbers)
    if synced and 0 <= uncertainty <= 500 and abs(source + offset - received) <= 5000:
        captured = round(source + offset)
    else:
        source = offset = uncertainty = None
        captured = received
    return {"source_monotonic_ms": source, "clock_offset_ms": offset, "clock_uncertainty_ms": uncertainty,
            "captured_session_time_ms": captured, "received_at": received}


def text_field(payload, key, max_length=1200, required=True):
    value = payload.get(key)
    if value is None and not required:
        return None
    if not isinstance(value, str) or len(value) > max_length or (required and not value.strip()):
        raise MeetingError(f"{key} 값을 확인해 주세요.")
    return value.strip() or None
