# ContextPin XR · 3D 설계 회의

3D 프린팅 시제품을 함께 검토하는 Unity·Quest 앱입니다. 발언을 당시의 모델과 선택한 부위에 연결합니다. “전체 높이는 그대로, 좀 더 곡선적인 느낌”처럼 여러 뜻이 가능한 요청은 실제로 형상이 다른 모델로 비교할 수 있습니다. 참여자는 자신의 해석과 디자인 선호를 따로 남기고, 진행자가 선택한 모델과 이유를 회의록에서 다시 확인합니다.

브라우저와 Unity는 현장 노트북의 **EDGE** 서버에서 동일한 GLB를 읽습니다. 수동 편집과 예시 플래너는 인터넷 없이 실행됩니다. 형상을 생성할 때 쓴 같은 매개변수·원본에서 미리보기와 STL을 함께 저장합니다.

## 해커톤 데모 · OpenSCAD 조립체

고정한 조립체에서 물체 2의 결합부를 유지한 채 외형을 바꿉니다. 실제 OpenSCAD가 생성한 STL을 Unity에서 원본과 같은 장착 기준으로 전환하고, 선택한 안과 이유를 원본 SCAD·매개변수와 함께 저장합니다.

```sh
artifacts/meeting-venv/bin/python -m backend.meeting assembly
```

