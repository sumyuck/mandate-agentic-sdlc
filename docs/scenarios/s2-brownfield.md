# Scenario 2: Brownfield

Add a delete endpoint to an existing service.

- **Run:** `run_20260917T012835Z_28d1ac`
- **Requirement:** [`scenarios/s2-brownfield.txt`](../../scenarios/s2-brownfield.txt)
- **Evidence:** [`runs/run_20260917T012835Z_28d1ac/`](../../runs/run_20260917T012835Z_28d1ac/)
- **Outcome:** Succeeded. 10 stages ran, 1 correctly skipped, 179 events, chain intact.

## Reproduce it

```bash
dotnet run --project src/Mandate.Cli -- run "$(cat scenarios/s2-brownfield.txt)" \
    --scenario brownfield --template templates/brownfield \
    --agents model --llm replay --as muskan
```

## The existing code is real

`templates/brownfield/` is the tree Scenario 1 produced. The brownfield scenario runs
against code this system actually wrote, not against a codebase invented to look
plausible. That distinction matters: an impact analysis of hand-written scaffolding proves
nothing about whether the system can reason over a real repository.

The starting tree is 24 files: seven source files, five test files, an OpenAPI contract, a
design document, an ADR, a requirements document, an ambiguity report, and the project
scaffolding.

## What the brownfield path adds

One stage, and it is the whole point of the scenario. The guard on the edge leaving
`requirements` evaluated differently from Scenario 1:

```
guard taken: requirements -> impact-analysis  [run.has-existing-code == true]
```

`impact-analysis` ran and succeeded. `clarification` was skipped, because the requirement
scored 0.15 for ambiguity, well under the 0.3 ceiling.

The stage read the tree and reported which components change, which merely depend on them,
which public surfaces are affected, and how the change could break something that
currently works. It set `impact.blast-radius` to a graded value and
`impact.affected-components` to the list, both of which enter the run context for
downstream stages to read.

Note that `architecture` joins on `any` rather than `all`. Its two inbound paths are
mutually exclusive: greenfield arrives directly from `requirements`, brownfield through
`impact-analysis`. A join of `all` would deadlock every run.

## Orchestration

Ten stages ran against one skipped, compared with nine against two in Scenario 1. The same
graph, the same engine, a different path, decided by a fact the run produced rather than
by a flag someone set.

Two humans again, neither the requester:

> tech-lead, alex: "Delete is additive and the blast radius is confined to the repository
> and the endpoint map."

> release-approver, priya: "Additive change, tests pass with measured coverage, review and
> scan clean."

## Validation

The service now passes **96 tests**, up from 70. The delete endpoint brought 26 with it.
Verified independently, outside the orchestrator:

```bash
cd services/url-shortener && dotnet test tests/Service.Tests/Service.Tests.csproj
```

The change is confined to `LinkRepository.cs` and `Program.cs`, which is what the impact
analysis predicted.

## Two behaviours only a brownfield run exercises

Existing code and parallel stages reach paths a greenfield run never touches. Both of
these are now covered by tests.

### Compensation when no clean revert exists

When `test` exhausts its retries, the loop-back returns the work to `implement` and the
fallback compensation reverts its commits. That revert can conflict, because a sibling
stage running in parallel may already have modified the same file, and then a clean revert
of one node's work does not exist.

The system treats this as a real outcome rather than an error condition. The revert is
abandoned, the tree is reset clean, and the compensation is reported as **failed**, naming
how far it got and recording that a human must decide what the tree should contain. The
node moves to `Failed`, a state the machine allows, rather than being stranded mid-rollback.

Tested in
[`GitRunWorkspaceTests`](../../tests/Mandate.Persistence.Tests/GitRunWorkspaceTests.cs),
including the assertion that no conflict markers survive for the next stage to commit as
its own work.

### Provenance when a file is returned unchanged

A stage editing an existing tree will sometimes return a file it did not need to change.
Because artifacts are content-addressed, that output is the *same artifact* as one of its
inputs, and the domain refuses an artifact listing itself as an ancestor.

Both halves of that are correct, and the resolution is that returning a file unchanged is
a no-op rather than a paradox: the self-reference is dropped from the provenance edge and
the stage proceeds. Provenance stays acyclic without penalising a stage for leaving
something alone.

## Metrics

| Measure | Value |
|---|---|
| Status | Succeeded |
| Events | 179 |
| Stages | 10 succeeded, 1 skipped |
| Artifacts | 23 |
| Chain | Intact |

## What this scenario demonstrates

Codebase reasoning over a real repository, conditional routing that adds a stage on
recorded evidence, a join policy chosen so mutually exclusive paths do not deadlock,
compensation against a real git tree including the case where no clean revert exists, and
content-addressed provenance that stays acyclic when a stage leaves a file alone.
