# Product A — the URL shortener

**Not written by hand.** Every file under `url-shortener/` was produced by a Mandate run:
the design, the code, the tests, the OpenAPI contract, the ADR, the README, and the
governance records under `docs/mandate/`. It is the *fixture* — the thing the orchestrator
was asked to build so that the orchestrator itself has something real to be judged on.

## Which run produced it

`run_20260917T004505Z_e2258e` — greenfield, the requirement in
[`../scenarios/s1-greenfield.txt`](../scenarios/s1-greenfield.txt). Its complete evidence is
in [`../runs/run_20260917T004505Z_e2258e/`](../runs/run_20260917T004505Z_e2258e/): every
event, the timeline, and a self-contained HTML report.

Nine stages succeeded, two were correctly skipped as not required on the greenfield path,
and two named humans signed it — `alex` approved the design, `priya` approved the release.
Neither is the requester. The audit chain verifies across all 171 events.

## Check it yourself

```bash
cd services/url-shortener && dotnet test tests/Service.Tests/Service.Tests.csproj
```

70 tests pass. That run is independent of the orchestrator — the point of copying the tree
out is that the test result is not something the system grading itself produced.

## What it does

`POST /api/v1/links` creates a short link, with an optional alias and expiry. `GET /{code}`
redirects and counts the click. `GET /api/v1/links/{code}/stats` reports what it knows.
URLs that are not http or https are rejected, and so are hosts in loopback, link-local and
RFC 1918 private ranges.

## What is honestly wrong with it

The run's own code review is in
[`url-shortener/docs/mandate/review-report.md`](url-shortener/docs/mandate/review-report.md)
and it is worth reading, because the reviewer is not flattering. The findings it raised
under the blocking threshold were accepted and remain in the code; a release checklist that
recorded no residual risk would be the suspicious one.

The scope is deliberately modest — SQLite, an in-process cache, no message broker, no
Kubernetes. [`../PLAN.md`](../PLAN.md) §4 records why that was cut and what it cost.
