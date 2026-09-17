# Traceability

Every requirement in the assignment brief, mapped to the code that implements it, the test
that holds it in place, and the recorded evidence that it ran.

This document exists so a reviewer does not have to take any claim on trust. Each row is a
path you can open.

The three runs referenced throughout:

| Short name | Run id | Scenario |
|---|---|---|
| S1 | `run_20260917T004505Z_e2258e` | Greenfield |
| S2 | `run_20260917T012835Z_28d1ac` | Brownfield |
| S3 | `run_20260917T020240Z_c6e2f4` | Ambiguous |

Evidence lives in [`runs/<id>/`](../runs/) as `events.jsonl`, `run.json`, `timeline.md`
and a self-contained `report.html`.

---

## Section 4: Core requirements

### 1. Requirement Understanding

> Interpret intent, identify ambiguity, normalize into a clear engineering problem.

| | |
|---|---|
| **Implemented by** | [`prompts/requirements-analyst.v1.prompt.md`](../prompts/requirements-analyst.v1.prompt.md) produces a requirement spec and a separate ambiguity report, and scores ambiguity on a stated rubric |
| **Enforced by** | The `requirements` node declares it produces both `requirement-spec` and `ambiguity-report`; the agent fails if either is missing ([`ModelStageAgent.RefuseUndeclaredOutput`](../src/Mandate.Agents/Model/ModelStageAgent.cs)) |
| **Tested by** | `ModelStageAgentTests.A_declared_output_the_model_did_not_produce_fails_the_stage` |
| **Evidence** | S1 scored 0.30, S2 scored 0.15, S3 scored 0.35 then 0.1 after clarification. All three ambiguity reports are in the run workspaces as `docs/ambiguity.md` |

The ambiguity score is not decorative. A guard and an entry gate both read it, so the
number changes the path the run takes. See S3.

### 2. Task Decomposition

> Convert high-level requirements into actionable tasks with dependencies and sequencing.

| | |
|---|---|
| **Implemented by** | [`workflows/sdlc.v1.yaml`](../workflows/sdlc.v1.yaml): 11 nodes, 17 edges, declared dependencies, join policies, guards |
| **Validated by** | [`WorkflowGraph`](../src/Mandate.Core/Workflow/WorkflowGraph.cs), 34 validation codes (WF001 to WF034) covering cycles, unreachable nodes, undeclared context keys, missing loop-back triggers, split lifecycles |
| **Tested by** | `Mandate.Workflows.Tests`, 81 tests |
| **Evidence** | `mandate workflow validate` reports the depth ordering and the widest parallel group. The rendered graph is [`docs/diagrams/sdlc.v1.mmd`](diagrams/sdlc.v1.mmd) |

### 3. Codebase Reasoning (Brownfield)

> Identify impacted modules/services/APIs/data flows and demonstrate architectural
> understanding.

| | |
|---|---|
| **Implemented by** | The `impact-analysis` stage and [`prompts/codebase-analyst.v1.prompt.md`](../prompts/codebase-analyst.v1.prompt.md) |
| **Routed by** | The guard `run.has-existing-code == true` on the edge leaving `requirements` |
| **Fed by** | [`IWorkspaceReader`](../src/Mandate.Core/Execution/IWorkspaceReader.cs), a read-only view of the tree, adapted by [`FileWorkspaceReader`](../src/Mandate.Persistence/Workspaces/FileWorkspaceReader.cs) |
| **Tested by** | `FileWorkspaceReaderTests`, including path traversal, symlink escape and binary rejection |
| **Evidence** | S2 ran `impact-analysis` and set `impact.blast-radius` and `impact.affected-components`. S1 correctly skipped it, with the reason recorded |

The brownfield template is the tree S1 produced, so the analysis is over real code this
system wrote. See [the scenario write-up](scenarios/s2-brownfield.md).

### 4. Workflow Orchestration (Critical Differentiator)

The brief lists ten specific capabilities under this heading. Each is taken separately.

#### 4a. Explicit dependency graph with entry/exit gates

