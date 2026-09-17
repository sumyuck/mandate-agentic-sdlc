# Scenario 1: Greenfield

Build a URL shortener from nothing.

- **Run:** `run_20260917T004505Z_e2258e`
- **Requirement:** [`scenarios/s1-greenfield.txt`](../../scenarios/s1-greenfield.txt)
- **Evidence:** [`runs/run_20260917T004505Z_e2258e/`](../../runs/run_20260917T004505Z_e2258e/)
- **Outcome:** Succeeded. 9 stages ran, 2 were correctly skipped, 171 events, chain intact.

## Reproduce it

```bash
dotnet run --project src/Mandate.Cli -- run "$(cat scenarios/s1-greenfield.txt)" \
    --scenario greenfield --agents model --llm replay --as muskan
```

No API key needed. The exchanges replay from `cassettes/`.

## What was asked for

A service with three endpoints (create, redirect, stats), an optional custom alias, an
optional expiry, and rejection of URLs that are not http or https or whose host is a
private-range IP literal. Persisted in SQLite, codes generated as base62 over a monotonic
identifier.

The requirement is deliberately precise. Scenario 3 covers what happens when it is not.

## Decomposition

The lifecycle decomposed the work into eleven declared stages and ran nine of them. The
two that did not run were skipped for stated reasons rather than forgotten:

| Stage | Outcome |
|---|---|
| `intake` | Recorded the request verbatim |
| `requirements` | Normalised it into 20 acceptance criteria; scored ambiguity at 0.30 |
| `clarification` | **Skipped.** The guard `requirements.ambiguity-score > 0.3` was false |
| `impact-analysis` | **Skipped.** The guard `run.has-existing-code == true` was false |
| `architecture` | Design, ADR and OpenAPI contract. Parked for the tech lead |
| `implement` | Seven source files. Build verified |
| `code-review` | Reviewed against the requirement and the approved design |
| `security-scan` | Scanned for secrets, injection, SSRF and access control |
| `documentation` | Wrote the README from the code that exists |
| `test` | Wrote the suite, ran it, reported measured coverage |
| `release-readiness` | Assembled the evidence pack. Parked for the release approver |

The skip reasons are in the log. `clarification` records "Join policy 'All' cannot be
satisfied: 'requirements' (guard false). The stage is not required on this path." A
skipped stage with no recorded reason is indistinguishable from one nobody thought about.

## Orchestration

**The conditional path was taken on evidence.** Two guarded edges leave `requirements`.
The log records the guard that fired:

```
guard taken: requirements -> architecture  [run.has-existing-code == false]
```

The other two were evaluated and not taken, and that is also recorded. A branch not taken
still has to be accountable months later.

**Four stages ran concurrently.** Test, review, security scan and documentation all depend
on `implement` and on nothing else, so the scheduler ran them together against the same
tree. `release-readiness` joins on `all`, so it waited for every one of the four.

**Two humans signed it, and neither was the requester.** The run was initiated by
`muskan`. The design was approved by `alex`, the release by `priya`. The
segregation-of-duties policy compares the approver against both the producing agent and
the run initiator, so `muskan` approving their own run would have been refused.

The approval notes are in the log:

> tech-lead, alex: "Design is proportionate to the requirement; SQLite and base62 over a
> monotonic id are the right scope."

> release-approver, priya: "Evidence pack reviewed: tests pass with measured coverage,
> review and security clean."

## Validation

**The build was measured, not claimed.** The implementation stage reported
`implementation.builds: true`, and the engine ran `dotnet build` over a copy of the tree
with the proposed files applied before committing anything. Had the claim been false, the
stage would have failed naming both figures.

**The tests were run.** `test.failures` and `test.coverage` come from the TRX report and
the Cobertura file of a real `dotnet test` invocation. The `coverage-at-least 0.75` gate
read a measured number.

**The output was verified independently.** Copied out of the workspace and built on its
own, outside the orchestrator, the service passes its tests. This matters: a test result
the system grades itself on is not evidence.

```bash
cd services/url-shortener && dotnet test tests/Service.Tests/Service.Tests.csproj
```

## What it produced

Seven source files, a test suite, an OpenAPI 3.1 contract, a design document, an
architecture decision record on SQLite write serialisation, a README, and the run's own
governance records under `docs/mandate/`.

The code is in [`services/url-shortener/`](../../services/url-shortener/), though that
directory now also contains Scenario 2's delete endpoint, since the two runs were
sequential.

## Metrics

Derived from the log, not stored:

| Measure | Value | Basis |
|---|---|---|
| Success rate | 100% | 9 of 9 attempted |
| End to end | 3.5 min | 171 events |
| Stage p50 / p95 | 20ms / 2.1m | 9 stages run, 2 not required |
| Retry rate | 10% | 1 of 10 attempts |
| Mean time to recovery | 57.2s | 1 failure |
| Rollback rate | 0% | 0 compensations |
| Gate block rate | 5.6% | 2 of 36 conditions |
| Approval wait | 10.6s | 2 granted, 0 refused |
| Autonomy ratio | 83.3% | 10 agent attempts against human decisions |
| Re-plans | 0 | |

## What this scenario demonstrates

Requirement understanding on a well-defined input, task decomposition into a dependency
graph, parallel execution with a synchronising barrier, conditional routing on recorded
facts, human approval at both irreversible decisions, measured rather than claimed
validation, and a complete audit trail.

This is the straight-through path. Scenario 2 adds codebase reasoning over the tree this
run produced, and Scenario 3 exercises the parts of the lifecycle that only engage when a
requirement cannot be built as written.
