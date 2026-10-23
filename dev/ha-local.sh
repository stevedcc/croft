#!/usr/bin/env bash
set -euo pipefail

# The local HA stack: two API and two Web instances against ONE database, behind a local
# Traefik container, for the HaTests E2E category (dev/run-ha-e2e.sh). Each `up` gives the
# stack a fresh blog_ha database (dropped/recreated, migrated once) and the seed script run
# once; `down` stops exactly what `up` started (and nothing else on the machine).
#
# Usage: dev/ha-local.sh up | down | start api|web 1|2
#   up                drop/recreate + migrate the databases, start the whole stack
#   down              stop exactly what up (or start) started — nothing else on the machine
#   start api|web N   (re)start ONE instance — the harness stand-in for k8s rescheduling a
#                     pod: HaTests kill instances and then restore what they killed, so the
#                     next test finds two Web and two API instances again
#
# Knobs (environment, read by up and start):
#   HA_NO_WEB_DB=1    start the Web instances WITHOUT the Web database (the in-memory token
#                     store and no shared Data Protection keys — the single-instance default).
#                     08a item 7's break-it-once run uses this: its admin probe must then fail
#                     with a sign-in redirect. Default is 0 (the shared croft_web_ha).
#
# The parameters below are the single source of truth for the HA stack. tests/Croft.Web.E2E/
# HaTests.cs mirrors them (like WebTest.cs mirrors dev/instance-params.sh); keep both in sync.
#
# Ports — the block 5300-5319, outside the agent range 5000-5199. Chosen after `ss -ltn`
# showed 5300-5319 free (5301-5312 of the original suggestion were reshuffled to keep the
# two Traefik entrypoints adjacent at the block's start):
#   5300  Traefik "web" entrypoint   readers and browsers; round-robin across Web 1/2 with
#                                   the sticky cookie "blog_ha_web"
#   5301  Traefik "api" entrypoint   Web -> API calls (page data, feed, the media proxy),
#                                   round-robin across API 1/2 — Web's ApiBaseUrl points here
#   5302  API 1 direct               bypassing Traefik (the uploads test uploads here)
#   5303  Web 1 direct               bypassing Traefik
#   5312  API 2 direct               bypassing Traefik (the uploads test fetches here)
#   5313  Web 2 direct               bypassing Traefik
#   5314  OIDC counting proxy        dev/ha-oidc-proxy.cs: the Web instances' Oidc:Authority.
#                                   It forwards to the HA mock IdP and rewrites exactly one
#                                   discovery field — token_endpoint — to itself, so its log
#                                   (/tmp/blog-ha-oidc-tokens.jsonl) is the request counter at
#                                   the token endpoint HaTests 08a item 7 asserts against,
#                                   while the issuer stays the HA mock's own and every token
#                                   the mock mints is its own short-lived JWT (the API's token
#                                   validation stays part of the test).
#   5315  HA mock IdP                this stack's OWN ghcr.io/navikt/mock-oauth2-server
#                                   container ("blog-ha-mock"), like its own Traefik: the
#                                   shared dev mock on :5556 is never reconfigured or
#                                   restarted. Its config (dev/ha-mock-oauth2-config.json)
#                                   gives the Web client 70-second access tokens — just over
#                                   the app's 60 s refresh window — which is the refresh
#                                   scenario's premise (08a item 7). Everything in this stack
#                                   that talks to an IdP uses it: the Web instances (through
#                                   the counting proxy), both APIs' token validation, the seed
#                                   step's OIDC_AUTHORITY, and the tests' direct token minting.
#
# Database: blog_ha (dropped and recreated on every `up`, migrated once, seeded once via
# dev/seed-e2e.sh). The media folder is SHARED by both API instances — one directory,
# standing in for the ReadWriteMany volume (D6). PID files /tmp/blog-ha-{api1,api2,web1,web2}.pid
# (plus /tmp/blog-ha-oidc-proxy.pid for the counting proxy) name the exact processes `down` and
# dev/ha-kill.sh stop; logs are next to them in /tmp.
#
# Web database: croft_web_ha (dropped and recreated on every `up`, migrated once through the Web
# app's `migrate` entry — the same serialised entry the k8s init container runs) with
# ConnectionStrings__WebDb set for BOTH Web instances, so they share login sessions and Data
# Protection keys like the k8s shape (D1/D2). It is the foundation of 08a item 7's signed-in
# tests; its break-it-once run turns it off with HA_NO_WEB_DB=1 (see the knobs above).
#
# The apps run as `dotnet <dll>` (not `dotnet run`) so each recorded PID IS the app process:
# dev/ha-kill.sh sends it SIGTERM and gets ASP.NET Core's graceful shutdown (in-flight
# requests complete, new connections are refused) — the local stand-in for the k8s preStop
# drain (D7). Killing by pid file with SIGTERM is still a genuine kill of that instance.
#
# Traefik: container "blog-ha-traefik" (image traefik:v3.7.14) run rootless via
# podman --remote run --rm, with the generated config directory .ha-traefik (in the worktree)
# bind mounted READ-ONLY at /etc/traefik (no other volumes, nothing privileged). `--remote` goes
# through the current user's rootless Podman socket ($XDG_RUNTIME_DIR/podman/podman.sock —
# the interface AGENTS.md names for containers): inside this sandbox's single-uid user
# namespace the local engine cannot set up its own rootless namespace (newuidmap cannot map
# the subuid range; `podman info` fails with it), while the socket's service runs containers
# exactly as rootless Podman should. The config directory cannot live in /tmp: the sandbox's
# /tmp is a private tmpfs the Podman service cannot see (statfs fails). It is mounted with
# :Z, the standard SELinux relabel for rootless volumes (the files are typed var_t otherwise
# and the container cannot read them), and regenerated on every up (it carries this script's
# port numbers, so the script stays the single source of truth for them). Down removes it
# and .gitignore lists .ha-traefik/, so a running HA stack never dirties git status.
# host.containers.internal (rootless Podman's host alias) is what reaches the host processes
# — verified working here, so the container keeps its own network namespace (host networking
# is not needed); the alias forwards to a NON-loopback host address, which is why start_app
# binds the apps to 0.0.0.0 (a loopback-only listener gets "connection refused" from there).
# `down` stops only that container (by its fixed name) and only the processes the pid files
# name.
#
# The Traefik config (generated by `up`) is what keeps a reader's requests 2xx across a kill:
#   - active health checks (Web on /, API on /health) every 1s remove a dead instance from
#     rotation quickly;
#   - passive health checks (maxFailedAttempts: 1) mark an instance DOWN on the first
#     connection failure, before the retry re-enters the load balancer;
#   - the retry middleware (3 attempts) retries a request whose connection was refused — the
#     request never reached the backend, so retrying is safe for every method — and a
#     502-504 response for idempotent methods only (Traefik never retries POST/PATCH/LOCK
#     on status codes), covering a connection broken mid-response during a graceful
#     shutdown; either way the retry re-enters the load balancer, and Traefik's sticky
#     cookie falls back to the surviving instance when its server is DOWN.
#
# The API processes run with Microsoft.AspNetCore request logging at Information (env, no
# code change) so their /tmp logs say which instance served which request: HaTests read the
# logs to prove the publisher ran exactly once per post and that proxied calls really spread
# across both API instances.

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