| | |
|---|---|
| **Implemented by** | [`workflows/sdlc.v1.yaml`](../workflows/sdlc.v1.yaml), `entry-gate` and `exit-gate` on each node; [`BuiltInGateEvaluators`](../src/Mandate.Orchestrator/Gates/BuiltInGateEvaluators.cs) supplies nine condition kinds |
| **Tested by** | `Mandate.Orchestrator.Tests` gate suites; every evaluator has a test for pass, fail and unreadable evidence |
| **Evidence** | Every `EntryGateEvaluated` and `ExitGateEvaluated` event in all three runs carries each condition's verdict and explanation. S1: 36 conditions, 2 blocking |

#### 4b. Sequential and parallel paths with synchronization

| | |
|---|---|
| **Implemented by** | Join policies `all`, `any` and `quorum`; bounded concurrency in [`RunExecution`](../src/Mandate.Orchestrator/Execution/RunExecution.cs) |
| **In the lifecycle** | Test, review, security scan and documentation run concurrently. `release-readiness` joins on `all`, a genuine barrier. `architecture` joins on `any`, because its two inbound paths are mutually exclusive and `all` would deadlock |
| **Tested by** | Orchestrator tests assert parallel branches are provably concurrent, and that a quorum join releases at the threshold |
| **Evidence** | `mandate workflow validate` reports "widest parallel group 4". All three runs show the four stages interleaved in the event log |

#### 4c. Cross-stage context and decision lineage

| | |
|---|---|
| **Implemented by** | [`RunContext`](../src/Mandate.Core/Context/RunContext.cs) with per-node scoping; [`ArtifactProvenance`](../src/Mandate.Core/Artifacts/ArtifactProvenance.cs) for `DerivedFrom` lineage; [`Decision`](../src/Mandate.Core/Decisions/Decision.cs) |
| **Least privilege** | A stage sees only context produced by its transitive dependencies, plus loop-back sources. A stage that cannot see unrelated facts cannot develop a hidden dependency on them |
| **Decision quality** | `Decision.Record` refuses fewer than two options, multiple chosen options, an unexplained rejection, or a missing rationale |
| **Tested by** | `Mandate.Core.Tests` provenance and decision suites, 256 tests total in that project |
| **Evidence** | 29 artifacts in S1, 23 in S2, 27 in S3, each with its `DerivedFrom` set. Any artifact can be traced back to the requirement it came from |

#### 4d. Human approval checkpoints for high-impact actions

| | |
|---|---|
| **Implemented by** | `approvals` on a node; `AwaitingApproval` in [`NodeStateMachine`](../src/Mandate.Core/Runs/NodeStateMachine.cs); `mandate approve` and `mandate deny` |
| **Which actions** | The design everything is built on, and the release itself. Plus clarification, which puts a question back to the requester |
| **Segregation of duties** | Enforced by policy: the approver is compared against both the producing agent and the run initiator. Clarification is exempt by explicit declaration, because the question belongs to the requester |
| **Tested by** | `Mandate.Policy.Tests`; `NodeStateMachineTests` asserts `Ready -> AwaitingApproval` does **not** exist, so there is no path to success without running |
| **Evidence** | S1 and S2 each have two `ApprovalGranted` events by two different named humans. S3 has an additional requester approval on clarification |

#### 4e. Bounded retries, fallback, rollback and safe-stop

| | |
|---|---|
| **Retries** | Per-node budget, exponential backoff with jitter, in [`RunExecution.ExecuteNodeAsync`](../src/Mandate.Orchestrator/Execution/RunExecution.cs) |
| **Fallback** | Declared per node: `fail-node`, `human-handoff`, or `compensate` |
| **Rollback** | [`RevertNodeCommitAction`](../src/Mandate.Orchestrator/Compensation/RevertNodeCommitAction.cs) performs a real `git revert`, newest first |
| **Safe-stop** | `mandate stop` sets a marker; the engine halts at the next node boundary with state preserved |
| **Tested by** | `GitRunWorkspaceTests` covers a clean revert and a conflicting one; orchestrator tests cover retry exhaustion into each fallback |
| **Evidence** | S1 recorded a 10% retry rate and 57s mean time to recovery. S3 recorded 28.6% across 14 attempts, with the test loop-back returning work to implementation. `--fail <agent>` reproduces any of these paths on demand |

A conflicting revert is reported as a failed compensation with a note that a human must
decide what the tree should contain, rather than crashing. That defect was found by S2.

#### 4f. Policy guardrails for security, compliance and change control

