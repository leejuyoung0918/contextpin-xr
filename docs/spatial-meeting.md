# 공간 설계 회의

회의 중 헤드셋에는 고정 조립체의 모델, 회의 조작, 짧은 요약이 기본으로 표시됩니다. PC는 마이크를 맡거나 공유해서 보는 후보를 확인하고, 회의가 끝나면 전사·요약·결정·CAD를 엽니다.

## 실행 환경

Unity 6000.4.0f1, Meta XR Core 209.0.0, OpenXR 1.16.1과 기존 glTFast 로더를 사용합니다. Meta Quest의 passthrough 표시와 앱이 카메라 프레임을 읽는 기능은 별도입니다. 회의 화면과 AI 문맥에 카메라 데이터 권한은 필요하지 않습니다. 헤드셋에서 녹음하는 경우 마이크 권한을 허용합니다. [Meta의 Unity passthrough 문서](https://developers.meta.com/horizon/documentation/unity/unity-passthrough/)

[OpenSCAD 설치와 실제 모델 등록](openscad-assembly.md)을 마친 뒤 저장소 루트에서 아래 명령을 실행합니다. 처음 실행하는 경우 README에 안내된 가상 환경을 준비합니다.

```sh
artifacts/meeting-venv/bin/python -m backend.meeting assembly
```

직접 라이브 음성을 사용하려면 PC에 `AI_MODE=live`, `AI_API_KEY`, `AI_TRANSCRIPTION_MODEL`, `AI_PLANNER_MODEL`을 설정합니다. 기본 실행은 **예시 진행**이며 발화 예시 또는 직접 입력을 사용할 수 있습니다. OpenSCAD의 모델 생성은 가상 재생에서도 실제로 실행됩니다.

| 장치 | 여는 방법 |
| --- | --- |
| PC | [회의 보조 화면](http://127.0.0.1:8788/meeting/) |
| Meta XR Simulator | `python3 scripts/relay.py simulator` → **3D 설계 회의** |
| Meta Quest | `python3 scripts/relay.py build` 및 `python3 scripts/relay.py deploy` → **3D 설계 회의** |

USB Quest 연결에는 개발자 모드와 USB 디버깅 허용이 필요합니다. `deploy`는 앱 설치와 8788 포트 전달을 수행합니다. PC 웹에서 마이크를 사용할 경우 localhost 또는 신뢰된 HTTPS에서 화면을 엽니다. PC는 헤드셋과 같은 회의에 참가할 수 있습니다.

## 진행

1. 조립체를 고정합니다. 헤드셋에서 원점, +X, +Y 기준점을 지정하고 반투명 원본을 보고 실물과 일치하는지 확인합니다. 패널은 조립체 옆에 놓아두며, **더 보기**에서 한 번 이동할 수 있습니다. 왼쪽 컨트롤러 X로 패널을 다시 불러옵니다. 고정 조립체나 기준판을 움직였으면 재정렬합니다.
2. **회의 시작**을 누릅니다. 지정한 기기 하나가 PCM 오디오를 연속 제공합니다. 첫 프레임 수신 이후에 녹음 상태가 표시됩니다. PC 마이크 사용 시 PC에서 권한을 부여하고 지정된 마이크를 시작합니다. 확정된 발화가 수집되는 동안 최근 전사를 필요할 때만 표시할 수 있습니다.
3. 수정안이 준비되었다는 조용한 표시가 나오면 비교를 엽니다. 한 번에 하나의 형상을 봅니다. PC에는 공유된 후보 번호가 나타납니다. **이 안 채택**으로 서버에 버전을 기록합니다. 이유는 PC에서 직접 쓰거나 기존 확정 발화에서 연결할 수 있습니다. 화면만 전환하면 미리보기로 남습니다.
4. **회의 종료**로 채택본을 고정합니다. 마지막 오디오를 닫고 확정 전사를 모은 뒤 파일을 저장합니다. 추적을 잃은 경우에도 종료 조작이 보입니다.
5. **저장됨**에서 PC의 전체 결과를 열거나 헤드셋에서 **결과 ZIP 받기**를 누릅니다. PC 결과는 같은 회의에 참가한 브라우저에서 인증된 상태로 열립니다. 헤드셋은 기기의 앱 저장소 `MeetingExports/`에 ZIP을 저장합니다.

헤드셋 패널 기본값은 폭 0.32m, 거리 1.15m, 글자 배율 1입니다. 기존 `Assets/Resources/relay-local.json`에서 `meetingPanelWidthMeters`, `meetingPanelDistanceMeters`, `meetingPanelTextScale`을 조정할 수 있습니다.

## 결과와 재현

PC의 기본 저장 경로는 `artifacts/assembly/results/<회의 ID>/<저장 디렉터리>/`입니다. `EDGE_DATA_DIR`를 사용하면 해당 폴더의 `results/`로 옮겨집니다.

- `manifest.json`에 확정 채택 버전과 저장 상태, 파일 해시, 전사 누락을 기록합니다. 늦은 전사가 도착하거나 정정하면 같은 CAD를 참조하는 다음 개정본을 저장합니다.
- `transcript.txt`, `transcript.jsonl`, `summary.md`, `decisions.json`에서 전체 전사, 발화 ID, 수정 해석과 채택 이유를 확인합니다.
- `cad/final.scad` 또는 매니페스트의 `final_scad` 경로에는 적용값이 반영된 실행 가능한 코드가 있습니다. `cad/parameters.json`은 전체 값을, `cad/parameter_provenance.json`은 단위와 값의 출처를 담습니다.
- `cad/final.stl`과 `cad/preview.glb`는 회의 중 실제 채택한 버전에서 복사합니다. `cad/build.json`에는 OpenSCAD 버전, 입출력 해시와 실행 설정이 있습니다.
- `original/`에 원본과 의존 소스를 보관합니다. `candidates/`에는 미리보기로 열었거나 채택한 후보의 실제 파일을 보관합니다.

SCAD의 편집 변수는 설정 파일의 기본값과 같은 값을 쓰는 최상위 리터럴 선언으로 한 번 정의합니다. 상대 경로의 `include`, `use`, `import()`에 필요한 파일을 `source_files`에 추가합니다. 결과 ZIP에서 `README.md`의 명령으로 해당 구조를 유지한 채 렌더링할 수 있습니다. 이전 앱에서 생성한 모델은 기존 뷰어와 모델별 내보내기에서 열 수 있습니다. 실행 가능한 SCAD까지 포함하는 회의 결과는 새 회의에서 생성하세요. [OpenSCAD include 및 use 문서](https://files.openscad.org/documentation/manual/Include_Statement.html)

## 확인 절차

```sh
artifacts/meeting-venv/bin/python -m unittest discover -s tests/meeting -p test_assembly.py -v
python3 -m unittest backend.meeting.test_openscad_geometry -v
python3 scripts/relay.py smoke-assembly-simulator
```

시뮬레이터에서는 예시 조립체와 합성 기준점을 사용합니다. 실기에서는 실제 조립체의 정합, 얼굴과 손의 작업 영역 가림, 한글 가독성, 실제 마이크와 라이브 전사, 외형 간섭을 각각 확인해야 합니다.
