#!/usr/bin/env bash
#
# Fail if any source file is being hidden from the repository by .gitignore.
#
# This exists because it happened. The pattern `artifacts/`, meant for the .NET SDK's
# build output, also matched `src/Mandate.Core/Artifacts/`, and on a case-insensitive
# filesystem `git add` silently skipped the artifact domain model. Everything built
# locally, because the files were on disk; a clean clone did not compile at all.
#
# CI catches this by failing to build, which is the right backstop but a slow one. This
# catches it on the machine where the files still exist, before the push.

set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root" || exit 1

hidden="$(git status --porcelain --ignored=matching -- src tests 2>/dev/null \
    | awk '/^!! /{print substr($0,4)}' \
    | grep -E '/(bin|obj)/' -v \
    | grep -E '\.(cs|csproj|props|targets)$|/$' \
    || true)"

# Directory entries are reported with a trailing slash; expand them to see if any source
# actually lives inside, since an ignored empty directory is not a problem.
offenders=""
while IFS= read -r path; do
    [ -n "$path" ] || continue
    if [ -d "$path" ]; then
        found="$(find "$path" -name '*.cs' -not -path '*/bin/*' -not -path '*/obj/*' -print -quit)"
        [ -n "$found" ] && offenders="${offenders}${path}"$'\n'
    else
        offenders="${offenders}${path}"$'\n'
    fi
done <<< "$hidden"

if [ -n "${offenders//[$'\n' ]/}" ]; then
    echo "check-sources: these source paths are excluded by .gitignore:"
    printf '%s' "$offenders" | sed 's/^/    /'
    echo
    echo "        They exist on this machine and would be missing from a clean clone."
    echo "        Anchor the offending pattern in .gitignore, or add an exception."
    exit 1
fi

echo "check-sources: OK"
