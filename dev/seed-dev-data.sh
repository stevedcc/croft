#!/usr/bin/env bash
set -euo pipefail

# Loads a small, realistic sample into the database behind a RUNNING local stack, for
# working on the site by hand:
#
#   - 3 categories with icons (Programming, Photography, Field Notes)
#   - 6 tags
#   - 8 published posts (one with a banner image and code blocks, one with a table)
#   - 1 draft
#   - 1 scheduled post (its publish time is in the future, UTC, so it stays scheduled)
#
# Everything goes through the API as the mock admin (the dev/login.html identity
# admin@example.com -> sub admin-id, groups ["Admin"]), never raw SQL, so the seed
# exercises the same validation as real use. The banner image is uploaded through the
# media endpoint at run time (a small generated PNG in /tmp); the database only ever holds
# its metadata and file reference.
#
# Idempotent: every piece is looked up through the API first (categories and tags by name,
# posts by title, the image by its original file name) and only created when missing.
# Running it twice leaves counts and content identical, and it never overwrites edits made
# by hand. A piece that exists but is incomplete (a category without its icon, an image
# without its alt text) gets only the missing part filled in.
#
# Usage:  dev/seed-dev-data.sh
#   API_BASE_URL      the stack's API to seed (default http://localhost:5000; use
#                     http://localhost:5010 for E2E instance 2, etc.)
#   MOCK_AUTHORITY    the local mock IdP (default http://localhost:5556/default)
#   MOCK_CLIENT_ID / MOCK_CLIENT_SECRET
#                     OIDC client of the local dev setup (defaults: the dev-only values
#                     from .env.example / dev/login.html)

API_BASE_URL="${API_BASE_URL:-http://localhost:5000}"
API_BASE_URL="${API_BASE_URL%/}"
MOCK_AUTHORITY="${MOCK_AUTHORITY:-http://localhost:5556/default}"
MOCK_AUTHORITY="${MOCK_AUTHORITY%/}"
MOCK_CLIENT_ID="${MOCK_CLIENT_ID:-blog-web}"
MOCK_CLIENT_SECRET="${MOCK_CLIENT_SECRET:-secret}"
# Never listened on: only the redirect target of the mock's authorization-code flow.
CALLBACK_URL="http://localhost:9876/callback"

TOKEN=""
CREATED=0
REUSED=0

# request "METHOD PATH" curl-args... : run one API call, fail loudly on HTTP errors.
request() {
    local label="$1"
    shift
    local out code resp
    out="$(curl -sS -w $'\n%{http_code}' "$@")" || {
        echo "seed-dev-data: $label: request failed" >&2
        exit 1
    }
    code="${out##*$'\n'}"
    resp="${out%$'\n'*}"
    case "$code" in
        [123][0-9][0-9]) ;;
        *)
            echo "seed-dev-data: $label failed (HTTP ${code:-none})" >&2
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

# The mock-IdP admin-token flow (curl code flow + token exchange): GET the mock's dev
# login page, POST the admin identity from dev/login.html, exchange the code for a token.
get_admin_token() {
    local authorize_url="$MOCK_AUTHORITY/authorize?response_type=code&client_id=$MOCK_CLIENT_ID&redirect_uri=$CALLBACK_URL&scope=openid&state=seed"
    local jar headers claims code response
    jar="$(mktemp)"
    headers="$(mktemp)"
    claims="$(jq -nc '{sub:"admin-id", name:"Admin User", email:"admin@example.com", email_verified:true, groups:["Admin"]}')"

    request "GET $MOCK_AUTHORITY/authorize" -c "$jar" -b "$jar" -o /dev/null "$authorize_url" >/dev/null
    request "POST $MOCK_AUTHORITY/authorize" -c "$jar" -b "$jar" -o /dev/null -D "$headers" \
        --data-urlencode "username=admin@example.com" \
        --data-urlencode "claims=$claims" \
        "$authorize_url" >/dev/null

    code="$(grep -io 'code=[^&]*' "$headers" | head -n 1 | sed 's/^[^=]*=//')" || code=""
    if [ -z "$code" ]; then
        echo "seed-dev-data: no authorization code in the mock's redirect ($headers):" >&2
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
        echo "seed-dev-data: no access token in the token response: $response" >&2
        exit 1
    fi
    rm -f "$jar" "$headers"
}

# All posts of any status (the admin sees drafts and scheduled posts too), paged.
fetch_all_posts() {
    local page=1 out="[]" resp total
    while :; do
        resp="$(api GET "/api/posts?page=$page&pageSize=100")"
        out="$(jq -c --argjson r "$resp" '. + $r.items' <<<"$out")"
        total="$(jq -r '.totalCount' <<<"$resp")"
        if [ "$(jq 'length' <<<"$out")" -ge "$total" ]; then
            break
        fi
        page=$((page + 1))
    done
    printf '%s' "$out"
}