# global.json pins SDK 10.0.4xx. Fedora ships 10.0.1xx in /usr/bin, so prefer ~/dotnet when present.
if [ -x "$HOME/dotnet/dotnet" ]; then
    export DOTNET_ROOT="$HOME/dotnet"
    export PATH="$HOME/dotnet:$PATH"
fi

# --- The stack parameters (see the header comment) -------------------------------------------
HA_DB=blog_ha
HA_WEB_DB=croft_web_ha
# The break-it-once knob (08a item 7): HA_NO_WEB_DB=1 starts the Web instances WITHOUT the Web
# database — the in-memory token store and no shared Data Protection keys, the single-instance
# default. On by default: the shared croft_web_ha.
HA_NO_WEB_DB="${HA_NO_WEB_DB:-0}"
HA_UPLOADS=/tmp/blog-ha-uploads
HA_API1_PORT=5302
HA_WEB1_PORT=5303
HA_API2_PORT=5312
HA_WEB2_PORT=5313
HA_TRAEFIK_WEB_PORT=5300   # "web" entrypoint: readers and browsers
HA_TRAEFIK_API_PORT=5301   # "api" entrypoint: Web -> API calls, load-balanced
HA_TRAEFIK_NAME=blog-ha-traefik
HA_TRAEFIK_IMAGE=traefik:v3.7.14
HA_TRAEFIK_CONFIG="$ROOT/.ha-traefik"
HA_STICKY_COOKIE=blog_ha_web
HA_OIDC_PROXY_PORT=5314    # the OIDC counting proxy the Web instances use as Oidc:Authority
HA_OIDC_PROXY_PID=/tmp/blog-ha-oidc-proxy.pid
HA_OIDC_PROXY_LOG=/tmp/blog-ha-oidc-proxy.log
HA_OIDC_PROXY_TOKENS=/tmp/blog-ha-oidc-tokens.jsonl
HA_OIDC_PROXY_APP="/tmp/blog-ha-oidc-proxy-app" # published file-based app (dev/ha-oidc-proxy.cs)
HA_MOCK_PORT=5315           # this stack's own mock IdP container (see the header comment)
HA_MOCK_NAME=blog-ha-mock
HA_MOCK_IMAGE=ghcr.io/navikt/mock-oauth2-server:2.1.10
HA_MOCK_CONFIG="$ROOT/dev/ha-mock-oauth2-config.json"

