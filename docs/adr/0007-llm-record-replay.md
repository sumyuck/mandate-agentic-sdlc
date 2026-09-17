# ADR-0007: LLM access sits behind a port with record/replay, so the submission runs offline

- **Status:** Accepted
- **Date:** 2026-09-16
- **Decider:** Human (Muskan Jain)

## Context
Agent stages call a language model. That introduces three problems for an assessed
deliverable: a reviewer may have no API key, model calls are non-deterministic so the same
run can produce different evidence twice, and a live demo can fail on a network blip or a
rate limit. Non-determinism is also a genuine engineering risk in an AI-assisted SDLC, not
merely a demo inconvenience.

## Decision
All model access goes through an `ILlmClient` port with four implementations:

| Implementation | Purpose |
|---|---|
| `AnthropicLlmClient` | Live calls, used while authoring the recorded runs |
| `RecordingLlmClient` | Decorator that writes every request/response pair to a cassette |
| `ReplayLlmClient` | Serves responses from committed cassettes; fails loudly on a cache miss |
| `StubLlmClient` | Deterministic synthetic answers for unit tests and for walking the lifecycle with neither key nor recording |
| `BudgetedLlmClient` | Decorator that refuses a call which would take the run past its spend ceiling (ADR-0013) |

A cassette is keyed by the **content address of the request**, prompt id, prompt version,
model, system prompt, conversation and output ceiling, hashed with the same canonical
serializer the audit chain uses, and filed at
`cassettes/<prompt>.<version>/<fingerprint>.json`. The default mode is `--llm replay`, which
requires no API key and no network.

> **Amended during implementation.** This ADR first said cassettes would live under
> `runs/<runId>/llm/`. That is wrong, and the reason is worth recording: a recording filed
> under the run that made it can only ever be replayed by that run, and every re-execution
> gets a new run id. Keying by request content instead makes a recording an answer to a
> *question*, not to an *occasion*, so any run that asks the same question gets it, and
> the three scenarios naturally share the recordings they have in common. The original
> layout would have produced a system that recorded everything and could replay nothing.

Two consequences follow from keying on content, and both are enforced in code rather than
documented and hoped for:

- **A prompt must be a pure function of the requirement and of upstream output.** Render a
  run id or a wall-clock timestamp into a prompt and every run asks a question no cassette
  has ever seen. `PromptTemplate.Render` refuses both patterns outright, because the failure
  would otherwise appear as an unexplained replay miss long after the mistake was made.
- **A cassette that does not hash to its own file name is refused.** The value of replay is
  that the answers are the ones the model actually gave; a hand-edited recording would make
  a run's evidence describe a conversation that never happened.

## Alternatives considered
| Option | Why we rejected it |
|---|---|
| Live calls only | Reviewer needs a key and a network; evidence is irreproducible; a rate limit can destroy the demo. |
| Temperature 0 and call it deterministic | Reduces variance, does not remove it, and still requires a key. |
| Mocks only, no live path | Cheap to demo but impossible to defend; "does it actually work with a model?" would have no answer. |

## Consequences
- The whole submission replays end to end with no credentials, no network, and identical
  output every time.
- A cache miss during replay is a hard failure, which keeps the cassettes honest: if a prompt
  changes, the run must be re-recorded rather than silently drifting.
- Cassettes must be scrubbed before commit; prompts contain repository content.
- Every response carries its own `Source`, live, replay or stub, and that label travels
  into the audit log. A stubbed run is not a cheap run: it is a run in which no engineering
  judgment was exercised, and its evidence has to say so.
- Keeping recorded runs in step with evolving prompts is extra upkeep. Accepted for the
  reproducibility it buys.

## Validation
`make demo` with no `ANTHROPIC_API_KEY` set and the network disabled must reproduce all three
scenarios byte-for-byte against the committed evidence.
