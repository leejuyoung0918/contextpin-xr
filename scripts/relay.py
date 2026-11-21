#!/usr/bin/env python3
"""ContextPin Trace development commands. Run from any working directory."""
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

from transfer_report import collect_report

ROOT=Path(__file__).resolve().parents[1]
PROJECT=ROOT/'ContextPinRelay'
APK=ROOT/'builds/ContextPinTrace.apk'
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

def server_env():
    # Parse a small dotenv file as data; do not execute shell commands from it.
    allowed={'AI_BASE_URL','AI_API_KEY','AI_VISION_MODEL','AI_TRANSCRIPTION_MODEL','RELAY_PORT','RELAY_DATA_DIR'}
    env=os.environ.copy()
    file=ROOT/'backend/.env'
    if file.exists():
        for line in file.read_text().splitlines():
            if not line.strip() or line.lstrip().startswith('#') or '=' not in line:continue
            key,value=line.split('=',1);key=key.strip()
            if key in allowed:env[key]=value.strip().strip('\"\'')
    return env

def relay_port(env):
    try:
        port = int(env.get('RELAY_PORT', '8787'))
        if 1 <= port <= 65535:
            return port
    except ValueError:
        pass
    raise SystemExit('RELAY_PORT는 1–65535 사이의 포트 번호여야 합니다.')

def server():
    env=server_env()
    print(f'교수 PC · http://127.0.0.1:{relay_port(env)}/teacher',flush=True)
    run([sys.executable,ROOT/'backend/server.py'],env=env)

def command_output(command, timeout=3):
    try:
        return subprocess.check_output([str(part) for part in command],stderr=subprocess.DEVNULL,text=True,timeout=timeout).strip()
    except (OSError,subprocess.SubprocessError):
        return None