# The HA stack's IdP authority: its own mock, never the shared dev mock on :5556.
ha_mock_authority() {
    echo "http://localhost:$HA_MOCK_PORT/default"
}

# WORKAROUND(W-084, Podman): the sandbox's single-uid user namespace cannot run local rootless Podman, so containers go through the socket
# Container CLI: through the current user's rootless Podman socket (see the header comment).
PODMAN=(podman --remote)
# END WORKAROUND(W-084)

usage() {
    echo "Usage: dev/ha-local.sh up | down | start api|web 1|2"
    echo "  up              drop/recreate + migrate $HA_DB, start API 1/2, Web 1/2, Traefik and the"
    echo "                  OIDC counting proxy (HA_NO_WEB_DB=1: Web runs with the in-memory store)"
    echo "  down            stop the Traefik container, the proxy and the processes the pid files"
    echo "                  name (nothing else)"
    echo "  start api|web N (re)start one instance, waiting for its health endpoint"
}

ha_pid_file() {  # ha_pid_file api|web 1|2
    echo "/tmp/blog-ha-$1$2.pid"
}

ha_log_file() {  # ha_log_file api|web 1|2
    echo "/tmp/blog-ha-$1$2.log"
}

ha_port_for() {  # ha_port_for api|web 1|2
    case "$1-$2" in
        api-1) echo "$HA_API1_PORT" ;;
        web-1) echo "$HA_WEB1_PORT" ;;
        api-2) echo "$HA_API2_PORT" ;;
        web-2) echo "$HA_WEB2_PORT" ;;
    esac
}

port_listening() {
    ss -ltnH 2>/dev/null | awk '{ print $4 }' | grep -qE ":$1\$"
}

wait_for_url() {
    local url=$1 label=$2
    for _ in {1..30}; do
        if curl -sf "$url" >/dev/null; then
            return 0
        fi
        sleep 1
    done
    echo "Timed out waiting for $label ($url)" >&2
    return 1
}

load_dotnet_and_env() {
    if [ ! -f .env ]; then
        echo "Creating .env from .env.example"
        cp .env.example .env
    fi
    # .env values may contain semicolons, so a plain `source` would truncate them.
    while IFS='=' read -r key value; do
        case "$key" in
            ''|\#*) continue ;;
        esac
        export "$key=$value"
    done < .env
}

# The blog_ha connection string: same Postgres and credentials as dev, only the database name
# changes. Refuse to derive anything that would not target blog_ha exactly (Npgsql would
# otherwise resolve to the dev database) — the same guard dev/instance-params.sh applies.
ha_connection_string() {
    local conn
    conn="$(printf '%s' "${ConnectionStrings__BlogDb:-}" | sed -E "s/(database=)[^;]*/\\1$HA_DB/I")"
    if [ -z "${ConnectionStrings__BlogDb:-}" ] \
        || [ "$(printf '%s' "$conn" | sed -En 's/.*database=([^;]*).*/\1/Ip')" != "$HA_DB" ]; then
        echo "ha-local: refusing to derive a connection string that does not target $HA_DB" >&2
        return 1
    fi
    printf '%s' "$conn"
}

