#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
source "$ROOT/dev/instance-params.sh"

MODE=dev
INSTANCE=1
usage() {
    echo "Usage: ./stop-local.sh [--e2e [--instance N]] [--all]"
    echo "  (no flags)          stop the dev stack"
    echo "  --e2e --instance N  stop E2E instance N (defaults to instance 1)"
    echo "  --all               stop the dev stack and every E2E instance"
    echo "Instance parameters: dev/instance-params.sh"
}
while [ $# -gt 0 ]; do
    case "$1" in
        --e2e) MODE=e2e ;;
        --instance) shift; INSTANCE="${1:?--instance needs a number}" ;;
        --all) MODE=all ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done
case "$INSTANCE" in
    ''|*[!0-9]*|0) echo "--instance needs a positive integer, got '$INSTANCE'" >&2; exit 2 ;;
esac

# How many PID files the last stop_pid_files call found (0 = nothing was running).
PID_FILES_FOUND=0

stop_pid_files() {
    PID_FILES_FOUND=0
    for pidfile in "$@"; do
        [ -f "$pidfile" ] || continue
        PID_FILES_FOUND=$((PID_FILES_FOUND + 1))
        pid="$(cat "$pidfile")"
        if kill "$pid" 2>/dev/null; then
            echo "Stopped process $pid ($pidfile)"
        else
            echo "Process $pid ($pidfile) was not running"
        fi
        rm -f "$pidfile"
    done
}

stop_dev() {
    instance_params dev
    stop_pid_files "$INSTANCE_API_PID" "$INSTANCE_WEB_PID"
}

stop_e2e_instance() {
    instance_params e2e "$1"
    stop_pid_files "$INSTANCE_API_PID" "$INSTANCE_WEB_PID"
}

case "$MODE" in
    dev)
        stop_dev
        echo "Local .NET services stopped."
        ;;
    e2e)
        stop_e2e_instance "$INSTANCE"
        if [ "$PID_FILES_FOUND" -gt 0 ]; then
            echo "E2E instance $INSTANCE stopped."
        else
            echo "E2E instance $INSTANCE: nothing was running (no PID files found)."
        fi
        ;;
    all)
        stop_dev
        # shellcheck disable=SC2046 # word-splitting the pid-file glob is intended; missing
        # files are skipped by stop_pid_files.
        stop_pid_files $(e2e_instance_pid_glob)
        echo "Local .NET services and all E2E instances stopped."
        ;;
esac
