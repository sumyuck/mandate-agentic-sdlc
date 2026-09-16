# services/

This directory holds **Product A**: the URL shortener.

It is deliberately empty at this stage. The service is not hand-written — it is produced by
Helmsman runs, then promoted here as the final materialised state so that a reviewer reads
ordinary, reviewable code.

- Scenario **S1 (greenfield)** produces the core service: create + redirect.
- Scenario **S2 (brownfield)** extends it with click analytics and per-API-key rate limiting.
- Scenario **S3 (ambiguous)** exercises link-security requirements under clarification.

Per-node diffs, patches and artifacts for each run are committed under `runs/<runId>/`, so
every line here can be traced back to the node and decision that produced it.
See [ADR-0006](../docs/adr/0006-git-backed-workspace.md).
