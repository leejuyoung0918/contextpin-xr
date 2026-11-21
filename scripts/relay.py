#!/usr/bin/env python3
"""ContextPin XR meeting development commands. Run from any working directory."""
import argparse
import datetime
import json
import hashlib
import os
from pathlib import Path
import platform
import plistlib
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request


ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from backend.meeting.bundle_provider import configure_pc

PROJECT=ROOT/'ContextPinRelay'
APK=ROOT/'builds/ContextPinPhoneStand.apk'
ANDROID_PACKAGE='org.contextpin.relay.phonestand'
LIVE_PORT=8791
XR_SIM=Path('/Applications/MetaXRSimulator.app/Contents/Resources/MetaXRSimulator')

def simulator_env():
    # Standalone OpenXR needs the rendered Game view on this Mac.
    manifest=XR_SIM/'meta_openxr_simulator.json'
    config=XR_SIM/'config'/'sim_core_configuration.json'
    for item in (manifest, config, XR_SIM/'SIMULATOR.so'):
        if not item.is_file():raise SystemExit('Meta XR Simulator 설치 파일을 찾을 수 없습니다: '+str(item))
    return {'XR_RUNTIME_JSON':str(manifest), 'XR_SELECTED_RUNTIME_JSON':str(manifest),
            'META_XRSIM_CONFIG_JSON':str(config)}

def unity_project():
    # Unity's package importer on macOS can drop package assemblies in NFD/NFC
    # non-ASCII project paths. Keep source live in the repo and cache under an
    # ASCII project root. Unlike a symlink to the whole project, this root is real.
    if sys.platform!='darwin' or str(PROJECT).isascii():return PROJECT
    shell=Path('/private/tmp')/('ContextPinRelay-'+hashlib.sha256(str(PROJECT).encode()).hexdigest()[:10])
    shell.mkdir(exist_ok=True)
    for name in ('Assets','Packages','ProjectSettings'):
        link=shell/name
        if not link.exists():link.symlink_to(PROJECT/name,target_is_directory=True)
        elif link.resolve()!=(PROJECT/name).resolve():raise SystemExit('Unity runner path is occupied: '+str(link))
    return shell

def run(args, **kwargs):
    return subprocess.run([str(x) for x in args],check=True,**kwargs)

def unity():
    configured=os.environ.get('UNITY_EDITOR')
    if configured and Path(configured).is_file():return Path(configured)
    candidates=list(Path('/Applications/Unity/Hub/Editor').glob('6000.4.0f1/Unity.app/Contents/MacOS/Unity'))
    if candidates:return candidates[0]
    raise SystemExit('Unity 6000.4.0f1을 설치하거나 UNITY_EDITOR에 에디터 실행 파일 경로를 지정하세요.')

def editor(method=None,batch=False):
    args=[unity(),'-projectPath',unity_project()]
    env=os.environ.copy();env['RELAY_WORKSPACE']=str(ROOT)
    if method=='SimulatorPreview':env.update(simulator_env())
    if batch:
        (ROOT/'artifacts').mkdir(exist_ok=True)
        if method=='BuildQuest':args+=['-buildTarget','Android']
        args+=['-batchmode','-quit','-logFile',ROOT/('artifacts/unity-build.log' if method=='BuildQuest' else 'artifacts/unity-prepare.log')]
    if method:args+=['-executeMethod','ContextPin.Editor.RelaySetup.'+method]
    if batch:
        try:run(args,env=env)
        except subprocess.CalledProcessError:
            log='unity-build.log' if method=='BuildQuest' else 'unity-prepare.log'
            print(f'Unity 실행 실패. artifacts/{log}를 확인하세요.')
            raise
    else:subprocess.Popen([str(x) for x in args],cwd=ROOT,env=env)

def adb():
    path=shutil.which('adb')
    if path:return path
    binary=unity().parents[3]/'PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb'
    if binary.is_file():return str(binary)
    raise SystemExit('Unity Hub → Installs → Android Build Support의 SDK/NDK 모듈을 설치하세요.')

