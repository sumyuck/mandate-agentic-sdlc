# Final engineering summary

## What was built

Mandate, an orchestrator for a governed software development lifecycle. It takes a
requirement in plain English, decomposes it across eleven declared stages, has language
models perform each one, holds their output to gates it cannot skip, stops for a named
human at the decisions that matter, and records every step in a tamper-evident log.

The URL shortener under [`services/`](../services/) is the fixture, not the product. It
exists so the orchestrator had something real to build. Every file in it was written by
the system.

| | |
|---|---|
| Source | 10 projects, 234 C# files |
| Tests | 867, all passing, warnings as errors |
| Decisions | 17 ADRs |
| Lifecycle | 11 stages, 17 edges, 3 conditional paths, 3 loop-backs, 3 human checkpoints |
| Runs recorded | 3, totalling 578 events, all chains intact |

## The plan, and how it held

The plan was seventeen parts, P0 to P16, ordered so that the orchestrator would be
complete and defensible before any model was involved. That ordering was the single best
decision in the project. By the time real models arrived at P11, the engine had 750 tests
behind it, and every failure after that point could be attributed to the model layer
rather than argued about.

Two scoping decisions shaped the result and are worth stating directly.

**The fixture is deliberately modest.** The URL shortener runs on SQLite with an
in-process cache rather than a distributed stack. None of the brief's eight core
requirements concerns the fixture's infrastructure; all eight concern the system that
produces it. Effort spent on message brokers and cluster manifests is effort not spent on
the orchestration layer the brief calls the critical differentiator.

**The target is .NET 10 LTS.** Supported to November 2028, where .NET 8 leaves support in
November 2026. In a regulated domain a support lifecycle is a compliance input rather than
a preference, which is why the framework is pinned centrally and a test fails the build if
any project declares its own. See [ADR-0002](adr/0002-target-framework.md).

## Rationale for the decisions that shaped everything

**The lifecycle is data.** The engine contains no knowledge of software development. It
walks a graph, evaluates gates, waits for humans and records events. Changing how software
gets built is a configuration change under review, not a code deployment. This is what
makes the gates meaningful: they are declared next to the stage they guard, in a file a
reviewer can read without knowing C#.

**Agents propose, the engine applies.** No agent writes to the workspace. They return
files they would like written, and the engine validates every path and commits them as
that stage's commit. "The agent may act in the sandbox" is then a boundary something
enforces, not a description of intended behaviour.

**Evidence over assertion.** Three gates read facts a model could simply claim. The real
toolchain now runs and the measured figure replaces the claim. A stage that overstates its
results fails, naming both numbers. The case most easily got wrong is the third one: when
verification runs but produces no figure, the stage fails rather than falling back to the
claim.

**Metrics are derived, never stored.** A number in a report is a consequence of what
happened. It cannot be set, only caused.

**An agent is a prompt and a declaration, not a class.** Eleven near-identical classes
would be eleven places for retry semantics and cost accounting to drift. What genuinely
differs between a requirements analyst and a security scanner is its instructions, the
model it runs on, the outputs the workflow declares, and the gates its output must pass.
All four are data.

## Artifacts

| What | Where |
|---|---|
| The orchestrator | [`src/`](../src/) |
| The lifecycle | [`workflows/sdlc.v1.yaml`](../workflows/sdlc.v1.yaml) |
| Policy packs | [`workflows/policies/`](../workflows/policies/) |
| Agent prompts | [`prompts/`](../prompts/) |
| The service the system built | [`services/url-shortener/`](../services/url-shortener/) |
| Run evidence | [`runs/`](../runs/) |
| Model recordings | [`cassettes/`](../cassettes/) |
| Architecture | [`ARCHITECTURE.md`](ARCHITECTURE.md) |
| Scenarios | [`scenarios/`](scenarios/) |
| Testing, limitations, trade-offs | [`TESTING.md`](TESTING.md) |
| Requirement traceability | [`TRACEABILITY.md`](TRACEABILITY.md) |
| Decisions | [`adr/`](adr/) |

