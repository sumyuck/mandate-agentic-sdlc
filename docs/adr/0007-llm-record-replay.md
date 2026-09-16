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
| `StubLlmClient` | Deterministic canned responses for unit tests |

Cassettes for all three scenarios are committed under `runs/<runId>/llm/`. The default
demo path is `--llm replay`, which requires no API key and no network.

## Alternatives considered
| Option | Why we rejected it |
|---|---|
| Live calls only | Reviewer needs a key and a network; evidence is irreproducible; a rate limit can destroy the demo. |
| Temperature 0 and call it deterministic | Reduces variance, does not remove it, and still requires a key. |
| Mocks only, no live path | Cheap to demo but impossible to defend — "does it actually work with a model?" would have no answer. |

## Consequences
- The whole submission replays end to end with no credentials, no network, and identical
  output every time.
- A cache miss during replay is a hard failure, which keeps the cassettes honest: if a prompt
  changes, the run must be re-recorded rather than silently drifting.
- Cassettes must be scrubbed before commit; prompts contain repository content.
- Keeping recorded runs in step with evolving prompts is extra upkeep. Accepted for the
  reproducibility it buys.

## Validation
`make demo` with no `ANTHROPIC_API_KEY` set and the network disabled must reproduce all three
scenarios byte-for-byte against the committed evidence.
