"""Export non-content aggregate metrics from ContextPin's device and PC stores."""
from collections import Counter
from datetime import datetime, timezone
import json
import math
from pathlib import Path
import sqlite3


def list_of_dicts(value):
    return [item for item in value if isinstance(item, dict)] if isinstance(value, list) else []


def counts(items, field):
    return dict(sorted(Counter(item.get(field) or 'unrecorded' for item in items).items()))


def times(values):
    ordered = sorted(value for value in values
                     if type(value) in (int, float) and math.isfinite(value) and value >= 0)
    if not ordered:
        return {'samples': 0, 'p50': None, 'p95': None}
    return {'samples': len(ordered), 'p50': ordered[math.ceil(.50*len(ordered))-1],
            'p95': ordered[math.ceil(.95*len(ordered))-1]}


def instant(value):
    try:
        result = datetime.fromisoformat(value)
        return result if result.tzinfo is not None else None
    except (TypeError, ValueError):
        return None


def interval(start, end):
    begin, finish = instant(start), instant(end)
    if begin is None or finish is None or finish < begin:
        return None
    return round((finish-begin).total_seconds(), 3)


def run_metrics(run):
    events = list_of_dicts(run.get('events'))
    verifications = list_of_dicts(run.get('verifications'))
    pending = {}
    recurs = []
    for event in events:
        step = event.get('stepId')
        if event.get('kind') == 'help_requested':
            pending.setdefault(step, event.get('utc'))
        if event.get('kind') == 'evidence_replayed' and step in pending:
            recurs.append(interval(pending.pop(step), event.get('utc')))
    elapsed = interval(run.get('startedUtc'), run.get('completedUtc'))
    active = None if elapsed is None else round(max(0, elapsed-(run.get('pausedSeconds') or 0)), 3)
    return {
        'runId': run.get('runId'),
        'mode': 'physical' if run.get('physical') else 'simulation',
        'sourceMode': run.get('sourceMode') or 'legacy',
        'guideVersion': run.get('guideVersion'),
        'guideHash': run.get('guideHash'),
        'startedAt': run.get('startedUtc'),
        'completedAt': run.get('completedUtc'),
        'activeSeconds': active,
        'completedSteps': run.get('stepIndex', 0),
        'completed': bool(run.get('completed')),
        'completionBasis': run.get('completionBasis') or 'unrecorded',
        'helpRequests': run.get('helpRequests', 0),
        'hintUses': run.get('hintUses', 0),
        'simulatorWrongAttempts': None if run.get('physical') else run.get('wrongAttempts', 0),
        'simulatorCorrectionSeconds': None if run.get('physical') else run.get('correctionSeconds', 0),
        'practiceEventsByKind': counts(events, 'kind'),
        'verificationByBasis': counts(verifications, 'basis'),
        'verificationByClaim': counts(verifications, 'claimType'),
        'helpToReplaySeconds': times(recurs),
    }


def capture_metrics(captures):
    return {
        'savedPhotos': len(captures),
        'bytes': sum(item['bytes'] for item in captures if type(item.get('bytes')) is int),
        'photosBySource': counts(captures, 'source'),
        'photosByPhase': counts(captures, 'phase'),
        'photosByQuality': counts(captures, 'quality'),
        'captureToSaveMs': times([item.get('elapsedMs') for item in captures]),
    }


def fields(value, keys, value_types):
    return ({key: value[key] for key in keys if key in value and type(value[key]) in value_types}
            if isinstance(value, dict) else {})


