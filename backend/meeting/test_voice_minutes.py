"""Mac/CPU end-to-end smoke with synthetic Korean speech and the registered cable CAD."""
import hashlib
import io
import json
import os
from pathlib import Path
import shutil
import queue
import threading
import struct
import subprocess
import tempfile
import time
import unittest
from unittest.mock import patch
import wave
import zipfile

from fastapi.testclient import TestClient
from .contracts import uid
from .gateway import local_model_dir
from .main import create_app
from .shared_workflows import cue_text

ROOT = Path(__file__).resolve().parents[2]


class MeetingVoiceTests(unittest.TestCase):
    def test_groove_candidates_follow_the_requested_edit(self):
        source = ROOT / "artifacts/cable-demo/cable_organizer.scad"
        if not source.is_file():
            self.skipTest("Register the original cable CAD for this integration check")
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp)
            config = folder / "assembly.json"
            shutil.copyfile(ROOT / "backend/meeting/openscad_sample/cable-assembly.json", config)
            shutil.copyfile(source, folder / source.name)
            env = {"AI_MODE": "live", "MEETING_PLAN_BACKEND": "registered", "ACTIVE_OBJECT_PROFILE": "object_2",
                   "OPENSCAD_ASSEMBLY_CONFIG": str(config), "FINAL_CONTEXT_DEBOUNCE_MS": "0"}
            bundled = ROOT / "artifacts/tools/OpenSCAD.app/Contents/MacOS/OpenSCAD"
            if bundled.is_file() and not os.getenv("OPENSCAD_EXECUTABLE") and not shutil.which("openscad"):
                env["OPENSCAD_EXECUTABLE"] = str(bundled)
            with patch.dict(os.environ, env), TestClient(create_app(folder / "edge")) as client:
                res = client.post("/v1/sessions", json={"display_name": "설계 요청 시험", "device_id": "intent-test"})
                self.assertEqual(res.status_code, 201, res.text)
                access = res.json()
                url = "/v1/sessions/" + access["session_id"]
                headers = {"Authorization": "Bearer " + access["token"]}
                def command(kind, **payload):
                    out = client.post(url + "/commands", headers=headers, json={
                        "type": kind, "command_id": uid("test"), "payload": payload})
                    self.assertIn(out.status_code, (200, 202), out.text)
                def snapshot():
                    return client.get(url, headers=headers).json()
                def reviewable():
                    deadline = time.monotonic() + 120
                    while time.monotonic() < deadline:
                        state = snapshot()
                        item = state["rounds"][-1]
                        if item["status"] in {"reviewable", "failed", "needs_input"}:
                            break
                        time.sleep(.2)
                    self.assertEqual(item["status"], "reviewable", item.get("error"))
                    return state, item
                command("meeting_start", mode="live", input_mode="manual")
                for text, eligible in [("안녕하세요.", False), ("홈이 다섯 개입니다.", False),
                                       ("충전선이 불거워", False), ("충전선이 헬거워", True)]:
                    command("text", text=text)
                    state = snapshot()
                    self.assertEqual(state["rounds"], [])
                    self.assertEqual(bool(state.get("active_design_utterance")), eligible)
                command("text", text="수정본 만들어줘")
                state, first = reviewable()
                self.assertEqual([u["text"] for u in state["utterances"][-2:]], ["충전선이 헬거워", "수정본 만들어줘"])
                self.assertEqual(first["context"]["utterance"]["text"], "충전선이 헬거워")
                self.assertEqual(first["context"]["trigger_utterance_id"], state["utterances"][-1]["utterance_id"])
                self.assertEqual([c["title"] for c in first["candidates"]], ["입구만 축소", "홈 내부만 축소", "입구와 내부 축소"])
                line = "케이블 홈 크기를 좀 키워줘."
                command("text", text=line)
                state, item = reviewable()
                self.assertEqual(item["context"]["utterance"]["text"], line)
                view = item["context"]["selection_snapshot"]
                self.assertEqual(view["source_mode"], "profile_scope")
                self.assertIsNone(view["hit_point_asset_m"])
                self.assertIsNone(view["image_id"])
                self.assertEqual([c["title"] for c in item["candidates"]], ["입구만 확대", "홈 내부만 확대", "입구와 내부 확대"])
                versions = {v["version_id"]: v for v in state["versions"]}
                for asset in ("print.stl", "preview.glb"):
                    self.assertEqual(len({versions[c["version_id"]]["assets"][asset]["sha256"] for c in item["candidates"]}), 3)
                # Replay the exact finals from the failed local-Whisper run; audio decoding can vary by run.
                observed = "충전선이 너무 헐렁해서 자꾸 빠져요"
                command("text", text=observed)
                self.assertEqual(len(snapshot()["rounds"]), 2)
                command("text", text="수정해 보여주세요")
                state, retry = reviewable()
                self.assertEqual(len(state["rounds"]), 3)
                self.assertEqual(retry["context"]["utterance"]["text"], observed)
                self.assertEqual(retry["context"]["trigger_utterance_id"], state["utterances"][-1]["utterance_id"])
                self.assertEqual([c["title"] for c in retry["candidates"]], ["입구만 축소", "홈 내부만 축소", "입구와 내부 축소"])

    def test_only_an_affirmative_request_or_concrete_agreement_triggers(self):
        for line in ("나는 A가 좋아.", "홈을 줄여볼까?", "아직 합의하지 않았어.", "수정안 보여주지 마.",
                     "수정안 보여줘라고 말했지.", "후보 A로 결정하자.", "길이를 줄이지 않기로 합의했어.",
                     "수정해 보여주세요?", "수정해 보여주세요라고 말했지.", "수정해 보여주지 마.",
                     "일정표를 수정해 보여주세요."):
            self.assertIsNone(cue_text(line), line)
        for line in ("수정안 보여줘.", "홈을 좁혀서 후보 보여주세요.", "길이를 줄이기로 합의했어.",
                     "그러면 내부 공간은 유지하고 입구만 좁혀보자.", "케이블 홈 크기를 좀 키워줘."):
            self.assertIsNotNone(cue_text(line), line)
        self.assertEqual(cue_text("수정해 보여주세요"), "")

    @unittest.skipUnless(os.getenv("MEETING_VOICE_SMOKE") == "1", "Set MEETING_VOICE_SMOKE=1 for local Whisper/OpenSCAD integration")
    def test_real_audio_cad_and_download(self):
        for tool in ("say", "ffmpeg"):
            self.assertIsNotNone(shutil.which(tool), tool)
        self.assertTrue((local_model_dir() / "model.bin").is_file())
        env = {"AI_MODE": "live", "MEETING_STT_BACKEND": "local", "MEETING_PLAN_BACKEND": "registered",
               "OPENSCAD_ASSEMBLY_CONFIG": str(ROOT / "artifacts/cable-demo/assembly.json"),
               "ACTIVE_OBJECT_PROFILE": "object_2", "FINAL_CONTEXT_DEBOUNCE_MS": "0"}
        if not os.environ.get("OPENSCAD_EXECUTABLE") and not shutil.which("openscad"):
            env["OPENSCAD_EXECUTABLE"] = str(ROOT / "artifacts/tools/OpenSCAD.app/Contents/MacOS/OpenSCAD")
        with tempfile.TemporaryDirectory() as folder, patch.dict(os.environ, env), TestClient(create_app(Path(folder)/"edge")) as client:
            def synth(key, line):
                src = Path(folder) / (key + ".aiff")
                dst = src.with_suffix(".wav")
                subprocess.run(["say", "-v", "Yuna", "-r", "165", "-o", str(src), line], check=True)
                subprocess.run(["ffmpeg", "-loglevel", "error", "-i", str(src), "-ac", "1", "-ar", "16000", str(dst)], check=True)
                with wave.open(str(dst)) as reader:
                    return reader.readframes(reader.getnframes())
            loose = synth("loose", "충전선이 너무 헐렁해서 자꾸 빠져요.")
            cue = synth("cue", "수정안 보여 주세요.")
            length = synth("length", "홈 다섯 개는 그대로 두고, 정리대 전체 길이를 오십칠 점 삼 밀리미터로 바꿔 주세요.")
            resp = client.post("/v1/sessions", json={"device_id": "voice-test", "display_name": "합성 음성 시험"})
            self.assertEqual(resp.status_code, 201, resp.text)
            conn = resp.json()
            sid, token = conn["session_id"], conn["token"]
            base = "/v1/sessions/" + sid
            headers = {"Authorization": "Bearer " + token}
            def cmd(kind, **payload):
                resp = client.post(base + "/commands", headers=headers, json={"command_id": uid("test"), "type": kind, "payload": payload})
                self.assertIn(resp.status_code, {200, 202}, resp.text)
                return resp.json()
            def snap():
                return client.get(base, headers=headers).json()
            def wait(check, timeout=50):
                deadline = time.monotonic() + timeout
                while time.monotonic() < deadline:
                    state = snap()
                    if check(state):
                        return state
                    time.sleep(.1)
                self.fail(str({"error": state.get("speech_error"), "notice": state.get("speech_intent_notice"),
                               "rounds": [(r["status"], r.get("error"), r.get("question")) for r in state["rounds"]],
                               "utterances": [u["text"] for u in state["utterances"]]}))
            def select(part):
                state = snap()
                target = dict(base_version_id=state["committed_version_id"], expected_adopted_revision=state["adopted_revision"],
                              part_id=part, hit_point_asset_m=[0, .005, 0])
                cmd("design_target", **target)
                return target
            cmd("meeting_start", mode="live", input_mode="microphone")
            seq, sample = 0, 0
            stream = uid("stream")
            with client.websocket_connect("/v1/audio-streams/" + stream) as socket:
                socket.send_json({"session_id":sid, "token":token, "device_id":"voice-test", "codec":"pcm_s16le",
                                  "channels":1, "actual_sample_rate_hz":16000, "stream_start_sample_index":0})
                self.assertEqual(socket.receive_json()["type"], "ready")
                # The real headset sends silence frames while humans inspect the candidates.
                pending = queue.Queue()
                stop = threading.Event()
                def produce():
                    nonlocal seq, sample
                    deadline = time.monotonic()
                    block = b""
                    while not stop.is_set():
                        if not block:
                            try:
                                block = pending.get_nowait()
                            except queue.Empty:
                                pass
                        frame = block[:3200].ljust(3200, b"\x00")
                        block = block[3200:]
                        try:
                            socket.send_bytes(struct.pack("<IQI", seq, sample, 1600) + frame)
                        except (RuntimeError, OSError):
                            return
                        seq += 1
                        sample += 1600
                        deadline += .1
                        stop.wait(max(0, deadline - time.monotonic()))
                producer = threading.Thread(target=produce, daemon=True)
                producer.start()
                def say(pcm):
                    pending.put(pcm + bytes(2 * 16000))
                say(loose)
                self.assertFalse(wait(lambda s: len(s["utterances"]) >= 1)["rounds"])
                say(cue)
                first = wait(lambda s: s["rounds"] and s["rounds"][-1]["status"] == "reviewable")
                first_round = first["rounds"][-1]
                self.assertEqual([c["title"] for c in first_round["candidates"]], ["입구만 축소", "홈 내부만 축소", "입구와 내부 축소"])
                self.assertEqual(first_round["proposal_provenance"]["source"], "registered_rules")
                self.assertFalse(first["decisions"])
                view = first_round["context"]["selection_snapshot"]
                self.assertEqual(view["source_mode"], "profile_scope")
                self.assertIsNone(view["hit_point_asset_m"])
                self.assertIsNone(view["image_id"])
                duplicate = cmd("design_request", use_registered_scope=True, base_version_id=first["committed_version_id"],
                                expected_adopted_revision=first["adopted_revision"], utterance_ids=[u["utterance_id"] for u in first["utterances"]])
                self.assertEqual(duplicate["round_id"], first_round["round_id"])
                def adopt(r, reason):
                    current = snap()
                    return cmd("decide", round_id=r["round_id"], revision=r["revision"], outcome="commit",
                               candidate_id=r["candidates"][0]["candidate_id"], reason=reason,
                               expected_committed_version_id=current["committed_version_id"], expected_adopted_revision=current["adopted_revision"])
                adopt(first_round, "안쪽 공간을 유지하려고 입구만 좁혔다.")
                select("outer_shell")
                say(length)
                second = wait(lambda s: len(s["rounds"]) == 2 and s["rounds"][-1]["status"] == "reviewable")
                second_round = second["rounds"][-1]
                self.assertEqual(len(second_round["candidates"]), 1)
                self.assertEqual(second_round["edit_base_version_id"], second["committed_version_id"])
                final = next(v for v in second["versions"] if v["version_id"] == second_round["candidates"][0]["version_id"])
                self.assertAlmostEqual(final["measurements"]["width_mm"], 57.3, places=2)
                self.assertEqual(final["parameters"]["throat_width"], 3.4)
                adopt(second_round, "다섯 홈을 유지하며 길이를 줄였다.")
                stop.set()
                producer.join(2)
                socket.send_json({"type":"stop", "last_sequence":seq-1})
                self.assertEqual(socket.receive_json()["type"], "stopped")
            cmd("meeting_end")
            done = wait(lambda s: s.get("result", {}).get("api_url") if s.get("result") else False)
            rev = done["result"]["revision"]
            txt = client.get(base + "/minutes.txt", headers=headers)
            self.assertEqual(txt.status_code, 200, txt.text)
            self.assertIn("안쪽 공간을 유지", txt.text)
            self.assertIn("다섯 홈을 유지", txt.text)
            self.assertEqual(len(done["utterances"]), 3)
            for u in done["utterances"]:
                self.assertIn(u["text"], txt.text)
                self.assertEqual(u["transcription_provider"], "local_whisper")
            self.assertEqual(client.get(base + "/minutes-code").status_code, 401)
            code = client.get(base + "/minutes-code", headers=headers).json()["code"]
            self.assertEqual(client.post("/v1/minutes-download", json={"code":"INVALID"}).status_code, 403)
            downloaded = client.post("/v1/minutes-download", json={"code": code})
            self.assertEqual(downloaded.content, txt.content)
            with zipfile.ZipFile(io.BytesIO(client.get(base + "/result.zip", headers=headers).content)) as bundle:
                self.assertEqual(bundle.read("minutes.txt"), txt.content)
                self.assertEqual(hashlib.sha256(bundle.read("cad/final.stl")).hexdigest(), final["assets"]["print.stl"]["sha256"])
            print(json.dumps({"speech":[u["text"] for u in done["utterances"]], "rounds":[3,1],
                              "width_mm":final["measurements"]["width_mm"], "minutes_revision":rev}, ensure_ascii=False))
