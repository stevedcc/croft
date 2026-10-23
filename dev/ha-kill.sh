#!/usr/bin/env bash
set -euo pipefail

# Kills ONE instance of the local HA stack (dev/ha-local.sh) by its pid file: a real kill of
# the process, but SIGTERM first so ASP.NET Core shuts down gracefully (in-flight requests
# complete, new connections are refused) — the local stand-in for the k8s preStop drain (D7).
# The HaTests call this while their reader loop runs; the reader loop must see zero non-2xx.
#
# Usage: dev/ha-kill.sh web|api 1|2
#   web 1   kill Web 1 (pid file /tmp/blog-ha-web1.pid)
#   api 2   kill API 2 (pid file /tmp/blog-ha-api2.pid)
#
# Exits 0 when the process exited within the graceful window (15 s), and removes its pid
# file — that instance is gone; dev/ha-local.sh down will not touch it again. Exits 1 (after
# escalating to SIGKILL) when the process ignored SIGTERM: the kill then was NOT graceful and
# callers must not treat the run as clean.

usage() {
    echo "Usage: dev/ha-kill.sh web|api 1|2"
}

KIND="${1:-}"
N="${2:-}"
case "$KIND" in
    web|api) ;;
    *) usage >&2; exit 2 ;;
esac
case "$N" in
    1|2) ;;
    *) usage >&2; exit 2 ;;
esac

PID_FILE="/tmp/blog-ha-$KIND$N.pid"
LABEL="$(printf '%s%s' "$KIND" "$N")"

if [ ! -f "$PID_FILE" ]; then
    echo "ha-kill: no pid file $PID_FILE for $LABEL; is the HA stack up (dev/ha-local.sh up)?" >&2
    exit 2
fi

PID="$(cat "$PID_FILE")"
if ! kill -0 "$PID" 2>/dev/null; then
    echo "ha-kill: $LABEL (pid $PID) is not running; refusing to pretend it was killed." >&2
    exit 1
fi

echo "ha-kill: sending SIGTERM to $LABEL (pid $PID, $PID_FILE) ..."
kill -TERM "$PID"

# Wait on the concrete condition — the process being gone — not on a blind sleep.
for _ in {1..15}; do
    if ! kill -0 "$PID" 2>/dev/null; then
        rm -f "$PID_FILE"
        echo "ha-kill: $LABEL (pid $PID) stopped gracefully."
        exit 0
    fi
    sleep 1
done

echo "ha-kill: $LABEL (pid $PID) ignored SIGTERM for 15s; escalating to SIGKILL." >&2
kill -KILL "$PID" 2>/dev/null || true
rm -f "$PID_FILE"
echo "ha-kill: $LABEL (pid $PID) had to be SIGKILLed — the kill was not graceful." >&2
exit 1
