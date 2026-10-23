#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT"

# global.json pins SDK 10.0.4xx. Fedora ships 10.0.1xx in /usr/bin, so prefer ~/dotnet when present.
if [ -x "$HOME/dotnet/dotnet" ]; then
    export DOTNET_ROOT="$HOME/dotnet"
    export PATH="$HOME/dotnet:$PATH"
fi

source "$ROOT/dev/instance-params.sh"

MODE=dev
INSTANCE=1
usage() {
    echo "Usage: ./start-local.sh [--e2e [--instance N]]"
    echo "  (no flags)          start the dev stack (shared database and uploads folder from .env)"
    echo "  --e2e               start E2E instance 1 on its isolated database, uploads and ports"
    echo "  --e2e --instance N  start E2E instance N (parameters: dev/instance-params.sh)"
}
while [ $# -gt 0 ]; do
    case "$1" in
        --e2e) MODE=e2e ;;
        --instance) shift; INSTANCE="${1:?--instance needs a number}" ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done
if [ "$MODE" = dev ] && [ "$INSTANCE" != 1 ]; then
    echo "--instance only makes sense together with --e2e" >&2
    exit 2
fi
case "$INSTANCE" in
    ''|*[!0-9]*|0) echo "--instance needs a positive integer, got '$INSTANCE'" >&2; exit 2 ;;
esac

if [ ! -f .env ]; then
    echo "Creating .env from .env.example"
    cp .env.example .env
fi

# Load .env into the current shell.
# .env values may contain semicolons, so a plain `source` would truncate them.
while IFS='=' read -r key value; do
    case "$key" in
        ''|\#*) continue ;;
    esac
    export "$key=$value"
done < .env

instance_params "$MODE" "$INSTANCE"

if ! curl -sf http://localhost:5556/default/.well-known/openid-configuration >/dev/null; then
    echo "OAuth2 mock server is not reachable at http://localhost:5556/default"
    echo "Start dependencies first: docker compose up -d"
    exit 1
fi

# Build once; migrations and both apps then run without rebuilding (avoids parallel builds
# fighting over obj/ and repeated restores).
echo "Building solution ..."
dotnet build Croft.slnx -nologo -v quiet

# Migrations need dotnet-ef, a local tool (dotnet-tools.json) a fresh clone has not restored.
# Restore it here and stop with a clear message if it is still missing: a start that skipped
# the migrations would report "ready" against an empty database and answer every page with 500.
echo "Restoring local tools ..."
dotnet tool restore -v quiet >/dev/null
if ! dotnet ef --version >/dev/null 2>&1; then
    echo "dotnet-ef is not available (dotnet tool restore did not provide it); cannot apply the database migrations." >&2
    echo "Check dotnet-tools.json and that the NuGet source is reachable, then run: dotnet tool restore" >&2
    exit 1
fi

