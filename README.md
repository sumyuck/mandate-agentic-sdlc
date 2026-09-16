# Helmsman

**A governed agentic software-engineering system.**
Helmsman turns a requirement into a reviewable engineering outcome by orchestrating the full
SDLC — requirements, design, implementation, test, review, documentation, release readiness —
as an explicit dependency graph with gates, policy guardrails, human approvals, bounded
retries, rollback, and an audit chain you can verify.

> **Principle:** the helmsman steers; the crew rows.
> Agents execute inside declared autonomy boundaries. Humans own oversight, approvals, and
> final quality.

The system's proving ground is a real service: a **URL shortener** with create/redirect APIs,
click analytics and reliability features, built and then modified *by* Helmsman rather than
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
| P2 | YAML workflow loader, static validator, diagram renderer | next |
| P3–P16 | Engine, persistence, reliability, approvals, policy, re-planning, observability, agents, the three scenarios, documentation | planned |

**207 tests** currently pass, with warnings treated as errors across the solution.

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
make help       # list available targets
```

Every target accepts a `DOTNET` override, e.g. `make verify DOTNET=$HOME/.dotnet/dotnet`.

Without `make`:

```bash
dotnet build --nologo && dotnet test --nologo
dotnet run --project src/Helmsman.Cli -- info --json
```

## Repository layout

```
src/
  Helmsman.Core/          domain model and every port the engine depends on
  Helmsman.Orchestrator/  the engine: scheduling, gates, retries, rollback, re-planning
  Helmsman.Policy/        declarative security / compliance / change-control rules
  Helmsman.Agents/        stage agents (requirements, design, implement, test, review, docs, release)
  Helmsman.Llm/           model provider port, prompts, record/replay cassettes
  Helmsman.Persistence/   event store, artifact store, git-backed workspace
  Helmsman.Observability/ structured logs, traces, metrics, run reports
  Helmsman.Cli/           `helmsman` - the sole composition root
  Helmsman.Api/           read-only run inspection surface
services/                 Product A: the URL shortener, produced by Helmsman runs
workflows/                the SDLC graph, policy packs, autonomy matrix
prompts/                  versioned prompt templates and custom instructions
runs/                     committed run evidence: events, artifacts, cassettes, metrics
tests/                    unit, architecture and integration tests
docs/                     architecture, ADRs, scenarios, testing, traceability
```

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
4. **The engine depends on ports only.** `Helmsman.Orchestrator` references nothing but the
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
