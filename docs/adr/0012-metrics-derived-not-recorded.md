# ADR-0012: Reliability metrics are derived from the log, never recorded alongside it

- **Status:** Accepted
- **Date:** 2026-09-16
- **Decider:** Human (Muskan Jain)

## Context
The brief asks the system to track reliability metrics — success rate, retry and rollback
frequency, MTTR, end-to-end latency. The conventional implementation increments counters as
things happen and stores the totals.

That produces numbers, and it produces a second source of truth. A counter can be incremented
by code that did not do the thing it counts, missed by a path that forgot to call it, or
adjusted later. None of those leave a trace. For a system whose central claim is that its
record is trustworthy, a metric that can disagree with the record is worse than no metric —
it lends the appearance of measurement to whatever the counter happens to say.

## Decision
**No metric is stored. Every figure is computed by folding over the run's event log.**
`MetricsCalculator` is a pure function: events in, numbers out. No clock, no configuration,
no storage.

Three consequences follow deliberately:

- The figures **cannot be set, only caused.** There is no code path that writes a success
  rate, so none that can write a wrong one.
- They are **reproducible by anyone holding the exported log**, including someone who does not
  trust this codebase.
- They are **exactly as trustworthy as the log**, and the log is hash-chained. "Our success
  rate is 94%" and "here is the tamper-evident record it was computed from" are different
  claims; this system can make the second.

The same rule governs the HTML report: it renders only what the log supports, and says so on
the page.

## Alternatives considered
| Option | Why we rejected it |
|---|---|
| Counters incremented as things happen | Fast to read, and a second source of truth that can drift from the events. A metric nobody can reconcile against the record is decoration. |
| A metrics table written at the end of each run | Same objection, plus it cannot answer a question nobody thought to ask while the run was going. Deriving means a new metric works on runs already recorded. |
| Prometheus/OpenTelemetry metrics as the system of record | Right tool for fleet-level operations, wrong one for per-run evidence: the pipeline is lossy by design, sampled, and outside the audit boundary. Traces are emitted for operations; the audit log stays the record. |
| Counting retries by subtracting stage count from attempt count | Tried it, and it is wrong: a stage re-run by a re-plan starts its attempt numbering again and would be counted as a retry. A retry and a redo have different causes and should not share a number. |
| Including unrecovered failures in MTTR as infinity | Produces one number that hides the difference between slow recovery and none. They are excluded from the mean and reported separately. |
| Counting skipped stages as successes | Would make a narrow path through the lifecycle look more reliable than a thorough one. |

## Consequences
- Adding a metric is a change to one pure function, and it applies retroactively to every run
  already recorded.
- The calculator is trivially testable: build a log, assert the numbers. The tests read as
  statements about what a log can be made to say.
- Computation is O(events) per request rather than O(1). Irrelevant at a few hundred events
  per run; noted as a scaling limit rather than discovered later.
- Tracing is emitted separately through `ActivitySource` — a run is a trace, each attempt a
  span — using the base library so the engine takes no vendor dependency and keeps referencing
  only the domain. Where those spans go is the operator's decision.
- Operational logging is a decorator over the journal rather than a second set of call sites,
  so the operational log and the audit log cannot describe different runs.

## Validation
- Every metric has a test that builds a log and asserts the derived figure.
- A skipped stage must not raise the success rate; a re-planned stage must not count as a
  retry; an unrecovered failure must not be averaged into MTTR.
- The report must contain no script and fetch nothing.