def doctor():
    env=server_env()
    port=relay_port(env)
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
    candidates=('AI_TRANSCRIPTION_MODEL','AI_VISION_MODEL')
    auth=bool(env.get('AI_API_KEY')) or env.get('AI_BASE_URL','').startswith(('http://127.0.0.1:','http://localhost:'))
    result['configuredLocally']={candidate:bool(auth and env.get(candidate)) for candidate in candidates}
    try:
        loopback=urllib.request.build_opener(urllib.request.ProxyHandler({}))
        with loopback.open(f'http://127.0.0.1:{port}/health/capabilities',timeout=2) as response:
            health=json.load(response)
        if health.get('service')!='contextpin-relay':
            raise ValueError('Different local service')
        result['relay']={'status':'reachable','teacherUrl':f'http://127.0.0.1:{port}/teacher',
                         **{key:health.get(key) for key in ('speechProvider','visionProvider','markersAvailable','providerRequests')}}
    except (OSError,ValueError,urllib.error.URLError):
        result['relay']={'status':'unreachable','port':port}
    print(json.dumps(result,ensure_ascii=False,indent=2))

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command',choices=['doctor','open','prepare','desktop','simulator','build','deploy','export','server','test','smoke','smoke-simulator','report'])
    parser.add_argument('--serial',help='deploy/export 대상 Quest ADB 기기번호')
    parser.add_argument('--archive',type=Path,help='report용 기기 export 폴더, Trace 폴더 또는 session.json')
    parser.add_argument('--relay-data',type=Path,help='report용 PC relay 저장 폴더 또는 transfer.sqlite3')
    parser.add_argument('--session',help='report에 포함할 단일 세션 ID')
    parser.add_argument('--output',type=Path,help='report 출력 JSON 경로. 생략하면 stdout')
    args=parser.parse_args()
    if args.command=='doctor':doctor()
    elif args.command=='open':editor()
    elif args.command=='prepare':editor('Configure',True)
    elif args.command=='desktop':editor('DesktopPreview')
    elif args.command=='simulator':
        if not Path('/Applications/MetaXRSimulator.app').exists():raise SystemExit('Meta XR Simulator를 먼저 설치하세요. README의 공식 링크를 참고하세요.')
        editor('SimulatorPreview')
    elif args.command=='build':editor('BuildQuest',True)
    elif args.command=='smoke':
        artifacts=ROOT/'artifacts';artifacts.mkdir(exist_ok=True)
        env={**os.environ,'RELAY_WORKSPACE':str(ROOT)}
        run([unity(),'-projectPath',unity_project(),'-batchmode',
             '-executeMethod','ContextPin.Editor.RelaySmoke.Run','-logFile',artifacts/'unity-smoke.log'],env=env,timeout=600)
    elif args.command=='smoke-simulator':
        artifacts=ROOT/'artifacts';artifacts.mkdir(exist_ok=True)
        log=artifacts/'unity-simulator-smoke.log'
        report=artifacts/'simulator-smoke-report.json'
        # OpenXR polls READY and starts stereo in Application.onBeforeRender. Open a
        # real Game view, as the macOS batch editor can omit that callback entirely.
        env={**os.environ,**simulator_env(),'RELAY_WORKSPACE':str(ROOT)}
        started=datetime.datetime.now(datetime.timezone.utc).isoformat()
        # A native runtime crash before Play starts must not leave an earlier successful result.
        def pending(message):
            report.write_text(json.dumps({'success':False,'startedUtc':started,'error':message,'log':log.name},indent=2))
        pending('Waiting for a simulator OpenXR session')
        args=[unity(),'-projectPath',unity_project(),'-force-metal',
              '-executeMethod','ContextPin.Editor.RelaySmoke.RunSimulator','-logFile',log]
        deadline=time.monotonic()+180
        with subprocess.Popen(args,env=env) as child:
            try:
                while child.poll() is None and time.monotonic()<deadline:
                    try:
                        finished='runningDisplays' in json.loads(report.read_text())
                    except json.JSONDecodeError:
                        finished=False
                    if finished:
                        # The report is written before leaving Play mode. Allow XR
                        # teardown, then close only the editor owned by this check.
                        try:child.wait(timeout=20)
                        except subprocess.TimeoutExpired:pass
                        break
                    time.sleep(.5)
            finally:
                if child.poll() is None:
                    child.terminate()
                    try:child.wait(timeout=8)
                    except subprocess.TimeoutExpired:
                        child.kill()
                        child.wait()
            saved=json.loads(report.read_text())
            if 'runningDisplays' not in saved:
                pending('Editor stopped before XR verification completed. See '+log.name)
                raise SystemExit('시뮬레이터 검사를 완료하지 못했습니다. '+str(log))
            if not saved.get('success'):raise SystemExit(saved.get('error') or str(log))
        print('시뮬레이터 XR 시연 확인: '+str(report))
    elif args.command=='deploy':
        command=device(args.serial)
        if not APK.exists():editor('BuildQuest',True)
        run(command+['reverse','tcp:8787',f'tcp:{relay_port(server_env())}'])
        run(command+['install','-r',APK])
        run(command+['shell','am','start','-n','org.contextpin.relay/com.unity3d.player.UnityPlayerActivity'])
        print('설치·실행 완료. 실제 AI는 별도 터미널에서 server 명령을 실행하세요.')
    elif args.command=='export':
        destination=ROOT/'artifacts/device-data'/datetime.datetime.now().strftime('%Y%m%d-%H%M%S');destination.mkdir(parents=True)
        run(device(args.serial)+['pull','/sdcard/Android/data/org.contextpin.relay/files/Trace',destination]);print(destination)
    elif args.command=='server':server()
    elif args.command=='report':
        if args.archive is None and args.relay_data is None:
            parser.error('report에는 --archive 또는 --relay-data가 필요합니다.')
        try:
            result=collect_report(args.archive,args.relay_data,args.session)
        except (OSError, ValueError) as error:
            parser.error(str(error))
        body=json.dumps(result,ensure_ascii=False,indent=2)+'\n'
        if args.output:
            args.output.parent.mkdir(parents=True,exist_ok=True)
            args.output.write_text(body,encoding='utf-8')
            print(args.output.resolve())
        else:
            print(body,end='')
    elif args.command=='test':
        run([sys.executable,'-m','unittest','discover','-s',ROOT/'backend','-p','test_*.py','-v'])
        env=os.environ.copy();env['DOTNET_CLI_HOME']=str(ROOT/'artifacts/dotnet');env['DOTNET_CLI_TELEMETRY_OPTOUT']='1'
        run(['dotnet','run','--project',ROOT/'tests/CoreChecks/CoreChecks.csproj'],env=env)

if __name__=='__main__':
    try:main()
    except subprocess.CalledProcessError as exc:sys.exit(exc.returncode)
