#!/usr/bin/env bash
# Runs the Playwright E2E suite across N isolated stack instances in parallel: starts
# instances FIRST..FIRST+N-1, runs the suite with one NUnit worker per instance (each
# fixture pins itself to its worker's instance for its lifetime — tests/Croft.Web.E2E/
# WebTest.cs), then stops exactly the instances it started, also on failure or Ctrl+C.
#
# Usage: dev/run-e2e.sh [--instances N] [--first-instance K] [--browser NAME] [--csp-enforced] [--default-branding] [--output-cache] [-- <dotnet test args>]
#   --instances N       run N instances with N NUnit workers (default 3, chosen by
#                       the 2026-10-10 item-4 measurements: N=1 229s, N=2 152s,
#                       N=3 130s, N=4 135s suite wall; N=2 is >10% off the best)
#   --first-instance K  start instances K..K+N-1 (default 1). Instances may belong to
#                       other work on this machine: check `ss -ltn` for ports 5000-5099
#                       first; this script refuses to touch ports that already listen.
#   --browser NAME      run the suite in chromium (the default), firefox or webkit (Playwright's
#                       BROWSER variable). The engines must be installed (see AGENTS.md). Only the
#                       CSP category is meant for firefox and webkit: pair it with --csp-enforced.
#                       A run for another engine than chromium skips the default-branding pass.
#   --csp-enforced      run only the CSP measurement — the CspTests category, unless -- names
#                       other tests — against instances that enforce CSP explicitly
#                       (Csp__ReportOnly=false, ruling R11). Redundant since Csp:ReportOnly
#                       defaults to false (the plain run enforces and includes Category=Csp);
#                       kept so the measurement can still be run on its own:
#                       dev/run-e2e.sh --instances 1 --csp-enforced.
#   --default-branding  run only the DefaultBranding category (the default fallback banner's
#                       pixel test) against instances with NO branding folder (BLOG_BRANDING_PATH
#                       empty), unless -- names other tests. A plain run (no flags, no --) does
#                       this itself as a second pass on one instance (FIRST) after the main suite.
#   --output-cache      run only the OutputCache category (brief 14b) against instances with the Web
#                       app's output cache ON (a minute for the pages, five for the feed and sitemap).
#                       Every other run starts its instances with Croft__Web__OutputCache__Duration and
#                       __FeedDuration at 00:00:00 (caching off, unless the caller exports other
#                       values): the suite publishes through the API
#                       behind the Web app's back and reads the page at once, which a cache that is
#                       up to a minute stale would turn into a lottery. A plain run does this pass
#                       itself, after the default-branding pass.
#   -- ...              passed to `dotnet test` verbatim (e.g. --filter "...")
#
# Instances start sequentially on purpose: every start-local.sh invocation builds the
# solution once, and parallel builds would fight over obj/. Each start itself waits for
# API health, Web readiness and the E2E seed.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

# global.json pins SDK 11.0.100 (RC 2). Fedora ships 10.0.1xx in /usr/bin, so prefer ~/dotnet when present.
if [ -x "$HOME/dotnet/dotnet" ]; then
    export DOTNET_ROOT="$HOME/dotnet"
    export PATH="$HOME/dotnet:$PATH"
fi

source "$ROOT/dev/instance-params.sh"
source "$ROOT/dev/lib/test-summary.sh"

INSTANCES=3
FIRST_INSTANCE=1
CSP_ENFORCED=0
DEFAULT_BRANDING=0
OUTPUT_CACHE=0
BROWSER_NAME=""
DOTNET_ARGS=()
usage() {
    echo "Usage: dev/run-e2e.sh [--instances N] [--first-instance K] [--browser NAME] [--csp-enforced] [--default-branding] [--output-cache] [-- <dotnet test args>]"
    echo "  --instances N       N isolated instances and N NUnit workers (default 3)"
    echo "  --first-instance K  start instances K..K+N-1 (default 1)"
    echo "  --browser NAME      chromium (default), firefox or webkit"
    echo "  --csp-enforced      only the CspTests category, on explicitly-enforced instances"
    echo "  --default-branding  only the DefaultBranding category, on instances with no branding folder"
    echo "  --output-cache      only the OutputCache category, on instances with the page output cache on"
    echo "  -- ...              passed to dotnet test verbatim"
}
while [ $# -gt 0 ]; do
    case "$1" in
        --instances) shift; INSTANCES="${1:?--instances needs a number}" ;;
        --first-instance) shift; FIRST_INSTANCE="${1:?--first-instance needs a number}" ;;
        --browser) shift; BROWSER_NAME="${1:?--browser needs chromium, firefox or webkit}" ;;
        --csp-enforced) CSP_ENFORCED=1 ;;
        --default-branding) DEFAULT_BRANDING=1 ;;
        --output-cache) OUTPUT_CACHE=1 ;;
        --) shift; DOTNET_ARGS=("$@"); break ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done
