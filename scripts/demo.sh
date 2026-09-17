#!/usr/bin/env bash
#
# A guided tour of Mandate for a reviewer with five minutes and no API key.
#
# Everything here runs offline. No model is called, nothing is sent anywhere,
# and no state outside this repository is touched.
#
# Usage:  make demo          (or:  bash scripts/demo.sh)

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOTNET="${DOTNET:-$(bash "$HERE/find-dotnet.sh")}"
CLI=("$DOTNET" run --project src/Mandate.Cli --no-build --)
S1="run_20260917T004505Z_e2258e"

step=0
say() {
    step=$((step + 1))
    printf '\n\033[1;34m%s\033[0m\n' "── $step. $1"
    [ $# -gt 1 ] && printf '   \033[2m%s\033[0m\n' "$2"
    printf '\n'
}

note() { printf '   \033[2m%s\033[0m\n' "$1"; }

# A run that parks for a human exits 3. That is the lifecycle working, not a
# failure, so the demo accepts it.
parked_ok() { local rc=0; "$@" || rc=$?; [ "$rc" -eq 0 ] || [ "$rc" -eq 3 ]; }

printf '\n\033[1m%s\033[0m\n' "Mandate: a governed agentic software engineering system"
note "Offline. No API key required. Ctrl-C at any point."

say "The toolchain" "The SDK has to match what global.json pins."
DOTNET="$DOTNET" bash "$HERE/doctor.sh"

say "Building" "Warnings are errors. This is the only slow step."
"$DOTNET" build --nologo --verbosity quiet
note "built"

say "The lifecycle is data, not code" \
    "Eleven stages in workflows/sdlc.v1.yaml, validated against 34 rules."
"${CLI[@]}" workflow validate

say "The model layer, proved offline" \
    "Exchanges replay from recordings committed under cassettes/."
"${CLI[@]}" llm check --llm replay

say "Walking the whole lifecycle with no key and no network" \
    "Synthetic answers. The engine, the gates and the approvals are all real."
parked_ok "${CLI[@]}" run "Build a URL shortener with create and redirect APIs" \
    --scenario greenfield --agents model --llm stub
note "Exit code 3 means the run parked for a human. That is the design."

say "Loading the three recorded scenario runs" \
    "Their evidence is committed under runs/. This puts it in the local store."
"${CLI[@]}" runs import runs

say "The runs, now inspectable" \
    "Greenfield, brownfield and ambiguous."
"${CLI[@]}" runs list

say "Proving no run log has been altered" \
    "Recomputes the SHA-256 chain over every event of every run."
"${CLI[@]}" audit verify

say "Reliability figures for the greenfield run" \
    "Derived from the event log. Nothing is stored, so nothing can be set."
"${CLI[@]}" runs metrics "$S1"

say "The policy rules evaluated on every run"
"${CLI[@]}" policy list

printf '\n\033[1;32m%s\033[0m\n\n' "Done."
cat <<'NEXT'
   Where to look next:

   runs/run_20260917T004505Z_e2258e/report.html    the greenfield run as one
                                                   self-contained page: timeline,
                                                   gate verdicts, approvals,
                                                   decisions and provenance

   docs/ARCHITECTURE.md                            how it is built and why
   docs/TRACEABILITY.md                            every requirement mapped to
                                                   code, test and evidence
   docs/scenarios/                                 the three runs in detail

   To see a control fire on demand, inject a failure:

   make run FAIL=test-engineer

NEXT
