# Design — URL Shortener Service

## 1. Purpose and scope of this document

This describes the design for the five endpoints now in the service — create link, redirect,
stats, and the new delete — as a single-process ASP.NET Core minimal API backed by one SQLite
file. The first four sections below restate the standing design (unchanged by this work) only
to the extent needed to show where delete fits; the full rationale for create/redirect/stats
lives in the acceptance-criteria table and the request-flow sections, and ADR 0001 remains the
record for why SQLite's own concurrency control, not application-level locking, backs alias
uniqueness and click-count correctness.

This revision adds exactly one capability: `DELETE /api/v1/links/{code}`, permanently removing
a link and its click statistics. Nothing here anticipates authentication, bulk delete, soft
delete, or multi-instance deployment — those remain out of scope per `docs/requirements.md`
and are not designed around. In particular, no new component is introduced: the existing
components each gain one method or one route, and that is the entire shape of this change.

## 2. Components

- **HttpApiLayer** — ASP.NET Core minimal API endpoint mappings in `Program.cs`. Owns request
  parsing, status-code selection, and header construction. Contains no business logic itself.
  Gains one new route: `MapDelete("/api/v1/links/{code}", ...)`.
- **LinkService** — the application logic. Orchestrates validation and drives
  `LinkRepository` through create/redirect/stats/delete. Gains `DeleteAsync(string code)`, a
  thin passthrough to the repository, exactly mirroring the existing `RedirectAsync` and
  `GetStatsAsync` passthroughs.
- **UrlValidator** — pure URL validation. Unrelated to and untouched by this change; delete
  takes no URL.
- **CodeGenerator** — base62 encoding of an id. Unrelated to and untouched by this change.
- **LinkRepository** — the only component that opens a `SqliteConnection` or writes SQL. Gains
  `DeleteAsync(string code)`, which issues one `DELETE FROM links WHERE code = $code` and
  reports whether a row was actually removed via the statement's own affected-row count. See
  ADR 0002 for why this is a single statement with no existence pre-check, no `RETURNING`
  clause, and no soft-delete state.
- **SchemaInitializer** — creates the `links` table if absent, sets WAL and busy-timeout
  pragmas. Unchanged: deletion needs no new column, table, or index, and this component's
  design is explicitly *not* touched to avoid the temptation to add a `deleted_at`/`is_deleted`
  column here, which the requirement forbids.
- **Configuration** — `BaseUrl` and the SQLite connection string. Unchanged; delete uses the
  same connection string as every other repository method.

## 3. Request flow

