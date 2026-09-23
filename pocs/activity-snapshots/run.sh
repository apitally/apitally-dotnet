#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
printf 'SDK: '
dotnet --version
dotnet restore ActivitySnapshots.csproj --locked-mode
dotnet build ActivitySnapshots.csproj --no-restore -c Release
dotnet list ActivitySnapshots.csproj package --include-transitive --no-restore
for framework in net8.0 net9.0 net10.0; do
    printf '\n=== %s ===\n' "$framework"
    dotnet run --project ActivitySnapshots.csproj --no-build --no-restore -c Release -f "$framework"
done
