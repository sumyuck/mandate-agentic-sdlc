# Review Report — URL Shortener Service

## Summary

The implementation closely follows the approved design and ADR 0001: SQLite's unique
constraint and atomic `UPDATE ... RETURNING` are correctly used for alias-collision (409) and
click-count-increment (302) correctness, the code-generation retry-on-collision algorithm
matches §5 of the design exactly, URL validation covers scheme and IPv4/IPv6 literal ranges as
documented, and all 20 acceptance criteria appear to be met on the main paths I traced
(create, redirect, stats, expiry, concurrency). No critical or high-severity defects were
found. Two medium/low fidelity gaps against the published API contract are worth fixing.

## Findings

### 1. [medium] Error response `error` codes don't match the published contract

- **Where:** `Program.cs`, the `MapPost("/api/v1/links", ...)` handler (all `CreateOutcomeKind.Invalid` branches), and `LinkService.cs` (`CreateOutcome.Invalid(...)` call sites).
- **What's wrong:** Every 400 from link creation is returned with `error: "invalid_request"`, regardless of which validation failed (bad scheme, blocked IP, bad alias, bad `expiresAt`). `contracts/openapi.yaml`'s `Error` schema documents distinct machine-readable codes, giving `"invalid_url"` as the example for this family of failures, implying callers can branch on `error` to distinguish failure kinds.
- **Why it matters:** Consumers built against the published contract expect a stable, specific `error` value per failure type. Collapsing all 400s into one generic code silently breaks that contract even though the HTTP status code and message text are correct — a client checking `error === "invalid_url"` never matches.
- **What to do instead:** Either have `LinkService`/`CreateOutcome.Invalid` carry a specific error code per validation branch (e.g. `invalid_url`, `invalid_alias`, `invalid_expiry`) and thread it through to the response, or update `contracts/openapi.yaml` to document the single generic code actually returned, so the contract and implementation agree.

### 2. [low] `additionalProperties: false` on `CreateLinkRequest` is not enforced

- **Where:** `Models.cs` (`CreateLinkRequest` record) and `Program.cs` (`MapPost("/api/v1/links", ...)` binding).
- **What's wrong:** The contract declares `CreateLinkRequest` with `additionalProperties: false`, but the minimal-API JSON binding via `System.Text.Json` silently ignores unrecognized properties rather than rejecting the request with 400.
- **Why it matters:** A caller sending an unexpected/misspelled field (e.g. `alais` instead of `alias`) gets a silent 201 with the field ignored instead of a 400 telling them their request was malformed, which is a real (if narrow) usability and contract-fidelity gap.
- **What to do instead:** Either configure strict JSON deserialization (reject unknown members) for this endpoint, or drop `additionalProperties: false` from the contract if permissive parsing is the intended behavior.

## What I did not flag

- Alias/generated-code collision retry, WAL/busy_timeout concurrency handling, atomic
  redirect-increment, and IPv4/IPv6 blocked-range checks all match the design and ADR and look
  correct on inspection.
- I did not assess test coverage, per instructions.