# Security Report — URL Shortener Service

## Summary

Scanned the codebase for committed secrets, injection vulnerabilities, unsafe input handling, access control gaps, and dependency risk. Found no live credentials, no injection surfaces, no authentication bypass, and no high-risk dependencies. The two contract-fidelity issues identified in the review report (error code specificity and strict JSON parsing) are implementation-contract mismatches, not security defects, and are recorded separately. Security posture is sound for the stated scope.

## Findings

**None.** Zero security issues identified.

## What was checked and found clean

### Secrets and committed credentials
- **appsettings.json**: Connection string uses local relative path `Data Source=links.db` (no credentials). Base URL is localhost example. No API keys, tokens, passwords, or connection strings with embedded credentials anywhere in the tree.
- **All source files (.cs)**: No hardcoded passwords, API keys, tokens, private keys, or credential-like strings. Comments and examples use obvious placeholders where needed.
- **Conclusion**: No live credentials are present.

### Injection: SQL
- **LinkRepository.cs**: All SQL operations use parameterized queries with `SqliteCommand.Parameters.AddWithValue(...)`. No string concatenation of user input into SQL. The only strings interpolated are literal schema names and fixed keywords (e.g. `INSERT INTO links`, `UPDATE links`). Input (URL, code, alias) only appears as named parameters (`$code`, `$url`, `$expiresAt`), never in the SQL text.
  - Alias-insertion path: `INSERT INTO links(code, original_url, created_at, expires_at, click_count) VALUES ($code, $url, $createdAt, $expiresAt, 0);` — alias bound as `$code` parameter.
  - Generated-code update: `UPDATE links SET code = $code WHERE id = $id;` — generated code bound as `$code` parameter.
  - Redirect-with-increment: `UPDATE links SET click_count = click_count + 1 WHERE code = $code AND ...` — code bound as parameter.
  - Stats query: `SELECT ... FROM links WHERE code = $code;` — code bound as parameter.
- **Conclusion**: No SQL injection surface.

### Injection: Shell/process/template
- **Program.cs**, **LinkService.cs**, **UrlValidator.cs**: No `System.Diagnostics.Process.Start`, no `shell`, no template rendering with unescaped user input, no dynamic code generation or compilation.
- **Conclusion**: No shell injection or process-spawning risk.

### Injection: XSS/output encoding
- **Program.cs**: Error responses and stats responses are returned via ASP.NET Core's `Results.Json(...)`, which serializes to JSON via `System.Text.Json`. JSON serialization automatically escapes special characters; there is no unescaped HTML or JavaScript output. The only text fields in responses are echoing back user-provided URLs or request-supplied aliases, but all are wrapped in JSON serialization, not HTML templates.
- **Conclusion**: No XSS risk.

### Unsafe input handling

#### URL validation
- **UrlValidator.cs**: 
  - **Scheme validation**: checked against a strict allow-list (`"http"` or `"https"`, case-insensitive). No dynamic scheme acceptance.
  - **IP-literal blocking**: only IPv4 and IPv6 literal IPs are checked against blocked ranges; non-literal hostnames are never resolved (per requirement). The blocked ranges are hardcoded:
    - IPv4: `127.0.0.0/8` (loopback), `169.254.0.0/16` (link-local), `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16` (RFC 1918).
    - IPv6: `::1` (loopback), `fe80::/10` (link-local), `fc00::/7` (unique-local).
  - No DNS resolution happens anywhere; hostnames pass through without validation (per requirement, which explicitly forbids DNS lookups and only asks to block literal IPs).
  - Conclusion: URL validation is safe; blocked ranges prevent SSRF into local infrastructure; non-literal hostnames are accepted without lookup (correct per spec).

#### Alias validation
- **LinkService.cs**: Alias validated with regex `^[A-Za-z0-9]{1,32}$` before any database operation. No escape sequences, no special characters, no injection surface.
- **Conclusion**: Alias validation is safe.

