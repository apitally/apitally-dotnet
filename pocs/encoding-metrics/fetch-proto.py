#!/usr/bin/env python3
"""Fetch only the pinned OTLP schemas needed by this experiment."""
import hashlib
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parent
commit = "790608c4d51e6ffc12210b541e8514cbed9e91a4"
files = ["LICENSE"] + [
    f"opentelemetry/proto/{name}"
    for name in (
        "common/v1/common.proto", "resource/v1/resource.proto",
        "trace/v1/trace.proto", "metrics/v1/metrics.proto", "logs/v1/logs.proto",
        "collector/trace/v1/trace_service.proto",
        "collector/metrics/v1/metrics_service.proto",
        "collector/logs/v1/logs_service.proto",
    )
]
checksums = []
for name in files:
    data = subprocess.check_output([
        "gh", "api", "-H", "Accept: application/vnd.github.raw+json",
        f"repos/open-telemetry/opentelemetry-proto/contents/{name}?ref={commit}",
    ], timeout=60)
    path = root / "vendor" / name
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    checksums.append(f"{hashlib.sha256(data).hexdigest()}  {name}")
(root / "vendor" / "SHA256SUMS").write_text("\n".join(checksums) + "\n")
print(f"Fetched {len(files)} files from opentelemetry-proto v1.11.0 ({commit})")