case "$BROWSER_NAME" in
    ""|chromium|firefox|webkit) ;;
    *) echo "--browser must be chromium, firefox or webkit, got '$BROWSER_NAME'" >&2; exit 2 ;;
esac
if [ -n "$BROWSER_NAME" ]; then
    export BROWSER="$BROWSER_NAME"
fi
if [ "$CSP_ENFORCED" -eq 1 ]; then
    # The CSP measurement needs the policy to really block (ruling R11): the instances this run
    # start serve the enforcing header — what the app does by default since Csp:ReportOnly
    # defaults to false; the explicit export pins it either way — and unless the caller named
    # other tests the run is the CspTests category alone.
    export Csp__ReportOnly=false
    if [ "${#DOTNET_ARGS[@]}" -eq 0 ]; then
        DOTNET_ARGS=(--filter "Category=Csp")
    fi
fi
# A plain run (no mode flag, no explicit dotnet test arguments) finishes with the default-branding
# pass; any filtered or special run does not.
SECOND_PASS=0
if [ "$CSP_ENFORCED" -eq 0 ] && [ "$DEFAULT_BRANDING" -eq 0 ] && [ "$OUTPUT_CACHE" -eq 0 ] && [ "${#DOTNET_ARGS[@]}" -eq 0 ] \
    && { [ -z "$BROWSER_NAME" ] || [ "$BROWSER_NAME" = chromium ]; }; then
    SECOND_PASS=1
fi
if [ "$DEFAULT_BRANDING" -eq 1 ]; then
    # The instances this run starts have no branding folder (start-local.sh: an empty
    # BLOG_BRANDING_PATH runs the built-in defaults), so the default fallback banner is what a
    # banner-less post shows.
    export BLOG_BRANDING_PATH=
    if [ "${#DOTNET_ARGS[@]}" -eq 0 ]; then
        DOTNET_ARGS=(--filter "Category=DefaultBranding")
    fi
fi
if [ "$OUTPUT_CACHE" -eq 1 ]; then
    # The instances this run starts cache the reading pages (the app's default), and the run is the
    # OutputCache category alone unless the caller named other tests.
    unset Croft__Web__OutputCache__Duration Croft__Web__OutputCache__FeedDuration
    export E2E_OUTPUT_CACHE=on
    if [ "${#DOTNET_ARGS[@]}" -eq 0 ]; then
        DOTNET_ARGS=(--filter "Category=OutputCache")
    fi
else
    # Caching is off for every other run (see the header), and the OutputCache tests, which
    # need it on, skip themselves.
    export Croft__Web__OutputCache__Duration="${Croft__Web__OutputCache__Duration-00:00:00}"
    export Croft__Web__OutputCache__FeedDuration="${Croft__Web__OutputCache__FeedDuration-00:00:00}"
    unset E2E_OUTPUT_CACHE
fi
positive_int() {
    case "$1" in ''|*[!0-9]*|0) return 1 ;; esac
}
positive_int "$INSTANCES" || { echo "--instances needs a positive integer, got '$INSTANCES'" >&2; exit 2; }
positive_int "$FIRST_INSTANCE" || { echo "--first-instance needs a positive integer, got '$FIRST_INSTANCE'" >&2; exit 2; }

LAST_INSTANCE=$((FIRST_INSTANCE + INSTANCES - 1))

port_listening() {
    ss -ltnH 2>/dev/null | awk '{ print $4 }' | grep -qE ":$1\$"
}

# Pre-flight: the mock IdP is shared by every instance; bring compose up if needed.
if ! curl -sf http://localhost:5556/default/.well-known/openid-configuration >/dev/null; then
    echo "Mock IdP not reachable; starting docker compose ..."
    docker compose up -d
    for _ in {1..30}; do
        curl -sf http://localhost:5556/default/.well-known/openid-configuration >/dev/null && break
        sleep 1
    done
    if ! curl -sf http://localhost:5556/default/.well-known/openid-configuration >/dev/null; then
        echo "Mock IdP still not reachable at http://localhost:5556/default" >&2
        exit 1
    fi
fi

# Pre-flight: never touch an instance whose ports are already serving another stack.
CONFLICTS=()
for i in $(seq "$FIRST_INSTANCE" "$LAST_INSTANCE"); do
    instance_params e2e "$i"
    port_listening "$INSTANCE_API_PORT" && CONFLICTS+=("instance $i API :$INSTANCE_API_PORT")
    port_listening "$INSTANCE_WEB_PORT" && CONFLICTS+=("instance $i Web :$INSTANCE_WEB_PORT")
