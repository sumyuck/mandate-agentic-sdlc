# URL Shortener Service

A REST API service that creates short links from long URLs, redirects visitors to the original URL while tracking clicks, and reports per-link statistics. Links are persisted in SQLite and can optionally include a caller-supplied alias and an expiry timestamp.

## Running the service

### Prerequisites

- .NET 10.0 or later

### Build and run

```bash
dotnet build
dotnet run
```

The service starts on `http://localhost:5000` by default. SQLite creates a `links.db` file in the working directory.

## Configuration

Configuration is read from `appsettings.json` or environment variables:

| Setting | Environment variable | Default | Purpose |
|---------|----------------------|---------|---------|
| `BaseUrl` | `BaseUrl` | `http://localhost:5000/` | The scheme and host used to construct absolute short URLs in responses. Must end with `/`. |
| `ConnectionStrings:Sqlite` | `ConnectionStrings__Sqlite` | `Data Source=links.db` | SQLite connection string. The file is created if absent. |

Example with environment variables:
```bash
export BaseUrl=https://short.example.com/
export ConnectionStrings__Sqlite="Data Source=/var/lib/shortener/links.db"
dotnet run
```

## API endpoints

### POST /api/v1/links

Create a short link.

**Request body** (JSON):

```json
{
  "url": "https://example.com/very/long/path/to/resource",
  "alias": "my-link",
  "expiresAt": "2026-01-01T00:00:00Z"
}
```

- `url` (required, string): The URL to shorten. Must be absolute with scheme `http` or `https`. The host (if a literal IP) must not be loopback, link-local, or RFC 1918 private range.
- `alias` (optional, string): A caller-supplied short code. Must match `^[A-Za-z0-9]{1,32}$`. If omitted, a code is generated automatically.
- `expiresAt` (optional, string): ISO-8601 timestamp with explicit UTC designator (`Z` or `+00:00`). After this time, the link returns 410 Gone instead of redirecting. If omitted, the link never expires.

**Success response (201 Created)**:

```json
{
  "code": "my-link",
  "shortUrl": "https://short.example.com/my-link"
}
```

**Error responses**:

- **400 Bad Request** if:
  - `url` is missing or empty.
  - `url` scheme is not `http` or `https`.
  - `url` host is an IP literal in a loopback range (`127.0.0.0/8`, `::1`), link-local range (`169.254.0.0/16`, `fe80::/10`), or RFC 1918 private range (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `fc00::/7`).
  - `alias` does not match `^[A-Za-z0-9]{1,32}$`.
  - `expiresAt` is not a valid ISO-8601 UTC timestamp.

  Response body:
  ```json
  {
    "error": "invalid_request",
    "message": "Specific reason (e.g., 'url scheme must be http or https')"
  }
  ```

- **409 Conflict** if the requested `alias` is already in use (no link is created):

  ```json
  {
    "error": "alias_taken",
    "message": "The requested alias is already taken."
  }
  ```

**Example: generate a code automatically**

```bash
curl -X POST http://localhost:5000/api/v1/links \
  -H "Content-Type: application/json" \
  -d '{"url":"https://example.com/long/path"}'
```

Response:
```json
{
  "code": "1",
  "shortUrl": "http://localhost:5000/1"
}
```

**Example: claim a specific alias**

```bash
curl -X POST http://localhost:5000/api/v1/links \
  -H "Content-Type: application/json" \
  -d '{
    "url": "https://example.com/page",
    "alias": "mypage"
  }'
```

Response:
```json
{
  "code": "mypage",
  "shortUrl": "http://localhost:5000/mypage"
}
```

**Example: set an expiry**

```bash
curl -X POST http://localhost:5000/api/v1/links \
  -H "Content-Type: application/json" \
  -d '{
    "url": "https://example.com/sale",
    "expiresAt": "2025-12-31T23:59:59Z"
  }'
```

Response:
```json
{
  "code": "A",
  "shortUrl": "http://localhost:5000/A"
}
```

---

### GET /{code}

Redirect to the original URL and record a click.

**Parameters**:

