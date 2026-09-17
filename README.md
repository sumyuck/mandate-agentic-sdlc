# Mandate

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

---

## Try it in five minutes

No API key required. The model exchanges replay from committed recordings.

```bash
make doctor                 # confirm the toolchain matches global.json
make verify                 # build with warnings as errors, 853 tests, style check
make workflow               # validate the lifecycle and show its parallel structure
make llm                    # prove the model layer works, offline
make run-model LLM=stub     # walk the whole lifecycle with no key and no network
```

Then read what the system actually did:

```bash
dotnet run --project src/Mandate.Cli -- runs list
dotnet run --project src/Mandate.Cli -- runs metrics run_20260917T004505Z_e2258e
dotnet run --project src/Mandate.Cli -- audit verify
```

And open `runs/run_20260917T004505Z_e2258e/report.html`, a self-contained page with the
timeline, the gate verdicts, the decisions and their rejected options, and the artifact
provenance. No network, no script.

Full command reference: [`docs/RUNBOOK.md`](docs/RUNBOOK.md).

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

## Documentation

| Document | What it covers |
|---|---|
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | Components, control flow, governance mechanisms, key decisions, what the system does not do |
| [`docs/RUNBOOK.md`](docs/RUNBOOK.md) | Installing, running, every command, operating a live run, spend control |
| [`docs/TESTING.md`](docs/TESTING.md) | The 853 tests and what they pin, the scope boundaries, and the trade-offs behind each design choice |
| [`docs/TRACEABILITY.md`](docs/TRACEABILITY.md) | Every requirement in the brief mapped to code, test and evidence |
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
services/                the URL shortener, produced by Mandate runs
templates/               trees a run is seeded from
tests/                   853 tests across 9 projects
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
