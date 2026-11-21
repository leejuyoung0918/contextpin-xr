# OpenSCAD 조립체 데모

회의의 연속 녹음, 공간 요약과 회의 전체 ZIP은 [공간 설계 회의](spatial-meeting.md)에서 확인할 수 있습니다.

고정한 조립체의 물체 2를 실제 크기로 맞춘 뒤, 결합부를 그대로 유지한 외형 후보를 같은 위치에서 전환합니다. PC 한 대가 OpenSCAD, 파일 제공, 입력과 기록을 처리합니다. Unity 한 대를 연결하면 같은 파일을 반투명하게 볼 수 있습니다.

## 실행

[OpenSCAD](https://openscad.org/downloads.html)와 Python 3.11 이상을 설치합니다. macOS의 기본 앱 경로 또는 PATH의 `openscad`를 찾습니다. 별도 위치에 설치했다면 `OPENSCAD_EXECUTABLE`에 실행 파일의 전체 경로를 지정합니다.

```sh
python3 -m venv artifacts/meeting-venv
artifacts/meeting-venv/bin/python -m pip install -r backend/meeting/requirements.txt
artifacts/meeting-venv/bin/python -m backend.meeting assembly
```

[PC 화면](http://127.0.0.1:8788/meeting/)에서 회의를 시작합니다. 기본 설정은 **예시 조립체**를 사용합니다. 실제 앱과 같은 경로에서 OpenSCAD가 STL을 생성하지만 이 예시의 파라미터 해석은 **REPLAY**로 표시합니다.

Unity에서는 별도 터미널에서 `python3 scripts/relay.py desktop` 또는 `python3 scripts/relay.py simulator`를 실행합니다. 홈의 **3D 설계 회의**에서 PC에 표시된 코드로 참가합니다. USB로 Quest를 연결하려면 `python3 scripts/relay.py build`, `python3 scripts/relay.py deploy`를 사용합니다. PC와 Quest의 8788 연결은 `adb reverse tcp:8788 tcp:8788`로 확인할 수 있습니다.

## 시연 순서

1. **원본 모델**을 표시합니다. 조립체와 기준판을 고정하고, Unity에서 기준 원점, 기준 +X, 기준 +Y를 순서대로 지정합니다. 반투명 원본을 실물과 비교하고 일치 여부를 직접 확인합니다.
2. PC의 치수 편집에서 외형 모서리 반경을 바꿉니다. OpenSCAD가 새 STL을 만드는 동안 이전 모델이 표시됩니다. 후보를 열어 같은 장착 위치에 머무는지 확인합니다.
3. **예시 발화 넣기** 또는 직접 입력을 사용합니다. “물체 2를 좀 더 곡선적으로 해보자. 연결 부분은 그대로.”에 대해 모서리 라운딩, 옆면 굴곡, 윗면 단면 변경이 실제로 다른 외형으로 나타납니다.
4. 원본과 후보를 번갈아 확인합니다. 얇아진 후보에서는 실제 물체의 일부가 반투명 외곽 밖으로 보일 수 있습니다. 후보를 연 뒤 “이 후보에서 조금 더”를 입력하여 같은 안을 추가 수정할 수 있습니다.
5. 채택할 후보를 선택하고 변경 이유를 기록합니다. 내보낸 ZIP의 `object_2.scad`, `parameters.json`, `model.stl`, `preview.glb`, `assembly_meeting.json`이 같은 버전에 속합니다. 의결 결과는 선택한 기존 파일을 참조합니다.

기준판이나 조립체가 움직이거나 기기 추적이 끊기면 정렬을 다시 확인합니다. 직접 정렬 여부와 추적 상태를 화면에서 확인합니다. 브라우저의 3D 화면은 형상과 단위를 확인하는 경로입니다. 장착 위치를 실제 조립체와 비교하려면 Quest에서 원본을 보며 확인합니다.

## 실제 부품으로 교체

[예시 설정](../backend/meeting/openscad_sample/assembly.json)을 로컬 작업 폴더에 복사하고 아래 항목을 채웁니다.

| 설정 | 값 |
| --- | --- |
| `display_name`, `object_1.display_name`, `object_2.display_name`, `is_demo` | 시연에서 쓰는 이름과 예시 여부 |
| `source_file`, `source_files` | 설정 파일과 같은 프로젝트 폴더 기준의 SCAD 원본과 참조 파일 |
| `profile.parameters`, `profile.edit_capabilities` | 상단에 선언된 외형 변수의 정확한 이름, 기본값, 타입, 단위와 허용 범위 |
| `mounting.position_m`, `mounting.rotation_xyzw` | Unity 수동 기준 프레임에서 물체 2의 CAD 원점으로 가는 미터 단위 이동과 XYZW 단위 쿼터니언 |
| `coupling_guard`, `preserved_conditions` | 물체 2의 원본 OpenSCAD 좌표계에서 고정 결합부를 포함하는 검사 영역과 유지 조건 |

SCAD에서 구멍과 슬롯을 깎는 형상, 지지 영역, 장착 기준은 고정 모듈 또는 고정 좌표에 둡니다. 외형 변수는 고정 영역 밖의 모듈에서만 사용하고, 변수 범위를 승인할 때 양 끝과 대표 조합을 확인합니다. 예시의 검사 축은 OpenSCAD Z입니다. 실제 부품에 다른 축이 맞으면 설정을 조정합니다.

```sh
artifacts/meeting-venv/bin/python -m backend.meeting assembly --config /path/to/assembly.json
```

원본 STL의 Z-up mm 기하를 기존 GLB용 Y-up m로 변환합니다. 변환 중 원점과 방향을 보존합니다. 후보마다 중심을 맞추거나 화면에서 크기를 새로 맞추지 않습니다. Unity에서 수동 기준에 장착 변환을 합성합니다.

## 짧은 PC 마이크 입력

실제 전사와 계획 생성을 사용할 경우 `AI_MODE=live`, `AI_API_KEY`, `AI_TRANSCRIPTION_MODEL`, `AI_PLANNER_MODEL`을 PC에 설정합니다. OpenAI 호환 서버의 주소가 필요하면 `AI_BASE_URL`을 사용합니다. 키 없이 직접 매개변수를 바꾸거나 예시 발화를 사용할 수 있습니다. 라이브 호출이 실패하면 기존 모델을 유지하고 수동 편집을 사용할 수 있습니다. 예시로 자동 전환되지는 않습니다.

## 검증 범위

실행 확인에는 OpenSCAD STL과 같은 메시에서 만든 GLB, 100mm 기준, 고정 결합 영역 비교, 후보 선택과 기록·ZIP 해시, 늦은 작업과 실패 처리가 포함됩니다.

```sh
artifacts/meeting-venv/bin/python -m pip install httpx==0.28.1
artifacts/meeting-venv/bin/python -m unittest discover -s tests/meeting -p test_assembly.py -v
python3 scripts/relay.py smoke-assembly
python3 scripts/relay.py smoke-assembly-simulator
```

실제 다른 물체와의 간섭, 제작 오차, 맞음새, 강도는 별도의 실물 검증 범위입니다.