def device(serial=None):
    output=subprocess.check_output([adb(),'devices'],text=True)
    found=[line.split() for line in output.splitlines()[1:] if line.strip() and not line.startswith('*')]
    ready=[row[0] for row in found if len(row)>1 and row[1]=='device']
    if serial and serial not in ready:raise SystemExit('선택한 기기가 준비되지 않았습니다. 헤드셋에서 USB 디버깅을 승인하세요.')
    if not serial:
        if len(ready)!=1:raise SystemExit('Quest 한 대를 USB로 연결하고 개발자 모드·USB 디버깅을 승인하세요. 여러 기기는 --serial로 선택하세요.\n'+output)
        serial=ready[0]
    model=subprocess.check_output([adb(),'-s',serial,'shell','getprop','ro.product.model'],text=True).strip()
    if 'Quest' not in model:raise SystemExit('Quest가 아닌 기기에는 설치하지 않습니다: '+model)
    return [adb(),'-s',serial]

def meeting_python():
    local = ROOT / 'artifacts/meeting-venv/bin/python'
    return str(local) if local.is_file() else sys.executable


def meeting_env(load_settings=True):
    env = os.environ.copy()
    dotenv = ROOT / 'backend/.env'
    if load_settings and dotenv.is_file():
        for line in dotenv.read_text().splitlines():
            if not line.strip() or line.lstrip().startswith('#') or '=' not in line:
                continue
            key, value = line.split('=', 1)
            key = key.strip()
            if key.startswith(('EDGE_', 'MEETING_', 'OPENSCAD_')) and key not in (
                    'MEETING_TEAM_API_KEY', 'MEETING_TEAM_MODEL', 'MEETING_BUNDLE_API_KEY'):
                env.setdefault(key, value.strip().strip(chr(34) + chr(39)))
    executable = ROOT / 'artifacts/tools/OpenSCAD.app/Contents/MacOS/OpenSCAD'
    if executable.is_file():
        env.setdefault('OPENSCAD_EXECUTABLE', str(executable))
    return env


def assembly_environment(replay=False, candidate_ai='local'):
    env = meeting_env()
    try:
        configure_pc(env, candidate_ai)
    except ValueError as error:
        raise SystemExit(str(error)) from error
    port = int(env.get('EDGE_PORT', 8788 if replay else LIVE_PORT))
    default_config = ROOT / ('artifacts/cable-demo/assembly.json' if replay else 'backend/meeting/phone_stand/assembly.json')
    config = Path(env.get('OPENSCAD_ASSEMBLY_CONFIG', default_config)).expanduser()
    if not config.is_file():
        if env.get('OPENSCAD_ASSEMBLY_CONFIG'):
            raise SystemExit(f'OPENSCAD_ASSEMBLY_CONFIG에서 지정한 파일을 찾을 수 없습니다. {config}')
        if replay:
            raise SystemExit('먼저 python3 scripts/prepare_cable_demo.py --upper-corners backend/meeting/openscad_sample/cable_organizer.scad 를 실행하세요.')
        raise SystemExit(f'휴대폰 거치대 CAD 설정을 찾을 수 없습니다. {config}')
    env['AI_MODE'] = 'replay' if replay else 'live'
    env.setdefault('EDGE_DATA_DIR', str(ROOT / 'artifacts' / ('cable-demo/live' if replay else 'phone-stand-live')))
    env.setdefault('CAD_JOB_TIMEOUT_SECONDS', '70')
    if replay:
        env.setdefault('CABLE_DEMO_PREWARM', '1')
    if 'EDGE_ADVERTISED_URL' not in env and sys.platform == 'darwin':
        lan_ip = command_output(['ipconfig', 'getifaddr', 'en0'])
        if lan_ip:
            env['EDGE_ADVERTISED_URL'] = f'http://{lan_ip}:{port}'
    return env, config, port


