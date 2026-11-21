# ContextPin XR · 휴대폰 거치대 설계 회의

A4 중앙에 실물 거치대를 두고, 그 옆의 청록색 **Latest**에서 바꿀 부위를 가리킵니다. 현장 PC가 OpenSCAD로 만든 후보 A와 B를 책상 위에서 비교합니다. 후보를 채택하면 다음 버전이 Latest 자리에 나타납니다. [Quest 시연 순서](DEMO.md)

## 현장 PC와 Quest 실행

이 브랜치의 기본 모델은 저장소에 포함된 휴대폰 거치대입니다. Python 3.11 이상과 OpenSCAD를 준비합니다. 새 PC에서는 저장소 루트에서 아래 명령으로 회의 서버 의존성과 한국어 음성 모델을 설치합니다. [CAD 원본](backend/meeting/phone_stand/phone_stand_parametric.scad) · [모델 설정](backend/meeting/phone_stand/assembly.json)

```sh
python3 -m venv artifacts/meeting-venv
artifacts/meeting-venv/bin/python -m pip install -r backend/meeting/requirements.txt faster-whisper==1.2.1
HF_HUB_DISABLE_XET=1 artifacts/meeting-venv/bin/python -c 'from huggingface_hub import snapshot_download; snapshot_download("Systran/faster-whisper-base", local_dir="artifacts/tools/faster-whisper-base", allow_patterns=["model.bin","config.json","tokenizer.json","vocabulary.txt"])'
```

