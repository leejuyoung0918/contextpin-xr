"""Run one EDGE process, or a stateless AI Gateway."""
import argparse
import os
from pathlib import Path

import uvicorn


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("service", choices=("assembly", "edge", "gateway"), nargs="?", default="edge")
    parser.add_argument("--host")
    parser.add_argument("--config", type=Path, help="Local OpenSCAD assembly JSON")
    parser.add_argument("--port", type=int)
    parser.add_argument("--tls-cert")
    parser.add_argument("--tls-key")
    args = parser.parse_args()
    edge = args.service != "gateway"
    if args.config and args.service != "assembly":
        parser.error("--config is only for the assembly demo.")
    if args.service == "assembly":
        if args.config:
            if not args.config.is_file():
                parser.error(f"Assembly config not found: {args.config}")
            os.environ["OPENSCAD_ASSEMBLY_CONFIG"] = str(args.config.resolve())
        os.environ["ACTIVE_OBJECT_PROFILE"] = "object_2"
        os.environ.pop("AI_GATEWAY_URL", None)
        os.environ.setdefault("EDGE_DATA_DIR", str(Path(__file__).resolve().parents[2] / "artifacts" / "assembly"))
    bind = args.host or os.environ.get("EDGE_BIND_HOST" if edge else "AI_GATEWAY_BIND_HOST", "127.0.0.1")
    port = args.port or int(os.environ.get("EDGE_PORT" if edge else "AI_GATEWAY_PORT", "8788" if edge else "8789"))
    if bool(args.tls_cert) != bool(args.tls_key):
        parser.error("Provide both --tls-cert and --tls-key.")
    for path in (args.tls_cert, args.tls_key):
        if path and not Path(path).is_file():
            parser.error(f"TLS file not found: {path}")
    target = "backend.meeting.main:app" if edge else "backend.meeting.gateway:app"
    uvicorn.run(target, host=bind, port=port, ssl_certfile=args.tls_cert, ssl_keyfile=args.tls_key,
                access_log=False, workers=1, ws="websockets-sansio", ws_max_size=65_536, ws_max_queue=8,
                proxy_headers=False, limit_concurrency=48)


if __name__ == "__main__":
    main()
