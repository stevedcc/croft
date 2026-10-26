#!/usr/bin/env bash
set -euo pipefail

# The HA E2E run: start the local HA stack fresh (dev/ha-local.sh up), run the HaTests
# category against it, stop exactly that stack (also on failure or Ctrl+C).
#
# Usage: dev/run-ha-e2e.sh [--filter EXPR]
#   --filter EXPR   dotnet test filter (default "Category=HaTests"; e.g.
#                   "Category=HaTests&FullyQualifiedName~Uploads" to run one test)
#
# Self-contained on purpose (like dev/run-large-data-e2e.sh): every run gets a fresh blog_ha
# database and a fresh stack, because the tests kill instances and the run must not inherit
# a half-dead stack from an earlier one. The stack is stopped at the end; `up`/`down` remain
# usable by hand for debugging between runs.
#
# The HaTests category is excluded from the normal E2E run (the test project's default
# filter); this script's filter selects exactly those tests. Exit code: non-zero when the
# tests fail.

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
source "$ROOT/dev/lib/test-summary.sh"

# global.json pins SDK 11.0.100 (RC 2). Fedora ships 10.0.1xx in /usr/bin, so prefer ~/dotnet when present.
if [ -x "$HOME/dotnet/dotnet" ]; then
    export DOTNET_ROOT="$HOME/dotnet"
    export PATH="$HOME/dotnet:$PATH"
fi

FILTER="Category=HaTests"
usage() {
    echo "Usage: dev/run-ha-e2e.sh [--filter EXPR]"
    echo "  --filter EXPR  dotnet test filter (default Category=HaTests)"
}
while [ $# -gt 0 ]; do
    case "$1" in
        --filter) shift; FILTER="${1:?--filter needs an expression}" ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done

# Always stop the stack this run started — on success, failure and interrupts.
STACK_STARTED=0
cleanup() {
    rc=$?
    trap - EXIT INT TERM
    if [ "$STACK_STARTED" = 1 ]; then
        echo ""
        echo "Stopping the HA stack started by this run ..."
        "$ROOT/dev/ha-local.sh" down || true
    fi
    exit "$rc"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

echo "== HA E2E run: dev/ha-local.sh up =="
STACK_STARTED=1
"$ROOT/dev/ha-local.sh" up

echo ""
echo "Running E2E filter '$FILTER' (one NUnit worker: the tests kill stack instances) ..."
SECONDS=0
RUN_LOG="/tmp/blog-e2e-run-ha.log"
set +e
dotnet test tests/Croft.Web.E2E --no-build --filter "$FILTER" \
    --logger "console;verbosity=normal" -- "NUnit.NumberOfTestWorkers=1" 2>&1 | tee "$RUN_LOG"
TEST_RC=${PIPESTATUS[0]}
set -e
DOTNET_RC=$TEST_RC
require_test_summary "$RUN_LOG" "$TEST_RC" || TEST_RC=$?

echo ""
echo "run-ha-e2e: filter '$FILTER', wall time ${SECONDS}s, dotnet test exit code $DOTNET_RC, run-ha-e2e exit code $TEST_RC, summary: ${TEST_SUMMARY_LINE:-none}."
exit "$TEST_RC"
