# Requirements — URL Shortener Service

## 1. Scope

Build a backend REST API service that:

- Creates short links from long URLs, optionally with a caller-supplied alias and an expiry timestamp.
- Redirects visitors from a short code to the original URL, tracking a click count.
- Reports per-link statistics.
- Persists all link data in SQLite.
- Validates submitted URLs to reject unsupported schemes and obvious local/private-network targets, without performing DNS resolution.

### 1.1 Out of scope

The following are explicitly **not** part of this build. They are not implied by the request and are called out so no one assumes they're covered:

- Authentication, authorization, API keys, or per-caller rate limiting.
- Editing or deleting an existing link once created.
- Custom domains or multiple base hosts per deployment.
- Any UI (web page, QR code, browser extension).
- Bulk/batch creation of links.
- Analytics beyond a raw click count (no referrer capture, geo, device, timestamps-per-click, etc.).
- DNS-based validation of hostnames (explicitly excluded by the request — literal-IP checks only).
- Horizontal scaling / multi-instance coordination of the monotonic ID counter (single SQLite instance is assumed).

## 2. Acceptance criteria

Each item below is independently testable.

1. `POST /api/v1/links` with a valid `url` and no `alias`/`expiresAt` returns `201` with a JSON body containing a short `code` and an absolute short URL built from the configured base URL.
2. `POST /api/v1/links` with a valid `url` and an available `alias` returns `201`, and the created link's code equals the supplied alias.
3. `POST /api/v1/links` with a valid `url` and a valid ISO-8601 UTC `expiresAt` returns `201`, and the stored/returned expiry matches the supplied value exactly.
4. `POST /api/v1/links` with an `alias` that already exists returns `409` and creates no new row (verified: a subsequent stats lookup for any newly-implied code returns `404`, and the existing link's data is unchanged).
5. `POST /api/v1/links` with `url` missing returns `400`.
6. `POST /api/v1/links` with a `url` whose scheme is not `http` or `https` (e.g. `ftp://`, `javascript:`) returns `400`.
7. `POST /api/v1/links` with a `url` whose host is a loopback IP literal (e.g. `127.0.0.1`, `::1`) returns `400`.
8. `POST /api/v1/links` with a `url` whose host is a link-local IP literal (e.g. `169.254.x.x`, `fe80::...`) returns `400`.
9. `POST /api/v1/links` with a `url` whose host is an RFC 1918 private IPv4 literal (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`) returns `400`.
10. `POST /api/v1/links` with a `url` whose host is a public IP literal, or an ordinary hostname (regardless of what it would resolve to), succeeds with `201` — no DNS lookup is performed to check it.
11. `POST /api/v1/links` with an `expiresAt` that is not valid ISO-8601 UTC returns `400`.
12. `GET /{code}` for an existing, unexpired code returns `302` with a `Location` header equal to the original URL, and increments that link's click count by exactly 1.
13. `GET /{code}` for a code that does not exist returns `404`.
14. `GET /{code}` for a code whose `expiresAt` is in the past returns `410`, does not redirect, and does not increment the click count.
15. Repeated `GET /{code}` calls each increment the click count by 1, observable via the stats endpoint.
16. `GET /api/v1/links/{code}/stats` for an existing code returns `200` with `code`, original `url`, creation timestamp, `expiresAt` (or explicit null/absence if none was set), and the current click count.
17. `GET /api/v1/links/{code}/stats` for a nonexistent code returns `404`.
18. Codes generated without a caller-supplied alias are the base62 encoding of a monotonically increasing identifier, and are unique across all links (aliased or generated).
19. Two concurrent `POST /api/v1/links` requests for the same `alias` result in exactly one `201` and the other(s) receiving `409`; no duplicate or corrupted row is left behind.
20. Link data persists in a SQLite database file and survives a service restart (a link created before restart is still retrievable and redirectable after restart).

## 3. Non-functional requirements

- **Persistence**: SQLite is the system of record; schema must enforce uniqueness of `code` at the database level (not just application-level checking), to make acceptance criterion 19 hold under concurrency.
- **No DNS resolution**: URL host validation operates only on the literal host string (IP literal parsing) or literal IP-in-hostname detection; hostnames are never resolved as part of validation, by requirement.
- **Determinism of short URL construction**: the absolute short URL returned by `POST /api/v1/links` is built from a single, operator-configured base URL (scheme + host + optional port), not from per-request headers (see `short-url-base-source` decision).
- **Data integrity over performance**: alias/code uniqueness must never be violated even under concurrent writes; a raced request must fail with `409` rather than corrupt state.
- **Input validation**: all three fields (`url`, `alias`, `expiresAt`) are validated before any database write; invalid input results in `400` and no side effects.
- **Auditability of expiry**: expiry comparison uses UTC "now" at request time; a link is considered expired when the current time is at or after `expiresAt`.
- **No implied authentication**: the API as specified has no auth; this is recorded as a scope boundary, not an oversight (see ambiguity report).
- **Observability**: not specified by the request beyond what's needed to verify acceptance criteria; standard structured logging of requests/errors is assumed but not tested here.

## 4. Recorded assumptions (non-material, ordinary engineering judgment)

- Alias character set: restricted to the same base62 alphabet (`[A-Za-z0-9]`) as generated codes, to keep a single unambiguous key space (see decision `alias-charset`).
- Alias/code length bounds: a reasonable maximum (e.g. 32 characters) is enforced but not otherwise specified by the request.
- Codes and aliases are treated as case-sensitive.
- `expiresAt` boundary is inclusive of expiry: a link is expired when `now >= expiresAt`.
- Click count increments only occur on a successful `302` redirect, not on `404` or `410` responses, per the literal wording of the request.
- IPv6 loopback/link-local/unique-local ranges are blocked alongside the named IPv4 ranges (see decision `ipv6-private-range-handling`).