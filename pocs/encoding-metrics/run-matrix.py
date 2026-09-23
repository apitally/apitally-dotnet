#!/usr/bin/env python3
"""Finite, local-only POC matrix. Build/runtime caches stay in this directory."""
import hashlib
import os
from pathlib import Path
import subprocess
import zlib

root = Path(__file__).resolve().parent
results = root / "results"
results.mkdir(exist_ok=True)
for name, value in {
    "DOTNET_CLI_HOME": str(root / ".local/dotnet"),
    "NUGET_PACKAGES": str(root / ".local/packages"),
    "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
    "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
    "DOTNET_NOLOGO": "1",
    "MSBUILDDISABLENODEREUSE": "1",
}.items():
    os.environ[name] = value


def run(name, command):
    print("$ " + " ".join(command), flush=True)
    process = subprocess.run(command, cwd=root, text=True, stdout=subprocess.PIPE,
                             stderr=subprocess.STDOUT, timeout=180)
    (results / f"{name}.log").write_text("$ " + " ".join(command) + "\n" + process.stdout)
    print(process.stdout, end="", flush=True)
    process.check_returncode()
    return process.stdout


for line in (root / "vendor/SHA256SUMS").read_text().splitlines():
    expected, name = line.split("  ", 1)
    assert hashlib.sha256((root / "vendor" / name).read_bytes()).hexdigest() == expected

sdks = run("installed-sdks", ["dotnet", "--list-sdks"])
run("installed-runtimes", ["dotnet", "--list-runtimes"])
assert run("default-sdk", ["dotnet", "--version"]).strip() == "10.0.301"
run("restore", ["dotnet", "restore", "EncodingMetrics.csproj", "--locked-mode"])
run("default-build", ["dotnet", "build", "EncodingMetrics.csproj", "--no-restore", "--no-incremental"])
for framework in ("net8.0", "net9.0", "net10.0"):
    run(f"default-{framework}", ["dotnet", "run", "--project", "EncodingMetrics.csproj", "-f", framework, "--no-build", "--no-restore"])

for version, framework in (("8.0.406", "net8.0"), ("9.0.200", "net9.0")):
    line = next(line for line in sdks.splitlines() if line.startswith(version + " "))
    sdk = Path(line.split("[", 1)[1].rstrip("]")) / version / "dotnet.dll"
    run(f"sdk-{version}-build", ["dotnet", str(sdk), "build", "EncodingMetrics.csproj", "-f", framework, "--no-restore", "--no-incremental"])
    run(f"sdk-{version}-run", ["dotnet", str(root / "bin/Debug" / framework / "EncodingMetrics.dll")])

verified = []
for path in sorted((root / "artifacts").glob("net*/*.gz")):
    compressed = path.read_bytes()
    decoder = zlib.decompressobj(wbits=31)
    decoded = decoder.decompress(compressed) + decoder.flush()
    assert decoder.eof and not decoder.unused_data and not decoder.unconsumed_tail
    assert len(decoded) <= 4_000_000 and len(compressed) <= 4 * 1024 * 1024
    verified.append(f"{path.relative_to(root)}: one gzip member, raw={len(decoded)}, stored={len(compressed)}")
assert len(verified) == 21
report = "\n".join(verified) + "\nPASS: checksums, SDK/runtime matrix and independent single-member gzip validation\n"
(results / "gzip-verification.log").write_text(report)
print(report)
