# Impact Analysis — Add DELETE /api/v1/links/{code}

## 1. Components that must change, and why

### `LinkRepository.cs`
This is, by the design doc and ADR 0001, "the only component that opens a `SqliteConnection` or
writes SQL." It currently exposes `CreateAsync`, `RedirectAsync`, and `GetStatsAsync` against the
single `links` table (`id`, `code`, `original_url`, `created_at`, `expires_at`, `click_count`).
It must gain a `DeleteAsync(string code)` method that:

- Issues a single `DELETE FROM links WHERE code = $code` (or an equivalent statement using
  `RETURNING` to detect whether a row existed), because `click_count` and the link record live
  in the *same row* — there is no separate statistics table. This is important: it means the
  atomicity requirement (AC10 in `docs/requirements.md`, "no client-visible state exists where
  one was removed and the other was not") is satisfied for free by deleting one row in one
  statement, exactly the pattern `RedirectAsync`'s atomic `UPDATE ... RETURNING` already
  establishes in this codebase.
- Reports whether a row was actually deleted (via `ExecuteNonQueryAsync`'s affected-row count, or
  a `RETURNING code` read), so the service/HTTP layer can distinguish "deleted" from "no such
  code" (AC4, AC9) the same way `RedirectAsync` already disambiguates not-found from expired by
  checking the outcome of its own statement rather than doing a separate existence probe first
  (avoiding the same time-of-check-to-time-of-use race ADR 0001 explicitly rejected for create).

No schema change and no new table are required — this is the one part of the change that is
*not* a migration.

### `LinkService.cs`
Currently exposes `CreateAsync`, `RedirectAsync` (a thin passthrough to the repository), and
`GetStatsAsync` (also a thin passthrough). It must gain a `DeleteAsync(string code)` method
mirroring the existing passthrough pattern, returning a result the HTTP layer can map to 204 or
404. Given the existing `RedirectResult`/`CreateOutcome` pattern in `Models.cs`, deletion needs
an equivalent — either a new `DeleteResult` type (`Deleted` / `NotFound`) or, since the only two
outcomes are binary, a `bool`/simple enum. The existing convention in this codebase (discriminated
result types per operation, not raw booleans) argues for a small `DeleteOutcome` enum for
consistency, but a `bool "found"` would also work; either is a same-file, same-layer change with
no consequence beyond `Program.cs`'s switch.

### `Models.cs`
Needs a new type (or reuse of an existing shape) to carry the delete outcome from
`LinkRepository`/`LinkService` up to `Program.cs`, following the existing pattern of
`RedirectResult`/`CreateOutcome`. No existing record here needs to change — `ErrorResponse` is
reused as-is for the 404 body, matching every other endpoint's error shape.

### `Program.cs`
Must add a new minimal-API route:

```csharp
app.MapDelete("/api/v1/links/{code}", async (string code, LinkService service) => { ... });
```

mapping the new delete outcome to `Results.NoContent()` (204, no body — note the existing
handlers all use `Results.Json(...)` with an explicit status code; 204 needs `Results.NoContent()`
or `Results.StatusCode(204)`, since a 204 must have *no* body, unlike every existing success
response in this file) or `Results.Json(new ErrorResponse("not_found", "No link exists for this
code."), statusCode: StatusCodes.Status404NotFound)` — reusing the exact error object and message
already used by both `GET /{code}` and `GET /api/v1/links/{code}/stats` for their 404s, so the
three 404 bodies stay indistinguishable as AC7 requires.

Route-registration risk to check explicitly: `Program.cs` already registers
`GET /api/v1/links/{code}/stats` and `GET /{code}` (a near-catch-all). ASP.NET Core's minimal API
router dispatches by HTTP method as well as template, so `MapDelete("/api/v1/links/{code}")` will
not collide with the existing `MapGet("/{code}")` or `MapGet("/api/v1/links/{code}/stats")` routes
— but this must be verified by an actual route match, not assumed, since `{code}` is an
unconstrained wildcard segment in this codebase (no route constraint like `{code:regex(...)}` is
used anywhere today).

### `contracts/openapi.yaml`
This is a public, checked-in contract with a full path/schema for every existing endpoint. It has
no `delete:` operation under `/api/v1/links/{code}` today. A `DELETE` operation must be added with
`204` (no content schema) and `404` (`$ref: '#/components/schemas/Error'`) responses, matching the
existing `Error` schema already used by `/{code}` and `/api/v1/links/{code}/stats`. Any consumer
that generates a client from this file, or validates requests/responses against it, will not know
the endpoint exists until this file changes — this is the one artifact in the tree that other
tooling is likely to consume directly.

### `README.md`
Documents every endpoint (`POST /api/v1/links`, `GET /{code}`, `GET /api/v1/links/{code}/stats`)
with request/response examples and a full endpoints table implied by structure. It needs a new
`### DELETE /api/v1/links/{code}` section following the same structure (parameters, success
response, error responses, example `curl`) as the three existing sections, or the documentation
silently drifts from the implementation the moment this ships.

### Tests: `tests/Service.Tests/LinkRepositoryTests.cs` and `tests/Service.Tests/LinkServiceTests.cs`
Both files test each repository/service method exhaustively today (create, redirect, stats) but
have zero coverage for delete since the method does not exist. New tests are required at both
layers: deleting an existing code succeeds and a subsequent `GetStatsAsync`/`RedirectAsync` for
the same code afterward returns not-found/null exactly as for a code that was never created
(mirroring `RedirectAsync_for_nonexistent_code_returns_not_found` and
`GetStatsAsync_for_nonexistent_code_returns_null` already in these files); deleting a nonexistent
code reports not-found; deleting twice reports not-found the second time (AC9). No existing test
in either file needs to change, but the constructors' per-test SQLite temp-file setup pattern
(`Path.GetTempFileName()` + `SchemaInitializer.Initialize`) should be reused rather than
reinvented.

## 2. Components that merely depend on the above and could break

- **`SchemaInitializer.cs`** — unaffected: no schema change is needed since deletion is a `DELETE`
  against the existing single `links` table, not a new column or table. It stays correct as-is,
  but this analysis explicitly confirms it does *not* need to change, so an implementer is not
  tempted to add a soft-delete column here (which the requirement explicitly forbids: "no soft
  delete and no recovery").
- **`CodeGenerator.cs`** and **`UrlValidator.cs`** — no dependency on delete at all; pure functions
  untouched by this change.
- **`GET /{code}` (redirect) and `GET /api/v1/links/{code}/stats` handlers in `Program.cs`** —
  these do not need code changes (their existing "no row found → 404" paths already produce the
  right behavior once the row is gone), but they are exactly the paths that must be *proven*
  unchanged by regression testing (AC5, AC6, AC11 / NFR "No regression"). A bug in the new
  `DELETE` handler's routing or the repository's `DELETE` statement (e.g., an overly broad
  `WHERE` clause, or a typo matching by `id` instead of `code`) would silently corrupt these two
  endpoints for codes that were never meant to be touched, and — because deleted-code and
  unknown-code responses are required to be identical — such a bug would produce a plausible,
  passing-looking 404 rather than an obvious error.
- **`POST /api/v1/links` (create)** — no code change, but is a load-bearing invariant for
  correctness after this change: because the requirement states a deleted code must behave
  exactly like an unknown one, and the existing create logic already treats "code taken" purely
  via the database's `UNIQUE` constraint on `code` (ADR 0001), a deleted code's row being fully
  gone means create-with-that-code-as-alias will succeed again with no extra work — this is
  correct only because `DeleteAsync` truly removes the row rather than leaving any residue (e.g.
  a `NULL`-code placeholder like the orphaned-row pattern already present in the collision-retry
  path of `CreateAsync`). If delete were ever implemented as an `UPDATE` that only clears
  `original_url` while leaving the `code` row and its `UNIQUE` entry in place, this endpoint would
  start rejecting recreation of a deleted code with 409 — a regression not covered by any
  existing test, since no current test recreates a code after "deletion" (the concept doesn't
  exist yet).
- **`docs/design.md`** — explicitly states in §1 "Nothing here anticipates ... deletion ... those
  are out of scope" and its component/data-model sections (§2, §4) describe only the four
  existing endpoints and no delete path. This document is now stale with respect to the shipped
  system and should be revisited in a later stage (not part of this analysis's scope, but flagged
  as a dependent artifact that will silently misdescribe the system if left alone).
- **`docs/adr/0001-sqlite-write-serialization.md`** — its correctness argument rests on
  `UNIQUE(code)` plus WAL/busy_timeout for create and redirect; a `DELETE` statement participates
  in the same single-writer serialization the ADR already describes, so no new ADR is strictly
  required, but the ADR's stated scope ("Three acceptance criteria bind together...") does not
  mention delete, so a reviewer relying on it for a complete concurrency picture would miss that
  a delete and a concurrent redirect-increment on the same code are now possible and must be
  reasoned about (see §5 below).

## 3. Public surfaces affected

- **New HTTP surface**: `DELETE /api/v1/links/{code}` — brand new route, new verb on an existing
  path prefix. This is the only new public endpoint; no existing endpoint's method, path,
  request schema, or response schema changes (satisfying requirement AC11 / "No regression").
- **`contracts/openapi.yaml`**: requires a new `delete:` operation object under the existing
  `/api/v1/links/{code}` path key (note: this path key does not exist yet in the file at all —
  today only `/api/v1/links`, `/{code}`, and `/api/v1/links/{code}/stats` are defined, so this is
  a wholly new path entry, not a modification of an existing one).
- **Error contract**: the existing `components.schemas.Error` shape (`error`, `message`) is
  reused for the new 404, consistent with all three existing error responses — no new error
  schema needed.
- **No request body** and **no response body on success** (204) — this is a shape ASP.NET Core's
  minimal API supports today (`Results.NoContent()`), but it is a *new* pattern in this codebase:
  every existing success response (`Program.cs`) returns a JSON body via `Results.Json(...)`.
  Implementers must not default to the existing `Results.Json(..., statusCode: 204)` pattern by
  copy-paste, since that would attach a body to a response the spec requires to have none.
- **Configuration**: no new configuration keys. `BaseUrl` and `ConnectionStrings:Sqlite` are
  unaffected — delete uses the same connection string as every other repository method.
- **No message/event contracts** exist in this codebase (no queue, no webhook, no outbox) to be
  affected.

## 4. Data and migration implications

- **No schema migration.** The `links` table (defined identically in both
  `SchemaInitializer.cs` and documented in `README.md`'s "Database" section) needs no new column,
  no new table, and no index change. Deletion is a `DELETE FROM links WHERE code = $code`, and
  because `click_count` is a column on the same row as `original_url`, deleting the row removes
  the link and its statistics in one atomic SQL statement — satisfying the atomicity requirement
  without any transaction wrapping beyond what SQLite already gives a single statement.
- **No soft-delete state, ever.** The requirement and `docs/requirements.md` (AC8) are explicit
  that there must be no tombstone, flag, or hidden state. This must be checked against whatever
  the implementation stage produces: any `is_deleted` column, any `deleted_at` timestamp, or any
  row left behind with a nulled-out `original_url` would violate AC8 even if every HTTP response
  looked correct, because it would leave recoverable state at the SQL/backup level.
  Note the codebase already has one precedent for "leave a row behind but make it
  unreachable" — the orphaned `NULL`-code rows from `CreateAsync`'s collision-retry path
  (documented in `docs/design.md` §5 and §9). Delete must not reuse that pattern; a deleted row
  must be genuinely gone (`DELETE`, not `UPDATE code = NULL`), or a future accidental change to
  the "code IS NULL is invisible" invariant elsewhere in the codebase could resurface deleted
  data.
- **No backup/recovery mechanism exists** in this system (per `README.md`'s "Limitations" —
  "Backup and disaster recovery are not built in") — this means the irreversibility the
  requirement calls for is the *only* protection between a caller and permanent loss; there is no
  safety net at the infrastructure layer to catch an errant delete.

## 5. Ways this change could break something that currently works

- **Route/verb collision risk**: `Program.cs` already maps `GET /{code}` as a near-catch-all
  single-segment route. Adding `DELETE /api/v1/links/{code}` is a different HTTP method on a
  different (longer, more specific) template, so ASP.NET Core's routing should not conflict —
  but this is exactly the kind of assumption that should be exercised by an actual request against
  the running app, not just template inspection, since `{code}` in both templates is an
  unconstrained wildcard.
- **Wrong-row deletion**: if the new repository method's `WHERE` clause is built incorrectly
  (e.g., matching on a stale/cached connection string, or a copy-paste from `RedirectAsync` that
  accidentally reuses its `expires_at` condition), a delete could silently affect zero rows when
  it should affect one, or vice versa — and because 404 for "wrong code" and 404 for "correctly
  not found" look identical, such a bug would not surface as a visible error to a caller.
- **Race between concurrent delete and redirect/stats on the same code**: SQLite's WAL mode plus
  `busy_timeout` (already configured in `SchemaInitializer.cs`) serializes writers, so a `DELETE`
  and a concurrent `RedirectAsync`'s `UPDATE ... RETURNING` cannot corrupt state, but the
  *outcome* for the redirect racing a delete is not specified anywhere in the request or
  `docs/requirements.md` (e.g., does a redirect that starts just before a delete completes still
  get its click counted, or does it see 404?). This is a genuine, currently-unaddressed edge case
  that the existing ADR 0001 does not cover because delete did not exist when it was written.
- **Recreate-after-delete regression**: as noted in §2, if delete is implemented as anything other
  than a true `DELETE`, the existing `UNIQUE(code)` constraint that create's alias path relies on
  (ADR 0001) would continue to reject a "freed" code, breaking the requirement's explicit
  statement that a deleted code must behave exactly like a never-used one for every purpose,
  including recreation via the existing `POST /api/v1/links` alias path.
- **204-with-body regression**: copying the existing `Results.Json(..., statusCode: ...)` pattern
  used by every other success path in `Program.cs` for the new 204 response would attach a JSON
  body to a response the spec requires to have none — a subtle contract violation that would not
  fail obviously in casual testing (many HTTP clients ignore a body on 204) but would violate
  AC1 and the OpenAPI schema once added.
- **Test-suite blind spot**: none of the existing tests in `LinkRepositoryTests.cs` or
  `LinkServiceTests.cs` exercise a delete-then-read sequence, so without new tests specifically
  covering "delete then redirect returns not-found" and "delete then stats returns null" (AC5,
  AC6), a regression in this exact interaction — the core of what this requirement asks for —
  could ship undetected by the existing suite.