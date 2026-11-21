"""Two authenticated clients exercise the same CAD, lease and saved continuation."""
import json
import io
import subprocess
import zipfile
import os
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch

from fastapi.testclient import TestClient
from .main import create_app
from .planner import validated_plan
from .contracts import uid
from .openscad_geometry import executable
from .geometry import GeometryError


class SharedWorkflowTests(unittest.TestCase):
    def test_two_clients_preview_conflict_transfer_and_live_provenance(self):
        try:
            executable()
        except GeometryError as error:
            self.skipTest(str(error))
        root = Path(__file__).resolve().parents[2]
        source = root / "artifacts/cable-demo/cable_organizer.scad"
        if not source.exists():
            self.skipTest("Register the supplied cable CAD to run the integration check")
        with tempfile.TemporaryDirectory() as folder:
            work = Path(folder)
            config = work / "assembly.json"
            config.write_bytes((root / "backend/meeting/openscad_sample/cable-assembly.json").read_bytes())
            (work / "cable_organizer.scad").write_bytes(source.read_bytes())
            env = {"ACTIVE_OBJECT_PROFILE": "object_2", "OPENSCAD_ASSEMBLY_CONFIG": str(config),
                   "AI_MODE": "live", "FINAL_CONTEXT_DEBOUNCE_MS": "0", "EDGE_ADVERTISED_URL": "http://192.168.1.42:8788"}
            # Planner test double validates the complete context and returns registered numeric operations.
            def fixture(context, mode):
                return validated_plan(context, "fixture")
            with patch.dict(os.environ, env), TestClient(create_app(work / "edge", fixture)) as client:
                a = client.post('/v1/sessions', json={"display_name": "A", "device_id": "client-a"}).json()
                sid = a['session_id']
                b = client.post('/v1/join', json={"display_name": "B", "device_id": "client-b", "join_code": a['snapshot']['join_code']}).json()
                self.assertIn('token', b, b)
                prefix = '/v1/sessions/' + sid
                def headers(connection):
                    return {'Authorization': 'Bearer ' + connection['token']}
                def snapshot(connection=a):
                    response = client.get(prefix, headers=headers(connection))
                    self.assertEqual(response.status_code, 200, response.text)
                    return response.json()
                def command(kind, payload, connection=a, status=200, command_id=None):
                    response = client.post(prefix + '/commands', headers=headers(connection), json={
                        'command_id': command_id or uid('test'), 'type': kind, 'payload': payload})
                    self.assertIn(response.status_code, (status, 202) if status == 200 else (status,), response.text)
                    return response.json()
                def wait(predicate):
                    end = time.monotonic() + 90
                    while time.monotonic() < end:
                        current = snapshot()
                        if predicate(current):
                            return current
                        time.sleep(.05)
                    self.fail('Timeout waiting for CAD: ' + str(current.get('edit_draft')))
                def request(line, base):
                    record = command('text', {'text': line})
                    result = command('design_request', {'base_version_id': base, 'expected_adopted_revision': snapshot()['adopted_revision'],
                        'part_id': 'outer_shell', 'hit_point_asset_m': [0, .01, 0], 'utterance_ids': [record['utterance_id']]})
                    rid = result['round_id']
                    current = wait(lambda s: next(r for r in s['rounds'] if r['round_id'] == rid)['status'] in ('reviewable', 'failed'))
                    item = next(r for r in current['rounds'] if r['round_id'] == rid)
                    self.assertEqual(item['status'], 'reviewable', item)
                    return current, item, item['candidates'][0]
                def decide(item, candidate, connection=a):
                    current = snapshot()
                    return command('decide', {'round_id': item['round_id'], 'revision': item['revision'],
                        'candidate_id': candidate['candidate_id'], 'outcome': 'commit',
                        'expected_committed_version_id': current['committed_version_id'],
                        'expected_adopted_revision': current['adopted_revision']}, connection)
                command('meeting_start', {'mode': 'live', 'input_mode': 'manual'})
                original = snapshot()['committed_version_id']
                cid = uid('same-input')
                command('text', {'text': '충전선을 다섯 개 꽂아서 쓰고 싶습니다.'}, command_id=cid)
                command('text', {'text': '충전선을 다섯 개 꽂아서 쓰고 싶습니다.'}, command_id=cid)
                self.assertEqual(len(snapshot()['utterances']), 1)
                self.assertFalse(snapshot()['rounds'])
                command('replay', {'sample': 'cable'}, status=409)
                info = client.get(prefix + '/join-info', headers=headers(a)).json()
                self.assertIn('192.168.1.42', info['payload'])
                self.assertIn('<svg', info['qr_svg'])
                current, item, candidate = request('홈 좁은 입구를 3.4mm로 바꿔 주세요.', original)
                first = decide(item, candidate, b)
                self.assertIsNone(first['rationale_text'])
                self.assertEqual(first['design_version'], 'V2')
                second_base = first['selected_model_version_id']
                current, item, candidate = request('전체 길이를 60mm로 바꿔 주세요.', second_base)
                draft = command('edit_begin', {'base_version_id': candidate['version_id'], 'expected_adopted_revision': 1,
                    'round_id': item['round_id'], 'candidate_id': candidate['candidate_id']})['edit_draft']
                self.assertEqual(snapshot(b)['edit_draft']['lease_id'], draft['lease_id'])
                command('edit_begin', {'base_version_id': candidate['version_id'], 'expected_adopted_revision': 1,
                    'round_id': item['round_id'], 'candidate_id': candidate['candidate_id']}, b, status=409)
                command('edit_preview', {'lease_id': draft['lease_id'], 'preview_revision': 1, 'parameters': {'body_length': 58.1}})
                command('edit_preview', {'lease_id': draft['lease_id'], 'preview_revision': 2, 'parameters': {'body_length': 57.3}})
                command('edit_preview', {'lease_id': draft['lease_id'], 'preview_revision': 1, 'parameters': {'body_length': 59}}, status=409)
                command('edit_commit', {'lease_id': draft['lease_id'], 'preview_revision': 1, 'displayed_version_id': candidate['version_id']}, status=409)
                ready = wait(lambda s: s['edit_draft']['status'] in ('ready', 'failed'))['edit_draft']
                self.assertEqual(ready['status'], 'ready', ready)
                self.assertEqual(ready['preview_revision'], 2)
                self.assertAlmostEqual(ready['parameters']['slot_pitch'], 9.825)
                self.assertEqual(snapshot(b)['edit_draft']['version_id'], ready['version_id'])
                adopted = command('edit_commit', {'lease_id': draft['lease_id'], 'preview_revision': 2, 'displayed_version_id': ready['version_id']}, a)
                self.assertEqual(adopted['design_version'], 'V3')
                final = next(v for v in snapshot()['versions'] if v['version_id'] == ready['version_id'])
                self.assertAlmostEqual(final['measurements']['width_mm'], 57.3, places=3)
                self.assertEqual(final['parameters']['throat_width'], 3.4)
                self.assertEqual(snapshot()['workspace']['physical_original_version_id'], original)
                current, third, third_candidate = request('전체 길이를 58.2mm로 바꿔 주세요.', ready['version_id'])
                self.assertEqual(third['edit_base_version_id'], ready['version_id'])
                second_lease = command('edit_begin', {'base_version_id': third_candidate['version_id'], 'expected_adopted_revision': 2,
                    'round_id': third['round_id'], 'candidate_id': third_candidate['candidate_id']}, b)['edit_draft']
                command('edit_preview', {'lease_id': second_lease['lease_id'], 'preview_revision': 1, 'parameters': {'body_length': 1}}, b, status=422)
                command('edit_cancel', {'lease_id': second_lease['lease_id']}, b)
                self.assertEqual(snapshot()['committed_version_id'], final['version_id'])
                command('pointer', {'version_id': final['version_id'], 'part_id': 'outer_shell', 'hit_point_asset_m': [0,.01,0],
                    'workspace_position_m': [0,.01,.14], 'tracking_state': 'tracking'}, b)
                self.assertIn(b['participant_id'], snapshot()['pointers'])
                c = client.post('/v1/join', json={'display_name':'Late','device_id':'client-c','join_code':a['snapshot']['join_code']}).json()
                self.assertEqual(c['snapshot']['committed_version_id'], final['version_id'])
                self.assertEqual(snapshot(b)['committed_version_id'], final['version_id'])
                command('meeting_end', {})
                completed = wait(lambda s: s['session_status'] == 'completed')
                self.assertTrue(all(u['source_kind'] == 'manual_input' for u in completed['utterances']))
                result = client.get(completed['result']['api_url'], headers=headers(a)).json()
                self.assertEqual(result['manifest']['mode'], 'live')
                self.assertEqual(result['manifest']['adopted_version_id'], final['version_id'])
                self.assertEqual(result['manifest']['status'], 'complete')
                from .openscad_geometry import _stl_faces, _surface_hash
                archive_response = client.get(completed['result']['zip_url'], headers=headers(a))
                with zipfile.ZipFile(io.BytesIO(archive_response.content)) as archive:
                    standalone = work / 'standalone.scad'
                    rebuilt = work / 'rebuilt.stl'
                    standalone.write_bytes(archive.read(result['manifest']['final_scad']))
                    subprocess.run([executable(), '--export-format', 'binstl', '-o', rebuilt, standalone],
                                   check=True, capture_output=True, timeout=80)
                    self.assertEqual(_surface_hash(_stl_faces(rebuilt.read_bytes())),
                                     _surface_hash(_stl_faces(archive.read('cad/final.stl'))))
                    self.assertEqual(len(json.loads(archive.read('design_history.json'))), 3)
                    self.assertEqual(json.loads(archive.read('verification.json'))['status'], 'not_performed')
                continuation = client.post(prefix + '/continue', json={'display_name':'Next worker','device_id':'next-device'}, headers=headers(a))
                self.assertEqual(continuation.status_code, 201, continuation.text)
                next_session = continuation.json()['snapshot']
                self.assertEqual(next_session['committed_version_id'], final['version_id'])
                self.assertEqual(next_session['transfer']['source_session_id'], sid)
                self.assertEqual(len(next_session['decisions']), 2)
                self.assertFalse(next_session['utterances'])


if __name__ == '__main__':
    unittest.main()
