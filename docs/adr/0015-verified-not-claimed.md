# ADR-0015: Build and test results are measured, and a stage that overstates them fails

- **Status:** Accepted
- **Date:** 2026-09-16
- **Decider:** Human (Muskan Jain)
- **Relates to:** [ADR-0014](0014-agents-are-prompts-not-classes.md), which makes each agent a prompt

## Context
Three context facts drive three gates: `implementation.builds` feeds `workspace-builds`,
`test.failures` feeds `tests-pass`, and `test.coverage` feeds `coverage-at-least`.

A model-backed agent can report on its own work, and the cheapest way to obtain those three
facts is to ask it. Doing so would make all three gates check a claim rather than a fact. A
gate reading a self-reported number is not a control; it is a field the graded party fills
in, and that is the difference between a governed system and a demonstration of one. The
lifecycle definition is explicit about it in the testing stage's own description: the gate
"depends on the recorded result of an actual test run, never on an agent's assertion that
the code works."


## Decision
A stage that declares any of those three facts has its claim replaced by a measurement.

**Where it runs.** An agent proposes files; the engine commits them. Verification happens in
between, against a **copy** of the tree with the proposed files overlaid, so a stage can
report a measured fact about a change that does not exist yet, and a change that does not
compile never becomes a commit. `IWorkspaceVerifier` is a port; `DotnetWorkspaceVerifier`
runs `dotnet build` and `dotnet test` and reads the numbers out of the TRX report and the
Cobertura file, not out of console text that changes between SDK releases.

**What triggers it** is the node's own `produces-context` declaration, not the agent's name.
A workflow that adds a build-verified stage gets verification without anyone editing C#, and
a stage declaring none of these facts never pays for a toolchain invocation.

**The model is still asked** for the values, and that is not redundant. Asking makes the
claim explicit and comparable, and the comparison is what turns "the tests failed" into
"the stage reported that the tests passed". Three outcomes:

| | |
|---|---|
| Claim agrees with measurement | The measured value is recorded. The gate judges it. |
| Stage **overstated** its work | The stage fails, naming both figures. Coverage has a 0.10 tolerance, because the model is asked for an estimate and a slightly optimistic estimate is an estimate; a wide miss is a different claim about the work. |
| Verification ran but produced no figure | The stage fails. **It does not fall back to the claim.** |

That last row is the one that matters most and is the easiest to get wrong. A silent fallback
would mean the gate passing on an unverified number while the run's own evidence said
verification was switched on, the original defect reached by a quieter route.

**The seed test in the template** exists for the same reason: a tree whose test run finds
nothing cannot tell "the suite passed" apart from "the suite never ran".

## Alternatives considered
| Option | Why we rejected it |
|---|---|
| Trust the model, rely on review to catch it | Review is downstream of the gate that the number opens. By then the claim has already done its work. |
| Verify after the engine commits | A non-compiling change becomes a commit that compensation then has to revert. Cheap to avoid by checking first. |
| Parse `dotnet test` console output | Formatted for people and changes between SDK releases. A coverage figure scraped from it would drift silently until a gate started passing everything. |
| Run `dotnet test` in the workspace itself | Leaves `bin`, `obj` and a TRX file behind, which the next stage's commit would carry as though an agent had authored them. |
| Only fail when verification contradicts the claim | Says nothing about the case where verification could not produce a figure, which is where the silent fallback lives. |

## Consequences
- Verification is **on by default** for live, record and replay, and **off by default for the
  stub**, the stub emits inert placeholder files, so compiling them proves nothing, and
  requiring an SDK would make the one mode needing no key or network the one mode needing a
  toolchain. `--verify` forces it on anyway; `--no-verify` forces it off, and a run with it
  off says so on its own output.
- The test stage now costs tens of seconds per attempt. That is the price of the number
  meaning something.
- `dotnet test` needs its packages. Build verification works offline; test verification needs
  the NuGet cache warm, which it is after the orchestrator has been built once, the
  template pins the same package versions the orchestrator uses, precisely for this.
- Two bugs surfaced immediately on first use, both recorded here because they are the kind
  that only a real failure finds: `dotnet test` in a directory holding several projects and
  no solution does nothing and exits zero, a silent pass, so the verifier discovers and
  invokes projects itself; and the engine could not invalidate a `Failed` node, so the first
  genuine test failure crashed the run when the loop-back cascaded back onto it.

## Validation
- `DotnetWorkspaceVerifierTests` runs the real SDK against the real template: a compiling
  tree, a proposed file that does not compile, a proposed test that fails, coverage actually
  collected, and the template left untouched afterwards.
- `VerifiedFactTests` covers each outcome above against a fake verifier, including the
  no-figure case and the "honestly predicted a failure" case, which must not be punished.
- End to end: a stubbed run claiming 0.90 coverage against a measured 0.33 fails the test
  stage, loops back to implementation, cascades through the dependent stages, exhausts its
  re-plan budget and fails; 200 events, chain intact.
