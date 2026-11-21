"""EDGE HTTP commands, artifact downloads, and authenticated event/audio sockets."""
import asyncio
import hashlib
import io
import json
import os
import re
import time
import zipfile
from collections import defaultdict, deque
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI, File, Form, Header, Request, UploadFile, WebSocket, WebSocketDisconnect
from fastapi.exceptions import RequestValidationError
from fastapi.responses import FileResponse, JSONResponse, RedirectResponse, Response
from fastapi.staticfiles import StaticFiles
from starlette.concurrency import run_in_threadpool

from . import geometry
from .audio import AudioCapture, MAX_WAV, inspect_wav
from .contracts import Command, CreateSession, JoinSession, MeetingError, clock_stamp, now_ms, uid
from .coordinator import SessionCoordinator
from .gateway import provider_ready

WEB = Path(__file__).with_name("web")
MAX_MEDIA = min(8 * 1024 * 1024, int(os.environ.get("MAX_MEDIA_BYTES", str(5 * 1024 * 1024))))
STRICT_CSP = ("default-src 'self'; script-src 'self'; style-src 'self'; "
              "img-src 'self' blob:; media-src 'self' blob:; connect-src 'self'; "
              "object-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'")


def create_app(root=None, planner=None):
    @asynccontextmanager
    async def lifecycle(app):
        app.state.coordinator = SessionCoordinator(root, planner)
        app.state.join_attempts = defaultdict(deque)
        try:
            yield
        finally:
            await run_in_threadpool(app.state.coordinator.close)

    app = FastAPI(title="ContextPin Meeting EDGE", lifespan=lifecycle, docs_url=None, redoc_url=None, openapi_url=None)

    @app.exception_handler(MeetingError)
    async def meeting_error(_, exc):
        return JSONResponse({"error": str(exc), **exc.details}, status_code=exc.status)

    @app.exception_handler(RequestValidationError)
    async def invalid_request(_, exc):
        errors = [".".join(map(str, issue["loc"])) for issue in exc.errors()]
        return JSONResponse({"error": "요청 형식을 확인해 주세요.", "fields": errors}, status_code=422)

    @app.middleware("http")
    async def http_headers(request, call_next):
        origin = request.headers.get("origin")
        if (request.method != "GET" and origin and origin != f"{request.url.scheme}://{request.headers.get('host')}"):
            return JSONResponse({"error": "허용되지 않은 요청 출처입니다."}, status_code=403)
        if request.method == "POST":
            limit = MAX_MEDIA + 65_536 if request.url.path.endswith("/media") else (
                MAX_WAV if request.url.path.endswith("/audio") else 65_536)
            try:
                size = int(request.headers.get("content-length", "-1"))
            except ValueError:
                size = -1
            if not 0 <= size <= limit:
                return JSONResponse({"error": "요청 크기를 확인해 주세요."}, status_code=413)
        response = await call_next(request)
        response.headers["Cache-Control"] = "no-store"
        response.headers["Referrer-Policy"] = "no-referrer"
        response.headers["X-Content-Type-Options"] = "nosniff"
        response.headers["Content-Security-Policy"] = STRICT_CSP
        response.headers["X-Frame-Options"] = "DENY"
        return response

    def edge():
        return app.state.coordinator

    @app.get("/")
    @app.get("/meeting")
    def landing():
        return RedirectResponse("/meeting/")

    @app.get("/healthz")
    def health():
        return {"service": "contextpin-meeting-edge", "status": "ready", "provider_mode": edge().mode,
                "advertised_url": os.environ.get("EDGE_ADVERTISED_URL"), "observer_url": "/meeting/",
                "assembly_demo": bool(edge().profile.get("assembly"))}

    @app.get("/v1/capabilities")
    def capabilities():
        remote = bool(os.environ.get("AI_GATEWAY_URL"))
        return {"provider_mode": edge().mode, "microphone": edge().mode == "live" and (remote or provider_ready("speech")),
                "planner": edge().mode == "live" and (remote or provider_ready("planner")),
                "frame_capture": "device_reported", "raw_audio_saved": os.environ.get("STORE_RAW_AUDIO") == "true"}

    @app.get("/v1/profiles")
    @app.get("/v1/object-profiles")
    def profiles():
        registered = [edge().profile] if edge().profile.get("assembly") else geometry.list_profiles()
        active = edge().profile["object_id"]
        return {"profiles": sorted(registered, key=lambda p: p["object_id"] != active), "active_profile_id": active}

    @app.get("/v1/object-profiles/{profile_id}")
    def profile(profile_id: str):
        try:
            return geometry.load_profile(profile_id)
        except geometry.GeometryError as error:
            raise MeetingError(str(error), 404) from error

    @app.post("/v1/sessions", status_code=201)
    def create(body: CreateSession):
        return edge().create(body)

    @app.post("/v1/join")
    def join(request: Request, body: JoinSession):
        address = request.client.host if request.client else "local"
        with edge().lock:
            now = time.monotonic()
            attempts = app.state.join_attempts
            if len(attempts) >= 1000:
                for key in tuple(attempts):
                    if not attempts[key] or now - attempts[key][-1] >= 60:
                        del attempts[key]
                if len(attempts) >= 1000 and address not in attempts:
                    raise MeetingError("참가 요청이 많습니다. 잠시 후 다시 시도해 주세요.", 429)
            queue = attempts[address]
            while queue and now - queue[0] >= 60:
                queue.popleft()
            if len(queue) >= 12:
                raise MeetingError("참가 요청이 많습니다. 잠시 후 다시 시도해 주세요.", 429)
            queue.append(now)
        return edge().join(body)

    def bearer(authorization):
        return authorization[7:] if authorization.startswith("Bearer ") else ""

    @app.get("/v1/sessions/{sid}")
    @app.get("/v1/sessions/{sid}/snapshot")
    def snapshot(sid: str, authorization: str = Header(default="")):
        return edge().snapshot(sid, bearer(authorization))

    @app.get("/v1/sessions/{sid}/clock")
    def clock(sid: str, authorization: str = Header(default="")):
        edge().authenticated(sid, bearer(authorization))
        return {"session_time_ms": now_ms()}

    @app.post("/v1/sessions/{sid}/commands")
    def command(sid: str, body: Command, authorization: str = Header(default="")):
        try:
            result = edge().command(sid, bearer(authorization), body)
        except (ValueError, OverflowError) as error:
            if isinstance(error, MeetingError):
                raise
            raise MeetingError("유효한 숫자와 데이터를 사용해 주세요.") from error
        status = 202 if body.type in {"edit", "text", "replay", "clarify", "restore"} and "round_id" in result else 200
        return JSONResponse(result, status_code=status)

    @app.get("/v1/sessions/{sid}/contexts/{cid}")
    def context(sid: str, cid: str, authorization: str = Header(default="")):
        with edge().lock:
            edge().actor(sid, bearer(authorization))
            row = edge().db.execute("SELECT body FROM contexts WHERE session_id=? AND context_id=?", (sid, cid)).fetchone()
            if row is None:
                raise MeetingError("이 회의의 발화 맥락이 아닙니다.", 404)
            return json.loads(row["body"])

    @app.get("/v1/sessions/{sid}/minutes")
    def minutes(sid: str, authorization: str = Header(default="")):
        _, state = edge().authenticated(sid, bearer(authorization))
        return {"session_id": sid, "committed_version_id": state["committed_version_id"],
                "decisions": state["decisions"], "utterances": state["utterances"],
                "rounds": state["rounds"], "print_results": state["print_results"]}

    @app.get("/v1/sessions/{sid}/result")
    def meeting_review(sid: str, revision: int | None = None, authorization: str = Header(default="")):
        folder, result = edge().result_folder(sid, bearer(authorization), revision)
        try:
            manifest = json.loads((folder / "manifest.json").read_text(encoding="utf-8"))
            return {"manifest": manifest, "transcript": [json.loads(line) for line in
                    (folder / "transcript.jsonl").read_text(encoding="utf-8").splitlines() if line],
                    "transcript_text": (folder / "transcript.txt").read_text(encoding="utf-8"),
                    "summary_markdown": (folder / "summary.md").read_text(encoding="utf-8"),
                    "decisions": json.loads((folder / "decisions.json").read_text(encoding="utf-8")),
                    "final_scad": (folder / manifest["final_scad"]).read_text(encoding="utf-8"),
                    "zip_url": result["zip_url"]}
        except (OSError, ValueError, KeyError) as error:
            raise MeetingError("저장한 결과를 읽을 수 없습니다.", 409) from error

    @app.get("/v1/sessions/{sid}/result.zip")
    def meeting_zip(sid: str, revision: int | None = None, authorization: str = Header(default="")):
        folder, result = edge().result_folder(sid, bearer(authorization), revision)
        if not (folder / "result.zip").is_file():
            raise MeetingError("저장한 ZIP 파일을 찾을 수 없습니다.", 409)
        return FileResponse(folder / "result.zip", media_type="application/zip",
                            filename=f"contextpin-{sid}-r{result['revision']}.zip")

    def asset_file(version, name, download=False):
        asset = version["assets"].get(name)
        if not asset:
            raise MeetingError("등록된 모델 파일이 아닙니다.", 404)
        path = edge().models / version["version_id"] / name
        if not path.is_file():
            raise MeetingError("모델 파일을 찾을 수 없습니다.", 404)
        return FileResponse(path, media_type=asset["media_type"], headers={"ETag": '"' + asset["sha256"] + '"'},
                            filename="model.stl" if download and name == "print.stl" else None)

    @app.get("/v1/sessions/{sid}/versions/{vid}/assets/{name}")
    def files(sid: str, vid: str, name: str, authorization: str = Header(default="")):
        _, state = edge().authenticated(sid, bearer(authorization))
        return asset_file(edge().version(state, vid), name, download=True)

    @app.get("/v1/assets/{asset_id}")
    def global_asset(asset_id: str, authorization: str = Header(default="")):
        token = bearer(authorization)
        with edge().lock:
            from .coordinator import token_hash
            row = edge().db.execute("SELECT session_id FROM participants WHERE token_hash=?",
                                    (token_hash(token),)).fetchone() if token and len(token) <= 128 else None
            if not row:
                raise MeetingError("참가 인증이 필요합니다.", 401)
            state = edge().load(row["session_id"])
        for version in state["versions"]:
            for name, asset in version["assets"].items():
                if asset["asset_id"] == asset_id:
                    return asset_file(version, name)
        raise MeetingError("이 회의의 모델 파일이 아닙니다.", 404)

    @app.get("/v1/sessions/{sid}/versions/{vid}/export")
    def export(sid: str, vid: str, authorization: str = Header(default="")):
        _, state = edge().authenticated(sid, bearer(authorization))
        version = edge().version(state, vid)
        decision = next((item for item in reversed(state["decisions"]) if item["selected_model_version_id"] == vid), None)
        observations = [item for item in state["print_results"] if item["version_id"] == vid]
        raw = io.BytesIO()
        with zipfile.ZipFile(raw, "w", zipfile.ZIP_DEFLATED) as bundle:
            for name, asset in version["assets"].items():
                path = edge().models / vid / name
                if not path.is_file():
                    raise MeetingError("원본 파일 일부가 유실됐습니다.", 409)
                content = path.read_bytes()
                if hashlib.sha256(content).hexdigest() != asset["sha256"]:
                    raise MeetingError("원본 파일의 해시가 일치하지 않습니다.", 409)
                bundle.writestr(name, content)
                if name == "print.stl":
                    bundle.writestr("model.stl", content)
            bundle.writestr("decision.json", json.dumps(decision, ensure_ascii=False, indent=2))
            if state.get("assembly"):
                bundle.writestr("assembly_meeting.json", json.dumps(decision["assembly_record"] if decision else None,
                                                                   ensure_ascii=False, indent=2))
            bundle.writestr("print_observations.json", json.dumps(observations, ensure_ascii=False, indent=2))
            bundle.writestr("README_PRINT.txt", "\n".join([
                f"모델 버전 {vid}", f"설계/STL 단위 mm · GLB 단위 m · STL Z-up",
                f"STL 폭·깊이·높이 {version['assets']['print.stl']['bounds_mm']['size']} mm",
                f"GLB SHA-256 {version['assets']['preview.glb']['sha256']}",
                f"model.stl / print.stl SHA-256 {version['assets']['print.stl']['sha256']}",
                f"원본 SHA-256 {version['source_hash']} · 어댑터 {version['adapter_id']}/{version['adapter_revision']}",
                "엔진 버전은 engine.json에서 확인할 수 있습니다.",
                *(["candidate_source.zip을 풀고 build.json의 input_path를 OpenSCAD에서 엽니다."]
                  if "candidate_source.zip" in version["assets"] else
                  ["원본 SCAD와 parameters.json의 -D 값을 함께 사용하면 같은 버전을 열 수 있습니다."]
                  if state.get("assembly") else []),
                "슬라이서에서 100% 배율과 지지대·재질을 따로 확인하세요.",
                "형상 검사 결과는 geometry_report.json에 있습니다. 강도·맞음새·실제 출력은 별도 검증입니다.",
                "이 파일은 슬라이싱 또는 실제 출력이 완료되었음을 기록하지 않습니다."]))
        return Response(raw.getvalue(), media_type="application/zip",
                        headers={"Content-Disposition": f'attachment; filename="contextpin-{vid}.zip"'})

    @app.post("/v1/sessions/{sid}/media", status_code=201)
    async def image(sid: str, file: UploadFile = File(...), version_id: str = Form(...),
                    part_id: str | None = Form(default=None), source_mode: str = Form(default="manual"),
                    source_monotonic_ms: float | None = Form(default=None), clock_offset_ms: float | None = Form(default=None),
                    clock_uncertainty_ms: float | None = Form(default=None), authorization: str = Header(default="")):
        token = bearer(authorization)
        await run_in_threadpool(edge().authenticated, sid, token)
        raw = await file.read(MAX_MEDIA + 1)
        if not 0 < len(raw) <= MAX_MEDIA:
            raise MeetingError("사진 크기를 확인해 주세요.", 413)
        signature = b"\x89PNG\r\n\x1a\n" if file.content_type == "image/png" else b"\xff\xd8\xff" if file.content_type == "image/jpeg" else None
        if signature is None or not raw.startswith(signature):
            raise MeetingError("JPEG 또는 PNG 사진을 사용해 주세요.", 415)
        if source_mode not in {"live_capture", "manual"}:
            raise MeetingError("사진 출처를 확인해 주세요.")
        stamp = clock_stamp({"source_monotonic_ms": source_monotonic_ms, "clock_offset_ms": clock_offset_ms,
                             "clock_uncertainty_ms": clock_uncertainty_ms}, now_ms())
        return await run_in_threadpool(edge().record_image, sid, token, raw, file.content_type, version_id, part_id or None,
                                       stamp, source_mode)

    @app.get("/v1/sessions/{sid}/media/{asset_id}")
    def get_media(sid: str, asset_id: str, authorization: str = Header(default="")):
        _, state = edge().authenticated(sid, bearer(authorization))
        datum = next((item for item in state["images"] if item["evidence_id"] == asset_id), None)
        mime = datum["mime"] if datum else "audio/wav" if any(
            u.get("audio_asset_id") == asset_id for u in state["utterances"]) else None
        path = edge().root / "media" / asset_id
        if not mime or not path.is_file():
            raise MeetingError("이 회의의 근거 파일을 찾을 수 없습니다.", 404)
        return FileResponse(path, media_type=mime)

    @app.post("/v1/sessions/{sid}/audio", status_code=202)
    async def wave_upload(request: Request, sid: str, start_ms: int | None = None, bind_view: bool = False,
                          authorization: str = Header(default="")):
        token = bearer(authorization)
        await run_in_threadpool(edge().authenticated, sid, token)
        if request.headers.get("content-type", "").split(";")[0] != "audio/wav":
            raise MeetingError("WAV 오디오를 보내 주세요.", 415)
        raw = await request.body()
        duration = inspect_wav(raw)
        now = now_ms()
        stream = uid("audio_stream")
        begin = start_ms if start_ms is not None and now - 30_000 < start_ms <= now else now - duration
        actor, device = await run_in_threadpool(edge().audio_start, sid, token, stream, None)
        try:
            await run_in_threadpool(edge().audio_submit, sid, stream, 1, begin, begin + duration,
                                    raw, actor, device, actor if bind_view else None)
        finally:
            await run_in_threadpool(edge().audio_stop, sid, stream)
        return {"stream_id": stream, "state": "queued"}

    async def handshake(socket, key):
        origin = socket.headers.get("origin")
        scheme = "https" if socket.url.scheme == "wss" else "http"
        if origin and origin != scheme + "://" + socket.headers.get("host", ""):
            await socket.close(code=1008)
            return None
        await socket.accept()
        try:
            message = await asyncio.wait_for(socket.receive_json(), timeout=8)
        except (asyncio.TimeoutError, ValueError, KeyError):
            await socket.close(code=1008)
            return None
        if not isinstance(message, dict) or not isinstance(message.get(key), str):
            await socket.close(code=1008)
            return None
        return message

    @app.websocket("/v1/sessions/{sid}/events")
    async def event_stream(socket: WebSocket, sid: str):
        try:
            hello = await handshake(socket, "token")
            if hello is None:
                return
            actor, state = await run_in_threadpool(edge().authenticated, sid, hello["token"])
            initial = edge().public(state, actor)
            await socket.send_json({"type": "snapshot", "snapshot": initial})
            after = hello.get("after_seq")
            cursor = after if type(after) is int and 0 <= after <= initial["last_seq"] else initial["last_seq"]
            while True:
                packet = asyncio.create_task(socket.receive())
                try:
                    while not packet.done():
                        events, latest = await run_in_threadpool(edge().wait_events, sid, actor, cursor)
                        if packet.done():
                            break
                        for index, event in enumerate(events):
                            packet_body = {"type": "event", "event": event}
                            if index == len(events) - 1:
                                packet_body["snapshot"] = latest
                            await asyncio.wait_for(socket.send_json(packet_body), 5)
                            cursor = event["server_seq"]
                    message = await packet
                    if message["type"] == "websocket.disconnect":
                        return
                finally:
                    if not packet.done():
                        packet.cancel()
        except (WebSocketDisconnect, OSError, RuntimeError, asyncio.TimeoutError):
            return
        except MeetingError:
            await socket.close(code=1008)

    @app.websocket("/v1/audio-streams/{stream_id}")
    async def audio_stream(socket: WebSocket, stream_id: str):
        capture = None
        try:
            hello = await handshake(socket, "token")
            if hello is None:
                return
            if not re.fullmatch(r"[A-Za-z0-9_-]{1,100}", stream_id):
                raise MeetingError("오디오 스트림 ID를 확인해 주세요.")
            capture = await run_in_threadpool(AudioCapture, edge(), stream_id, hello)
            await socket.send_json({"type": "ready"})
            while True:
                message = await asyncio.wait_for(socket.receive(), 20)
                if message["type"] == "websocket.disconnect":
                    return
                if message.get("bytes") is not None:
                    await run_in_threadpool(capture.add, message["bytes"])
                elif message.get("text"):
                    packet = json.loads(message["text"])
                    if isinstance(packet, dict) and packet.get("type") == "stop":
                        last_sequence = packet.get("last_sequence")
                        if last_sequence is not None and (type(last_sequence) is not int or last_sequence < -1):
                            raise MeetingError("마지막 오디오 번호가 올바르지 않습니다.")
                        await run_in_threadpool(capture.close, True, last_sequence)
                        capture = None
                        await socket.send_json({"type": "stopped"})
                        await socket.close()
                        return
        except (WebSocketDisconnect, OSError, RuntimeError):
            return
        except (MeetingError, ValueError, asyncio.TimeoutError) as error:
            await socket.send_json({"type": "error", "message": str(error) or "오디오 연결이 시간 초과됐습니다."})
            await socket.close(code=1008)
        finally:
            if capture is not None:
                try:
                    await run_in_threadpool(capture.close)
                except MeetingError:
                    pass

    if WEB.is_dir():
        app.mount("/meeting", StaticFiles(directory=WEB, html=True), name="observer")
    return app


app = create_app()
