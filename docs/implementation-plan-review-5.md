# Implementation plan review, round 5

Date: 2026-09-28. Status: Resolved. All findings are decided and folded into the plan and design. Production code is unchanged.

## Assessment

This round checked the design and plan for internal consistency, coherence and stale statements after the marker cleanup, the public API listing and the stage reordering. There are no findings that would lead an implementer to build the wrong thing. The findings are contradictions left by successive edits: a stage that points to a removed soak, a test rule that implied an exporter seam, a registration list that ignored staging, and wording that still read as undecided.

## Baseline and method

| Item | Revision |
| --- | --- |
| .NET repository | `v1` at `f4bd9f4` |
| Shared SDK documents | cloud `f98007ae` |

Two general-purpose subagents reviewed `design.md` and `implementation-plan.md` separately. Each finding below was verified against the quoted source lines. All relative links in both documents resolve.

## Findings

| ID | Priority | Finding | Needs decision |
| --- | --- | --- | --- |
| H1 | Medium | Plan stage 8 says "run the soak (section 12)", but commit `82573d4` removed section 12's "Measured performance" subsection, which defined the soak and the batch-setting selection. | Yes |
| C1 | Medium | Plan section 12 said "Use in-memory exporters for owned-data assertions". Owned records reach Apitally's own exporter inside `ApitallyBatchProcessor<T>`, so this implies an exporter seam that section 11 forbids. | No |
| C2 | Medium | Plan section 4 listed every `AddApitally` registration as if registered at once, although `IApitally` lands in stage 4 and the logger provider in stage 7. The list also omitted `TelemetryRuntime` and `IHttpContextAccessor`. | No |
| C3 | Medium | Design section 5 capped spans per request without reserving the SERVER span, which the plan reserves. | No |
| C4 | Medium | Design section 2 still said to inspect attribute-limit settings, contradicting its own conclusion that no OTel limit applies. | No |
| C5 | Medium | Design section 3 said "Invalid settings disable telemetry"; only a missing or invalid token does. | No |
| C6 | Medium | Design section 16 implied TestServer as a default telemetry harness, although Apitally suppresses itself there. | No |
| C7 | Low | Stale wording: "This draft", "agreed during the design interview", "A proposed .NET mechanism", "candidate" in the TestServer evidence, the conditional test-suppression direction, the escalation sentence in section 2, "still apply", "or suitable isolated instrumentation", "Propose these public snapshot representations", "the proposed final phase", and "separate limits" for span attributes. | No |
| C8 | Low | Plan layout named `BatchProcessor.cs` for type `ApitallyBatchProcessor<T>`, and section 1 said "two stock batch processors". | No |
| C9 | Low | The design's research references did not say which cloud revision the decisions were last checked against. | No |

**H1 Decision (2026-09-28):** drop the soak. Stage 8 keeps measuring the batch settings, and section 12 is renamed "Validation criteria". The integration tests already verify bounded buffers and spool.

**C1 Decision (2026-09-28):** assert owned data by decoding memory-mode spool contents; use OTel in-memory exporters only on application-owned providers; keep the loopback receiver for HTTP behavior.

**C2 Decision (2026-09-28):** list all registrations in section 4 and record in the section 11 growth paragraph that `AddApitally` gains `IHttpContextAccessor` and `IApitally` in stage 4 and the logger provider in stage 7.

**C3 to C9 Decision (2026-09-28):** wording fixes applied as described.

## Keep these

- Every production module in the plan layout is assigned to exactly one stage, and no stage uses a later stage.
- The section 4 API listing matches the snapshot, callback and helper prose in both documents.
- Repeated numbers and limits agree across both documents and the shared design.

## Candidates not retained

- Moving the request-association paragraph out of design section 2: it already points to plan section 5, and moving it is restructuring without benefit.
- Fixing the `#measured-performance` link in `implementation-plan-review-3.md`: review documents are historical records.