def server(replay=False, candidate_ai='local'):
    env, config, port = assembly_environment(replay, candidate_ai)
    if not replay:
        print('수정안 생성 · 현장 PC 로컬 모델 · 외부 API 사용료 0원', flush=True)
    print(f'CAD 설정 · {config.name}', flush=True)
    print(f'PC 회의 · http://127.0.0.1:{port}/meeting/', flush=True)
    run([meeting_python(), '-m', 'backend.meeting', 'assembly', '--config', config, '--candidate-ai', env['MEETING_CANDIDATE_AI'],
         '--host', env.get('EDGE_BIND_HOST', '0.0.0.0'), '--port', port], cwd=ROOT, env=env)


def command_output(command, timeout=3):
    try:
        return subprocess.check_output([str(part) for part in command],stderr=subprocess.DEVNULL,text=True,timeout=timeout).strip()
    except (OSError,subprocess.SubprocessError):
        return None

def doctor():
    port=int(meeting_env().get('EDGE_PORT', LIVE_PORT))
    manifest=json.loads((PROJECT/'Packages/manifest.json').read_text())
    packages={name:version for name,version in manifest['dependencies'].items()
              if name.startswith(('com.meta.xr.', 'com.unity.xr.', 'com.unity.inputsystem'))}
    package_lock=json.loads((PROJECT/'Packages/packages-lock.json').read_text())['dependencies']
    unity_version=(PROJECT/'ProjectSettings/ProjectVersion.txt').read_text().splitlines()[0].split(': ',1)[-1]
    result={'checkedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),
            'project':str(PROJECT),'unityVersion':unity_version,'python':platform.python_version(),
            'dotnet':command_output(['dotnet','--version']),'commit':command_output(['git','-C',ROOT,'rev-parse','HEAD']),
            'packageManifest':packages,
            'packageLock':{name:package_lock[name]['version'] for name in packages if name in package_lock}}
    try:
        exe=unity()
        android=exe.parents[3]/'PlaybackEngines/AndroidPlayer'
        result['unityEditor']=str(exe)
        result['androidBuildTools']={component:(android/component).is_dir() for component in ('SDK','NDK','OpenJDK')}
    except SystemExit:
        result['unityEditor']=None
    try:
        with (XR_SIM.parents[1]/'Info.plist').open('rb') as file:
            result['simulatorVersion']=plistlib.load(file).get('CFBundleShortVersionString')
    except (OSError,ValueError,plistlib.InvalidFileException):
        result['simulatorVersion']=None
    camera_api=next(iter(PROJECT.glob('Library/PackageCache/com.meta.xr.mrutilitykit@*/Core/Scripts/PassthroughCameraAccess.cs')),None)
    api_source=camera_api.read_text(errors='replace') if camera_api else ''
    names=('IsSupported','Timestamp','Intrinsics','CurrentResolution','GetCameraPose','GetTexture')
    result['installedCameraApi']={name:name in api_source for name in names}
    result['realSensorStatus']='requires_on_device_capture'
    try:
        adb_exe=adb()
        output=command_output([adb_exe,'devices','-l'],timeout=5)
        result['adbDevices']={'status':'unavailable_in_current_environment'} if output is None else {
            'status':'queried','states':[line.split()[1] for line in output.splitlines()
                if len(line.split())>=2 and not line.startswith(('List of','*'))]}
    except SystemExit:
        result['adbDevices']={'status':'tool_missing'}
    try:
        loopback=urllib.request.build_opener(urllib.request.ProxyHandler({}))
        with loopback.open(f'http://127.0.0.1:{port}/healthz',timeout=2) as response:
            result['meeting']=json.load(response)
    except (OSError,ValueError,urllib.error.URLError):
        result['meeting']={'status':'unreachable','port':port}
    print(json.dumps(result,ensure_ascii=False,indent=2))

