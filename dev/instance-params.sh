# The single source of truth for local stack instance parameters: database name, uploads
# folder, ports, PID and log files. Sourced by start-local.sh and stop-local.sh; no other
# file hard-codes these values (the E2E suite's instance-1 URL defaults in
# tests/Croft.Web.E2E/WebTest.cs are the documented exception).
#
# Instance model:
#   dev    the shared dev stack: database from .env (blog), uploads folder from .env
#          (./uploads), API 5000 / Web 5002, PID and log files /tmp/blog-{api,web}.{pid,log}.
#   e2e N  the E2E suite's isolated instance N (N defaults to 1): database blog_e2e_N, the
#          Web app's database blog_e2e_N_web (sessions live in its UserSessions table, so a
#          test can end a session the way the purge does), an
#          uploads folder /tmp/blog-e2e-N-uploads (emptied on every start), PID and log
#          files /tmp/blog-e2e-N-{api,web}.{pid,log}.
#
# Ports: instance 1 keeps the dev ports (API 5000 / Web 5002, so it cannot run alongside the
# dev stack). Instance N > 1 uses a fixed offset of 10 ports per instance: API 5000 + 10*(N-1),
# Web 5002 + 10*(N-1) — instance 2 is API 5010 / Web 5012, instance 3 is 5020/5022, and so on.
# Instances therefore never collide with each other or with the dev ports.

# PID file naming; e2e_instance_pid_glob derives its glob from the same prefixes.
DEV_API_PID=/tmp/blog-api.pid
DEV_WEB_PID=/tmp/blog-web.pid
E2E_PID_PREFIX=/tmp/blog-e2e

# The database name inside a connection string ("...;Database=blog;...").
conn_db_name() {
    printf '%s' "$1" | sed -En 's/.*database=([^;]*).*/\1/Ip'
}

