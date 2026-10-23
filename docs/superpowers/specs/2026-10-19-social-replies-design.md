# Social replies under posts (Mastodon and Bluesky)

Status: draft for the owner's review, 2026-10-19.

## Goal

Readers discuss a post where they already are, on Mastodon or Bluesky, and the replies appear
below the post on the blog, merged into one discussion. Croft runs no comment system of its
own: no reader accounts, no logins, no comment database to moderate from scratch. The platforms
moderate first; the owner can hide any reply on request.

Owner's conditions (2026-10-19):
- only replies that are publicly visible without an account, and only where the platforms'
  terms allow showing them;
- integrated as far as possible (one thread, not two widgets);
- a blocklist for hiding replies on request;
- not searchable through the blog (the whole discussion section is `data-nosnippet`).

## Research summary (2026-10-19)

Neither platform grants third parties a licence; both expect public posts to be shown elsewhere
with attribution, and the common practice (dozens of existing widgets) relies on that. The
existing widgets all run in the reader's browser and none honours the opt-outs below. Croft
does it server-side and honours them.

- **Bluesky:** `GET https://public.api.bsky.app/xrpc/app.bsky.feed.getPostThread` (no auth,
  generous limits, `cache-control: max-age=30`). It does not filter for us:
  - skip `notFoundPost` and `blockedPost`;
  - skip replies whose author carries the self-label `!no-unauthenticated` ("ask apps not to
    show my account to logged-out users"; Bluesky asks third-party apps to respect it);
  - skip replies listed in the thread's `threadgate.record.hiddenReplies` (hidden by the owner
    in Bluesky);
  - hide replies labelled `porn`, `sexual`, `nudity` or `graphic-media`.
  - Developer guidelines: a way to delete content on request, to block abusive users, and a
    public contact address (the Impressum).
- **Mastodon:** `GET https://<server>/api/v1/statuses/<id>/context` on the owner's server
  (ieji.de: works without auth, Mastodon 4.7.3, 300 requests / 5 min). It lists only replies
  the server knows about.
  - show only `public` and `unlisted`;
  - skip accounts with `noindex: true`;
  - `indexable: false` is a search opt-in, not a display opt-out: show those replies (owner
    decision), the section is `data-nosnippet` anyway;
  - `sensitive` / `spoiler_text`: render collapsed behind the content warning;
  - the content is HTML: sanitise it to our own allow-list.
- **GDPR (not legal advice):** legitimate interest (Art. 6(1)(f)) with a documented balancing
  test; store the minimum (display name, handle, avatar, text, time, link); deletions at the
  source disappear here at the next refresh, avatars included; the privacy page describes it
  (Art. 14); removal requests go through the blocklist.

## Design

### Linking a post to its announcements

- A post gets two optional fields, edited in the post editor under "Discussion":
  - **Mastodon post URL** (e.g. `https://ieji.de/@SteveDCC/1153…`);
  - **Bluesky post URL** (`https://bsky.app/profile/crawford.dev/post/3l…`; Croft resolves it
    to the `at://` URI once, on save, and stores both).
- Validation: the Mastodon URL's host must be the configured server
  (`Social:Mastodon:Server`, here `ieji.de`), the Bluesky URL's author must resolve to the
  configured DID (`Social:Bluesky:Did`), so only the owner's own posts can carry a discussion.
- Normally Croft fills both fields itself when it announces the post (below); entering URLs by
  hand stays as the fallback (an announcement made outside Croft, or an older post).

### Announcing on publish (POSSE)

Owner decision (2026-10-19): Croft announces each post itself when it is published
(Publish on your Own Site, Syndicate Elsewhere).

- **Announcement text** per post, in the editor's "Discussion" section: defaults to the title,
  the subtitle and the post's URL; editable; a preview per network and a live character count
  (Mastodon 500, Bluesky 300 graphemes, the URL included as each network counts it).
- **Per post choice:** *Announce on publish* (default on for posts, off for pages) or *Don't
  announce*; per network checkboxes.
- **When:** at the moment the post becomes published, whether by the Publish button or by the
  scheduler (scheduled posts announce at their publication time). An "Announce now" button
  for a post that was published without announcing.
- **Exactly once:** an outbox row per post and network (pending → posted, with the remote id and
  URL, or failed with the error and attempt count). A row that is `posted` is never posted again.
  Edits to the article never re-announce. The editor shows the status and the error, with a
  Retry button. Owner decisions (2026-10-22):
  - **Uncertain send** (timeout, crash between the remote call and the database update): before
    every retry, Croft checks the account's latest posts for the post's URL; found → recorded as
    posted, not found → sent again. Mastodon also gets an `Idempotency-Key` (the outbox row id).
    There is no "unknown, wait for a click" state.
  - **Retries with backoff for up to 6 hours** after the row was queued (not 24); after that the
    row is failed and the editor's Retry decides. Errors that can't heal (bad credentials, a
    refused post) fail at once.
  - **Unpublished while pending:** the pending rows are cancelled. **Edited while pending:** the
    current announcement text is sent (it is composed at send time, not when queued).
