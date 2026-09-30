# Vendored OTLP schemas

Source: https://github.com/open-telemetry/opentelemetry-proto

- Tag: `v1.11.0`
- Commit: `790608c4d51e6ffc12210b541e8514cbed9e91a4`
- License: Apache-2.0, unmodified upstream text in `LICENSE`

The `.proto` files are unmodified upstream copies. `Grpc.Tools` generates internal C# message classes from them during every build, into `obj/`. Generated code is not checked in and is not part of the public API. gRPC service generation is disabled.

## Updating

Replace `REVISION` with the new upstream commit, then update this README:

```sh
cd src/Apitally/Protos
REVISION=790608c4d51e6ffc12210b541e8514cbed9e91a4
for name in LICENSE $(find opentelemetry -name '*.proto'); do
  mkdir -p "$(dirname "$name")"
  gh api -H "Accept: application/vnd.github.raw+json" \
    "repos/open-telemetry/opentelemetry-proto/contents/$name?ref=$REVISION" > "$name"
done
```

To verify the files are unmodified, run the loop with the current `REVISION` and confirm `git diff` is empty.
