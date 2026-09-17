# Testing approach, limitations and trade-offs

## The short version

867 tests across 9 projects, all passing, zero warnings with warnings treated as errors.
Run them with `make test`, or the full gate including style with `make verify`.

| Project | Tests | Covers |
|---|---:|---|
| `Mandate.Core.Tests` | 256 | Domain: state machine, audit chain, provenance, decisions, guards, graph validation |
| `Mandate.Orchestrator.Tests` | 149 | Engine: scheduling, joins, gates, retries, compensation, re-planning, architecture rules |
| `Mandate.Cli.Tests` | 91 | Every command the binary exposes, end to end |
| `Mandate.Workflows.Tests` | 81 | YAML loading and the 34 validation codes |
| `Mandate.Persistence.Tests` | 85 | SQLite store, git workspace, toolchain verification, workspace reader, evidence round trip |
| `Mandate.Llm.Tests` | 67 | Prompt library, cassettes, budget, pricing, request fingerprints |
| `Mandate.Agents.Tests` | 64 | Response parsing, contract enforcement, verified facts |
| `Mandate.Policy.Tests` | 44 | Policy rules, waivers, segregation of duties |
| `Mandate.Observability.Tests` | 31 | Metrics derivation, HTML report, timeline |

## What is actually being tested

### Tests that would catch a real regression

The suite is written against behaviour a reviewer would care about, not against method
signatures. Some examples of what is pinned:

**The governance model itself.** `NodeStateMachineTests` asserts the complete transition
table, including the edges that must *not* exist. `Succeeded` cannot become `Cancelled`,
because a stop must not relabel a settled outcome. `Ready` cannot become
`AwaitingApproval`, because that would create a path to success without running.

**Architecture decisions, as executable rules.**
[`DependencyRuleTests`](../tests/Mandate.Orchestrator.Tests/Architecture/DependencyRuleTests.cs)
fails the build if the engine gains a dependency beyond the domain, if an adapter depends
on the engine, if a target framework is declared outside the central file, or if a second
project starts referencing the model vendor's SDK. An architecture decision that is only
written down decays.

**The committed evidence, imported and verified.**
[`RunEvidenceReaderTests`](../tests/Mandate.Persistence.Tests/RunEvidenceReaderTests.cs)
reads the three scenario runs out of `runs/`, loads them into a store and asserts every
chain verifies. The submission's central claim is that a reviewer can check the record
themselves, so that claim is pinned by a test rather than having been checked once by
hand. A companion test edits an exported payload, leaves its recorded digest alone, and
asserts the import then fails verification: if import recomputed digests, every imported
log would verify by construction and the check would be theatre.

**The request fingerprint, pinned to a literal.** If the canonical serializer, property
order or record shape changes, every cassette ever recorded stops matching. One test
holds the hash to a constant, and it is the only thing that would say so before a demo did.

**Verification against the real toolchain.**
[`DotnetWorkspaceVerifierTests`](../tests/Mandate.Persistence.Tests/DotnetWorkspaceVerifierTests.cs)
runs the actual .NET SDK against the actual template: a compiling tree, a proposed file
that does not compile, a proposed test that fails, coverage genuinely collected, and the
template left untouched afterwards. A verifier tested against a canned TRX file proves
that the test author can write TRX, not that the toolchain produces it.

**Reproducibility, tested as a property.** Verify the same tree twice and assert the two
reports are byte-identical. Toolchain output reaches a retry's prompt, so a prompt that
differed between two identical runs could never be replayed from a recording. The property
is asserted rather than assumed.

**Every refusal, separately.** The agent's contract enforcement has a test per refusal:
a declared output missing, an undeclared output present, a missing context fact, an
undeclared context fact, a path outside the workspace, a decision with one option, a
truncated answer, an empty answer, a document with no content block.

### Tests derived from live execution

A system like this is only as good as what happens when it meets reality, so a substantial
part of the suite was derived by running it against real models and real toolchains and
pinning every edge that surfaced. These are the cases a purely unit-tested orchestrator
would never discover:

