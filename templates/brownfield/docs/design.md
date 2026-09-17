# Design — URL Shortener Service

## 1. Purpose and scope of this document

This describes the design for the four endpoints in the requirement — create link, redirect,
stats, plus the validation and persistence rules that back them — as a single-process ASP.NET
Core minimal API backed by one SQLite file. Nothing here anticipates authentication, editing,
deletion, multi-instance deployment, or any other capability the requirement did not ask for;
those are out of scope per `docs/requirements.md` §1.1 and are not designed around.

## 2. Components

- **HttpApiLayer** — ASP.NET Core minimal API endpoint mappings in `Program.cs`. Owns request
  parsing, status-code selection, and header construction (`Location` on redirect). Contains no
  business logic itself; every decision it makes is "which `LinkService` method to call and
  which HTTP status corresponds to its result."
- **LinkService** — the application logic. Orchestrates validation, calls `CodeGenerator` when
  no alias is supplied, and drives `LinkRepository` through the create/redirect/stats
  operations. This is the one place that knows the full rules from the requirement (e.g. "click
  count only increments on a successful redirect").
- **UrlValidator** — pure function(s) over a candidate URL string: scheme check, IP-literal
  detection, and range checks (loopback/link-local/RFC1918/IPv6 equivalents). No I/O, no DNS.
- **CodeGenerator** — turns a numeric identifier into a base62 string and back is not needed;
  only encode is used. Pure, stateless, unit-testable in isolation from SQLite.
- **LinkRepository** — the only component that opens a `SqliteConnection` or writes SQL. Owns
  the insert-with-collision-retry algorithm (§5), the redirect-with-increment statement, and the
  stats query. Nothing above this layer knows SQLite exists.
- **SchemaInitializer** — runs once at startup: creates the `links` table if absent, sets
  `PRAGMA journal_mode=WAL` and `PRAGMA busy_timeout`. Idempotent, safe to run every boot.
- **Configuration** — binds `BaseUrl` (scheme+host[+port] used to build the absolute short URL)
  and the SQLite connection string from `appsettings.json`/environment, per NFR
  "Determinism of short URL construction."

## 3. Request flow

### 3.1 `POST /api/v1/links`

1. HttpApiLayer deserializes the body. Missing/malformed `url` → 400 immediately, no further
   validation needed (AC5).
2. LinkService validates, in order, stopping at the first failure (all before any DB write, per
   NFR "Input validation"):
   - `url` parses as an absolute URI with scheme `http` or `https` (AC6).
   - `url`'s host, if it is an IP literal, is not loopback/link-local/RFC1918/IPv6-equivalent
     (AC7, AC8, AC9). A non-literal hostname, or a public IP literal, passes without any DNS
     lookup (AC10).
   - `alias`, if present, matches `^[A-Za-z0-9]{1,32}$` (recorded assumption; keeps aliases and
     generated codes in one key space, per the ambiguity report's `alias-charset` decision).
   - `expiresAt`, if present, parses as ISO-8601 with an explicit UTC designator (`Z` or
     `+00:00`) (AC3, AC11).
3. LinkService calls `LinkRepository.CreateAsync(url, alias, expiresAt)`, which either:
   - inserts a row keyed by the supplied alias, atomically detecting a pre-existing alias via
     the database's own unique constraint (AC2, AC4, AC19), or
   - inserts a row with an id-derived base62 code when no alias was supplied (AC1, AC18).
4. On success: 201 with `{ code, shortUrl }`, `shortUrl = Configuration.BaseUrl + code`. On
   alias collision: 409, no row persisted (AC4, AC19).

### 3.2 `GET /{code}`

1. `LinkRepository.RedirectAsync(code, nowUtc)` executes an atomic
   `UPDATE ... SET click_count = click_count + 1 WHERE code = ? AND (expires_at IS NULL OR
   expires_at > ?) RETURNING original_url` (AC12, AC15).
2. If a row was updated: HttpApiLayer returns 302 with `Location: <original_url>`.
3. If no row was updated: a second, cheap `SELECT expires_at FROM links WHERE code = ?`
   disambiguates — no row at all → 404 (AC13); row exists but expired → 410, no increment
   (AC14).

Expiry is evaluated as `now >= expiresAt` (inclusive), per the recorded assumption, using UTC
wall-clock time captured once per request before the query, so click-count and expiry-check see
the same instant.

### 3.3 `GET /api/v1/links/{code}/stats`

Single `SELECT code, original_url, created_at, expires_at, click_count FROM links WHERE code =
?`. Row found → 200 with all five fields, `expiresAt` explicitly `null` when unset (AC16). No
row → 404 (AC17).

## 4. Data model

One table, one file, no migrations framework (a single `CREATE TABLE IF NOT EXISTS` is the
entire schema surface for this scope):

```sql
CREATE TABLE IF NOT EXISTS links (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    code          TEXT UNIQUE,          -- see §5: transiently NULL only mid-generation
    original_url  TEXT NOT NULL,
    created_at    TEXT NOT NULL,        -- ISO-8601 UTC
    expires_at    TEXT,                 -- ISO-8601 UTC, NULL if unset
    click_count   INTEGER NOT NULL DEFAULT 0
);
```

`code` is one column, one uniqueness constraint, shared by aliases and generated codes — per the
ambiguity report's resolution, this removes any question of a generated code someday colliding
with an alias silently (the DB constraint would refuse it either way).

State lives entirely in this SQLite file (path from `Configuration`). The process holds no
in-memory link state across requests — a restart loses nothing but the OS page cache (AC20).
`journal_mode=WAL` lets `GET` redirects and stats reads proceed without blocking on an
in-flight write; `busy_timeout` makes a writer that arrives while another write is in flight wait
briefly rather than fail immediately with `SQLITE_BUSY`.

## 5. Code generation and the alias/generated-code collision

Generated codes are base62 of `id`, and `id` comes from SQLite's own `AUTOINCREMENT`, which is
already a durable, monotonically increasing, gap-tolerant sequence — no separate counter is
maintained (see ADR 0001).

Alias path (single statement, atomic):
```sql
INSERT INTO links(code, original_url, created_at, expires_at, click_count)
VALUES (?alias, ?, ?, ?, 0);
```
A `UNIQUE` violation here is definitionally "alias taken" → 409, nothing persisted.

Generated-code path (two statements, same logical operation):
```sql
INSERT INTO links(code, original_url, created_at, expires_at, click_count)
VALUES (NULL, ?, ?, ?, 0);              -- id assigned by AUTOINCREMENT, code left NULL
-- id := last_insert_rowid()
UPDATE links SET code = ?base62(id) WHERE id = ?id;
```
The `UPDATE` can itself hit the `UNIQUE` constraint — this happens only when some earlier alias
happens to equal `base62(id)` exactly (e.g. someone claimed the alias `"c"` before the id
counter reached the value that encodes to `"c"`). When that happens the row with `id` already
committed with a `NULL` code is left in place (harmless: no query ever matches on `code IS
NULL`, so it is permanently unreachable and invisible to every endpoint) and a new row is
inserted to obtain a fresh, higher `id`, retried up to a bounded number of attempts (5) before
returning 500. This is a deliberate, documented trade: it is correct and simple, at the cost of
occasional inert rows and, in an adversarial worst case, exhausted retries. See §6 and ADR 0001.

## 6. Validation details

**Scheme**: `Uri.TryCreate(url, UriKind.Absolute, out uri)`; reject if parse fails or
`uri.Scheme` is not (case-insensitively) `http`/`https`.

**IP-literal detection**: `Uri.CheckHostName(uri.Host)` distinguishes `IPv4`/`IPv6` from
`Dns`/hostname. Only literal IPv4/IPv6 hosts are checked against ranges; anything else
(hostname) is accepted without resolution, by requirement.

- IPv4 blocked ranges: `127.0.0.0/8` (loopback), `169.254.0.0/16` (link-local),
  `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16` (RFC 1918).
- IPv6 blocked ranges (per the ambiguity report's resolved decision `ipv6-private-range-handling`):
  `::1` (loopback), `fe80::/10` (link-local), `fc00::/7` (unique-local).

**`expiresAt`**: parsed with `DateTimeOffset.TryParse` using round-trip/UTC styles; a value that
parses but lacks an explicit UTC designator, or does not parse at all, is a 400.

## 7. Acceptance criteria coverage

| AC | How it's met |
|----|--------------|
| 1 | §3.1 step 4, no alias path |
| 2 | §3.1 step 3, alias path |
| 3 | `expires_at` stored and echoed verbatim after parse/round-trip |
| 4 | Unique constraint on `code`, alias path, no row on conflict |
| 5 | Body-parse failure before any validation |
| 6 | Scheme check, §6 |
| 7,8,9 | IPv4 range checks, §6 |
| 10 | Non-literal hosts and public IPs pass unchanged, no DNS call anywhere in the codebase |
| 11 | `expiresAt` parse failure, §6 |
| 12 | Atomic `UPDATE ... RETURNING`, §3.2 |
| 13 | No row matched at all → 404, §3.2 |
| 14 | Row matched but expired → 410, no increment, §3.2 |
| 15 | Each redirect is its own atomic `UPDATE`, increments are not lost under concurrency |
| 16 | Stats query returns all five fields, `expiresAt` nullable |
| 17 | Stats query no-match → 404 |
| 18 | `id` is `AUTOINCREMENT`, monotonic; `code` uniqueness enforced across both paths |
| 19 | Unique constraint + SQLite's own write serialization (WAL + busy_timeout), see ADR 0001 |
| 20 | SQLite file on disk, not `:memory:`; schema created idempotently at startup |

## 8. Failure modes designed for

- **Concurrent identical-alias requests** (AC19): resolved by the DB-level unique constraint;
  the loser gets 409 and leaves no row, regardless of timing.
- **Concurrent redirects on the same code** (AC15): resolved by the atomic
  `UPDATE ... RETURNING`; no read-modify-write race on `click_count`.
- **Expiry exactly at request time**: defined as inclusive (`now >= expiresAt`), removing the
  ambiguity of the boundary instant.
- **Alias colliding with a not-yet-generated code**: resolved by the shared unique column and
  the bounded retry described in §5, rather than silently allowing two links to answer to the
  same code.
- **Process restart mid-write**: SQLite's own transaction durability means a write either
  committed before the crash (visible after restart) or did not (no partial row) — no
  application-level recovery logic is needed.

## 9. Failure modes knowingly left

- **Retry exhaustion on generated-code collision** (§5): if an adversary deliberately claims
  every short alias in the low end of the id space, new links could receive 500s until the id
  counter advances past the claimed aliases. Judged acceptable: the requirement has no auth or
  rate limiting (explicit scope boundary), so defending against a deliberately hostile caller is
  not asked for here.
- **IPv4-mapped IPv6 literals** (e.g. `http://[::ffff:127.0.0.1]/`) are checked as IPv6 addresses
  only; the design does not unwrap them and re-check against the IPv4 ranges. Not called for by
  the requirement or the ambiguity report; left as a gap rather than speculatively closed.
- **SQLite single-writer throughput**: all writes serialize through one file lock. Acceptable
  because the requirement assumes a single instance and states no throughput target; a design
  that added connection pooling tricks or sharding to work around this would be solving a
  problem nobody has stated.
- **Orphaned NULL-code rows** from §5's retry path are never cleaned up. They are inert (never
  matched by any query) but do consume id space and disk. No cleanup job is included because
  nothing in the requirement calls for one and the volume is expected to be effectively zero in
  normal operation.
- **Clock skew / leap seconds** in expiry comparison: the design trusts the host's system clock
  for "now"; no NTP-drift handling is included, as this is standard for a service of this scope.