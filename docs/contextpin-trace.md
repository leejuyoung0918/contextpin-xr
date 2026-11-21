# ContextPin Trace · 회로 실습

**교수의 설명을 실물 회로의 위치에 연결해, 학생이 자기 보드에서 한 단계씩 따라 하는 XR 실습 도구입니다.** 현재 구현인 **ContextPin Trace**는 Unity와 Meta Quest 3를 사용합니다. 교수는 PC 브라우저에서 단계와 근거를 확인해 가이드를 승인하고, 학생은 연결할 위치를 보면서 필요할 때 원음과 전사를 다시 엽니다.

[120초 실물 데모 순서](../DEMO.md) · [Quest 실기 검증표](../docs/quest-validation.md) · [브레드보드 마커 인쇄 파일](../output/board/ContextPin-30row-marker-sheet.svg)

## 빠른 시작 · 장비 없이 LED 실습 체험

macOS에 Python 3와 **Unity 6000.4.0f1**을 설치한 뒤, 저장소 루트에서 실행하세요. 예시에는 초안, 합성 음성, 발화 원문이 포함되어 있어 **Quest나 AI 키 없이** 실행할 수 있습니다.

```sh
python3 scripts/relay.py desktop
```

`Start Desktop.command`를 열어도 됩니다. Unity가 이미 열려 있다면 Play를 멈추고 **ContextPin → 2. Desktop Preview**를 선택하세요.

1. **교수 승인** · **LED 예시 시연 → 2 저항 연결**에서 둘째 위치를 `A1`로 지정하고 **검토 완료 · 가이드 승인**을 누릅니다. **시연 근거 열기**에서 같은 단계의 발화와 예시 음성을 확인할 수 있습니다.
2. **오배선 복구** · **시뮬레이션 시작**에서 전원이 꺼졌음을 확인합니다. 저항을 `+1 → C1`에 연결하면 가상 보드가 `A1`을 안내합니다. **예시 설명 다시 듣기**로 원래 안내를 연 뒤 `+1 → A1`에 연결합니다.
3. **점등 확인** · LED 양극을 `A2`, 음극을 `B1`에 연결하고 점퍼선을 `B2 → −1`에 꽂습니다. **전원 차단 상태로 배선 확인 → 가상 전원 켜고 점등 확인**에서 결과를 봅니다. 중간에 나갔다면 **기록 재생 → 실습 이어가기**로 돌아옵니다.

시뮬레이션은 전기 노드와 LED 방향, 이미 사용한 구멍을 검사합니다. 실물 모드에서는 교사나 학생이 배선과 점등을 직접 확인하고, 앱이 확인 출처를 기록합니다.

## 현재 구현과 접근성 확장

| 흐름 | 현재 할 수 있는 일 |
| --- | --- |
| 시연 기록 | 학생이 Quest를 착용하고 음성을 기록합니다. 학생이 장면을 저장하거나 교수가 PC에서 촬영을 요청할 수 있습니다. |
| 교수 검토 | 전사, 원음, 사진을 보며 위치와 극성을 정합니다. AI 키 없이 수동 전사를 입력할 수 있습니다. 수정은 새 승인 버전으로 저장됩니다. |
| 학생 실습 | 학생 보드를 따로 등록해 현재 단계의 작업 위치를 표시합니다. **잘 모르겠어요**를 눌러 근거를 다시 열고 교수 PC에 도움을 요청할 수 있습니다. |
| 기록 재생 | 저장된 승인본과 진행을 다시 엽니다. 오프라인에서도 기기에 저장한 자료를 열 수 있습니다. 실물 위치는 재실행 후 다시 맞춥니다. |

접근성 확장에서는 교수의 설명을 실제 물체에 연결한 뒤 학생에게 맞는 방식으로 전달하는 방향을 제안합니다. **농·난청 학생을 위한 보드 옆 실시간 자막**과 **시각장애 학생을 위한 공간 음성·진동 안내**가 다음 검증 대상입니다. 현재 체험에서는 단계별 텍스트 안내와 녹음·전사 근거를 다시 볼 수 있습니다.

## Quest 3에서 실행

### 설치와 교수 PC 연결

