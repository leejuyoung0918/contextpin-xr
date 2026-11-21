# 3D 설계 회의 실행과 검증

## 같은 노트북에서 시연

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
| Unity 데스크톱 | `python3 scripts/relay.py desktop` → 홈의 **3D 설계 회의** |
| Meta XR Simulator | `python3 scripts/relay.py simulator` → 홈의 **3D 설계 회의** |
| USB Quest | `python3 scripts/relay.py build`, `python3 scripts/relay.py deploy`, `adb reverse tcp:8788 tcp:8788` → 홈의 **3D 설계 회의** |

시작 전에 같은 장치에서 EDGE **연결 확인**을 사용하거나 [로컬 상태 확인](http://127.0.0.1:8788/healthz)을 엽니다. 기기의 localhost는 해당 기기 자신을 가리킵니다. USB 역방향 포워딩은 Quest에 있는 8788 포트를 노트북에 전달합니다. 무선 연결에서는 다음처럼 서버를 바인딩하고 노트북에서 실제로 쓰는 주소를 Unity에 지정합니다.

```sh
EDGE_ADVERTISED_URL=http://192.168.0.20:8788 \
  artifacts/meeting-venv/bin/python -m backend.meeting --host 0.0.0.0
```

로컬 Wi-Fi에서 브라우저 마이크까지 쓰려면 접속 주소가 포함된 TLS 인증서를 발급하고, 브라우저를 실행하는 각 장치에 발급 CA를 신뢰하도록 설정합니다. EDGE에는 `--tls-cert /path/to/cert.pem --tls-key /path/to/key.pem`과 HTTPS의 `EDGE_ADVERTISED_URL`을 넘깁니다. 장치에서 인증서가 검증된 주소를 열면 WebSocket도 WSS를 사용합니다. 기본 실행은 loopback에서만 수신합니다. [브라우저 마이크 보안 컨텍스트](https://developer.mozilla.org/en-US/docs/Web/API/MediaDevices/getUserMedia)

공간 모델 배치는 각 Unity 장치에서 수동으로 지정합니다. **실물 크기**는 GLB의 미터 단위 크기를 유지하며, 확대는 디스플레이에만 적용합니다. 따라서 다른 기기에서 모델 버전과 설계 치수는 같고 배치 위치는 장치별로 설정됩니다. 장치가 보고하는 카메라·손·시선·공간 추적 상태를 확인하세요. 정밀한 공유 앵커와 실물 대상을 자동으로 CAD 부위에 대입하는 기능은 별도 실기 검증 범위입니다.

## AI Gateway 실행

### 한 노트북

공급자 키와 호환되는 모델 이름을 환경 변수로 주입한 터미널에서 실행합니다.

```sh
AI_MODE=live artifacts/meeting-venv/bin/python -m backend.meeting
```

EDGE 내부의 Gateway 모듈이 기존 relay의 WAV 전사와 OpenAI 호환 HTTP를 사용합니다. 브라우저는 오디오 소유자로 지정된 한 기기에서 PCM16을 전송합니다. 서버는 짧은 구간을 VAD로 나눕니다. Unity는 기존 마이크 캡처에서 최대 15초 WAV 구간을 보냅니다. 완료된 전사만 설계에 사용합니다. 스트리밍 부분 전사를 반환하는 공급자 어댑터가 없어 중간 자막은 표시하지 않습니다. 화자가 확인되지 않은 공동 마이크는 화자를 미확인으로 저장합니다. 사용자가 특정 화면을 발화 근거로 연결하도록 지정할 수 있습니다.

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
python3 scripts/relay.py smoke
python3 scripts/relay.py smoke-meeting
python3 scripts/relay.py smoke-meeting-simulator
```

두 회의 스모크 테스트는 앞서 실행한 EDGE에 붙습니다. Chrome 7개 시나리오를 반복하려면 별도 프로필로 Chrome DevTools를 로컬에서 열고 Node 20 이상에서 테스트를 실행합니다.

```sh
"/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" --headless \
  --remote-debugging-port=9288 --user-data-dir="$(mktemp -d)" --enable-unsafe-swiftshader
```

```sh
node --experimental-websocket tests/meeting_browser_smoke.mjs
```

장치에서 확인할 때는 초기 모델의 실제 폭 100mm가 화면에서 0.1m로 읽히는지, 폭만 수정한 뒤 동일한 버전의 STL을 mm로 읽는지 확인합니다. Unity 데스크톱과 XR Simulator 검증은 물리적인 공간 배치나 실제 카메라·마이크 검증과 구분됩니다. 실제 시제품을 출력하면 슬라이서 배율, 예상 형상과 맞는지, 조립 시 맞음새를 버전별로 기록합니다. 형상 보고서의 `print_process_validation` 기본값은 `not_performed`입니다.
