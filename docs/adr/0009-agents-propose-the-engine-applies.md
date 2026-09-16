# ADR-0009: Agents propose file changes; the engine applies them, and rollback reverts

- **Status:** Accepted
- **Date:** 2026-09-16
- **Decider:** Human (Muskan Jain)

## Context
Stages have to produce code. The obvious design hands each agent a working directory and lets
it write — which is what most agentic coding tools do, and what the phrase "the agent may act
in the run workspace" naturally suggests.

That design has two problems for a governed system. The autonomy boundary becomes a
description of intended behaviour rather than something the engine can enforce: nothing stops
a stage writing outside its tree, into a git hook, or over another stage's output. And
compensation becomes unverifiable, because the engine never knew precisely what the stage
changed.

## Decision
**Agents return the files they propose. The engine validates and applies them.**

`StageResult.Files` carries path/content pairs. The engine checks every path against the
workspace boundary — no absolute paths, no drive roots, no parent traversal, nothing into
`.git` — then writes them and commits them as that node's contribution.

**Each node's output is its own commit**, tagged with a `Mandate-Node` trailer naming the node
and attempt.

**Compensation is `git revert`, never `git reset`.** Reverting adds a commit that undoes the
change; both the change and its reversal stay in the history.

**Rollback proceeds in reverse topological order**, and a node with nothing to undo is left
alone rather than marked rolled back.

## Alternatives considered
| Option | Why we rejected it |
|---|---|
| Give each agent a directory handle | The boundary stops being enforceable. A stage could write a git hook — code the host then executes — and nothing in the engine would be in a position to refuse. |
| Agents return a unified diff | More faithful to how a human reviews, but a diff that does not apply cleanly is an error class we would have to handle for no gain; full contents always apply. |
| `git reset --hard` to roll back | Simpler, and it destroys the evidence. For a system whose purpose is an auditable trail, erasing the record that work happened is the wrong instinct — an auditor wants to see the mistake *and* its correction. |
| Snapshot the directory and restore it | Works, produces no reviewable diff, and reimplements a worse git. |
| Roll back only the failing node | Leaves the tree holding work built on a premise that turned out to be wrong: a design whose implementation could never be made to pass. |
| A managed git library | Another dependency to ship, and the resulting repository is less obviously an ordinary one that a reviewer can open with their own tools. |

## Consequences
- The autonomy boundary is enforced, not described. A stage proposing `.git/hooks/pre-commit`
  fails — and fails *as a stage*, so it goes through the same retry and compensation machinery
  as any other failure rather than taking the run down.
- Rollback is checkable. After compensation the tree can be inspected: added files are gone,
  edited files are as they were, `git status` is clean. Tests assert exactly that against real
  git rather than against a stub.
- Every generated line has provenance to the node and attempt that produced it, readable with
  ordinary `git log`.
- A failed attempt's partial writes are discarded before anything else happens, so the
  workspace is never left in a state no node declared.
- Agents cannot perform side effects the engine does not mediate. That is a constraint on what
  agents can be asked to do, and an accepted one.
- Requires git on the host — already a prerequisite for obtaining the submission.

## Validation
- `GitRunWorkspaceTests` asserts that after reverting a node, its added files are gone, its
  edited files match their prior contents, and the tree is clean — against real git.
- A stage proposing a path outside the workspace must fail the stage, not the engine, and
  nothing must be written.
- Rollback must leave the change and its reversal both present in the history.
