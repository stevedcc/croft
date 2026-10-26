#!/usr/bin/env bash
# Runs a command while holding the machine-wide suite lock, so two test suites never run at once
# (issue #73: two concurrent suites pushed the dev box into swap on 2026-10-23 and into OOM kills
# on 2026-10-25).
#
# Usage: dev/with-suite-lock.sh [--try] <command...>
#   --try   do not wait: exit 75 at once when another suite holds the lock
#
# The lock is flock on ${XDG_CACHE_HOME:-$HOME/.cache}/croft/suite.lock. The holder writes
# suite.lock.owner (pid|start-time|command) and removes it on exit; a waiter prints that once.
# The command's exit code is passed through. The command does not inherit the lock's file
# descriptor, so a process it leaves running cannot keep the lock after the wrapper exits.
# Re-entrancy: with CROFT_SUITE_LOCK_HELD=1 (set for the command) the command runs unlocked, so
# a locked run-e2e.sh can call itself for its later passes and start-local.sh.
# The lock is per machine; CI is not special-cased.
set -euo pipefail

TRY=0
if [ "${1:-}" = "--try" ]; then
    TRY=1
    shift
fi
if [ $# -eq 0 ]; then
    echo "Usage: dev/with-suite-lock.sh [--try] <command...>" >&2
    exit 2
fi

if [ "${CROFT_SUITE_LOCK_HELD:-}" = "1" ]; then
    exec "$@"
fi

LOCK_DIR="${XDG_CACHE_HOME:-$HOME/.cache}/croft"
LOCK_FILE="$LOCK_DIR/suite.lock"
OWNER_FILE="$LOCK_FILE.owner"
mkdir -p "$LOCK_DIR"

exec 9>"$LOCK_FILE"
if ! flock -n 9; then
    if [ "$TRY" -eq 1 ]; then
        echo "with-suite-lock: another suite is running; not waiting (--try)" >&2
        exit 75
    fi
    owner="$(cat "$OWNER_FILE" 2>/dev/null || true)"
    if [ -n "$owner" ]; then
        IFS='|' read -r o_pid o_since o_cmd <<<"$owner"
        echo "with-suite-lock: another suite is running (pid $o_pid, since $o_since, command $o_cmd); waiting ..." >&2
    else
        echo "with-suite-lock: another suite is running (owner unknown); waiting ..." >&2
    fi
    flock 9
fi

printf '%s|%s|%s\n' "$$" "$(date +%FT%T)" "$*" >"$OWNER_FILE"
remove_owner() {
    # Only the holder's own record: a successor writes its record only after this process exits.
    if [ "$(cut -d'|' -f1 "$OWNER_FILE" 2>/dev/null)" = "$$" ]; then
        rm -f "$OWNER_FILE"
    fi
}
trap remove_owner EXIT

# The command runs in the background (fd 9 closed, stdin kept) so INT and TERM sent to this
# wrapper reach it: the lock is held exactly as long as the command runs.
CROFT_SUITE_LOCK_HELD=1 "$@" 9>&- <&0 &
child=$!
forward() {
    kill -s "$1" "$child" 2>/dev/null || true
}
trap 'forward INT' INT
trap 'forward TERM' TERM
rc=0
while true; do
    wait "$child" && rc=0 || rc=$?
    # wait returns early (rc > 128) when a trapped signal arrived; wait again until the child is gone.
    kill -0 "$child" 2>/dev/null || break
done
exit "$rc"
