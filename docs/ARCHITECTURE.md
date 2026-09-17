# Architecture

## What this system is

Mandate is an orchestrator for a governed software development lifecycle. You give it a
requirement in plain English. It decomposes the work, routes it through a declared
lifecycle, has language models perform each stage, checks their output against gates it
cannot skip, stops for a named human at the decisions that matter, and records every step
in a tamper-evident log.

The URL shortener in [`services/`](../services/) is not the point. It is the fixture: a
real piece of software the orchestrator had to actually produce, so that the orchestrator
itself has something real to be judged on. Everything under `services/url-shortener/` was
written by the system, including its tests, its OpenAPI contract, its architecture
decision record and its README.

## The central idea

Most agentic systems are a loop: call a model, look at the output, call it again. That
works until something goes wrong, and then there is nothing to inspect, no boundary that
was enforced, and no way to answer "why did it do that".

This system inverts the relationship. **The lifecycle is data, not code.** It lives in
[`workflows/sdlc.v1.yaml`](../workflows/sdlc.v1.yaml) as an explicit graph of stages,
dependencies, guards and gates. The engine contains no knowledge of software development
at all. It knows how to walk a graph, evaluate a gate, wait for a human and record an
event. Changing how software gets built here is a configuration change under review, not a
code deployment.

Three consequences follow, and they are what make the system defensible:

**A gate is a fact, not a hope.** Each stage declares what must be true to enter it and
what must be true for its output to be accepted. The engine refuses to advance a stage
whose exit gate fails, and it refuses to start one whose entry gate is unmet. The agent
has no way to bypass this because the agent never controls the transition.

**Every claim is checked against evidence.** A stage that says the tests pass does not get
to define what passing means. The real toolchain runs and the measured figure replaces the
claim. A stage that overstates its results fails outright.

**The record is the product.** Every state change, gate evaluation, approval, artifact,
decision and model call is an event in a hash-chained log. The run's metrics are derived
from that log rather than stored beside it, so no figure in a report can be set. It can
only be caused.

## Components

Ten projects, arranged so the engine depends on nothing but the domain.

```
src/
  Mandate.Core           The domain and every port the engine depends on.
                         Graph, state machine, events, artifacts, context,
                         decisions, policy contracts, the model port.
                         References nothing.

  Mandate.Orchestrator   The engine. Scheduling, gates, joins, guards,
                         retries, compensation, safe-stop, re-planning.
                         References Mandate.Core only.

  Mandate.Workflows      Loads and validates the YAML lifecycle. Renders it
                         as a Mermaid diagram.

  Mandate.Agents         The stage agents. One class driven by eleven
                         prompts, plus a scripted agent for testing the
                         engine without a model.

  Mandate.Llm            The model layer. Provider adapter, prompt library,
                         record and replay, spend ceiling, price book.

  Mandate.Policy         The policy engine and its declarative rule packs.

  Mandate.Persistence    SQLite event store, git-backed workspace, evidence
                         export, toolchain verification.

  Mandate.Observability  Metrics derived from the log, HTML run report,
                         tracing.

  Mandate.Cli            The composition root. The only place adapters are
                         bound to ports.

  Mandate.Api            A read-only HTTP surface over recorded runs.
```

The dependency rule is enforced by tests rather than by convention.
[`DependencyRuleTests`](../tests/Mandate.Orchestrator.Tests/Architecture/DependencyRuleTests.cs)
fails the build if `Mandate.Orchestrator` gains a reference to anything but `Mandate.Core`,
if any adapter depends on the engine, or if a second project starts referencing the model
vendor's SDK. That last rule is what keeps "swapping providers touches one file" true
rather than aspirational. See [ADR-0003](adr/0003-hexagonal-layering.md).

## The lifecycle

Eleven stages, seventeen edges, three conditional paths, three loop-backs, three human
checkpoints. Rendered from the definition itself:
[`docs/diagrams/sdlc.v1.mmd`](diagrams/sdlc.v1.mmd).

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

