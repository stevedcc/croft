#!/usr/bin/env bash
set -euo pipefail

# Large-data seed for the isolated E2E instance: 400 categories, 3,000 tags, 3,000 published
# posts (each with 1 category and 3 tags) and 600 media items (small generated images).
# Built for brief 03i's large-data tests to run against.
#
# Everything goes through the API as the mock admin (the dev/login.html identity
# admin@example.com -> sub admin-id, groups ["Admin"]), never raw SQL, so the seed exercises
# the same validation as real use. The images are small generated PNGs, written to /tmp and
# uploaded through the media endpoint; the database only ever holds their metadata and file
# references. Requests run in parallel so a full seed is usable.
#
# Usage:  dev/seed-large-data.sh [--instance N]
#   --instance N    the E2E instance to seed (parameters: dev/instance-params.sh; N
#                   defaults to 1, the same default dev/instance-params.sh uses)
#   PARALLELISM     concurrent requests per phase (default 24)
#   MOCK_AUTHORITY  the local mock IdP (default http://localhost:5556/default)
#   MOCK_CLIENT_ID / MOCK_CLIENT_SECRET
#                   OIDC client of the local dev setup (defaults: the dev-only values
#                   from .env.example / dev/login.html)
#
# Target safety — this script can never seed the dev database `blog`:
#   - it never talks to Postgres at all; every write goes through the target instance's API;
#   - the API base URL is derived exclusively from dev/instance-params.sh for `e2e N`
#     (blog_e2e_N's fixed port). An API_BASE_URL from the environment is deliberately
#     ignored;
#   - it refuses to run unless /tmp/blog-e2e-N-api.pid names a live process, i.e. unless
#     the API on that port was started by ./start-local.sh --e2e --instance N (which always
#     runs against blog_e2e_N). The dev stack records /tmp/blog-api.pid instead and can
#     never pass that check.
#
# This seed APPENDS. On a freshly started instance (./start-local.sh --e2e --instance N
# drops and recreates the database and the uploads folder) it loads exactly the counts
# above; run twice and posts and media double (categories and tags are created idempotently
# by name — the API's create returns the existing row unchanged). If a run fails partway,
# restart the instance and run it again so it starts from a clean database.
#
# It prints the counts before and after and how long each phase and the whole seed took.

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/dev/instance-params.sh"

TARGET_CATEGORIES=400
TARGET_TAGS=3000
TARGET_POSTS=3000
TARGET_MEDIA=600
PARALLELISM="${PARALLELISM:-24}"

INSTANCE=1
usage() {
    echo "Usage: dev/seed-large-data.sh [--instance N]"
    echo "  --instance N  E2E instance to seed (dev/instance-params.sh; N defaults to 1)"
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
    ''|*[!0-9]*|0) echo "seed-large-data: --instance needs a positive integer, got '$INSTANCE'" >&2; exit 2 ;;
esac
case "$PARALLELISM" in
    ''|*[!0-9]*|0) echo "seed-large-data: PARALLELISM needs a positive integer, got '$PARALLELISM'" >&2; exit 2 ;;
esac

instance_params e2e "$INSTANCE"
API_BASE_URL="${INSTANCE_API_BASE_URL%/}"

# Target safety (see the header): only a database named blog_e2e_* is ever seeded, and only
# through an API that start-local.sh --e2e started for that instance.
if [[ ! "$INSTANCE_DB" =~ ^blog_e2e_[0-9]+$ ]]; then
    echo "seed-large-data: refusing to seed '$INSTANCE_DB': only blog_e2e_* instances are seedable" >&2
    exit 1
fi
if [ ! -f "$INSTANCE_API_PID" ]; then
    echo "seed-large-data: $INSTANCE_LABEL is not running (no $INSTANCE_API_PID)" >&2
    echo "Start it first: ./start-local.sh --e2e --instance $INSTANCE" >&2
    exit 1
fi
if ! kill -0 "$(cat "$INSTANCE_API_PID")" 2>/dev/null; then
    echo "seed-large-data: stale PID file $INSTANCE_API_PID: $INSTANCE_LABEL is not running" >&2
    echo "Start it first: ./start-local.sh --e2e --instance $INSTANCE" >&2
    exit 1
fi
if ! curl -sf "$API_BASE_URL/health" >/dev/null; then
    echo "seed-large-data: no API answering at $API_BASE_URL" >&2
    echo "Start the instance: ./start-local.sh --e2e --instance $INSTANCE" >&2
    exit 1
