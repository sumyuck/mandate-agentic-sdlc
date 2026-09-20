# Mandate

[![ci](https://github.com/sumyuck/mandate-agentic-sdlc/actions/workflows/ci.yml/badge.svg)](https://github.com/sumyuck/mandate-agentic-sdlc/actions/workflows/ci.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10.0%20LTS-512BD4)](global.json)
[![tests](https://img.shields.io/badge/tests-868-1a7f37)](docs/TESTING.md)
[![licence](https://img.shields.io/badge/licence-MIT-blue)](LICENSE)

**A governed agentic software-engineering system.**

Mandate turns a requirement written in plain English into a reviewable engineering outcome.
It decomposes the work across an explicit dependency graph, has language models perform each
stage, holds their output to gates it cannot skip, stops for a named human at the decisions
that matter, and records every step in a log you can verify has not been altered.

> **Principle:** agents execute under a mandate; humans grant it, bound it, and revoke it.

The proving ground is a real service. The **URL shortener** in [`services/`](services/) was
written by Mandate, not by hand: its code, its tests, its OpenAPI contract, its architecture
decision record and its README. The orchestrator is the deliverable; the service is the
evidence that it works.

---

## See it running

**→ [sumyuck.github.io/mandate-agentic-sdlc](https://sumyuck.github.io/mandate-agentic-sdlc/)**

Three runs are recorded with their complete evidence committed. Each report is a
self-contained page, so you can read what the system did without installing anything.

| | Run | Outcome | What it shows |
|---|---|---|---|
| **S1** | [Greenfield](https://sumyuck.github.io/mandate-agentic-sdlc/runs/run_20260917T004505Z_e2258e/report.html) | succeeded, 171 events | Decomposition, parallel execution behind a barrier, both human checkpoints |
| **S2** | [Brownfield](https://sumyuck.github.io/mandate-agentic-sdlc/runs/run_20260917T012835Z_28d1ac/report.html) | succeeded, 179 events | Codebase reasoning, conditional routing that adds a stage, compensation on a real git tree |
| **S3** | [Ambiguous](https://sumyuck.github.io/mandate-agentic-sdlc/runs/run_20260917T020240Z_c6e2f4/report.html) | **held**, 228 events | Ambiguity scored and routed to a human, three re-plans, then stopped at the test gate |

![The greenfield run: reliability figures and a per-stage timeline](docs/images/report-hero.png)

Details of each run are in [`docs/scenarios/`](docs/scenarios/).

---

## Try it in five minutes

No API key required. The model exchanges replay from committed recordings.

```bash
make demo
```

It checks the toolchain, builds, validates the lifecycle, proves the model layer offline,
walks all eleven stages with no key and no network, loads the three recorded runs and
verifies their audit chains. Or take the steps yourself:

```bash
make doctor                 # confirm the toolchain matches global.json
make verify                 # build with warnings as errors, 868 tests, style check
make workflow               # validate the lifecycle and show its parallel structure
make llm                    # prove the model layer works, offline
make run-model LLM=stub     # walk the whole lifecycle with no key and no network
make run FAIL=test-engineer # inject a failure and watch retry, fallback and rollback
```

To read a recorded run locally, import the committed evidence first:

```bash
dotnet run --project src/Mandate.Cli -- runs import runs
dotnet run --project src/Mandate.Cli -- runs metrics run_20260917T004505Z_e2258e
dotnet run --project src/Mandate.Cli -- audit verify
```

Full command reference: [`docs/RUNBOOK.md`](docs/RUNBOOK.md).

---

## What makes it a governed system rather than an agent loop

- **The lifecycle is data, not code.** It lives in
  [`workflows/sdlc.v1.yaml`](workflows/sdlc.v1.yaml) as a graph of stages, dependencies,
  guards and gates. Changing how software gets built is a configuration change, not a
  deployment.
- **Agents propose; the engine applies.** No agent writes to the workspace. They return files
  they would like written, and the engine validates every path and commits them as that
  stage's commit.
- **Claims are measured, not accepted.** `dotnet build` and `dotnet test` run over the
  proposed tree before anything is committed, and the measured figure replaces the claim. A
  stage that overstates its results fails, naming both numbers.
- **Humans own the irreversible decisions.** The design and the release both require a named
  human who is not the requester. Re-planning revokes an approval it invalidated.
- **The record is the product.** Every state change, gate verdict, approval, artifact,
  decision and model call is an event in a SHA-256 hash chain.

---

## The lifecycle

Eleven stages, seventeen edges, three conditional paths, three loop-backs, three human
checkpoints.

```
intake
  |
requirements
  |-- (ambiguity >= 0.3) --> clarification --[on success]--> requirements
  |-- (has existing code) --> impact-analysis
  |
architecture                       [human: tech-lead]
  |
implement
  |
  +-------> test ---------[on failure]-------> implement
  +-------> code-review --[on failure]-------> implement
  +-------> security-scan
  +-------> documentation
  |
release-readiness                  [human: release-approver]
```

It is not linear. An ambiguous requirement diverts to a human and loops back. Work against
existing code earns an extra analysis stage. Four stages run concurrently and
`release-readiness` waits for all of them.

![mandate workflow validate](docs/images/cli-workflow.png)

---

## Governance

Two human checkpoints, each requiring a named human who is not the requester. Both the
question and the answer are events in the log.

![The approval trail from the greenfield run](docs/images/report-humans.png)

Ten gate conditions are evaluated against recorded evidence rather than an agent's opinion,
and they fail closed. S3 is the run where one bites: the test stage produced no measured
result, so the gate in front of release had no evidence to read and declined to advance the
work.

![The ambiguous run: failed, chain intact, three re-plans](docs/images/report-ambiguous.png)

Ten policy rules across three packs enforce security, compliance and change control. A
violation blocks; a human can waive it with `mandate waive`, which records the rule, the
waiving human and the reason. Retries are bounded per node, and compensation is a real
`git revert` of the node's commits.

Every event commits to its predecessor, and the store refuses UPDATE and DELETE at the
database level:

![mandate audit verify](docs/images/cli-audit.png)

How each mechanism works, and the alternatives rejected, is in
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

---

## Metrics

Success rate, retry and rollback frequency, MTTR, end-to-end latency, stage percentiles,
approval wait, autonomy ratio, re-plans and policy activity — all recomputed from the event
log on each call, never stored.

![mandate runs metrics](docs/images/cli-metrics.png)

---

## Repository layout

```
src/
  Mandate.Core/          domain model and every port the engine depends on
  Mandate.Orchestrator/  the engine: scheduling, gates, retries, rollback, re-planning
  Mandate.Workflows/     YAML lifecycle loader, validator, diagram renderer
  Mandate.Agents/        the eleven stage agents, and scripted agents for testing
  Mandate.Llm/           model port, prompt library, record and replay, spend ceiling
  Mandate.Policy/        declarative security, compliance and change-control rules
  Mandate.Persistence/   event store, git workspace, toolchain verification
  Mandate.Observability/ metrics derived from the log, HTML run report
  Mandate.Cli/           `mandate`, the sole composition root
  Mandate.Api/           read-only run inspection surface

workflows/               the lifecycle graph and the policy packs
prompts/                 one versioned file per agent
config/                  dated model prices, with the source they came from
cassettes/               recorded model exchanges, keyed by request content
scenarios/               the three requirements, as given to the system
runs/                    committed evidence: events, timelines, HTML reports
services/                the URL shortener, produced by Mandate runs
templates/               trees a run is seeded from
tests/                   868 tests across 9 projects
docs/                    architecture, ADRs, scenarios, testing
```

The engine depends only on the domain, and the rule is enforced by tests: the build fails if
`Mandate.Orchestrator` gains a reference to anything but `Mandate.Core`, if any adapter
depends on the engine, or if a second project starts referencing the model vendor's SDK.

---

## Tests and continuous integration

868 tests across 9 projects, plus 96 in the generated service. Three jobs run in parallel on
every push ([`ci.yml`](.github/workflows/ci.yml)), on a clean machine:

| Job | What it proves |
|---|---|
| `build / test / style` | The code compiles from a clean clone with warnings as errors, 868 tests pass, and `dotnet format` reports no changes |
| `offline demo` | The five-minute path above still works: the whole lifecycle, the committed run evidence, every audit chain |
| `generated service` | The URL shortener Mandate wrote passes its 96 tests, built on its own, outside the orchestrator |

**No API key is set anywhere in the demo job**, so if any part of the offline path quietly
needed a model provider, that job fails. Scope boundaries and trade-offs are in
[`docs/TESTING.md`](docs/TESTING.md).

---

## Documentation

| Document | What it covers |
|---|---|
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | Components, control flow, governance mechanisms, key decisions, what the system does not do |
| [`docs/RUNBOOK.md`](docs/RUNBOOK.md) | Installing, running, every command, operating a live run, spend control |
| [`docs/TESTING.md`](docs/TESTING.md) | The 868 tests and what they pin, the scope boundaries, and the trade-offs |
| [`docs/FINAL-SUMMARY.md`](docs/FINAL-SUMMARY.md) | Plan, rationale, artifacts, risk controls, assumptions, roadmap |
| [`docs/TRACEABILITY.md`](docs/TRACEABILITY.md) | Every capability mapped to the code that implements it, the test that holds it, and the run where it happened |
| [`docs/scenarios/`](docs/scenarios/) | The three runs, in detail |
| [`docs/adr/`](docs/adr/) | 17 architecture decision records with the alternatives rejected |

---

## Setup

- **.NET 10 SDK**, pinned in [`global.json`](global.json)
- `git`
- `make`, optional

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
export PATH="$HOME/.dotnet:$PATH"
```

To call a model for real, set `ANTHROPIC_API_KEY` and pass `--llm live` or `--llm record`.
Spend is capped per run and every call is an audited event.

### Exit codes

| Code | Meaning |
|---|---|
| `0` | The command did what was asked |
| `1` | The work failed, or verification found a defect |
| `2` | Bad input: an unknown id, a missing file, an unusable configuration |
| `3` | The run is complete as far as it can go and is waiting on a human |

Code 3 is deliberately distinct: a run parked at an approval is the lifecycle working.

---

## Licence

[MIT](LICENSE) © 2026 Samyak Jain