Sections 3.1–3.3 (create, redirect, stats) are unchanged by this work and are restated here
only as context for §3.4 and the failure-mode analysis in §7–8; their behaviour, status codes,
and response bodies are exactly as before this change, which is itself an acceptance criterion
(AC11: no other endpoint's contract or behaviour changes).

### 3.1 `POST /api/v1/links` — unchanged
Validates url/alias/expiresAt, inserts via the repository's alias or generated-code path, using
the `code` column's unique index as the sole arbiter of "alias taken." See §5 for how this
interacts with delete: because delete performs a true `DELETE`, not a soft-delete or placeholder
update, a code freed by deletion is indistinguishable from a code that was never used, and this
path's existing logic requires no change to correctly accept it again.

### 3.2 `GET /{code}` — unchanged
Atomic `UPDATE ... SET click_count = click_count + 1 WHERE code = ? AND (expires_at IS NULL OR
expires_at > ?) RETURNING original_url`, disambiguated against a cheap existence check on no
match. A code that has been deleted takes exactly the same path as a code that was never
created: the `UPDATE` matches zero rows (the row is gone, not merely flagged), the follow-up
existence check also finds nothing, and the endpoint returns 404 — the same 404, with the same
body, as an always-unknown code.

### 3.3 `GET /api/v1/links/{code}/stats` — unchanged
Single `SELECT` by code; no row found returns 404. A deleted code's row is gone entirely, so
this returns exactly the 404 it would return for a code that never existed — no new branch, no
special-casing "was this deleted," because there is nothing left in the table to distinguish the
two cases by.

### 3.4 `DELETE /api/v1/links/{code}` — new

1. HttpApiLayer receives the request; no request body is read (none is defined).
2. `LinkService.DeleteAsync(code)` calls straight through to
   `LinkRepository.DeleteAsync(code)`.
3. `LinkRepository.DeleteAsync` executes:
   ```sql
   DELETE FROM links WHERE code = $code;
   ```
   via `ExecuteNonQueryAsync`, and returns `true` if the affected-row count is greater than
   zero, `false` otherwise. This is the entire operation — one connection, one statement, no
   explicit transaction wrapper, because SQLite already treats a single statement as atomic and
   the link record and its click statistics are the same row (see ADR 0002 and ADR 0001's note
   that `code` is the single shared uniqueness column).
4. HttpApiLayer maps the result:
   - `true` → `Results.NoContent()` (204, genuinely empty body — see §6 for why this must not
     reuse the existing `Results.Json(..., statusCode: ...)` pattern used by every other success
     response in this codebase).
   - `false` → `Results.Json(new ErrorResponse("not_found", "No link exists for this code."),
     statusCode: StatusCodes.Status404NotFound)` — the identical error object and message
     already used by the redirect and stats 404s, so all three 404 bodies stay indistinguishable
     (AC7).

Calling this twice on the same code: the first call's `DELETE` matches one row and returns
true → 204. The second call's `DELETE` matches zero rows (nothing left to match) and returns
false → 404, with no error and no special "already deleted" state — it is, at the SQL level,
indistinguishable from a code that was never created (AC9).

## 4. Data model

Unchanged. One table, one file, no migrations:

```sql
CREATE TABLE IF NOT EXISTS links (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    code          TEXT UNIQUE,
    original_url  TEXT NOT NULL,
    created_at    TEXT NOT NULL,
    expires_at    TEXT,
    click_count   INTEGER NOT NULL DEFAULT 0
);
```

Deletion removes the entire row, not a subset of its columns. Because `click_count` and
`original_url` are columns on the same row, "remove the link" and "remove its click statistics"
(AC2, AC3) are the same operation, and its atomicity (AC10) is the atomicity SQLite already
gives a single `DELETE` statement — no application-level transaction coordination is added,
because none is needed for a one-row, one-table removal.

State lives entirely in this SQLite file, as before. A deleted row leaves no trace anywhere in
this file: no tombstone column, no separate deleted-items table, nothing SchemaInitializer
creates or that any endpoint reads (AC8).

## 5. Interaction with existing invariants

- **Recreation after deletion**: `POST /api/v1/links` with an alias equal to a just-deleted code
  succeeds, because the unique index has nothing in it for that code anymore — the row is
  truly gone, not present-but-flagged. This is correct *because* delete is a real `DELETE`; had
  it been implemented as an update leaving a placeholder row (mirroring the harmless orphaned
  `NULL`-code rows left behind by the create-collision retry path), the unique constraint would
  still hold the code and recreation would wrongly 409. This is the one place a naive
  implementation could silently regress a currently-passing invariant, and it is the reason
  `docs/requirements.md`'s recreate-after-delete note is treated as settled, not ambiguous.
- **Concurrent delete and redirect on the same code**: both are single SQL statements against
  the same row; SQLite's WAL mode plus busy-timeout (already configured by SchemaInitializer,
  ADR 0001) serializes the two writers, so no corruption is possible — one completes before the
  other starts. Which one wins when they arrive close together is not specified by the
  requirement and is left unspecified by this design: a redirect that acquires the write lock
  fractionally before a concurrent delete will count its click and succeed; one that loses the
  race will see the row already gone and return 404. This is a knowingly-left behavior, not a
  bug (§8).
- **Route dispatch**: `MapDelete("/api/v1/links/{code}")` is a different HTTP method on a
  distinct, more specific template than `MapGet("/{code}")`, and ASP.NET Core's router
  dispatches on method as well as template — the two do not collide. This is asserted here as a
  design fact to be exercised by an actual request in testing, not left as an unverified
  assumption.