| Test | The condition it pins |
|---|---|
| `A_revert_that_conflicts_with_later_work_reports_it_and_leaves_a_clean_tree` | A parallel stage touched the same file, so no clean revert exists. Compensation reports failure and leaves the tree clean rather than half-reverted |
| `Returning_a_file_unchanged_is_a_no_op_not_a_self_derived_artifact` | Content-addressing makes an unchanged file the same artifact as its input. Provenance drops the self-reference rather than rejecting the stage |
| `Two_identical_verifications_report_identical_text` | Toolchain output reaches a retry's prompt, so it must be free of timings and paths that vary between runs |
| `A_retry_is_told_what_the_previous_attempt_got_wrong` | A retry carries the previous failure, so a deterministic error can be corrected rather than repeated |
| `A_raw_newline_inside_a_string_is_repaired_rather_than_rejected` | A raw control character inside a JSON string is always invalid, so repairing it can only rescue a broken document |
| `A_symlink_pointing_out_of_the_tree_is_refused` | Path normalisation does not follow links, so containment is re-checked against the resolved target |
| `A_file_that_is_not_valid_text_is_not_read` | Byte-order-mark sniffing would let a binary choose its own decoding and reach a prompt as plausible nonsense |
| `Any_settled_verdict_can_be_invalidated_when_its_inputs_change` | A failure is a verdict about particular inputs; when those change it is as stale as a success |

### Tests that prove the system is honest

The point of several tests is that the system cannot flatter itself:

- A stage that overstates its coverage fails, and a stage that honestly predicts a failure
  is recorded rather than punished. Those are different things and the tests keep them so.
- A verifier that runs but produces no figure fails the stage rather than falling back to
  the claim.
- Every stubbed document declares that no model produced it, and the assertion is a test.
- A cassette that does not hash to its own file name is refused.

## The test pyramid, and where it is deliberately not a pyramid

Most of the suite is fast unit tests over pure domain logic, which is where it belongs:
256 tests in `Mandate.Core.Tests` run in under half a second.

Three areas are deliberately slower and heavier:

**Git.** `Mandate.Persistence.Tests` runs real `git init`, `commit` and `revert` against
real temporary directories. Mocking git would test a model of git, and the interesting
behaviour (a conflicting revert) is exactly where a mock would differ from reality.

**The .NET toolchain.** The verifier tests invoke `dotnet build` and `dotnet test`, which
takes about 16 seconds. That is the price of knowing the TRX and Cobertura parsing works.

**The CLI.** `Mandate.Cli.Tests` drives the same command definitions the binary exposes,
through the real Spectre command app, against real SQLite stores and real git workspaces.
A harness that configured its own commands would verify a CLI nobody ships.

## Scope boundaries

Every system has an edge. These are where this one's sit, and why each was drawn there.

**Model judgment is held to a contract, not certified.** No test asserts that a model
produces a wise design, because that is not a testable proposition. What is tested, and
heavily, is that the system holds a model to its declared outputs, measures its claims
against the real toolchain, refuses anything it cannot verify, and records what it did.
Judging whether a design is *good* stays with the named human at the approval gate, which
is where the brief puts it.

**A single process owns a run.** Runs are resumable across process boundaries because
state lives entirely in the event log, but two engines executing the same run concurrently
is outside the model. The SQLite store would serialise the writes; the run state would
diverge. Distributed execution would need a lease on the run id, which is a small change
to the journal port and no change to the engine.

**Concurrency is between stages, not within them.** Four stages run in parallel behind a
synchronising barrier. A single stage is one model call, which is why the implementation
prompt asks for compact output and declares a low reasoning effort. Splitting a stage
across parallel agents is a natural extension and the scheduler already supports the
shape.

**The workspace view is bounded.** A prompt carries at most 200,000 characters of the
tree. Beyond that, files are withheld and the stage is told explicitly which ones, so it
reports that it could not assess them rather than guessing. On a repository substantially
larger than the fixture the answer is retrieval rather than a larger ceiling.

**One lifecycle ships.** The loader and validator are general and carry 81 tests across 34
validation rules, and the engine holds no knowledge of this particular lifecycle. A second
workflow is a YAML file, not a code change.

**Fixture performance is out of scope.** No load or soak testing. The URL shortener exists
to give the orchestrator something real to build, and its throughput is not what the brief
assesses.

**Provider behaviour is mapped, not simulated exhaustively.** Rate limits, 5xx responses
and connection failures are classified as transient and tested through a fake client.
Novel provider behaviour beyond those classes is handled by the same retry and fallback
machinery as any other stage failure.

## Trade-offs

Decisions where a reasonable engineer could have gone the other way.

**JSON envelope with content outside it.** Content in JSON would be one format instead of
two. It was tried, and three separate stages died on escaping: a raw newline in twenty
kilobytes of C#, an unescaped quote 2.4 kB into a design document, and the backslash
hazard in every regular expression. The cost of two formats is a slightly more complex
parser. The benefit is that the format most likely to carry awkward characters has no
escaping at all.