if [ "$MODE" = e2e ]; then
    # This path owns the blog_e2e_* databases and must never touch the dev database. The
    # connection string is derived in dev/instance-params.sh and re-checked here before
    # anything destructive runs against it.
    if [[ ! "$INSTANCE_DB" =~ ^blog_e2e_[0-9]+$ ]]; then
        echo "Refusing to drop '$INSTANCE_DB': --e2e only drops blog_e2e_* databases" >&2
        exit 1
    fi

    # Drop fails while the instance's own API still holds connections; restart the instance
    # clean first, then make sure nothing else is serving its ports before we report ready.
    "$ROOT/stop-local.sh" --e2e --instance "$INSTANCE"
    for _ in {1..15}; do
        if ! curl -sf "http://localhost:$INSTANCE_API_PORT/health" >/dev/null \
            && ! curl -sf "http://localhost:$INSTANCE_WEB_PORT/" >/dev/null; then
            break
        fi
        sleep 1
    done
    if curl -sf "http://localhost:$INSTANCE_API_PORT/health" >/dev/null \
        || curl -sf "http://localhost:$INSTANCE_WEB_PORT/" >/dev/null; then
        echo "Ports $INSTANCE_API_PORT/$INSTANCE_WEB_PORT are already serving another stack (the dev stack?); refusing to start $INSTANCE_LABEL" >&2
        exit 1
    fi

    echo "Recreating database $INSTANCE_DB ..."
    # database drop takes no --connection; it drops the context's configured database, so the
    # instance connection string is passed via the environment. The dry run names the target and
    # must name INSTANCE_DB exactly: this path must never drop the dev database.
    drop_target="$(ConnectionStrings__BlogDb="$INSTANCE_CONN" \
        dotnet ef database drop --dry-run -p src/Croft.Api -s src/Croft.Api --no-build)"
    echo "$drop_target"
    if [[ "$drop_target" != *"the database '$INSTANCE_DB'"* ]]; then
        echo "Refusing to drop: the dry run did not name '$INSTANCE_DB' (got: $drop_target)" >&2
        exit 1
    fi
    ConnectionStrings__BlogDb="$INSTANCE_CONN" \
        dotnet ef database drop --force -p src/Croft.Api -s src/Croft.Api --no-build

    echo "Applying database migrations ..."
    ConnectionStrings__BlogDb="$INSTANCE_CONN" \
        dotnet ef database update -p src/Croft.Api -s src/Croft.Api --no-build --connection "$INSTANCE_CONN"

    # The Web app's own database (sessions, Data Protection keys), recreated the same way. Its
    # name is blog_e2e_N_web, re-checked like the API's.
    if [[ ! "$INSTANCE_WEB_DB" =~ ^blog_e2e_[0-9]+_web$ ]]; then
        echo "Refusing to drop '$INSTANCE_WEB_DB': --e2e only drops blog_e2e_*_web Web databases" >&2
        exit 1
    fi
    echo "Recreating database $INSTANCE_WEB_DB ..."
    web_drop_target="$(ConnectionStrings__WebDb="$INSTANCE_WEB_CONN" \
        dotnet ef database drop --dry-run -p src/Croft.Web -s src/Croft.Web --no-build)"
    echo "$web_drop_target"
    if [[ "$web_drop_target" != *"the database '$INSTANCE_WEB_DB'"* ]]; then
        echo "Refusing to drop: the dry run did not name '$INSTANCE_WEB_DB' (got: $web_drop_target)" >&2
        exit 1
    fi
    ConnectionStrings__WebDb="$INSTANCE_WEB_CONN" \
        dotnet ef database drop --force -p src/Croft.Web -s src/Croft.Web --no-build
    ConnectionStrings__WebDb="$INSTANCE_WEB_CONN" \
        dotnet ef database update -p src/Croft.Web -s src/Croft.Web --no-build --connection "$INSTANCE_WEB_CONN"

    # Fresh empty uploads folder for every E2E start: the suite must never see files a
    # previous run left behind. This runs before the apps start and before the baseline
    # seed (dev/seed-e2e.sh, below), so anything the seed uploads survives the start.
    rm -rf "$INSTANCE_UPLOADS"
    mkdir -p "$INSTANCE_UPLOADS"
else
    echo "Applying database migrations ..."
    dotnet ef database update -p src/Croft.Api -s src/Croft.Api --no-build
fi

wait_for_url() {
    local url=$1
    local label=$2
    local attempts=${3:-30}
    for _ in $(seq "$attempts"); do
        if curl -sf "$url" >/dev/null; then
            return 0
        fi
        sleep 1
    done
    echo "Timed out waiting for $label ($url)"
    return 1
}

start_api() {
    if curl -sf "http://localhost:$INSTANCE_API_PORT/health/live" >/dev/null; then
        echo "API already running on http://localhost:$INSTANCE_API_PORT"
        return
    fi

    # E2E instances: the discussion is on, but no Mastodon server and no Bluesky DID are configured
    # (dev/instance-params.sh), so the fetcher asks no platform and the API shows what the tests stored.
    # Pinned empty here, whatever the shell exports: no E2E run may reach a real platform.
    if [ -n "${INSTANCE_SOCIAL_ENABLED:-}" ]; then
        export Social__Enabled="$INSTANCE_SOCIAL_ENABLED" Social__Mastodon__Server= Social__Mastodon__Account= Social__Bluesky__Did=
    fi
    echo "Starting API on http://localhost:$INSTANCE_API_PORT ..."
    ConnectionStrings__BlogDb="$INSTANCE_CONN" \
        FileStorage__BasePath="$INSTANCE_UPLOADS" \
        Oidc__Authority="$Oidc__Authority" \
        Oidc__ClientId="$Oidc__ClientId" \
        Oidc__ClientSecret="$Oidc__ClientSecret" \
        Analytics__IngestKey="${Analytics__IngestKey:-}" \
        Analytics__HashKey="${Analytics__HashKey:-}" \
        Database__ReadHost="${INSTANCE_READ_HOST:-}" \
        dotnet run --no-build --project src/Croft.Api --urls "http://localhost:$INSTANCE_API_PORT" >"$INSTANCE_API_LOG" 2>&1 &

    echo $! >"$INSTANCE_API_PID"
}