| | |
|---|---|
| **Implemented by** | [`PolicyEngine`](../src/Mandate.Policy/PolicyEngine.cs) and three declarative packs: [`security.yaml`](../workflows/policies/security.yaml), [`compliance.yaml`](../workflows/policies/compliance.yaml), [`change-control.yaml`](../workflows/policies/change-control.yaml) |
| **Ten rules** | SEC-001 to SEC-002, CMP-001 to CMP-004, CHG-001 to CHG-004. Each carries a mandatory `rationale` |
| **Waivers** | `mandate waive` records the rule, the human and the reason as an event. It overrides the block without silencing the finding |
| **Tested by** | `Mandate.Policy.Tests`, 44 tests |
| **Evidence** | `mandate policy list` prints the rules. `mandate policy check <runId>` evaluates every pack against a recorded run |

#### 4g. Audit-grade observability and traceability

| | |
|---|---|
| **Implemented by** | [`AuditChain`](../src/Mandate.Core/Events/AuditChain.cs), SHA-256 hash chaining over every event |
| **Detects** | Missing origin, sequence gap, duplicate sequence, bad genesis link, broken link, altered content, foreign run, timestamp regression |
| **Storage** | SQLite with append-only triggers that raise on any UPDATE or DELETE ([`SqliteSchema`](../src/Mandate.Persistence/Sqlite/SqliteSchema.cs)) |
| **Reporting** | [`HtmlRunReport`](../src/Mandate.Observability/Reporting/HtmlRunReport.cs), self-contained with no script and no network |
| **Tested by** | `Mandate.Core.Tests` audit suites cover each of the eight detections |
| **Evidence** | `mandate audit verify` reports all three chains intact. Reports at `runs/<id>/report.html` |

#### 4h. Reliability metrics

> success rate, retry/rollback frequency, MTTR, and end-to-end latency

| | |
|---|---|
| **Implemented by** | [`MetricsCalculator`](../src/Mandate.Observability/Metrics/MetricsCalculator.cs), a pure function from events to numbers |
| **Never stored** | Derived on demand, so no figure can be set, only caused. See [ADR-0012](adr/0012-metrics-derived-not-recorded.md) |
| **Reports** | Success rate, end-to-end latency, stage p50 and p95, retry rate, MTTR, rollback rate, gate block rate, approval wait, autonomy ratio, re-plan count, policy activity, model spend |
| **Tested by** | `Mandate.Observability.Tests`, 31 tests |
| **Evidence** | `mandate runs metrics <runId>`, and the tables in each scenario write-up |

#### 4i. Dynamic re-planning when upstream outputs change

| | |
|---|---|
| **Implemented by** | Content-hash invalidation and cascade in [`RunExecution`](../src/Mandate.Orchestrator/Execution/RunExecution.cs); `mandate amend` for human-initiated changes |
| **Approval-aware** | Invalidating a node revokes approvals granted against the invalidated work. A tech lead who approved one design has not approved a different one |
| **Bounded** | A re-plan budget; exhaustion records `ReplanRefused` rather than looping silently |
| **Tested by** | Orchestrator re-planning suites, including that amending one stage cascades only to what depended on it |
| **Evidence** | **S3 performed three re-plans**: clarification returning control to requirements, requirements producing different output, and the test loop-back returning work to implementation. All three are `ReplanPerformed` events |

#### 4j. Controlled agent autonomy with governance maintained

| | |
|---|---|
| **Implemented by** | An `autonomy` level per node: `propose-only`, `act-in-sandbox`, `act-and-auto-accept-low-risk` |
| **Enforced by** | Agents propose files; the engine validates every path and applies them. Agents never write. See [ADR-0009](adr/0009-agents-propose-the-engine-applies.md) |
| **Tested by** | `ModelStageAgentTests.A_path_outside_the_workspace_fails_the_stage` |
| **Evidence** | The autonomy ratio metric: 83.3% agent attempts against human decisions in S1, 87.5% in S3 |

### 5. Engineering Output Generation

> Produce production-quality code, API/schema definitions, unit/integration tests, and
> supporting documentation.

| | |
|---|---|
| **Produced** | [`services/url-shortener/`](../services/url-shortener/): seven source files, a test suite, an OpenAPI 3.1 contract, a design document, an ADR, a README |
| **Verified** | 96 tests pass when the tree is built independently, outside the orchestrator |
| **Quality gates** | The tree must compile before it is committed; coverage must meet 0.75; no review finding at or above `high` |
| **Evidence** | `cd services/url-shortener && dotnet test tests/Service.Tests/Service.Tests.csproj` |