- **Bluesky:** `com.atproto.server.createSession` with an app password, then
  `com.atproto.repo.createRecord` for an `app.bsky.feed.post` with the URL as a link facet and an
  `app.bsky.embed.external` card (title, description, and the banner uploaded with
  `uploadBlob`, re-encoded to fit the size limit). The returned `at://` URI and its bsky.app URL
  are stored on the post.
- **Mastodon:** `POST /api/v1/statuses` with an `Idempotency-Key` (the outbox row id) and
  visibility `public`; Mastodon builds the link card itself. The returned URL is stored.
- **Credentials:** a Mastodon access token for the owner's account limited to the
  `write:statuses` scope, and a Bluesky app password, both from the deployment's secrets (tswap
  in the site repository). Without them, announcing is off and the editor says so; manual URLs
  still work.
- **Deleting or unpublishing a post** does not delete the announcements (the replies are the
  readers'); the editor offers "Delete announcement" per network as a separate, confirmed
  action.

### Fetching and storing (API)

- A hosted service fetches each linked post's thread on a schedule that follows the post's age:
  every 10 minutes for the first 2 days after publishing or after a link was added, hourly
  until day 30, daily after that. Admins can trigger a refresh per post.
- Respect rate limits: `X-Ratelimit-*` (Mastodon), 429 with `Retry-After` (both); one request
  at a time per platform.
- Each successful fetch **replaces** the stored snapshot for that post and platform (one
  transaction): what disappeared upstream disappears here. Nothing deleted upstream is kept.
- A failed fetch keeps the last snapshot, but a snapshot older than 48 hours without a
  successful refresh is no longer shown (we can't know what was deleted meanwhile).
- Stored per reply (table `SocialReplies`): platform, external id (status id / `at://` URI),
  post id, parent external id, author handle, display name, author URL, avatar key, created at,
  permalink, sanitised content, content-warning text, fetched at. Nothing else (no follower
  counts, no other metadata).
- Filtering (above) happens before storing: filtered replies are never stored.
- Reply depth: everything the APIs return (Mastodon caps at 60 descendants, depth 20; Bluesky
  `depth=10`); display caps nesting at 3 levels and flattens below that.

### Blocklist (moderation)

- Table `SocialBlocks`: block one reply (platform + external id) or one account (platform +
  account id/DID), with who, when and an optional note. Blocks survive refreshes and apply
  before storing.
- Admin: a "Discussion" panel on the post's edit page lists the stored replies with
  **Hide reply** and **Hide account** buttons, and the post's refresh status (last fetch, last
  error). An admin page lists all blocks with **Unhide**.
- A block removes the reply (and its subtree) from the page at once on the Web pod that handled
  it, and within the output cache's duration (1 minute) on the others (owner, 2026-10-21:
  eventual consistency, a minute is fine). See Caching.

### Avatars

- Fetched by the API, never by the reader's browser: downloaded once, decoded and re-encoded to
  a 48×48 WebP (strips metadata, defuses malicious images), stored under a content hash, served
  by Web at `/social/avatar/<hash>.webp` with a long cache lifetime. `img-src 'self'`
  unchanged.
- An avatar no longer referenced by any stored reply is deleted at the next refresh.
- Fallback: the initial of the display name in a neutral circle.

### Rendering (Web, static SSR on the post page)

- Below the article: `<section id="discussion" aria-labelledby="discussion-heading"
  data-nosnippet>` with the heading "Discussion", the reply count, and the two reply actions.
- **One merged thread:** the top-level replies of both platforms interleaved by time, each with
  its nested replies beneath (cross-platform replies can't exist; the merge is chronological at
  the top level). Each reply shows avatar, display name, handle, a small platform mark, relative
  time (absolute in `title`), the content, and a link "View on Mastodon/Bluesky" to the
  original.
- Content: Mastodon HTML sanitised to `p`, `br`, `a` (http/https only, `rel="nofollow ugc
  noopener"`), mentions and hashtags as links; custom emoji become their `:shortcode:` text;
  Bluesky text with facets turned into the same links. No images or media from replies in this
  version (a "has media, view on …" note instead).
- Content warnings: `<details><summary>CW: …</summary>…</details>`, no script.
- No discussion links on a post: no section at all. Links but no replies yet: the section with
  "No replies yet" and the reply actions.
