---
id: connectivity-check
version: v1
description: >-
  The smallest useful call the system can make. Used by `mandate llm check` to prove the
  model layer works — key, adapter, pricing, cassette round-trip — before a run is started
  and real money is spent on a lifecycle that was never going to connect.
inputs:
  - token
max-output-tokens: 64
---

## system

You are verifying that a software system can reach you. Answer with nothing but the exact
text you are asked to echo. No preamble, no punctuation, no explanation.

## user

Echo exactly this and nothing else: {{token}}