Four properties of this graph matter more than its shape.

**It is not linear.** Two guarded edges leave `requirements`, and which one is taken
depends on facts the run produced rather than on a script. An ambiguous requirement
diverts to a human. Work against existing code earns an extra analysis stage.

**It fans out and re-synchronises.** Test, review, security scan and documentation run
concurrently against the same tree. `release-readiness` joins on `all`, so it is a genuine
synchronising barrier: every one of the four must land before a release decision is
considered. `architecture` joins on `any`, because its two inbound paths are mutually
exclusive and waiting for both would deadlock.

**It loops back on evidence.** A failing test stage returns the work to implementation, as
does a review finding above the severity ceiling. Every loop-back declares its trigger
explicitly, `on: failure` or `on: success`, and validation rejects one that does not. An
implicit default here is dangerous in a specific way: a loop-back that fires on the wrong
condition still produces events, still re-plans, and still looks like progress, so it is
made impossible to write rather than left to convention.

**Humans own the irreversible decisions.** The design that everything downstream is built
on, and the release itself, both require a named human. The third checkpoint is
clarification, which is deliberately different: it puts a question back to the person who
asked for the work, so it is the one approval where segregation of duties is switched off.

## How a run executes

1. **Plan.** The engine loads and validates the graph, mints a time-ordered run id, seeds
   the run context with the request and the scenario, and records `RunPlanned`.

2. **Schedule.** It repeatedly finds nodes whose dependencies are satisfied and whose join
   policy is met, evaluates their guards, and classifies the rest as pending or skipped. A
   node that can never become eligible is skipped with the reason recorded, because a
   skipped stage with no explanation is indistinguishable from one that was forgotten.

3. **Gate on entry.** Entry conditions are preconditions, not blockers. A node whose entry
   gate fails stays pending and is reconsidered when the run reaches quiescence. This
   matters for the ambiguous path: `impact-analysis` waits for the ambiguity score to drop
   rather than failing the run the moment it is too high.

4. **Execute.** The node's agent runs with a scoped view of the context, the artifacts its
   upstream stages produced, and a read-only view of the workspace. It returns artifacts,
   context facts, decisions, proposed files and a record of every model call it made.

5. **Verify.** Where the node declares a fact the toolchain can measure, the toolchain
   measures it. See "Verification" below.

6. **Apply.** The engine validates every proposed path and commits the files as that
   node's commit in the run's git workspace. The agent never writes.

7. **Gate on exit.** Conditions are evaluated against recorded evidence. A failure sends
   the node back through its retry budget, then to its declared fallback.

8. **Record.** Every step appends an event whose hash commits to its predecessor.

A run that reaches a human checkpoint exits cleanly with code 3 and its state fully
preserved. `mandate resume` reconstructs the run from its own log and continues. Nothing
is held in memory between invocations.

## Governance mechanisms

### Gates

Eight condition kinds, each evaluated against recorded evidence rather than against an
agent's opinion:

| Kind | Reads |
|---|---|
| `artifact-exists` | An artifact of the named kind was produced |
| `any-artifact-exists` | At least one of several kinds was produced |
| `approval-held` | A named human granted the named role |
| `tests-pass` | `test.failures` is zero, measured |
| `coverage-at-least` | `test.coverage` meets the threshold, measured |
| `workspace-builds` | `implementation.builds` is true, measured |
| `no-secrets-committed` | `security.secrets-found` is false |
| `no-findings-above` | `review.highest-severity` is below the ceiling |
| `ambiguity-below` | `requirements.ambiguity-score` is under the ceiling |
| `policy-clean` | No unwaived policy violation |

Gate conditions fail closed. `no-secrets-committed` distinguishes three cases: no secrets
found, secrets found, and a value it cannot read at all. The third fails the gate and says
so, because "there are secrets" and "I could not tell" call for different actions from
whoever reads the log.