1. Unity Hub에서 **Unity 6000.4.0f1**과 해당 버전의 **Android Build Support**, **Android SDK & NDK Tools**, **OpenJDK**를 설치합니다. Quest에서 개발자 모드를 켜고 데이터 전송용 USB로 Mac에 연결한 뒤, 헤드셋에서 USB 디버깅을 허용합니다.
2. 아래 명령으로 PC relay를 실행하고 [교수 검토 화면](http://127.0.0.1:8787/teacher)을 엽니다. APK는 `build` 명령으로 로컬에서 생성합니다. 다른 터미널에서 `build`와 `deploy`를 순서대로 실행하세요.

   ```sh
   python3 scripts/relay.py server
   ```

   ```sh
   python3 scripts/relay.py build
   python3 scripts/relay.py deploy
   ```

3. Quest의 **실물 기록**에서 녹음·저장 범위를 확인하고 동의합니다. 헤드셋에 표시된 페어링 코드를 PC에 입력하세요. 코드는 공개 화면에서 가립니다.

`deploy`는 `builds/ContextPinTrace.apk`를 설치하고 앱을 연 뒤에도 사용할 수 있도록 USB 포트 전달을 설정합니다. 이미 생성한 APK로 설치하려면 `Install on Quest.command`를 사용할 수 있습니다. 소스를 바꿨다면 다시 빌드하세요. 여러 기기가 연결되어 있으면 `python3 scripts/relay.py deploy --serial 기기번호`로 선택하고, USB를 다시 연결한 뒤 relay에 닿지 않으면 `deploy`로 포트 전달을 복원합니다. PC relay는 기본적으로 `127.0.0.1:8787`에서 대기합니다.

컨트롤러를 사용할 때는 **조준선을 버튼에 맞추고 검지 트리거**를 누릅니다. 손 추적 모드에서는 화면 중앙으로 조준하고 검지를 핀치합니다. 보드의 물리적 위치를 잡는 기준점 등록에는 아래의 손동작을 사용합니다.

### 선택 설정 · 라이브 전사와 AI 보조

수동 전사로 시연하려면 위 명령만 실행하면 됩니다. AI를 사용할 때는 [환경 변수 예시](../backend/.env.example)를 `backend/.env`로 복사하고 **PC에만** 값을 설정한 뒤 서버를 다시 실행하세요.

- **음성 전사** · `AI_API_KEY`와 WAV 전사를 지원하는 `AI_TRANSCRIPTION_MODEL`을 설정합니다. 음성은 짧은 WAV 구간으로 나눠 전사하며 구간 내 개별 문장 시각은 추정값입니다.
- **초안 생성과 사진 비교** · 이미지 입력과 JSON 응답을 지원하는 `AI_VISION_MODEL`을 설정합니다. 사진 비교 결과는 눈에 보이는 부품 후보를 보여 줍니다. 단자 접촉, LED 극성, 전기적 연속성은 실물에서 직접 확인합니다.
- **공급자 설정** · 기본 주소는 `https://api.openai.com/v1`입니다. OpenAI 호환 제공자를 쓸 때는 `AI_BASE_URL`을 지정하세요.

설정한 기능에 따라 음성과 선택한 이미지가 해당 AI 공급자에게 전송됩니다. 참가자의 동의를 받은 자료만 기록하고 보관하세요.

### 실물 브레드보드 맞추기

이 데모는 **30개 번호 줄, 2.54 mm 피치, A–E·F–J 스트립과 중앙 홈**을 갖춘 `led-breadboard-v1` 프로필을 사용합니다. 실물 보드의 `A1`과 `C1`은 같은 줄의 왼쪽 전기 스트립입니다. 빠른 시작의 가상 보드에서는 두 위치가 서로 다른 노드로 설정돼 있습니다.

1. 교수가 전원을 끄고 사용하는 보드의 간격, 홈, 전원 레일의 연속성과 분할을 확인합니다. 사용할 양극 레일 두 구멍을 `+1`·`+2`, 접지 레일 두 구멍을 `−1`·`−2`로 표시하세요. 같은 레일의 두 구멍은 모두 같은 근단 구간에 있어야 합니다. 전압, 전류 제한, 저항, LED는 교수가 실제 키트에서 확인한 조건을 사용하세요.
2. 보드를 받침에 고정하고 **교사가 확인한 보드로 등록**을 누릅니다. 오른쪽 검지 끝을 `A1 → J1 → A30 → J30 → F15`에 차례로 대면서 각 지점에서 왼손을 핀치합니다. 홀 중심 기준 간격은 A1–J1 27.94 mm, A1–A30 73.66 mm, 중앙 홈 E–F 7.62 mm입니다.
3. 앱이 처음 세 기준점으로 배치와 방향을 맞추고, 뒤의 두 기준점으로 정렬을 확인합니다. 보드와 표시가 맞으면 **보드 일치 확인 · 계속**을 누르세요. 안내는 스트립·영역 수준이며 실기 오차는 [검증표](../docs/quest-validation.md)의 방법대로 따로 측정합니다.

<details>
<summary>ArUco 마커로 정렬 후보 찾기</summary>

[마커 파일](../output/board/ContextPin-30row-marker-sheet.svg)을 **100% 배율**로 인쇄하세요. 검은 마커 두 변이 각각 26 mm인지, 인쇄된 50 mm 눈금이 실측과 일치하는지 자로 확인합니다. 인쇄물의 A1·J1·A30 기준에 보드를 맞추고 마커 표면과 홀 입구를 같은 높이로 고정합니다.

```sh
python3 -m pip install --target artifacts/aruco-python opencv-python-headless==4.11.0.86
PYTHONPATH="$PWD/artifacts/aruco-python" python3 scripts/relay.py server
```

PC 연결과 카메라가 준비되면 **마커 사진으로 위치 찾기**에서 두 마커를 촬영합니다. 후보가 나오면 `J30`과 `F15`를 직접 짚어 확인하세요. 사진이나 인쇄 상태가 좋지 않으면 손으로 다섯 기준점을 등록할 수 있습니다.

</details>

### 기록 → 승인 → 학생 실습

1. **교수 시연 기록** · Quest에서 마이크와 RGB 카메라 권한을 허용합니다. 교수의 설명 중 **연결 전**, **연결 후**, **모호한 부분**을 골라 **이 장면 저장**을 누르면 사진이 근거로 저장됩니다. PC의 **헤드셋에 촬영 요청**도 같은 방식으로 동작합니다.
2. **교수 가이드 승인** · **기록 종료 · 실습 초안 만들기**를 누른 뒤 PC에서 발화와 원음, 단계와 위치를 검토합니다. 전사를 수정하거나 보충할 때는 **수동 전사 또는 교수 보충 추가**를 사용하세요. 위치, 극성, 키트를 확인하고 **가이드 승인 요청**을 누릅니다. 이후 변경은 **새 버전 편집**에서 진행합니다.
3. **학생 보드에서 이어 하기** · **학생 실습 → 실물 보드에서 시작**에서 학생 보드를 맞춥니다. 같은 프로필이면 다른 위치에 놓인 보드에도 단자 ID를 기준으로 안내가 나타납니다. 막힌 단계에서 **잘 모르겠어요**를 열거나 PC에 도움을 요청하고, 배선을 직접 확인한 뒤 **연결했어요**를 누릅니다. 보드를 옮기거나 추적이 끊기면 **보드 다시 맞추기**로 복구하세요.
4. **마무리** · 전원이 꺼진 상태에서 배선을 점검한 다음 별도로 점등을 관찰합니다. **이전 단계 → 현재 단계로 복귀**로 근거를 다시 보고, **기록 재생 → 실습 이어가기**로 저장한 진행을 엽니다.

## 미리보기와 검증

```sh
python3 scripts/relay.py doctor           # 설치된 도구, ADB, 로컬 서버와 모델 설정
python3 scripts/relay.py test             # Python 백엔드와 C# 코어 검사
python3 scripts/relay.py smoke            # 데스크톱에서 기존 복습, LED 실습, 진행 복원
python3 scripts/relay.py smoke-simulator  # Meta XR Simulator의 양안 렌더링 및 XR 입력
```

C# 검사는 .NET 9 SDK를 사용합니다. XR 미리보기는 [Meta XR Simulator](https://developers.meta.com/horizon/documentation/unity/xrsim-intro/)를 설치하고 `python3 scripts/relay.py simulator` 또는 `Start Meta Simulator.command`로 실행하세요. 열린 Unity에서는 Play를 멈춘 뒤 **ContextPin → 3. Meta XR Simulator Preview**를 선택합니다. 시뮬레이터에서 마우스를 쓰려면 오른손 입력을 **Controller**로 바꾸고 **Point & click**과 **Compatibility mode**를 켭니다. **ISDK pointer offset**을 끄고 **Action input → Right - Controller**를 선택한 뒤 버튼을 클릭하세요. 데스크톱에서는 `R` 키로 화면을 다시 중앙에 놓을 수 있습니다.

개발 과정에서 **Python·C# 검사, 데스크톱·XR 시뮬레이터, 로컬 교수 화면, ARM64 APK 빌드**를 확인했습니다. 최신 빌드의 실기 컨트롤러 조작, 실제 센서와 보드 정합, 라이브 STT·Vision, 학습자 파일럿은 [실기 검증표](../docs/quest-validation.md)의 확인 범위에 남아 있습니다. 로컬 실행으로 생성한 검사 보고서와 로그는 `artifacts/`에서 확인할 수 있습니다.

<details>
<summary>기존 회로 지점 복습 체험</summary>

홈에서 **기존 복습 시연 → 설명 기록 시작**을 선택합니다. 첫 발화에서 **IN 입력 단자**, 두 번째 발화에서 **AMP 증폭단 출력**을 선택합니다. 세 번째 발화는 핀치를 건너뛰고 기록을 종료합니다. 검토 화면에서 재현 장면과 원음을 확인한 뒤 **접지 지점 연결** 또는 **위치 미확인**을 선택하세요. **학생 B 복습 시작**에서 발화 순서대로 내용을 다시 열고, **안내 끄고 위치 찾기**에서 첫 시도와 최종 결과를 확인합니다.

실제 회로에서 이 흐름을 사용할 때는 PC relay를 실행하고 **복습용 현장 기록**에서 참가자 동의를 확인합니다. Quest에서 입력, 증폭단 출력, 접지, 최종 출력 네 곳을 등록하고 마이크와 카메라 권한을 허용하세요. 종료 후에는 원문, 핀치, 사진을 확인하여 위치를 확정하거나 미확인으로 남깁니다. 학습자가 먼저 확인할 지점을 **과제 지점 지정**으로 고를 수 있습니다. 기준 보드 예시는 [trace-board.json](ContextPinRelay/Assets/Resources/trace-board.json)에 있습니다.

</details>

## 소스와 기록

| 경로 | 용도 |
| --- | --- |
| [ContextPinRelay/](../ContextPinRelay) | Unity 프로젝트, Quest·데스크톱 UI, 실습 상태와 공간 정렬 |
| [backend/](../backend) | Python 로컬 relay, 교수 검토 UI, 전사 및 선택적 시각 모델 연동 |
| [scripts/](../scripts) · [tests/](../tests) | 실행·빌드·설치 도구와 C# 핵심 로직 검증 |
| [DEMO.md](../DEMO.md) · [docs/](../docs) | 실물 시연 순서와 검증 체크리스트 |

PC 기록은 기본적으로 `artifacts/relay-sessions`에 남으며 `RELAY_DATA_DIR`로 바꿀 수 있습니다. 교수 화면의 **세션 보고서 다운로드**를 사용하거나 다음 명령으로 기록을 내보낼 수 있습니다.

```sh
python3 scripts/relay.py export
python3 scripts/relay.py report --relay-data artifacts/relay-sessions --output artifacts/transfer-report.json
python3 scripts/relay.py report --archive 'artifacts/device-data/내보낸-폴더명' --output artifacts/device-report.json
```

집계 보고서는 촬영 요청·저장·업로드 수, 관찰된 지연, 자료와 단계 확인의 출처를 기록합니다. 원문·개인 음성·사진·페어링 토큰은 집계에 복제하지 않습니다. 예시 실행에는 재현 이미지와 한국어 합성 음성을 사용합니다. 동봉한 NanumGothic 글꼴은 [SIL Open Font License](../ContextPinRelay/Assets/ContextPin/Fonts/OFL.txt)를 따릅니다.