- Static markup only: no script, no inline style; works on the static reading pages.

### Reply actions

- **Reply on Bluesky:** a link to the Bluesky post on bsky.app.
- **Reply on Mastodon:** a small GET form "Your Mastodon server: [____] [Reply]" posting to
  `/discussion/mastodon/reply?post=<post id>&server=<host>` on our own origin (`form-action
  'self'` unchanged). The input has `autocomplete` so the browser remembers the server; Croft
  sets no cookie.
- That endpoint validates the host (a DNS name, no port, no path) and returns a small static
  page "Continue to <server>" with a link to
  `https://<server>/authorize_interaction?uri=<the Mastodon post URL>` (Mastodon's remote
  interaction entry point). A page with a link rather than a redirect, so the endpoint can't be
  used as an open redirect.

### Privacy, legal, CSP

- Privacy page (owner edits it; Croft supplies suggested text in the README): which data is
  shown, from where, on what basis, how long (until deleted at the source or blocked), how to
  ask for removal (Impressum contact).
- **CSP impact: none.** Avatars from our origin, the form posts to our origin, no script, no
  inline style. `CspSnapshots.txt` must not change.
- No cookies for readers (unchanged).
- `robots`: the post stays indexable; the discussion section is `data-nosnippet`.

### Caching (added 2026-10-21, after brief 14b)

Two caches sit between the stored replies and a reader: the API's public read cache
(`IPublicReadCache`, groups, cross-replica invalidation by LISTEN/NOTIFY) and the Web's output
cache (14b: anonymous post pages kept 60 s per Web pod, evicted only on that pod's staff writes).

- **Own API cache group, `Discussion`.** A post's discussion read is cached in it. A refresh
  that changed nothing (same snapshot hash per post and platform) writes nothing and invalidates
  nothing; a changed snapshot or a block/unblock invalidates `Discussion` only, never the
  post, list, category or tag groups. Otherwise the 10-minute refreshes would flush the whole
  public cache several times an hour.
- **The 48-hour rule is applied on read**, against the snapshot's last successful fetch time, so
  a cached entry cannot outlive it: the entry's lifetime is capped at the time the snapshot turns
  stale.
- **Web output cache: unchanged, eventual consistency accepted (owner, 2026-10-21).** New
  replies, blocks and unblocks reach a reader within the fetch interval plus at most one minute.
  The admin's Hide/Unhide goes through the Web's write path, so it evicts that pod's pages at
  once. No cross-pod eviction signal in this spec (a later Valkey pub/sub brief may add one for
  all content).
- **Avatars** (`/social/avatar/<hash>.webp`) are content-addressed: `Cache-Control: public,
  max-age=31536000, immutable`.
- **Not cached:** the Mastodon reply form's target (`/discussion/mastodon/reply`) and its
  continue page; they are not reading routes.
- **Warm-up (14a):** the API's warm-up list gains the discussion read for the newest post, and
  the drift guard covers the new `BlogApiClient` method.

### Configuration

```
Social:Mastodon:Server   ieji.de
Social:Mastodon:Account  SteveDCC          (for validating linked posts)
Social:Bluesky:Did       did:plc:djoo4i22jpa7o7ulgddooaud
Social:Enabled           true              (off by default: no fetching, no section)
```

## Briefs (contracts first, then parallel)

1. **12a: data and fetching (API).** Post fields and migration (expand only), URL validation and
   Bluesky resolution, the two fetchers with filtering, the snapshot store, schedule, rate
   limits, the 48-hour rule, blocklist tables and their application. Contract for 12b/12c: the
   read DTO for a post's discussion and the block/unblock endpoints.
2. **12b: rendering and reply actions (Web).** The discussion section, sanitising and facets,
   content warnings, avatars endpoint and fallback, the Mastodon reply form and continue page,
   `data-nosnippet`, E2E against recorded API fixtures (no live calls in tests).
3. **12c: admin (Web).** Editor "Discussion" fields, the per-post Discussion panel with
   Hide/Unhide, the blocks page, manual refresh.
4. **12d: announcing on publish (API + editor).** Outbox, both posters, idempotency, retries,
   credentials, the editor's announcement text, preview, counts, status and buttons. Needs 12a's
   post fields; its editor part shares files with 12c, so it runs after 12c (or 12c and 12d
   become one brief).

12b and 12c can run in parallel once 12a's DTOs are fixed in the brief. Tests never call the
real platforms: recorded JSON fixtures for both APIs, including every filter case (deleted,
blocked, `!no-unauthenticated`, hidden, labelled, private, `noindex`, sensitive).

## Out of scope (later)

- Bridgy Fed (the blog as its own account in both networks).
- Media from replies, likes/boost counts, reply notifications.
