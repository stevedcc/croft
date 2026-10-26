#!/usr/bin/env bash
set -euo pipefail

# The large-data E2E run: start the E2E instance, seed the large dataset, run the LargeData
# test category, check the Web process's memory, stop the instance and print the timings.
#
# Usage:  dev/run-large-data-e2e.sh [--instance N]
#   --instance N    the E2E instance to run against (parameters: dev/instance-params.sh; N
#                   defaults to 1, the same default dev/seed-large-data.sh uses)
#
# The run is fully self-contained per instance:
#   1. ./start-local.sh --e2e --instance N   (drops/recreates blog_e2e_N, starts API + Web)
#   2. dev/seed-large-data.sh --instance N   (400 categories, 3,000 tags, 3,000 published
#      posts, 600 media items, through the API as the mock admin)
#   3. dotnet test tests/Croft.Web.E2E --filter "Category=LargeData" against that instance
#   4. the memory check: the Web process's VmRSS read from /proc/<pid>/status after the tests
#      must stay under 600 MiB (checked here, never from the tests)
#   5. ./stop-local.sh --e2e --instance N    (always, including on failure)
# and it prints the phase durations and the per-surface timings the tests measured
# (navigation start -> first interactive control responding, budget 2 s per surface).
#
# The LargeData category is excluded from the normal E2E run (the test project's default
# filter); this script's filter selects exactly those tests. Exit code: non-zero when the
# tests fail or the memory budget is exceeded.

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

# One suite at a time on this machine (dev/with-suite-lock.sh): re-run under the lock unless
# the caller already holds it (the later passes of a locked run call this script again).
if [ "${CROFT_SUITE_LOCK_HELD:-}" != "1" ]; then
    case " $* " in
        *" -h "*|*" --help "*) ;;
        *) exec "$ROOT/dev/with-suite-lock.sh" "$ROOT/dev/run-large-data-e2e.sh" "$@" ;;
    esac
fi

# global.json pins SDK 11.0.100 (RC 2). Fedora ships 10.0.1xx in /usr/bin, so prefer ~/dotnet when present.
if [ -x "$HOME/dotnet/dotnet" ]; then
    export DOTNET_ROOT="$HOME/dotnet"
    export PATH="$HOME/dotnet:$PATH"
fi

source "$ROOT/dev/instance-params.sh"
source "$ROOT/dev/lib/test-summary.sh"

INSTANCE=1
usage() {
    echo "Usage: dev/run-large-data-e2e.sh [--instance N]"
    echo "  --instance N  E2E instance to run against (dev/instance-params.sh; N defaults to 1)"
}
while [ $# -gt 0 ]; do
    case "$1" in
        --instance) shift; INSTANCE="${1:?--instance needs a number}" ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done
case "$INSTANCE" in
    ''|*[!0-9]*|0) echo "run-large-data-e2e: --instance needs a positive integer, got '$INSTANCE'" >&2; exit 2 ;;
esac

instance_params e2e "$INSTANCE"
WEB_URL="http://localhost:$INSTANCE_WEB_PORT"
API_URL="http://localhost:$INSTANCE_API_PORT"

# The Web process must stay under 600 MiB after the run (VmRSS, read below).
MEMORY_BUDGET_KB=$((600 * 1024))

TEST_LOG="$(mktemp "${TMPDIR:-/tmp}/run-large-data-e2e.XXXXXX")"

# Always stop the instance this run started — on success, failure and interrupts. The explicit
# stop at the end prints what it stopped; this trap covers the paths that never get there.
stop_instance_if_running() {
    if [ -f "$INSTANCE_API_PID" ] || [ -f "$INSTANCE_WEB_PID" ]; then
        "$ROOT/stop-local.sh" --e2e --instance "$INSTANCE" >/dev/null 2>&1 || true
    fi
}
trap 'stop_instance_if_running; rm -f "$TEST_LOG"' EXIT