# ensure_category NAME ICON : create the category and set its icon, or fill in a missing
# icon on an existing one. Prints the category id.
ensure_category() {
    local name="$1" icon="$2" list id
    list="$(api GET /api/categories)"
    id="$(jq -r --arg n "$name" '[.[] | select(.name == $n)][0].id // empty' <<<"$list")"
    if [ -n "$id" ]; then
        REUSED=$((REUSED + 1))
        if [ -z "$(jq -r --arg n "$name" '[.[] | select(.name == $n)][0].icon // empty' <<<"$list")" ]; then
            api PUT "/api/categories/$id" "$(jq -nc --arg n "$name" --arg i "$icon" '{name:$n, icon:$i}')" >/dev/null
            echo "  category '$name': exists; set its missing icon '$icon'"
        else
            echo "  category '$name': exists"
        fi
    else
        id="$(api POST /api/categories "$(jq -nc --arg n "$name" '$n')" | jq -r .id)"
        api PUT "/api/categories/$id" "$(jq -nc --arg n "$name" --arg i "$icon" '{name:$n, icon:$i}')" >/dev/null
        CREATED=$((CREATED + 1))
        echo "  category '$name': created (icon '$icon')"
    fi
    CATEGORY_ID="$id"
}

CATEGORY_ID=""

# ensure_tag NAME : create the tag unless it already exists.
ensure_tag() {
    local name="$1" id
    id="$(api GET /api/tags | jq -r --arg n "$name" '[.[] | select(.name == $n)][0].id // empty')"
    if [ -n "$id" ]; then
        REUSED=$((REUSED + 1))
        echo "  tag '$name': exists"
    else
        api POST /api/tags "$(jq -nc --arg n "$name" '$n')" >/dev/null
        CREATED=$((CREATED + 1))
        echo "  tag '$name': created"
    fi
}

BANNER_ID=""

# ensure_banner : upload the sample banner image (a small generated PNG in /tmp) through
# the media endpoint unless an asset with its original file name already exists; sets the
# alt text when missing. The file bytes never touch the database.
ensure_banner() {
    local png
    png="$(mktemp "${TMPDIR:-/tmp}/seed-dev-banner.XXXXXX.png")"
    base64 -d >"$png" <<'PNG_B64'
iVBORw0KGgoAAAANSUhEUgAAAUAAAAC0CAIAAABqhmJGAAABnElEQVR42u3TUQkAMAhAQTutxT7M
YYalWkRrKBxcggcvThawVEgABgYMDBgYDAwYGDAwYGAwMGBgwMBgYMDAgIEBA4OBAQMDBgYMDAYG
DAwYGAwMGBgwMGBgMDBgYMDAgIHBwICBAQODgQEDAwYGDAwGBgwMGBgMrAIYGDAwYGAwMGBgwMCA
gcHAgIEBA4OBAQMDBgYMDAYGDAwYGDAwGBgwMGBgMDBgYMDAgIHBwICBAQMDBgYDAwYGDAwGBgwM
GBgwMBgYMDBgYDAwYGDAwICBwcCAgQEDAwYGAwMDBv7vAksZGAwMGBgwMBgYMDBgYMDAYGDAwICB
wcCAgQEDAwYGAwMGBgwMGBgMDBgYMDAYGDAwYGDAwGBgwMCAgQEDg4EBAwMGBgMDBgYMDBgYDAwY
GDAwGFgFMDBgYMDAYGDAwICBAQODgQEDAwYGAwMGBgwMGBgMDBgYMDBgYDAwYGDAwGBgwMCAgQED
g4EBAwMGBgwMBgYMDBgYDAwYGDAwYGAwMGBgwMBgYMDAgIEBA4OBAQMDBgYMDAYGBmjYxO4I3Tx2
0wAAAABJRU5ErkJggg==
PNG_B64

    local list id
    list="$(api GET /api/media)"
    id="$(jq -r --arg n "$BANNER_NAME" '[.[] | select(.originalName == $n)][0].id // empty' <<<"$list")"
    if [ -n "$id" ]; then
        REUSED=$((REUSED + 1))
        if [ -z "$(jq -r --arg n "$BANNER_NAME" '[.[] | select(.originalName == $n)][0].altText // empty' <<<"$list")" ]; then
            api PUT "/api/media/$id" "$(jq -nc --arg a "$BANNER_ALT" '{altText:$a}')" >/dev/null
            echo "  media '$BANNER_NAME': exists; set its missing alt text"
        else
            echo "  media '$BANNER_NAME': exists"
        fi
    else
        id="$(request "POST /api/media" -X POST -H "Authorization: Bearer $TOKEN" \
            -F "file=@$png;filename=$BANNER_NAME;type=image/png" \
            -F "scope=Global" \
            "$API_BASE_URL/api/media" | jq -r .id)"
        api PUT "/api/media/$id" "$(jq -nc --arg a "$BANNER_ALT" '{altText:$a}')" >/dev/null
        CREATED=$((CREATED + 1))
        echo "  media '$BANNER_NAME': uploaded"
    fi
    rm -f "$png"
    BANNER_ID="$id"
}

