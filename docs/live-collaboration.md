# 같은 공간의 설계 회의

기존 케이블 정리대 프로필, OpenSCAD 생성기, GLB 표시, SQLite 회의 저장과 결과 ZIP을 사용합니다. `live` 회의는 실제 전사와 직접 입력을 기록하며, `replay` 회의는 대본과 예시 후보의 출처를 함께 표시합니다.

## PC 실행

```sh
artifacts/meeting-venv/bin/python -m pip install -r backend/meeting/requirements.txt
python3 scripts/prepare_cable_demo.py /path/to/cable_organizer.scad
```

같은 Wi-Fi의 Quest가 접근할 수 있는 PC 주소를 `EDGE_ADVERTISED_URL`에 설정합니다. 아래 주소는 예시이며 실제 PC 주소로 바꿉니다. 방화벽에서 같은 네트워크의 기기가 해당 포트로 접속할 수 있어야 합니다.

```sh
MEETING_STT_BACKEND=local MEETING_PLAN_BACKEND=registered \
EDGE_ADVERTISED_URL=http://192.168.0.20:8789 \
python3 scripts/relay.py server
```

위 구성에서 PC의 faster-whisper가 한국어 음성을 전사합니다. PC 가상 환경에 `faster-whisper`를 설치하고 로컬 모델 디렉터리를 `MEETING_WHISPER_DIR`로 지정합니다. 기본 위치는 `artifacts/tools/faster-whisper-base`입니다. 등록 치수 플래너는 확정 발화 속 케이블 정리대의 변경 요청을 해석해 허용 범위 안의 값을 제안하고 OpenSCAD로 후보를 생성합니다. 라이브 서버의 `/healthz`에서 `speech_backend=local`과 `planner_backend=registered`를 확인할 수 있습니다.

외부 공급자를 선택하는 PC는 `backend/.env` 또는 프로세스 환경에 공급자 설정 `AI_API_KEY`, `AI_TRANSCRIPTION_MODEL`, `AI_PLANNER_MODEL`과 필요한 경우 `AI_BASE_URL`을 지정하고 두 백엔드 선택을 `provider`로 설정합니다. PC에 마이크나 전사 모델이 없으면 웹의 **직접 입력으로 시작**을 사용할 수 있습니다. [실행 환경 준비](../README.md)

예시 시연은 별도 데이터 경로로 실행합니다.

```sh
AI_MODE=replay CABLE_DEMO_PREWARM=1 \
EDGE_DATA_DIR="$PWD/artifacts/cable-replay" \
OPENSCAD_EXECUTABLE="$PWD/artifacts/tools/OpenSCAD.app/Contents/MacOS/OpenSCAD" \
artifacts/meeting-venv/bin/python -m backend.meeting assembly \
  --config artifacts/cable-demo/assembly.json --host 0.0.0.0 --port 8788
```

예시의 발화·해석은 대본에서 오며 형상은 OpenSCAD로 생성됩니다. Live AI 제안도 같은 전체 파라미터·코드의 형상이 있으면 캐시를 사용할 수 있습니다. 전사 출처, 제안 출처, 형상 생성 출처는 각각 기록됩니다.

## 입력부터 채택까지

1. Quest의 **회의 시작**으로 마이크와 연속 회의 전사를 시작합니다. PC에서 만든 회의는 종전대로 **PC 마이크 준비 → 회의 시작** 또는 직접 입력으로 시작할 수 있습니다.
2. A4의 앞왼쪽, 앞오른쪽, 뒤오른쪽, 뒤왼쪽을 차례로 오른손 표시점으로 짚고 각 점에서 **A**를 누릅니다. 실물은 중앙 십자에 놓습니다. 오른쪽 앞 가상 Latest의 바닥과 방향을 확인한 뒤 **배치 확인**을 누릅니다.
3. 새 Quest 회의에서는 **수정 요청 시작 → 발언 → 발언 완료**를 사용합니다. 패널에 확정 전사가 뜨면 오른쪽 앞 가상 Latest를 가리키고 **A**를 누릅니다. 서버는 원음과 전사, 해당 버전의 표면 좌표와 선택 당시의 이미지를 한 건으로 저장합니다. 패널의 **수정 요청 저장 완료**로 확인합니다.
4. 기존 비교·채택 회의에서는 발언과 선택을 제출하고 준비된 후보를 비교할 수 있습니다. 채택한 CAD는 같은 오른쪽 앞 자리에서 교체됩니다.
5. 슬라이더의 값·단위·범위를 확인하며 조정합니다. 서버의 정확한 형상이 준비되어 화면에 표시된 뒤 **표시된 조정안 채택**을 누릅니다. 생성 중에도 마지막 표시 형상은 유지되며 채택 버튼은 비활성화됩니다. 취소하면 마지막 채택 모델로 돌아갑니다.
6. 선택 이유는 직접 입력하거나 실제 발언을 연결합니다. 이유를 입력하지 않으면 미기록으로 저장됩니다. 후보에 대한 선호 입력과 채택은 별도의 동작입니다.
7. 채택한 V2를 대상으로 후속 요청을 합니다. 다섯 홈과 양끝 여백 9mm를 유지한 길이 57.3mm는 홈 피치 9.825mm로 실제 재생성됩니다. 추가 요청도 현재 채택본에서 이어집니다.

