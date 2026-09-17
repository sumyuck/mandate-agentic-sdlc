#!/usr/bin/env bash
#
# Report whether the toolchain can build this repository, and if it cannot,
# say precisely what to do about it.

set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET="${DOTNET:-dotnet}"
pinned="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$root/global.json" | head -1)"
major="${pinned%%.*}"

printf 'pinned by global.json : %s\n' "$pinned"
printf 'dotnet in use         : %s\n' "$(command -v "$DOTNET" || echo "$DOTNET")"

if "$DOTNET" --list-sdks 2>/dev/null | grep -qE "^${major}\."; then
    printf 'resolved SDK          : %s\n' "$("$DOTNET" --version 2>/dev/null)"
    echo "doctor: OK"
    exit 0
fi

echo
echo "doctor: this dotnet has no .NET ${major} SDK."
printf '        it offers: %s\n' "$("$DOTNET" --list-sdks 2>/dev/null | tr '\n' ' ' | sed 's/  */ /g')"
echo

found="$(bash "$root/scripts/find-dotnet.sh")"
if [ "$found" != "dotnet" ]; then
    echo "        A .NET ${major} SDK is installed elsewhere on this machine:"
    printf '            %s\n' "$found"
    echo
    echo "        The build already prefers it, so plain 'make verify' will work."
    echo "        To use it in your own shell:"
    printf '            export PATH="%s:$PATH"\n' "$(dirname "$found")"
    exit 0
fi

echo "        No .NET ${major} SDK was found in any of the usual locations."
echo "        Install it:"
echo "            curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel ${major}.0"
echo '            export PATH="$HOME/.dotnet:$PATH"'
echo
echo "        See docs/adr/0002-target-framework.md for why the version is pinned."
exit 1