# Usage: instance_params dev | instance_params e2e [N]
# Sets INSTANCE_MODE, INSTANCE_N, INSTANCE_DB, INSTANCE_CONN, INSTANCE_WEB_DB,
# INSTANCE_WEB_CONN (e2e only: the Web app's own database, see below), INSTANCE_UPLOADS,
# INSTANCE_API_PORT, INSTANCE_WEB_PORT, INSTANCE_API_BASE_URL, INSTANCE_API_PID,
# INSTANCE_WEB_PID, INSTANCE_API_LOG, INSTANCE_WEB_LOG, INSTANCE_LABEL and
# INSTANCE_CSP_REPORT_ONLY, INSTANCE_READ_HOST and INSTANCE_SOCIAL_ENABLED in the caller.
# Requires .env to be loaded (ConnectionStrings__BlogDb) for anything that touches the
# database or the apps; stop-local.sh only needs the PID files.
instance_params() {
    local mode="${1:?usage: instance_params dev|e2e [N]}"
    local n="${2:-1}"

    INSTANCE_MODE="$mode"
    INSTANCE_N="$n"
    INSTANCE_API_PORT=$((5000 + 10 * (n - 1)))
    INSTANCE_WEB_PORT=$((5002 + 10 * (n - 1)))

    if [ "$mode" = dev ]; then
        INSTANCE_DB="$(conn_db_name "${ConnectionStrings__BlogDb:-}")"
        INSTANCE_CONN="${ConnectionStrings__BlogDb:-}"
        INSTANCE_UPLOADS="${FileStorage__BasePath:-./uploads}"
        INSTANCE_API_BASE_URL="${ApiBaseUrl:-http://localhost:$INSTANCE_API_PORT/}"
        INSTANCE_API_PID="$DEV_API_PID"
        INSTANCE_WEB_PID="$DEV_WEB_PID"
        INSTANCE_API_LOG=/tmp/blog-api.log
        INSTANCE_WEB_LOG=/tmp/blog-web.log
        INSTANCE_LABEL="Local dev stack"
        # The dev stack keeps the app's Csp:ReportOnly default (enforced): empty means the
        # variable is not passed at all. Export Csp__ReportOnly=true before starting for a
        # report-only rehearsal of a site's rollout.
        INSTANCE_CSP_REPORT_ONLY=""
        # The dev stack reads from the one database; only E2E instances run a read connection.
        INSTANCE_READ_HOST=""
        INSTANCE_SOCIAL_ENABLED=""
        return 0
    fi

    if [ "$mode" != e2e ]; then
        echo "instance_params: unknown mode '$mode' (want dev or e2e)" >&2
        return 2
    fi

    INSTANCE_DB="blog_e2e_$n"
    INSTANCE_UPLOADS="$E2E_PID_PREFIX-$n-uploads"
    INSTANCE_API_BASE_URL="http://localhost:$INSTANCE_API_PORT/"
    INSTANCE_API_PID="$E2E_PID_PREFIX-$n-api.pid"
    INSTANCE_WEB_PID="$E2E_PID_PREFIX-$n-web.pid"
    INSTANCE_API_LOG="$E2E_PID_PREFIX-$n-api.log"
    INSTANCE_WEB_LOG="$E2E_PID_PREFIX-$n-web.log"
    INSTANCE_LABEL="E2E instance $n"
    # The CSP mode of this instance's Web app (ruling R11): E2E instances run the app's
    # Csp:ReportOnly default — enforced — so a plain dev/run-e2e.sh run measures the enforced
    # policy with no further wiring. An explicit Csp__ReportOnly still passes through
    # (dev/run-e2e.sh --csp-enforced pins false; a rollout rehearsal can export true).
    INSTANCE_CSP_REPORT_ONLY="${Csp__ReportOnly:-}"

    # The discussion under a post (brief 12b): E2E instances run with Social:Enabled on and NO Mastodon
    # server and NO Bluesky DID (start-local.sh pins both empty), so the API shows the replies the tests
    # stored and its fetcher runs for no platform: no outbound request, ever. The dev stack keeps the
    # app's default (off).
    INSTANCE_SOCIAL_ENABLED=true

    # Same Postgres, same credentials as dev; only the database name changes. Refuse to
    # derive anything when the result would not target blog_e2e_N exactly (e.g. a connection
    # string without a Database= segment, which Npgsql would resolve to the dev database).
    if [ -n "${ConnectionStrings__BlogDb:-}" ]; then
        INSTANCE_CONN="$(printf '%s' "$ConnectionStrings__BlogDb" | sed -E "s/(database=)[^;]*/\\1$INSTANCE_DB/I")"
        if [ "$(conn_db_name "$INSTANCE_CONN")" != "$INSTANCE_DB" ]; then
            echo "instance_params: refusing to derive an E2E connection string that does not target $INSTANCE_DB" >&2
            return 1
        fi
    else
        INSTANCE_CONN=""
    fi

    # The Web app's own database (ConnectionStrings:WebDb): token sessions and Data Protection
    # keys. A second database beside blog_e2e_N, because the two contexts keep separate
    # migration histories.
    INSTANCE_WEB_DB="${INSTANCE_DB}_web"
    if [ -n "$INSTANCE_CONN" ]; then
        INSTANCE_WEB_CONN="$(printf '%s' "$INSTANCE_CONN" | sed -E "s/(database=)[^;]*/\1$INSTANCE_WEB_DB/I")"
        if [ "$(conn_db_name "$INSTANCE_WEB_CONN")" != "$INSTANCE_WEB_DB" ]; then
            echo "instance_params: refusing to derive a Web connection string that does not target $INSTANCE_WEB_DB" >&2
            return 1
        fi
    else
        INSTANCE_WEB_CONN=""
    fi

    # Every E2E instance routes public reads through the read connection (Database:ReadHost), so the
    # E2E suite exercises the production read path instead of one primary connection: the same
    # server under a second address ("localhost" becomes 127.0.0.1), which gives the API a read
    # connection of its own (the read Application Name, a read-only default transaction) without a
    # replica. ReadYourWritesTests asserts the premise from the API's startup log.
    INSTANCE_READ_HOST=""
    if [ -n "$INSTANCE_CONN" ]; then
        INSTANCE_READ_HOST="$(printf '%s' "$INSTANCE_CONN" | sed -En 's/(^|.*;)[[:space:]]*host=([^;]*).*/\2/Ip')"
        if [ "$INSTANCE_READ_HOST" = localhost ]; then
            INSTANCE_READ_HOST=127.0.0.1
        fi
    fi
    return 0
}

# Every E2E instance's PID files, existing or not (stop-local.sh --all skips missing ones).
e2e_instance_pid_glob() {
    echo "$E2E_PID_PREFIX-*-api.pid $E2E_PID_PREFIX-*-web.pid"
}