[PC 화면 열기](http://127.0.0.1:8788/meeting/) · [공간 회의·연속 녹음·종료 결과](docs/spatial-meeting.md) · [실제 SCAD 등록](docs/openscad-assembly.md)

헤드셋에서는 모델, 회의 조작, 작은 요약이 기본으로 보입니다. 회의를 종료하면 그 시점에 채택돼 있던 버전의 실행 가능한 SCAD와 정확한 STL, 확정 전사·요약·결정을 한 묶음으로 저장합니다.

OpenSCAD와 아래 가상 환경 설치가 필요합니다. 기본 설정에는 실제 대상 대신 분리된 예시 조립체가 들어 있습니다. 다음 절에는 이전 범용 회의 모드의 실행 방법이 있습니다.

## 기존 회의 모드 · 장비 없이 열기

Python 3.11 이상에서 저장소 루트에서 실행하세요.

```sh
python3 -m venv artifacts/meeting-venv
artifacts/meeting-venv/bin/python -m pip install -r backend/meeting/requirements.txt
artifacts/meeting-venv/bin/python -m backend.meeting
```

[브라우저에서 회의 열기](http://127.0.0.1:8788/meeting/) · [EDGE 상태 확인](http://127.0.0.1:8788/healthz)

1. 이름을 입력하고 **회의 시작**을 누릅니다. 두 번째 참가자는 다른 브라우저 탭에서 진행자가 받은 6자리 코드로 참가합니다.
2. 모델을 끌어 돌리고 부위를 선택합니다. **치수 직접 수정**에서 폭을 100mm에서 112mm로 바꿔 검토 모델을 만든 뒤 STL을 내려받습니다.
3. **예시 발화**를 실행하면 높이를 유지하면서 모서리 반경, 몸체 윤곽, 윗부분의 경사를 바꾼 세 모델이 생성됩니다. 발화와 계획은 예시 데이터이며 화면의 형상과 STL은 작업 프로세스가 새로 생성합니다.
4. 각 참가자가 **내 해석**과 **디자인 선호**를 따로 남깁니다. 진행자는 모델을 열고 이유를 입력해 **이 모델로 결정**합니다. 결정 당시의 모델, 발화, 이전과 이후 치수, 이유, 확인자가 회의록에 남습니다.
5. 회의록에서 이전 모델을 열거나 해당 버전을 내보냅니다. ZIP에는 GLB, STL, 매개변수, 어댑터 원본, 프로필, 실제 형상 검사, 결정 기록과 파일 해시가 포함됩니다.

**직접 입력 예시** · ‘폭을 114mm로’를 입력하면 수치가 명확한 후보 하나가 생성됩니다. ‘이 부분만 둥글게’라고 입력할 때는 변경할 부위를 먼저 선택하세요. 지정된 부위가 없으면 앱이 한 번 질문합니다.

## Unity와 Quest 연결

Unity Hub에서 **Unity 6000.4.0f1**을 설치합니다. 앞의 명령으로 EDGE를 실행한 상태에서 다음 명령을 사용하세요.

```sh
python3 scripts/relay.py desktop        # Unity Play Mode
python3 scripts/relay.py simulator      # Meta XR Simulator 설치가 필요
```

Unity 홈에서 **3D 설계 회의**를 엽니다. 브라우저에서 만든 코드로 참가하면 같은 모델과 최신 회의 결정을 볼 수 있습니다. 데스크톱은 마우스로 클릭합니다. Meta XR Simulator에서 마우스를 컨트롤러로 쓰려면 **Right input → Controller**, **Point & click**, **Compatibility mode**를 켜고 **Action input → Right - Controller**를 선택합니다.

Quest용 빌드는 Unity Android Build Support, SDK·NDK 및 OpenJDK를 사용합니다. USB 디버깅 승인 후 설치합니다.

```sh
python3 scripts/relay.py build
python3 scripts/relay.py deploy
adb reverse tcp:8788 tcp:8788
```

Quest의 기본 EDGE 주소는 `http://127.0.0.1:8788`입니다. 무선에서는 EDGE를 접속 가능한 인터페이스에 바인딩하고 Unity 회의 설정에 노트북 주소를 입력합니다. 브라우저 마이크를 다른 장치에서 쓰려면 해당 장치가 신뢰하는 HTTPS/WSS 인증서가 필요합니다. Unity는 로컬 공간에 실측 크기로 표시하고, 브라우저 3D 화면은 보기 배율을 조절합니다. [연결·장치 기능·검증 절차](docs/meeting-runbook.md)

## 라이브 AI

같은 컴퓨터에서 실행할 때는 `AI_MODE=live`, `AI_API_KEY`, `AI_TRANSCRIPTION_MODEL`, `AI_PLANNER_MODEL`을 EDGE 프로세스에 설정합니다. OpenAI 호환 서버를 쓰려면 `AI_BASE_URL`을 지정합니다. 원격 Gateway에서는 키와 모델을 Gateway에만 넣고, EDGE에는 `AI_GATEWAY_URL`과 `AI_GATEWAY_TOKEN`을 설정합니다. 실제 공급자가 실패해도 수동 치수 편집과 회의록 조회는 사용할 수 있습니다. [실행 모드와 환경 변수](docs/meeting-runbook.md)

## 다른 모델로 바꾸기

새 물체의 부위·치수·편집 범위는 **ObjectProfile**에, 형상 생성과 실제 치수 측정은 **ModelAdapter**에 등록합니다. 예제의 조형 블록은 파라메트릭 메시로 생성합니다. 제약은 생성된 형상의 실제 치수로 검사합니다. [프로필·어댑터 등록과 단위 검증](docs/meeting-model-adapters.md)

## 코드와 검증

| 위치 | 역할 |
| --- | --- |
| [backend/meeting/](backend/meeting/) | EDGE, SQLite 회의 상태와 이벤트, AI Gateway, 파라메트릭 모델 작업 프로세스 |
| [브라우저 검토 화면](backend/meeting/web/) | WebGL2 모델 선택·비교, PCM 마이크, 참가·회의록·내보내기 |
| [Unity 프로젝트](ContextPinRelay/) | GLB 런타임 로딩, 컨트롤러·마우스 부위 선택, Quest 마이크·카메라 연결 |
| [검증 절차](docs/meeting-runbook.md) | 데스크톱, 두 참가자, 실제 크기, 공급자·실물 확인 범위 |

기존 [ContextPin Trace 회로 실습](docs/contextpin-trace.md)은 Unity 홈과 8787 relay에서 계속 열 수 있으며, 개발 당시 버전은 [보관 브랜치](https://github.com/leejuyoung0918/contextpin-xr/tree/codex/contextpin-trace-archive)에 있습니다.
