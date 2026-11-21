# 3D 회의 모델 등록과 출력 검증

회의를 시작할 때 ObjectProfile이 편집 가능한 부위와 수치 범위를 정의합니다. ModelAdapter는 등록된 수치로 실제 형상을 생성하고 치수를 측정합니다. 회의 상태와 발화, 참가자 응답, 최종 결정은 기존 모델 생성 코드와 독립적으로 관리됩니다.

## 다른 물체 등록하기

1. `backend/meeting/model_adapters/`에 새 JSON 프로필을 만듭니다. [조형 블록 프로필](../backend/meeting/model_adapters/rounded_fixture.json)에 필드가 모두 들어 있습니다. 대상마다 고유한 `object_id`, `adapter_id`, `adapter_revision`을 지정하세요. `semantic_parts`의 `part_id`는 생성 전후에 같은 부위를 뜻해야 합니다. 한국어 부위·치수의 별칭, 편집 가능한 항목과 값 범위, 실제 측정 가능한 속성을 등록합니다.
2. 같은 폴더에 Python 모듈을 추가하고 `ModelAdapter`를 상속합니다. `build(params)`에서 오른손 좌표계의 Y-up mm `MeshGeometry`를 반환하고, `measure(geometry)`에서 생성된 메시의 실제 치수를 프로필의 `property_id`에 연결합니다. 메시의 삼각형은 바깥 방향 일관성을 갖추고 안정적인 `part_id`별로 묶어야 합니다. 형상 정의가 보조 소스 파일을 사용한다면 `source_files()`를 확장해 원본 ZIP에 포함합니다. [조형 블록 어댑터](../backend/meeting/model_adapters/rounded_fixture.py), [ModelAdapter](../backend/meeting/geometry.py)
3. [프로필 등록 파일](../backend/meeting/model_adapters/__init__.py)의 `PROFILES`에 `"my_object": (".my_module", "MyAdapter", "my_object.json")`을 추가합니다. EDGE 시작 시 `ACTIVE_OBJECT_PROFILE=my_object`를 지정하면 새 회의의 기본값이 됩니다. 세션 생성 API에서는 `profile_id`로 등록된 모델을 지정할 수도 있습니다. 이전 회의의 모델 버전과 파일은 회의 생성 시의 원본·프로필과 함께 조회됩니다.
4. 기본값과 경계값으로 실제 버전을 만들어 브라우저 또는 Unity에서 부위를 고르고, 수치 변경 후 미리보기와 STL의 치수를 확인합니다. 재생 시연도 제공하려면 `demo_utterance`, `demo_ambiguity_aliases`, `demo_interpretations`에 새 물체에서 실행 가능한 발화와 서로 다른 형상 변경을 지정합니다.

등록 후 EDGE와 별개로 모델을 검사하려면 저장소 루트에서 다음 명령을 실행합니다. `my_object`를 등록한 ID로 바꿉니다.

```bash
ACTIVE_OBJECT_PROFILE=my_object python3 - <<'PY'
from backend.meeting.geometry import build_model, default_parameters, load_profile
profile = load_profile()
model = build_model("artifacts/model-check", "first-version", profile, default_parameters(profile))
print(model["measured_dimensions_mm"], model["bounds_m"])
PY
```

결과는 `artifacts/model-check/first-version/`에 저장됩니다. 다시 생성할 때는 새 버전 ID를 사용하세요. 등록한 어댑터의 소스 또는 형상 규칙을 수정할 때는 `adapter_revision`을 올리고 새 회의를 시작합니다. 생성 시점의 어댑터는 각 버전의 `adapter_source_snapshot.zip`에 저장됩니다.

## 단위와 좌표

| 파일·표시 | 단위 | 좌표계 |
| --- | --- | --- |
| 설계 수치와 `bounds_mm` | mm | 오른손, Y-up |
| `preview.glb`와 `bounds_m` | m | 오른손, Y-up |
| `print.stl`과 `stl_bounds_mm` | mm 관례 | 오른손, Z-up |

GLB의 위치만 mm에서 m로 한 번 변환합니다. STL은 같은 꼭짓점을 `(x, -z, y)`로 회전합니다. 새 어댑터에서 출력 기준면을 Y=0으로 만들면 STL의 Z=0에 배치됩니다. GLB 로더가 Unity 좌표계로 변환하며, 확대 보기와 공간 배치는 파일이나 CAD 수치에 영향을 주지 않습니다. [glTF 좌표계와 단위](https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html#coordinate-system-and-units)

예제 초기 폭은 100mm입니다. 자동 검사에서 GLB의 X 범위는 약 0.1m, STL의 X 범위는 100mm였습니다. 폭을 123.4mm로 바꾼 경우에도 두 파일의 범위가 같은 설계 수정에서 나오는지 확인했습니다. 실제 출력 전에는 슬라이서에서 mm로 불러와 `geometry_report.json`의 STL 치수와 대조합니다.

## 검증과 원본 재생성

작업 프로세스는 닫힌 표면, 연결 성분, 삼각형 방향과 면적, 양의 부피를 검사합니다. 요청 및 회의의 강제 유지 조건을 생성된 메시 치수로 재검사합니다. 내보낸 GLB·STL의 삼각형을 다시 읽고, 각각의 좌표계에서 원본과 일치할 때 결과를 등록합니다. `artifact_manifest.json`에서 버전 ID와 파일 해시를 확인할 수 있습니다.

예제는 파라메트릭 메시 엔진으로 실제 3차원 형상을 만듭니다. 원본은 어댑터 Python과 `parameters.json`으로 구성되며 예제의 STEP 제공은 미지원입니다. `geometry_report.json`에 검사한 항목과 검사 범위를 기록합니다. 자체 교차, 벽 두께, 오버행, 재료·강도·맞음새와 실제 출력은 별도 검증이 필요합니다.

`adapter_source_snapshot.zip`을 새 작업 폴더에 풀고 내보낸 `parameters.json`을 같은 폴더에 둡니다. 그 폴더에서 다음 명령으로 저장된 매개변수의 모델을 다시 생성할 수 있습니다.

```bash
python3 - <<'PY'
import json
from pathlib import Path
from backend.meeting.geometry import build_model, load_profile
profile = load_profile()
params = json.loads(Path("parameters.json").read_text(encoding="utf-8"))
model = build_model("output", "rebuilt", profile, params)
print(model["geometry_hash"])
PY
```

동봉된 `engine.json`에 생성 환경의 Python과 엔진 버전이 기록됩니다. 예제 원본 ZIP을 풀어 기준 모델을 재생성한 자동 검사에서 STL SHA-256이 일치했습니다.

```bash
python3 -m unittest backend.meeting.test_geometry -v
```