# ensure_post TITLE CATEGORY PUBLISH_AT SUBTITLE BODY [TAG ...]
#   PUBLISH_AT empty -> leave as draft; a past/now UTC instant -> published; a future one
#   -> scheduled (PostService turns a future publish time into PostStatus.Scheduled).
ensure_post() {
    local title="$1" category="$2" publish_at="$3" subtitle="$4" body="$5"
    shift 5
    local tags=("$@")
    local id
    id="$(jq -r --arg t "$title" '[.[] | select(.title == $t)][0].id // empty' <<<"$ALL_POSTS")"
    if [ -n "$id" ]; then
        REUSED=$((REUSED + 1))
        echo "  post '$title': exists"
        return 0
    fi

    local cat_id banner_arg payload
    cat_id="$(api GET /api/categories | jq -r --arg n "$category" '[.[] | select(.name == $n)][0].id // empty')"
    banner_arg=""
    case "$title" in
        "$BANNER_POST_TITLE") banner_arg="$BANNER_ID" ;;
    esac
    payload="$(jq -nc --arg title "$title" --arg md "$body" --arg sub "$subtitle" \
        --arg cat "$cat_id" --arg banner "$banner_arg" --args \
        '{title:$title, markdownContent:$md,
          subtitle:(if $sub == "" then null else $sub end),
          categoryId:(if $cat == "" then null else $cat end),
          bannerImageId:(if $banner == "" then null else $banner end),
          tags:$ARGS.positional}' "${tags[@]}")"
    id="$(api POST /api/posts "$payload" | jq -r .id)"
    if [ -n "$publish_at" ]; then
        api POST "/api/posts/$id/publish" "$(jq -nc --arg t "$publish_at" '{publishedAt:$t}')" >/dev/null
    fi
    CREATED=$((CREATED + 1))
    echo "  post '$title': created"
}

BANNER_NAME="blog-banner.png"
BANNER_ALT="Slate over warm rock — the generated sample banner"
BANNER_POST_TITLE="Getting started with Blazor Server"

echo "Seeding the sample data through $API_BASE_URL as the mock admin ..."
get_admin_token

echo "Categories:"
ensure_category "Programming" "Code"
ensure_category "Photography" "CameraAlt"
ensure_category "Field Notes" "EditNote"

echo "Tags:"
ensure_tag "C#"
ensure_tag "Blazor"
ensure_tag "PostgreSQL"
ensure_tag "Docker"
ensure_tag "Testing"
ensure_tag "Workflow"

echo "Media:"
ensure_banner

# Publish times, all UTC: the eight published posts are staggered over the last week so
# the listing order is stable and realistic; the scheduled post is a month out so it stays
# Scheduled (its publish time is in the future) no matter when the sample is loaded.
P8="$(date -u -d '1 day ago'  +%Y-%m-%dT%H:%M:%SZ)"
P7="$(date -u -d '2 days ago' +%Y-%m-%dT%H:%M:%SZ)"
P6="$(date -u -d '3 days ago' +%Y-%m-%dT%H:%M:%SZ)"
P5="$(date -u -d '4 days ago' +%Y-%m-%dT%H:%M:%SZ)"
P4="$(date -u -d '5 days ago' +%Y-%m-%dT%H:%M:%SZ)"
P3="$(date -u -d '6 days ago' +%Y-%m-%dT%H:%M:%SZ)"
P2="$(date -u -d '7 days ago' +%Y-%m-%dT%H:%M:%SZ)"
P1="$(date -u -d '8 days ago' +%Y-%m-%dT%H:%M:%SZ)"
S1="$(date -u -d '30 days'    +%Y-%m-%dT%H:%M:%SZ)"

BODY_BLazor='Blazor Server keeps the whole application on the server and streams UI changes
over a SignalR connection. This is the shortest path I know from an empty folder to
something running in a browser.

## Set it up

```bash
dotnet new blazor -n Scratch --interactivity Server
cd Scratch && dotnet run
```

The counter sample is enough to prove the circuit works. From there, register a service
and inject it into a component:

```csharp
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);

@inject TimeProvider Clock
<p>It is @Clock.GetUtcNow().ToString("u")</p>
```

Everything after that is ordinary dependency injection. Keep components small: pages
fetch data, components present it.'

BODY_TABLE='A handful of PostgreSQL patterns I keep reaching for. None of them are clever;
all of them earn their keep within a week.

