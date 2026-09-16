# ADR-0008: Conditional paths use a closed predicate grammar, not an expression evaluator

- **Status:** Accepted
- **Date:** 2026-09-16
- **Decider:** Human (Muskan Jain)

## Context
The lifecycle is not linear: impact analysis runs only against existing code, ambiguous
requirements divert to a human, a failing test returns to implementation. Those choices are
expressed as guards on edges in `workflows/sdlc.v1.yaml` — for example
`run.has-existing-code == true`.

Guards therefore arrive as **configuration authored outside the engine**. The file is
reviewed and version-controlled, but it is still data being fed to something that must decide
what to do with it. The obvious implementation is to hand the string to an expression library
(NCalc, DynamicExpresso, Jint) and read the boolean back.

## Decision
Implement a purpose-built grammar in `Mandate.Core.Workflow.Guards` that admits exactly:

- references to context keys (`requirements.ambiguity-score`)
- string, number and boolean literals
- the comparisons `== != < <= > >=`
- membership: `in [ ... ]` and `not in [ ... ]`
- the connectives `and`, `or`, `not`, and parentheses

There is no token for a function call, a method, an arithmetic operator, an assignment, a
member access, indexing, interpolation or a statement separator — so those constructs are not
expressible, rather than expressible and then rejected.

Three further constraints fall out of the same reasoning:

1. **A predicate must begin with a context key.** `'a' == 'a'` is refused; it is either
   trivially true or a mistake, and refusing it keeps the language's purpose — routing on run
   state — unambiguous.
2. **Membership lists hold only literals**, so the accepted set is readable in the file.
3. **Evaluation fails closed.** A guard naming a key the context does not hold throws. It does
   not evaluate to false, because a false guard silently skips a lifecycle stage, and a stage
   skipped by a typo is a governance failure rather than a routing decision.

To make the third constraint a load-time error rather than a run-time one, each node declares
the context keys it contributes (`produces-context`). Workflow validation collects every key
referenced by every guard and rejects the workflow if any is neither produced by a node nor
supplied by the engine.

## Alternatives considered
| Option | Why we rejected it |
|---|---|
| A general expression library (NCalc, DynamicExpresso) | Turns a change-controlled configuration file into a code-execution surface. Several of these can reach static methods or the CLR type system, and the ones that can be sandboxed require the sandbox to be exactly right forever. In a regulated financial system "the workflow file can run code" is a finding, not a feature. |
| An embedded scripting engine (Jint, Lua) | Same objection, larger. Also introduces non-determinism and unbounded execution time into a scheduling decision. |
| Equality on a single key only, no grammar | Safe and trivial, but forces the lifecycle's real conditions into node duplication — a separate node per scenario — which is precisely the "simple linear task chaining" the brief rejects. |
| C# delegates registered in code | Safe, but puts the routing logic back in the engine, defeating ADR-0004. A lifecycle change would become a code deployment. |
| Guards evaluate to false on a missing key | Convenient, and wrong. A mistyped key would silently disable a lifecycle stage with no error anywhere. |

## Consequences
- The workflow file cannot execute code, call a method, or reach a type. That is a property of
  the grammar, and it is asserted by tests: `GuardGrammarBoundaryTests` feeds it function
  calls, arithmetic, indexing, interpolation, shell substitution and template syntax, and
  requires every one to fail parsing.
- Guard mistakes surface at load with a caret pointing at the offending character, including
  the most likely one — a single `=` instead of `==`.
- Every evaluation produces a human-readable explanation
  (`run.scenario ('brownfield') == 'brownfield' → true`) which is recorded on the edge's audit
  event, so a branch not taken can still be explained later.
- We own a lexer, a parser and an evaluator: roughly 500 lines including diagnostics. Accepted
  cost, and the alternative's cost was a security property.
- The grammar is deliberately not extensible. Adding an operator is a change to the engine and
  its tests, which is the right friction for something that decides whether a governance stage
  runs.

## Validation
- `GuardGrammarBoundaryTests` must keep every code-reaching construct unparseable.
- `Directory.Packages.props` must contain no expression-evaluation or scripting package.
- A guard reading a key no node declares must fail `mandate workflow validate`, not the run.