# The croft_web_ha connection string for both Web instances: the same Postgres and credentials
# as the blog_ha string above, only the database name changes — with the same refusal to derive
# anything that would not target $HA_WEB_DB exactly.
ha_web_connection_string() {
    local conn
    conn="$(printf '%s' "${ConnectionStrings__BlogDb:-}" | sed -E "s/(database=)[^;]*/\\1$HA_WEB_DB/I")"
    if [ -z "${ConnectionStrings__BlogDb:-}" ] \
        || [ "$(printf '%s' "$conn" | sed -En 's/.*database=([^;]*).*/\1/Ip')" != "$HA_WEB_DB" ]; then
        echo "ha-local: refusing to derive a Web connection string that does not target $HA_WEB_DB" >&2
        return 1
    fi
    printf '%s' "$conn"
}

start_app() {  # start_app api|web N conn [web-conn]
    local kind=$1 n=$2 conn=$3 web_conn=${4:-}
    local port pid log
    port="$(ha_port_for "$kind" "$n")"
    pid="$(ha_pid_file "$kind" "$n")"
    log="$(ha_log_file "$kind" "$n")"

    echo "Starting $kind $n on http://localhost:$port ..."
    # The apps bind ALL interfaces (0.0.0.0), not localhost: Traefik reaches the host processes
    # via host.containers.internal, which rootless Podman forwards to a non-loopback host
    # address — a loopback-only listener gets "connection refused" there. The HA ports are
    # therefore reachable on this machine's other interfaces too (like Postgres :5432 already
    # is); this stack is a dev/test stack and is documented as such.
    # ASPNETCORE_ENVIRONMENT=Development matches the other local stacks (`dotnet run` gets it
    # from launchSettings.json): these are dev/test processes against the mock IdP. env -C sets
    # the project directory as the working directory — the content root `dotnet run` would give
    # the app (appsettings.json, wwwroot). env(1) also carries the logging key with dots in its
    # name, which bash would parse as a command.
    if [ "$kind" = api ]; then
        # WORKAROUND(W-084, Podman): host.containers.internal reaches only non-loopback listeners, so the apps bind 0.0.0.0
        env -C "$ROOT/src/Croft.Api" ASPNETCORE_ENVIRONMENT=Development \
            ConnectionStrings__BlogDb="$conn" \
            FileStorage__BasePath="$HA_UPLOADS" \
            Oidc__Authority="$(ha_mock_authority)" \
            Oidc__ClientId="$Oidc__ClientId" \
            Oidc__ClientSecret="$Oidc__ClientSecret" \
            Analytics__IngestKey="${Analytics__IngestKey:-}" \
            Analytics__HashKey="${Analytics__HashKey:-}" \
            "Logging__LogLevel__Microsoft.AspNetCore=Information" \
            dotnet "$ROOT/src/Croft.Api/bin/Debug/net10.0/Croft.Api.dll" \
                --urls "http://0.0.0.0:$port" >"$log" 2>&1 &
        # END WORKAROUND(W-084)
    else
        # The Web instances use the OIDC counting proxy as their authority (port 5314): its
        # discovery document points token_endpoint at itself, so the token log counts every
        # login exchange and refresh, while the issuer and every other endpoint stay the HA
        # mock IdP's own — tokens validate against the API exactly as on the ordinary stack.
        # With HA_NO_WEB_DB=1 (the break-it-once knob) no ConnectionStrings__WebDb reaches the
        # app: it runs with the in-memory token store and per-instance Data Protection keys.
        # WORKAROUND(W-084, Podman): host.containers.internal reaches only non-loopback listeners, so the apps bind 0.0.0.0
        env -C "$ROOT/src/Croft.Web" ASPNETCORE_ENVIRONMENT=Development \
            ApiBaseUrl="http://localhost:$HA_TRAEFIK_API_PORT/" \
            Blog__BaseUrl="http://localhost:$HA_TRAEFIK_WEB_PORT/" \
            Blog__BrandingPath="${BLOG_BRANDING_PATH-$ROOT/branding.example}" \
            ConnectionStrings__WebDb="$web_conn" \
            Oidc__Authority="http://localhost:$HA_OIDC_PROXY_PORT/default" \
            Oidc__ClientId="$Oidc__ClientId" \
            Oidc__ClientSecret="$Oidc__ClientSecret" \
            Analytics__IngestKey="${Analytics__IngestKey:-}" \
            dotnet "$ROOT/src/Croft.Web/bin/Debug/net10.0/Croft.Web.dll" \
                --urls "http://0.0.0.0:$port" >"$log" 2>&1 &
        # END WORKAROUND(W-084)
    fi
    echo $! >"$pid"
}

