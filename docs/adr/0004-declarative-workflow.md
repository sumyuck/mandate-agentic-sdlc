# ADR-0004: Workflows are declarative YAML graphs, not code

- **Status:** Accepted
- **Date:** 2026-09-16
- **Decider:** Human (Muskan Jain)

## Context
The brief demands "an explicit dependency graph with entry/exit gates" and explicitly
rejects "simple linear task chaining". A workflow expressed as imperative C#, a sequence of
awaits, or a `foreach` over stages, cannot be inspected, diffed, versioned or reasoned about
before it runs, and cannot be shown to a reviewer as a graph.

## Decision
Workflows are declared in versioned YAML (`workflows/sdlc.v1.yaml`): nodes, edges with guard
expressions, join nodes, gates, retry policies, compensations, autonomy levels and approval
requirements. The engine loads, statically validates, and interprets that definition. There is
no orchestration control flow in C#.

## Alternatives considered
| Option | Why we rejected it |
|---|---|
| Imperative C# pipeline | Opaque: the plan only exists at runtime. No pre-flight validation, no rendering, and re-planning would mean rewriting code paths. |
| Fluent C# builder DSL | Better than raw imperative, but the graph is still compiled in. A workflow change needs a rebuild, and a reviewer cannot read the plan without reading code. |
| BPMN / XML | Far heavier, tooling-dependent, and hostile to review in a plain diff. |

## Consequences
- The workflow can be validated before execution (reachability, unknown agents, gate typos)
  and rendered as a diagram for the architecture docs.
- Changing the lifecycle is a config change under change control, not a code deployment,
  which is itself the governance story the brief is asking about.
- Requires building a loader, a validator and a small guard-expression evaluator. Accepted
  cost; it is also where a lot of the assessed value sits.
- YAML is stringly typed. Mitigated by strict schema validation that fails loudly at load.

## Validation
An invalid workflow must be rejected at load with a precise, actionable message; never
silently skipped, and never discovered mid-run.