fi

MOCK_AUTHORITY="${MOCK_AUTHORITY:-http://localhost:5556/default}"
MOCK_AUTHORITY="${MOCK_AUTHORITY%/}"
MOCK_CLIENT_ID="${MOCK_CLIENT_ID:-blog-web}"
MOCK_CLIENT_SECRET="${MOCK_CLIENT_SECRET:-secret}"
# Never listened on: only the redirect target of the mock's authorization-code flow.
CALLBACK_URL="http://localhost:9876/callback"

TOKEN=""
WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/seed-large-data.XXXXXX")"
trap 'rm -rf "$WORK_DIR"' EXIT

# Small generated PNGs (32x32, one colour each) for the media items; every upload's file is
# generated from one of them at run time, with a distinct file name per item.
PNG_VARIANTS=(
    iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAIAAAD8GO2jAAAAKklEQVR4nGNIKemmKWIYtWDUglELRi0YtWDUglELRi0YtWDUglELhooFACiejEzOqabuAAAAAElFTkSuQmCC
    iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAIAAAD8GO2jAAAAKklEQVR4nGPgndJBU8QwasGoBaMWjFowasGoBaMWjFowasGoBaMWDBULAAYepD3nRepOAAAAAElFTkSuQmCC
    iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAIAAAD8GO2jAAAAKklEQVR4nGO4Wc5GU8QwasGoBaMWjFowasGoBaMWjFowasGoBaMWDBULAL5WWEz7580bAAAAAElFTkSuQmCC
    iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAIAAAD8GO2jAAAAKklEQVR4nGPI1bhJU8QwasGoBaMWjFowasGoBaMWjFowasGoBaMWDBULAOQluEzhmLjdAAAAAElFTkSuQmCC
    iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAIAAAD8GO2jAAAAKklEQVR4nGPYJ2RDU8QwasGoBaMWjFowasGoBaMWjFowasGoBaMWDBULAJEaMD0DhzNsAAAAAElFTkSuQmCC
    iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAIAAAD8GO2jAAAAKklEQVR4nGNgzlxIU8QwasGoBaMWjFowasGoBaMWjFowasGoBaMWDBULAFJGND2ftpJKAAAAAElFTkSuQmCC
    iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAIAAAD8GO2jAAAAKklEQVR4nGPwreGnKWIYtWDUglELRi0YtWDUglELRi0YtWDUglELhooFADcoYC7iFQteAAAAAElFTkSuQmCC
    iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAIAAAD8GO2jAAAAKklEQVR4nGNYmMROU8QwasGoBaMWjFowasGoBaMWjFowasGoBaMWDBULAG5KKD3CRgxaAAAAAElFTkSuQmCC
)

# request "METHOD PATH" curl-args... : run one API call, fail loudly on HTTP errors.
request() {
    local label="$1"
    shift
    local out code resp
    out="$(curl -sS -w $'\n%{http_code}' "$@")" || {
        echo "seed-large-data: $label: request failed" >&2
        exit 1
    }
    code="${out##*$'\n'}"
    resp="${out%$'\n'*}"
    case "$code" in
        [123][0-9][0-9]) ;;
        *)
            echo "seed-large-data: $label failed (HTTP ${code:-none})" >&2
            echo "$resp" >&2
            exit 1 ;;
    esac
    printf '%s' "$resp"
}

# api METHOD PATH [JSON-BODY] : an authenticated API call; prints the response body.
api() {
    local method="$1" path="$2" body="${3:-}"
    local args=(-X "$method" -H "Authorization: Bearer $TOKEN")
    if [ -n "$body" ]; then
        args+=(-H 'Content-Type: application/json' -d "$body")
    fi
    request "$method $path" "${args[@]}" "$API_BASE_URL$path"
}

