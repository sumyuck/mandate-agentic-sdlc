# Ambiguity report — URL Shortener Service

## Score: 0.3 (real gaps, buildable with documented assumptions)

This request is a precise, well-scoped API spec — most fields, status codes, and validation rules are stated explicitly and testably. The band is 0.3 rather than 0.0–0.2 because the request contains at least one gap (IPv6 handling of the private/loopback ranges it explicitly asks to block) that is a genuine security-relevant fork in behavior, not just a naming or default-value choice — but it is one a competent engineer can resolve today by choosing the conventional secure default and writing the assumption down, so it does not rise to the level of stopping the line. No gap here meets the bar of "guessing wrong builds a different product" or "surprising, expensive-to-undo default" strongly enough to warrant a 0.6+ score.

---

## Material-adjacent ambiguities (documented, not blocking)

### 1. "IP literal in a loopback, link-local, or RFC 1918 private range"

> "Reject with 400 any URL whose host is an IP literal in a loopback, link-local, or RFC 1918 private range."

RFC 1918 is an IPv4-specific term. The request never mentions IPv6 at all.

- **Interpretation A (literal reading):** only IPv4 ranges are checked (`127.0.0.0/8`, `169.254.0.0/16`, `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`). Any IPv6 literal — including `::1` (loopback) or `fe80::/10` (link-local) — is accepted.
- **Interpretation B (intent reading):** the equivalent IPv6 ranges (`::1`, `fe80::/10` link-local, `fc00::/7` unique-local) are blocked too, because the goal is clearly to prevent the service being used to redirect into local/private infrastructure, and that goal applies to both address families.

**What would be built differently:** the URL-validation function either contains only IPv4 CIDR checks (A) or dual-stack checks (B). Under A, a caller can submit `http://[::1]/admin` and it will be accepted where `http://127.0.0.1/admin` would not — an inconsistent and exploitable security posture. This is flagged as a decision in the requirements doc (chose B) because it is security-relevant, but resolvable without asking, since B is the unambiguous conventional-safe default.

### 2. "Absolute short URL"

> "It returns 201 with the short code and the absolute short URL."

The request never says what host/scheme the absolute URL is built from.

- **Interpretation A:** the service uses a single, operator-configured base URL (from config/env), independent of how the request arrived.
- **Interpretation B:** the service derives the base URL from the incoming request (`Host` or `X-Forwarded-Host` header), which would make the returned short URL match whatever hostname the client actually connected to.

**What would be built differently:** A requires a config value and produces identical output regardless of ingress path (safe, deterministic, but requires an operator to set it correctly, e.g. behind a reverse proxy with multiple hostnames). B is more "automatic" across environments but trusts client-supplied headers to construct a persisted-looking URL, which is a known injection vector if the value is ever reflected elsewhere or used for cache keys. Documented as a decision (chose A).

### 3. Alias character set / shared key space with generated codes

> "`alias` (optional)" ... "Generate codes as base62 over a monotonic identifier."

The request never states what characters a caller-supplied alias may contain, nor whether aliases live in the same namespace as generated codes.

- **Interpretation A:** aliases are restricted to the same base62 alphabet as generated codes, so `code` is a single uniform key space with one uniqueness constraint.
- **Interpretation B:** aliases may contain a broader set of URL-safe characters (hyphens, underscores, percent-encoded characters, unicode), distinct from what the generator ever produces.

**What would be built differently:** under A, validation is a simple regex and the DB schema needs only one unique column. Under B, the routing layer (`GET /{code}`) must handle a wider character set safely (path decoding, normalization), and there's a theoretical question of whether a future generated code could ever collide with a previously-chosen alias outside the base62 space (it can't, under B, since generated codes stay base62 — but the two spaces coexisting is a design decision worth recording). Documented as a decision (chose A) because it removes an entire class of routing-edge-case questions with no loss of functionality the request asked for.

---

## Non-material items (recorded as assumptions in requirements.md, not ambiguities)

These have more than one technically possible reading but a competent engineer would pick the obvious convention and move on — they do not change what product is being built:

- Alias/code maximum length.
- Case sensitivity of codes/aliases.
- Whether `expiresAt` boundary is inclusive or exclusive at the exact instant.
- Whether hitting a `404`/`410` code counts toward click statistics (the request only ties the increment to the `302` case, so this reads as settled, not ambiguous).
- Absence of authentication/rate limiting — the request never mentions it, and nothing in the text implies it was assumed; treated as an intentional scope boundary, not a gap requiring a guess.