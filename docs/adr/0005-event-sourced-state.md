# ADR-0005: Run state is event-sourced in SQLite with a hash-chained audit log

- **Status:** Accepted
- **Date:** 2026-09-16
- **Decider:** Human (Muskan Jain)

## Context
The brief requires stateful, non-linear execution, resumability, human approval checkpoints
that survive process exit, "audit-grade observability and traceability", and reliability
metrics including MTTR. A mutable status column cannot answer "how did this run reach this
state, and who authorised it?" — and for a regulated financial client, that is the question
that matters.

## Decision
Every state transition, decision, gate verdict, policy evaluation, approval and retry is an
append-only `RunEvent` persisted in SQLite. Current run state is a projection over the event
stream, never the source of truth. Each event stores the SHA-256 hash of its canonical bytes
plus the hash of its predecessor, forming a chain per run. `mandate audit verify <runId>`
re-walks the chain and detects any insertion, deletion or edit.

## Alternatives considered
| Option | Why we rejected it |
|---|---|
| Mutable state table | No history, so no lineage, no MTTR, no replay, and nothing to audit. Fails four separate requirements at once. |
| JSON files per run | Workable for state, but no transactions, no query surface for metrics, and concurrent node completions would race. |
| PostgreSQL | Better at scale, but requires a running server. The submission must clone and run; a reviewer without Docker up would see nothing. |
| Plain event log, no hash chain | Gives lineage but not integrity. "The log says so" is weaker evidence than "the log is provably unedited", and the chain costs about thirty lines. |

## Consequences
- Metrics (success rate, retry and rollback frequency, MTTR, stage and end-to-end latency) are
  *derived* from the event stream. None can be hardcoded, because none are stored.
- Resume and replay fall out of the design rather than being bolted on.
- Canonical serialization becomes load-bearing: reordering a persisted record's members
  changes its hash. Locked down and tested in `MandateJson` / `MandateJsonTests`.
- Reads cost a projection. Irrelevant at prototype scale; noted as a scaling limitation.

## Validation
Kill a run mid-flight, resume it, and the outcome must be identical. Tamper with one byte of
one persisted event and `audit verify` must fail and name the broken link.
