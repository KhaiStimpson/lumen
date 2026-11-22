#!/bin/sh
# D2 - the phase boundary ends the session.
#
# A session owns one phase. When the first unchecked task in the plan moves into
# a LATER phase than the one this session started on, that phase is done and the
# session's useful life is over: everything it learned that matters is already in
# the plan and the handoff, and everything else is transcript nobody should pay
# to re-read.
#
# It never edits the plan and never stops anything itself. The loop prompt reads
# the directive and acts on it, which is the load-bearing path.
#
# HOW IT TALKS BACK. Wired as a `Stop` hook, stdout on exit 0 goes to the
# transcript and the model never sees it - which made the belt-and-braces path
# inert. Directives therefore go to STDERR with exit 2, which is the contract
# that blocks the stop and feeds the text back as an instruction. Every exit-2
# path is guarded by a marker file so the second pass falls through and the
# session can actually end.
. "$(dirname "$0")/_common.sh"

slug=$(effort_slug) || exit 0
[ -n "$slug" ] || exit 0

plan=$(plan_file "$slug")
[ -n "$plan" ] && [ -f "$plan" ] || exit 0

# Every box ticked: the effort is over, not the phase. Different ending, and it
# must not spawn a successor that would have nothing to do.
if plan_complete "$plan"; then
  [ -f .flow/wrap-pending ] && exit 0
  : > .flow/wrap-pending
  {
    printf 'flow: every task in %s is ticked. The effort is done.\n' "$plan"
    printf '  Do NOT spawn a successor. Stop the loop and run /flow:wrap.\n'
  } >&2
  exit 2
fi

cur=$(current_phase "$plan")
[ -n "$cur" ] || exit 0

# First check of a new session: adopt the phase we found and say nothing. This is
# also the repair path if .flow/phase is lost - a session with no recorded phase
# claims the one it is standing in rather than declaring a false boundary.
started=$(session_phase) || {
  set_session_phase "$cur"
  exit 0
}

# The pending marker is what makes a re-run idempotent, and it is also the signal
# /flow:loop reads to know it must spawn rather than loop here.
[ -f .flow/handoff-pending ] && exit 0

if [ "$cur" != "$started" ]; then
  # The boundary. Leave .flow/phase alone: this session still OWNS the phase it
  # finished, and the handoff has to be able to say which one that was. The
  # successor claims the next phase at its own session start.
  printf '%s' "$started" > .flow/handoff-pending

  {
    printf 'flow: PHASE BOUNDARY - "%s" is complete.\n' "$started"
    printf '  This session has finished its phase. Before doing anything else:\n'
    printf '    1. Commit any uncommitted work from that phase.\n'
    printf '    2. Update HANDOFF-%s.md - it is the only thing that crosses the boundary.\n' "$slug"
    printf '    3. Stop the loop here. Do NOT start "%s" in this session.\n' "$cur"
    printf '  Then run /flow:loop, which spawns a fresh agent on clean context for the next phase.\n'
    printf '  The user can override this with one word if the remaining work is small.\n'
  } >&2
  exit 2
fi

# Mid-phase, but over budget. Phase sizing is a human guess made at the point of
# least information, and this is what catches a wrong one: the boundary stops
# depending on the guess being right. Same ending as a phase boundary - the
# difference is that the phase is UNFINISHED, so the handoff has to say so.
if budget=$(context_over_budget "$plan"); then
  ctx=${budget% *}
  lim=${budget#* }
  printf '%s' "$started" > .flow/handoff-pending

  {
    printf 'flow: CONTEXT BOUNDARY - %s of a %s budget, mid-phase.\n' "$(fmt_k "$ctx")" "$(fmt_k "$lim")"
    printf '  "%s" is NOT finished, but this session is: past this point every turn\n' "$started"
    printf '  re-reads the whole thread and the same work costs several times more.\n'
    printf '    1. Finish ONLY the task in hand, then commit it.\n'
    printf '    2. Update HANDOFF-%s.md. Say that the phase is PART DONE and name the\n' "$slug"
    printf '       exact task the successor picks up - the phase was sized too large.\n'
    printf '    3. Stop the loop and run /flow:loop to continue on fresh context.\n'
  } >&2
  exit 2
fi

exit 0
