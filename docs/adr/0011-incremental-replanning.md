# ADR-0011: Re-planning is incremental and lazy, and withdraws the approvals it invalidates

- **Status:** Accepted
- **Date:** 2026-09-16
- **Decider:** Human (Muskan Jain)

## Context
The brief requires the orchestrator to "dynamically re-plan when upstream outputs change while
maintaining governance". Two things happen in a real lifecycle that demand it: a human decides
an input was wrong after work has been done on it, and a stage returns control to an earlier
one — a clarification re-entering requirements, a failing test sending the code back.

The naive implementation of both is a restart: invalidate the trigger and everything
downstream, redo it all. That is correct and wasteful, and on a lifecycle with a human
checkpoint it is also rude — it discards approvals for work that may not have changed at all.

## Decision
**Invalidation is lazy.** A re-plan invalidates only the stage it names. Whether anything
downstream is invalidated is decided *after* that stage re-runs, by comparing what it produced
against what it produced before. Because artifacts are content-addressed, that comparison is
exact: identical bytes mean nothing downstream needs redoing.

**Loop-back edges declare which outcome fires them** — `on: success` or `on: failure`. A
clarification returns to requirements when it succeeds; a test returns to implementation when
it fails.

**Re-planning is bounded** by a per-run budget. When it is spent the re-plan is *refused and
recorded*, and nothing is transitioned.

**Governance is re-applied, not carried over.** Invalidating a stage withdraws any approval
held for it.

**A loop-back grants context scope in the direction it flows.** The stage being re-run can see
what the stage that sent it back produced.

## Alternatives considered
| Option | Why we rejected it |
|---|---|
| Invalidate the trigger and all descendants at once | Simple, and it is a restart with a nicer name. It also discards approvals for work that may be byte-identical afterwards, which trains people to re-approve without looking. |
| Compare timestamps rather than content | Every re-run changes a timestamp, so everything downstream would always be invalidated. Content addressing is already there; using it is free. |
| Loop-backs with no declared trigger | Tried it, defaulting to "on success". `test -> implement` then re-planned the implementation *every time the tests passed* — 457 events on a run that should take 123. The failure looked like progress, which is the worst way for a bug to present. |
| Unbounded re-planning | A lifecycle that re-plans without limit is not adaptive, it is stuck, and from the outside it looks busy. |
| Blocking the affected nodes when the budget runs out | Attempted, and the state machine refused it: a succeeded node cannot become blocked. The refusal was right. Those nodes are not broken — their results stand — it is the loop that stopped turning, and marking finished work as blocked misdescribes which thing needs attention. |
| Keeping approvals across an invalidation | Cheaper for the human and dishonest: it puts their name against output they never saw. |
| Deciding idempotence from the node's current state | Wrong, and it took a test to show it. By the time a run finishes, an amended stage has been redone and is settled again, so a second resume would redo it a second time. Idempotence is read from the log instead: a re-plan recorded after the amendment is the evidence it was acted on. |

## Consequences
- A human amendment produces two recorded re-plans: one invalidating the named stage, and — if
  and only if the re-run produced something different — one invalidating what was built on it.
  The second names the approvals it withdrew.
- Every re-plan records what it left alone as well as what it undid, so "why was this not
  redone?" is answerable from the log.
- A refused re-plan is recorded rather than dropped. A loop that stopped turning is something a
  reviewer needs to see, and silence would look like it never came up.
- Re-planning interacts with concurrency, so re-plans are queued while stages execute and
  applied between scheduling passes — the same safe-boundary rule as a stop.
- The scripted agents' output depends on the context they could see, which is what makes the
  cascade observable at all. A stand-in that ignored its inputs would produce identical bytes
  forever and make the incremental path untestable.

## Validation
- A re-run that produces identical output must leave downstream work untouched; one that
  produces different output must invalidate it. Both are asserted.
- An invalidated stage holding an approval must lose it, and the withdrawal must name the role.
- A loop-back with no declared trigger must fail workflow validation.
- Applying an amendment twice must redo the work once.
