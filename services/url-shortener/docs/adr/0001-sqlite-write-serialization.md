# ADR 0001: Native SQLite write serialization for code uniqueness and click-count correctness

## Context

Three acceptance criteria bind together into one problem: a requested alias that is already
taken must return 409 and create nothing even under concurrent identical requests (AC19);
generated codes must be a monotonically increasing base62 sequence, unique across both aliases
and generated codes (AC18); and repeated redirects must each increment the click count by
exactly 1, with no lost updates under concurrency (AC15). The store is fixed by the requirement
as SQLite, and the NFRs state a single SQLite instance is assumed — there is no multi-instance
or distributed-coordination requirement to design around.

SQLite allows exactly one writer transaction at a time regardless of how the application
structures its code; the question is not whether to add concurrency control, but whether to let
SQLite's own locking do that work or to duplicate it in the application.

## Decision

Use SQLite's native concurrency control as the sole source of truth for correctness:

- `PRAGMA journal_mode=WAL` so reads (redirects, stats) are not blocked by an in-flight write.
- `PRAGMA busy_timeout` so a writer arriving while another write is in progress waits briefly
  instead of failing immediately.
- A single `UNIQUE` constraint on one shared `code` column, covering both aliases and generated
  codes. A `UNIQUE` violation on the alias-insert path is the direct, atomic signal for 409.
- Generated codes come from `id INTEGER PRIMARY KEY AUTOINCREMENT` — no separate counter is
  maintained. The rare case where `base62(id)` collides with a pre-existing alias is handled by
  a bounded retry that inserts a fresh row to obtain a new, higher `id`, rather than by any
  application-level locking around the whole operation.

## Alternatives considered

- **Application-level global write mutex** (a `SemaphoreSlim` around all writes): rejected
  because it duplicates a guarantee SQLite's transactions and unique index already provide, adds
  a whole lock-lifecycle to reason about, and still would not by itself resolve the
  alias/generated-code collision case — that logic is needed either way, so the mutex buys
  nothing but a wider critical section and a new class of deadlock/starvation bugs to avoid.

- **Separate counter table for monotonic IDs**, incremented in its own transaction ahead of the
  main insert: rejected because it adds a second write (and a second lock acquisition) to every
  create request for no benefit — SQLite's `AUTOINCREMENT` rowid is already a durable,
  monotonic, gap-tolerant sequence, and splitting the counter out only creates a second place
  for the two writes to disagree if one succeeds and the other doesn't.

- **Optimistic pre-check** (`SELECT` for the alias, then `INSERT` if absent): rejected outright —
  this is the textbook time-of-check-to-time-of-use race, and AC19 specifically tests for it:
  two concurrent identical-alias requests must yield exactly one 201, and a pre-check-then-insert
  cannot guarantee that.

- **Design for multi-instance coordination** (distributed lock service, or swap SQLite for a
  client/server database): rejected as solving a problem the requirement does not have. The
  NFRs explicitly assume a single SQLite instance; SQLite's file locking wouldn't help across
  processes/machines anyway, so this would be speculative infrastructure with no acceptance
  criterion behind it.

## Consequences

**Liked:**
- Alias uniqueness is enforced by the same mechanism the data lives in — there is no window,
  however small, where the application's view of "is this alias taken" can be stale relative to
  what actually gets committed.
- No new concurrency primitives, lock objects, or ordering rules to get wrong in application
  code; the entire correctness argument for AC19 is "the database has a unique index."
- Matches the stated scope (single SQLite instance) exactly, with no unused generality.

**Disliked (accepted anyway):**
- All writes across the whole service serialize through SQLite's single-writer lock. At the
  scale implied by this requirement that is a non-issue, but it is a real ceiling that has not
  been load-tested, and it would need to be revisited before any future requirement asks for
  higher write throughput or multiple app instances.
- The bounded retry-on-collision path (§5 of the design) is a genuine new failure surface: it
  can, in principle, be exhausted (documented in the design doc as a knowingly-left failure
  mode), and it leaves harmless-but-permanent orphaned rows behind when it fires.
- This design is explicitly not portable to a multi-instance deployment without rework — SQLite
  file locking does not coordinate across separate processes on separate machines. That rework
  is out of scope today, but this decision is the reason it would be needed later.