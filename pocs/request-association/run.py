#!/usr/bin/env python3
"""Bounded loopback-only experiment with both shared frameworks pinned."""
import json
import os
from pathlib import Path
import signal
import subprocess

root = Path(__file__).resolve().parent
results = root / "results"
results.mkdir(exist_ok=True)
environment = {key: value for key, value in os.environ.items()
               if not key.startswith(("OTEL_", "APITALLY_", "ASPNETCORE_"))
               and key != "DOTNET_ENVIRONMENT"}
environment.update(DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
frameworks = {"net8.0": "8.0.13", "net9.0": "9.0.2", "net10.0": "10.0.9"}


def run(name, arguments, timeout=120):
    print("$ " + " ".join(arguments), flush=True)
    process = subprocess.Popen(arguments, cwd=root, env=environment, text=True,
                               stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                               start_new_session=True)
    try:
        output, _ = process.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        os.killpg(process.pid, signal.SIGKILL)
        output, _ = process.communicate()
        (results / (name + ".log")).write_text(output)
        raise
    (results / (name + ".log")).write_text(output)
    print(output, end="", flush=True)
    assert process.returncode == 0, f"{name}: exit {process.returncode}"
    return output


assert run("sdk", ["dotnet", "--version"], 30).strip() == "10.0.301"
run("runtimes", ["dotnet", "--list-runtimes"], 30)
run("tool-restore", ["dotnet", "tool", "restore"])
assert run("formatter-version", ["dotnet", "csharpier", "--version"], 30).strip() == "1.3.0"
run("format", ["dotnet", "csharpier", "check", "."])
run("restore", ["dotnet", "restore", "RequestAssociation.csproj", "--locked-mode"])
run("build", ["dotnet", "build", "RequestAssociation.csproj", "--no-restore", "--nologo"])
for target, version in frameworks.items():
    directory = root / "bin" / "Debug" / target
    configuration = json.loads((directory / "RequestAssociation.runtimeconfig.json").read_text())
    shared = configuration["runtimeOptions"]["frameworks"]
    assert {framework["name"] for framework in shared} == {"Microsoft.NETCore.App", "Microsoft.AspNetCore.App"}
    for framework in shared:
        framework["version"] = version
    configuration["runtimeOptions"]["rollForward"] = "Disable"
    pinned = directory / "RequestAssociation.pinned.runtimeconfig.json"
    pinned.write_text(json.dumps(configuration, indent=2) + "\n")
    output = run(target, ["dotnet", "exec", "--runtimeconfig", str(pinned),
                         str(directory / "RequestAssociation.dll")], 120)
    assert f"Runtime: .NET {version};" in output
    assert f"/Microsoft.AspNetCore.App/{version}/" in output
    assert "all hosts, providers, late tasks and batch workers disposed" in output
print("PASS exact three-runtime matrix, locked restore and CSharpier 1.3.0 check")
