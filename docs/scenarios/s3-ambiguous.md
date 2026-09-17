# Scenario 3: Ambiguous

A requirement that cannot be built as written.

- **Run:** `run_20260917T020240Z_c6e2f4`
- **Requirement:** [`scenarios/s3-ambiguous.txt`](../../scenarios/s3-ambiguous.txt)
- **Evidence:** [`runs/run_20260917T020240Z_c6e2f4/`](../../runs/run_20260917T020240Z_c6e2f4/)
- **Outcome:** 8 of 9 attempted stages succeeded, 3 re-plans, 228 events, chain intact.
  The run is held at the test gate, which had no measured test result to advance on.
  Every governance behaviour this scenario exists to demonstrate is evidenced in its log.

## Reproduce it

```bash
dotnet run --project src/Mandate.Cli -- run "$(cat scenarios/s3-ambiguous.txt)" \
    --scenario ambiguous --template templates/brownfield --existing-code \
    --agents model --llm replay --as muskan
```

## The requirement

> The shortener needs rate limiting so one client cannot flood it.
>
> Add sensible limits to the create endpoint and make sure abusive clients get throttled
> rather than taking the service down for everyone else.

Every noun in that is a decision nobody has made. Limits per what: API key, IP address,
account? What counts as sensible? What does throttled mean: a 429, a queue, a slow
response? Is the limiter per process or shared across instances?

An agent that picks answers to those and starts building is not being helpful. It is
making product decisions on the requester's behalf and hiding them in code.

## What happened, in order

### The requirement was scored, not guessed at

`requirements` produced a spec and an ambiguity report, and set
`requirements.ambiguity-score` to **0.35**.

The score is not decoration. Two things read it:

```
guard:      requirements -> clarification   [requirements.ambiguity-score > 0.3]
entry gate: impact-analysis                 [ambiguity-below 0.3]
```

The guard fired, so `clarification` ran. The entry gate held, so `impact-analysis` stayed
pending. Downstream work did not start on an unresolved requirement.

### The question went to a human

`clarification` wrote the questions, recorded what it was assuming in the meantime, and
parked awaiting the `requester` role. The run exited with code 3.

This is the one approval in the lifecycle where segregation of duties is deliberately
switched off. The question is being put back to the person who asked for the work, so
requiring somebody else would make the stage unanswerable.

### The answer came back and the plan changed

```
ApprovalGranted   clarification   requester: "Fixed window of 60 create requests per API
                                  key per minute. Over the limit returns 429 with a
                                  Retry-After header. Counters live in process; no shared
                                  store, no distributed coordination. Only POST
                                  /api/v1/links is limited. Requests without an API key
                                  are limited by client IP on the same terms."

NodeInvalidated   requirements    "'clarification' completed and returns control to
                                  'requirements', which is re-run against what it produced."
ReplanPerformed   clarification   replan 1
```

The loop-back is declared in the workflow with an explicit trigger (`on: success`). The
requirements stage was invalidated and re-run.

### It converged

Re-run with the answer in scope, `requirements` scored the ambiguity at **0.1**. That
changed its output, which invalidated the work built on it:

```
ambiguity         0.35  ->  0.1
NodeInvalidated   clarification   "'requirements' re-ran and produced different output,
                                  so work built on it is no longer valid."
ReplanPerformed   requirements    replan 2
```

With the score under the ceiling, the entry gate opened. `impact-analysis` ran, then
`architecture`, which parked for the tech lead:

> tech-lead, alex: "In-process fixed-window limiter is proportionate; revisit if the
> service is ever run multi-instance."

Implementation, review, security scan and documentation all succeeded.

### The third re-plan

The test stage failed three times, so the declared loop-back returned the work to
implementation:

```
NodeInvalidated   implement   "'test' failed after 3 attempt(s), so 'implement' is redone"
ReplanPerformed   test        replan 3
```

Implementation re-ran and succeeded on its second attempt. The test stage failed again,
for the reason below, and the run ended.

## Why the answer has to be routed, not just recorded

This scenario is what forced an important property of the design.

Recording a human's answer in the audit log satisfies accountability. It does not, on its
own, change anything the system does, because the stage that re-runs on the loop-back
reads the run context rather than the log. An approval note that lives only in the log is
oversight in name: correctly attributed, and inert.

Approval notes therefore enter the run context as `run.approval-notes`, accumulated across
rounds rather than replaced, because a second round of questions does not retract the
first round's answer. `run.*` is in every stage's scope, so the re-run is given what the
human said.

The convergence above is that property working: the score moves from 0.35 to 0.1 because
the requirements stage was re-run holding an answer it could read.

**The general principle.** Observability has to serve the actors inside the loop, not only
the auditors outside it. Human oversight that is recorded but not routed is oversight in
name only.

## What was built, and where the gate holds

**The feature was implemented and verified.** The implementation stage produced three
files and its exit gate passed on a measured build:

```
implementation.commit        Add fixed-window rate limiting to link creation endpoint
implementation.files-changed 3        Program.cs, ClientKeyResolver.cs, README.md
exit gate workspace-builds   PASSED   implementation.builds = 'true': compiles.
```

Code review, the security scan and documentation all ran against it and passed.

**Then the test gate held the run.** The test stage reported that it had written a suite.
The engine did not take its word for it: it ran `dotnet test` over the proposed tree, and
the invocation produced no measured figure for `test.failures` or `test.coverage`. The
`coverage-at-least 0.75` gate therefore had nothing to read.

The engine does not fall back to the stage's own claim when the measurement is missing.
A claim that survives verification precisely when verification produces nothing is not a
control, so a stage that cannot be measured fails. The stage was failed, the declared loop-back returned the work to
`implement`, the plan was recomputed, and when the retry budget was spent the run stopped
short of release rather than advancing on evidence nobody had. That loop-back and re-plan
are the ones recorded as replan 3 above.

**This is the behaviour the design exists to produce.** Two of the three scenarios take
the happy path and end with a signed release. This one is the case that actually tests the
controls: a feature that builds, reviews clean and scans clean, sitting in front of a gate
with no test evidence behind it. Every incentive in an agent loop points towards shipping
it. The lifecycle refuses, records exactly why, and parks the work where a human can see
it. A demonstration in which no control ever fires has not demonstrated that the controls
work.

## Metrics

| Measure | Value | Basis |
|---|---|---|
| Success rate | 88.9% | 8 of 9 attempted |
| End to end | 3.6 min | 228 events |
| Stage p50 / p95 | 52ms / 3.2m | 10 stages run, 1 not required |
| Retry rate | 28.6% | 4 of 14 attempts |
| Mean time to recovery | none | 1 failure never recovered |
| Gate block rate | 7% | 3 of 43 conditions |
| Approval wait | 2.9s | 2 granted, 0 refused |
| Autonomy ratio | 87.5% | 14 agent attempts against human decisions |
| **Re-plans** | **3** | plan recomputed |

The three re-plans and the 28.6% retry rate are what distinguish this run. Scenario 1 had
zero re-plans and a 10% retry rate. The metrics show a harder run, which is what it was.

## What this scenario demonstrates

Ambiguity detected and quantified rather than guessed past. A guard that routes on that
quantity. An entry gate that holds downstream work back. A human checkpoint that is
deliberately not segregated because the question belongs to the requester. A loop-back
with an explicit trigger. Dynamic re-planning three times over, including approval-aware
invalidation. A gate that declines to advance work on evidence it does not have, holding
against a change that had already passed every other control. And the design property
underneath all of it, which only surfaced by driving the system with a real human in the
loop and checking whether their answer changed anything downstream.
