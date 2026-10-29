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

# One suite at a time on this machine (dev/with-suite-lock.sh): re-run under the lock unless
# the caller already holds it (the later passes of a locked run call this script again).
if [ "${CROFT_SUITE_LOCK_HELD:-}" != "1" ]; then
    case " $* " in
        *" -h "*|*" --help "*) ;;
        *) exec "$ROOT/dev/with-suite-lock.sh" "$ROOT/dev/run-e2e.sh" "$@" ;;
    esac
fi

# global.json pins SDK 11.0.100 (RC 3). Fedora ships 10.0.1xx in /usr/bin, so prefer ~/dotnet when present.
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

# Pre-flight: the mock IdP must issue the claims a login posts (brief 17a item 5, issue #57).
# A discovery document only proves the HTTP server is up. On CI the first token of a run came
# back without the Admin group (a 403 on the first matrix fixture's upload), and nothing had
# exercised /authorize or /token on the cold JVM before two workers started their flows at
# once. mock-oauth2-server 2.3.0 keeps the code-to-login map in a plain HashMap that its
# Netty worker threads write concurrently (AuthorizationCodeHandler.codeToLoginCache), so a
# lost entry yields a token with the default claims. This runs the code flow the way
# dev/seed-dev-data.sh does and requires sub admin-id and the Admin group in the access
# token: serially until one is good (up to 30 x 1 s, which also initialises the maps before
# anything runs concurrently), then three flows at once. It is a readiness check like the
# discovery wait above, not a test retry.
MOCK_AUTHORITY="http://localhost:5556/default"
IDP_REQUIRED_GROUP="${IDP_REQUIRED_GROUP:-Admin}"

# Prints the decoded payload of an access token for the posted admin identity, or a
# parenthesised reason when the flow itself failed (and returns 1).
idp_admin_token_payload() {
    local jar headers authorize_url claims code response token segment pad
    jar="$(mktemp)"
    headers="$(mktemp)"
    authorize_url="$MOCK_AUTHORITY/authorize?response_type=code&client_id=blog-web&redirect_uri=http%3A%2F%2Flocalhost%3A9876%2Fcallback&scope=openid&state=gate"
    claims='{"sub":"admin-id","name":"Admin User","email":"admin@example.com","email_verified":true,"groups":["Admin"]}'
    curl -s -m 10 -o /dev/null -c "$jar" -b "$jar" "$authorize_url"
    curl -s -m 10 -o /dev/null -D "$headers" -c "$jar" -b "$jar" \
        --data-urlencode "username=admin@example.com" --data-urlencode "claims=$claims" "$authorize_url"
    code="$(grep -io 'code=[^&[:space:]]*' "$headers" | head -n 1 | sed 's/^[^=]*=//')"
    if [ -z "$code" ]; then
        echo "(no authorization code in the login redirect: $(head -n 1 "$headers" | tr -d '\r'))"
        rm -f "$jar" "$headers"
        return 1
    fi
    response="$(curl -s -m 10 -c "$jar" -b "$jar" \
        --data-urlencode "grant_type=authorization_code" --data-urlencode "code=$code" \
        --data-urlencode "client_id=blog-web" --data-urlencode "client_secret=secret" \
        --data-urlencode "redirect_uri=http://localhost:9876/callback" "$MOCK_AUTHORITY/token")"
    rm -f "$jar" "$headers"
    token="$(grep -o '"access_token" *: *"[^"]*"' <<<"$response" | head -n 1 | sed 's/.*: *"//; s/"$//')"
    if [ -z "$token" ]; then
        echo "(no access token in the token response: $response)"
        return 1
    fi
    segment="$(cut -d. -f2 <<<"$token" | tr '_-' '/+')"
    pad=$(( (4 - ${#segment} % 4) % 4 ))
    [ "$pad" -gt 0 ] && segment="$segment$(printf '=%.0s' $(seq 1 "$pad"))"
    base64 -d <<<"$segment" 2>/dev/null
    echo
}

idp_payload_is_admin() {
    grep -Eq '"sub" *: *"admin-id"' <<<"$1" && grep -Eq "\"groups\" *: *\[[^]]*\"$IDP_REQUIRED_GROUP\"" <<<"$1"
}

idp_gate_failed() {
    echo "Mock IdP gate failed: $1" >&2
    shift
    local n=0 payload
    for payload in "$@"; do
        n=$((n + 1))
        echo "  payload $n: $payload" >&2
    done
    echo "docker compose logs --tail 50 oauth2:" >&2
    docker compose logs --tail 50 oauth2 >&2 || true
    exit 1
}

idp_gate() {
    local payload="" attempt flow dir
    for attempt in $(seq 1 30); do
        payload="$(idp_admin_token_payload)" && idp_payload_is_admin "$payload" && break
        payload="${payload:-(empty)}"
        [ "$attempt" -lt 30 ] && sleep 1
    done
    idp_payload_is_admin "$payload" || idp_gate_failed "no good admin token in 30 tries (last payload below)" "$payload"

    dir="$(mktemp -d)"
    for flow in 1 2 3; do
        ( idp_admin_token_payload >"$dir/$flow" 2>&1 ) &
    done
    wait
    local payloads=() bad=0
    for flow in 1 2 3; do
        payloads+=("$(cat "$dir/$flow")")
        idp_payload_is_admin "${payloads[-1]}" || bad=1
    done
    rm -rf "$dir"
    [ "$bad" -eq 0 ] || idp_gate_failed "three concurrent flows did not all return the posted admin claims" "${payloads[@]}"
    echo "Mock IdP gate: serial flow and three concurrent flows returned sub admin-id with group $IDP_REQUIRED_GROUP."
}
idp_gate

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
# loaded the way start-local.sh loads .env (values may contain semicolons) and with the same
# precedence: a variable already set and non-empty in the environment is never overwritten by
# .env (PRESET_KEYS snapshots it before the loop, so a repeat inside .env still takes the last line).
if [ -f "$ROOT/.env" ]; then
    PRESET_KEYS=" "
    while IFS= read -r name; do
        if [ -n "${!name:-}" ]; then
            PRESET_KEYS="$PRESET_KEYS$name "
        fi
    done < <(compgen -e)
    while IFS='=' read -r key value; do
        case "$key" in
            Oidc__Authority|Oidc__ClientId|Oidc__ClientSecret)
                case "$PRESET_KEYS" in
                    *" $key "*) continue ;;
                esac
                export "$key=$value" ;;
        esac
    done < "$ROOT/.env"
fi

echo ""
echo "Running E2E suite: $INSTANCES instances ($FIRST_INSTANCE..$LAST_INSTANCE), $INSTANCES NUnit workers ..."
SECONDS=0
# The log is what proves the run finished (dev/lib/test-summary.sh); CI uploads /tmp/blog-e2e-*.log.
# The mode is in the name so the later passes (same instance range) do not overwrite each other's log.
LOG_MODE=""
[ "$CSP_ENFORCED" -eq 1 ] && LOG_MODE="-csp-enforced"
[ "$DEFAULT_BRANDING" -eq 1 ] && LOG_MODE="-default-branding"
[ "$OUTPUT_CACHE" -eq 1 ] && LOG_MODE="-output-cache"
RUN_LOG="/tmp/blog-e2e-run-${FIRST_INSTANCE}-${LAST_INSTANCE}${LOG_MODE}.log"
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