done
if [ "${#CONFLICTS[@]}" -gt 0 ]; then
    echo "Refusing to start: ports already serving another stack: ${CONFLICTS[*]}" >&2
    echo "ss -ltn on ports 5000-5099 shows what is running; this script only manages instances $FIRST_INSTANCE..$LAST_INSTANCE." >&2
    exit 1
fi

# Stop every instance this script started — on success, on failure and on Ctrl+C — and
# nothing else (stop-local.sh --e2e --instance N touches instance N's PID files only).
STARTED=()
cleanup() {
    rc=$?
    trap - EXIT INT TERM
    if [ "${#STARTED[@]}" -gt 0 ]; then
        echo ""
        echo "Stopping instance(s) started by this run: ${STARTED[*]}"
        for i in "${STARTED[@]}"; do
            "$ROOT/stop-local.sh" --e2e --instance "$i" || true
        done
    fi
    exit "$rc"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

for i in $(seq "$FIRST_INSTANCE" "$LAST_INSTANCE"); do
    STARTED+=("$i")
    ./start-local.sh --e2e --instance "$i"
done

# The suite maps each fixture's NUnit worker to one of the started instances through these
# variables; an exported E2E_WEB_URL/E2E_API_URL would override that mapping back onto a
# single instance, so they are unset for the test process.
unset E2E_WEB_URL E2E_API_URL
export E2E_INSTANCE_COUNT="$INSTANCES"
export E2E_FIRST_INSTANCE="$FIRST_INSTANCE"

# CircuitAdmissionTests starts a Web instance of its own and signs staff in on it (circuits are
# for signed-in staff only, brief 13a), so the test process needs the sign-in settings the
# instances got. start-local.sh has created .env by now; only these three keys are passed on,
# loaded the way start-local.sh loads .env (values may contain semicolons).
if [ -f "$ROOT/.env" ]; then
    while IFS='=' read -r key value; do
        case "$key" in
            Oidc__Authority|Oidc__ClientId|Oidc__ClientSecret) export "$key=$value" ;;
        esac
    done < "$ROOT/.env"
fi

echo ""
echo "Running E2E suite: $INSTANCES instances ($FIRST_INSTANCE..$LAST_INSTANCE), $INSTANCES NUnit workers ..."
SECONDS=0
# The log is what proves the run finished (dev/lib/test-summary.sh); CI uploads /tmp/blog-e2e-*.log.
RUN_LOG="/tmp/blog-e2e-run-${FIRST_INSTANCE}-${LAST_INSTANCE}.log"
set +e
dotnet test tests/Croft.Web.E2E --no-build "${DOTNET_ARGS[@]}" -- "NUnit.NumberOfTestWorkers=$INSTANCES" 2>&1 | tee "$RUN_LOG"
TEST_RC=${PIPESTATUS[0]}
set -e
DOTNET_RC=$TEST_RC
require_test_summary "$RUN_LOG" "$TEST_RC" || TEST_RC=$?

echo ""
echo "E2E run finished: instances $FIRST_INSTANCE..$LAST_INSTANCE, $INSTANCES workers, suite wall time ${SECONDS}s, dotnet test exit code $DOTNET_RC, run-e2e exit code $TEST_RC, summary: ${TEST_SUMMARY_LINE:-none}."

if [ "$SECOND_PASS" -eq 1 ]; then
    # Second pass: the DefaultBranding category needs a site with no branding folder. The main
    # instances are stopped first (and the list cleared, so the trap does not stop them twice);
    # the pass reuses instance FIRST, which this run has just freed.
    echo ""
    echo "Stopping the main run's instance(s) ${STARTED[*]} before the default-branding pass ..."
    for i in "${STARTED[@]}"; do
        "$ROOT/stop-local.sh" --e2e --instance "$i" || true
    done
    STARTED=()
    PASS_RC=0
    "$ROOT/dev/run-e2e.sh" --default-branding --instances 1 --first-instance "$FIRST_INSTANCE" || PASS_RC=$?
    if [ "$TEST_RC" -eq 0 ]; then
        TEST_RC=$PASS_RC
    fi

    # Third pass: the OutputCache category needs instances that cache the reading pages.
    echo ""
    echo "Starting the output-cache pass ..."
    PASS_RC=0
    "$ROOT/dev/run-e2e.sh" --output-cache --instances 1 --first-instance "$FIRST_INSTANCE" || PASS_RC=$?
    if [ "$TEST_RC" -eq 0 ]; then
        TEST_RC=$PASS_RC
    fi
fi
exit "$TEST_RC"
