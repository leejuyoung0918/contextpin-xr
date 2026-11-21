"""Subprocess entry point for one trusted model-adapter build."""

import json
from pathlib import Path
import sys

from .geometry import GeometryError, build_artifacts


def main():
    path = Path(sys.argv[1])
    try:
        request = json.loads(path.read_text(encoding="utf-8"))
        result = build_artifacts(request, path.parent / "files")
    except GeometryError as error:
        print(json.dumps({"error": str(error)}, ensure_ascii=True))
        return 1
    print(json.dumps(result, ensure_ascii=True, allow_nan=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
