# Release Checklist — URL Shortener Service

## Scope

Build a URL shortener REST API service (SQLite-backed) that creates, redirects, and reports
click stats for short links, per `run.request` and `docs/requirements.md`.

Requirements were recorded with an ambiguity score of 0.3 (see `docs/ambiguity.md`) and 20
acceptance criteria (`requirements.acceptance-criteria: 20`). This is a greenfield build
(`run.has-existing-code: false`), initiated by `human:muskan`.

## What was built, against each requirement

The following is traced against the literal requirement text and the architecture components
recorded (`HttpApiLayer, LinkService, LinkRepository, UrlValidator, CodeGenerator,
SchemaInitializer, Configuration`), the API contract (`contracts/openapi.yaml`), and the
source patches listed upstream (`Program.cs`, `Models.cs`, `UrlValidator.cs`,
`CodeGenerator.cs`, `SchemaInitializer.cs`, `LinkRepository.cs`, `LinkService.cs`,
`appsettings.json`).

| Requirement | Built by | Evidence |
|---|---|---|
| `POST /api/v1/links` creates a link from `url` (required), `alias` (optional), `expiresAt` (optional ISO-8601 UTC) | HttpApiLayer, LinkService, Models.cs | `contracts/openapi.yaml`, `LinkService.cs`, `Models.cs` |
| Returns 201 with short code and absolute short URL | HttpApiLayer | `Program.cs`, `contracts/openapi.yaml` |
| Alias collision returns 409 and creates nothing | LinkService, LinkRepository | `LinkService.cs`, `LinkRepository.cs`, `tests/Service.Tests/LinkServiceTests.cs` |
| `GET /{code}` returns 302 to original URL and increments click count | HttpApiLayer, LinkRepository | `Program.cs`, `LinkRepository.cs`, `tests/Service.Tests/LinkRepositoryTests.cs` |
| Unknown code returns 404 | HttpApiLayer, LinkService | `LinkService.cs`, `tests/Service.Tests/LinkServiceTests.cs` |
| Expired link returns 410 Gone | LinkService | `LinkService.cs`, `tests/Service.Tests/LinkServiceTests.cs` |
| `GET /api/v1/links/{code}/stats` returns code, original URL, creation time, expiry, click count | HttpApiLayer, LinkRepository | `contracts/openapi.yaml`, `LinkRepository.cs` |
| Reject non-http/https scheme with 400 | UrlValidator | `UrlValidator.cs`, `tests/Service.Tests/UrlValidatorTests.cs` |
| Reject loopback/link-local/RFC 1918 literal IP hosts with 400, no DNS resolution | UrlValidator | `UrlValidator.cs`, `tests/Service.Tests/UrlValidatorTests.cs` |
| Persist links in SQLite | LinkRepository, SchemaInitializer | `LinkRepository.cs`, `SchemaInitializer.cs`, `docs/adr/0001-sqlite-write-serialization.md` |
| Codes generated as base62 over a monotonic identifier | CodeGenerator | `CodeGenerator.cs`, `tests/Service.Tests/CodeGeneratorTests.cs` |

**Finding — not directly verifiable from this run context:** the run context supplies 20
acceptance criteria as a count and aggregate test figures, but no artifact enumerating a
pass/fail result per individual acceptance criterion was made available to this stage. The
mapping above is a reconstruction from requirement text against component and test-file
names, not a verified per-criterion trace. An approver relying on 1:1 criterion coverage
should ask for that explicit trace before signing.

A dedicated ADR (`docs/adr/0001-sqlite-write-serialization.md`) addresses SQLite write
serialization, which is the expected concurrency risk point for a SQLite-backed service under
concurrent redirect/click-increment load.

## Tests

