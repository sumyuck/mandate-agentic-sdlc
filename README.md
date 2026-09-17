# Mandate

[![ci](https://github.com/13muskanjain/mandate-agentic-sdlc/actions/workflows/ci.yml/badge.svg)](https://github.com/13muskanjain/mandate-agentic-sdlc/actions/workflows/ci.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10.0%20LTS-512BD4)](global.json)

**A governed agentic software-engineering system.**

Mandate turns a requirement written in plain English into a reviewable engineering outcome.
It decomposes the work across an explicit dependency graph, has language models perform each
stage, holds their output to gates it cannot skip, stops for a named human at the decisions
that matter, and records every step in a log you can verify has not been altered.

> **Principle:** agents execute under a mandate; humans grant it, bound it, and revoke it.
>
> A *mandate* is the document stating exactly what an agent may and may not do on someone
> else's behalf: its limits, its prohibited actions, its reporting obligations. That is
> precisely what an autonomy boundary is, which is why the system is named for it.

The proving ground is a real service. The **URL shortener** in [`services/`](services/) was
written by Mandate, not by hand: its code, its tests, its OpenAPI contract, its architecture
decision record and its README. Two products in one repository. The orchestrator is the
deliverable; the service is the evidence that it works.

> **Assessing this against the brief?**
> [`docs/TRACEABILITY.md`](docs/TRACEABILITY.md) is the map: all eight core requirements, the
> ten sub-capabilities of the orchestration requirement, and all five deliverables, each
> pointing at the code that implements it, the test that holds it in place, and the committed
> run where it happened. Every row resolves to a file you can open. The short version is
> [further down this page](#how-this-maps-to-the-brief).

---

## Try it in five minutes

No API key required. The model exchanges replay from committed recordings.

One command runs the whole tour:

```bash
make demo
```

It checks the toolchain, builds, validates the lifecycle, proves the model layer
offline, walks all eleven stages with no key and no network, loads the three recorded
runs and verifies their audit chains. Or take the steps yourself:

```bash
make doctor                 # confirm the toolchain matches global.json
make verify                 # build with warnings as errors, 867 tests, style check
make workflow               # validate the lifecycle and show its parallel structure
make llm                    # prove the model layer works, offline
make run-model LLM=stub     # walk the whole lifecycle with no key and no network
```

Then read what the system actually did. The recorded runs are committed as evidence
rather than as a database, so the first command loads them into your local store:

```bash
dotnet run --project src/Mandate.Cli -- runs import runs
dotnet run --project src/Mandate.Cli -- runs list
dotnet run --project src/Mandate.Cli -- runs metrics run_20260917T004505Z_e2258e
dotnet run --project src/Mandate.Cli -- audit verify
```

`audit verify` recomputes the SHA-256 chain over every event of every run, on your
machine, from the files in this repository. It is the claim this system rests on and
it is checkable in one command.

And open `runs/run_20260917T004505Z_e2258e/report.html`, a self-contained page with the
timeline, the gate verdicts, the decisions and their rejected options, and the artifact
provenance. No network, no script.

Full command reference: [`docs/RUNBOOK.md`](docs/RUNBOOK.md).

---

## Continuous integration

Everything above is checked on every push, on a clean Ubuntu machine that has none of the
author's tooling or state. Three jobs run in parallel
([`ci.yml`](.github/workflows/ci.yml)):

| Job | What it proves |
|---|---|
| `build / test / style` | The code compiles from a clean clone with warnings as errors, 867 tests pass, and `dotnet format` reports no changes |
| `offline demo` | The five-minute path above still works. It runs the whole lifecycle, loads the committed run evidence and verifies every audit chain |
| `generated service` | The URL shortener Mandate wrote passes its 96 tests, built on its own, outside the orchestrator |

The demo job is the interesting one. **No API key is set anywhere in it**, so if any part
of the offline path quietly needed to reach a model provider, that job fails rather than
the reviewer discovering it. It is also the difference between claiming the evidence is
verifiable and demonstrating it: the job imports the three recorded runs and recomputes
their hash chains on a machine that has never seen them before.

This is not decoration. A `.gitignore` pattern written for build output can quietly match a
source directory, and the result builds perfectly on the machine that still has the files on
disk. Only a clean checkout finds it, which is why every push gets one.

Prose-only pushes skip the pipeline. The exclusion list is explicit rather than a blanket
`**.md`, because several markdown files here are not prose: `prompts/*.prompt.md` are the
agent prompts, and `templates/**/*.md` are part of the workspace every stage reads.
Changing either invalidates the recordings, so those still run the full pipeline.

To run the checks yourself, use `make verify` and `make demo`, or trigger the workflow from
the Actions tab, which accepts a manual run.

---

## What makes it a governed system rather than an agent loop

Most agentic systems are a loop: call a model, look at the output, call it again. That works
until something goes wrong, and then there is nothing to inspect, no boundary that was
enforced, and no way to answer "why did it do that".

Five things are different here.

**The lifecycle is data, not code.** It lives in
[`workflows/sdlc.v1.yaml`](workflows/sdlc.v1.yaml) as a graph of stages, dependencies,
guards and gates. The engine knows nothing about software development. Changing how software
gets built is a configuration change under review, not a deployment.

**Agents propose; the engine applies.** No agent writes to the workspace. They return files
they would like written, and the engine validates every path and commits them as that
stage's commit. The autonomy boundary is enforced, not described.

**Claims are measured, not accepted.** A stage that says the tests pass does not define what
passing means. `dotnet build` and `dotnet test` run over the proposed tree before anything
is committed, and the measured figure replaces the claim. A stage that overstates its
results fails, naming both numbers.

**Humans own the irreversible decisions.** The design everything is built on, and the
release itself, both require a named human who is not the requester. Approving work that
re-planning has since invalidated is impossible, because invalidation revokes the approval.

**The record is the product.** Every state change, gate verdict, approval, artifact,
decision and model call is an event in a SHA-256 hash chain. Metrics are derived from that
log rather than stored beside it, so no number in a report can be set. It can only be caused.

---

## The lifecycle

Eleven stages, seventeen edges, three conditional paths, three loop-backs, three human
checkpoints. Rendered from the definition itself.

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
`release-readiness` waits for all of them. A failing test or a severe review finding
returns the work to implementation.

---

## Three recorded scenarios

Each replays offline with no key. Each has its events, timeline and HTML report committed.

| | Scenario | Outcome | What it demonstrates |
|---|---|---|---|
| [S1](docs/scenarios/s1-greenfield.md) | Greenfield | Succeeded, 171 events | Decomposition, parallel execution with a barrier, both human checkpoints, measured validation |
| [S2](docs/scenarios/s2-brownfield.md) | Brownfield | Succeeded, 179 events | Codebase reasoning over real code the system wrote, conditional routing that adds a stage, compensation on a real git tree |
| [S3](docs/scenarios/s3-ambiguous.md) | Ambiguous | Held at the test gate, 228 events | Ambiguity scored and routed on, clarification to a human, three re-plans, convergence from 0.35 to 0.1 |

The third run is the most interesting of the three. A deliberately vague requirement was
scored, routed to a human, answered, re-planned three times, and converged from 0.35 to
0.1. The rate limiter was then implemented and verified by a measured build, reviewed,
scanned and documented.

Then the run stops, and that is the part worth looking at. The test stage produced no
measured result, so the gate standing in front of release had no test evidence to read,
and it declined to advance the work. It is the one run in the set where a control actually
bites, which makes it the run that proves the controls are real. A system that shipped the
feature anyway, on the strength of a stage's own assurance that it had written tests,
would be the broken one.

### What the review stage caught in the system's own output

Reviewing code **the system had written itself**, the review stage found that IPv4-mapped
IPv6 literals (`::ffff:127.0.0.1`) parse as `InterNetworkV6`, miss all three IPv6 range
checks and reach loopback, defeating the private-range blocking the requirement asked for.
It named the file, the mechanism, the consequence and the fix, and the severity gate held
the change back until it was addressed.

That is the whole argument for the design in one example. A generated change was stopped by
a control, on a real vulnerability, with the reasoning recorded.

---

## How this maps to the brief

The brief names workflow orchestration the critical differentiator and lists ten capabilities
under it. Each one, where it lives, and how to see it working:

| Required | Where it lives | See it |
|---|---|---|
| Explicit dependency graph with entry and exit gates | [`workflows/sdlc.v1.yaml`](workflows/sdlc.v1.yaml); nine gate condition kinds in [`BuiltInGateEvaluators`](src/Mandate.Orchestrator/Gates/BuiltInGateEvaluators.cs) | `make workflow` |
| Sequential and parallel paths with synchronization | Four stages run concurrently; `release-readiness` joins on all four | `make workflow` |
| Cross-stage context and decision lineage | Every decision, its rejected options and its reason are events in the run log | `mandate runs show <id>` |
| Human approval checkpoints for high-impact actions | Two, each requiring a named human who is not the requester | S1, both checkpoints |
| Bounded retries, fallback, rollback and safe-stop | `retry.max-attempts` per node; [`RevertNodeCommitAction`](src/Mandate.Orchestrator/Compensation/RevertNodeCommitAction.cs) reverts a stage's commit on a real git tree; [`FileSafeStopMonitor`](src/Mandate.Persistence/FileSafeStopMonitor.cs) stops at the next safe boundary, never mid-stage | S2 compensates; S3 exhausts three attempts |
| Policy guardrails for security, compliance and change control | [`workflows/policies/`](workflows/policies/), three packs as data, with waivers on the record | `mandate policy check` |
| Audit-grade observability and traceability | A SHA-256 chain over every event ([`AuditChain`](src/Mandate.Core/Events/AuditChain.cs)) | `mandate audit verify` |
| Reliability metrics | Derived from the log rather than stored beside it | `mandate runs metrics <id>` |
| Dynamic re-planning when upstream outputs change | Invalidation revokes any approval that covered the invalidated work | S3, three re-plans |
| Controlled agent autonomy under governance | No agent writes to the workspace; agents propose and the engine validates and commits | Any run's artifact provenance |

The other seven core requirements, the five deliverables and the eight evaluation criteria
are mapped the same way, with test names and event references, in
[`docs/TRACEABILITY.md`](docs/TRACEABILITY.md).

### The reliability metrics, on a committed run

The brief asks for success rate, retry and rollback frequency, MTTR and end-to-end latency.
This is the greenfield run, read back from its own event log:

```
$ mandate runs metrics run_20260917T004505Z_e2258e

╭───────────────────────┬─────────────┬───────────────────────────────────────────╮
│ measure               │       value │ basis                                     │
├───────────────────────┼─────────────┼───────────────────────────────────────────┤
│ success rate          │        100% │ 9 of 9 attempted                          │
│ end to end            │        3.5m │ 171 events                                │
│ stage p50 / p95       │ 20ms / 2.1m │ 9 stage(s) run, 2 not required            │
│ retry rate            │         10% │ 1 of 10 attempts                          │
│ mean time to recovery │      57.24s │ 1 failure(s)                              │
│ rollback rate         │          0% │ 0 compensation(s)                         │
│ gate block rate       │        5.6% │ 2 of 36 conditions                        │
│ approval wait         │       10.6s │ 2 granted, 0 refused                      │
│ autonomy ratio        │       83.3% │ 10 agent attempt(s) against human decisions│
│ re-plans              │           0 │ plan recomputed                           │
│ policy                │           1 │ 0 blocked, 0 waived                       │
╰───────────────────────┴─────────────┴───────────────────────────────────────────╯
```

Every figure carries the basis it was computed from, and none of them is stored. They are
recomputed from the event log on each call, so no number in a report can be set. It can only
be caused. See [ADR-0012](docs/adr/0012-metrics-derived-not-recorded.md).

---

## Documentation

| Document | What it covers |
|---|---|
| [`docs/TRACEABILITY.md`](docs/TRACEABILITY.md) | **The assessor's map.** Every requirement in the brief against the code that implements it, the test that holds it, and the committed run where it happened |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | Components, control flow, governance mechanisms, key decisions, what the system does not do |
| [`docs/RUNBOOK.md`](docs/RUNBOOK.md) | Installing, running, every command, operating a live run, spend control |
| [`docs/TESTING.md`](docs/TESTING.md) | The 867 tests and what they pin, the scope boundaries, and the trade-offs behind each design choice |
| [`docs/FINAL-SUMMARY.md`](docs/FINAL-SUMMARY.md) | Plan, rationale, artifacts, risk controls, assumptions, roadmap |
| [`docs/scenarios/`](docs/scenarios/) | The three runs, in detail |
| [`docs/adr/`](docs/adr/) | 17 architecture decision records with the alternatives rejected |

The 17 ADRs are worth reading alongside the code. Each records the alternatives that were
weighed and the specific reason each was rejected.

---

## Requirements

- **.NET 10 SDK**, pinned in [`global.json`](global.json)
- `git`
- `make`, optional

.NET 10 is the current LTS, supported to November 2028. In a regulated domain the support
lifecycle is a compliance input rather than a preference. See
[ADR-0002](docs/adr/0002-target-framework.md).

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
export PATH="$HOME/.dotnet:$PATH"
```

To call a model for real, set `ANTHROPIC_API_KEY` and pass `--llm live` or `--llm record`.
Spend is capped per run and every call is an audited event. See
[ADR-0013](docs/adr/0013-model-spend-as-a-governed-budget.md).

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
scripts/                 the guided demo, and toolchain resolution
services/                the URL shortener, produced by Mandate runs
templates/               trees a run is seeded from
tests/                   867 tests across 9 projects
docs/                    architecture, ADRs, scenarios, testing, traceability
```

---

## Engineering conventions

- **One target framework**, declared once in `Directory.Build.props`.
- **Warnings are errors** in `src/`, with analysers at `latest-recommended`. No project
  opts out, and a test asserts none does.
- **Central package management.** Every NuGet version is declared once.
- **Architecture rules are tests.** The engine may depend only on the domain, no adapter may
  depend on the engine, and exactly one project may reference the model vendor's SDK.
  Breaking any of those fails the build rather than the review.
- **Deterministic builds**, so identical inputs produce identical outputs.
- **Test names are sentences**, so the runner output reads as a specification.

---

## Exit codes

| Code | Meaning |
|---|---|
| `0` | The command did what was asked |
| `1` | The work failed, or verification found a defect |
| `2` | Bad input: an unknown id, a missing file, an unusable configuration |
| `3` | The run is complete as far as it can go and is waiting on a human |

Code 3 is deliberately distinct. A run parked at an approval is the lifecycle working, and
a script that could not tell the difference would report the system's central behaviour as
a failure.