### Verification

Three of those gates read facts that a model could simply assert. The lifecycle definition
itself says the test gate "depends on the recorded result of an actual test run, never on
an agent's assertion that the code works", and for a while the code did not honour that.

It does now. Before the engine commits anything, `dotnet build` and `dotnet test` run over
a copy of the tree with the stage's proposed files applied. Counts come from the TRX report
and coverage from the Cobertura file, not from scraping console output that changes between
SDK releases. The measured figure replaces the claim, and three outcomes are distinguished:

- The claim agrees with the measurement. The measured value is recorded.
- The stage overstated its work. The stage fails, naming both figures. Coverage carries a
  0.10 tolerance because the model is asked for an estimate, and a slightly optimistic
  estimate is an estimate; a wide miss is a different claim about the work.
- Verification ran but produced no figure. **The stage fails. It does not fall back to the
  claim.** That last case is the one most easily got wrong, and a silent fallback would
  reintroduce the original defect by a quieter route.

See [ADR-0015](adr/0015-verified-not-claimed.md).

### Policy

Ten rules across three packs (security, compliance, change control), each declarative YAML
with a mandatory `rationale` field. A rule without a stated reason cannot be reviewed, only
obeyed.

A violation blocks. A human can waive it with `mandate waive`, which records the rule, the
waiving human and the reason as an event. The waiver overrides the block; it does not
silence the finding, which remains in the log and in the report. See
[ADR-0010](adr/0010-policy-as-data-waivers-on-the-record.md).

### Reliability

Retries are bounded per node with exponential backoff and jitter. When the budget is spent,
the node's declared fallback applies: fail the node, hand off to a human, or compensate.

Compensation is a real `git revert` of the node's real commits, newest first, leaving both
the change and its reversal in the history. A rollback that leaves no trace is not an audit
trail.

It can also fail. A sibling stage running in parallel may have touched the same file, in
which case a clean revert does not exist. The revert is abandoned, the tree is reset to a
clean state, and the compensation is reported as failed with how far it got and a note that
a human must decide what the tree should contain. This is a governance outcome, not a
crash, and it took a real brownfield run to find it.

Safe-stop is cooperative: `mandate stop` sets a marker, the engine halts at the next node
boundary with state preserved, and in-flight work is allowed to finish rather than being
killed mid-commit.

### Dynamic re-planning

Artifacts are content-addressed, so "did this input change?" is a digest comparison rather
than a heuristic. When a human amends an upstream input with `mandate amend`, or when a
loop-back re-runs a stage that produces different output, the engine invalidates every node
downstream that depended on it, returns them to the plan, and re-evaluates their gates.

Critically, **it revokes approvals that were granted against the invalidated work**. A
tech-lead who approved a design does not have their signature silently carried over to a
different design. Re-planning is bounded by a budget; when it is exhausted the engine
records `ReplanRefused` rather than looping, because a loop that stopped turning is
something a reviewer needs to see.

See [ADR-0011](adr/0011-incremental-replanning.md).

### Audit

Every event carries a SHA-256 hash over its content and its predecessor's hash. The chain
detects eight distinct failures: a missing origin, a sequence gap, a duplicate sequence, a
bad genesis link, a broken link, altered content, a foreign run's event, and a timestamp
that goes backwards.

The store enforces append-only at the database level with SQLite triggers that raise on any
UPDATE or DELETE. You cannot quietly edit history even with direct database access.

`mandate audit verify` proves it for one run or all of them.

### Metrics

Derived from the log by a pure function, never stored. Success rate, retry rate, rollback
rate, gate block rate, mean time to recovery, end-to-end latency, stage percentiles,
approval wait, autonomy ratio, re-plan count, policy activity, and model spend.

Because nothing is stored, nothing can be set. A figure in a report is a consequence of
what happened. See [ADR-0012](adr/0012-metrics-derived-not-recorded.md).

