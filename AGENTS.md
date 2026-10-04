# Agent guidance

## Checks

- Verify code changes with the Makefile targets, never with hand-picked subsets of them: `make check` (CSharpier formatting, fixed with `make format`) and `make test` (build, then all tests). Run `dotnet tool restore` once to install CSharpier.
- `make test` runs on net10.0 only; CI also runs net8.0 and net9.0. Run `make test-matrix` to cover all three when the .NET 8 and 9 runtimes are installed.
- For documentation-only changes, review the diff and run `git diff --check`; reserve the Makefile targets for code, configuration, or test changes.

## Code style

- Write the least amount of code that gets the job done.
- Write modern, idiomatic C# 12 using only APIs available in .NET 8, the library's target framework.
- Fix compiler and analyzer warnings instead of suppressing them; the library build treats them as errors. The library must stay trim- and Native AOT-compatible (`IsAotCompatible`).
- Everything outside the public API is `internal`, and every class is `sealed` or `static`. Tests reach internals through `InternalsVisibleTo`.
- Private fields are camelCase with no underscore prefix.
- Library code awaits with `.ConfigureAwait(false)`.
- SDK failures never reach the application. Catch exceptions thrown by SDK work and by user callbacks in `ApitallyOptions`, report them through `SdkDiagnostics`, and fall back to safe behavior. Application exceptions the SDK observes are always rethrown.
- Delays and timers use the injected `TimeProvider`, so tests can drive them with `FakeTimeProvider`.
- Member order within a type is deliberate, not accidental: public entry points first, private helpers after, so the file reads top-down.
- No single-use helper methods unless extraction meaningfully improves readability at the call site.

## Naming and wording conventions

- Use plain, precise English. No invented shorthand, metaphors, or informal jargon.
- A word qualifies only by referring to an actual thing in this codebase or its dependencies, never by sounding technical: "server activity" (the `Activity` from `IHttpActivityFeature`), "spool" (the `TelemetrySpool` class), "startup filter" (`IStartupFilter`).
- Prefer a longer clear name over a compact clever one.
- Vague verbs need an object or a from/to: not `Resolve` but `ResolveEnv`.
- Boolean predicates read as questions: `Is`/`Should`/`Has` prefixes (`IsResponseBodyRetained`, `ShouldRedactHeader`). Never name a predicate as an imperative command.
- The name states what the method actually does, including its outcome: a method that only logs a warning is `WarnIf<Condition>`, not `Check<Thing>`.
- One concept, one name across files. Names align with the option they implement (`IsRequestBodyCaptured` for `CaptureRequestBody`).
- When renaming a method, rename its associated constants to match.
- Public API names are stable: `AddApitally`, the `IApitally` methods (`SetConsumer`, `SetRequestAttribute`, `CaptureException`, `StartActivity`), and the `ApitallyOptions` properties, which double as keys in the `Apitally` configuration section. Naming improvements are internal only.

## Comments

- Comments are sparse and concise (one or two lines). A comment states something the code cannot: a constraint, an external system's behavior, or the reason for a choice. It explains the WHY, never narrates the WHAT; a comment that restates the code below it does not get written.
- Name the real component (the env var, the OTel class, the ASP.NET Core version behavior), never a metaphor.
- No historical references: nothing about the 0.x SDK, "previously", or "ported from". Comments describe the present code only.
- No references to planning or design documents. Every comment stands alone against the code and its dependencies; a comment that needs a rationale states the rationale itself.
- A comment sits next to the code it justifies and stays accurate about what that code covers.
- `///` XML doc comments are for the public API only, where users read them in IntelliSense, and describe behavior from the user's point of view. Internal code uses `//` comments.

## Testing

### What gets tested

- A test may only fail when user-observable behavior regresses against a contract. Documented gaps, internal mechanisms, and constants are never pinned; decisions without a user-observable failure mode are enforced in review, not tests.
- Every test needs an important reason to exist: it pins a spec requirement, a settled design decision, or a behavior a plausible change would silently break. Tests that restate the implementation, or assert theoretical edge cases no real deployment hits, do not get written.
- Test only the SDK's own code. Never write tests that assert what OpenTelemetry or ASP.NET Core does on its own; dependencies appear in tests only as the environment the SDK's behavior is observed in.
- Never replace the SDK's own classes or methods with test doubles. Substitute only process boundaries: export to the loopback `OtlpReceiver`, and reserve `LoopbackHttpServer` for connection-level transport behavior.
- Prefer one integration test proving a flow end-to-end over several micro-tests asserting its intermediate steps.
- Do not multiply a scenario into parameter variants; `[Theory]` is for genuine input tables.

### Layout and naming

- `tests/Apitally.Tests/` mirrors `src/Apitally/` with one test class per source type, named after that type, never after scenarios (`Export/TelemetrySpoolTests.cs` tests `Export/TelemetrySpool.cs`). `Integration/` holds end-to-end scenarios that run `Apitally.TestApp` on a real Kestrel host through `ApplicationHost`.
- Test method names are present-tense behavior statements readable without the test body, with no "Should": `UnmatchedRequestExportsServerSpanWithoutRoute`. Name the observable behavior, not the mechanism or an internal codename.
- Test order within a class is deliberate: core behavior first, then edge cases, failure paths, and shutdown last.

### Coverage ownership

- Every behavior is asserted in exactly one place: the lowest layer that can observe it. Two tests pinning the same contract is a defect.
- Helpers stay consolidated in `tests/Apitally.Tests/Support/`: extending an existing helper always beats adding a sibling.

### Isolation, timing, and assertions

- Tests run sequentially because they share process-wide environment variables and activity listeners. Set environment variables only through `EnvironmentVariables`, which clears Apitally's variables and restores them on dispose.
- Drive SDK timers by advancing a `FakeTimeProvider`; never wait for a real export interval to elapse.
- Integration tests call `host.StopAsync()` before asserting on exports; shutdown flushes all pending telemetry.
- Assertions are exact by default: exact counts of spans, log records, and metric data points, full attribute equality, decoded OTLP payloads. No snapshots.