# The mock-IdP admin-token flow (curl code flow + token exchange), same as
# dev/seed-dev-data.sh: GET the mock's dev login page, POST the admin identity from
# dev/login.html, exchange the code for a token.
get_admin_token() {
    local authorize_url="$MOCK_AUTHORITY/authorize?response_type=code&client_id=$MOCK_CLIENT_ID&redirect_uri=$CALLBACK_URL&scope=openid&state=seed"
    local jar="$WORK_DIR/jar" headers="$WORK_DIR/headers" claims code response
    claims="$(jq -nc '{sub:"admin-id", name:"Admin User", email:"admin@example.com", email_verified:true, groups:["Admin"]}')"

    request "GET $MOCK_AUTHORITY/authorize" -c "$jar" -b "$jar" -o /dev/null "$authorize_url" >/dev/null
    request "POST $MOCK_AUTHORITY/authorize" -c "$jar" -b "$jar" -o /dev/null -D "$headers" \
        --data-urlencode "username=admin@example.com" \
        --data-urlencode "claims=$claims" \
        "$authorize_url" >/dev/null

    code="$(grep -io 'code=[^&]*' "$headers" | head -n 1 | sed 's/^[^=]*=//')" || code=""
    if [ -z "$code" ]; then
        echo "seed-large-data: no authorization code in the mock's redirect ($headers):" >&2
        cat "$headers" >&2
        exit 1
    fi

    response="$(request "POST $MOCK_AUTHORITY/token" -c "$jar" -b "$jar" \
        --data-urlencode "grant_type=authorization_code" \
        --data-urlencode "code=$code" \
        --data-urlencode "client_id=$MOCK_CLIENT_ID" \
        --data-urlencode "client_secret=$MOCK_CLIENT_SECRET" \
        --data-urlencode "redirect_uri=$CALLBACK_URL" \
        "$MOCK_AUTHORITY/token")"
    TOKEN="$(jq -r '.access_token // empty' <<<"$response")"
    if [ -z "$TOKEN" ]; then
        echo "seed-large-data: no access token in the token response: $response" >&2
        exit 1
    fi
}

# counts_line : the seeded quantities, fetched back through the API.
counts_line() {
    local cats tags posts published media
    cats="$(api GET /api/categories | jq 'length')"
    tags="$(api GET /api/tags | jq 'length')"
    posts="$(api GET '/api/posts?pageSize=1' | jq -r .totalCount)"
    published="$(api GET '/api/posts?pageSize=1&status=Published' | jq -r .totalCount)"
    media="$(api GET /api/media | jq 'length')"
    printf '%s categories, %s tags, %s posts (%s published), %s media items' \
        "$cats" "$tags" "$posts" "$published" "$media"
}

# run_parallel PARALLELISM FUNC COUNT : split 1..COUNT into PARALLELISM contiguous chunks
# and run FUNC FROM TO per chunk as background workers; fail if any worker fails. The
# workers are plain background subshells, so they see TOKEN and the maps below unchanged.
run_parallel() {
    local par="$1" func="$2" count="$3"
    local step start end pid rc=0
    local -a pids=()
    step=$(( (count + par - 1) / par ))
    for (( start = 1; start <= count; start += step )); do
        end=$(( start + step - 1 ))
        if [ "$end" -gt "$count" ]; then
            end=$count
        fi
        "$func" "$start" "$end" &
        pids+=("$!")
    done
    for pid in "${pids[@]}"; do
        wait "$pid" || rc=1
    done
    return "$rc"
}

# run_phase LABEL FUNC COUNT : run one seeding phase and time it; a failed phase aborts
# with instructions to restart the instance (the database then holds partial data).
run_phase() {
    local label="$1" func="$2" count="$3"
    local started="$SECONDS"
    printf '  %-62s' "$label"
    if run_parallel "$PARALLELISM" "$func" "$count"; then
        echo "done in $((SECONDS - started))s"
    else
        echo "FAILED after $((SECONDS - started))s"
        echo "seed-large-data: $label failed; $INSTANCE_DB now holds partial data." >&2
        echo "Restart the instance and run the seed again:" >&2
        echo "  ./stop-local.sh --e2e --instance $INSTANCE && ./start-local.sh --e2e --instance $INSTANCE" >&2
        exit 1
    fi
}

# create_categories FROM TO : create "Seed Category NNNN" (the API's create is idempotent
# by name: an existing category comes back unchanged).
create_categories() {
    local i
    for (( i = $1; i <= $2; i++ )); do
        api POST /api/categories "$(printf '"Seed Category %04d"' "$i")" >/dev/null
    done
}

# create_tags FROM TO : create "Seed Tag NNNN" (idempotent by name, like categories).
create_tags() {
    local i
    for (( i = $1; i <= $2; i++ )); do
        api POST /api/tags "$(printf '"Seed Tag %04d"' "$i")" >/dev/null
    done
}

