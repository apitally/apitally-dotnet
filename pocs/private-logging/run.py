#!/usr/bin/env python3
"""Bounded local-only checks; intentionally failing API probes must fail exactly."""
import os
from pathlib import Path
import re
import subprocess

root = Path(__file__).resolve().parent
results = root / "results"
results.mkdir(exist_ok=True)
environment = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")


def run(name, arguments, timeout=120, expected_failure=False):
    print("$ " + " ".join(arguments), flush=True)
    result = subprocess.run(arguments, cwd=root, env=environment, text=True,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=timeout)
    (results / (name + ".log")).write_text(result.stdout)
    print(result.stdout, end="", flush=True)
    if expected_failure:
        codes = set(re.findall(r"error (CS\d+):", result.stdout))
        assert result.returncode != 0 and codes == {"CS0029", "CS1061"}, result.stdout
        assert "to 'string'" in result.stdout and "'EventName'" in result.stdout
        print("PASS expected negative API probe: string-only Body; no direct EventName property (use EventId.Name)")
    else:
        assert result.returncode == 0, result.stdout
    return result.stdout


run("sdk", ["dotnet", "--version"], timeout=30)
run("restore", ["dotnet", "restore", "PrivateLogging.csproj", "--locked-mode"])
for framework in ["net8.0", "net9.0", "net10.0"]:
    run("unsupported-" + framework,
        ["dotnet", "build", "PrivateLogging.csproj", "--no-restore", "--nologo",
         "--framework", framework, "-p:DefineConstants=UNSUPPORTED_PUBLIC_API"], expected_failure=True)
run("build", ["dotnet", "build", "PrivateLogging.csproj", "--no-restore", "--nologo"])
for framework in ["net8.0", "net9.0", "net10.0"]:
    output = run(framework, ["dotnet", "run", "--no-build", "--no-restore", "--framework", framework], timeout=60)
    assert "all providers and batch workers disposed" in output
print("PASS all three frameworks, including three expected compile-time limitations")