def smoke_meeting(simulator, assembly=False):
    artifacts = ROOT / 'artifacts'
    artifacts.mkdir(exist_ok=True)
    env = {**os.environ, 'RELAY_WORKSPACE': str(ROOT)}
    edge = env.setdefault('MEETING_EDGE_URL', 'http://127.0.0.1:8788')
    try:
        with urllib.request.urlopen(edge.rstrip('/') + '/healthz', timeout=3) as health:
            if health.status != 200:
                raise ValueError(f'HTTP {health.status}')
    except (OSError, ValueError) as error:
        raise SystemExit(f'회의 EDGE를 먼저 시작하세요. {edge} · {error}') from error
    check = 'assembly' if assembly else 'meeting'
    if env.get('MEETING_CANDIDATE_COMMIT_SMOKE') == '1':
        check = 'candidate-commit'
    elif env.get('MEETING_CANDIDATE_SET_SMOKE') == '1':
        check = 'candidate-set'
    elif env.get('MEETING_CAPTURE_ONLY_SMOKE') == '1':
        check = 'capture-only'
    elif env.get('MEETING_EXPLICIT_SMOKE') == '1':
        check = 'explicit'
    elif env.get('MEETING_LIVE_ALIGNMENT_SMOKE') == '1':
        check = 'live-alignment'
    elif env.get('MEETING_SAVED_DESIGN_SMOKE') == '1':
        check = 'saved-design'
    report = artifacts / f'{check}-{"simulator" if simulator else "smoke"}-report.json'
    log = artifacts / f'unity-{check}{"-simulator" if simulator else ""}.log'
    if assembly:
        env['MEETING_SMOKE_ASSEMBLY'] = '1'
    report.unlink(missing_ok=True)
    options = ['-force-metal'] if simulator else ['-batchmode']
    method = 'RunMeetingSimulator' if simulator else 'RunMeeting'
    if simulator:
        env.update(simulator_env())
        # Unity can start adb while holding the editor project lock. Start the daemon
        # outside Unity so the next Android build can open the project after the smoke.
        try:
            subprocess.run([adb(), 'start-server'], timeout=10, check=False)
        except (OSError, subprocess.TimeoutExpired, SystemExit):
            pass
    command = [unity(), '-projectPath', unity_project(), *options,
               '-executeMethod', f'ContextPin.Editor.RelaySmoke.{method}', '-logFile', log]
    try:
        if simulator:
            # The native simulator can keep an editor window alive after Unity
            # writes its report. Bound teardown for the editor this check owns.
            with subprocess.Popen([str(arg) for arg in command], env=env) as child:
                deadline = time.monotonic() + (650 if check in ('candidate-set', 'candidate-commit') else 220 if assembly else 160)
                try:
                    while child.poll() is None and time.monotonic() < deadline:
                        if report.is_file():
                            try:
                                if 'success' in json.loads(report.read_text()):
                                    try:
                                        child.wait(timeout=15)
                                    except subprocess.TimeoutExpired:
                                        pass
                                    break
                            except json.JSONDecodeError:
                                pass
                        time.sleep(.5)
                finally:
                    if child.poll() is None:
                        child.terminate()
                        try:
                            child.wait(timeout=8)
                        except subprocess.TimeoutExpired:
                            child.kill()
                            child.wait()
        else:
            run(command, env=env, timeout=650 if check in ('candidate-set', 'candidate-commit') else 180)
    except (subprocess.CalledProcessError, subprocess.TimeoutExpired) as error:
        detail = json.loads(report.read_text()).get('error') if report.is_file() else None
        raise SystemExit(detail or f'Unity 회의 검증을 완료하지 못했습니다. {log}') from error
    saved = json.loads(report.read_text()) if report.is_file() else {}
    if not saved.get('success'):
        raise SystemExit(saved.get('error') or f'회의 검증 결과를 받지 못했습니다. {log}')
    print(report)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command',choices=['doctor','open','prepare','desktop','simulator','build','deploy','replay-server','server','open-saved-review','test','smoke','smoke-simulator','smoke-meeting','smoke-meeting-simulator','smoke-assembly','smoke-assembly-simulator'])
    parser.add_argument('--serial',help='설치할 Quest의 ADB 기기번호')
    parser.add_argument('--candidate-ai', choices=('local','team'), help='현장 서버는 local만 지원합니다. team 요청은 시작 전에 차단합니다.')
    launch = parser.add_mutually_exclusive_group()
    launch.add_argument('--open-saved-design', action='store_true', help='설치 후 최근 저장한 회의의 발언으로 수정안 열기')
    launch.add_argument('--join-code', help='설치 후 실시간 회의 서버의 여섯 자리 코드로 참가')
    parser.add_argument('--session', help='open-saved-review에 사용할 저장된 실시간 회의 ID')
    args=parser.parse_args()
    if args.candidate_ai is not None and args.command != 'server':
        parser.error('--candidate-ai는 실시간 회의 서버(server)에서 사용하세요.')
    if args.join_code is not None and (len(args.join_code) != 6 or not args.join_code.isascii() or not args.join_code.isdigit()):
        parser.error('--join-code에는 여섯 자리 숫자가 필요합니다.')
    if args.command=='doctor':doctor()
    elif args.command=='open':editor()
    elif args.command=='prepare':editor('Configure',True)
    elif args.command=='desktop':editor('DesktopPreview')
    elif args.command=='simulator':
        if not Path('/Applications/MetaXRSimulator.app').exists():raise SystemExit('Meta XR Simulator를 먼저 설치하세요. README의 공식 링크를 참고하세요.')
        editor('SimulatorPreview')
    elif args.command=='build':editor('BuildQuest',True)
    elif args.command.startswith('smoke'):
        smoke_meeting(args.command.endswith('-simulator'), 'meeting' not in args.command)
    elif args.command=='deploy':
        command=device(args.serial)
        if not APK.exists():editor('BuildQuest',True)
        port=int(meeting_env().get('EDGE_PORT', LIVE_PORT))
        run(command+['reverse',f'tcp:{LIVE_PORT}',f'tcp:{port}'])
        run(command+['reverse','tcp:8788','tcp:8788'])
        run(command+['install','-r',APK])
        launch_args = ['shell', 'am', 'start', '-W', '-n', ANDROID_PACKAGE+'/com.unity3d.player.UnityPlayerActivity']
        if args.open_saved_design:
            launch_args.extend(['--ez', 'contextpin_open_saved_design', 'true'])
        elif args.join_code:
            launch_args.extend(['--es', 'contextpin_join_code', args.join_code])
        launch = run(command + launch_args,
                     stdout=subprocess.PIPE, text=True)
        print(launch.stdout, end='')
        if 'LaunchCheckRequires6dofDialogActivity' in launch.stdout:
            print('설치 완료. 헤드셋을 착용하고 기기의 위치 추적 안내를 확인하세요.')
        elif f'Activity: {ANDROID_PACKAGE}/' in launch.stdout:
            print('ContextPin XR Phone Stand 설치·실행 완료.')
        else:
            print('설치 완료. 헤드셋에 앱 실행을 요청했습니다.')
    elif args.command == 'open-saved-review':
        if not args.session:
            parser.error('open-saved-review에는 --session이 필요합니다.')
        env, config, _ = assembly_environment()
        env['OPENSCAD_ASSEMBLY_CONFIG'] = str(config.resolve())
        env['ACTIVE_OBJECT_PROFILE'] = 'object_2'
        env.pop('AI_GATEWAY_URL', None)
        run([meeting_python(), '-m', 'backend.meeting.operator_review', '--data', env['EDGE_DATA_DIR'],
             '--session', args.session], cwd=ROOT, env=env)
    elif args.command in ('server','replay-server'):
        server(replay=args.command=='replay-server', candidate_ai=args.candidate_ai or 'local')
    elif args.command=='test':
        for directory in ('backend', 'tests/meeting'):
            run([meeting_python(), '-m', 'unittest', 'discover', '-s', directory, '-v'],
                cwd=ROOT, env=meeting_env(load_settings=False))


if __name__=='__main__':
    try:main()
    except subprocess.CalledProcessError as exc:sys.exit(exc.returncode)
