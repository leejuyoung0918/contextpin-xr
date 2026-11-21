"""Open an archived live meeting as a new review from its PC-local data directory."""
import argparse
import json
import os
from pathlib import Path

from .bundle_provider import configure_pc
from .contracts import MeetingError
from .coordinator import SessionCoordinator


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--session", required=True, help="Completed source meeting ID")
    parser.add_argument("--data", type=Path, required=True, help="Existing live meeting archive")
    args = parser.parse_args()
    configure_pc(os.environ)
    try:
        archive = SessionCoordinator(args.data, exclusive_archive=True)
        try:
            result = archive.open_archived_review(args.session)
        finally:
            archive.close()
    except (MeetingError, OSError, ValueError) as error:
        parser.exit(1, json.dumps({"status": str(error)}, ensure_ascii=False) + "\n")
    print(json.dumps(result, ensure_ascii=False))
    return 1 if result["status"] == "failed" else 0


if __name__ == "__main__":
    raise SystemExit(main())
