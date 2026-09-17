# Ambiguity Report — Delete Endpoint for URL Shortener

## Score: 0.15 (build-it band)

All gaps identified below are resolvable by a competent engineer inspecting the
existing codebase (this is a brownfield change with code already present) and applying
its established conventions, rather than requiring a new policy decision from the
requester. Nothing here changes *what* is being built — a permanent delete endpoint
with the specified status codes — only *how* it is wired into existing infrastructure.
No gap here meets the bar of "guessing wrong builds the wrong product" or introduces an
undefensible silent default on a safety/money/data-loss axis that isn't already
explicitly settled by the request itself (the request is explicit that deletion is
permanent and unrecoverable, which is the one genuine data-loss decision, and it is
stated, not ambiguous).

## Findings

### 1. Who is allowed to call the delete endpoint

**Quote:** "DELETE /api/v1/links/{code} removes a link and its click statistics."

The request never says whether this endpoint requires authentication, and if so,
which caller is entitled to delete a given link (e.g. only its creator, any
authenticated user, or anyone who knows the code).

- **Interpretation A — reuse existing auth pattern.** The endpoint enforces whatever
  auth middleware/ownership check the existing write endpoints (e.g. link creation)
  already use. Built as: add the route behind the same middleware stack, no new auth
  logic.
- **Interpretation B — no authentication.** Anyone who knows a code can delete it, same
  as anyone can currently trigger `GET /{code}`. Built as: no auth check added at all.
- **Interpretation C — new elevated permission.** A stricter, delete-specific
  permission is introduced (e.g. admin-only, or "only original creator"). Built as: new
  authorization logic and possibly new data (link ownership) not otherwise required by
  this task.

**Resolution:** treated as non-material here because the existing codebase already
answers this — the engineer inspects how other write endpoints (particularly link
creation) are protected and mirrors it. This is recorded as a decision in the run log
rather than escalated, since brownfield context makes it discoverable rather than a
guess about unstated intent. It would become material only if the existing codebase
itself has no auth convention on any endpoint and link ownership is otherwise
undefined — in that case, a human should confirm whether "anyone can delete any link"
is truly acceptable.

### 2. Effect on aggregate/derived statistics

**Quote:** "removes a link and its click statistics"

This is clear about the per-link record and its own click statistics. It is silent on
whether any denormalized or aggregate statistics elsewhere in the system (e.g. a
site-wide total click count, a "most popular links" cache, or per-user totals) must be
adjusted when a link's clicks disappear.

- **Interpretation A — stats are computed on read.** If aggregates are always derived
  live from the underlying per-link data, deleting the per-link rows automatically
  fixes every aggregate; no extra work needed.
- **Interpretation B — stats are stored/denormalized.** If some aggregate counters are
  stored independently (incremented on each click, not recomputed), deletion must also
  decrement or invalidate those stored values, or they will silently overcount forever.

**Resolution:** non-material as an ambiguity to escalate — it is a fact about the
existing system's architecture, not a choice to be made from the request text. The
engineer must inspect how stats are currently computed and stored before implementing,
and this is recorded as an assumption in the requirements spec rather than a question
for the requester.

### 3. Whether a code can be reused after deletion

**Quote:** "Deleting is permanent; there is no soft delete and no recovery."

This clearly establishes that the deleted link's own data cannot come back. It does
not say whether the *code string itself* becomes available again for a brand-new link
to be created with the same code afterward.

- **Interpretation A — code becomes free.** Once deleted, the code is indistinguishable
  from any other never-used code, and the existing creation endpoint may assign or
  accept it again.
- **Interpretation B — code is permanently retired.** The system keeps a marker (code
  reserved but not resolvable) so the same code can never be issued again, e.g. to
  avoid confusing users who shared the old link.

**Resolution:** non-material and explicitly out of scope for this task — the request
says the delete endpoint must make the code behave "exactly as an unknown one," which
directly implies Interpretation A (a deleted code is indistinguishable from a
never-created one, and the creation endpoint's existing rules for unknown codes apply
unchanged). This is stated plainly enough that no ambiguity remains; recorded here only
because it is the kind of question a less careful reading might have flagged as open.

## Assumptions Carried Into the Spec (not raising the score)

- Malformed `{code}` path values are treated as "not found" (`404`), matching how the
  existing redirect endpoint already handles invalid codes.
- The "stats endpoint" referenced in the request is the existing per-code stats route
  already present in the codebase; no new stats endpoint is introduced.
- Deletion is implemented as a synchronous, transactional operation; no asynchronous or
  eventually-consistent deletion is implied by "permanent."