## 6. The 204 response is new shape in this codebase

Every existing success response in `Program.cs` is `Results.Json(...)` with an explicit status
code and a body. `Results.NoContent()` is a new pattern here specifically because 204 must have
no body at all — copying the existing `Results.Json(..., statusCode: 204)` idiom by analogy
would attach a JSON body to a response the contract requires to be empty (AC1). This is called
out explicitly because it is the one place in this change where the established convention in
the file is the wrong thing to copy.

## 7. Acceptance criteria coverage (this requirement)

| AC | How it's met |
|----|--------------|
| 1 | §3.4 step 4: `Results.NoContent()`, no body |
| 2 | §3.4 step 3: `DELETE FROM links` removes the row; no subsequent `SELECT` matches it |
| 3 | Same row holds `click_count`; same `DELETE` removes it — §4 |
| 4 | §3.4 step 4: zero affected rows → 404 |
| 5 | §3.2: `GET /{code}` finds no row post-delete → 404, same as always-unknown |
| 6 | §3.3: stats query finds no row post-delete → 404, same as always-unknown |
| 7 | All three 404s use the identical `ErrorResponse("not_found", "No link exists for this code.")` |
| 8 | §4: no tombstone column, no flag, no hidden state — the row is gone, not marked |
| 9 | §3.4 last paragraph: second delete affects zero rows → 404 |
| 10 | §4: link and its stats are one row; one `DELETE` statement removes both atomically |
| 11 | §3.1–3.3 restated as unchanged; no request/response schema, status code, or side effect of any other endpoint is touched by this change |

## 8. Failure modes designed for

- **Repeated delete on an already-deleted code** (AC9): the affected-row count is the only
  signal used, and it is naturally zero the second time — no separate "already deleted" state
  to get out of sync.
- **Delete racing a create of the same code** (via alias): serialized by SQLite's writer lock;
  whichever statement commits first determines the outcome, and the other observes a
  consistent post-commit state (either the row exists or it doesn't) — no partial view.
- **Delete racing a redirect's click-increment on the same code**: serialized the same way; no
  lost update, no corrupted row, no partial deletion visible to any reader (§5).
- **Recreating a deleted code**: correct by construction because deletion removes the row
  entirely rather than leaving a placeholder that could keep the unique constraint occupied
  (§5) — this is the specific regression this design was checked against, per the impact
  analysis.
- **Malformed or unusual `{code}` path values**: no route constraint exists on `{code}` anywhere
  in this codebase (matching the existing redirect and stats routes); such values simply match
  zero rows and fall through to the normal 404 path, with no special parsing or rejection logic
  needed.

## 9. Failure modes knowingly left

- **Outcome of a delete/redirect race is unspecified**: as noted in §5, a redirect that narrowly
  loses a race against a concurrent delete on the same code sees 404 instead of a successful
  redirect with an incremented click count. The requirement does not specify which behavior is
  required in this exact interleaving, and no ordering guarantee (e.g., "in-flight redirects
  always complete") is designed in. This is judged acceptable: the window is a single SQL
  statement wide, the outcome is never corrupted data, and manufacturing a specific ordering
  guarantee here would add coordination logic the requirement never asked for.
- **No audit trail of what was deleted**: because the repository never reads the row before
  deleting it (§ADR 0002), the service has no record of the URL or click count a deleted link
  held at the moment of deletion. This is deliberate — the requirement states deletion is
  permanent and unrecoverable, and no logging/audit requirement is stated — but it does mean a
  future requirement asking for a deletion audit log would need to change this method's shape
  (read-then-delete, or a `RETURNING` clause), not just add a call site.
- **No new защита against accidental mass deletion**: a caller who can enumerate or guess codes
  can delete any of them one at a time with no rate limiting or authorization beyond whatever
  the existing write endpoints already enforce (per NFR "consistency with existing
  conventions" — this codebase has none). Unchanged from the standing security posture recorded
  in `README.md`'s "Limitations," and explicitly out of scope for this task per
  `docs/requirements.md`.