start_web() {
    if curl -sf "http://localhost:$INSTANCE_WEB_PORT/health/live" >/dev/null; then
        echo "Web already running on http://localhost:$INSTANCE_WEB_PORT"
        return
    fi

    echo "Starting Web on http://localhost:$INSTANCE_WEB_PORT ..."
    # The instance's CSP mode (dev/instance-params.sh, ruling R11): empty keeps the app's
    # Csp:ReportOnly default (enforced). An explicit Csp__ReportOnly passes through:
    # dev/run-e2e.sh --csp-enforced pins false, and a rollout rehearsal can export true for a
    # report-only instance.
    if [ -n "$INSTANCE_CSP_REPORT_ONLY" ]; then
        export Csp__ReportOnly="$INSTANCE_CSP_REPORT_ONLY"
    fi
    # The site's branding folder: the repo's example by default (it proves the merge and gives
    # dev/E2E a visibly site-specific identity). BLOG_BRANDING_PATH overrides it, and an empty
    # BLOG_BRANDING_PATH= runs the built-in defaults (for the default-branding screenshots).
    # E2E instances keep their sessions in their own Web database (INSTANCE_WEB_CONN, empty in
    # dev mode, where the Web app runs database-less as it always did).
    ConnectionStrings__WebDb="${INSTANCE_WEB_CONN:-}" \
        ApiBaseUrl="$INSTANCE_API_BASE_URL" \
        Blog__BaseUrl="http://localhost:$INSTANCE_WEB_PORT/" \
        Blog__BrandingPath="${BLOG_BRANDING_PATH-$ROOT/branding.example}" \
        Blog__SiteFilesPath="${BLOG_SITE_FILES_PATH-$ROOT/site-files.example}" \
        Oidc__Authority="$Oidc__Authority" \
        Oidc__ClientId="$Oidc__ClientId" \
        Oidc__ClientSecret="$Oidc__ClientSecret" \
        Analytics__IngestKey="${Analytics__IngestKey:-}" \
        dotnet run --no-build --project src/Croft.Web --urls "http://localhost:$INSTANCE_WEB_PORT" >"$INSTANCE_WEB_LOG" 2>&1 &

    echo $! >"$INSTANCE_WEB_PID"
}

# Both apps warm themselves up before they report ready (brief 14a, Warmup:*): wait for
# /health/ready, not for the port, so the instance a test or a hand-test meets is warm. Liveness
# is the "already running" test above: a pod that is still warming is running. The wait allows
# the warm-up's own budget (Warmup:Timeout, one minute) on top of the start.
start_api
wait_for_url "http://localhost:$INSTANCE_API_PORT/health/ready" "API readiness" 90 || exit 1

start_web
wait_for_url "http://localhost:$INSTANCE_WEB_PORT/health/ready" "Web readiness" 120 || exit 1

# The E2E baseline seed (dev/seed-e2e.sh) inserts through the API as the mock admin, so it
# runs only once the whole instance is up, and it gets THIS instance's API base URL (a
# seed must never default to instance 1's port). A failing seed fails the start loudly:
# the stack is running but it is not ready without its baseline data.
if [ "$MODE" = e2e ]; then
    if ! API_BASE_URL="$INSTANCE_API_BASE_URL" "$ROOT/dev/seed-e2e.sh"; then
        echo "dev/seed-e2e.sh failed against $INSTANCE_API_BASE_URL" >&2
        echo "$INSTANCE_LABEL is running but unseeded; stop it with: ./stop-local.sh --e2e --instance $INSTANCE" >&2
        exit 1
    fi
fi

echo ""
echo "$INSTANCE_LABEL is ready:"
echo "  API:   http://localhost:$INSTANCE_API_PORT"
echo "  Web:   http://localhost:$INSTANCE_WEB_PORT"
echo "  OAuth: http://localhost:5556/default"
echo "  DB:    $INSTANCE_DB   uploads: $INSTANCE_UPLOADS"
echo ""
echo "Logs: $INSTANCE_API_LOG $INSTANCE_WEB_LOG"
if [ "$MODE" = e2e ]; then
    echo "Stop with: ./stop-local.sh --e2e --instance $INSTANCE   (or ./stop-local.sh --all)"
else
    echo "Stop with: ./stop-local.sh"
fi
