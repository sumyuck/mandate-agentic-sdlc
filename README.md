# Mandate

**A governed agentic software-engineering system.**
Mandate turns a requirement into a reviewable engineering outcome by orchestrating the full
SDLC — requirements, design, implementation, test, review, documentation, release readiness —
as an explicit dependency graph with gates, policy guardrails, human approvals, bounded
retries, rollback, and an audit chain you can verify.

> **Principle:** agents execute under a mandate; humans grant it, bound it, and revoke it.
>
> In this domain a *mandate* is the document that states exactly what an agent may and may
> not do on someone else's behalf — its limits, its prohibited actions, its reporting
> obligations. That is precisely what an autonomy boundary is, which is why the system is
> named for it. Agents execute inside declared boundaries. Humans own oversight, approvals,
> and final quality.

The system's proving ground is a real service: a **URL shortener** with create/redirect APIs,
click analytics and reliability features, built and then modified *by* Mandate rather than
by hand. Two products, one repository — the orchestrator is the deliverable, the service is
the evidence that it works.

---

## Status

This repository is being built in ordered parts (see [PLAN.md](PLAN.md)). The governance core
lands before the agents, so the orchestrator is defensible even at an intermediate state.

| | Part | State |
|---|---|---|
| P0 | Repository skeleton, build governance, ADRs | **done** |
| P1 | Core domain: graph, state machine, hash-chained events, artifact provenance, decisions, context | **done** |
| P2 | Guard grammar, strict YAML loader, static validation, diagram renderer, the lifecycle itself | **done** |
| P3 | Engine: scheduler, join policies, guard-driven skipping, gates, bounded concurrency | **done** |
| P4 | Durable event store, run catalogue, `audit verify`, evidence export | **done** |
| P5 | Reliability: bounded retries, fallback, rollback, safe-stop | next |
| P6–P16 | Approvals and resume, policy, re-planning, observability, model-backed agents, the three scenarios, documentation | planned |

**436 tests** currently pass, with warnings treated as errors across the solution.

The lifecycle runs end to end today against *scripted* agents — which produce real
content-addressed artifacts with real provenance, contribute the context facts their nodes
declare, and record decisions. What is not yet real is the engineering judgment inside each
stage; that arrives with the model-backed agents in P11. Runs stop at the first human
checkpoint, because approvals land in P6.

```bash
make run                                  # greenfield
make run SCENARIO=brownfield              # takes the impact-analysis path
make run SCENARIO=ambiguous               # diverts to a human, and refuses to design

make runs                                 # list recorded runs
make audit                                # prove no run's log has been altered
```

Runs persist by default to `.mandate/runs.db`. Inspect, verify or export one:

```bash
dotnet run --project src/Mandate.Cli -- runs show <runId>
dotnet run --project src/Mandate.Cli -- audit verify <runId>
dotnet run --project src/Mandate.Cli -- runs export <runId>   # -> runs/<runId>/
```

Only the commands listed below exist today. Nothing in this README describes behaviour that
is not yet implemented.

## Requirements

- **.NET 8 SDK** (`8.0.425` or a later 8.0 feature band — pinned in [`global.json`](global.json))
- `git`
- `make` (optional — every target is a one-line `dotnet` command)

.NET 8 is the current LTS release and the deliberate target; see
[ADR-0002](docs/adr/0002-target-framework.md). If a newer SDK comes first on your `PATH`:

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0
export PATH="$HOME/.dotnet:$PATH"
```

## Quick start

```bash
make doctor     # confirm the toolchain matches global.json
make verify     # toolchain, build (warnings are errors), all tests, code style
make info       # print engine identity and host diagnostics
make workflow   # validate the lifecycle definition and show its parallel structure
make help       # list available targets
```

Inspect the lifecycle directly:

```bash
dotnet run --project src/Mandate.Cli -- workflow validate
dotnet run --project src/Mandate.Cli -- workflow render --markdown
```

Every target accepts a `DOTNET` override, e.g. `make verify DOTNET=$HOME/.dotnet/dotnet`.

Without `make`:

```bash
dotnet build --nologo && dotnet test --nologo
dotnet run --project src/Mandate.Cli -- info --json
```

## Repository layout

```
src/
  Mandate.Core/          domain model and every port the engine depends on
  Mandate.Orchestrator/  the engine: scheduling, gates, retries, rollback, re-planning
  Mandate.Policy/        declarative security / compliance / change-control rules
  Mandate.Agents/        stage agents (requirements, design, implement, test, review, docs, release)
  Mandate.Llm/           model provider port, prompts, record/replay cassettes
  Mandate.Persistence/   event store, artifact store, git-backed workspace
  Mandate.Observability/ structured logs, traces, metrics, run reports
  Mandate.Cli/           `mandate` - the sole composition root
  Mandate.Api/           read-only run inspection surface
