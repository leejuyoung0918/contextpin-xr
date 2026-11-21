"""Exercise immutable evidence, failed generation, authenticated retry and both downloads."""
import io
import json
import threading
import time
import zipfile
from unittest.mock import patch

from . import minutes_generator
from .contracts import MeetingError
from .test_explicit_design import ExplicitDesignCase


class MinutesExportTests(ExplicitDesignCase):
    def wait_result(self, revision):
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            snapshot = self.snapshot()
            if snapshot["session_status"] == "completed" and snapshot["result"]["revision"] == revision:
                return snapshot["result"]
            time.sleep(.04)
        self.fail("Result revision was not published")

    def download(self, result):
        reply = self.client.get(result["zip_url"], headers=self.headers)
        self.assertEqual(reply.status_code, 200)
        with zipfile.ZipFile(io.BytesIO(reply.content)) as archive:
            return {name: archive.read(name) for name in archive.namelist()}

    def test_retry_after_model_failure_uses_exact_raw_package(self):
        self.command("text", {"text": "받침 크기는 지금이 괜찮습니다."})
        observed = []
        def fail(context):
            root = self.client.app.state.coordinator.root / "results" / self.sid
            observed.extend(folder for folder in root.glob("*/result.zip") if folder.is_file())
            raise MeetingError("로컬 모델 연결 실패", 502)

        with patch.object(minutes_generator, "generate", side_effect=fail):
            self.command("meeting_end", {})
            failed = self.wait_result(1)
        self.assertEqual(len(observed), 1)
        self.assertEqual(failed["minutes_status"], "generation_failed")
        raw = self.download(failed)
        for name in ("transcript.jsonl", "transcript.txt", "decisions.json", "design_history.json",
                     "cad/final.scad", "cad/parameters.json", "cad/final.stl"):
            self.assertIn(name, raw)
        self.assertNotIn("minutes/minutes.md", raw)
        api = self.prefix + "/minutes/regenerate"
        self.assertEqual(self.client.post(api, headers=self.peer).status_code, 403)
        self.assertEqual(self.client.get(self.prefix + "/minutes.md", headers=self.headers).status_code, 404)
        entered, release = threading.Event(), threading.Event()
        def choose(_background, _rows, allowed, _base, _model):
            entered.set()
            if not release.wait(8):
                raise ValueError("test did not release AI")
            return {k: ids[:1] for k, ids in allowed.items()}
        with patch.object(minutes_generator, "_select", side_effect=choose) as model:
            first = self.client.post(api, headers=self.headers)
            self.assertEqual(first.status_code, 202)
            try:
                self.assertTrue(entered.wait(5))
                second = self.client.post(api, headers=self.headers)
                self.assertEqual(first.json(), second.json())
                self.assertEqual(self.download(failed), raw)
            finally:
                release.set()
            success = self.wait_result(2)
            self.assertEqual(model.call_count, 1)
        self.assertEqual(success["minutes_status"], "generated")
        enriched = self.download(success)
        self.assertEqual({k: enriched[k] for k in raw if k != "manifest.json"},
                         {k: v for k, v in raw.items() if k != "manifest.json"})
        report = json.loads(enriched["minutes/minutes.json"])
        self.assertEqual((report["meeting_id"], report["final_version_id"], report["design_decisions"]),
                         (self.sid, self.original, []))
        self.assertEqual(self.client.get(self.prefix + "/minutes.json", headers=self.headers).json(), report)
        self.assertEqual(self.client.get(self.prefix + "/minutes.md", headers=self.headers).content,
                         enriched["minutes/minutes.md"])
        code = self.client.get(self.prefix + "/minutes-code", headers=self.headers).json()["code"]
        redeemed = self.client.post("/v1/minutes-download", json={"code": code, "format": "markdown"})
        self.assertEqual(redeemed.content, enriched["minutes/minutes.md"])
        self.assertEqual(self.download(failed), raw)
