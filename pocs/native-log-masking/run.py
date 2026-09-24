#!/usr/bin/env python3
"""Bounded native callback probe. No telemetry endpoints or credentials."""
import os
from pathlib import Path
import re
import subprocess

root = Path(__file__).resolve().parent
results = root / "results"
results.mkdir(exist_ok=True)
environment = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
frameworks = {"net8.0": "8.0.13", "net9.0": "9.0.2", "net10.0": "10.0.9"}


def run(name, arguments, timeout=120, negative=False):
    print("$ " + " ".join(arguments), flush=True)
    result = subprocess.run(arguments, cwd=root, env=environment, text=True,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=timeout)
    (results / (name + ".log")).write_text(result.stdout)
    print(result.stdout, end="", flush=True)
    if negative:
        codes = set(re.findall(r"error (CS\d+):", result.stdout))
        assert result.returncode != 0 and codes == {"CS1729"}, result.stdout
        assert "'LogRecord'" in result.stdout and "0 arguments" in result.stdout
        print("PASS expected negative API probe: no public parameterless LogRecord constructor")
    else:
        assert result.returncode == 0, result.stdout
    return result.stdout


assert run("sdk", ["dotnet", "--version"], timeout=30).strip() == "10.0.301"
run("runtimes", ["dotnet", "--list-runtimes"], timeout=30)
run("tool-restore", ["dotnet", "tool", "restore"])
assert run("formatter-version", ["dotnet", "csharpier", "--version"], timeout=30).strip() == "1.3.0"
run("format", ["dotnet", "csharpier", "check", "."])
run("restore", ["dotnet", "restore", "NativeLogMasking.csproj", "--locked-mode"])
for framework in frameworks:
    run("unsupported-" + framework,
        ["dotnet", "build", "NativeLogMasking.csproj", "--no-restore", "--nologo",
         "--framework", framework, "-p:DefineConstants=UNSUPPORTED_PUBLIC_CONSTRUCTOR"], negative=True)
run("build", ["dotnet", "build", "NativeLogMasking.csproj", "--no-restore", "--nologo"])
for framework, runtime in frameworks.items():
    output = run(framework,
                 ["dotnet", "--fx-version", runtime, "--roll-forward", "Disable",
                  "bin/Debug/" + framework + "/NativeLogMasking.dll"], timeout=60)
    assert "Runtime: .NET " + runtime + ";" in output
    assert "all providers and batch workers disposed" in output
print("PASS all three exact runtimes, format check, locked restore, and expected constructor limitations")