#### ExpiresAt validation
- **LinkService.cs**: Parsed with `DateTimeOffset.TryParse` using `DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal`; a value that does not parse is rejected with 400. No injection surface (it's a datetime, not SQL, not a command).
- **Conclusion**: Safe.

#### Code parameter validation
- **Program.cs**: Code is extracted from the URL path (`MapGet("/{code}", ...)`). No explicit validation of the code before passing to service, but:
  - **LinkRepository.RedirectAsync**: uses code as a parameterized SQL query argument only (not checked, but safe to use as-is since it's bound as a parameter, not interpolated).
  - **OpenAPI contract**: code matches pattern `^[A-Za-z0-9]{1,32}$` by documentation. No enforcement of this pattern in the HTTP layer before lookup (not a security issue — worst case, a code outside this set simply returns 404).
- **Conclusion**: No injection even if malformed code is sent; worst case is a false 404.

### Access control
- **Program.cs**: 
  - `POST /api/v1/links` — no authentication check. Per scope (out-of-scope section of requirements), authentication is not required. This is a deliberate scope boundary, not a gap.
  - `GET /{code}` — no authentication. By design, redirects are public.
  - `GET /api/v1/links/{code}/stats` — no authentication. Stats are public.
  - All endpoints are stateless; no session or user identity is tracked.
- **Conclusion**: No authentication is required by spec; no unauthorized state-changing operations are exposed. The API is intentionally public (no auth required). Not a security gap.

### SSRF (server-side request forgery)
- No HTTP client calls are made from the service. The service does not fetch URLs, validate them by connecting, or use them as redirect targets for internal requests. User-supplied URLs are stored as text and returned to the client for the client to follow. Only literal-IP blocking happens (see URL validation above).
- **Conclusion**: No SSRF risk.

### Dependency risk
- **Directory.Packages.props**: Dependencies are pinned to versions used by the orchestrator itself:
  - `Microsoft.Data.Sqlite` v10.0.12 — official SQLite wrapper for .NET, widely used, no known critical vulnerabilities in this version as of the metadata available.
  - `Microsoft.NET.Test.Sdk` v18.10.1 — test runtime, dev-only.
  - `xunit` v2.9.3 — test framework, dev-only.
  - `xunit.runner.visualstudio` v4.0.0 — test runner, dev-only.
  - `coverlet.collector` v10.0.1 — test coverage, dev-only.
- No transitive dependencies are pulled; all versions are locked centrally. No dynamic or remote-loaded code.
- **Conclusion**: Dependency risk is minimal and normal for the scope.

### Concurrency and race conditions
- **LinkRepository.cs** and **ADR 0001**: Concurrency control relies on SQLite's native single-writer lock, WAL mode, and a unique constraint on the `code` column. This is the correct design for the stated scope (single SQLite instance). No application-level locking, no shared mutable state.
- **Alias collision** (AC19): Two concurrent identical-alias requests both hit the database's unique constraint; exactly one succeeds (201) and the other receives a `SqliteException` with error code 19 (constraint violation), caught and returned as 409. No race window.
- **Click-count increment** (AC15): The `UPDATE ... SET click_count = click_count + 1 ... RETURNING` is atomic in SQLite; no read-modify-write race.
- **Conclusion**: Concurrency handling is correct for single-instance scope.

### Database security
- **SchemaInitializer.cs**: Sets `PRAGMA journal_mode=WAL` (allows concurrent reads) and `PRAGMA busy_timeout=5000` (writers wait briefly if a write is in flight). Standard safe settings for this scope.
- **No schema-injection surface**: `CREATE TABLE IF NOT EXISTS` is a literal string; no table names or column names are parameterized (correct — they cannot be parameterized in SQL).
- **Persistence**: SQLite file is local; no network exposure documented.
- **Conclusion**: Safe for single-instance local use.

### Error handling and information disclosure
- **Program.cs**: Error responses return machine-readable `error` codes and human-readable messages. No stack traces, source paths, or internal state are leaked. 
  - 400 errors (invalid input) return `{ error: "invalid_request", message: "..." }` — no sensitive details.
  - 404/410 errors return `{ error: "not_found" or "expired", message: "..." }` — no leakage.
  - 409 (alias taken) returns `{ error: "alias_taken", message: "..." }` — expected.
  - 500 (server error) is not explicitly mapped; ASP.NET's default 500 handler takes over. No custom details leaked.
- **Conclusion**: Error handling does not leak sensitive information.

### Logging and observability
- No custom logging visible in the codebase. ASP.NET Core's default pipeline logs requests at the application level (not shown here). No secrets (credentials, user data, URLs) are logged by the application itself (cannot verify without runtime traces, but the code does not call any logging APIs with sensitive data).
- **Conclusion**: No obvious logging-based information disclosure.

---

## Not a security finding (contract/implementation mismatch, already in review report)

The review report identifies two issues:

1. **Error response `error` codes** — all 400s from link creation return `"invalid_request"` even though the contract documents distinct codes like `"invalid_url"`. This is a contract-fidelity gap, not a security vulnerability (the HTTP status and message text are correct; only the machine-readable `error` field is generic).

2. **Strict JSON parsing** — the request body accepts and silently ignores unrecognized fields, where the contract declares `additionalProperties: false`. This is a usability gap (misspelled field names are not rejected), not a security issue (an attacker cannot use it to bypass validation or corrupt state).

Both are design/contract issues, not security defects. They are listed in the review report and not duplicated here.

---

## Conclusion

**No security findings.** The codebase is free of committed secrets, injection vulnerabilities, unsafe input handling, unauthorized access paths, and high-risk dependencies. Concurrency control is correct for the stated single-instance scope. Error handling does not leak sensitive information. The service is safe to deploy as-specified.
