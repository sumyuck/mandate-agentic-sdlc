# ADR-0002: Target .NET 10 (current LTS), with the TFM and SDK pinned centrally

- **Status:** Accepted
- **Date:** 2026-09-16
- **Decider:** Human (Muskan Jain)

## Context
.NET releases annually each November and alternates support terms: odd-numbered releases are
LTS with three years of support, even-numbered are STS with two. As of September 2026:

| Version | Term | Support ends |
|---|---|---|
| .NET 8 | LTS | **10 November 2026** |
| .NET 9 | STS | already out of support (May 2026) |
| **.NET 10** | **LTS** | **November 2028** |

This system is written for a regulated financial platform. In that setting the support
lifecycle of a runtime is a compliance input, an unsupported runtime is an audit finding,
not an inconvenience. A greenfield prototype that targets a framework leaving support eight
weeks from now would be a defect in the decision, however well the code was written.

## Decision
Target **`net10.0`**, the current LTS. Declare it exactly once, as
`$(MandateTargetFramework)` in `Directory.Build.props`; no project declares its own
`TargetFramework`. Pin the SDK to `10.0.400` with `rollForward: latestFeature` in
`global.json`, so local and CI builds use the same LTS band.

## Alternatives considered
| Option | Why we rejected it |
|---|---|
| `net8.0` | Leaves support on 10 November 2026. Defensible only as "match what the client runs today mid-migration", which is an argument for maintaining existing code, not for starting new code. A reviewer would be right to ask why a new system was started on an expiring runtime. |
| `net9.0` | An STS release that left support in May 2026. Targeting it would start a new system on a runtime that is already an audit finding. |
| Multi-target `net8.0;net10.0` | Doubles build and test time and adds conditional-compilation surface for no assessment value. Nothing here needs to be library-portable. |
| Per-project `<TargetFramework>` | Invites drift across 12+ projects and turns any future retarget into a 12-file change instead of a one-line one. |

## Consequences
- The prototype runs on a runtime supported until November 2028.
- Retargeting stays a one-line edit, guarded by `DependencyRuleTests`, which fails the build
  if any project declares its own TFM.
- `global.json` pins `10.0.400` with `rollForward: latestFeature`, so a standard .NET 10
  install satisfies the repository with no `PATH` manipulation.
- Code may use current-framework APIs: `Sha256Hash` uses `Convert.ToHexStringLower` rather
  than hashing to uppercase hex and lowering it in a second allocation.

## Validation
- `grep -r "<TargetFramework>" src tests services` returns only `Directory.Build.props`
  (asserted by `DependencyRuleTests.No_project_declares_its_own_target_framework`).
- `make doctor` fails with a remediation hint unless the resolved SDK is `10.0.*`.
- `BuildInfoTests.Target_framework_is_known` fails if the compiled moniker drifts.