## 두 번째 참가자

진행자의 **Quest 연결**에서 표시한 참가 QR 또는 코드를 이용합니다. QR의 주소는 `EDGE_ADVERTISED_URL`에서 나오며 참가 토큰이나 API 키를 포함하지 않습니다. Quest 참가 화면의 서버 주소도 같은 PC 주소를 사용합니다.

같은 실물을 보는 각 Quest는 고정된 제품의 같은 지점을 기준으로 위치와 방향을 맞추고 반투명 V1을 확인합니다. 추적을 잃거나 시제품을 옮기면 다시 배치합니다. 고정 카드로 정렬할 때는 `assembly.json`의 `qr_alignment.mounting`에 카드와 CAD 원점 사이의 위치·회전을 설정합니다. [선택형 정렬 카드](../backend/meeting/web/assembly-qr-card.svg)

서버가 같은 후보·공유 선택·슬라이더 초안·채택 상태를 모든 참가자에게 보냅니다. 한 참가자가 슬라이더를 잡으면 다른 참가자는 같은 값을 보며, 완료·취소·연결 종료·편집권 만료 후 다음 참가자가 조정할 수 있습니다. 늦게 참가하거나 재연결한 화면은 최신 snapshot을 받습니다.

## 결과와 후속 작업

Quest에서 상태 버튼의 **종료 → PC에서 회의록 받기**를 눌러 10자리 코드를 받습니다. 같은 서버의 PC 웹 시작 화면에서 **회의록 받기 → 전체 회의록 다운로드**로 전체 전사와 설계 결정을 TXT로 받습니다. 코드는 10분간 유효합니다. 이미 참가한 PC에서는 결과 화면의 **전체 회의록 TXT**를 사용할 수 있습니다. 실시간 회의와 대본 시연의 기본 서버는 각각 8789와 8788 포트를 사용합니다.

전체 CAD와 이력을 받으려면 PC 결과 화면에서 ZIP을 내려받거나 Quest의 **메뉴 → 결과 ZIP 저장**을 누릅니다. ZIP에는 최종 SCAD, STL, 전체 파라미터·manifest, 이전 채택본·후보, 확정 전사와 결정 근거가 들어 있습니다. 최종 SCAD에는 적용값이 들어 있어 별도 `-D` 없이 재생성할 수 있습니다.

**이 결과로 다음 작업**은 저장된 최종 채택 형상을 새 세션의 입력으로 불러옵니다. 이전 결정과 부모 버전 관계가 보존되며 새 요청을 이어갈 수 있습니다.

결과 화면의 **다음 작업자가 이해한 내용 기록**에는 실제 참가자가 이해한 수정 대상·방법·치수·다음 행동과 추가 질문 횟수를 입력합니다. 자동으로 참가자의 답이나 일치율을 채우지 않습니다. 이 양식으로 수집한 응답은 결과 기록에 포함됩니다.

### 현장 PC에서 저장한 발화로 다시 검토하기

이전 회의의 기기 참가 정보가 사라졌으면 현장 PC의 보관함 운영자가 저장된 발화로 새 검토 회의를 열 수 있습니다. 먼저 그 PC의 실시간 서버를 정상 종료하세요. 사용 중인 녹음과 생성 작업을 마친 뒤 서버 터미널에서 Ctrl+C를 누릅니다.

```sh
python3 scripts/relay.py open-saved-review --session meeting_원래회의ID
python3 scripts/relay.py server
```

첫 명령은 기존 실시간 서버와 같은 CAD 설정·저장 폴더를 읽고 새 회의 ID, 여섯 자리 참가 코드, 생성 상태를 출력합니다. 서버가 보관함을 사용 중이면 종료 안내를 표시합니다. `reviewable`이면 서버를 다시 연 뒤 Quest의 **코드로 참가**에서 새 코드를 입력해 후보를 비교하고 채택할 수 있습니다. `needs_input`이면 새 회의에서 확인 질문을 엽니다.

