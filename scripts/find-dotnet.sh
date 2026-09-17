#!/usr/bin/env bash
#
# Print the path of a dotnet whose SDK set satisfies global.json.
#
# A machine with several .NET installations resolves `dotnet` to whichever
# directory comes first on PATH, which is often not the one holding the SDK
# this repository pins. Rather than telling the reviewer to fix their PATH,
# the build finds the right one.
#
# Falls back to plain `dotnet` so error messages come from the SDK itself.

set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
want="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([0-9]*\)\..*/\1/p' "$root/global.json" | head -1)"
[ -n "$want" ] || want=10

has_sdk() {
    local exe="$1"
    [ -x "$exe" ] || command -v "$exe" >/dev/null 2>&1 || return 1
    "$exe" --list-sdks 2>/dev/null | grep -qE "^${want}\."
}

for candidate in \
    "${DOTNET:-}" \
    "$(command -v dotnet 2>/dev/null || true)" \
    /opt/homebrew/bin/dotnet \
    /usr/local/share/dotnet/dotnet \
    "$HOME/.dotnet/dotnet" \
    /usr/lib/dotnet/dotnet \
    /usr/share/dotnet/dotnet
do
    [ -n "$candidate" ] || continue
    if has_sdk "$candidate"; then
        printf '%s\n' "$candidate"
        exit 0
    fi
done

printf 'dotnet\n'
