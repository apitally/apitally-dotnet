#!/usr/bin/env python3
"""Build and run the isolated matrix with process-group timeouts."""
import os
from pathlib import Path
import signal
import subprocess
import sys

ROOT = Path(__file__).resolve().parent
RESULTS = ROOT / "results"
RESULTS.mkdir(exist_ok=True)
ENV = {key: value for key, value in os.environ.items() if not key.startswith("OTEL_")}
ENV["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
ENV["DOTNET_NOLOGO"] = "1"


def run(name, arguments, timeout):
    print("+ " + " ".join(arguments), flush=True)
    with subprocess.Popen(arguments, cwd=ROOT, env=ENV, start_new_session=True,
                          stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True) as process:
        try:
            output, _ = process.communicate(timeout=timeout)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            output, _ = process.communicate()
            output += "\nFAIL process-group timeout\n"
        (RESULTS / (name + ".txt")).write_text(output, encoding="ascii")
        print(output, end="", flush=True)
        if process.returncode:
            sys.exit(process.returncode if process.returncode > 0 else 1)


run("sdk", ["dotnet", "--version"], 10)
run("restore", ["dotnet", "restore", "ProviderRegistration.csproj", "--locked-mode", "--disable-parallel"], 180)
run("build", ["dotnet", "build", "ProviderRegistration.csproj", "-c", "Release", "--no-restore", "--disable-build-servers"], 180)
for framework in ["net8.0", "net9.0", "net10.0"]:
    run(framework, ["dotnet", str(ROOT / "bin" / "Release" / framework / "ProviderRegistration.dll")], 120)
