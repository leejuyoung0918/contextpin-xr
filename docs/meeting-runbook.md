# 3D 설계 회의 실행과 검증

## 케이블 정리대 회의 시작

README의 가상 환경, faster-whisper 한국어 전사 모델과 OpenSCAD를 준비합니다. 실시간 서버는 8789, 대본 서버는 8788 포트를 사용합니다. [실행 환경 준비](../README.md)

```sh
python3 scripts/prepare_cable_demo.py /path/to/cable_organizer.scad
MEETING_STT_BACKEND=local MEETING_PLAN_BACKEND=registered python3 scripts/relay.py server
```

대본 시연에는 별도 터미널에서 `python3 scripts/relay.py replay-server`를 실행합니다. 실시간 서버의 `/v1/capabilities`에서 전사 장치 설정을 확인할 수 있습니다. Quest 첫 화면에는 **새 회의 만들기**, **대본으로 체험**, 참가 QR과 코드 입력이 있습니다.

| 구간 | 실시간 회의 확인 |
| --- | --- |
| 시작 | Quest에서 **회의 시작**을 누르고 A4의 앞왼쪽부터 앞오른쪽, 뒤오른쪽, 뒤왼쪽을 짚습니다. 중앙에 실물을 놓고 오른쪽 앞 가상 Latest의 방향과 바닥을 확인한 뒤 **배치 확인**을 누릅니다. |
| 발언 | **수정 요청 시작**을 누르고 수정할 내용을 짧게 말합니다. **발언 완료**를 누르고 패널의 최종 전사를 확인합니다. |
| 선택과 저장 | 오른쪽 앞 가상 Latest의 부위를 가리키고 오른손 **A**를 누릅니다. **수정 요청 저장 완료**가 뜨면 서버의 `edit_requests/<회의 ID>/<요청 ID>/request.json`에서 버전·발언·모델 좌표·이미지 출처를 확인합니다. |
| 종료 | 상태 버튼의 **종료 → PC에서 회의록 받기**를 누릅니다. PC 시작 화면에서 **회의록 받기**에 10자리 코드를 입력합니다. 이전에 같은 회의에 참가한 브라우저에서는 바로 **전체 회의록 TXT**를 받을 수 있습니다. |

로컬 전사 백엔드 `local`은 실제 마이크의 PCM을 한국어로 전사합니다. `registered`는 케이블 정리대에서 지원하는 치수와 범위로 변경을 제안합니다. 대본 시연은 **대본으로 체험 → 예시 회의 시작**으로 열고 미리 준비한 발화와 형상을 사용합니다. [케이블 대본과 치수](cable-demo.md)

## 100mm 샘플 모델 회귀 시연

저장소 루트에서 [README](../README.md)의 가상 환경 설치를 마친 뒤 실행합니다.

```sh
artifacts/meeting-venv/bin/python -m backend.meeting
```