| Pattern | When to use it | Cost |
| --- | --- | --- |
| `timestamptz` everywhere | Always | None |
| Partial unique index | A few rows must be unique | Low |
| `citext` column | Case-insensitive lookups | Low |
| Statement logging | Debugging locally | Temporary |

Store every point in time as `timestamp with time zone` and normalise it to UTC before it
reaches the driver. The rest is ordinary modelling.'

BODY_DOCKER='My dev stack is two containers: PostgreSQL and a mock OIDC server. One compose
file, no surprises.

I keep the compose file next to the code it serves, so a fresh checkout can run
`docker compose up -d` and nothing else. The volumes hold the database; everything else
is disposable.

The same file backs local testing. The test suite spins up its own PostgreSQL per run, but
the schema comes from the same migrations, so what runs here is what runs in CI.'

BODY_TESTS='A test that passes only when the database is in the right mood is not a test,
it is a rumour. Every test should build the world it needs.

Two rules do most of the work: create the data you assert on, and assert outcomes rather
than the presence of markup. A broken image is still "visible" to a browser.

The third rule is the hardest — when something fails, find out why before touching it. A
flaky test with a longer timeout is a broken test with better manners.'

BODY_SENJA='We drove north with three days of forecast and none of them were right. The
light on Senja arrives without warning and leaves the same way.

Most of the good frames came from stopping the car on a whim. The planned shots, at the
planned times, were the least interesting of the trip.

I came back with fewer keepers than expected and a notebook full of "next time".'

BODY_GIT='My git workflow got calmer when I stopped pretending I would remember why a
branch existed.

Small commits, honest messages, and a rebase only when nothing is shared. Everything
else is ceremony I relearn every year.

The habit that stuck: writing the commit message before writing the code. It does not
survive contact with reality unchanged, but it names the thing I am trying to do.'

BODY_WALKS='The best photo of last year came from a walk I nearly skipped. Nine
kilometres, four frames, one keeper.

Carrying less gear helps. So does leaving the camera off until something asks to be
photographed.

Fewer photos, longer looks.'

BODY_NOTES='A year of daily notes taught me less about note-taking than about what I
actually do all day.

Plain text outlasted every app I tried. One folder, one file per day, and a greppable
format beats a clever one.

The notes I reread are the ones written in sentences, not bullets. Future me wants the
story, not the fragments.'

BODY_DRAFT='Rough notes for the autumn walk — nothing here is fixed.

- route along the ridge, start before sunrise
- bring the small tripod, leave the zoom at home
- write down the light, not just the locations

TODO: check the ferry times, and whether the cabin is open in October.'

BODY_SCHEDULED='The plan for the winter is deliberately small: one project finished beats
three started.

Top of the list is rebuilding the photo archive. Everything after that is a bonus.'

ALL_POSTS="$(fetch_all_posts)"
echo "Posts:"
ensure_post "$BANNER_POST_TITLE" "Programming" "$P1" \
    "The shortest path from an empty folder to a running app" "$BODY_BLazor" "Blazor" "C#"
ensure_post "PostgreSQL patterns I keep reusing" "Programming" "$P2" \
    "" "$BODY_TABLE" "PostgreSQL"
ensure_post "A small Docker Compose stack for .NET" "Programming" "$P3" \
    "" "$BODY_DOCKER" "Docker"
ensure_post "Writing tests that stay green" "Programming" "$P4" \
    "" "$BODY_TESTS" "Testing" "C#"
ensure_post "Chasing light on Senja" "Photography" "$P5" "" "$BODY_SENJA"
ensure_post "A calmer git workflow" "Field Notes" "$P6" "" "$BODY_GIT" "Workflow"
ensure_post "Long walks, few photos" "Photography" "$P7" "" "$BODY_WALKS"
ensure_post "What I learned from a year of notes" "Field Notes" "$P8" "" "$BODY_NOTES" "Workflow"
ensure_post "Ideas for the autumn photo walk" "Photography" "" "" "$BODY_DRAFT"
ensure_post "Planning the winter projects" "Field Notes" "$S1" "" "$BODY_SCHEDULED" "Workflow"

echo ""
echo "Sample data ready against $API_BASE_URL (created $CREATED, reused $REUSED):"
echo "  categories: $(api GET /api/categories | jq 'length') (Programming/Code, Photography/CameraAlt, Field Notes/EditNote)"
echo "  tags:       $(api GET /api/tags | jq 'length')"
POSTS_NOW="$(fetch_all_posts)"
echo "  posts:      $(jq '[.[] | select(.status == 2 or .status == "Published")] | length' <<<"$POSTS_NOW") published, $(jq '[.[] | select(.status == 0 or .status == "Draft")] | length' <<<"$POSTS_NOW") draft, $(jq '[.[] | select(.status == 1 or .status == "Scheduled")] | length' <<<"$POSTS_NOW") scheduled"
echo "Done."
