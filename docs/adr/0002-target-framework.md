# ADR-0002: Target .NET 8 (LTS), with the TFM and SDK pinned centrally

- **Status:** Accepted
- **Date:** 2026-09-16
- **Decider:** Human (Muskan Jain)
- **Supersedes:** an earlier draft of this record that targeted `net9.0` as a concession to
  the build host. The host was fixed instead. Recorded here rather than quietly deleted,
  because the reasoning matters: toolchain convenience is not a valid input to a
  platform-targeting decision.

## Context
.NET 8 is the current LTS release and the version enterprise platforms — particularly in
regulated financial environments, where support lifecycle is a compliance input rather than a
preference — standardise on. The build host initially carried only the .NET 9 SDK and
runtime, and its `dotnet new` templates would not offer `net8.0` as a target.

## Decision
Target **`net8.0`**. Declare it exactly once, as `$(HelmsmanTargetFramework)` in
`Directory.Build.props`; no project declares its own `TargetFramework`. Pin the SDK to
`8.0.425` with `rollForward: latestFeature` in `global.json`, so every build — local and CI —
uses the same LTS SDK band.

Where the host was short a toolchain, we installed it. A .NET 8 SDK was installed into a
dedicated, self-consistent root (`~/.dotnet`) rather than being layered into the existing
Homebrew installation: mixing Microsoft-signed host binaries with Homebrew-built ones in a
single root breaks macOS library validation and the host is killed on launch. Two clean roots
beat one mixed root.

## Alternatives considered
| Option | Why we rejected it |
|---|---|
| Target `net9.0` because that is what the host had | Ships the prototype on a non-LTS runtime and lets tooling dictate a platform decision. The support lifecycle is a real constraint for the target environment; the missing SDK was not. |
| Multi-target `net8.0;net9.0` | Doubles build and test time and adds conditional-compilation surface for no assessment value. Nothing here needs to be library-portable. |
| Per-project `<TargetFramework>` | Invites drift across 12+ projects and turns a retarget into a 12-file change. |
| Layer the .NET 8 SDK into the Homebrew root | Tried; the mixed-signature host is SIGKILLed by macOS library validation. Diagnosed and abandoned in favour of a separate clean root. |

## Consequences
- The prototype runs on a supported LTS runtime, matching the environment it is written for.
- Retargeting is a one-line edit, guarded by `DependencyRuleTests`, which fails the build if
  any project declares its own TFM.
- Local development needs the .NET 8 SDK ahead of a newer one on `PATH`. `make doctor`
  reports the mismatch with the exact fix rather than leaving a confusing restore error.
- CI is unambiguous: `actions/setup-dotnet` reads `global.json`, so the pin is the single
  source of truth for both environments.

## Validation
- `grep -r "<TargetFramework>" src tests services` returns only `Directory.Build.props`
  (asserted by `DependencyRuleTests.No_project_declares_its_own_target_framework`).
- `dotnet --version` inside the repository reports an `8.0.4xx` SDK.
- `BuildInfoTests.Target_framework_is_known` fails if the compiled moniker drifts.
