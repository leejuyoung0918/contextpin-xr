import base64
import asyncio
import os
import sys
import unittest
import urllib.request
import wave
from contextlib import redirect_stderr, redirect_stdout
from io import BytesIO, StringIO
from unittest.mock import MagicMock, patch

from . import bundle_provider, gateway, provider_http
from . import __main__ as launcher
from . import main as edge_service
from .contracts import MeetingError
from scripts import relay


class BundleProviderTests(unittest.TestCase):
    def test_local_mode_ignores_all_cloud_credentials_and_models(self):
        env = {"MEETING_TEAM_API_KEY": "test-team", "AI_API_KEY": "test-other",
               "MEETING_BUNDLE_API_KEY": "old", "AI_BASE_URL": "https://external.example/v1",
               "MEETING_TEAM_MODEL": "test-team-model", "AI_PLANNER_MODEL": "generic"}
        with patch.dict(os.environ, env, clear=True):
            self.assertEqual(bundle_provider.settings(), ("local", bundle_provider.LOCAL_URL, "qwen3-vl-2b", ""))
            os.environ.update(MEETING_BUNDLE_BASE_URL="http://localhost:9100/v1", MEETING_BUNDLE_MODEL="qa")
            self.assertEqual(bundle_provider.settings(), ("local", "http://localhost:9100/v1", "qa", ""))
            for url in [bundle_provider.TEAM_URL, "http://localhost:9100@external.example/v1"]:
                os.environ["MEETING_BUNDLE_BASE_URL"] = url
                with self.assertRaises(MeetingError):
                    bundle_provider.settings()

    def test_team_mode_is_rejected_even_with_complete_credentials(self):
        env = {"MEETING_CANDIDATE_AI": "team", "MEETING_BUNDLE_BASE_URL": "http://localhost:9100/v1",
               "MEETING_BUNDLE_MODEL": "local-model", "MEETING_BUNDLE_API_KEY": "old", "AI_API_KEY": "other"}
        with patch.dict(os.environ, env, clear=True):
            with self.assertRaises(MeetingError):
                bundle_provider.settings()
            os.environ.update(MEETING_TEAM_API_KEY="test-team", MEETING_TEAM_MODEL="team-vision")
            with self.assertRaises(MeetingError):
                bundle_provider.settings()

    def test_demo_launchers_ignore_inherited_cloud_and_block_explicit_team(self):
        contaminated = {"MEETING_CANDIDATE_AI":"team", "MEETING_TEAM_MODEL":"private", "MEETING_TEAM_API_KEY":"sample",
                        "MEETING_PLAN_BACKEND":"provider", "MEETING_STT_BACKEND":"provider",
                        "AI_GATEWAY_URL":"https://example.invalid", "OPENAI_API_KEY":"sample",
                        "AI_API_KEY":"sample", "AI_PLANNER_MODEL":"remote", "AI_BASE_URL":"https://example.invalid"}
        def local(env):
            self.assertEqual(env["MEETING_CANDIDATE_AI"], "local")
            self.assertEqual(env["MEETING_STT_BACKEND"], "local")
            self.assertEqual(env["MEETING_PLAN_BACKEND"], "registered")
            self.assertFalse(set(env) & {"MEETING_TEAM_API_KEY", "OPENAI_API_KEY", "AI_API_KEY", "AI_GATEWAY_URL", "AI_BASE_URL"})
        for cmd in ("server", "replay-server", "open-saved-review"):
            args = ["relay", cmd] + (["--session","sample"] if cmd == "open-saved-review" else [])
            with patch.object(sys, "argv", args), patch.object(relay, "run") as launch, redirect_stdout(StringIO()), \
                    patch.object(relay, "meeting_env", return_value={**contaminated,
                        "OPENSCAD_ASSEMBLY_CONFIG":__file__, "EDGE_ADVERTISED_URL":"http://localhost:8789"}):
                relay.main()
                local(launch.call_args.kwargs["env"])
        for service in ("assembly", "edge"):
            with patch.dict(os.environ, contaminated, clear=True), patch.object(sys, "argv", ["meeting", service]), \
                    patch.object(launcher.uvicorn, "run") as launch:
                launcher.main()
                local(os.environ)
                launch.assert_called_once()
        for app, argv in ((relay, ["relay", "server", "--candidate-ai", "team"]),
                          (launcher, ["meeting", "assembly", "--candidate-ai", "team"])):
            with patch.object(sys, "argv", argv), redirect_stderr(StringIO()), \
                    patch.object(relay, "meeting_env", return_value=contaminated.copy()), \
                    patch.object(relay, "run") as child, patch.object(launcher.uvicorn, "run") as serve, \
                    self.assertRaises(SystemExit):
                try:
                    app.main()
                finally:
                    child.assert_not_called()
                    serve.assert_not_called()

    def test_pc_transport_allows_only_local_candidate_chat_without_proxy_or_redirect(self):
        reply = MagicMock()
        reply.__enter__.return_value.read.return_value = b"{}"
        with patch.dict(os.environ, {"MEETING_CANDIDATE_AI":"local", "HTTP_PROXY":"http://proxy.invalid"}, clear=True), \
                patch.object(urllib.request, "urlopen") as remote, patch.object(urllib.request, "build_opener") as opener:
            opener.return_value.open.return_value = reply
            for path, url in [("/chat/completions", bundle_provider.TEAM_URL), ("/audio/transcriptions", None)]:
                with self.assertRaises(provider_http.InputError):
                    provider_http.upstream(path, b"{}", base_url=url, api_key="sample")
            provider_http.upstream("/chat/completions", b"{}", base_url=bundle_provider.LOCAL_URL, api_key="")
            no_proxy, no_redirect = opener.call_args.args
            self.assertEqual(no_proxy.proxies, {})
            with self.assertRaises(provider_http.InputError):
                no_redirect.redirect_request(None, None, 302, "", {}, bundle_provider.TEAM_URL)
            remote.assert_not_called()
            opener.return_value.open.assert_called_once()

    def test_local_stt_failure_is_reported_without_a_cloud_retry(self):
        pcm = BytesIO()
        with wave.open(pcm, "wb") as wav:
            wav.setparams((1, 2, 16000, 0, "NONE", "not compressed"))
            wav.writeframes(bytes(32000))
        with patch.dict(os.environ, {}, clear=True), patch.object(gateway, "provider_ready", return_value=True), \
                patch.object(gateway, "load_whisper", side_effect=OSError("unavailable")), \
                patch.object(gateway, "transcribe") as paid:
            bundle_provider.configure_pc(os.environ)
            with self.assertRaisesRegex(MeetingError, "로컬"):
                gateway.transcribe_live({"wav_base64":base64.b64encode(pcm.getvalue()).decode()})
            paid.assert_not_called()

    def test_exported_asgi_app_also_pins_local_inference_without_the_cli(self):
        async def startup():
            async with edge_service.app.router.lifespan_context(edge_service.app):
                self.assertEqual(os.environ["MEETING_STT_BACKEND"], "local")
                self.assertEqual(os.environ["MEETING_PLAN_BACKEND"], "registered")
                self.assertEqual(os.environ["MEETING_CANDIDATE_AI"], "local")
                self.assertNotIn("AI_GATEWAY_URL", os.environ)
        with patch.dict(os.environ, {"MEETING_CANDIDATE_AI":"team","AI_GATEWAY_URL":"https://example.invalid"}, clear=True), \
                patch.object(edge_service, "SessionCoordinator"):
            asyncio.run(startup())

    def test_team_credential_is_never_forwarded_on_redirect(self):
        response = MagicMock()
        response.__enter__.return_value.read.return_value = b"{}"
        with patch.object(urllib.request, "urlopen", return_value=response) as transport:
            provider_http.upstream("/chat/completions", b"{}", base_url=bundle_provider.TEAM_URL, api_key="test-team")
        request = transport.call_args.args[0]
        self.assertEqual(request.get_header("Authorization"), "Bearer test-team")
        redirect = urllib.request.HTTPRedirectHandler().redirect_request(
            request, None, 302, "redirect", {}, "http://127.0.0.1:9100/v1")
        self.assertIsNone(redirect.get_header("Authorization"))