새 회의는 현장 PC 운영자가 열고 원래 회의의 확정 발화와 발화자를 근거로 참조합니다. 후속 검토는 직접 입력으로 시작하며 열려 있는 검토에 다시 명령을 실행하면 참가 코드를 확인할 수 있습니다.

## 검증 환경의 구분

| 확인 항목 | 필요한 환경 |
| --- | --- |
| 캐시에 없는 치수 생성과 SCAD 재현 | PC와 실제 OpenSCAD |
| 편집권·최신 revision·취소·후속 요청 | 서버와 두 독립 클라이언트 |
| UI 조작·모델 로딩·공유 값 | PC 브라우저 또는 Unity Simulator |
| 실제 STT·AI 요청 | 구성된 실제 provider |
| 현장 음성 전사 | 실제 회의용 마이크와 참가자 |
| 동일 위치의 모델·후보 | 같은 방의 두 Quest와 고정 QR |
| 결과를 이해하고 이어가는 효용 | 실제 다음 작업자와 응답 기록 |

기능의 구현과 실제 환경의 검증 상태는 실행 보고에서 따로 확인합니다.

## 이전 빌드의 2026-11-21 검증 기록

아래 표는 로컬 음성 전사와 상시 패널 손잡이를 도입하기 전 빌드의 실행 기록입니다. 당시 예시 서버는 `http://127.0.0.1:8788/meeting/`, live 서버는 `http://127.0.0.1:8789/meeting/`로 열었고 PC LAN 주소는 `192.168.0.4`였습니다. 다음 실행에서는 해당 PC의 현재 IP를 사용합니다.

이 검증에 사용한 서버는 `artifacts/shared-workflows/assembly.json`의 원본을 사용했습니다. 현재 실행 명령에서 다른 조립체를 쓰려면 `OPENSCAD_ASSEMBLY_CONFIG`에 해당 설정 경로를 지정합니다.

| 항목 | 구현과 직접 확인한 환경 |
| --- | --- |
| Live 전사·AI adapter | 실제 OpenAI 호출 수행. STT와 플래너 모두 HTTP 429 `credit_balance_exhausted` 반환. 성공한 현장 음성·AI 결과 미확인 |
| 입력과 후보 요청 분리 | 실제 브라우저에서 직접 입력 한 개, 모델 선택·화면 캡처·요청, 오류 표시 및 대본 대체 방지 확인 |
| 실제 새 형상과 후속 요청 | OpenSCAD로 57.3mm·피치 9.825mm 생성, V2 입구 유지, 세 번째 요청 확인. 로컬 통합검사의 플래너는 명시적인 시험 대역 |
| 두 클라이언트 공유 | 독립 브라우저 두 창 및 서버 HTTP 검사에서 후보 ID·GLB·슬라이더·편집권·채택·늦은 참가 확인 |
| Meta XR Simulator | 합성 QR pose, 선택 광선과 렌더링 화면 저장, 실제 크기 A/B/C, 빠른 슬라이더의 마지막 57.3mm, V1 proxy 보존, V3·이력·ZIP 확인 |
| Quest 단독 실행 | Android 빌드·설치 성공. 설치 APK 해시 일치, 실행 중인 Activity, live 서버 HTTP 200 확인 |
| 실제 두 Quest 정렬 | 미실시. 두 기기와 인쇄한 고정 QR, 실제 장착 변환 확인 필요 |
| 결과와 후속 작업 | 최종 SCAD를 별도 override 없이 재생성해 STL 전체 표면 일치 확인. 브라우저 새 세션에서 최종 모델·이전 결정 복원 |
| 실제 사용자 효용 | 미실시. 응답 양식은 자동 UI 점검용 입력으로만 확인 |

서버 회귀 테스트 69개 중 선택 의존성이 필요한 ArUco 검사 4개를 제외하고 통과했습니다. 기존 회의 통합 검사 20개와 코어 검사 54개도 통과했습니다. 확인 로그와 시뮬레이터 결과는 로컬 `artifacts/collaboration-regression.log`, `artifacts/live-ai-check/regression.log`, `artifacts/collaboration-core-checks.log`, `artifacts/assembly-simulator-report.json`에 있습니다.

당시 시뮬레이터의 예시 ZIP은 `artifacts/collaboration-example-result.zip`입니다. 현장 음성 인식은 선택한 전사 백엔드로 실제 참가자의 발화를 녹음해 확인합니다. 두 Quest 간 정합은 같은 받침에 각각 정렬해 모델 위치를 대조하고, 결과물의 이해도는 다음 작업자의 실제 응답으로 확인합니다.