- `code` (path, required, string): The short code (e.g. `"mypage"` or a generated code like `"1"`).

**Success response (302 Found)**:

The response includes a `Location` header with the original URL. The link's click count is incremented by 1.

```bash
curl -i http://localhost:5000/mypage
```

Output (excerpt):
```
HTTP/1.1 302 Found
Location: https://example.com/page
```

**Error responses**:

- **404 Not Found** if the code does not exist:

  ```json
  {
    "error": "not_found",
    "message": "No link exists for this code."
  }
  ```

- **410 Gone** if the code exists but has expired (no redirect occurs; click count is not incremented):

  ```json
  {
    "error": "expired",
    "message": "The link has expired."
  }
  ```

---

### GET /api/v1/links/{code}/stats

Get statistics for a short link.

**Parameters**:

- `code` (path, required, string): The short code.

**Success response (200 OK)**:

```json
{
  "code": "mypage",
  "url": "https://example.com/page",
  "createdAt": "2025-01-15T10:30:45Z",
  "expiresAt": "2025-12-31T23:59:59Z",
  "clickCount": 42
}
```

- `code`: The short code (what was supplied as alias or generated).
- `url`: The original URL this code redirects to.
- `createdAt`: ISO-8601 UTC timestamp when the link was created.
- `expiresAt`: ISO-8601 UTC timestamp when the link expires, or `null` if no expiry was set.
- `clickCount`: Total number of successful redirects (GET /{code} responses with 302 status).

**Error response (404 Not Found)** if the code does not exist:

```json
{
  "error": "not_found",
  "message": "No link exists for this code."
}
```

**Example**:

```bash
curl http://localhost:5000/api/v1/links/mypage/stats
```

Response:
```json
{
  "code": "mypage",
  "url": "https://example.com/page",
  "createdAt": "2025-01-15T10:30:45Z",
  "expiresAt": null,
  "clickCount": 5
}
```

---

## Data validation

All three request fields are validated **before any database write**:

### URL validation

- **Scheme**: Must be `http` or `https`. Other schemes (`ftp://`, `javascript:`, `file://`, etc.) are rejected with 400.
- **Host type**: 
  - If the host is a literal IP address, it is checked against blocked ranges (see below). 
  - If the host is a hostname, it is accepted as-is. **No DNS resolution is performed** — the service does not look up what an IP address the hostname resolves to.
- **Blocked IPv4 ranges** (rejected with 400):
  - `127.0.0.0/8` (loopback)
  - `169.254.0.0/16` (link-local)
  - `10.0.0.0/8` (RFC 1918 private)
  - `172.16.0.0/12` (RFC 1918 private)
  - `192.168.0.0/16` (RFC 1918 private)
- **Blocked IPv6 ranges** (rejected with 400):
  - `::1` (loopback)
  - `fe80::/10` (link-local)
  - `fc00::/7` (unique-local private)

### Alias validation

If supplied, must match `^[A-Za-z0-9]{1,32}$` (alphanumeric, 1–32 characters). Case-sensitive.

### Expiry validation

If supplied, must be a valid ISO-8601 timestamp with an explicit UTC designator (`Z` or `+00:00`). Examples of valid formats:
- `2026-01-01T00:00:00Z`
- `2026-01-01T00:00:00+00:00`

Timestamps without a UTC designator (e.g. `2026-01-01T00:00:00` or `2026-01-01T00:00:00-05:00`) are rejected with 400.

---

## Limitations

### Single-instance only

This service assumes a single SQLite database file and a single running process. It does not support horizontal scaling across multiple processes or machines. If multiple instances attempt to write concurrently through the same SQLite file, conflicts may occur.

### No built-in authentication or rate limiting

The API has no authentication, API keys, or rate limiting. Any caller can create, read, and list links. If deployed to the internet, deploy it behind a reverse proxy or gateway that enforces authentication and rate limits.

### Click count is approximate under high concurrency

Each redirect increments the click count in a single atomic database operation. Under extreme concurrent load (hundreds of simultaneous redirects to the same code), SQLite's single-writer design may introduce small incremental latencies, but counts remain accurate.

