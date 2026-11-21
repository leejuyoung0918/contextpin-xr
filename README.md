# ContextPin XR · 3D 설계 회의

Quest 3와 Quest 3S에서 실제 시제품과 같은 크기의 설계 후보를 책상 위에서 비교하는 앱입니다. A4 중앙에는 실물을 놓습니다. 오른쪽 앞에는 현재 채택한 CAD(Latest)가 있고, 왼쪽 뒤에는 수정 후보 A, 오른쪽 뒤에는 수정 후보 B가 나타납니다. 수정할 부위를 컨트롤러로 가리키면 현장 PC가 발화·모델 좌표·이미지를 함께 저장하고 OpenSCAD 후보를 생성합니다. 선택한 모델에서 다음 수정을 이어갈 수 있으며, 채택 이력과 선택 이유, 전체 전사는 서버에 보관됩니다.

## 현장 PC 실행

Python 3.11 이상과 OpenSCAD가 필요합니다. 등록할 CAD는 다섯 홈 케이블 정리대의 원본입니다.

```sh
python3 -m venv artifacts/meeting-venv
artifacts/meeting-venv/bin/python -m pip install -r backend/meeting/requirements.txt
python3 scripts/prepare_cable_demo.py --upper-corners backend/meeting/openscad_sample/cable_organizer.scad
```

등록하면 원본 CAD와 기존 회의용 `assembly.json`, 상부 외곽 네 모서리를 각각 조정할 수 있는 새 회의용 `corner-assembly.json`이 `artifacts/cable-demo/`에 준비됩니다. 실시간 서버는 새 회의용 설정을, 예시 재생은 기존 설정을 자동으로 선택합니다. 다른 등록 파일은 `OPENSCAD_ASSEMBLY_CONFIG`에 지정할 수 있습니다.

