#!/usr/bin/env bash
set -euo pipefail

# Deterministic fixture seed for the screenshot gate (item 7, ruling R17): the same fixed-name,
# fixed-date content on both sides of the comparison, created through the API as the mock admin
# (dev/seed-dev-data.sh's token flow), never raw SQL. Every value is a constant — titles, body
# text, tag and category names, publish times (fixed past UTC instants), the banner file
# (tests/Croft.Web.E2E/test-image.png, byte-identical in every checkout) — so two seeds on two
# fresh databases produce content that renders identically. Run-id text would make the captures
# incomparable; none is used here.
#
# Usage:  API_BASE_URL=http://localhost:5010 GATE_OUT=/tmp/opencode/screenshot-gate/side dev/screenshot-gate/seed.sh
#   API_BASE_URL      the stack's API to seed (default http://localhost:5000)
#   GATE_OUT          where to write fixtures.json for the capture step (default /tmp/opencode/screenshot-gate/out)
#   MOCK_AUTHORITY    the local mock IdP (default http://localhost:5556/default)
#   MOCK_CLIENT_ID / MOCK_CLIENT_SECRET
#                     OIDC client of the local dev setup (defaults: the dev-only values
#                     from .env.example / dev/login.html)

API_BASE_URL="${API_BASE_URL:-http://localhost:5000}"
API_BASE_URL="${API_BASE_URL%/}"
GATE_OUT="${GATE_OUT:-/tmp/opencode/screenshot-gate/out}"
MOCK_AUTHORITY="${MOCK_AUTHORITY:-http://localhost:5556/default}"
MOCK_AUTHORITY="${MOCK_AUTHORITY%/}"
MOCK_CLIENT_ID="${MOCK_CLIENT_ID:-blog-web}"
MOCK_CLIENT_SECRET="${MOCK_CLIENT_SECRET:-secret}"
# Never listened on: only the redirect target of the mock's authorization-code flow.
CALLBACK_URL="http://localhost:9876/callback"

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
BANNER_FILE="$ROOT/tests/Croft.Web.E2E/test-image.png"

TOKEN=""

# request "METHOD PATH" curl-args... : run one API call, fail loudly on HTTP errors.
request() {
    local label="$1"
    shift
    local out code resp
    out="$(curl -sS -w $'\n%{http_code}' "$@")" || {
        echo "screenshot-gate seed: $label: request failed" >&2
        exit 1
    }
    code="${out##*$'\n'}"
    resp="${out%$'\n'*}"
    case "$code" in
        [123][0-9][0-9]) ;;
        *)
            echo "screenshot-gate seed: $label failed (HTTP ${code:-none})" >&2
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
    local authorize_url="$MOCK_AUTHORITY/authorize?response_type=code&client_id=$MOCK_CLIENT_ID&redirect_uri=$CALLBACK_URL&scope=openid&state=gate"
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
        echo "screenshot-gate seed: no authorization code in the mock's redirect ($headers):" >&2
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
        echo "screenshot-gate seed: no access token in the token response: $response" >&2
        exit 1
    fi
    rm -f "$jar" "$headers"
}

# create_post TITLE SUBTITLE CATEGORY_ID BANNER_ID PUBLISH_AT BODY [TAG ...] : one post of the
# fixture set; PUBLISH_AT empty leaves it a draft. Prints the post id.
create_post() {
    local title="$1" subtitle="$2" category_id="$3" banner_id="$4" publish_at="$5" body="$6"
    shift 6
    local payload id
    payload="$(jq -nc --arg title "$title" --arg md "$body" --arg sub "$subtitle" \
        --arg cat "$category_id" --arg banner "$banner_id" --args \
        '{title:$title, markdownContent:$md,
          subtitle:(if $sub == "" then null else $sub end),
          categoryId:(if $cat == "" then null else $cat end),
          bannerImageId:(if $banner == "" then null else $banner end),
          tags:$ARGS.positional}' "$@")"
    id="$(api POST /api/posts "$payload" | jq -r .id)"
    if [ -n "$publish_at" ]; then
        api POST "/api/posts/$id/publish" "$(jq -nc --arg t "$publish_at" '{publishedAt:$t}')" >/dev/null
    fi
    printf '%s' "$id"
}

# slug_of ID : the post's public slug, read back from the API.
slug_of() {
    api GET "/api/posts/$1" | jq -r .slug
}

BANNER_BODY='The screenshot gate renders this fixed content on both sides of the comparison, so any
difference in the captured pixels is a difference the change under review introduced.

## A section

First paragraph with enough words to fill the reading column comfortably and show the measure
the blog is designed around at every captured width.

Second paragraph with a [link to Home](/) inline, showing an anchor at the reading measure
rather than a stub line.

- Alpha item with enough words to wrap onto a second line on a narrow phone screen
  - Nested one
  - Nested two
- Beta item

```csharp
var x = 1;
```'

PLAIN_BODY='A second published post without a banner, category, subtitle or tags: the fallback
banner and the no-chip shapes, deterministic text throughout.

## Plain section

Plain paragraph with enough words to read comfortably at the reading measure.'

DRAFT_BODY='The draft behind the post-editor capture: every optional field missing by
construction, with a short fixed body.'

echo "Seeding the screenshot-gate fixtures through $API_BASE_URL as the mock admin ..."
get_admin_token

# Category with icon (the icon renders in the admin posts grid's Category column).
CATEGORY_ID="$(api POST /api/categories "$(jq -nc '"Gate Cat"')" | jq -r .id)"
api PUT "/api/categories/$CATEGORY_ID" "$(jq -nc '{name:"Gate Cat", icon:"Code"}')" >/dev/null

# Tags (fixed names; rendered as the post's tag pills and the editor's chips).
api POST /api/tags "$(jq -nc '"gatetagone"')" >/dev/null
api POST /api/tags "$(jq -nc '"gatetagtwo"')" >/dev/null

# The banner: the repository's fixed test image, uploaded once (the database keeps only its
# metadata and file reference).
BANNER_ID="$(request "POST /api/media" -X POST -H "Authorization: Bearer $TOKEN" \
    -F "file=@$BANNER_FILE;filename=gate-banner.png;type=image/png" \
    -F "scope=Global" \
    "$API_BASE_URL/api/media" | jq -r .id)"
api PUT "/api/media/$BANNER_ID" "$(jq -nc '{altText:"Gate banner"}')" >/dev/null

# Fixed past UTC publish instants: the rendered dates ("10 January 2026" / "5 January 2026")
# are constants, so the captures carry no clock.
BANNER_POST_ID="$(create_post "Gate Post With Banner" "A fixed subtitle for the screenshot gate" \
    "$CATEGORY_ID" "$BANNER_ID" "2026-01-10T09:00:00Z" "$BANNER_BODY" gatetagone gatetagtwo)"
PLAIN_POST_ID="$(create_post "Gate Post Plain" "" "" "" "2026-01-05T09:00:00Z" "$PLAIN_BODY")"
DRAFT_ID="$(create_post "Gate Draft Post" "" "" "" "" "$DRAFT_BODY")"

mkdir -p "$GATE_OUT"
jq -nc --arg bannerSlug "$(slug_of "$BANNER_POST_ID")" \
    --arg plainSlug "$(slug_of "$PLAIN_POST_ID")" \
    --arg draftId "$DRAFT_ID" \
    '{bannerSlug:$bannerSlug, plainSlug:$plainSlug, draftId:$draftId}' >"$GATE_OUT/fixtures.json"

echo "Screenshot-gate fixtures seeded:"
cat "$GATE_OUT/fixtures.json"