### 6. Validation and Risk Control

> Identify risks/trade-offs/failure scenarios and define validation and safety guardrails.

| | |
|---|---|
| **Validation** | Real toolchain verification before commit: [`DotnetWorkspaceVerifier`](../src/Mandate.Persistence/Verification/DotnetWorkspaceVerifier.cs). A stage that overstates its results fails ([ADR-0015](adr/0015-verified-not-claimed.md)) |
| **Risk identification** | The `code-review` and `security-scan` stages, with severity ceilings enforced as gates |
| **Safety guardrails** | Path validation on every proposed write, a closed guard grammar with no code-execution surface ([ADR-0008](adr/0008-restricted-guard-grammar.md)), spend ceilings that refuse rather than report ([ADR-0013](adr/0013-model-spend-as-a-governed-budget.md)) |
| **Risks and trade-offs** | [`TESTING.md`](TESTING.md) sections "Limitations" and "Trade-offs" |
| **Evidence** | S1's review found a real SSRF bypass via IPv4-mapped IPv6 literals in code the system had written itself. The finding is in the run's `docs/mandate/review-report.md` |

### 7. Controlled Autonomy

> Agents execute multi-step work; humans provide oversight, approvals, and final quality
> control.

Covered by 4d and 4j above. The principle is enforced structurally rather than by
convention: agents cannot write to the workspace, cannot approve, cannot clear a policy
block, and cannot cause a state transition the machine forbids.

### 8. Final Engineering Summary

> Include plan/rationale, artifacts, risks/trade-offs/validation, assumptions, and
> limitations.

[`docs/FINAL-SUMMARY.md`](FINAL-SUMMARY.md), with supporting detail in the 17 ADRs in
[`docs/adr/`](adr/) and in [`TESTING.md`](TESTING.md). The ADRs carry the rationale
decision by decision, each naming the alternatives that were weighed and why each was
rejected.

---

## Section 5: Deliverables

| Deliverable | Where |
|---|---|
| Working prototype, runnable end to end | `make verify`, then `make run-model LLM=stub`. Three recorded runs replay offline with no key |
| Architecture overview | [`docs/ARCHITECTURE.md`](ARCHITECTURE.md) |
| Three scenarios | [greenfield](scenarios/s1-greenfield.md), [brownfield](scenarios/s2-brownfield.md), [ambiguous](scenarios/s3-ambiguous.md) |
| Setup instructions | [`docs/RUNBOOK.md`](RUNBOOK.md) and the [README](../README.md) |
| Testing approach, limitations, trade-offs | [`docs/TESTING.md`](TESTING.md) |

---

## Section 6: Evaluation criteria

| Criterion | Where it is demonstrated |
|---|---|
| Effectiveness of agentic orchestration | Three runs, 578 events between them, three re-plans, four human approvals, all chains intact |
| Architecture and system design quality | [`ARCHITECTURE.md`](ARCHITECTURE.md); hexagonal layering enforced by `DependencyRuleTests` rather than by convention |
| Depth of decomposition and execution quality | 11 stages, 17 edges, 3 conditional paths, 3 loop-backs; the lifecycle is data and validated by 34 rules |
| Realism and quality of outputs | A service that compiles and passes 96 tests when built outside the orchestrator |
| Validation and risk management rigour | Measured verification, severity ceilings, policy packs with audited waivers, spend guard that refuses |
| Clarity and defensibility of decisions | 17 ADRs with alternatives rejected and reasons, and a commit history that states the reasoning behind each change |
| Modular, testable, reliable, secure code with safe change management | 10 projects with an enforced dependency rule, 867 tests, warnings as errors, per-stage commits with real revert, and CI running build, tests, style and the offline demo from a clean clone on every push |
| Engineering judgment | [`FINAL-SUMMARY.md`](FINAL-SUMMARY.md), and the limitations stated in [`TESTING.md`](TESTING.md) rather than omitted |

---

## Coverage summary

All eight core requirements are implemented, tested and evidenced, with the ten
sub-capabilities of requirement 4 answered individually above. All five deliverables are
present. Every claim in this document resolves to a file you can open: a source path, a
test name, or an event in a committed run whose audit chain verifies.