**Prompts as files rather than code.** Files cannot be type-checked or refactored by a
tool. In exchange, a prompt change is reviewable as a diff by someone who does not read
C#, and it can be versioned independently of the assembly. For a system whose behaviour is
mostly determined by its instructions, that is the right way round.

**One agent class instead of eleven.** Eleven classes would allow per-agent behaviour in
code. They would also be eleven places for retry semantics, provenance wiring and cost
accounting to drift. The difference between agents is genuinely their instructions, so the
difference lives where instructions live.

**Verification before commit rather than after.** Verifying after would be simpler: commit,
then check. It would also mean a non-compiling change becomes a commit that compensation
then has to revert, and compensation can conflict. Checking first costs a tree copy per
verified stage.

**A closed guard grammar instead of an expression evaluator.** An evaluator would be more
expressive and would turn the workflow file into a code-execution surface wearing a
configuration file's clothes. The grammar supports comparison and boolean composition and
fails closed on anything else. See [ADR-0008](adr/0008-restricted-guard-grammar.md).

**Metrics derived rather than stored.** Deriving them means recomputing on every request,
which for runs of this size is microseconds. Storing them would be faster and would mean a
number in a report could be set rather than caused.

**SQLite rather than Postgres.** Single-file, no server, trivially inspectable by a
reviewer with `sqlite3`. It also rules out multi-writer scenarios, which the engine does
not support anyway.

**The fixture is deliberately modest.** The URL shortener uses SQLite and an in-process
cache rather than a distributed stack. None of the brief's eight core requirements
concerns the fixture's infrastructure; all eight concern the system that produces it. A
richer fixture would have consumed effort that belongs in the orchestrator, and would have
made the brownfield scenario slower to run without making it more revealing.

## Continuous integration

The suite is only meaningful if it runs somewhere other than the machine it was written
on. [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) runs on every push and pull
request, and can be started by hand from the Actions tab.

Three jobs, each beginning from a clean clone:

- **`build / test / style`** restores, builds with warnings as errors, runs all 867 tests
  and checks formatting. Because the architecture rules are tests, this job also enforces
  ADR-0001 through ADR-0003.
- **`offline demo`** runs `make demo`: the lifecycle validator, the model layer in replay,
  all eleven stages against stub agents, then an import of the three committed runs and a
  verification of their audit chains. The job sets no `ANTHROPIC_API_KEY`, which makes
  "this runs offline" a property the pipeline enforces rather than a claim in a document.
- **`generated service`** builds and tests the URL shortener on its own, outside the
  orchestrator, for the reason given above: a result the system grades itself on is not
  evidence.

Pushes that touch only prose skip the pipeline. The exclusion list names the documentation
paths one by one instead of matching `**.md`, because not every markdown file here is
documentation: the agent prompts are `prompts/*.prompt.md`, and `templates/**/*.md` is part
of the workspace tree every stage reads. Editing either changes a prompt fingerprint and
invalidates every cassette, which would break the offline demo, so exactly the changes most
likely to break the pipeline are the ones that must keep triggering it.

**What CI caught that local testing could not.** The first run on the public repository
failed to compile. A `.gitignore` pattern written for the .NET SDK's build output,
`artifacts/`, also matched `src/Mandate.Core/Artifacts/`, and on a case-insensitive
filesystem `git add` skipped the directory without reporting anything. Every local build
and all 867 tests passed, because the files were on disk; a clean clone had no artifact
domain model and no test for it.

The fix anchored the pattern, and
[`scripts/check-sources-tracked.sh`](../scripts/check-sources-tracked.sh) now fails
`make verify` if any source file is being hidden from the repository, so the same class of
defect is caught on the machine where it is still cheap to fix. Writing that check
immediately found a second latent instance: `[Rr]elease/` would have swallowed any future
`src/.../Release/` directory the same way.

## Running the tests

```bash
make test                                  # everything
make verify                                # toolchain, build, tests, style

dotnet test tests/Mandate.Core.Tests       # one project
dotnet test --filter 'FullyQualifiedName~NodeStateMachine'
dotnet test --filter 'Category!=Toolchain' # skip the slow SDK-invoking tests
```

Test names are sentences, so the runner output reads as a specification:

```
Any_settled_verdict_can_be_invalidated_when_its_inputs_change
A_retry_is_told_what_the_previous_attempt_got_wrong
Claiming_no_failures_when_tests_failed_fails_the_stage
Failures_the_stage_honestly_predicted_are_recorded_not_punished
```

`CA1707`, which forbids underscores in identifiers, is disabled for test projects only and
never for `src/`.
