# Architecture Decision Records

Each record states the decision, the options rejected, the consequences, and how we would
know the decision was wrong. Records are immutable once accepted; a change means a new record
that supersedes the old one.

Decisions taken during orchestrated runs are recorded as `Decision` events in the run's audit
chain and rendered into records from that data, so the rationale is captured when the choice
is made rather than reconstructed afterwards.

| ADR | Decision | Status |
|---|---|---|
| [0001](0001-orchestrator-in-dotnet.md) | Implement the orchestrator in C# / .NET, with no agent framework | Accepted |
| [0002](0002-target-framework.md) | Target .NET 10 (current LTS), TFM and SDK pinned centrally | Accepted |
| [0003](0003-hexagonal-layering.md) | Engine depends on ports only; the CLI is the sole composition root | Accepted |
| [0004](0004-declarative-workflow.md) | Workflows are declarative YAML graphs, not code | Accepted |
| [0005](0005-event-sourced-state.md) | Run state is event-sourced in SQLite with a hash-chained audit log | Accepted |
| [0006](0006-git-backed-workspace.md) | Agent output lands in a per-run git workspace; rollback is a real revert | Accepted |
| [0007](0007-llm-record-replay.md) | LLM access sits behind a port with record/replay, so the submission runs offline | Accepted |
| [0008](0008-restricted-guard-grammar.md) | Conditional paths use a closed predicate grammar, not an expression evaluator | Accepted |
| [0009](0009-agents-propose-the-engine-applies.md) | Agents propose file changes; the engine applies them, and rollback reverts | Accepted |
| [0010](0010-policy-as-data-waivers-on-the-record.md) | Policy is declarative data, and a waiver overrides without silencing | Accepted |
| [0011](0011-incremental-replanning.md) | Re-planning is incremental and lazy, and withdraws the approvals it invalidates | Accepted |
| [0012](0012-metrics-derived-not-recorded.md) | Reliability metrics are derived from the log, never recorded alongside it | Accepted |

Enforcement: ADR-0001, 0002, 0003 and 0008 are asserted by
[`DependencyRuleTests`](../../tests/Mandate.Orchestrator.Tests/Architecture/DependencyRuleTests.cs),
so breaking them fails the build rather than the review.

## Exit codes

Commands return `0` success, `1` failed or verification defect, `2` bad input, and `3` the run
is complete as far as it can go and is waiting on a human. The last is deliberately distinct:
a run parked at an approval is not a broken run, and a script that could not tell the
difference would report the system's central behaviour as a failure.