def device_report(path, session_id):
    if not path.exists():
        raise ValueError('기기 세션 파일 또는 폴더를 찾을 수 없습니다.')
    files = [path] if path.is_file() else sorted(path.rglob('session.json'))
    results = []
    invalid = 0
    for file in files:
        try:
            session = json.loads(file.read_text(encoding='utf-8-sig'))
            identifier = session.get('id')
            if not isinstance(identifier, str) or not identifier:
                raise ValueError('Invalid session id')
        except (OSError, ValueError, AttributeError):
            invalid += 1
            continue
        if session_id and identifier != session_id:
            continue
        registration = list_of_dicts(session.get('registrations'))
        captures = list_of_dicts(session.get('captures'))
        notes = list_of_dicts(session.get('notes'))
        mode = session.get('transferMode')
        if not mode:
            mode = 'synthetic' if session.get('mode') in {'demo','guide-example'} else 'legacy'
        approvals = list_of_dicts(session.get('approvedGuides'))
        results.append({
            'sessionId': identifier, 'sourceMode': mode, 'createdAt': session.get('createdUtc'),
            'schemaVersion': session.get('schemaVersion'), 'status': session.get('status'),
            'shutterRequests': session.get('shutterRequests'), 'rawReadbacks': session.get('photoReadbacks'),
            'failedPhotos': session.get('failedPhotos'), **capture_metrics(captures),
            'noteCount': len(notes), 'notesBySource': counts(notes, 'sourceMode'),
            'registrationByRole': counts(registration, 'role'), 'registrationBySource': counts(registration, 'source'),
            'guideVersions': [{field: record.get(field) for field in ('id','version','contentHash','approvedAt','boardHash')}
                              for record in approvals],
            'runs': [run_metrics(run) for run in list_of_dicts(session.get('guideRuns'))],
        })
    return {'source':'device_archive','sessions':results,'invalidFiles':invalid}


def workstation_report(path, session_id):
    database = path / 'transfer.sqlite3' if path.is_dir() else path
    if not database.is_file():
        raise ValueError('PC relay transfer.sqlite3를 찾을 수 없습니다.')
    try:
        with sqlite3.connect(database.resolve().as_uri() + '?mode=ro', uri=True) as db:
            db.row_factory = sqlite3.Row
            sql = 'SELECT id,source_mode,stage,snapshot,created_at,updated_at FROM sessions'
            if session_id:
                rows = db.execute(sql+' WHERE id=?',(session_id,)).fetchall()
            else:
                rows = db.execute(sql+' ORDER BY created_at').fetchall()
            results = []
            for row in rows:
                identifier = row['id']
                snapshot = json.loads(row['snapshot'])
                command = db.execute('SELECT kind,status,COUNT(*) AS count FROM commands WHERE session_id=? GROUP BY kind,status',(identifier,))
                assets = db.execute('SELECT mime,COUNT(*) AS count,SUM(size) AS bytes FROM assets WHERE session_id=? GROUP BY mime',(identifier,))
                approvals = db.execute('SELECT guide_id AS guideId,version,content_hash AS hash,created_at AS createdAt FROM approvals WHERE session_id=? ORDER BY version',(identifier,))
                jobs = [json.loads(data['value']) for data in db.execute('SELECT value FROM observation_jobs WHERE session_id=?',(identifier,))]
                results.append({
                    'sessionId':identifier,'sourceMode':row['source_mode'],'stage':row['stage'],
                    'createdAt':row['created_at'],'updatedAt':row['updated_at'],
                    'deviceMetrics':fields(snapshot.get('metrics'),('shutterRequests','photoReadbacks','failedPhotos',
                        'savedPhotos','uploadedPhotos'),(int,)),
                    'deviceCapabilities':fields(snapshot.get('capabilities'),('device','camera','microphone','speech','vision'),(str,)),
                    'uploadedAssets':[dict(asset) for asset in assets],
                    'commands':[dict(item) for item in command],
                    'guideVersions':[dict(item) for item in approvals],
                    'observationJobsByStatus':counts(jobs,'status'),
                    'observationJobElapsedMs':times([job.get('elapsedMs') for job in jobs]),
                    'runs':[run_metrics(run) for run in list_of_dicts(snapshot.get('runs'))],
                })
    except (sqlite3.Error, ValueError, AttributeError) as exc:
        raise ValueError('PC relay 기록 스키마를 읽지 못했습니다.') from exc
    return {'source':'pc_relay','sessions':results}


def collect_report(archive=None, relay_data=None, session_id=None):
    records=[]
    if archive:
        records.append(device_report(Path(archive),session_id))
    if relay_data:
        records.append(workstation_report(Path(relay_data),session_id))
    return {
        'schemaVersion':1,'generatedAt':datetime.now(timezone.utc).isoformat(),
        'records':records,
        'eventVersusPeriodic':{'status':'not_run','reason':'Requires independent timestamped dense benchmark sources.'},
        'learnerPilot':{'status':'not_run','reason':'Requires labeled participant sessions and independent observation.'},
    }
