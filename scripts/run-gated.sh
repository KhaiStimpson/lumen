#!/bin/sh
# D4 - build and test output, filtered at the source.
#
# A green test run is one line of information delivered as thousands of lines of
# text, and every one of them stays in context for the rest of the session. This
# wrapper keeps the whole log on disk and prints the part that carries the
# information: pass or fail, the counts, and - only when it failed - the failing
# lines.
#
# Used as the build/test command in a plan's Ground rules:
#   - **Tests:** `scripts/run-gated.sh dotnet test`
#
# Filtering output you needed is worse than paying for output you did not, so
# failure is never summarised into uselessness: the log path is always printed,
# and a failing run shows real lines, not a count.
. "$(dirname "$0")/_common.sh"

[ $# -gt 0 ] || { echo "usage: run-gated.sh <command> [args...]" >&2; exit 64; }

mkdir -p .flow/logs 2>/dev/null
stamp=$(date -u '+%Y%m%dT%H%M%S' 2>/dev/null || echo now)
name=$(printf '%s' "$1" | tr -c 'A-Za-z0-9' '-' | cut -c1-24)
log=".flow/logs/$stamp-$name.log"

"$@" > "$log" 2>&1
code=$?

lines=$(wc -l < "$log" 2>/dev/null | tr -d ' ')

if [ "$code" -eq 0 ]; then
  printf 'PASS  %s  (exit 0, %s lines suppressed)\n' "$*" "${lines:-0}"
  # Whatever the toolchain calls its tally, if it is on one line, it is worth one line.
  grep -i -m1 -E 'passed|[0-9]+ (tests?|assertions?|examples?)|build succeeded' "$log" 2>/dev/null \
    | sed 's/^[[:space:]]*/      /'
  printf '      full log: %s\n' "$log"
  exit 0
fi

printf 'FAIL  %s  (exit %s)\n' "$*" "$code"
printf '      full log: %s  (%s lines)\n' "$log" "${lines:-0}"
printf '      --- failing lines ---\n'

# Prefer the lines that name the failure; fall back to the tail, which is where
# a toolchain that does not label its errors puts them anyway.
hits=$(grep -n -i -E 'error|fail(ed|ure)?|exception|assert' "$log" 2>/dev/null | head -40)
if [ -n "$hits" ]; then
  printf '%s\n' "$hits" | sed 's/^/      /'
  printf '      --- tail ---\n'
  tail -15 "$log" 2>/dev/null | sed 's/^/      /'
else
  tail -40 "$log" 2>/dev/null | sed 's/^/      /'
fi

exit "$code"
