#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p results
for project in Framework BuiltInOpenApi Swashbuckle SentryProbe; do
    dotnet restore "$project" --locked-mode
    for framework in net8.0 net9.0 net10.0; do
        dotnet run --project "$project" --framework "$framework" --no-restore --no-launch-profile \
            > "results/$project-$framework.log" 2>&1
        printf '%s %s passed\n' "$project" "$framework"
    done
done
