# Product A, the URL shortener

**Not written by hand.** Every file under `url-shortener/` was produced by a Mandate run:
the design, the code, the tests, the OpenAPI contract, the ADR, the README, and the
governance records under `docs/mandate/`. It is the *fixture*, the thing the orchestrator
was asked to build so that the orchestrator itself has something real to be judged on.

## Which runs produced it

Two, in sequence, which is why the tree looks like a codebase rather than a snapshot.

**`run_20260917T004505Z_e2258e`**, greenfield, from
[`../scenarios/s1-greenfield.txt`](../scenarios/s1-greenfield.txt). Built the service from
an empty template: nine stages succeeded, two were correctly skipped as not required on the
greenfield path, 171 events. 70 tests.

**`run_20260917T012835Z_28d1ac`**, brownfield, from
[`../scenarios/s2-brownfield.txt`](../scenarios/s2-brownfield.txt). Added the delete
endpoint to the tree the first run produced, which is the honest way to do a brownfield
scenario: the existing code is genuinely code this system wrote. Ten stages succeeded,
including `impact-analysis`, which only runs when there is existing code to reason about,
and one was skipped. 179 events. 96 tests.

Each run's complete evidence is under [`../runs/`](../runs/): every event, a timeline, and
a self-contained HTML report. Both chains verify.

Four named humans across the two runs: `alex` approved each design, `priya` approved each
release. Neither is the requester, which the segregation-of-duties check enforces.

## Check it yourself

```bash
cd services/url-shortener && dotnet test tests/Service.Tests/Service.Tests.csproj
```

96 tests pass, and that run is independent of the orchestrator. The point of copying the
tree out is that the result is not one the system produced while grading itself.

## What it does

`POST /api/v1/links` creates a short link, with an optional alias and expiry. `GET /{code}`
redirects and counts the click. `GET /api/v1/links/{code}/stats` reports what it knows.
`DELETE /api/v1/links/{code}` removes a link and its statistics, added by the second run.
URLs that are not http or https are rejected, and so are hosts in loopback, link-local and
RFC 1918 private ranges.

## What the review found

The run's own code review is in
[`url-shortener/docs/mandate/review-report.md`](url-shortener/docs/mandate/review-report.md)
and it is worth reading, because the reviewer is not flattering. The findings it raised
under the blocking threshold were accepted and remain in the code; a release checklist that
recorded no residual risk would be the suspicious one.

The scope is deliberately modest: SQLite, an in-process cache, no message broker, no
Kubernetes. The run's own design document records why, and the service's two architecture
decision records state what those choices cost:
[`design.md`](url-shortener/docs/design.md),
[`docs/adr/`](url-shortener/docs/adr/).
