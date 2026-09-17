# Release Checklist — DELETE /api/v1/links/{code}

## 1. What was built

Scope (from `requirements.scope`): Add a permanent DELETE endpoint (`DELETE /api/v1/links/{code}`)
to the existing URL shortener that removes a link and its click statistics, returns 204 on
success and 404 when the code is unknown, and leaves every other endpoint's behaviour unchanged.

Commit: "Add permanent DELETE /api/v1/links/{code} endpoint" — 6 files changed
(`implementation.files-changed`).

Affected components (`impact.affected-components`):
- LinkRepository.cs
- LinkService.cs
- Program.cs
- Models.cs
- contracts/openapi.yaml
- README.md
- tests/Service.Tests/LinkRepositoryTests.cs
- tests/Service.Tests/LinkServiceTests.cs

Blast radius recorded by impact analysis: **high**. Design is governed by ADR 0002
("single-statement-atomic-delete"), which record why the delete is implemented as a single
atomic statement rather than a soft-delete flag.

## 2. Acceptance criteria vs. evidence

`requirements.acceptance-criteria` records **10** acceptance criteria in `docs/requirements.md`.
This checklist maps the requirement's stated behaviours to the artifacts produced; it does not
re-derive the criteria text, since the authoritative list lives in `docs/requirements.md`.

| Behaviour required by the request | Evidence |
|---|---|
| `DELETE /api/v1/links/{code}` removes link + stats | Implemented in LinkRepository.cs / LinkService.cs per source patches; covered by `tests/Service.Tests/LinkRepositoryDeleteTests.cs` and `tests/Service.Tests/LinkServiceDeleteTests.cs` |
| Returns 204, no body, on success | Asserted in the delete test suites listed above (test bodies not re-verified line-by-line here — see test-report for pass/fail counts) |
| Returns 404 when code doesn't exist | Same test suites; also stated in `docs/design.md` |
| Deleted code behaves as unknown afterwards: GET /{code} → 404 | Covered by delete test suites per file names; explicit cross-endpoint check not separately itemized in this checklist beyond the recorded test suite existence |
| Stats endpoint → 404 for deleted code | Same as above |
| Deletion is permanent, no soft delete/recovery | ADR 0002 explicitly documents the atomic, non-recoverable delete design |
| All other endpoints' behaviour unchanged | Impact analysis lists no changes outside the enumerated files; `contracts/openapi.yaml` was updated for the new endpoint only |

**Gap:** this checklist was not given the acceptance-criteria text itself, only the count (10)
and the requirement spec artifact. A release approver should confirm the 10 criteria in
`docs/requirements.md` are each individually checked off — that mapping is not independently
re-verifiable from the run context facts supplied here.

## 3. Test status

- Recorded failures: **0** (`test.failures`)
- Recorded coverage: **81.57%** (`test.coverage`)
- Dedicated new test suites for the delete path exist: `LinkRepositoryDeleteTests.cs`,
  `LinkServiceDeleteTests.cs`, in addition to updates to the pre-existing
  `LinkRepositoryTests.cs` and `LinkServiceTests.cs`.
- Two `test-report` artifacts are listed upstream (4921 bytes and 11354 bytes) — this checklist
  cannot tell from the run context whether the smaller report is superseded or is a partial run;
  that should be resolved by the approver before sign-off if it matters to them.
- Build status: **builds = true** (`implementation.builds`).

## 4. Review

- `review.findings`: **0**
- `review.highest-severity`: **none**
- A `review-report` artifact exists at `docs/mandate/review-report.md`.

## 5. Security

- `security.findings`: **0**
- `security.secrets-found`: **false**
- A `security-report` artifact exists at `docs/mandate/security-report.md`.

## 6. Documentation

- `documentation.written`: **true**
- README.md was updated as part of this change (listed in both source patches and
  documentation artifacts).
- `contracts/openapi.yaml` was updated for the new endpoint.

## 7. Risks accepted

- **Deletion is permanent and irreversible by design** (explicit requirement, confirmed by ADR
  0002). There is no soft-delete or recovery path. This is an intentional product decision, not
  an oversight, but it means any bug in the delete path has no undo.
- **Blast radius is recorded as high** despite the change being scoped to one new endpoint,
  because the repository layer's delete logic touches shared data paths (links + click stats).
  Mitigated by dedicated delete-path test suites, 0 review findings, 0 security findings.
- **Ambiguity score of 0.15** on the requirement — low, but non-zero. No specific open questions
  were surfaced in the facts available here beyond the score itself; see `docs/ambiguity.md` for
  detail not reproduced in this checklist.
- The acceptance-criteria-to-evidence mapping in section 2 is checklist-level, not
  criterion-by-criterion verified against the 10 recorded criteria — flagged as a gap above.

## 8. Rollback trigger

Roll back this release if, after deployment:
- `DELETE /api/v1/links/{code}` returns anything other than 204 on a valid existing code, or
  anything other than 404 on an unknown/already-deleted code.
- `GET /{code}` or the stats endpoint returns anything other than 404 for a code that was
  deleted.
- Any other endpoint's previously-passing behaviour regresses (per the "unchanged" requirement).
- Any data loss or corruption is observed beyond the intended link + stats removal for the
  targeted code (e.g. cascading deletes affecting unrelated codes).

Because deletion is permanent, a rollback of the *code* cannot undo already-executed deletes
against production data. There is no recorded recovery mechanism — this should be surfaced to
the human approver explicitly as a standing operational risk, not just a deploy-time concern.

## 9. Human actions required

- Before release: the named approver (per `run.initiated-by`: human:muskan or a designated
  release approver) must sign off on this checklist, and specifically confirm the 10 acceptance
  criteria in `docs/requirements.md` are each satisfied, since that per-criterion confirmation
  is not independently reconstructed here.
- Before release: resolve which of the two `test-report` artifacts is authoritative, if that
  matters to the approver's sign-off.
- After release: monitor for the rollback triggers listed in section 8, given the irreversible
  nature of the delete operation.

## 10. Decision

**Recommendation: GO**

Rationale: build succeeds, recorded test failures are 0 with 81.57% coverage, review and
security findings are both 0 with no secrets found, and the design is deliberately documented
(ADR 0002) rather than incidental. The recommendation is conditioned on the approver confirming
the acceptance-criteria mapping gap noted in section 2/7 and resolving the duplicate test-report
artifact question in section 9. This is a recommendation only; the named human approver retains
the decision.