# The PID start-local.sh recorded is `dotnet run`; the app itself is its child process, and the
# memory budget is about the app. Walk the recorded process's descendants for the Croft.Web
# binary (its cmdline carries ".../Croft.Web/bin/..."); fall back to the recorded PID itself.
web_app_pid() {
    local root_pid="$1"
    local -a queue=("$root_pid")
    local pid cmd child
    while [ "${#queue[@]}" -gt 0 ]; do
        pid="${queue[0]}"
        queue=("${queue[@]:1}")
        cmd="$(tr '\0' ' ' <"/proc/$pid/cmdline" 2>/dev/null || true)"
        case "$cmd" in
            *Croft.Web/bin*) printf '%s' "$pid"; return 0 ;;
        esac
        for child in $(pgrep -P "$pid" 2>/dev/null || true); do
            queue+=("$child")
        done
    done
    printf '%s' "$root_pid"
}

SECONDS=0
echo "== Large-data E2E run: $INSTANCE_LABEL (API $API_URL, Web $WEB_URL) =="

echo "[1/5] start the instance ..."
phase_start=$SECONDS
"$ROOT/start-local.sh" --e2e --instance "$INSTANCE"
start_secs=$((SECONDS - phase_start))

echo "[2/5] seed the large dataset ..."
phase_start=$SECONDS
"$ROOT/dev/seed-large-data.sh" --instance "$INSTANCE"
seed_secs=$((SECONDS - phase_start))

echo "[3/5] run the LargeData tests ..."
phase_start=$SECONDS
set +e
E2E_WEB_URL="$WEB_URL" E2E_API_URL="$API_URL" \
    dotnet test tests/Croft.Web.E2E --no-build --filter "Category=LargeData" \
    --logger "console;verbosity=normal" 2>&1 | tee "$TEST_LOG"
test_rc=${PIPESTATUS[0]}
set -e
require_test_summary "$TEST_LOG" "$test_rc" || test_rc=$?
test_secs=$((SECONDS - phase_start))

echo "[4/5] Web memory (after the tests) ..."
memory_ok=1
if [ -f "$INSTANCE_WEB_PID" ] && [ -r "/proc/$(cat "$INSTANCE_WEB_PID")/status" ]; then
    app_pid="$(web_app_pid "$(cat "$INSTANCE_WEB_PID")")"
    app_cmd="$(tr '\0' ' ' <"/proc/$app_pid/cmdline" 2>/dev/null | cut -c1-100)"
    rss_kb="$(sed -n 's/^VmRSS:[[:space:]]*\([0-9]*\).*/\1/p' "/proc/$app_pid/status")"
    rss_mib="$(awk -v kb="$rss_kb" 'BEGIN { printf "%.1f", kb / 1024 }')"
    budget_mib=$((MEMORY_BUDGET_KB / 1024))
    echo "  Web process $app_pid ($app_cmd)"
    echo "  VmRSS ${rss_kb} kB = ${rss_mib} MiB (budget ${budget_mib} MiB)"
    if [ "$rss_kb" -ge "$MEMORY_BUDGET_KB" ]; then
        echo "  FAILED: the Web process uses ${rss_mib} MiB, at or over the ${budget_mib} MiB budget." >&2
        memory_ok=0
    fi
else
    echo "  FAILED: no Web process to measure ($INSTANCE_WEB_PID is gone)." >&2
    memory_ok=0
fi

echo "[5/5] stop the instance ..."
stop_instance_if_running

total_secs=$SECONDS
echo ""
echo "== Timings =="
printf '  start   %4ss\n' "$start_secs"
printf '  seed    %4ss\n' "$seed_secs"
printf '  tests   %4ss\n' "$test_secs"
printf '  total   %4ss\n' "$total_secs"
echo ""
echo "  per surface (navigation start -> first interactive control responded):"
grep "LARGE-DATA TIMING" "$TEST_LOG" | sed 's/^/  /' || echo "  (none measured)"

if [ "$test_rc" -ne 0 ]; then
    echo ""
    echo "run-large-data-e2e: the LargeData tests failed (exit $test_rc)." >&2
    exit "$test_rc"
fi
if [ "$memory_ok" -ne 1 ]; then
    echo ""
    echo "run-large-data-e2e: the Web process exceeded the $((MEMORY_BUDGET_KB / 1024)) MiB memory budget." >&2
    exit 1
fi
echo ""
echo "run-large-data-e2e: all checks passed."
