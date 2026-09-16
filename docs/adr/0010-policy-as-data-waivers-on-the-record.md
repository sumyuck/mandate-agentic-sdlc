# ADR-0010: Policy is declarative data, and a waiver overrides without silencing

- **Status:** Accepted
- **Date:** 2026-09-16
- **Decider:** Human (Muskan Jain)

## Context
The brief requires guardrails for security, compliance and change control. Gates already check
whether a *stage's own output* is acceptable. Policy answers a different question: whether the
*run as a whole* is still within the rules it is obliged to obey — and the second is not the
sum of the first. A run can have every gate pass and still have no approval on file, no test
executed, or a secret committed in a file nobody described.

The hard part is not the checking. It is what happens when a rule is violated and someone
needs to ship anyway.

## Decision
**Policy packs are versioned YAML** in `workflows/policies/`, loaded strictly. Each rule states
what must hold, *why it matters*, its category, its severity, and the check that evaluates it.
The rationale is mandatory: whoever decides whether to override a rule needs to know what they
are overriding.

**Checks are evidence-based and fail closed**, exactly as gate conditions are. A rule whose
evidence is missing is a violation, not a pass. A rule whose *check* is not registered is also
a violation — a control nobody can evaluate is not a control that passed.

**A waiver overrides without silencing.** A human can allow a blocking violation through, but:

- the rule is still evaluated, and still reported as violated;
- the waiver requires a named human and a stated reason;
- the waiver is recorded as an event in the run's hash-chained log;
- only *blocking* rules can be waived, and only rules that exist.

**The engine evaluates and records; the gate only judges.** Policy results are computed by the
engine and handed to the gate evaluator, so the verdict a gate acts on and the evidence written
to the log come from the same evaluation.

## Alternatives considered
| Option | Why we rejected it |
|---|---|
| Policy rules as code | Fast to write, and it makes "what rules do we enforce?" a question only a developer can answer. A control framework a compliance reviewer cannot read is not a control framework they can sign off. |
| A general policy language (OPA/Rego, CEL) | Genuinely good tools, and both re-introduce exactly the objection from ADR-0008: an expression language turns a change-controlled file into an execution surface. The closed set of named checks keeps the file declarative and the evaluation auditable. |
| A waiver that suppresses the check | The obvious implementation, and it destroys the thing the audit trail exists for. An auditor's question is not "were there violations?" but "what did we knowingly let through, and who said so?" A suppressed check cannot answer it. |
| Waivers configured in the pack | Then the override is a property of the policy rather than of a decision someone made about a specific run, and it applies silently forever. |
| Let the gate evaluator call the policy engine | The engine has to record the evaluation as an audit event regardless. If the evaluator triggered its own, the gate's verdict and the recorded evidence could come from two different evaluations of a moving target. |
| Require policy packs for every run | A workflow with no policy gate should not be blocked by the absence of packs it never consults. Packs are required only when the lifecycle actually gates on one. |

## Consequences
- "What rules does this system enforce?" is answered by three readable files, and
  `mandate policy list` prints them.
- A blocking violation stops the run at a gate, with the rule id, the statement and the
  evidence in the failure message.
- Overriding is possible, attributable and permanent in the record. `mandate waive` refuses a
  rule nobody declared, an advisory rule that does not block, and any waiver with no reason.
- **Defence in depth falls out of the separation.** Waiving `CHG-001` (every required approval
  is held) does *not* release the stage, because the stage's own exit gate independently
  requires the signature. One override does not collapse two controls — asserted by a test.
- Ten rules is a small pack. It is deliberately a starting set of rules that can actually be
  evaluated from recorded evidence, rather than a longer list of aspirations.
- The secret scanner is pattern-based and will not find a secret that does not look like one.
  The rule's own rationale says so: a net with a known mesh size, not a proof.

## Validation
- Every check the shipped packs name must be registered, asserted by a test — so the packs and
  the checks cannot drift into a pack that reports violations for want of an evaluator.
- A pack that was not evaluated must fail the gate closed, never pass it.
- A waived violation must still appear as violated in the evaluation and in the log.
