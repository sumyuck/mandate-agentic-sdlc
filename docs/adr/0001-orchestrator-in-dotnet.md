# ADR-0001: Implement the orchestrator in C# / .NET

- **Status:** Accepted
- **Date:** 2026-09-16
- **Decider:** Human (Muskan Jain)

## Context
The assignment requires an agentic SDLC orchestration layer plus a URL shortener fixture.
The orchestrator is the largest and most heavily weighted artifact. Python has the denser
agent-framework ecosystem and would be faster to write. The engagement this system is built
for is a .NET engineering role on a trading/financial platform.

## Decision
Write both the orchestrator and the fixture service in C# on .NET, with no agent framework
dependency. The orchestration engine is built from first principles: graph, state machine,
event store, gates, policy.

## Alternatives considered
| Option | Why we rejected it |
|---|---|
| Python + LangGraph / CrewAI / AutoGen | Fastest path, but the framework would supply exactly the capability being assessed (graph, state, retries). Demonstrating orchestration by importing someone else's orchestrator inverts the point of the exercise, and it sends the wrong signal for a .NET role. |
| Python orchestrator + .NET fixture | Splits the toolchain, doubles the setup burden on a reviewer, and puts the largest artifact in the wrong language. |
| .NET + an off-the-shelf workflow engine (Elsa, Workflow Core, Temporal) | Same objection as the Python frameworks, plus heavier infrastructure. Temporal in particular would need a server, breaking the "clone and run" requirement. |

## Consequences
- The engine's behaviour is fully ours to explain in an interview, every gate, retry and
  rollback decision is defensible line by line.
- More code to write than a framework-based approach; mitigated by the build ordering in
  `PLAN.md`, where the governance core lands before the agents.
- No framework upgrade path. Acceptable: this is a prototype, and the design is documented
  well enough to port.

## Validation
The engine must satisfy every orchestration requirement in the brief without a workflow
library on the dependency list. `Directory.Packages.props` is the evidence: if an
orchestration framework appears there, this decision failed.