브라우저에서 [회의 화면](http://127.0.0.1:8788/meeting/)을 두 탭에 엽니다. 탭을 별도로 열고 첫 탭에서 만들고, 둘째 탭에서 6자리 참가 코드를 사용합니다. 코드는 10분 후 만료되며 진행자가 다시 발급할 수 있습니다. 자기 회의에 다시 연결할 때는 세션을 처음 열었던 탭을 새로고침합니다.

| 구간 | 시연 |
| --- | --- |
| 0–20초 | 초기 폭 100mm 모델을 열고 다른 참가자를 연결합니다. 수동으로 폭을 바꾸고 열린 후보를 확인합니다. |
| 20–45초 | **예시 발화**에서 높이 유지 요청과 서로 다른 기하 후보를 봅니다. 세 후보를 같은 시점에서 전환하고 변경된 부위를 확인합니다. |
| 45–75초 | 참가자는 발언의 해석에 가까운 안과 선호하는 디자인을 다르게 선택합니다. 진행자가 한 모델과 이유를 기록합니다. |
| 75–90초 | 회의록에서 발언과 변경 치수를 열고, 결정 버전의 STL 또는 전체 ZIP을 내보냅니다. |

회의 DB와 생성된 모델 파일은 기본적으로 `artifacts/meeting/`에 저장됩니다. 키 없이 사용하는 예시에는 한국어 예시 전사와 예시 플래너가 들어 있습니다. 두 경우 모두 생성 파일의 `build_mode`는 `generated`입니다. 참가자의 직접 입력은 `manual`, 예시 발화는 `replay`, 마이크로 실제 수집한 구간은 `live_capture`로 남습니다.

## Unity 및 Quest

| 장치 | 실행 |
| --- | --- |
| Unity 데스크톱 | `python3 scripts/relay.py desktop` |
| Meta XR Simulator | `python3 scripts/relay.py simulator` |
| USB Quest | `python3 scripts/relay.py build`, `python3 scripts/relay.py deploy` |

USB 배포가 8789와 8788 포트를 노트북으로 전달합니다. PC의 실시간 서버 상태와 전사·플래너 설정은 상태 페이지에서 확인합니다. 무선으로 참가하려면 시작할 서버의 `EDGE_ADVERTISED_URL`에 PC의 실제 LAN 주소를 지정하고 그 PC가 표시하는 참가 QR을 읽습니다. [실시간 상태](http://127.0.0.1:8789/healthz)

```sh
EDGE_ADVERTISED_URL=http://192.168.0.20:8789 \
MEETING_STT_BACKEND=local MEETING_PLAN_BACKEND=registered python3 scripts/relay.py server
```

로컬 Wi-Fi에서 브라우저 마이크까지 쓰려면 접속 주소가 포함된 TLS 인증서를 발급하고, 브라우저를 실행하는 각 장치에 발급 CA를 신뢰하도록 설정합니다. EDGE에는 `--tls-cert /path/to/cert.pem --tls-key /path/to/key.pem`과 HTTPS의 `EDGE_ADVERTISED_URL`을 넘깁니다. 장치에서 인증서가 검증된 주소를 열면 WebSocket도 WSS를 사용합니다. 기본 실행은 loopback에서만 수신합니다. [브라우저 마이크 보안 컨텍스트](https://developer.mozilla.org/en-US/docs/Web/API/MediaDevices/getUserMedia)

케이블 정리대는 각 헤드셋에서 고정 받침의 인쇄 QR과 설정된 장착 변환으로 정렬합니다. QR을 사용할 수 없는 장면에서는 **수동 맞추기**를 선택합니다. 현재 채택본과 후보의 실물 크기는 GLB의 미터 단위를 유지합니다. 100mm 샘플의 확대 보기는 화면 표시 배율을 조정합니다.

## 외부 전사·플래너 공급자를 사용할 때

### 한 노트북

공급자 키와 호환되는 모델 이름을 환경 변수로 주입한 터미널에서 실행합니다.

```sh
AI_MODE=live artifacts/meeting-venv/bin/python -m backend.meeting
```

PC에 내장된 Gateway 모듈이 WAV 전사와 OpenAI 호환 HTTP를 사용합니다. 공간 회의에서는 마이크로 지정된 Quest 또는 PC 브라우저가 PCM을 지속 전송합니다. 서버가 음성 구간을 나누고 확정된 전사를 설계 요청에 사용합니다. 공동 마이크의 발화는 화자 미확인으로 저장됩니다. 케이블 정리대에 외부 공급자를 연결할 때는 `MEETING_STT_BACKEND=provider MEETING_PLAN_BACKEND=provider python3 scripts/relay.py server`로 실행합니다.

### 분리 배치

Gateway 호스트에서 TLS 인증서와 공급자·Gateway 토큰을 설정합니다. EDGE와 마찬가지로 Python 의존성을 설치합니다.

```sh
artifacts/meeting-venv/bin/python -m backend.meeting gateway \
  --host 0.0.0.0 --port 8789 --tls-cert /path/to/cert.pem --tls-key /path/to/key.pem
```

EDGE 호스트에서 동일한 `AI_GATEWAY_TOKEN`과 함께 다음을 실행합니다.

```sh
AI_MODE=live AI_GATEWAY_URL=https://gateway.example:8789 \
  artifacts/meeting-venv/bin/python -m backend.meeting
```

Gateway는 전사와 설계 제안만 처리합니다. SQLite, 파일, 최종 결정은 EDGE에 남습니다. 기본 검토는 가지치기된 전사와 모델 맥락을 사용합니다. 사진은 세션의 근거로 저장하며 이 구성에서는 AI에 전송하지 않습니다. 원음 저장 기본값은 꺼져 있습니다. 저장 여부와 전사 공급자 사용 범위는 녹음 전에 참여자에게 알립니다.

| 환경 변수 | 위치와 용도 |
| --- | --- |
| `EDGE_BIND_HOST`, `EDGE_PORT`, `EDGE_ADVERTISED_URL`, `EDGE_DATA_DIR` | EDGE 접속 주소 및 저장 폴더 |
| `ACTIVE_OBJECT_PROFILE` | 새 회의의 기본 모델 프로필 |
| `AI_MODE` | 기본값 `replay`. 공급자 연결은 `live` |
| `MEETING_STT_BACKEND`, `MEETING_WHISPER_DIR` | 로컬은 `local`, 기본 모델 위치는 `artifacts/tools/faster-whisper-base`. 외부 공급자는 `provider` |
| `MEETING_PLAN_BACKEND` | 케이블 정리대의 등록 치수 플래너는 `registered`, 외부 모델은 `provider` |
| `AI_API_KEY`, `AI_BASE_URL`, `AI_TRANSCRIPTION_MODEL`, `AI_PLANNER_MODEL` | 공급자 키·URL·모델. 분리 배치에서 Gateway 호스트만 설정 |
| `AI_GATEWAY_URL`, `AI_GATEWAY_TOKEN` | 분리 배치 URL 및 EDGE↔Gateway 인증 |
| `AI_GATEWAY_BIND_HOST`, `AI_GATEWAY_PORT` | Gateway 수신 위치 |
| `STORE_RAW_AUDIO`, `VAD_END_SILENCE_MS`, `MAX_MEDIA_BYTES` | 원음 파일 저장, 발화 구간 분리 및 사진 크기 |
| `CAD_WORKER_CONCURRENCY`, `CAD_JOB_TIMEOUT_SECONDS`, `FINAL_CONTEXT_DEBOUNCE_MS` | 모델 생성 동시 실행·제한 시간·발화 합치기 |
| `REQUIRE_ALL_PARTICIPANTS_ACK` | `true`일 때 모든 참가자가 동일 후보를 검토 확인해야 채택 가능 |

## 반복 검증

```sh
artifacts/meeting-venv/bin/python -m pip install httpx==0.28.1
artifacts/meeting-venv/bin/python -m unittest discover -s tests/meeting -v
python3 -m unittest backend.meeting.test_geometry -v
python3 scripts/relay.py test
python3 scripts/relay.py smoke-assembly-simulator
```

조립체 공간 스모크는 앞서 실행한 8788 대본 서버에 붙습니다. 100mm 일반 프로필을 별도로 띄운 경우 `MEETING_EDGE_URL`을 그 서버 주소로 지정하여 `smoke-meeting`과 `smoke-meeting-simulator`를 실행할 수 있습니다. Chrome 시나리오를 반복하려면 별도 프로필로 Chrome DevTools를 로컬에서 열고 Node 20 이상에서 테스트를 실행합니다.

```sh
"/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" --headless \
  --remote-debugging-port=9288 --user-data-dir="$(mktemp -d)" --enable-unsafe-swiftshader
```

```sh
node --experimental-websocket tests/meeting_browser_smoke.mjs
```

장치에서 확인할 때는 초기 모델의 실제 폭 100mm가 화면에서 0.1m로 읽히는지, 폭만 수정한 뒤 동일한 버전의 STL을 mm로 읽는지 확인합니다. Unity 데스크톱과 XR Simulator 검증은 물리적인 공간 배치나 실제 카메라·마이크 검증과 구분됩니다. 실제 시제품을 출력하면 슬라이서 배율, 예상 형상과 맞는지, 조립 시 맞음새를 버전별로 기록합니다. 형상 보고서의 `print_process_validation` 기본값은 `not_performed`입니다.