## The controls catching real problems

The point of a governed lifecycle is that its controls fire on things a person would
otherwise miss. Two examples from the recorded runs, neither of which anyone asked the
system to look for.

**A genuine SSRF bypass, caught before release.** Reviewing code the system had written
itself, the review stage found that IPv4-mapped IPv6 literals (`::ffff:127.0.0.1`) parse
as `InterNetworkV6`, miss all three IPv6 range checks and reach loopback, defeating the
private-range blocking the requirement explicitly asked for. It named the file, the
mechanism, the attacker's path and the fix. The severity gate held the change back until
it was addressed.

That is the entire argument for the design in one example: generated code stopped by a
control, on a real vulnerability, with the reasoning on the record.

**A stale governance signal, caught by the same control.** On another run the reviewer
flagged that a context fact was reporting the architecture as unapproved while
implementation proceeded. A human had in fact approved, so the report was wrong on the
facts and right on the substance: that fact could only ever read `false`, because the
agent writing it is not the approver and nothing updated it afterwards. No gate consulted
it, so it duplicated the real record badly. It was removed, and approval remains where it
always was, in the `ApprovalGranted` event and the `approval-held` gate.

A review stage that audits the lifecycle it is running inside is an unusual property, and
it came free from giving the reviewer the run's context rather than only its diff.

## A design principle the system is built on

Building this surfaced a principle that turned out to govern most of the design, and it is
worth stating because it is not obvious and it is not in the literature.

**Observability has to serve the actors, not only the auditors.**

An audit log answers "what happened" for a person reading afterwards. That is necessary
and it is not sufficient, because the components inside the loop also need to see things
in order to work at all. Every one of these is a case where a component held the
information and the wrong party could read it:

- **A retry must see why the last attempt failed.** Without it, a retry asks an identical
  question and receives an identical answer, and the retry budget is decorative against
  any deterministic error. Retries now carry the previous failure, and reproducibility
  survives because that failure is itself deterministic.
- **Diagnostics fed back into a loop must be stable.** Toolchain output carries timings
  and temporary paths. Left in, they make a prompt differ between two identical runs and
  no recording can ever match. They are normalised, and a test asserts two identical
  verifications produce byte-identical reports.
- **A contradiction must travel with its evidence.** Reporting that a stage claimed zero
  failures against thirty-seven measured tells it that it was wrong, not what to change.
  The toolchain output travels with the finding.
- **A stage cannot reason about what it was not shown.** The workspace view is bounded,
  so a withheld file is named explicitly and the stage reports that it could not assess
  it rather than inventing an assessment.
- **A reviewer must not judge work in flight.** Review and testing run concurrently, so
  the reviewer sees a snapshot taken before its sibling's work landed. Coverage is
  measured by a real test run and enforced by its own gate, and the reviewer is scoped out
  of it.
- **A human's answer must reach the stage that acts on it.** An approval note recorded
  only in the audit log satisfies accountability and changes nothing. Notes enter the run
  context as `run.approval-notes`, accumulated across rounds, so the loop-back that
  re-runs a stage is given the answer that was provided. A lifecycle with a clarification
  loop exists in order to be told something; one that cannot hear the reply performs
  consultation without doing it.

The last is the one worth dwelling on in a governed system. Human oversight that is
recorded but not routed is oversight in name only.

## Assumptions

Stated because they are load-bearing.

1. **A single process owns a run.** Two engines executing the same run concurrently is not
   supported. SQLite would serialise the writes; the run state would diverge.
2. **Prompts are a pure function of the requirement and upstream output.** This is what
   makes replay possible, and it is enforced: a prompt value containing a run identifier or
   a wall-clock timestamp is refused at render time.
3. **The workspace is text.** The verification sandbox is materialised from what the stage
   could read, which is source and configuration. A binary asset would not survive.
4. **A human is available at the checkpoints.** Runs park indefinitely. There is no timeout
   that auto-approves, deliberately.