## The agents

Eleven stages, one class, eleven prompt files.

What distinguishes a requirements analyst from a security scanner is the instruction it is
given, the model it runs on, the outputs the workflow declares it must produce, and the
gates its output must pass. All four are data, in `prompts/` and in the workflow. Eleven
near-identical classes would move that difference into code, where a reviewer would have to
read eleven files to discover that ten of them do the same thing. See
[ADR-0014](adr/0014-agents-are-prompts-not-classes.md).

**The workflow's declaration is enforced against what the model returns, in both
directions.** A declared output the model omitted fails the stage. So does an *undeclared*
one, and that is the direction with teeth: gates are written against what a node declares,
so an extra artifact is checked by nothing, and an extra context fact could be branched on
by a guard that validation never saw. A stage able to emit facts nobody validated can route
a run down a path nobody reviewed.

Models are matched to the risk of the stage. Sonnet for requirements, impact analysis,
architecture, implementation, review and release; Haiku for intake, clarification, testing,
security scanning and documentation. The model that ran each stage is recorded in the audit
log, so model provenance is part of the evidence.

How hard a stage may think is also declared per prompt, as `effort`. This is not a tuning
knob. On models that reason adaptively the thinking is charged against the same output
ceiling as the answer, so a stage given a hard problem and a modest ceiling can spend its
entire budget reasoning and return nothing at all. A stage can therefore consume its entire
allowance reasoning and return nothing, which is why effort is declared alongside the
ceiling it competes with and why an empty answer is its own distinct failure rather than
being reported as truncation.

### The response format

Agents return a JSON envelope followed by delimited content blocks:

```
{ "summary": "...", "documents": [ { "kind": "source-patch", "path": "src/A.cs" } ],
  "facts": { "implementation.builds": "true" }, "decisions": [] }

@@@MANDATE-FILE src/A.cs
var re = new Regex("\d+");
@@@MANDATE-END
```

The envelope is JSON because it is short and structured, and models write short JSON
reliably. Content is *not*, because it is kilobytes of source code and JSON requires all of
it escaped. Three separate stages died on that: a raw newline in twenty kilobytes of C#, an
unescaped quote 2.4 kB into a design document, and the backslash hazard in every regular
expression the system will ever generate. Each threw away a complete and correct answer.

Blocks are matched to documents by path in both directions. A document with no block fails
the stage, and so does a block for a path nothing declares, because content nothing declares
would reach the tree with no artifact recording where it came from.

## The model layer

All model access goes through `ILlmClient`, one method, no streaming, no tools. Four
implementations compose:

| Mode | Behaviour |
|---|---|
| `live` | Calls the provider |
| `record` | Calls the provider and keeps every exchange |
| `replay` | Answers from recordings. No key, no network, no spend |
| `stub` | Synthetic answers. No model is consulted |

**Replay is the default.** The mode that spends money should be the one somebody typed.

A cassette is keyed by the content address of the request: prompt id, version, model,
system prompt, conversation and output ceiling, hashed with the same canonical serializer
the audit chain uses. A recording is therefore an answer to a *question*, not to an
occasion, so any run asking the same question gets it and the scenarios share what they
have in common.

Two rules are enforced rather than documented. A cassette that does not hash to its own
file name is refused, because a hand-edited recording would make a run's evidence describe
a conversation that never happened. And a prompt value containing a run identifier or a
wall-clock timestamp is refused at render time, because it would make every run ask a
question no cassette has ever seen.

A replay miss is a hard failure. Falling through to a live call would mean a reviewer
setting out to reproduce a recorded run silently getting a different one, billed to someone
else's account, with nothing in the evidence saying so.

See [ADR-0007](adr/0007-llm-record-replay.md).

### Spend as a governed budget

