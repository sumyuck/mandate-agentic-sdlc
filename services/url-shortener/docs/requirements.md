# Requirements — Delete Endpoint for URL Shortener

## Scope

Add a single new endpoint, `DELETE /api/v1/links/{code}`, to the existing URL shortener
service. The endpoint permanently removes a link and its associated click statistics.
No other endpoint's request contract, response contract, or behaviour changes.

### In scope

- The route `DELETE /api/v1/links/{code}`.
- Permanent removal of the link record identified by `{code}`.
- Permanent removal of the click statistics associated with that `{code}`.
- Making the redirect endpoint (`GET /{code}`) and the stats endpoint behave, after
  deletion, exactly as they do for a code that was never created.
- Preserving the current behaviour of every other existing endpoint.

### Out of scope

- Soft delete, tombstoning, undo, or any recovery mechanism.
- Bulk or batch deletion (multiple codes in one call).
- Changes to how links are created, redirected, or how stats are recorded, beyond what
  is needed to make deletion observable as specified.
- Any new authentication/authorization scheme beyond what existing endpoints already use.
- Audit logging or notification of deletion, unless such logging already exists
  generically for write operations in the current system.
- Rate limiting or abuse protection specific to the delete endpoint.
- Deciding whether a code can be reused/recreated after deletion — that is governed by
  the existing creation endpoint's behaviour, unchanged by this work.

## Acceptance Criteria

1. `DELETE /api/v1/links/{code}` for a code that currently maps to a link returns HTTP
   `204 No Content` with an empty response body.
2. On success, the link record for `{code}` is removed from persistent storage such
   that no subsequent read of it succeeds.
3. On success, the click statistics associated with `{code}` are removed from
   persistent storage such that no subsequent read of them succeeds.
4. `DELETE /api/v1/links/{code}` for a code that does not exist returns HTTP `404`.
5. After a successful delete, `GET /{code}` (the redirect endpoint) returns HTTP `404`
   for that code.
6. After a successful delete, the existing stats endpoint for that code returns HTTP
   `404`.
7. The `404` response returned for a deleted code (via redirect, stats, or a repeat
   `DELETE`) is not distinguishable, in status code or contract, from the `404`
   returned for a code that was never created.
8. There is no soft-delete flag, tombstone record, or hidden state: once deleted, no
   endpoint in the system can return the link's data or restore it.
9. Calling `DELETE /api/v1/links/{code}` a second time on an already-deleted code
   returns HTTP `404` (matching criterion 4 — a deleted code is now "no such code").
10. The deletion of a link record and its click statistics is atomic: no client-visible
    state exists where one was removed and the other was not.
11. No other existing endpoint (creation, redirect, stats, list, etc.) changes its
    request schema, response schema, status codes, or side effects as a result of this
    change.

## Non-Functional Requirements

- **Data loss is intentional and irreversible.** The system must not retain a
  recoverable copy of the link or its statistics after deletion; this is a stated
  requirement, not an oversight to be mitigated.
- **Consistency with existing conventions.** The new endpoint must follow the same
  authentication/authorization mechanism, error response shape, and routing
  conventions already used by other `/api/v1/links/*` endpoints, so that the API
  surface remains uniform.
- **Atomicity.** Removal of the link and its statistics must not leave partially
  deleted state observable through any endpoint, including under concurrent requests
  or partial failures (use a transaction or equivalent guarantee).
- **No regression.** Existing endpoints must be covered by regression tests (or
  equivalent verification) proving their behaviour is unchanged after this addition.
- **Idempotent-safe repetition.** A client retrying a `DELETE` call (e.g. after a
  timeout) must not error unsafely; a second call simply reports `404` per criterion 9.
- **Performance.** Deletion should complete within the same latency envelope as other
  single-record write operations in the existing system (no new expensive scans or
  full-table operations introduced).

## Assumptions Recorded (non-material, ordinary engineering judgment)

- The existing stats endpoint's route and response shape are unchanged by this work;
  only its behaviour for a deleted/unknown code (`404`) is asserted here.
- Malformed or syntactically invalid `{code}` values are treated the same as unknown
  codes and return `404`, consistent with how the existing redirect endpoint already
  handles unknown codes.
- If the system maintains any denormalized or cached aggregate statistics that
  incorporate a link's clicks (e.g. a global click counter), removing that link's
  contribution follows whatever mechanism the existing stats system already uses to
  stay consistent (recomputation, decrement, or cache invalidation) — this endpoint
  does not introduce a new aggregation strategy.
- The delete endpoint requires whatever authentication/authorization the existing
  write endpoints (e.g. link creation) already require; see the recorded decision in
  the run's decision log.