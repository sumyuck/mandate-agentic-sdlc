# Runbook

How to install, run and operate Mandate. Everything here works from a clean clone.

## Requirements

- **.NET 10 SDK**, pinned in [`global.json`](../global.json)
- `git`
- `make`, optional. Every target is a one-line `dotnet` command.

**No API key is required.** Model calls replay from the recordings committed under
[`cassettes/`](../cassettes/). Nothing reaches a provider unless you type a mode that does.

If .NET 10 is not on your PATH:

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
export PATH="$HOME/.dotnet:$PATH"
```

## Five minutes, start to finish

```bash
make doctor      # confirm the toolchain matches global.json
make verify      # build with warnings as errors, run 853 tests, check style
make workflow    # validate the lifecycle and show its parallel structure
make llm         # prove the model layer works, offline, from recordings
make run-model LLM=stub   # walk the whole lifecycle with no key and no network
```

Then read a recorded run:

```bash
dotnet run --project src/Mandate.Cli -- runs list
dotnet run --project src/Mandate.Cli -- runs show run_20260917T004505Z_e2258e
dotnet run --project src/Mandate.Cli -- runs metrics run_20260917T004505Z_e2258e
dotnet run --project src/Mandate.Cli -- audit verify
```

And open the HTML report for the greenfield run, which is self-contained with no script and
no network:

```
runs/run_20260917T004505Z_e2258e/report.html
```

## Running the lifecycle

### The four model modes

Every command that executes stages takes `--llm`:

| Mode | What it does | Needs a key |
|---|---|---|
| `replay` | Answers from committed recordings. **The default.** | No |
| `stub` | Synthetic answers. No model is consulted. | No |
| `record` | Calls the provider and keeps every exchange. | Yes |
| `live` | Calls the provider, keeps nothing. | Yes |

The default is `replay` deliberately. The mode that spends money should be the one somebody
typed.

### Agents

`--agents scripted` (the default) uses deterministic agents that exercise the engine without
a model. They produce genuine content-addressed artifacts with real provenance, contribute
the context facts their nodes declare, and record decisions, so the graph walk, the joins,
the guards, the gates and the audit chain are all doing real work. What is not real is the
engineering judgment inside each stage.

`--agents model` uses the eleven prompt-driven agents.

### A run, end to end

Runs stop at human checkpoints. That is the point, so expect to approve twice.

```bash
# Start. Exits with code 3 at the design approval.
dotnet run --project src/Mandate.Cli -- run "$(cat scenarios/s1-greenfield.txt)" \
    --scenario greenfield --agents model --llm replay --as muskan

# The run id is printed on its own line at the end. Approve the design.
dotnet run --project src/Mandate.Cli -- approve <runId> \
    --role tech-lead --as alex --note "Design is proportionate to the requirement."

# Continue. Exits with code 3 again at the release approval.
dotnet run --project src/Mandate.Cli -- resume <runId> --agents model --llm replay

dotnet run --project src/Mandate.Cli -- approve <runId> \
    --role release-approver --as priya --note "Evidence pack reviewed."

dotnet run --project src/Mandate.Cli -- resume <runId> --agents model --llm replay
```

`--as` names the human. It is recorded on everything that human causes, and the
segregation-of-duties policy checks it: the same person cannot both produce work and
approve it, except at the clarification checkpoint where the question is deliberately
being put back to the requester.

### Exit codes

Scripts need to tell a parked run from a broken one.

| Code | Meaning |
|---|---|
| `0` | The command did what was asked |
| `1` | The work failed, or verification found a defect |
| `2` | Bad input: an unknown id, a missing file, an unusable configuration |
| `3` | **The run is complete as far as it can go and is waiting on a human** |

Code 3 is not an error. A run parked at an approval is the lifecycle working, and a CI job
that could not tell the difference would report the system's central behaviour as a failure.

## Every command

### Executing

```bash
mandate run <request>            # Execute the lifecycle for a requirement
mandate resume <runId>           # Continue a run from its recorded log
mandate stop <runId>             # Halt at the next safe boundary
mandate stop <runId> --clear     # Withdraw the stop request
```

Useful options on `run`:

| Option | Purpose |
|---|---|
| `--scenario` | `greenfield`, `brownfield` or `ambiguous` |
| `--template` | Tree to seed the workspace from |
| `--existing-code` | The target already contains the code under change |
| `--agents` | `scripted` or `model` |
| `--llm` | `replay`, `stub`, `record` or `live` |
| `--budget-usd` | Ceiling on model spend for this run. Defaults to $5 |
| `--verify` / `--no-verify` | Run the real toolchain over proposed changes |
| `--max-concurrency` | How many stages may execute at once. Defaults to 4 |
| `--fail <agent>` | Make an agent fail every attempt, to exercise recovery |
| `--ephemeral` | Do not persist. Nothing to inspect, verify or resume afterwards |

### Human decisions

```bash
mandate approve <runId> --role tech-lead --as alex --note "..."
mandate deny <runId> --role tech-lead --as alex --note "Blast radius is too large."
mandate amend <runId> --stage requirements --as muskan --reason "Expiry means a TTL."
mandate waive <runId> --rule CHG-003 --as alex --reason "Covered by the manual test plan."
mandate waive <runId> --rule CHG-003 --withdraw
```

`amend` is how you tell a completed run that one of its inputs has changed. The engine
invalidates everything downstream that depended on it, revokes the approvals granted
against the invalidated work, and re-plans.

### Inspecting

```bash
mandate runs list                       # Recorded runs, most recent first
mandate runs list --ids                 # Just ids, for scripting
mandate runs show <runId>               # Rebuild from the log and show where it stands
mandate runs show <runId> --events      # The full audit log
mandate runs metrics <runId>            # Reliability figures, derived from the log
mandate runs metrics <runId> --json     # The same, machine readable
mandate runs report <runId>             # Self-contained HTML
mandate runs export <runId>             # events.jsonl, run.json, timeline.md
```

### Verifying

```bash
mandate audit verify                    # Every recorded run
mandate audit verify <runId>            # One run
mandate policy list                     # The rules this system asserts it obeys
mandate policy list --category security
mandate policy check <runId>            # Evaluate every pack against a run
mandate workflow validate               # Check the lifecycle can be loaded and executed
mandate workflow render --markdown      # Mermaid diagram
mandate llm prompts                     # The versioned prompts, with fingerprints
mandate llm check                       # Prove the model layer works
mandate info --json                     # Engine identity and host diagnostics
```

## Using a real model

Only needed if you want to record new runs. The committed recordings cover everything
described in the scenario write-ups.

```bash
export ANTHROPIC_API_KEY=sk-ant-...
dotnet run --project src/Mandate.Cli -- llm check --llm live
```

On zsh, put the export in `~/.zshenv`, not `~/.zshrc`. Non-interactive shells do not read
`.zshrc`, so a key set there is invisible to anything a script launches.

Then run with `--llm record` to keep the exchanges:

```bash
dotnet run --project src/Mandate.Cli -- run "..." --agents model --llm record --budget-usd 2
```

**Spend control.** `--budget-usd` is enforced by refusing the call that would breach it,
not by reporting the overrun afterwards. The check uses the call's worst case (its input
plus the full output ceiling the prompt declares), so it is deliberately conservative and
will occasionally refuse a call that would have fitted. That is the right direction for a
ceiling to err in.

To measure what a set of recordings actually cost:

```bash
python3 - <<'PY'
import json, glob
price = {"claude-sonnet-5": (2.0, 10.0), "claude-haiku-4-5": (1.0, 5.0),
         "claude-haiku-4-5-20251001": (1.0, 5.0)}
