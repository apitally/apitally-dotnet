# Error and optional integration feasibility probes

Isolated experiments approved after draft `58dc004`. These do not implement the SDK or select optional package architecture. All source, lock files, and results are local to this directory. No production schema endpoint is mapped or requested.

## Reproduce

```sh
cd /Users/simon.gurcke/Repos/apitally/apitally-dotnet/pocs/error-integrations
bash run.sh
```

Single case:

```sh
dotnet run --project Framework --framework net10.0 --no-launch-profile
```

Executed successfully on 2026-09-23 using SDK 10.0.301, macOS arm64. `run.sh` restores in locked mode and runs 12 finite processes. Each server uses Kestrel at `127.0.0.1:0`, is stopped and asynchronously disposed, and each client has a timeout. Full successful output is in `results/<project>-<framework>.log`.

| Project | net8.0 / runtime 8.0.13 | net9.0 / runtime 9.0.2 | net10.0 / runtime 10.0.9 |
| --- | --- | --- | --- |
| Framework | PASS | PASS | PASS |
| BuiltInOpenApi | PASS: operation metadata only | PASS: negative public-provider check | PASS: public document provider |
| Swashbuckle | PASS | PASS | PASS |
| SentryProbe | PASS | PASS | PASS |

The corresponding ASP.NET Core runtimes are 8.0.13, 9.0.2, and 10.0.9. Exact direct packages:

- Framework: no packages on net8/net9; Microsoft.Extensions.Validation 10.0.9 on net10. Its source generator is included. SDK 10 emits NU1510 for this reference; it is retained intentionally.
- BuiltInOpenApi: Microsoft.AspNetCore.OpenApi 8.0.13 / 9.0.2 / 10.0.9. Microsoft.OpenApi resolves to 1.4.3 / 1.6.17 / explicitly pinned 2.7.5. The net10 override avoids the vulnerability warning on its minimum dependency, 2.0.0.
- Swashbuckle: Swashbuckle.AspNetCore 10.2.3, Microsoft.OpenApi 2.7.5 through its locked dependency.
- SentryProbe: Sentry.AspNetCore, Sentry.Extensions.Logging, and Sentry all 6.11.1. No other Sentry version was tested.

## Validation evidence

`Framework/Program.cs` runs three configurations on each runtime: Minimal APIs without problem-details services, Minimal APIs with them, and MVC plus Minimal APIs with them. The options observers do not register MVC. The Minimal-only hosts assert that MVC's action-descriptor service is absent. The process also asserts that no Sentry assembly was loaded.

Supported public observations demonstrated:

1. Wrapping `ApiBehaviorOptions.InvalidModelStateResponseFactory` with `PostConfigure` observes automatic MVC validation. Calling the existing factory preserves its behavior. `ModelState` exposes messages and sometimes exception types; the tested DataAnnotations and binding errors expose no validator type, so `type` is empty.
2. Wrapping `ProblemDetailsOptions.CustomizeProblemDetails` observes actual `HttpValidationProblemDetails` when the default problem-details service runs. MVC's factory and this callback can both see the same error, so the probe normalizes only once.
3. `TypedResults.ValidationProblem` bypasses the problem-details service on net8, but uses it on net9/net10. `Results.ValidationProblem(..., statusCode: 422)` uses it on all three when registered. Without that service, the known response shape is recognized instead.
4. DataAnnotations on Minimal API parameters do not automatically validate on net8/net9. `AddValidation` on net10 does. With problem-details services, the callback sees the validation object. Without them, the actual fallback is **400 application/json with only title and errors**, not a fully populated problem document. The net10 response recognizer includes this exact compact shape.

The response recognizer requires a known title, an errors object containing arrays of strings, eligible final status, and the appropriate media type and default type/status fields. It recognizes 400 and 422 defaults, including the framework's RFC4918 type for 422. Arbitrary JSON 400s with an `errors` member, ordinary `Results.Problem(400)`, and Minimal API parameter-binding 400s are not counted. Complete malformed JSON and inputs above 50,000 bytes are skipped. Application responses deliberately identical to the framework shape are indistinguishable by bytes alone.

Normalized fields are opaque. Verified values include `payload.literal.dot`, `items[a.b].name`, and the MVC query alias `limit.literal`. MVC itself formats the dictionary validation key as `Entries[0].Value.Name`; the probe preserves that supplied string rather than reconstructing the original JSON key `a.b`. A single MVC body parameter supplies source `body`; an exact query binding alias supplies `query`. Minimal result objects supply neither component nor validator type, so those strings remain empty.