현장 Mac에서 로컬 비전 모델이 이미 동작 중이면 그대로 사용합니다. 시작하려면 준비한 llama.cpp 실행 파일, Qwen3-VL 가중치와 비전 프로젝터를 다음과 같이 지정합니다. [Qwen 모델 파일](https://huggingface.co/Qwen/Qwen3-VL-2B-Instruct-GGUF) · [llama.cpp 배포판](https://github.com/ggml-org/llama.cpp/releases/tag/b12608)

```sh
VLM_DIR=artifacts/tools/candidate-vlm
"$VLM_DIR/llama-b12608/llama-server" --host 127.0.0.1 --port 18905 --alias qwen3-vl-2b \
  -m "$VLM_DIR/Qwen3VL-2B-Instruct-Q4_K_M.gguf" --mmproj "$VLM_DIR/mmproj-Qwen3VL-2B-Instruct-Q8_0.gguf" \
  --ctx-size 12288 --parallel 1 --image-min-tokens 1024 --image-max-tokens 1024 --temp 0 --reasoning off -n 650
```

다른 터미널에서 거치대 서버를 시작합니다. 기본 포트는 **8791**, 새 회의 저장 경로는 `artifacts/phone-stand-live/`입니다. 서버가 OpenSCAD를 찾지 못하면 `OPENSCAD_EXECUTABLE`을 실행 파일 경로로 지정합니다. 기본값으로 실행하려면 셸과 `backend/.env`의 `OPENSCAD_ASSEMBLY_CONFIG`, `EDGE_PORT`, `EDGE_DATA_DIR`에 케이블 데모 전용 재지정 값이 남아 있는지 확인하세요. [PC 회의 화면](http://127.0.0.1:8791/meeting/)

```sh
python3 scripts/relay.py server
```

Unity 6000.4.0f1과 Android Build Support가 있는 PC에서 Quest의 USB 디버깅을 허용한 뒤 빌드하고 설치합니다. 앱 이름은 **ContextPin XR Phone Stand**, 패키지 ID는 `org.contextpin.relay.phonestand`, 빌드 결과는 `builds/ContextPinPhoneStand.apk`입니다. 설치 명령이 Quest의 8791 포트를 거치대 서버에 연결합니다. [실행 스크립트](scripts/relay.py)

```sh
python3 scripts/relay.py build
python3 scripts/relay.py deploy
```

현장 서버는 한국어 발화를 로컬 faster-whisper로 전사하고 선택 이미지, Ray, 현재 CAD와 확정 발화를 로컬 Qwen3-VL로 해석합니다. 외부 AI API 사용료는 0원입니다. 로컬 모델 오류는 회의 화면에 표시됩니다. 팀 키가 있는 PC에서도 이 경로는 로컬 모델만 호출합니다. [후보 해석 구현](backend/meeting/bundle_interpretation.py)

## 두 Quest로 같은 거치대 보기

1. 두 Quest를 같은 PC 회의 서버에 연결합니다. USB로 시연할 때는 `adb devices -l`에서 기기번호를 확인하고 **기기마다** `python3 scripts/relay.py deploy --serial 기기번호`를 실행합니다. 두 USB 연결을 유지합니다. 같은 Wi-Fi에서 무선으로 실행하려면 빌드 전에 `ContextPinRelay/Assets/Resources/relay-local.json`을 열거나 생성해 `meetingServerUrl`을 현장 PC 주소로 설정합니다. 예를 들어 PC 주소가 192.168.1.20이면 `{"meetingServerUrl":"http://192.168.1.20:8791"}`입니다.
2. A4를 책상에 고정합니다. 진행자가 한쪽에 서서 **왼쪽 아래 ①, 오른쪽 아래 ②, 오른쪽 위 ③, 왼쪽 위 ④**를 종이에 직접 적습니다. 진행자가 **회의 준비**를 누르고 네 모서리를 번호순으로 짚은 뒤 **배치 확인**을 누릅니다.
3. 참가자는 **참가 코드 입력**에서 진행자 패널의 6자리 코드를 입력합니다. 반대편에 서 있어도 같은 종이에 적힌 **①→②→③→④**를 짚습니다. 정렬되면 현재 Latest와 준비된 후보가 나타납니다. 진행 중인 회의에 들어가도 현재 버전에서 시작합니다.
4. 참가자가 Latest를 가리키고 오른손 **A**를 누르면 같은 CAD 지점에 표시가 뜹니다. 후보를 가리키고 **A**를 누르면 검토할 후보를 제안합니다. 진행자가 후보에 **A**를 눌러 채택하면 두 Quest의 Latest가 함께 갱신됩니다.
5. 한 기기만 추적을 잃으면 해당 기기에서 **A4로 다시 맞추기**를 사용합니다. 실제 종이를 옮겼다면 진행자가 **메뉴 → 종이를 옮겼어요 · 공통 기준 변경**을 누르고 모두 새 위치에서 네 점을 다시 짚습니다.

종이 중심이 공통 원점이며 **+X는 ①→②, +Z는 ①→④, +Y는 책상 위쪽**입니다. 위치는 미터, 회전은 Unity 쿼터니언 `[x,y,z,w]`입니다. 서버는 진행자가 확인한 Latest와 실제 배치한 각 후보의 종이 기준 자세를 보관합니다. 각 Quest는 자기 A4 자세에 이 값을 곱합니다. [Unity 변환](ContextPinRelay/Assets/ContextPin/Runtime/MeetingPaperSpace.cs) · [서버 상태](backend/meeting/paper_space.py)

| 책상 위 모델 | 종이 중심에서 잰 위치 |
|---|---|
| 실물 거치대 | A4 중앙 |
| Latest · 청록색 | ①→② 방향으로 24 cm |
| 후보 A · 주황색 | ①→④ 방향으로 24 cm, ②→① 방향으로 14 cm |
| 후보 B · 보라색 | ①→④ 방향으로 24 cm, ①→② 방향으로 14 cm |

거리는 각 거치대 바닥의 중심까지 잽니다. 종이 밖의 모델도 같은 높이의 책상에 놓이도록 공간을 비워 둡니다. 이전 버전에서 Latest를 중앙에 두었던 회의는 진행자가 **이전 회의 열기**로 돌아온 뒤 네 모서리를 다시 지정하고 **배치 확인**을 누릅니다.

## 회의 녹음과 수정 요청

1. Quest에서 **회의 준비**를 누르고 A4의 네 점을 지정합니다. 실물 옆에 놓인 청록색 V1의 바닥과 방향을 확인한 뒤 **배치 확인 → 회의 시작**을 누릅니다. 이때부터 같은 마이크 스트림으로 일반 대화와 후보 검토 중의 대화를 전사합니다.
2. **수정 요청**을 누르고 바꿀 부분을 말한 뒤 **발언 완료**를 누릅니다. 앞뒤 무음을 제외한 발화 길이의 절반 이상이 두 버튼 사이에 들어오면 그 확정 발화 전체가 명령이 됩니다. 한 요청에 여러 문장을 말할 수 있습니다. 무음 안내가 뜨면 **수정 요청**을 다시 누릅니다.
3. 확정 문구를 확인하고 청록색 Latest의 부위를 가리켜 **A**를 누릅니다. 서버는 요청을 시작한 버전과 발화 ID, 표면 좌표, 이미지로 후보를 생성합니다. 원하는 후보를 가리켜 **A**로 채택하면 기존 Latest 자리에 다음 버전이 나타납니다.
4. **회의 종료**를 누르면 마지막 음성과 대기 중인 전사를 처리하고 **회의 정리 중…**이 뜹니다. **회의록 저장 완료**가 뜨면 PC에서 회의록을 받습니다. [버튼별 시연 대본](DEMO.md)

## CAD 치수와 기준점

기본 모델에서 OpenSCAD가 생성한 STL의 실제 치수는 **너비 120 × 깊이 145 × 높이 127.9005 mm**입니다. OpenSCAD 원점은 바닥 중심이고 바닥 평면은 `z = 0`입니다. 위쪽은 CAD +Z, 충전 통로가 있는 앞턱은 CAD −Y 쪽에 있습니다. 실제 올린 휴대폰은 모델에 포함되지 않습니다. [SCAD 형상](backend/meeting/phone_stand/phone_stand_parametric.scad)

서버는 STL의 `(x, y, z)` mm를 GLB의 `(x, z, −y) / 1000` m로 변환합니다. glTFast가 Unity 좌표의 X 방향을 반전하므로, 모델 로컬 좌표에서 위쪽은 +Y, 앞턱은 +Z입니다. A4를 안내 순서로 등록하면 앞턱은 처음 짚은 종이 변의 반대쪽에 보입니다. V2와 V3도 V1의 바닥 중심과 회전을 사용합니다. [메시 변환](backend/meeting/openscad_geometry.py) · [Latest 배치](ContextPinRelay/Assets/ContextPin/Runtime/MeetingAssembly.cs)

| 편집 변수 | 조절하는 치수 | 기본값 | 등록 범위 |
|---|---|---:|---:|
| `viewing_angle` | 책상과 등받이 사이 각도 | 65° | 50–80° |
| `backrest_length` | 기울어진 등받이를 따라 잰 길이 | 132 mm | 118–145 mm |
| `backrest_width` | 등받이 너비 | 88 mm | 80–96 mm |
| `base_width` | 바닥 받침의 좌우 너비 | 120 mm | 110–130 mm |
| `base_depth` | 바닥 받침의 앞뒤 깊이 | 145 mm | 140–155 mm |
| `lip_height` | 앞턱의 바닥 받침 윗면 기준 높이 | 15 mm | 10–22 mm |
| `charging_notch_width` | 앞턱 중앙의 충전 통로 너비 | 15 mm | 8–24 mm |

예를 들어 등받이 각도를 바꾸면 지지대도 SCAD 수식에 따라 연결부를 따라갑니다. 앞턱 높이 변경은 좌우 양쪽에 함께 적용됩니다. 바닥 두께 7 mm, 모서리 반경 8 mm, 등받이 하단 회전축 `y = −37, z = 7` mm와 내부 창 치수는 고정입니다. 서버는 바닥의 아랫부분 2 mm를 잘라 **요청된 받침 폭·깊이를 적용한 기준 형상**과 비교합니다. 이 검사는 CAD 형상에 대한 검사이며 실물 출력, 강도, 하중 시험은 별도입니다. [프로필과 허용 범위](backend/meeting/phone_stand/assembly.json)

## 결과 파일

**회의록 저장 완료 → PC에서 회의록 받기**를 누른 뒤 Quest에 표시된 코드를 PC 회의 화면에 입력하면 Markdown 회의록을 내려받습니다. 전체 파일은 해당 회의에 참가한 PC의 결과 화면에서 **CAD 포함 ZIP**, 또는 Quest의 **메뉴 → 결과 ZIP 저장**으로 받습니다. [결과물 저장 코드](backend/meeting/meeting_result.py)

| 파일 | 확인할 내용 |
|---|---|
| `minutes/minutes.md` | 회의 목적과 주요 논의, 채택한 후보, 실제 치수 변경, 미해결 사항과 다음 작업 |
| `minutes/minutes.json` | 토론 근거 발화 ID, 수정 요청 ID, 결정별 이전·결과 버전, 선택 후보와 기록된 이유 |
| `transcript.txt`, `transcript.jsonl` | 시간순 전체 대화와 각 발화의 ID·개정·출처 |
| `edit_requests/`, `decisions.json` | 수정 명령의 원문, Ray 좌표, 선택 시점 이미지, 사람이 채택한 후보 |
| `design_history.json`, `adopted_versions/`, `cad/` | 버전별 SCAD·치수·STL과 최종 `cad/final.scad`, `cad/final.stl` |
| `manifest.json` | 회의 ID와 처음·마지막 버전, 전사와 회의록 상태, 파일별 크기와 SHA-256 |

현장 PC의 로컬 모델이 전체 전사와 설계 기록에서 논의 근거를 고릅니다. 채택 이력과 치수 변화는 서버가 보관한 실제 버전에서 가져옵니다. 입력된 선택 이유가 없으면 회의록에 **미기록**으로 표시합니다. AI 생성에 실패하면 회의록 코드는 전사·결정 기록을 제공하고, 전체 ZIP에는 CAD와 근거 자료가 보존됩니다. 회의 진행자는 인증된 `POST /v1/sessions/<회의 ID>/minutes/regenerate`로 저장한 근거에서 회의록을 다시 생성할 수 있습니다.

실시간 회의의 전체 전사는 CAD 내보내기 전에 `artifacts/phone-stand-live/results/<회의 ID>/transcript/`에 확정합니다. `transcript.txt`는 각 발화의 최종 문장을 시간순으로 보여주고, `transcript.jsonl`은 발화 ID, 시작·종료 시각, 입력 출처와 개정을 보존합니다. 결과 ZIP에도 같은 두 파일이 들어갑니다.

현장 PC에서는 `artifacts/phone-stand-live/models/<버전 ID>/` 아래에 적용된 `candidate.scad`, `print.stl`, `preview.glb`를 보관합니다. `edit_requests/<회의 ID>/<요청 ID>/`에는 발화 원음, 확정 전사, 이미지, Ray 선택 근거와 후보 세트를 저장합니다. 이미지 출처는 Quest RGB 촬영이면 `quest_rgb`, 렌더 화면이면 `rendered_debug`입니다.

## 검증 실행

`python3 scripts/relay.py test`로 회귀 검사를 실행합니다. 거치대 형상 검사만 실행하려면 아래 명령을 사용합니다. OpenSCAD가 PATH에 없으면 로컬 실행 파일을 `OPENSCAD_EXECUTABLE`로 지정합니다.

```sh
artifacts/meeting-venv/bin/python -m unittest -v \
  backend.meeting.test_openscad_geometry.OpenScadGeometryTests.test_phone_stand_exports_keep_the_base_origin_and_exact_scale
```

Meta XR Simulator의 두 번 채택 검사는 운영 회의와 분리한 테스트 서버를 사용합니다. 로컬 모델을 실행하고 별도 터미널에서 같은 거치대 프로필로 QA 서버를 시작합니다.

```sh
EDGE_BIND_HOST=127.0.0.1 EDGE_PORT=18897 EDGE_DATA_DIR="$PWD/artifacts/phase3-minutes-qa" python3 scripts/relay.py server
```

Mac의 한국어 음성과 FFmpeg로 일반 대화와 수정 명령을 준비합니다. 시뮬레이터는 두 번의 채택과 종료 직전 발화를 완료하고, 앱으로 내려받은 ZIP에서 회의록의 결정·발화 ID 및 최종 CAD 파일 해시를 대조합니다.

```sh
python3 tests/meeting/prepare_timeline_audio.py artifacts/phase2-timeline-audio
MEETING_EDGE_URL=http://127.0.0.1:18897 MEETING_CAPTURE_DATA_DIR="$PWD/artifacts/phase3-minutes-qa" \
  MEETING_CANDIDATE_COMMIT_SMOKE=1 MEETING_PHONE_STAND_SMOKE=1 \
  MEETING_TIMELINE_PCM_DIR="$PWD/artifacts/phase2-timeline-audio" \
  MEETING_EXPLICIT_PCM="$PWD/artifacts/phase2-timeline-audio/angle.pcm" \
  MEETING_FOLLOWUP_PCM="$PWD/artifacts/phase2-timeline-audio/lip.pcm" \
  python3 scripts/relay.py smoke-assembly-simulator
```

같은 QA 서버에서 두 사용자의 좌표 검사를 실행할 수 있습니다. 시뮬레이터 하나에서 진행자와 참가자 역할을 전환하고 별도 참가 토큰과 이벤트 연결을 사용합니다. 서로 다른 추적 원점에서 본 모델의 종이 기준 위치·회전, 공유 Ray, 늦은 참가와 재정렬 결과는 `artifacts/colocation-simulator-report.json`에 기록됩니다.

```sh
MEETING_EDGE_URL=http://127.0.0.1:18897 MEETING_CAPTURE_DATA_DIR="$PWD/artifacts/phase3-minutes-qa" \
  MEETING_COLOCATION_SMOKE=1 MEETING_EXPLICIT_PCM="$PWD/artifacts/phase2-timeline-audio/angle.pcm" \
  python3 scripts/relay.py smoke-assembly-simulator
```

## 기존 케이블 정리대 데모

케이블 정리대 안정 버전은 `main`의 체크포인트 `9b46ace754246747388e5dd1f7cc285638efc47b`입니다. 해당 버전의 실시간 서버는 8789, Quest 패키지는 `org.contextpin.relay`입니다. 이 브랜치에서도 8788의 기존 케이블 대본을 실행할 수 있습니다. [케이블 대본 실행 순서](DEMO.md#기존-케이블-정리대-대본)
