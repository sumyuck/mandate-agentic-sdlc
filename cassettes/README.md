# Recorded model exchanges

Every model call this system has made, kept so the runs in [`../runs`](../runs) can be
re-executed by someone with no API key, no network and no budget. That is the position a
reviewer is usually in.

## Layout

```
cassettes/<prompt-id>.<version>/<request-fingerprint>.json
```

The file name is the SHA-256 of the request — prompt id, prompt version, model, system
prompt, conversation and output ceiling, hashed with the same canonical serializer the audit
chain uses. So a recording is an answer to a **question**, not to an occasion: any run that
asks the same question gets it, and the three scenarios share whatever they have in common.

Each file holds the request in full, not just its hash. "What did you actually ask the
model?" is the first question anyone sensible asks of an agentic system, and a file
containing only a hash answers nothing.

## Using them

```bash
mandate llm check
```

Replay is the default mode everywhere. Nothing reaches a provider unless someone typed a
mode that does.

## Rules this directory lives by

- **A miss is a hard failure.** Replay never falls through to a live call. A reviewer who
  set out to reproduce a recorded run would otherwise silently get a different one, billed
  to someone else's account, with nothing in the evidence saying so.
- **An edited recording is refused.** A cassette whose contents do not hash to its own file
  name is rejected on load. The whole value of replay is that the answers are the ones the
  model actually gave.
- **Re-record, don't hand-tune.** If a prompt changes, the old answer is an answer to a
  different question. Run `mandate llm check --llm record` (or the scenario's own record
  command) and commit the new files.

See [ADR-0007](../docs/adr/0007-llm-record-replay.md) for why, and
[ADR-0013](../docs/adr/0013-model-spend-as-a-governed-budget.md) for what a live re-record
is allowed to cost.