Limits: this is not a general binding-source resolver. Localized/custom response titles, custom validation factories/writers, FluentValidation, complex mixed-source models, and arbitrary custom validation results are not covered. The tiny response fixtures use a MemoryStream; the parser enforces input eligibility and size, but this is **not** a bounded transport buffer implementation. Aborted-response parsing, body capture, streaming, exclusions, and sampling belong to the separate transport experiment.

## Exception evidence

Each runtime tests a non-handling `IExceptionHandler` observer registered before and after a handling service, plus a handler delegate. Net10 additionally tests `SuppressDiagnosticsCallback = _ => false`.

Each configuration exercises handled 500, 503, and 200 responses; intentional 500 without an exception; two explicit captures followed by an automatic capture; a single-leaf aggregate; an unrelated OperationCanceledException; and a real HTTP client cancellation while the server awaits RequestAborted.

- `IExceptionHandlerFeature.Error` remains available after `UseExceptionHandler` returns, including when diagnostics are suppressed. Its original endpoint is `/error/{status:int}`. This public feature is sufficient for these handled-exception observations without changing diagnostic suppression.
- An observer handler returning false is called only if registered before the handler returning true. It is not an order-independent solution.
- The outer catch sees **zero** escaping exceptions for handled requests. Escaping-only interception is incomplete.
- net8/net9 emit one `Microsoft.AspNetCore.Diagnostics.HandledException` diagnostic per handled exception. net10 emits zero by default for a handling IExceptionHandler, but one for a handler delegate. Explicitly disabling suppression restores one for the service case.
- The first capture wins and the single-leaf aggregate is unwrapped. Only a retained exception plus final status exactly 500 satisfies the server-group predicate. 503, 200, and deliberate 500 do not.
- Real client cancellation produces framework status 499, no exception feature, no observer-handler invocation, and no retained error. An unrelated OperationCanceledException with a live request token remains an error.

This tests request-local capture and the eligibility predicate, not aggregation or OTel exception-event emission. Automatic middleware placement, response-started failures, route re-execution, and general final-transport completion are outside this probe.

## Sentry evidence and constraints

All Sentry runs use a fake DSN (`https://public@example.invalid/1`) and an in-memory ITransport with no network implementation. Empty host configuration avoids loading appsettings, environment configuration, or user secrets. No real credentials were read and no real events were sent. Default unhandled-exception, task-exception, and diagnostic-source capture integrations are disabled; every event is synthetic.

Two public registrations are tested both before and after `UseSentry` registration, always before host construction:

- `AddSingleton<ISentryEventProcessor>`: automatic handled exception events are observed during Sentry's request scope.
- `PostConfigure<SentryAspNetCoreOptions>` plus `AddEventProcessor`: automatic events and later captures outside that request scope are observed.

For each configuration, two concurrent requests throw distinct synthetic exceptions. A middleware reading `IExceptionHandlerFeature` associates each exception with its real Kestrel SERVER Activity before Sentry's outer middleware processes the event. Both event IDs remain distinct and match fake-transport events. This also succeeds with net10's default handled-exception diagnostic suppression.

A third request retains a synthetic exception association. The fixture waits for that request's SERVER ActivityStopped callback, then explicitly captures the exception from outside any Activity. The options processor correlates its event ID to the retained server-span ID. The DI-only processor does **not** run for that out-of-request capture, although Sentry sends it to the fake transport. Thus request-scope DI alone is insufficient for this late-capture scenario. No LastEventId accessor is used.

A separate public SentryClient test proves that event processors run before BeforeSend: a processor can see a nonempty event ID even though BeforeSend drops the event and the fake transport receives nothing. Observing an ID is not proof of delivery.

Concrete dependency constraint: the tested hooks require implementing Sentry's `ISentryEventProcessor` and using Sentry-typed DI/options/scope APIs. No package-neutral public discovery/event-registration hook was found. A dependency-free core cannot call these APIs or implement this interface without an adapter dependency or reflective machinery. The Framework project proves absence works; SentryProbe proves a typed companion can register the hook. It does **not** demonstrate automatic loading/registration of an optional package merely because Sentry is installed. A no-dependency, no-opt-in automatic hook remains a negative finding, not a reflection implementation or package decision.

Limits: four hosts run sequentially, with concurrent requests inside each. Simultaneous Sentry hosts, late SDK initialization/replacement, global-mode scope behavior, and initialization through other Sentry packages are untested. Sentry's registration source uses a global HubAdapter, so host isolation must not be assumed. The retained association is fixed-size test state; bounded expiration and attachment to an owned export snapshot/undrained aggregate belong to the snapshot/lifecycle implementation. Already-exported telemetry is not modified.

## Endpoint and OpenAPI evidence