- Recorded failures: **0** (`test.failures: 0`)
- Recorded coverage: **82.59%** (`test.coverage: 0.8259`)
- Test suites present: `UrlValidatorTests.cs` (two versions listed upstream, 4990 and 4094
  bytes), `CodeGeneratorTests.cs` (two versions, 2045 and 1347 bytes), `LinkRepositoryTests.cs`
  (two versions, 9230 and 6251 bytes), `LinkServiceTests.cs` (two versions, 13866 and 5703
  bytes). Two test-report artifacts are also listed (5455 and 7692 bytes). The presence of two
  versions of each suite and report is recorded as-is from the run context; this checklist does
  not assert which is the final one, and an approver should confirm which test-report is
  authoritative before relying on the 82.59% figure as final.

No claim is made here that "tests passed" beyond the recorded figure: 0 recorded failures at
82.59% coverage.

## Review

- Findings: **2** (`review.findings: 2`)
- Highest severity: **medium** (`review.highest-severity: medium`)
- Full detail in `docs/mandate/review-report.md`; this checklist does not reproduce finding
  text and defers to that artifact for specifics. Both findings are being accepted as release
  risk below, since neither is above medium severity, but this checklist recommends they be
  tracked to closure rather than lost after release.

## Security

- Findings: **0** (`security.findings: 0`)
- Secrets found: **false** (`security.secrets-found: false`)
- Full scan detail in `docs/mandate/security-report.md`.

## Documentation

- `documentation.written: true` — README.md (12661 bytes) and design doc
  (`docs/design.md`, 11905 bytes) are present upstream.

## Build

- `implementation.builds: true`
- Commit: "Implement URL shortener API over SQLite"
- Files changed: 8

## Risks being accepted

1. Two medium-severity review findings are open (`review.findings: 2`,
   `review.highest-severity: medium`). Detail is in `docs/mandate/review-report.md`. Accepted
   for this release because neither reaches high/critical severity, but should be tracked as
   post-release follow-up work.
2. Requirement ambiguity score is 0.3 (`requirements.ambiguity-score: 0.3`), non-zero. The
   specific ambiguous points are recorded in `docs/ambiguity.md`; this checklist does not
   restate them but flags that some interpretation decisions were made upstream of this stage.
3. Two differing versions of each test suite and test-report artifact were supplied to this
   stage with no indication of which superseded which. This checklist treats the coverage and
   failure figures given (`test.coverage: 0.8259`, `test.failures: 0`) as the ones to rely on,
   but cannot independently confirm they come from the final suite version.
4. Per-acceptance-criterion pass/fail evidence was not available to this stage (see Finding
   above); only aggregate test and criterion-count figures were available.

## Rollback triggers

- Any 500-class error rate increase on `POST /api/v1/links` or `GET /{code}` observed in
  production that was not present in the recorded test run.
- Discovery that SQLite write serialization (per `docs/adr/0001-sqlite-write-serialization.md`)
  does not hold under real concurrent load, causing data corruption or lost writes.
- Discovery that the loopback/link-local/RFC 1918 validation in `UrlValidator.cs` has a gap
  allowing SSRF-style redirects to internal hosts.
- Any post-release finding that raises the review severity above medium, or any newly
  discovered secret exposure.

## Actions required of the human approver

- Confirm which of the two test-report artifacts and the two versions of each test suite is
  authoritative, and re-derive the coverage/failure figures from that one if it differs from
  what is recorded here.
- Review `docs/mandate/review-report.md` and decide whether the 2 medium-severity findings are
  acceptable to ship with, or must be fixed pre-release.
- Confirm acceptance-criteria coverage directly against `docs/requirements.md` (20 criteria),
  since this stage could not verify a 1:1 pass/fail mapping.
- Decide on and communicate the rollback owner and process before enabling production traffic.

## Recommendation

**go** — subject to the human approver completing the actions above. The recorded figures
(0 test failures, 82.59% coverage, 0 security findings, no secrets, 2 medium review findings)
support release, but this is not a claim that release is risk-free; the risks and required
actions above are the conditions under which "go" holds.