# Vendored OTLP schema provenance

Source: https://github.com/open-telemetry/opentelemetry-proto

- Tag: `v1.11.0`
- Immutable commit: `790608c4d51e6ffc12210b541e8514cbed9e91a4`
- License: Apache-2.0; unmodified upstream text in `LICENSE`
- Copyright: OpenTelemetry Authors, as recorded in each schema
- Files: eight unmodified `.proto` files and the upstream `LICENSE`
- Integrity: `SHA256SUMS` covers all upstream files
- Upstream schema whitespace is preserved; local Git whitespace checks exclude `.proto` files in this directory.

`../fetch-proto.py` downloads these specific paths through `gh api` at the
immutable commit. It does not clone the upstream repository.

`Grpc.Tools` 2.76.0 (`libprotoc 31.1` on this machine) generates C# message
classes during build under `../obj/Debug/<framework>/opentelemetry/proto/`.
Those generated message classes derive from these Apache-2.0 schemas and
are covered by this provenance/license notice. They are reproducible build
artifacts, not hand-edited source. `GrpcServices=None` generates messages only.
`Google.Protobuf` 3.33.5 provides the serializer and parser. Package licenses
remain with the NuGet packages; no package source code is vendored here.
