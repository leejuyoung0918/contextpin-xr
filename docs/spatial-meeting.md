# 공간 설계 회의

회의 중 헤드셋에는 고정 조립체의 모델, 회의 조작, 짧은 요약이 기본으로 표시됩니다. PC는 마이크를 맡거나 공유해서 보는 후보를 확인하고, 회의가 끝나면 전사·요약·결정·CAD를 엽니다.

## 실행 환경

Unity 6000.4.0f1, Meta XR Core 209.0.0, OpenXR 1.16.1과 기존 glTFast 로더를 사용합니다. Meta Quest의 passthrough 표시와 앱이 카메라 프레임을 읽는 기능은 별도입니다. 회의 화면과 AI 문맥에 카메라 데이터 권한은 필요하지 않습니다. 헤드셋에서 녹음하는 경우 마이크 권한을 허용합니다. [Meta의 Unity passthrough 문서](https://developers.meta.com/horizon/documentation/unity/unity-passthrough/)

[OpenSCAD 설치와 실제 모델 등록](openscad-assembly.md)을 마친 뒤 저장소 루트에서 아래 명령을 실행합니다. 처음 실행하는 경우 README에 안내된 가상 환경을 준비합니다.

```sh
MEETING_STT_BACKEND=local MEETING_PLAN_BACKEND=registered python3 scripts/relay.py server
```

이 명령은 PC의 한국어 faster-whisper 모델과 케이블 정리대 등록 치수 플래너를 사용하는 실시간 서버를 엽니다. PC에 필요한 로컬 모델 준비 및 외부 공급자 선택 방법은 README를 참고합니다. 대본 예시도 사용할 때는 별도 터미널에서 `python3 scripts/relay.py replay-server`를 실행합니다. [서버 설정](../README.md)

| 장치 | 여는 방법 |
| --- | --- |
| PC | [실시간 회의](http://127.0.0.1:8789/meeting/) · [대본 시연](http://127.0.0.1:8788/meeting/) |
| Meta XR Simulator | `python3 scripts/relay.py simulator` |
| Meta Quest | `python3 scripts/relay.py build` 후 `python3 scripts/relay.py deploy` |

USB Quest 연결에는 개발자 모드와 USB 디버깅 허용이 필요합니다. `deploy`가 앱을 설치·실행하고 8789와 8788 포트를 전달합니다. PC 웹에서 마이크를 사용할 때는 localhost 또는 신뢰된 HTTPS에서 회의를 엽니다.

## 진행

1. 헤드셋에서 **회의 시작**을 누르고 마이크 권한을 허용합니다. 이미 PC에서 연 회의가 있다면 첫 화면에서 참가 QR을 읽거나 여섯 자리 코드를 입력합니다.
2. 새 실시간 회의에서는 빈 A4를 책상 위에 놓고 몸에서 가까운 왼쪽부터 앞왼쪽, 앞오른쪽, 뒤오른쪽, 뒤왼쪽 순서로 오른손 표시점을 대고 **A**를 누릅니다. 실물은 종이 중앙에 두고, 오른쪽 앞에 나타난 가상 Latest의 바닥과 방향을 확인한 뒤 **배치 확인**을 누릅니다. 새로 채택한 버전도 같은 오른쪽 앞 자리에 놓입니다. 종이나 책상을 움직였으면 다시 등록합니다. 재등록한 위치에서도 최신 버전을 이어서 봅니다. 고정 카드를 쓰는 현장은 다른 정렬 방법에서 QR을 선택할 수 있습니다. [정렬 카드 인쇄와 배치](cable-demo.md#qr-설치)
3. 패널은 언제든지 옮길 수 있습니다. 위쪽 **잡고 옮기기**에 컨트롤러 광선을 대고 트리거를 누른 채 패널을 옮긴 후 놓습니다. 데스크톱에서는 마우스 왼쪽 버튼으로 끕니다. 공간 회의에서는 왼쪽 컨트롤러 X로 패널을 다시 부를 수 있습니다.
4. **수정 요청 시작**을 누르고 변경할 내용을 말한 뒤 **발언 완료**를 누릅니다. 확정 전사가 뜨면 오른쪽 앞 가상 Latest의 해당 부위를 가리키고 A를 누릅니다. 패널의 **수정 요청 저장 완료**를 확인합니다.
5. 상태 버튼의 **종료**를 누르면 오디오를 닫고 전사와 채택본을 저장합니다. **PC에서 회의록 받기**를 눌러 받은 10자리 코드를 같은 서버의 PC 시작 화면 **회의록 받기**에 입력합니다. 코드의 유효 시간은 10분입니다. PC 결과 화면에서도 **전체 회의록 TXT**를 받을 수 있습니다. CAD와 이전 버전이 포함된 ZIP은 PC 결과 화면이나 Quest **메뉴 → 결과 ZIP 저장**에서 받습니다. Quest 저장 폴더는 앱 저장소의 `MeetingExports/`입니다.

헤드셋 패널 기본값은 폭 0.32m, 거리 1.15m, 글자 배율 1입니다. 기존 `Assets/Resources/relay-local.json`에서 `meetingPanelWidthMeters`, `meetingPanelDistanceMeters`, `meetingPanelTextScale`을 조정할 수 있습니다.

## 결과와 재현

회의 결과는 PC의 `EDGE_DATA_DIR/results/<회의 ID>/<저장 디렉터리>/`에 저장됩니다. `scripts/relay.py server`의 기본 실시간 데이터 폴더는 `artifacts/cable-live/`입니다.

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