stop_process_from_pid_file() {  # stop_process_from_pid_file label pid-file
    local label=$1 pid_file=$2
    local pid i
    if [ ! -f "$pid_file" ]; then
        echo "$label: no pid file ($pid_file); nothing to stop."
        return 0
    fi
    pid="$(cat "$pid_file")"
    if ! kill -0 "$pid" 2>/dev/null; then
        echo "$label: pid $pid ($pid_file) is not running; removing the stale pid file."
        rm -f "$pid_file"
        return 0
    fi
    echo "$label: sending SIGTERM to pid $pid ..."
    kill -TERM "$pid" 2>/dev/null || true
    for i in {1..30}; do
        if ! kill -0 "$pid" 2>/dev/null; then
            echo "$label: pid $pid stopped."
            rm -f "$pid_file"
            return 0
        fi
        sleep 1
    done
    echo "$label: pid $pid ignored SIGTERM for 30s; sending SIGKILL." >&2
    kill -KILL "$pid" 2>/dev/null || true
    rm -f "$pid_file"
    return 0
}

# The OIDC counting proxy (dev/ha-oidc-proxy.cs): published to a temporary build folder and run
# as one process. It must be up before the Web instances (their Oidc:Authority is this proxy),
# and it forwards to this stack's own mock IdP.
start_oidc_proxy() {
    echo "Publishing the OIDC counting proxy (dev/ha-oidc-proxy.cs) ..."
    dotnet publish dev/ha-oidc-proxy.cs -o "$HA_OIDC_PROXY_APP" --nologo -v quiet
    echo "Starting the OIDC counting proxy on http://localhost:$HA_OIDC_PROXY_PORT -> http://localhost:$HA_MOCK_PORT ..."
    "$HA_OIDC_PROXY_APP/ha-oidc-proxy" \
        "$HA_OIDC_PROXY_PORT" "http://localhost:$HA_MOCK_PORT" "$HA_OIDC_PROXY_TOKENS" >"$HA_OIDC_PROXY_LOG" 2>&1 &
    echo $! >"$HA_OIDC_PROXY_PID"
}

# This stack's own mock IdP ("blog-ha-mock"): the shared dev mock on :5556 is never touched.
# The container is run exactly like the Traefik one (rootless, --remote through the current
# user's Podman socket, read-only bind mounts of this repo's config and login page, nothing
# else), and `down` stops it by its fixed name.
start_mock_idp() {
    echo "Starting HA mock IdP container $HA_MOCK_NAME ($HA_MOCK_IMAGE) on http://localhost:$HA_MOCK_PORT ..."
    "${PODMAN[@]}" run --rm -d --name "$HA_MOCK_NAME" \
        -p "$HA_MOCK_PORT:8080" \
        -e SERVER_PORT=8080 \
        -e JSON_CONFIG_PATH=/app/config.json \
        -e LOG_LEVEL=INFO \
        -v "$HA_MOCK_CONFIG:/app/config.json:ro,Z" \
        -v "$ROOT/dev/login.html:/app/login.html:ro,Z" \
        "$HA_MOCK_IMAGE" >/dev/null
}