**실시간 회의**는 [PC 회의 화면](http://127.0.0.1:8789/meeting/)에서 엽니다. 기본 포트는 8789입니다. OpenSCAD가 PATH에 없으면 `OPENSCAD_EXECUTABLE`로 실행 파일을 지정하세요. 참가 QR에는 각 헤드셋에서 도달할 수 있는 PC 주소가 필요합니다. Mac의 기본 Wi-Fi 주소를 자동으로 사용하며 다른 인터페이스에서는 `EDGE_ADVERTISED_URL=http://PC의_LAN_IP:8789`를 지정합니다. 공급자 설정은 PC의 환경 변수나 `backend/.env`에 둡니다.

### 로컬 한국어 전사와 수정안 생성

이 PC는 한국어 faster-whisper를 로컬에서 실행합니다. 새 Quest 회의의 설계 후보는 이미지 입력을 지원하는 모델이 해석하고 OpenSCAD가 생성합니다. 기존 대본·등록 치수 방식도 별도로 사용할 수 있습니다. 다른 PC에서도 같은 구성으로 실행하려면 아래 명령으로 음성 모델을 한 번 설치합니다.

```sh
artifacts/meeting-venv/bin/python -m pip install faster-whisper==1.2.1
HF_HUB_DISABLE_XET=1 artifacts/meeting-venv/bin/python -c 'from huggingface_hub import snapshot_download; snapshot_download("Systran/faster-whisper-base", local_dir="artifacts/tools/faster-whisper-base", allow_patterns=["model.bin","config.json","tokenizer.json","vocabulary.txt"])'
```

현장 회의 서버는 한국어 전사를 PC의 faster-whisper에 맡기고, 기존 대본·치수 흐름은 등록된 규칙으로 처리합니다. [설정 예시](backend/.env.example)

### 로컬 모델로 후보 개발·검증하기

기본 명령 `python3 scripts/relay.py server`의 **외부 AI API 사용료는 0원**입니다. 전사와 CAD 생성은 현장 PC에서 수행하고, 후보 해석에는 이 PC의 Qwen3-VL을 사용합니다. 서버는 확정 전사, 선택 이미지, Ray 좌표·법선, 기준 버전의 SCAD·치수·제약과 선택점을 표시한 STL 렌더를 로컬 모델에 전달합니다. 서버를 시작한 셸에 기존 클라우드 설정이 있어도 이 경로가 적용됩니다. 같은 요청을 다시 열면 저장된 CandidateSet을 불러옵니다. 예시 재생과 보관 회의 검토도 로컬로 실행합니다. [모델 입력과 결과 검증 구현](backend/meeting/bundle_interpretation.py)

이 Mac에서는 아래 명령을 두 터미널에서 실행합니다. 다른 Mac에서는 Apple Silicon용 llama.cpp와 GGUF 모델·비전 프로젝터를 먼저 준비합니다. [Qwen 모델 파일](https://huggingface.co/Qwen/Qwen3-VL-2B-Instruct-GGUF) · [llama.cpp 배포판](https://github.com/ggml-org/llama.cpp/releases/tag/b12608)

```sh
VLM_DIR=artifacts/tools/candidate-vlm
"$VLM_DIR/llama-b12608/llama-server" --host 127.0.0.1 --port 18905 --alias qwen3-vl-2b \
  -m "$VLM_DIR/Qwen3VL-2B-Instruct-Q4_K_M.gguf" --mmproj "$VLM_DIR/mmproj-Qwen3VL-2B-Instruct-Q8_0.gguf" \
  --ctx-size 12288 --parallel 1 --image-min-tokens 1024 --image-max-tokens 1024 --temp 0 --reasoning off -n 650
```

모델 로딩이 끝나고 첫 터미널에 포트 18905의 수신 상태가 뜨면, 저장소를 연 두 번째 터미널에서 회의 서버를 시작합니다.

```sh
python3 scripts/relay.py server
```

로컬 모델이 응답하지 않으면 해당 요청에 오류가 표시됩니다. 다른 로컬 모델로 검사할 때는 `MEETING_BUNDLE_BASE_URL`을 PC의 루프백 HTTP 주소·포트로, `MEETING_BUNDLE_MODEL`을 그 모델의 별칭으로 지정합니다.

현장 회의 앱은 팀 키나 개인 키가 있어도 외부 AI 서비스를 호출하지 않습니다. 예전 팀 모델 실행 옵션은 서버 시작 시 설정 오류로 안내합니다. 로컬 모델이 멈추면 오류가 표시되며, 전사는 직접 입력으로 복구할 수 있습니다.

**예시 시연**은 별도 터미널에서 `python3 scripts/relay.py replay-server`를 실행하고 Quest에서 **대본으로 체험**을 선택합니다. 이 모드는 8788 포트에 기록하며 대본에 준비된 발화를 사용합니다. [예시 시연 순서](DEMO.md)

## Quest에서 한 번의 회의 진행하기

1. Quest에서 **회의 시작**을 누르고 마이크 권한을 허용합니다. 이때부터 전체 회의가 전사됩니다. 다른 참가자는 PC에 표시한 참가 QR 또는 여섯 자리 코드를 사용합니다.
2. 책상의 빈 A4를 **앞왼쪽 → 앞오른쪽 → 뒤오른쪽 → 뒤왼쪽** 순서로 짚고 각 모서리에서 오른손 **A**를 누릅니다. 실물 시제품을 **A4 중앙**에 놓습니다. **오른쪽 앞의 Latest V1**이 실물과 같은 방향으로 책상에 놓였는지 보고 **배치 확인**을 누릅니다.
3. **수정 요청 시작**을 누르고 “이 모서리를 조금 더 둥글게 만들어줘”라고 말한 뒤 **발언 완료**를 누릅니다. 최종 전사가 뜨면 **오른쪽 앞 Latest의 상단 외곽 모서리** 하나에 Ray를 대고 오른손 **A**를 누릅니다. 그 순간의 이미지와 모델 좌표가 같은 요청에 저장됩니다.
4. 종이 **왼쪽 뒤의 후보 A**와 **오른쪽 뒤의 후보 B**를 중앙의 실물, 오른쪽 앞의 V1과 실제 크기로 비교합니다. 원하는 **후보 모델**을 Ray로 가리켜 **A**를 누릅니다. 서버가 선택을 저장하고 V2가 로드되면 후보들이 사라지고 **오른쪽 앞 같은 자리**에 V2가 나타납니다. 후보 수가 줄거나 배치 공간이 부족하면 화면의 제한 이유를 확인하세요.
5. **수정 요청 시작 → “전체 길이를 조금 줄여줘” → 발언 완료** 후 **오른쪽 앞의 V2**를 가리켜 **A**를 누릅니다. 뒤쪽의 길이 후보 중 하나를 채택하면 오른쪽 앞 모델이 V3로 바뀝니다. 중앙의 실물과 비교해 모서리와 길이 변경을 함께 확인합니다.
6. 종료 후 **PC에서 회의록 받기**로 결과를 내려받습니다. 서버의 `EDGE_DATA_DIR/edit_requests/<회의 ID>/<요청 ID>/`에는 발화 원음, 전사, 이미지, Ray 좌표와 `candidate_set.json`이 있습니다. 후보 SCAD·STL·GLB는 `EDGE_DATA_DIR/models/<후보 version_id>/`에 있습니다. 선택 이유를 입력하지 않은 결정은 `not_stated`로 기록됩니다.

이미지 출처는 실제 카메라 프레임일 때 `quest_rgb`, XR 렌더 화면일 때 `rendered_debug`입니다. STT가 실패하면 다시 녹음하거나 PC에서 해당 구간의 전사를 직접 입력할 수 있습니다. 이때 전사 출처는 `manual`입니다.

PC 웹에서 만든 회의와 이전에 저장된 회의는 기존 입력 방식을 사용합니다. 대본 시연은 **대본으로 체험**에서 시작합니다.

### 이미 회의를 저장했다면

Quest에서 **이전 회의 열기**로 저장된 회의를 엽니다. **이 발언으로 수정안 보기**를 누르면 저장된 수정 발언과 그때의 채택 모델을 후속 작업으로 가져옵니다. 케이블 정리대는 지정된 공통 외형 치수로 후보를 생성합니다. 원래 회의록·최종 모델은 보존되고 후속 작업에서 녹음은 시작되지 않습니다.

QR 정렬을 건너뛰어도 Quest가 찾은 책상이나 직접 지정한 상판 위에서 실제 크기로 비교할 수 있습니다. 상판을 확보할 때까지 패널에서 발화와 후보의 설명을 확인할 수 있습니다. **A·B·C**에서 같은 발언을 어떻게 다르게 반영했는지 확인하고 원하는 모델을 채택하세요. 실물 위에 겹쳐 보려면 메뉴에서 정렬을 시작합니다.

## Unity 실행과 검증

Unity 6000.4.0f1과 Android Build Support를 설치합니다. Quest는 USB 디버깅을 승인한 뒤 연결합니다. 앱 식별자는 `org.contextpin.relay`입니다.

```sh
python3 scripts/relay.py desktop
python3 scripts/relay.py simulator
python3 scripts/relay.py build
python3 scripts/relay.py deploy
python3 scripts/relay.py test
MEETING_COLLABORATION_SMOKE=1 python3 scripts/relay.py smoke-assembly-simulator
```

설치 시 USB로 실시간·예시 서버 포트를 모두 연결합니다. 대본 시뮬레이터 검사는 실행 중인 예시 서버를 사용합니다. 새 후보 채택 흐름은 두 터미널에서 아래처럼 검사합니다. 로컬 비전 모델을 먼저 실행하고, PCM 두 개는 16 kHz 모노 signed 16-bit little-endian 한국어 음성을 사용하세요.

```sh
EDGE_PORT=8795 EDGE_DATA_DIR=artifacts/candidate-commit-qa python3 scripts/relay.py server
# 다른 터미널
MEETING_EDGE_URL=http://127.0.0.1:8795 MEETING_CANDIDATE_COMMIT_SMOKE=1 MEETING_CAPTURE_DATA_DIR="$PWD/artifacts/candidate-commit-qa" \
  MEETING_EXPLICIT_PCM=/absolute/path/to/korean-upper-corner-request.pcm \
  MEETING_FOLLOWUP_PCM=/absolute/path/to/korean-length-request.pcm \
  python3 scripts/relay.py smoke-assembly-simulator
```

저장 전용·기존 후보 생성 회귀 검사는 등록 당시의 고정 모서리 프로필을 **명시적으로** 지정한 별도 서버에서 실행합니다.

```bash
OPENSCAD_ASSEMBLY_CONFIG="$PWD/artifacts/cable-demo/assembly.json" \
  EDGE_PORT=8794 EDGE_DATA_DIR=artifacts/explicit-qa-live python3 scripts/relay.py server
# 다른 터미널
MEETING_EDGE_URL=http://127.0.0.1:8794 MEETING_CAPTURE_ONLY_SMOKE=1 MEETING_CAPTURE_DATA_DIR="$PWD/artifacts/explicit-qa-live" \
  MEETING_EXPLICIT_PCM=/absolute/path/to/korean-design-request.pcm \
  MEETING_FOLLOWUP_PCM=/absolute/path/to/korean-length-request.pcm \
  python3 scripts/relay.py smoke-assembly-simulator
```

이 검사는 가상 컨트롤러와 입력 PCM으로 실물 중앙 자리, 오른쪽 앞의 Latest, 실제 STT 및 두 요청의 파일을 확인합니다. 두 번째 요청에 쓸 V2는 기존 수동 치수 수정·채택 기능으로 준비합니다. 비교·채택 회귀 검사는 같은 환경에서 `MEETING_CAPTURE_ONLY_SMOKE` 대신 `MEETING_EXPLICIT_SMOKE=1`을 사용합니다. 설치 파일은 `builds/ContextPinXR.apk`입니다. [공동 회의와 저장 결과](docs/live-collaboration.md) · [OpenSCAD 등록과 좌표](docs/openscad-assembly.md)

후보 생성까지 검증할 때는 위 저장 전용 검사와 같은 입력 형식으로 `MEETING_CANDIDATE_SET_SMOKE=1`을 사용합니다. 첫 PCM에는 모서리 둥글림 요청을, 두 번째 PCM에는 홈을 넓히는 요청을 넣습니다. 검사에서 V1의 고정 모서리 제한, V2의 홈 후보, Unity가 내려받은 실제 모델 파일을 대조합니다.