### Alias/code collision recovery leaves orphaned rows

When a generated code (base62 of the auto-increment ID) happens to match a pre-existing alias, the service inserts a new row to obtain a higher ID and retries up to 5 times. If an orphaned (unreachable) row is left behind after a retry exhaustion, it is never cleaned up and consumes a small amount of disk space. This is a known, acceptable trade-off for simplicity; collision in normal operation is extraordinarily rare (happens only if someone claims an alias like `"1"` before the ID counter naturally reaches the value that encodes to `"1"`).

### Retry exhaustion on generated codes (hypothetical)

If an adversary deliberately claims every alias in the low range of numeric codes (e.g. `"0"`, `"1"`, `"2"`, ..., `"Z"`), new links without a supplied alias could receive 500 errors until the ID counter advances past the claimed aliases. This is not defended against because the requirement specifies no authentication or rate limiting; a deployment at risk should add those measures.

### IPv4-mapped IPv6 addresses not unwrapped

An IPv6 literal like `http://[::ffff:192.168.1.1]/` (IPv4-mapped IPv6) is checked against IPv6 ranges only, not unwrapped and re-checked against IPv4 ranges. Such URLs are accepted if they do not fall within the `fc00::/7` unique-local range. This edge case is not called for by the specification.

### No timezone handling on expiry

Expiry comparison uses the system's UTC clock at request time. If the host system's clock is significantly skewed or drifts, expired links may not behave as expected. No NTP-sync or leap-second handling is included.

### SQLite file location

The SQLite file must be on a filesystem the process can write to. If the file becomes inaccessible or the disk fills, all write operations fail with errors. Backup and disaster recovery are not built in; use standard SQLite backup tools.

---

## Health check

**GET /health/live**

Returns 200 OK with a simple JSON body:

```json
{
  "status": "live"
}
```

Use this endpoint to verify the service is running.

---

## Database

The service automatically creates a SQLite schema on startup if it does not exist. The schema consists of a single table, `links`:

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

- `id`: Auto-incrementing internal identifier (never exposed in the API).
- `code`: The short code (unique constraint enforced by the database). Shared namespace for both aliases and generated codes.
- `original_url`: The full URL the short code redirects to.
- `created_at`: ISO-8601 UTC timestamp of creation.
- `expires_at`: ISO-8601 UTC timestamp of expiry, or NULL if no expiry was set.
- `click_count`: Number of successful redirects.

All timestamps are stored and returned in ISO-8601 format with UTC timezone.

---

## Concurrency

The service uses SQLite's Write-Ahead Logging (WAL) mode to allow concurrent reads (redirects, stats queries) while writes are in flight. A busy timeout of 5000 milliseconds is configured so writers briefly wait rather than immediately fail if another write is in progress.

Alias uniqueness is enforced by SQLite's unique constraint on the `code` column, making concurrent alias-creation requests race-free: only one succeeds with 201; the others receive 409.

---

## Troubleshooting

### Service fails to start with "database is locked"

This may occur if another process holds the SQLite file open (e.g. a backup tool, or a previous crashed instance). Ensure no other processes are using `links.db` and try again.

### Generated codes are not sequential

Generated codes are base62-encoded auto-increment IDs, so the sequence may have gaps (e.g., `1`, `2`, `5`) if a row insertion fails (e.g., alias collision retry path). Gaps are normal and do not indicate an error.

### Redirect returns 404 for a code I just created

Verify the code was successfully created by checking the response status (should be 201) and then querying the stats endpoint (`GET /api/v1/links/{code}/stats`) to confirm the link exists.

### Click count not incrementing

Only successful redirects (HTTP 302) increment the click count. A 404 (code not found) or 410 (expired) does not increment. Query the stats endpoint to see the current count.

### ExpiresAt is in the future but GET returns 410 Gone

Verify the timestamp is in UTC and includes the UTC designator (`Z` or `+00:00`). Timestamps without explicit UTC (e.g. `2026-01-01T00:00:00`) may be misinterpreted. Query the stats endpoint to see the stored expiry value.