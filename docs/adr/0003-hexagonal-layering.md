# ADR-0003: The engine depends on ports only; the CLI is the sole composition root

- **Status:** Accepted
- **Date:** 2026-09-16
- **Decider:** Human (Muskan Jain)

## Context
The orchestration engine needs persistence, policy evaluation, telemetry and LLM access. The
naive wiring is `Orchestrator -> Persistence, Policy, Observability, Agents`. That makes the
engine untestable without a database, and makes "what does the engine actually decide?"
impossible to answer in isolation, which is precisely the question this assessment asks.

## Decision
`Mandate.Core` owns the domain model *and* every port (interface) the engine needs.
`Mandate.Orchestrator` references **only** `Mandate.Core`. Adapters
(`Persistence`, `Policy`, `Llm`, `Observability`, `Agents`) reference `Core` and are bound to
ports in exactly one place: `Mandate.Cli/Program.cs`.

## Alternatives considered
| Option | Why we rejected it |
|---|---|
| Engine references adapters directly | Cannot unit-test scheduling, gating or rollback without SQLite, a policy file and a model provider. The governance logic would only ever be testable as an integration test. |
| Single project | Nothing structurally prevents the engine from reaching into infrastructure; layering becomes a naming convention instead of a compiler-enforced constraint. |
| Separate `Mandate.Ports` project | An extra assembly whose contents are inseparable from the domain model they describe. |

## Consequences
- Engine behaviour is unit-testable with in-memory fakes; `Mandate.Orchestrator.Tests` needs
  no infrastructure at all.
- The dependency rule is enforced by the compiler: an adapter reference added to the
  orchestrator project is a visible, reviewable change to one `.csproj`.
- Slightly more ceremony when adding a capability: define the port, then the adapter, then
  the binding.

## Validation
`Mandate.Orchestrator.csproj` must list exactly one `ProjectReference`: `Mandate.Core`.
This is asserted as an architecture test, not left to review.