An agentic system that can loop can spend without bound, and "we monitor usage" is not a
control. `BudgetedLlmClient` caps a run at a number of calls, a number of tokens and a
number of dollars, and **refuses the call that would breach the ceiling** rather than
reporting the overrun afterwards. The check is made against the call's worst case, because
the real cost is not knowable until the money is spent.

Prices are dated data in [`config/model-pricing.yaml`](../config/model-pricing.yaml) with
the source URL, not constants in code. A model absent from the file is unpriced, and its
cost is reported as unknown rather than as zero, because "we do not know what this cost"
and "this cost nothing" are different statements.

Every call is a `ModelCalled` event carrying four token counts at four different rates, the
model that actually answered, and the cost as an integer count of nano-dollars. Integer
rather than decimal because a decimal carries scale, and `0.10` would hash differently from
`0.1`. See [ADR-0013](adr/0013-model-spend-as-a-governed-budget.md).

## Where state lives

| State | Where | Why |
|---|---|---|
| Run history | SQLite, append-only | The only source of truth. Run state is a fold over it. |
| Produced files | A git repository per run | Per-stage commits make the change reviewable and genuinely revertible. |
| Model exchanges | `cassettes/`, content-addressed files | Reviewable as a diff; a new recording shows up in `git status`. |
| Prompts | `prompts/`, versioned files | A prompt is a specification, so a change to one is reviewable as a diff. |
| Lifecycle | `workflows/sdlc.v1.yaml` | Changing how software is built is a configuration change under review. |
| Policy | `workflows/policies/*.yaml` | Same reasoning. |
| Prices | `config/model-pricing.yaml` | Vendor prices change; a constant would go stale silently. |
| Metrics | Nowhere | Derived on demand, so they cannot be set. |

## Key decisions

Seventeen ADRs in [`docs/adr/`](adr/). The ones that shaped everything else:

| ADR | Decision |
|---|---|
| [0003](adr/0003-hexagonal-layering.md) | The engine depends on ports only; the CLI is the sole composition root |
| [0004](adr/0004-declarative-workflow.md) | Workflows are declarative YAML graphs, not code |
| [0005](adr/0005-event-sourced-state.md) | Run state is event-sourced with a hash-chained log |
| [0006](adr/0006-git-backed-workspace.md) | Output lands in a per-run git workspace; rollback is a real revert |
| [0007](adr/0007-llm-record-replay.md) | Model access sits behind a port with record and replay |
| [0008](adr/0008-restricted-guard-grammar.md) | Conditional paths use a closed predicate grammar, not an expression evaluator |
| [0009](adr/0009-agents-propose-the-engine-applies.md) | Agents propose file changes; the engine applies them |
| [0012](adr/0012-metrics-derived-not-recorded.md) | Metrics are derived from the log, never recorded alongside it |
| [0014](adr/0014-agents-are-prompts-not-classes.md) | An agent is a prompt and a declaration, not a class |
| [0015](adr/0015-verified-not-claimed.md) | Build and test results are measured; overstating them fails the stage |

ADR-0008 deserves a note. Edge guards use a closed predicate grammar with a hand-written
lexer and parser, not a scripting engine. A workflow file that can evaluate arbitrary
expressions is a code-execution surface wearing a configuration file's clothes. The grammar
supports comparison, boolean composition and nothing else, and it fails closed on anything
it does not recognise.

## Deliberate non-goals

Boundaries drawn on purpose, so the shape of the system is unambiguous.

- It does not deploy anything. `release-readiness` produces a go or no-go recommendation
  and an evidence pack for a human; it does not ship.
- It does not run agents in parallel *within* a stage. Concurrency is between stages.
- It has no web UI. There is a CLI, and a read-only HTTP surface for inspecting runs.
- Agents cannot write to the workspace. They propose, and the engine applies.
- The read-only API is deliberately read-only. Mutating a run is an audited act that
  records who performed it, and those go through the CLI where the acting human is named.
  An HTTP surface that could approve its own runs would defeat the segregation-of-duties
  control the system is built around.
