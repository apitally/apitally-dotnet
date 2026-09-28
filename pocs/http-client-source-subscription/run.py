#!/usr/bin/env python3
"""Build once, then run every variant on exact .NET 8/9/10 runtimes from a private DOTNET_ROOT."""
import json
import os
from pathlib import Path
import signal
import subprocess
import sys

root = Path(__file__).resolve().parent
results = root / "results"
results.mkdir(exist_ok=True)
dotnet_root = Path(os.environ.get("POC_DOTNET_ROOT", "/tmp/dotnet-poc"))
dotnet = str(dotnet_root / "dotnet")
environment = {key: value for key, value in os.environ.items()
               if not key.startswith(("OTEL_", "APITALLY_", "ASPNETCORE_", "DOTNET_"))}
environment.update(DOTNET_ROOT=str(dotnet_root), PATH=f"{dotnet_root}:{environment['PATH']}",
                   DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1", DOTNET_MULTILEVEL_LOOKUP="0")
frameworks = {"net8.0": "8.0.13", "net9.0": "9.0.2", "net10.0": "10.0.9"}
variants = sys.argv[1:] or ["wildcard", "baseline"]


def run(name, arguments, timeout=120, check=True):
    print("$ " + " ".join(arguments), flush=True)
    process = subprocess.Popen(arguments, cwd=root, env=environment, text=True,
                               stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                               start_new_session=True)
    try:
        output, _ = process.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        os.killpg(process.pid, signal.SIGKILL)
        output, _ = process.communicate()
        output += "\nFAIL process-group timeout\n"
    (results / (name + ".log")).write_text(output)
    print(output, end="", flush=True)
    if check:
        assert process.returncode == 0, f"{name}: exit {process.returncode}"
    return process.returncode, output


assert run("sdk", [dotnet, "--version"], 30)[1].strip() == "10.0.301"
run("runtimes", [dotnet, "--list-runtimes"], 30)
run("tool-restore", [dotnet, "tool", "restore"])
assert run("formatter-version", [dotnet, "csharpier", "--version"], 30)[1].strip() == "1.3.0"
run("format", [dotnet, "csharpier", "check", "."])
run("restore", [dotnet, "restore", "HttpClientSourceSubscription.csproj", "--locked-mode"])
run("build", [dotnet, "build", "HttpClientSourceSubscription.csproj", "--no-restore", "--nologo"])
summary = []
for target, version in frameworks.items():
    directory = root / "bin" / "Debug" / target
    configuration = json.loads((directory / "HttpClientSourceSubscription.runtimeconfig.json").read_text())
    shared = configuration["runtimeOptions"]["frameworks"]
    assert {framework["name"] for framework in shared} == {"Microsoft.NETCore.App", "Microsoft.AspNetCore.App"}
    for framework in shared:
        framework["version"] = version
    configuration["runtimeOptions"]["rollForward"] = "Disable"
    pinned = directory / "HttpClientSourceSubscription.pinned.runtimeconfig.json"
    pinned.write_text(json.dumps(configuration, indent=2) + "\n")
    for variant in variants:
        code, output = run(f"{target}-{variant}", [dotnet, "exec", "--runtimeconfig", str(pinned),
                                                   str(directory / "HttpClientSourceSubscription.dll"), variant],
                           check=False)
        assert f"Runtime: .NET {version};" in output, f"{target}: wrong runtime loaded"
        assert f"/Microsoft.AspNetCore.App/{version}/" in output, f"{target}: wrong ASP.NET Core loaded"
        summary.append(f"{target} {variant}: exit {code}")
print("\n".join(summary))