up() {
    load_dotnet_and_env
    local conn
    conn="$(ha_connection_string)"

    local conflicts=()
    for port in "$HA_TRAEFIK_WEB_PORT" "$HA_TRAEFIK_API_PORT" "$HA_API1_PORT" "$HA_WEB1_PORT" "$HA_API2_PORT" "$HA_WEB2_PORT" "$HA_OIDC_PROXY_PORT" "$HA_MOCK_PORT"; do
        port_listening "$port" && conflicts+=("$port")
    done
    if [ "${#conflicts[@]}" -gt 0 ]; then
        echo "Refusing to start: ports already serving another stack: ${conflicts[*]}" >&2
        echo "ss -ltn shows what is running; this stack only manages ports 5300-5315." >&2
        exit 1
    fi

    # This stack's own mock IdP (see the header comment): the shared dev mock on :5556 is
    # never configured, restarted or otherwise touched. Postgres is the one dependency this
    # stack still shares (its databases live there); `dotnet ef` fails loudly if it is down.
    start_mock_idp
    wait_for_url "http://localhost:$HA_MOCK_PORT/default/.well-known/openid-configuration" \
        "HA mock IdP" || exit 1

    echo "Building solution ..."
    dotnet build Croft.slnx -nologo -v quiet

    echo "Recreating database $HA_DB ..."
    # Database drop takes no --connection: it drops the context's configured database, so the
    # blog_ha connection string is passed via the environment. The dry run must name $HA_DB
    # exactly before anything destructive runs.
    local drop_target
    drop_target="$(ConnectionStrings__BlogDb="$conn" \
        dotnet ef database drop --dry-run -p src/Croft.Api -s src/Croft.Api --no-build)"
    echo "$drop_target"
    if [[ "$drop_target" != *"the database '$HA_DB'"* ]]; then
        echo "Refusing to drop: the dry run did not name '$HA_DB' (got: $drop_target)" >&2
        exit 1
    fi
    ConnectionStrings__BlogDb="$conn" \
        dotnet ef database drop --force -p src/Croft.Api -s src/Croft.Api --no-build

    echo "Applying database migrations ..."
    ConnectionStrings__BlogDb="$conn" \
        dotnet ef database update -p src/Croft.Api -s src/Croft.Api --no-build --connection "$conn"

    # The Web database (D1): both Web instances share one croft_web_ha — sessions and Data
    # Protection keys must be readable by either instance. Fresh on every up, like blog_ha.
    # The schema is applied ONCE through the Web app's own "migrate" entry (the serialised one
    # the k8s init container runs), so the instances themselves never race the schema. The
    # entry cannot create the database — its advisory lock already opens it — so this script
    # creates the empty one first with `database update 0` (zero migrations), exactly the k8s
    # shape where the CNPG Database creates it before the init container migrates.
    # HA_NO_WEB_DB=1 (the break-it-once knob) skips all of it: the Web instances then start
    # with no ConnectionStrings__WebDb at all and keep the in-memory store.
    local web_conn=""
    if [ "$HA_NO_WEB_DB" = 1 ]; then
        echo "HA_NO_WEB_DB=1: skipping the Web database; the Web instances run in-memory."
    else
        web_conn="$(ha_web_connection_string)"
        echo "Recreating Web database $HA_WEB_DB ..."
        local web_drop_target
        web_drop_target="$(ConnectionStrings__WebDb="$web_conn" \
            dotnet ef database drop --dry-run -p src/Croft.Web -s src/Croft.Web --no-build)"
        echo "$web_drop_target"
        if [[ "$web_drop_target" != *"the database '$HA_WEB_DB'"* ]]; then
            echo "Refusing to drop: the dry run did not name '$HA_WEB_DB' (got: $web_drop_target)" >&2
            exit 1
        fi
        ConnectionStrings__WebDb="$web_conn" \
            dotnet ef database drop --force -p src/Croft.Web -s src/Croft.Web --no-build

        echo "Creating the empty Web database $HA_WEB_DB ..."
        ConnectionStrings__WebDb="$web_conn" \
            dotnet ef database update 0 -p src/Croft.Web -s src/Croft.Web --no-build

        echo "Applying Web database migrations (once, via the migrate entry) ..."
        ConnectionStrings__WebDb="$web_conn" \
            dotnet "$ROOT/src/Croft.Web/bin/Debug/net10.0/Croft.Web.dll" migrate
    fi

    # One shared media folder for both API instances (the RWX stand-in). Emptied on every up
    # so a fresh database never sees files from an earlier run.
    rm -rf "$HA_UPLOADS"
    mkdir -p "$HA_UPLOADS"

    start_app api 1 "$conn"
    start_app api 2 "$conn"
    wait_for_url "http://localhost:$HA_API1_PORT/health" "API 1" || exit 1
    wait_for_url "http://localhost:$HA_API2_PORT/health" "API 2" || exit 1

    # Traefik before Web: Web's ApiBaseUrl is the Traefik api entrypoint, so Web cannot render
    # a page before the entrypoint answers.
    generate_traefik_config
    echo "Starting Traefik container $HA_TRAEFIK_NAME ($HA_TRAEFIK_IMAGE) ..."
    # WORKAROUND(W-084, Podman): the :ro,Z mount relabels for SELinux; host.containers.internal forwards to a non-loopback address
    "${PODMAN[@]}" run --rm -d --name "$HA_TRAEFIK_NAME" \
        -p "$HA_TRAEFIK_WEB_PORT:$HA_TRAEFIK_WEB_PORT" \
        -p "$HA_TRAEFIK_API_PORT:$HA_TRAEFIK_API_PORT" \
        -v "$HA_TRAEFIK_CONFIG:/etc/traefik:ro,Z" \
        "$HA_TRAEFIK_IMAGE" >/dev/null
    # END WORKAROUND(W-084)
    wait_for_url "http://localhost:$HA_TRAEFIK_API_PORT/health" "Traefik api entrypoint" || exit 1

    # The OIDC counting proxy before the Web instances: their Oidc:Authority points at it.
    start_oidc_proxy
    wait_for_url "http://localhost:$HA_OIDC_PROXY_PORT/default/.well-known/openid-configuration" \
        "OIDC counting proxy" || exit 1

    start_app web 1 "$conn" "$web_conn"
    start_app web 2 "$conn" "$web_conn"
    wait_for_url "http://localhost:$HA_WEB1_PORT/" "Web 1" || exit 1
    wait_for_url "http://localhost:$HA_WEB2_PORT/" "Web 2" || exit 1

    # The web entrypoint must answer too, which also proves the container reaches the host
    # processes (host.containers.internal) and its health checks bring both Web servers up.
    wait_for_url "http://localhost:$HA_TRAEFIK_WEB_PORT/feed.xml" "Traefik web entrypoint" || exit 1

    # The E2E baseline seed runs once per up, through the Traefik api entrypoint (which also
    # exercises the load balancer). dev/seed-e2e.sh takes the stack's API base URL as env; it
    # seeds nothing today (it is a documented no-op), but OIDC_AUTHORITY names this stack's
    # own mock IdP so a future seed that mints tokens can never reach the shared :5556 one.
    if ! API_BASE_URL="http://localhost:$HA_TRAEFIK_API_PORT" \
        OIDC_AUTHORITY="$(ha_mock_authority)" \
        "$ROOT/dev/seed-e2e.sh"; then
        echo "dev/seed-e2e.sh failed against the Traefik api entrypoint" >&2
        echo "The HA stack is running but unseeded; stop it with: dev/ha-local.sh down" >&2
        exit 1
    fi

    echo ""
    echo "HA stack is ready:"
    echo "  Traefik web (readers):  http://localhost:$HA_TRAEFIK_WEB_PORT   (sticky cookie $HA_STICKY_COOKIE)"
    echo "  Traefik api (Web->API): http://localhost:$HA_TRAEFIK_API_PORT"
    echo "  API 1 / 2 direct:       http://localhost:$HA_API1_PORT / http://localhost:$HA_API2_PORT"
    echo "  Web 1 / 2 direct:       http://localhost:$HA_WEB1_PORT / http://localhost:$HA_WEB2_PORT"
    echo "  DB: $HA_DB (API) + $( [ "$HA_NO_WEB_DB" = 1 ] && echo 'no Web DB (HA_NO_WEB_DB=1)' || echo "$HA_WEB_DB (Web)" )   uploads: $HA_UPLOADS (shared by both API instances)"
    echo ""
    echo "Logs: $(ha_log_file api 1) $(ha_log_file api 2) $(ha_log_file web 1) $(ha_log_file web 2)"
    echo "Stop with: dev/ha-local.sh down"
}

