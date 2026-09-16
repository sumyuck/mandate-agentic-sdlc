# ADR-0013: Model spend is a governed budget, not a line on a dashboard

- **Status:** Accepted
- **Date:** 2026-09-16
- **Decider:** Human (Muskan Jain)

## Context
An agentic system that can loop can spend without bound. This one loops on purpose: stages
retry within a budget, a failing test suite sends the plan back to implementation, and an
amended requirement re-plans everything downstream of it. Every one of those paths is
correct behaviour, and every one of them costs money.

"We monitor usage" is not a control. By the time a dashboard shows an overrun, the money is
gone, and in a regulated setting the question asked afterwards is not *how much* but *what
stopped it* — the same question asked of any other unbounded resource a system consumes.

Cost is also evidence. Reliability metrics already answer "did this run work, and how
hard did it have to try"; spend answers "what did trying cost", and a review that can see
one but not the other cannot judge whether an automated lifecycle is worth running.

## Decision
Three things, all mechanical.

**1. Prices are dated data, not constants.** `config/model-pricing.yaml` carries a
`source` URL and an `as-of` date alongside four rates per model — uncached input, output,
cache read, cache write — because those are billed at four different rates and a single
blended figure cannot be re-derived. A model absent from the file is **unpriced**, and the
system reports its cost as unknown rather than as zero: "we do not know what this cost" and
"this cost nothing" are different statements, and only one of them is ever true.

**2. A budget refuses the call, it does not report the overrun.** `BudgetedLlmClient` caps
a run at a number of calls, a number of tokens, and a number of dollars. The check runs
*before* the call, against its worst case — the input as written plus the full output
ceiling the prompt declared — because the real cost is not knowable until the money is
spent. It will occasionally refuse a call that would have fitted. That is the correct
direction for a ceiling to err in.

Three ceilings rather than one, because they fail in different ways: a runaway loop
breaches the call count long before the dollar cap, a stage that pastes a repository into a
prompt breaches the token cap on its first call, and the dollar cap is the one a human
actually agreed to.

**3. Every call is an audited event.** `ModelCalled` records the prompt and version, the
model that *answered* (an alias resolves to a dated snapshot, and it is the snapshot that is
billed), the request fingerprint, four token counts, the cost, the stop reason and the
duration — for **every** call, including calls made by an attempt that then failed. Cost is
stored as an integer count of nano-dollars rather than a decimal, because a decimal carries
scale and `0.10` would hash differently from `0.1`, which the audit chain would notice and
nobody would enjoy diagnosing.

Because the event is in the log, spend is derived by the same fold that derives everything
else (ADR-0012). There is no separate ledger to keep in step.

## Alternatives considered
| Option | Why we rejected it |
|---|---|
| Report spend after the fact | Not a control. The money is already gone, and the run that overran is the one you most wanted to stop. |
| A single dollar ceiling | Cannot bind an unpriced model, and says nothing useful about a run looping on cheap calls. |
| Count only successful attempts | A run that burned its retry budget would look cheaper than one that succeeded first time — exactly backwards. |
| Store cost as a decimal | Decimal carries scale, so the same amount can serialise two ways and hash two ways. Integers cannot. |
| Hard-code prices in C# | Vendor prices change. A constant goes stale silently; a dated file goes stale in a diff somebody has to approve. |

## Consequences
- A run cannot spend more than it was allowed, and the refusal is a stage failure that flows
  into the same retry, fallback and compensation machinery as any other.
- Replayed and stubbed calls are costed at the same prices and counted against the same
  ceiling. The budget bounds what a run *would* cost, not what this particular execution
  happens to bill — otherwise a run that breaches its budget live would sail through in
  replay, and replay would stop predicting anything about the run it replays. Whether money
  actually changed hands is recorded per call, as the call's source.
- The price file needs maintaining. `mandate llm check` prints its `as-of` date next to
  every figure, so a stale one is visible rather than merely wrong.
- An unpriced model can only be bounded by calls and tokens. `mandate llm check` fails when
  the model that answered is unpriced, because a layer that cannot cost its own answers has
  a spend ceiling that does not bind.

## Validation
- `mandate llm check` reports the model that answered, its four token counts, and a cost —
  and exits non-zero if that model has no published price.
- `BudgetAndPricingTests` pins the published figures against the arithmetic: a million input
  tokens of Sonnet 5 is exactly $2.00, and a million of each token kind on Haiku 4.5 is
  exactly $7.35.
- The ceilings are tested by asserting the provider was never reached, not merely that an
  exception was thrown.
