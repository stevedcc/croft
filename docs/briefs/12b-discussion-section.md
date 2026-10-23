# Brief 12b: readers see the Mastodon and Bluesky replies under a post and can join in

Read AGENTS.md first and follow it. One commit per item, **test first: write the test, run it,
watch it fail for the right reason, then the code** (break-it-once is an extra check, never the
red evidence instead of it). E2E twice at the end, the final whole-branch review.

**Spec (approved by the owner 2026-10-21):** `docs/superpowers/specs/2026-10-19-social-replies-design.md`.
Read "Rendering", "Avatars", "Reply actions", "Privacy, legal, CSP" and "Caching" in full first.
Where this brief is more specific than the spec, the brief wins. 12a (accepted) built the data:
read `src/Croft.Shared/Contracts/Social/` (`PostDiscussionDto`, `DiscussionReplyDto`),
`IBlogApiClient.GetDiscussionAsync`, `src/Croft.Api/Social/DiscussionService.cs` and
`SocialAvatarsController`. Don't change the API or the DTOs; if something you need is missing, stop
and report.

**Builds on 14b/14c:** the post page is output-cached for anonymous readers (one minute), and
`MediaCachePolicy` + `MediaDocumentMetadata` (14c) cache image proxies. Read `src/Croft.Web/Caching/`.

**Owner-visible outcome:**
- A post linked to a Mastodon and/or a Bluesky post shows a "Discussion" section below the article:
  the replies of both platforms as one thread, with avatars, names, handles, the platform, the time,
  the text, content warnings folded, and a link to each original.
- A reader can reply: "Reply on Bluesky" opens the Bluesky post; "Reply on Mastodon" asks for the
  reader's server and leads them to the post on it.
- A post without links shows no section. A linked post without replies says "No replies yet" and
  still offers both actions.
- No script, no inline style, no cookie, no third-party request: avatars come from our origin.

**CSP impact: none.** Markup and stylesheet only; the form posts to our origin (`form-action
'self'`); avatars are same-origin (`img-src 'self'`). `CspSnapshots.txt` byte-identical. Any change
that seems to need a CSP change: stop and report.

## Decisions (made; don't re-open)
- **Trust the API's sanitising.** `ContentHtml` is sanitised at ingest by 12a (only `p`, `br`, and
  `a` with http/https and `rel="nofollow ugc noopener"`) and is rendered as it is
  (`MarkupString`). One sanitiser, tested in 12a; the CSP is the second layer. Say so in a comment
  where it is rendered.
- **The discussion never breaks the post.** The post page loads the discussion alongside the related
  posts (both started before either is awaited). If the discussion read fails (the API is down,
  `ApiUnavailableException`, a 5xx), the post renders without the section and the failure is logged
  once at Warning. A null result (unknown or unpublished post) means no section.
- **Placement:** directly after the post body, before the related posts, in the text column.
- **The Mastodon form carries the post's slug**, not its id (the spec says id; the API's public read
  is by slug and cached there): `GET /discussion/mastodon/reply?post=<slug>&server=<host>`.
- **No section when both URLs are null** (that is also what the API returns with `Social:Enabled`
  off).

## Item 1: the discussion section (static markup)
- New components in `Components/Blog/`: `DiscussionSection` (the `<section>`, heading, count,
  actions, the list or "No replies yet") and `DiscussionReply` (one reply and its nested list,
  recursive). They take the DTO as parameters; `Post.razor` loads and passes it.