services/                 Product A: the URL shortener, produced by Mandate runs
workflows/                the SDLC graph, policy packs, autonomy matrix
prompts/                  versioned prompt templates and custom instructions
runs/                     committed run evidence: events, artifacts, cassettes, metrics
tests/                    unit, architecture and integration tests
docs/                     architecture, ADRs, scenarios, testing, traceability
```

## The lifecycle

[`workflows/sdlc.v1.yaml`](workflows/sdlc.v1.yaml) is the plan — the engine holds no lifecycle
control flow of its own. The diagram in
[`docs/diagrams/sdlc.v1.mmd`](docs/diagrams/sdlc.v1.mmd) is generated from that same file, so
the two cannot drift apart.

It is deliberately not a chain:

- **Conditional paths.** Impact analysis runs only against existing code. Requirements whose
  ambiguity score exceeds the threshold divert to a human and loop back.
- **Parallel work with a synchronising barrier.** Test, code review, security scan and
  documentation run concurrently from one implementation, and all four must land before a
  release decision is even considered.
- **Bounded returns.** A failing test returns to implementation along a declared loop-back
  edge, bounded by that stage's retry budget — non-linear without being unbounded.
- **Human checkpoints at the irreversible decisions.** The design everything is built on, and
  the release itself. Both enforce segregation of duties: the participant that produced the
  work cannot approve it.
- **Autonomy declared per stage.** L0 to L2, matched to the risk of the stage. L3 (fully
  autonomous) is representable and deliberately unassigned.
- **Model capability matched to stage risk**, and recorded per stage in the audit log, so model
  provenance is part of the evidence.

Twenty-two tests in `ShippedWorkflowTests` guard these properties, because the lifecycle is
configuration and nothing in the compiler stops someone weakening it.

## What the core model already guarantees

The domain is built so that the governance properties are enforced by types rather than by
discipline:

- **Illegal state changes are impossible.** Every node transition goes through one table, so
  a node cannot reach `Succeeded` without running, the engine cannot clear its own policy
  block, and work parked for approval can be invalidated by an upstream change instead of
  being approved stale.
- **An unattributed action cannot be recorded.** Events, artifacts, decisions and context
  facts all refuse to exist without an actor, which is what makes segregation of duties
  checkable rather than aspirational.
- **An indefensible decision cannot be recorded.** `Decision` rejects a choice with fewer than
  two options, or a rejected option with no stated reason. Rationale is captured when the
  choice is made, because ADRs are generated from these records.
- **Tampering with the audit log is detectable.** Editing, deleting, reordering, duplicating,
  backdating or splicing events all break the hash chain at an identifiable point.
- **Provenance is structural.** Artifacts are content-addressed and name what they were
  derived from, so lineage needs no trace document and re-plan invalidation is an exact digest
  comparison rather than a heuristic.
- **A workflow that cannot execute is rejected before a run exists**, with every problem
  reported at once — cycles in the forward graph, nodes wired into no path, contradictory
  autonomy, a compensation fallback with nothing to compensate, an attempt with no timeout.
- **The workflow file cannot execute code.** Conditional paths use a closed predicate grammar
  with no function calls, arithmetic, member access or interpolation, so a change-controlled
  configuration file never becomes a code-execution surface. Guards fail closed, and a guard
  reading a context key no stage produces is rejected at load rather than halting a run.
- **Nothing is read loosely.** An unknown key, a misspelled enum, a malformed duration or a
  duplicate mapping is an error naming the file, the line and the accepted values — never a
  silently dropped setting.
- **The engine refuses to start a run it cannot finish.** An agent the workflow names but
  nothing provides, or a gate condition nothing can judge, fails at construction with every
  problem listed. An unjudgeable gate is the dangerous case: skipped, it would appear in the
  audit log as a check that passed.
- **Gates are evidence-based and fail closed.** `tests-pass` reads a recorded test result, not
  an agent's opinion of its own code. Absent evidence fails, because a check that passes for
  want of data is worse than no check.
- **A run can be rebuilt from its log alone.** Live execution and replay go through the same
  reducer, so a rebuilt run cannot disagree with the one that was executed — asserted by a
  test over the full lifecycle, and again against the durable store.
- **The log is append-only at the storage layer**, not merely by convention: database triggers
  refuse an `UPDATE` or `DELETE` on the event table whoever issues it. A forged *insert* is
  still possible through raw SQL — and that is what the hash chain catches, which a test
  demonstrates by forging one.
- **The run listing is a query over the events**, not a second table recording the same facts,
  so there is no denormalised status that can drift away from the log.

## Design in one page

Five choices shape everything else:

1. **The workflow is data, not code.** The SDLC graph lives in versioned YAML — nodes, guarded
   edges, join barriers, gates, retry policies, compensations, autonomy levels. It is
   validated before it runs and can be rendered as a diagram.
   ([ADR-0004](docs/adr/0004-declarative-workflow.md))
2. **State is an append-only event stream, hash-chained.** Current state is a projection. Every
   transition, gate verdict, policy decision, approval and retry is an event whose hash
   commits to its predecessor, so `audit verify` can prove the log was not edited — and every
   reliability metric is derived from that stream rather than stored.
   ([ADR-0005](docs/adr/0005-event-sourced-state.md))
3. **Rollback is a real revert.** Agents write into a per-run git workspace and each node
   commits its own output, so compensation reverts commits and the tree provably returns to
   its prior state. ([ADR-0006](docs/adr/0006-git-backed-workspace.md))
4. **The engine depends on ports only.** `Mandate.Orchestrator` references nothing but the
   domain, so governance logic is unit-testable with no database, no policy file and no model
   provider. Enforced by a test, not a convention.
   ([ADR-0003](docs/adr/0003-hexagonal-layering.md))
5. **Model calls are recorded and replayable.** The default demo path needs no API key and no
   network, and produces identical evidence every time.
   ([ADR-0007](docs/adr/0007-llm-record-replay.md))

Full decision log: [docs/adr](docs/adr/README.md). Build order and rationale: [PLAN.md](PLAN.md).

## Engineering conventions

- **Warnings are errors** in every project, with .NET analyzers at `latest-recommended`.
- **One target framework** (.NET 8 LTS), declared once in `Directory.Build.props`.
- **Central package management** — every NuGet version in a single reviewable file.
- **Architecture tests** assert the structural ADRs, so layering violations fail CI.
- Underscored, sentence-shaped test names; the runner output reads as a specification.
