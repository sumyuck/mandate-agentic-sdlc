# ADR-0006: Agent output lands in a per-run git workspace; rollback is a real revert

- **Status:** Accepted
- **Date:** 2026-09-16
- **Decider:** Human (Muskan Jain)

## Context
The brief requires rollback and safe-stop. Many agentic demos "roll back" by writing a log
line saying rollback occurred while the generated files stay on disk. That is theatre. We need
compensation that genuinely returns the workspace to its prior state, and we need the
evidence of what each node produced to be reviewable.

## Decision
Each run executes against a git repository workspace at
`.helmsman/workspaces/<runId>/`. Each node that writes code commits its own output, tagged
with the node id and the run id. Compensation for a node is a real `git revert` of that
node's commit, executed in reverse topological order. Safe-stop leaves the workspace at a
known commit.

The reviewable outputs are promoted in two directions:
- the final materialised service is committed to `services/` in this repository, so a
  reviewer reads ordinary code;
- per-run diffs, patches and artifacts are committed under `runs/<runId>/`, so a reviewer can
  see exactly which node produced which line.

## Alternatives considered
| Option | Why we rejected it |
|---|---|
| Write directly into `services/` on the main branch | Run mechanics would pollute project history, and a failed run would leave the repository dirty. Rollback would mean reverting commits a human also authored. |
| File snapshot / copy-on-write directory | Works, but produces no diff a human can review, and reimplements a worse git. |
| In-memory virtual filesystem | Nothing to inspect after the run, and the test agent could not shell out to `dotnet test` against it. |
| Git submodule for the workspace | Extra clone step for reviewers, for no added isolation. |

## Consequences
- Rollback is verifiable: after compensation, `git status` is clean and the tree matches the
  pre-node commit.
- Every generated line has provenance down to the node that wrote it.
- The test agent can run `dotnet test` against a real tree, so gate verdicts come from real
  test results rather than model assertions.
- Requires git on the host and a small git wrapper. Acceptable: git is already a hard
  prerequisite for obtaining the submission.

## Validation
Inject a failing test, exhaust the retry budget, and assert that the workspace returns to the
pre-implementation commit and that the rollback is recorded in the audit chain.