- Markup, per the spec: `<section id="discussion" aria-labelledby="discussion-heading"
  data-nosnippet>`, `<h2 id="discussion-heading">Discussion</h2>`, the count ("1 reply" / "N
  replies", nested ones included: `ReplyCount`), then an `<ol>` of top-level replies, each `<li>`
  holding an `<article>` and, if it has answers, a nested `<ol>` (the DTO nests to depth 3; render
  what it gives).
- Each reply: the avatar (item 2), display name, handle, the platform as text ("Mastodon" /
  "Bluesky", no logos), the time as relative text in a `<time datetime="…" title="<absolute,
  UTC, ISO-like 'yyyy-MM-dd HH:mm UTC'>">` ("just now", "5 minutes ago", "3 hours ago", "2 days
  ago", then the date "12 Oct 2026"), the content, and "View on Mastodon" / "View on Bluesky"
  linking `Permalink` (`rel="nofollow ugc noopener"`). The author name links `AuthorUrl` (same rel).
- `ContentWarning` not null → `<details><summary>CW: <warning></summary>` around the content.
- `HasMedia` → after the content: "Has media: view on Mastodon/Bluesky" linking the permalink.
- Relative time comes from `TimeProvider` (injectable; tests use `FakeTimeProvider`). The page is
  cached for a minute, so "just now" covers under two minutes.
- Styles in the components' `.razor.css`, using the site's existing tokens (`--font-*`, colours from
  `branding/theme.css`); light and dark both readable. Nesting indents by a border-left rule per
  level, capped by the DTO's depth. At 390 px wide nothing overflows horizontally.
- Tests (bUnit):
  - Both URLs null → nothing rendered.
  - Links, zero replies → the section, "No replies yet", both actions (item 3 adds the actions;
    here assert the section and the text).
  - Two top-level replies from different platforms → rendered in the DTO's order, each with the
    platform text, handle, permalink, and the `rel` values.
  - A reply with two answers, one with its own answer → the nested `<ol>`s, three levels.
  - Count text: 1 → "1 reply", 0 → no count line (the "No replies yet" instead), 7 → "7 replies".
  - Content warning → `<details>` with `summary` "CW: …" and the content inside it.
  - `HasMedia` → the note and its link.
  - Relative time at 30 s, 5 min, 3 h, 2 days, 40 days; the `title` and `datetime`.
  - `data-nosnippet`, `id="discussion"`, `aria-labelledby` pointing at the heading.
- Break-it-once: render the replies flat (drop the nested list); the three-level test fails.

## Item 2: avatars
- Web endpoint `GET /social/avatar/{hash}.webp`: checks the hash is exactly 64 lower-case hex
  characters (else 404 without calling the API), proxies to the API's
  `/api/social/avatars/{hash}.webp` the way the `/api/media` proxy does (the visitor forwarding,
  the 503 on an API failure, the per-client limit's media partition), replays `Content-Type` and
  `Cache-Control` (`public, max-age=31536000, immutable`).
- It carries `MediaDocumentMetadata` and the `MediaCachePolicy` (14c), so a public avatar is kept in
  the pod for the cache duration like an image. If that policy needs a change to serve this route,
  make it in this item and test it.
- In the reply: `<img src="/social/avatar/<hash>.webp" width="48" height="48" alt="" loading="lazy"
  decoding="async">` (decorative: the name is next to it). `AvatarHash` null → the fallback: the
  first letter of the display name (or of the handle when the name is empty), upper-cased, in a
  neutral circle (CSS class, no inline style), `aria-hidden="true"`.
- Tests: Web.Tests with a fake API: a valid hash → 200, bytes and headers replayed; a bad hash
  (`ABC…`, 63 chars, `../x`) → 404 and the API not called; the API 404 → 404; the API down → 503;
  two anonymous requests → one API call. bUnit: hash → the `img`; null → the initial, from the name,
  from the handle when the name is empty.
- Break-it-once: drop the hash check; the "API not called" test fails.

## Item 3: the reply actions
- In the section, under the heading: **Reply on Bluesky** (only when `BlueskyUrl` is set), a link to
  `BlueskyUrl` (`rel="noopener"`). **Reply on Mastodon** (only when `MastodonUrl` is set): a GET
  form to `/discussion/mastodon/reply` with a hidden `post` (the slug) and a text input
  `server` labelled "Your Mastodon server" (`autocomplete="url"` is wrong for a host: use
  `autocomplete="on"` with `name="server"`, `inputmode="url"`, `spellcheck="false"`,
  `autocapitalize="none"`), and a "Reply" button. No script.
- Endpoint `GET /discussion/mastodon/reply` (static SSR page or a minimal endpoint rendering a small
  static page with the site layout; your choice, say why):
  - Normalises `server`: trims, drops a leading `https://` or `http://`, a trailing `/`, and
    anything up to the last `@` (so `@me@mastodon.example` and `https://mastodon.example/` work),
    lower-cases.
  - Validates the result as a DNS host name: ASCII letters, digits, hyphens and dots; at least one
    dot; labels 1–63 characters, not starting or ending with a hyphen; at most 253 characters; no
    port, path, query or user info left. (IDN: a reader types the punycode form; out of scope.)
  - Valid, and the post exists with a `MastodonUrl` (via `GetDiscussionAsync(slug)`): 200, a page
    "Continue to <host>" with one link to
    `https://<host>/authorize_interaction?uri=<MastodonUrl, percent-encoded>` and a line saying
    the reader will be asked to sign in to their server. **A link, never a redirect** (no open
    redirect).
  - Invalid host: 400, the same page with "That doesn't look like a server name" and the form again
    (pre-filled with what they typed, HTML-encoded).
  - Unknown post, or no `MastodonUrl`: 404 (the not-found page).
  - `<meta name="robots" content="noindex">`; not output-cached (not a reading route; assert the
    cache policy says no); no cookie set.
- Tests (Web.Tests, fake API): each normalisation input above → the right link; each invalid input
  (`mastodon`, `-bad.example`, `a..b`, `host:8080`, `host/path`, a 254-character name,
  `user:pass@host` after normalising, `javascript:alert(1)`) → 400 and no `authorize_interaction`
  link; the `uri` parameter is the encoded URL; no `Location` header on any response; no
  `Set-Cookie`; the robots meta. bUnit: actions present/absent per URL.
- Break-it-once: drop the dot rule; `mastodon` gets a 200 and its test fails.

## Item 4: the post page loads the discussion
- `Post.razor`: start `GetDiscussionAsync(slug)` and `GetRelatedPostsAsync` together, await both;
  pass the discussion to `DiscussionSection` after `PostBody`.
- A failing discussion read → no section, the post renders, one Warning log (source-generated
  `LoggerMessage`, the post's slug, the exception type).
- Tests (Web.Tests or bUnit with a fake client): discussion with replies → the section is in the
  page; the discussion read throws `ApiUnavailableException` → the page renders 200 with the body
  and without the section, one Warning; the discussion read and the related read are both in
  flight before either completes (a fake that holds both and counts).
- Break-it-once: await the discussion before starting the related read; the in-flight test fails.

## Item 5: E2E, accessibility, screenshots
- E2E instances run with `Social__Enabled=true` and **no** Mastodon server and no Bluesky DID: the
  fetcher then runs for no platform (check `SocialFetchHostedService`: it must make no outbound
  request; assert that in a test if it isn't already), and the API shows what is stored. Set this
  where the E2E instances get their environment (`dev/` scripts); say where in the commit.
- Each E2E test seeds its own post with both URLs and its own replies **directly into 12a's tables**
  (the E2E project already opens Npgsql connections in `WebTest`), with the thread rows' last
  success time set to now (the 48-hour rule). Use invented names and texts (no real people's data).
  No test calls a real platform.
- E2E:
  - A post with replies from both platforms, a content warning, a nested answer and a reply with
    media: the section shows the count, both platforms' replies in time order, the `<details>`
    closed, opening it shows the text; "View on …" links have the permalinks.
  - The fallback avatar renders for a reply without one.
  - The Mastodon form: type `@reader@mastodon.example`, submit → the continue page's link is
    `https://mastodon.example/authorize_interaction?uri=…` with the post's URL; nothing navigates
    off-site by itself.
  - A post without links: no `#discussion`.
  - Axe: no serious or critical violations on the post with a discussion, light and dark (follow
    `AccessibilityTests`' pattern).
  - Screenshots at 1440 × 900 and 390 × 844, light and dark, of the discussion section, in the
    existing `ScreenshotTests` output, for the owner to look at (no pixel assertions).
  - CSP: the existing enforced-CSP pass reports no violation on the post with a discussion.
- Break-it-once: render `ContentWarning` outside the `<details>`; the "closed" test fails.

## Out of scope
- The editor fields, the per-post Discussion panel, Hide/Unhide, the blocks page and manual refresh
  (12c). Announcing on publish (12d).
- Any API or DTO change; re-sanitising in the Web; IDN host names in the Mastodon form.
- Media, likes, boosts, reply notifications. Cross-pod eviction (a minute of staleness is accepted).
- The privacy page text (the owner writes it; a later docs item supplies a suggestion).
