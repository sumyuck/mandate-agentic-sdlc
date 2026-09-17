# ADR 0002: Single-statement DELETE, affected-row count as the existence signal

## Context

`DELETE /api/v1/links/{code}` must permanently remove a link and its click statistics, return
204 when a row existed and was removed, 404 when it did not, and guarantee that removal of "the
link" and "its statistics" is atomic — no client-visible state where one is gone and the other
isn't (AC10). It must also be idempotent-safe under retry (AC9: a second delete on an
already-deleted code simply reports 404, not an error) and must not introduce any recoverable
trace of the deleted data (AC8, and the NFR stating data loss here is intentional and
irreversible).

The data model, unchanged by this work, already puts a link's URL, timestamps, and click count
in one row of one table (`links`); there is no separate statistics table to coordinate against.
The question this decision resolves is how `LinkRepository` should both detect whether a code
exists and remove it, given that ADR 0001 already established the precedent — for the create
path's alias-uniqueness check — that any separate "does it exist" step ahead of a mutating
statement is a race window that SQLite's own atomic statements make unnecessary.

## Decision

Implement `LinkRepository.DeleteAsync(string code)` as exactly one statement:

```sql
DELETE FROM links WHERE code = $code;
```

executed via `ExecuteNonQueryAsync`, using its returned affected-row count as the sole signal:
greater than zero means a row existed and was removed (204); zero means no such row existed,
whether it was never created or was already deleted (404). No existence check precedes the
delete, no `RETURNING` clause is used, and no column or row is retained to mark that a deletion
occurred.

## Alternatives considered

- **SELECT existence check, then a separate DELETE statement**: rejected. This is the same
  time-of-check-to-time-of-use shape ADR 0001 explicitly rejected for alias creation
  ("optimistic pre-check"), for the same reason: between the `SELECT` and the `DELETE`, a
  concurrent request (another delete, or in principle a recreate) can change what's true, and
  the two-statement round trip also costs more than the single-record write latency this
  operation is expected to match (per the NFR that deletion should complete within the same
  envelope as other single-record writes).

- **`DELETE ... RETURNING code`, mirroring `RedirectAsync`'s pattern**: rejected. `RedirectAsync`
  uses `RETURNING original_url` because it has to hand data back to the caller alongside the
  mutation — the redirect target. Delete has nothing to hand back; it only needs to know whether
  a row existed, which `ExecuteNonQueryAsync`'s affected-row count already answers without
  opening and disposing a `SqliteDataReader` for no data. Copying the `RETURNING` pattern here
  would apply a solution to a problem (retrieve-while-mutating) this operation doesn't have.

- **Soft delete**: mark the row with an `is_deleted` flag or a `deleted_at` timestamp instead of
  removing it, keeping the row (and thus recoverability) available. Rejected outright — the
  requirement states plainly that deletion is permanent, with no soft delete and no recovery
  (AC8, and the explicit NFR that this is intentional, not an oversight to mitigate). Any row
  left behind, flagged or not, is recoverable state the requirement forbids.

- **Explicit multi-statement transaction wrapping a delete from the link table and a delete from
  a separate statistics table**: rejected because it does not match the actual data model.
  There is no separate statistics table — `click_count` is a column on the same row as
  `original_url` — so this alternative would be building transaction-coordination machinery to
  guard against a form of partial failure (link deleted, stats not) that cannot occur given how
  the schema is actually shaped.

## Consequences

**Liked:**
- Symmetric with `RedirectAsync`'s existing precedent of letting the statement's own outcome
  disambiguate cases, rather than a separate existence probe — no new race-avoidance idiom is
  introduced to this codebase, the existing one is reused.
- Naturally idempotent: a second delete's affected-row count is zero with no special-casing
  needed to distinguish "already deleted" from "never existed" (AC9), because nothing is
  retained that could tell the two cases apart.
- Naturally atomic for AC10: the link record and its click statistics are removed by the same
  single SQL statement affecting the same single row — there is no window between "link gone"
  and "stats gone" for a concurrent reader to observe, because there is only one write.
- Minimal surface: one method, one statement, no new failure-handling branch beyond the
  affected-row check.

**Disliked (accepted anyway):**
- Correctness depends on the ADO.NET SQLite provider's affected-row count being accurate for a
  simple `DELETE`, which is well-established behavior but is still a dependency on driver
  semantics rather than on data the application reads back itself.
- No record of what was deleted is retained anywhere — not even transiently in memory for
  logging. If a future requirement asks for a deletion audit trail, this method's shape would
  have to change (read-before-delete, or a `RETURNING` clause carrying the row's prior state);
  that is out of scope today; and this decision is why it isn't free to add later.
- The outcome of a delete racing a concurrent redirect on the same code is left to whichever
  statement's write-lock acquisition happens first, with no ordering guarantee designed in
  (see `docs/design.md` §5, §9). This is not a correctness gap — no corruption results either
  way — but it is a nondeterminism the requirement does not resolve and this decision does not
  attempt to resolve either.