total = 0.0
for f in glob.glob("cassettes/*/*.json"):
    r = json.load(open(f))["response"]; u = r["usage"]
    pin, pout = price.get(r["model"], (0, 0))
    total += u["inputTokens"] * pin / 1e6 + u["outputTokens"] * pout / 1e6
print(f"${total:.2f}")
PY
```

One trap worth knowing: that tally counts recordings **currently on disk**. Deleting stale
ones erases money already spent from the count, so it under-reports after any cleanup.

## Operating

### A run is parked and you want to see why

```bash
mandate runs show <runId>
```

The stage table gives each node's state and the reason. For the full picture including
every gate evaluation:

```bash
mandate runs show <runId> --events
```

### A run failed and you want the evidence

```bash
mandate runs report <runId> -o /tmp/run.html
```

The HTML report has the timeline, the stage outcomes, the gate results, the decisions with
their rejected options, the artifacts with provenance, and the metrics. It is
self-contained: no network, no script, no CDN.

### You need to stop a long run

```bash
mandate stop <runId>
```

Cooperative, not a kill. The engine halts at the next node boundary with state preserved,
and in-flight work finishes rather than being interrupted mid-commit. `mandate resume`
picks it up.

### A policy is blocking something you have decided to accept

```bash
mandate policy check <runId>          # See exactly which rule and why
mandate waive <runId> --rule SEC-002 --as alex --reason "..."
mandate resume <runId>
```

The waiver is an event. It overrides the block and does not remove the finding, which stays
in the log and in the report.

### The requirement changed after the run acted on it

```bash
mandate amend <runId> --stage requirements --as muskan --reason "Expiry means a TTL."
mandate resume <runId>
```

Expect approvals to be revoked. That is the point: a tech lead who approved one design has
not approved a different one.

### Verifying nobody tampered with the record

```bash
mandate audit verify
```

Each event commits to its predecessor, so any edit, deletion or reordering is detected. The
SQLite store also refuses UPDATE and DELETE at the database level through triggers, so you
cannot quietly edit history even with direct access to the file.

## Where things live

```
workflows/sdlc.v1.yaml       The lifecycle. This is the plan.
workflows/policies/          Security, compliance and change-control rules.
prompts/                     One versioned file per agent.
config/model-pricing.yaml    Dated prices, with the source they came from.
cassettes/                   Recorded model exchanges, keyed by request content.
scenarios/                   The three requirements, as given to the system.
runs/                        Committed evidence: events, timeline, HTML report.
services/url-shortener/      What the system built.
templates/                   Trees a run is seeded from.
.mandate/                    Local run state. Machine-local, not committed.
```

## Make targets

```bash
make help        # List targets
make doctor      # Check the toolchain matches global.json
make verify      # Toolchain, build, tests, style. The full local gate
make build       # Build with warnings as errors
make test        # All tests
make lint        # Style check without modifying files
make format      # Apply style fixes
make info        # Engine identity
make workflow    # Validate the lifecycle
make diagram     # Regenerate the Mermaid diagram from the definition
make llm         # Prove the model layer (LLM=replay|stub|record|live)
make prompts     # List the prompt library with fingerprints
make run         # Run with scripted agents
make run-model   # Run with model agents (LLM=stub by default)
make runs        # List recorded runs
make audit       # Verify every recorded run
make policy      # List the policy rules
make metrics RUN=<runId>
make report RUN=<runId>
make clean       # Remove build output and local run state
```