After StartAsync, the finalized EndpointDataSource includes `/api/v1/items/{id:int}` with GET, PUT, and PATCH, plus `/health` GET. Both nested group prefixes and route constraints are retained.

- net8 built-in OpenAPI exposes per-operation `OpenApiOperation` metadata through WithOpenApi. It does not provide the net9 document-generation feature.
- net9 generation services `OpenApiDocumentService` and `IDocumentProvider` are internal. Runtime public-type inspection confirms no public document provider. Public transformers can observe generation, but do not initiate it. No reflective/internal access or self-request workaround is used.
- net10 exposes keyed `IOpenApiDocumentProvider`. Resolving `v1` and calling GetOpenApiDocumentAsync produces a document with the grouped path and executes the registered document transformer. Serialization to JSON is entirely in process.
- Swashbuckle's public `ISwaggerProvider.GetSwagger("v1")` produces the grouped path on all three runtimes, also without an HTTP endpoint or request.

The document name is known fixture configuration, not discovered automatically. NSwag, multiple documents, custom serializers, dynamic endpoint changes, PathBase/mounted applications, wildcard method metadata, schema size omission, and build-time generation are untested. No AOT work was performed.

## Research references

Context7 lookups used `/dotnet/aspnetcore.docs`, `/getsentry/sentry-docs`, and `/domaindrivendev/swashbuckle.aspnetcore`. Version-specific source was retrieved with `gh api`, rather than assuming documentation examples describe every tested runtime.

- [net8 ValidationProblem result](https://github.com/dotnet/aspnetcore/blob/009e1ccafde4086ea52999e878f6e7aa5a7c4ccf/src/Http/Http.Results/src/ValidationProblem.cs)
- [net10 validation filter and fallback](https://github.com/dotnet/aspnetcore/blob/d34d7e49dbcc1f8318db7182819f0fe88b9ca7d2/src/Http/Routing/src/ValidationEndpointFilterFactory.cs)
- [net10 exception feature, handler ordering, cancellation, suppression](https://github.com/dotnet/aspnetcore/blob/d34d7e49dbcc1f8318db7182819f0fe88b9ca7d2/src/Middleware/Diagnostics/src/ExceptionHandler/ExceptionHandlerMiddlewareImpl.cs)
- [net9 internal IDocumentProvider](https://github.com/dotnet/aspnetcore/blob/704f7cb1d2cea33afb00c2097731216f121c2c73/src/OpenApi/src/Services/IDocumentProvider.cs)
- [net10 public IOpenApiDocumentProvider](https://github.com/dotnet/aspnetcore/blob/d34d7e49dbcc1f8318db7182819f0fe88b9ca7d2/src/OpenApi/src/Services/IOpenApiDocumentProvider.cs)
- [net10 keyed provider registration](https://github.com/dotnet/aspnetcore/blob/d34d7e49dbcc1f8318db7182819f0fe88b9ca7d2/src/OpenApi/src/Extensions/OpenApiServiceCollectionExtensions.cs)
- [Swashbuckle public ISwaggerProvider](https://github.com/domaindrivendev/Swashbuckle.AspNetCore/blob/adec6ea0e77aaa31a8dcc1e8574865ad3ee8626b/src/Swashbuckle.AspNetCore.Swagger/ISwaggerProvider.cs)
- [Sentry public event processor](https://github.com/getsentry/sentry-dotnet/blob/20e149b183396198133ea4f749064d204b86dde1/src/Sentry/Extensibility/ISentryEventProcessor.cs)
- [Sentry options registration](https://github.com/getsentry/sentry-dotnet/blob/20e149b183396198133ea4f749064d204b86dde1/src/Sentry/SentryOptions.cs#L1713-L1745)
- [Sentry middleware feature observation and scoped processors](https://github.com/getsentry/sentry-dotnet/blob/20e149b183396198133ea4f749064d204b86dde1/src/Sentry.AspNetCore/SentryMiddleware.cs)
- [Sentry startup filter placement](https://github.com/getsentry/sentry-dotnet/blob/20e149b183396198133ea4f749064d204b86dde1/src/Sentry.AspNetCore/SentryStartupFilter.cs)
- [Sentry DI/global hub registration](https://github.com/getsentry/sentry-dotnet/blob/20e149b183396198133ea4f749064d204b86dde1/src/Sentry.Extensions.Logging/Extensions/DependencyInjection/ServiceCollectionExtensions.cs)
- [Sentry processor before BeforeSend](https://github.com/getsentry/sentry-dotnet/blob/20e149b183396198133ea4f749064d204b86dde1/src/Sentry/SentryClient.cs#L380-L389)