declare -A CATEGORY_IDS

# build_category_ids : name -> id map for the seeded categories, fetched once through the
# API; posts reference their category by id.
build_category_ids() {
    local name id
    while IFS=$'\t' read -r name id; do
        CATEGORY_IDS["$name"]="$id"
    done < <(api GET /api/categories | jq -r '.[] | [.name, .id] | @tsv')
}

# create_posts FROM TO : each post is created as a draft and then published with a past
# time (PostService turns a past publish time into PostStatus.Published). Assignment is
# deterministic: post i gets category ((i-1) % 400) + 1 and tags ((i-1) % 3000) + 1,
# ((i-1+1000) % 3000) + 1 and ((i-1+2000) % 3000) + 1 — always 3 distinct tags, every
# category and tag used. Publish times are staggered one minute apart going back from the
# start of the run, so the newest-first order of the seeded posts is stable.
create_posts() {
    local i cat_name cat_id tag1 tag2 tag3 published payload id
    for (( i = $1; i <= $2; i++ )); do
        cat_name="$(printf 'Seed Category %04d' $(( (i - 1) % TARGET_CATEGORIES + 1 )))"
        cat_id="${CATEGORY_IDS[$cat_name]:-}"
        if [ -z "$cat_id" ]; then
            echo "seed-large-data: category '$cat_name' is missing; cannot seed post $i" >&2
            exit 1
        fi
        tag1="$(printf 'Seed Tag %04d' $(( (i - 1) % TARGET_TAGS + 1 )))"
        tag2="$(printf 'Seed Tag %04d' $(( (i - 1 + 1000) % TARGET_TAGS + 1 )))"
        tag3="$(printf 'Seed Tag %04d' $(( (i - 1 + 2000) % TARGET_TAGS + 1 )))"
        published="$(date -u -d "@$(( BASE_EPOCH - 60 * i ))" +%Y-%m-%dT%H:%M:%SZ)"
        payload="$(jq -nc \
            --arg title "$(printf 'Seed Post %04d' "$i")" \
            --arg md "$(printf 'Large-data seed post %04d.\n\nA generated post body for load testing: enough text to look real in listings and on detail pages without slowing the seed down.\n\n- generated content, one of 3,000 seeded posts\n- written through the API by dev/seed-large-data.sh\n' "$i")" \
            --arg cat "$cat_id" \
            --args \
            '{title:$title, markdownContent:$md,
              categoryId:(if $cat == "" then null else $cat end),
              tags:$ARGS.positional}' "$tag1" "$tag2" "$tag3")"
        id="$(api POST /api/posts "$payload" | jq -r .id)"
        api POST "/api/posts/$id/publish" "$(printf '{"publishedAt":"%s"}' "$published")" >/dev/null
    done
}

# upload_media FROM TO : upload small generated PNGs through the media endpoint under
# distinct file names (seed-image-NNNN.png). The file bytes never touch the database.
upload_media() {
    local i png name
    png="$WORK_DIR/image-$1.png"
    for (( i = $1; i <= $2; i++ )); do
        printf '%s' "${PNG_VARIANTS[$(( (i - 1) % ${#PNG_VARIANTS[@]} ))]}" | base64 -d >"$png"
        name="$(printf 'seed-image-%04d.png' "$i")"
        request "POST /api/media" -X POST -H "Authorization: Bearer $TOKEN" \
            -F "file=@$png;filename=$name;type=image/png" \
            -F "scope=Global" \
            "$API_BASE_URL/api/media" >/dev/null
    done
}

echo "Seeding large data into $INSTANCE_LABEL (database $INSTANCE_DB) through $API_BASE_URL"
echo "  as the mock admin; parallelism $PARALLELISM"
echo "  (this appends: a freshly started instance gets exactly the target counts)"
get_admin_token

SECONDS=0
echo "Before: $(counts_line)"

BASE_EPOCH="$(date -u +%s)"

run_phase "400 categories ..." create_categories "$TARGET_CATEGORIES"
build_category_ids
run_phase "3,000 tags ..." create_tags "$TARGET_TAGS"
run_phase "3,000 published posts (1 category + 3 tags each) ..." create_posts "$TARGET_POSTS"
run_phase "600 media items (small generated PNGs) ..." upload_media "$TARGET_MEDIA"

echo "After:  $(counts_line)"
echo "Done in ${SECONDS}s."
