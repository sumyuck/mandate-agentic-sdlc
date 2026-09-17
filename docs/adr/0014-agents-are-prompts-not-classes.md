# ADR-0014: An agent is a prompt and a declaration, not a class

- **Status:** Accepted
- **Date:** 2026-09-16
- **Decider:** Human (Muskan Jain)

## Context
The lifecycle names eleven agents: intake, requirements analyst, clarification broker,
codebase analyst, architect, implementer, test engineer, reviewer, security scanner,
technical writer, release manager.

The obvious shape is eleven classes. Having written the first two, the difference between
them was entirely in their prompt text: both render a prompt, call a model, parse the answer,
build artifacts with provenance, contribute context facts, record decisions, and report what
the call cost. Eleven copies of that would be eleven places for the retry semantics, the
provenance wiring, or the cost accounting to drift, and a reviewer would have to read all
eleven to discover that ten of them were the same.

## Decision
One class, `ModelStageAgent`, and eleven prompt files. An agent is defined by four things,
all of them data:

| What distinguishes an agent | Where it lives |
|---|---|
| What it is asked to do | `prompts/<agent>.v1.prompt.md` |
| Which model runs it | `model:` on the node in `workflows/sdlc.v1.yaml` |
| What it must produce | `produces:` / `produces-context:` on the node |
| What its output must satisfy | `entry-gate:` / `exit-gate:` on the node |

The agent's id *is* its prompt's id, so adding a stage without writing its prompt fails at
composition with a message naming the file to create, rather than at the moment that stage
first runs.

**The workflow's declaration is enforced against the model's answer**, in both directions:

- A declared output the model did not produce fails the stage. It would fail the exit gate
  anyway, but several steps later and with a worse message.
- An **undeclared** output fails the stage too, and this is the direction that matters. Gates
  are written against what a node declares it produces, so an extra artifact is checked by
  nothing, and an extra context fact could be branched on by a guard that validation never
  saw. A stage able to emit facts nobody validated can route a run down a path nobody
  reviewed.

The same applies to decisions: the domain refuses a decision with fewer than two options or
an unexplained rejection, and the agent lets that refusal be the stage's failure rather than
swallowing it. A "decision" with nothing rejected is a statement.

## Alternatives considered
| Option | Why we rejected it |
|---|---|
| Eleven agent classes | Eleven copies of retry, provenance and cost accounting. The real difference between the agents is their instructions, and code is the wrong place to keep instructions. |
| One class, prompts embedded as C# strings | A prompt change would be invisible in a code review as a behaviour change, and could not be versioned independently of the assembly. |
| A bespoke response schema per agent | Eleven parsers to keep in step with one interface. The engine's contract with a stage is already exactly "artifacts, facts, decisions, files"; the model's contract mirrors it. |
| Accept whatever the model returns and let the gates catch it | Gates only check what a node declares. Undeclared output is never checked at all, so "the gates will catch it" is false for the case that matters most. |

## Consequences
- Adding a lifecycle stage is: a node in the YAML, and a prompt file. No code.
- Changing what a stage does is a diff in one Markdown file, reviewable by someone who does
  not read C#.
- The prompt library's fingerprint is recorded on every run, so "which instructions produced
  this evidence" is answerable from the evidence.
- Prompts are rendered only from the requirement, the scoped context and the tree; never
  from the run id or the attempt number, so the same question is asked on a retry and a run
  can be replayed (ADR-0007).
- **A gap that was recorded here and has since been closed.** Three context facts,
  `implementation.builds`, `test.coverage` and `test.failures`, were originally the model's
  own assertion about its own work, which made three gates check a claim rather than a fact.
  [ADR-0015](0015-verified-not-claimed.md) replaced them with measurements from a real
  `dotnet build` and `dotnet test`, and made a stage that overstates its own results fail.
  The entry is left in place rather than deleted: the shape of the mistake is worth keeping,
  and so is the fact that it was written down before it was found by someone else.

## Validation
- `AgentCoverageTests` asserts every agent the lifecycle names has a prompt, every model it
  names is priced, and the offline stub satisfies every node's declared contract.
- `ModelStageAgentTests` asserts each refusal separately, including the undeclared-output and
  undeclared-fact cases, and that a retry asks a byte-identical question.
- A lifecycle naming an agent with no prompt fails composition with the filename to create.