generate_traefik_config() {
    rm -rf "$HA_TRAEFIK_CONFIG"
    mkdir -p "$HA_TRAEFIK_CONFIG"

    cat >"$HA_TRAEFIK_CONFIG/traefik.yml" <<EOF
# Generated by dev/ha-local.sh (up); the directory is bind mounted read-only at /etc/traefik.
entryPoints:
  web:
    address: ":$HA_TRAEFIK_WEB_PORT"
  api:
    address: ":$HA_TRAEFIK_API_PORT"
providers:
  file:
    filename: /etc/traefik/dynamic.yml
log:
  level: INFO
EOF

    cat >"$HA_TRAEFIK_CONFIG/dynamic.yml" <<EOF
# Generated by dev/ha-local.sh (up).
http:
  routers:
    web:
      entryPoints: [web]
      rule: "PathPrefix(\`/\`)"
      service: web
      middlewares: [retry]
    api:
      entryPoints: [api]
      rule: "PathPrefix(\`/\`)"
      service: api
      middlewares: [retry]
  middlewares:
    # Retry keeps a reader's request 2xx while an instance is killed:
    #   - a refused connection never reached a backend (safe for every method) and is
    #     retried after a short backoff; the retry re-enters the load balancer, which has
    #     already marked the dead server DOWN (passive health check below) and picks the
    #     survivor;
    #   - a 502-504 response (e.g. the connection broke after the request reached the
    #     backend during a graceful shutdown) is retried for idempotent methods only —
    #     Traefik never retries POST/PATCH/LOCK on status codes, so the write paths
    #     (posts, publish, upload) cannot be double-executed.
    retry:
      retry:
        attempts: 3
        initialInterval: 50ms
        status: ["502-504"]
  services:
    web:
      loadBalancer:
        sticky:
          cookie:
            # This local stack is plain HTTP, so the cookie omits secure and sameSite;
            # the k8s documentation (k8s/README.md) keeps the full flags for its
            # TLS-terminated ingress: httpOnly, secure, sameSite: lax.
            name: $HA_STICKY_COOKIE
            httpOnly: true
        healthCheck:
          path: /
          interval: 1s
          timeout: 500ms
          unhealthyInterval: 500ms
        passiveHealthCheck:
          maxFailedAttempts: 1
          failureWindow: 5s
        servers:
          - url: "http://host.containers.internal:$HA_WEB1_PORT"
          - url: "http://host.containers.internal:$HA_WEB2_PORT"
    api:
      loadBalancer:
        healthCheck:
          path: /health
          interval: 1s
          timeout: 500ms
          unhealthyInterval: 500ms
        passiveHealthCheck:
          maxFailedAttempts: 1
          failureWindow: 5s
        servers:
          - url: "http://host.containers.internal:$HA_API1_PORT"
          - url: "http://host.containers.internal:$HA_API2_PORT"
EOF
}