5. **Published model prices are current.** [`config/model-pricing.yaml`](../config/model-pricing.yaml)
   carries the date and the source URL so staleness is visible in a diff.
6. **`dotnet test` can restore its packages.** Build verification works offline; test
   verification needs the NuGet cache warm, which it is after the solution has been built
   once. The template pins the same package versions the orchestrator uses, for this reason.

## Where the edges are

The full treatment is in [`TESTING.md`](TESTING.md) under "Scope boundaries". Two
properties of the current build are worth naming directly.

**The ambiguous scenario is held at its test gate.** The run scores the requirement,
routes it to a human, takes the answer, re-plans three times and converges from 0.35 to
0.1. The feature itself is implemented and verified: three files, and an exit gate that
passed on a measured `dotnet build`. Review, security scanning and documentation all pass
against it.

The test stage then produced no measured figure, and a gate with no test evidence in front
of it does not advance the work. That is the behaviour the gate exists for, and it is
worth having one run in the set where a control actually bites rather than three that all
take the happy path. A demonstration where nothing is ever refused has not demonstrated
that anything can be.

**Offline replay is exact up to a stage's first retry.** Verification output that reaches
a retry's prompt is normalised first, so timings and sandbox paths cannot vary the cassette
key between runs. That normalisation is implemented and covered by a property test. The
stub path walks the entire lifecycle offline with no recordings at all and is unaffected.

## What is verified, and how

Verified mechanically, and reproducible by anyone with the repository:

- The service builds and passes 96 tests **outside** the orchestrator. A test result the
  system grades itself on is not evidence, so that check is run independently.
- All three audit chains verify. The store refuses UPDATE and DELETE at the database level.
- 867 tests pass with warnings as errors and `dotnet format` clean.
- The architecture rules are tests, so breaking them fails the build rather than the review.

Where the boundaries of that verification sit:

- The system holds models to a declared contract, measures their claims against the real
  toolchain, and records what they did. Judging whether a design is *wise* remains a human
  decision, which is why the two irreversible decisions require a named approver.
- Performance characteristics of the generated service are out of scope for the fixture.
- One production lifecycle ships. The loader and validator are general, covered by 81
  tests and 34 validation rules.

## Roadmap

The design leaves room for each of these, and none of them requires reworking the engine.

1. **Retrieval over the workspace.** The bounded view is a correct but blunt answer. A
   repository substantially larger than the fixture wants relevance ranking rather than a
   larger ceiling.
2. **A second lifecycle.** The strongest demonstration that the lifecycle is genuinely
   data is a different one running on the same engine with no code change. It is a YAML
   file and a set of prompts.
3. **Parallelism within a stage.** The scheduler already supports the shape; a large
   implementation is currently one call.
4. **Per-stage cost modelling.** The spend guard bounds a run. Historical per-stage cost
   from the event log would allow budgets per stage and earlier refusal.
5. **A lease on the run id.** A small change to the journal port would allow more than one
   engine to share a store safely.

## Closing

The brief asked for non-linear, stateful execution with governance rather than simple
linear task chaining, and said the orchestration layer was the critical differentiator. The
system routes on facts it produced, runs four stages in parallel behind a synchronising
barrier, loops back on evidence, re-plans when upstream output changes, revokes approvals
that re-planning invalidated, refuses to advance on unmeasured claims, and records all of
it in a chain that detects tampering.

Three scenarios are recorded end to end with their evidence committed: 578 events, every
audit chain intact, four human approvals by named people who were not the requester, three
re-plans, and a service that compiles and passes 96 tests when built outside the
orchestrator that produced it.

The parts of the design that matter most are the ones that refuse. A gate that will not
advance on an unmeasured claim, a policy block that needs a named waiver, an approval that
re-planning revokes, and a spend ceiling that declines the call rather than reporting the
overrun afterwards. Anything can be built to succeed on a good day. What makes a system
trustworthy is what it does on a bad one.
