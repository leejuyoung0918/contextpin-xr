# ContextPin XR · 휴대폰 거치대 설계 회의

A4 중앙에 놓인 실물 크기의 휴대폰 거치대가 현재 채택본 **Latest**입니다. 수정 내용을 말한 뒤 해당 부위를 가리키면 현장 PC가 등록된 치수를 바꿔 OpenSCAD 후보를 만듭니다. 종이 왼쪽의 A와 오른쪽의 B를 비교해 하나를 채택하면 중앙 모델이 같은 자리에서 다음 버전으로 바뀝니다. [Quest 시연 순서](DEMO.md)

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

**회의 종료 → PC에서 회의록 받기**로 결과 ZIP을 엽니다. `cad/final.scad`와 `cad/final.stl`은 채택한 모델 파일입니다. `cad/parameters.json`, `cad/version.json`, `decisions.json`, `transcript.jsonl`에서 적용 치수, 버전, 선택 기록과 발화를 확인합니다. 선택 이유를 입력하지 않으면 `not_stated`로 남깁니다. [결과물 저장 코드](backend/meeting/meeting_result.py)

현장 PC에서는 `artifacts/phone-stand-live/models/<버전 ID>/` 아래에 적용된 `candidate.scad`, `print.stl`, `preview.glb`를 보관합니다. `edit_requests/<회의 ID>/<요청 ID>/`에는 발화 원음, 확정 전사, 이미지, Ray 선택 근거와 후보 세트를 저장합니다. 이미지 출처는 Quest RGB 촬영이면 `quest_rgb`, 렌더 화면이면 `rendered_debug`입니다.

## 검증 실행

`python3 scripts/relay.py test`로 회귀 검사를 실행합니다. 거치대 형상 검사만 실행하려면 아래 명령을 사용합니다. OpenSCAD가 PATH에 없으면 로컬 실행 파일을 `OPENSCAD_EXECUTABLE`로 지정합니다.

```sh
artifacts/meeting-venv/bin/python -m unittest -v \
  backend.meeting.test_openscad_geometry.OpenScadGeometryTests.test_phone_stand_exports_keep_the_base_origin_and_exact_scale
```

Meta XR Simulator의 두 번 채택 검사는 운영 회의와 분리한 테스트 서버를 사용합니다. 로컬 모델을 실행하고 별도 터미널에서 같은 거치대 프로필로 QA 서버를 시작합니다.

```sh
EDGE_PORT=18896 EDGE_DATA_DIR="$PWD/artifacts/phone-stand-qa" python3 scripts/relay.py server
```

Mac의 한국어 음성과 FFmpeg가 있으면 두 발화를 시험 입력으로 준비할 수 있습니다. 이 입력은 가상 컨트롤러와 함께 쓰는 시뮬레이터용 음성입니다.

```sh
mkdir -p artifacts/phone-smoke-audio
say -v Yuna -r 155 -o artifacts/phone-smoke-audio/angle.aiff '등받이 각도를 조금 줄여 주세요.'
say -v Yuna -r 155 -o artifacts/phone-smoke-audio/lip.aiff '앞쪽 받침턱의 높이를 조금 늘려 주세요.'
for clip in angle lip; do
  ffmpeg -v error -y -i "artifacts/phone-smoke-audio/$clip.aiff" -af apad=pad_dur=0.35 \
    -ar 16000 -ac 1 -f s16le "artifacts/phone-smoke-audio/$clip.pcm"
done
MEETING_EDGE_URL=http://127.0.0.1:18896 MEETING_CAPTURE_DATA_DIR="$PWD/artifacts/phone-stand-qa" \
  MEETING_CANDIDATE_COMMIT_SMOKE=1 MEETING_PHONE_STAND_SMOKE=1 \
  MEETING_EXPLICIT_PCM="$PWD/artifacts/phone-smoke-audio/angle.pcm" \
  MEETING_FOLLOWUP_PCM="$PWD/artifacts/phone-smoke-audio/lip.pcm" \
  python3 scripts/relay.py smoke-assembly-simulator
```

## 기존 케이블 정리대 데모

케이블 정리대 안정 버전은 `main`의 체크포인트 `9b46ace754246747388e5dd1f7cc285638efc47b`입니다. 해당 버전의 실시간 서버는 8789, Quest 패키지는 `org.contextpin.relay`입니다. 이 브랜치에서도 8788의 기존 케이블 대본을 실행할 수 있습니다. [케이블 대본 실행 순서](DEMO.md#기존-케이블-정리대-대본)