down() {
    load_dotnet_and_env

    # Only the containers this stack started (their fixed names), and only its own pid files.
    for container in "$HA_TRAEFIK_NAME" "$HA_MOCK_NAME"; do
        if "${PODMAN[@]}" ps -a --format '{{.Names}}' 2>/dev/null | grep -qx "$container"; then
            echo "Stopping container $container ..."
            "${PODMAN[@]}" stop "$container" >/dev/null
        else
            echo "Container $container: not running."
        fi
    done

    stop_process_from_pid_file "OIDC counting proxy" "$HA_OIDC_PROXY_PID"
    stop_process_from_pid_file "API 1" "$(ha_pid_file api 1)"
    stop_process_from_pid_file "API 2" "$(ha_pid_file api 2)"
    stop_process_from_pid_file "Web 1" "$(ha_pid_file web 1)"
    stop_process_from_pid_file "Web 2" "$(ha_pid_file web 2)"

    # The generated Traefik config is a temporary file of this stack (and must never linger
    # in the worktree); PID and log files under /tmp stay for inspection.
    rm -rf "$HA_TRAEFIK_CONFIG"

    echo "HA stack stopped. Logs remain in /tmp/blog-ha-*.log for inspection."
}

# (Re)start ONE instance of the stack — the harness stand-in for k8s rescheduling a pod.
# HaTests kill instances and restore what they killed with this, so the next test finds the
# full stack again. The pid file is rewritten by start_app, so `down` and dev/ha-kill.sh keep
# managing exactly the running processes.
start_one() {
    local kind="${1:-}" n="${2:-}"
    case "$kind" in
        api|web) ;;
        *) usage >&2; exit 2 ;;
    esac
    case "$n" in
        1|2) ;;
        *) usage >&2; exit 2 ;;
    esac

    load_dotnet_and_env
    local conn web_conn=""
    conn="$(ha_connection_string)"
    if [ "$HA_NO_WEB_DB" != 1 ]; then
        web_conn="$(ha_web_connection_string)"
    fi

    if [ "$kind" = api ]; then
        start_app api "$n" "$conn"
        wait_for_url "http://localhost:$(ha_port_for api "$n")/health" "API $n" || exit 1
    else
        start_app web "$n" "$conn" "$web_conn"
        wait_for_url "http://localhost:$(ha_port_for web "$n")/" "Web $n" || exit 1
    fi
}

case "${1:-}" in
    up) up ;;
    down) down ;;
    start) shift; start_one "${1:-}" "${2:-}" ;;
    *) usage >&2; exit 2 ;;
esac
