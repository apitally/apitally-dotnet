# Native endpoint documentation metadata

This follow-up probe tests a minimal alternative to full OpenAPI document capture. It is outside `Apitally.sln` and does not implement production SDK behavior.

## Findings

Eight path/method cases passed on .NET 8.0.13, 9.0.2 and 10.0.9, built with SDK 10.0.301:

- Minimal API `WithSummary` and `WithDescription` values are readable from finalized endpoints.
- Group metadata is inherited; endpoint metadata overrides the corresponding group value.
- MVC action-level `EndpointSummary` and `EndpointDescription` attributes are readable through the same interfaces.
- Unannotated endpoints have no summary/description metadata.
- A multi-method endpoint supplies the same metadata for each method.
- No OpenAPI, Swashbuckle or NSwag assemblies were loaded. The project has no NuGet package references.

The prospective SDK addition is two lookups during existing startup route enumeration:

```csharp
var summary = endpoint.Metadata.GetMetadata<IEndpointSummaryMetadata>()?.Summary;
var description = endpoint.Metadata.GetMetadata<IEndpointDescriptionMetadata>()?.Description;
```

These interfaces are in `Microsoft.AspNetCore.Http.Metadata` and are available through the ASP.NET Core shared framework. This reads supplied strings rather than generating documentation or adding work to request handling.

A separate initial compilation attempt applied the two attributes to a controller class. All three target frameworks rejected that with CS0592: these attributes target methods, not classes. The runnable fixture uses MVC action attributes instead.

This probe does not collect a complete OpenAPI document, parse XML comments, run generator-specific transformers, measure overhead, or validate the combined startup-event export path. A missing annotation stays absent; no summary is inferred from an endpoint's display name.

## Reproduction

From the repository root:

```sh
(
  set -e
  export DOTNET_CLI_TELEMETRY_OPTOUT=1
  dotnet build pocs/endpoint-metadata/EndpointMetadata.csproj
  for framework in net8.0 net9.0 net10.0; do
    dotnet run --project pocs/endpoint-metadata/EndpointMetadata.csproj --no-build -f "$framework"
  done
)
```

The fixture uses synthetic text, empty application configuration, loopback Kestrel endpoints and a 15-second cancellation budget for host startup/shutdown. It makes no HTTP requests or telemetry exports. See `results.txt` for the observed matrix output.
