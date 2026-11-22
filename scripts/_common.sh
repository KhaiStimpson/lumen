#!/bin/sh
# Shared helpers for flow hooks. POSIX sh only - these run under Git Bash on Windows.

# The live effort slug, or empty. Every hook exits silently when this is empty,
# so a repo with no flow effort pays nothing for having the plugin installed.
effort_slug() {
  [ -f .flow/current ] || return 1
  tr -d ' \t\r\n' < .flow/current
}

plan_file() {
  _slug="$1"
  [ -n "$_slug" ] && [ -f "docs/$_slug-plan.md" ] && { printf 'docs/%s-plan.md' "$_slug"; return 0; }
  ls docs/*-plan.md 2>/dev/null | head -1
}

# The "## Phase ..." heading that the first unchecked task sits under, or empty
# when every box is ticked. This is what makes a phase boundary detectable: the
# heading changes exactly once per phase, at the moment the last task under it
# is ticked.
current_phase() {
  [ -f "$1" ] || return 1
  awk '
    /^## Phase/                 { h = $0 }
    /^[[:space:]]*- \[ \]/      { if (h != "") print h; exit }
  ' "$1"
}

plan_complete() {
  [ -f "$1" ] || return 1
  ! grep -q -- '- \[ \]' "$1"
}

# The phase this session started on. Written once when the loop begins, compared
# on every check. A session owns exactly one phase.
session_phase() {
  [ -f .flow/phase ] || return 1
  tr -d '\r\n' < .flow/phase
}

set_session_phase() {
  [ -d .flow ] || mkdir -p .flow
  printf '%s' "$1" > .flow/phase
}

# ---------------------------------------------------------------------------
# Transcript location, for the context backstop.
#
# Verified 2026-08-26 against a running session: the live transcript is the most
# recently modified *.jsonl in the project directory that Claude Code derives
# from the cwd by replacing ':' and '/' with '-'. A hook payload's
# transcript_path is preferred when one is supplied, but nothing depends on it.
# ---------------------------------------------------------------------------
transcript_file() {
  if [ -n "$1" ] && [ -f "$1" ]; then printf '%s' "$1"; return 0; fi
  _p=$(pwd -W 2>/dev/null || pwd)
  _enc=$(printf '%s' "$_p" | sed 's#[:/\\]#-#g')
  _dir="$HOME/.claude/projects/$_enc"
  [ -d "$_dir" ] || return 1
  ls -t "$_dir"/*.jsonl 2>/dev/null | head -1
}

# Live context = input + cache_read + cache_creation from the last assistant
# usage record. Read from the tail only; these files reach 20 MB and a hook that
# scans the whole thing on every turn is its own tax.
#
# The '"' before input_tokens is load-bearing: it stops the pattern matching
# inside "cache_read_input_tokens", where the preceding character is '_'.
live_context() {
  [ -f "$1" ] || return 1
  _line=$(tail -c 2000000 "$1" 2>/dev/null | grep '"cache_read_input_tokens"' | tail -1)
  [ -n "$_line" ] || return 1
  _i=$(printf '%s' "$_line" | grep -o '"input_tokens":[0-9]*'                | tail -1 | cut -d: -f2)
  _r=$(printf '%s' "$_line" | grep -o '"cache_read_input_tokens":[0-9]*'     | tail -1 | cut -d: -f2)
  _c=$(printf '%s' "$_line" | grep -o '"cache_creation_input_tokens":[0-9]*' | tail -1 | cut -d: -f2)
  echo $(( ${_i:-0} + ${_r:-0} + ${_c:-0} ))
}

# ---------------------------------------------------------------------------
# The context budget, shared by context-budget.sh (the hook backstop) and
# phase-boundary.sh (the path the loop prompt actually walks). Both need the
# same number, and a threshold that lives in two files drifts.
#
# Default 250K, which is where the measured cost knee is. The earlier 400K sat
# ABOVE the knee, so by the time it fired the expensive turns were already paid
# for - and above a typical `/autocompact 300k`, so it could never fire at all.
# ---------------------------------------------------------------------------
context_limit() {
  _l=$(ground_rule "$1" 'Context backstop')
  case "$_l" in
    ''|*[!0-9]*) _l=250000 ;;
  esac
  printf '%s' "$_l"
}

# Echoes "<ctx> <limit>" and returns 0 when context is at or over budget.
# Returns 1 when it is under, or when the transcript cannot be read - this is
# advisory, and it must never be an obstacle when it cannot see.
context_over_budget() {
  _lim=$(context_limit "$1")
  _t=$(transcript_file "$CLAUDE_TRANSCRIPT_PATH") || return 1
  [ -n "$_t" ] || return 1
  _ctx=$(live_context "$_t") || return 1
  [ -n "$_ctx" ] || return 1
  [ "$_ctx" -lt "$_lim" ] && return 1
  printf '%s %s' "$_ctx" "$_lim"
}

# A "- **Key:** value" line from the plan's Ground rules, or empty.
ground_rule() {
  [ -f "$1" ] || return 1
  grep -m1 -i -- "- \*\*$2:\*\*" "$1" 2>/dev/null \
    | sed 's/.*\*\*[^*]*\*\*[[:space:]]*//' | tr -d '\r' | sed 's/[[:space:]]*$//'
}

# Round a token count to something a human reads at a glance.
fmt_k() {
  echo $(( $1 / 1000 ))K
}
