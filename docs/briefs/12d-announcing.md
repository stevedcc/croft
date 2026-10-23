# Brief 12d: a published post announces itself on Mastodon and Bluesky, exactly once

Read AGENTS.md first and follow it. One commit per item, **test first: write the test, run it,
watch it fail for the right reason, then the code** (break-it-once is an extra check, never the
red evidence instead of it). E2E twice at the end, the final whole-branch review.

**Spec (approved by the owner 2026-10-21, decisions added 2026-10-22):**
`docs/superpowers/specs/2026-10-19-social-replies-design.md`. Read "Linking a post to its
announcements", "Announcing on publish (POSSE)", "Caching" and "Briefs" item 4 in full first.
Where this brief is more specific than the spec, the brief wins.

**What exists (12a–12c on blog-framework; 0.6.7 released, 12c accepted):**
- `Post` (`src/Croft.Api/Models/Post.cs`): `Title`, `Subtitle`, `Slug`, `Status` (Draft,
  Scheduled, Published), `Kind` (Post, Page), `PublishedAt`, `BannerImageId`/`BannerImage`
  (a `MediaAsset`, file via `IFileStorage`), and 12a's `MastodonUrl`, `MastodonStatusId`,
  `BlueskyUrl`, `BlueskyUri`, `DiscussionLinkedAt`. Latest migration `20261021154522_SocialReplies`.
- Publishing: `PostService.PublishAsync` (`Services/PostService.cs:512-550`; sets Published or
  Scheduled, then `SaveAndNotifyAsync(db, CacheGroups.PostVisibility)`; note it does **not** look
  at the previous status, so publishing a published post re-stamps it), `UnpublishAsync`
  (:552-575), and `ScheduledPublisherHostedService.PublishDuePostsAsync` (:79; `ExecuteUpdateAsync`
  inside `BackgroundJobLock.TryRunAsync`, its own rows recognised by `UpdatedAt == now`,
  returns `PublishDuePostsResult` with the published posts).
- The pattern to copy for the sender: `Social/SocialFetchHostedService.cs` (tick, claim under the
  advisory lock with a conditional `ExecuteUpdateAsync` that sets `LeaseUntil`, work outside the
  lock, one short transaction per result, `LastError` capped at 500 characters, public
  `TickAsync(db, ct)` for tests, exceptions logged by type name). Job ids in
  `Services/BackgroundJobIds.cs`; **6 is the next free id**, values never change.
- `Social/SocialOptions.cs` (`Social:Enabled`, `Social:Mastodon:Server`, `:Account`,
  `Social:Bluesky:Did`; no credentials yet). `Social/SocialHttp.cs`: named clients with the SSRF
  guard `SocialEgressGuard`, no redirects/cookies/logging, User-Agent, standard resilience handler
  with a 10 s timeout and **no retries**; only a size-capped `GetAsync` helper.
  `SocialRateLimit.RetryAt`, `SocialPlatformGates` (one semaphore per platform), `SocialFetchErrors`.
- `SocialLinkService.ApplyAsync(post, ResolvedSocialLinks, now, ct)` sets a post's link fields and
  creates/removes its `SocialThread` rows; `ResolvedSocialLinks(MastodonUrl, MastodonStatusId,
  BlueskyUrl, BlueskyUri, MastodonUnchanged, BlueskyUnchanged)`.
- `Social/SocialAvatarService.Reencode` (SkiaSharp: header check, decode, encode): the model for the
  image fitter in item 4.
- Web: `PostEditPage.razor` (Discussion tab, `DiscussionPanel`; `_announcement` is already the
  page's status message, pick other names), `PostEditorForm.razor:135-153` (the Discussion
  section, posts only), `PostEditModel`, `LinkChange`, `BlogApiClient` write pattern (`SendAsync`,
  `EnsureSuccessWithDetailAsync`, `EvictReadingPagesAsync()`), `FakeBlogApiClient`.
- Tests: `Fixtures/Social/StubHttp.cs` (`StubClientFactory`, `StubHandler` recording requests),
  `SocialFetchHarness` (scratch DB, `FakeTimeProvider`, `NewReplica()`), `SocialApiFactory`
  (removes the hosted fetcher, replaces `TimeProvider`), `EndpointAuthorizationTable`,
  `RouteAuthorizationGuardTests`, `WarmupRoutesDriftTests.NotWarmed`, `MigrationContractGuardTests`.

**Owner-visible outcome:**
- Publishing a post (button or scheduler) posts an announcement to Mastodon and Bluesky within a
  minute, once. The post's Discussion links fill in by themselves, so its replies start showing.
- In the editor's Discussion section: "Announce on Mastodon" and "Announce on Bluesky" (on for new
  posts), the announcement text (empty = the default: title and subtitle), and a preview per
  network with a live count against the network's limit.
- On the Discussion tab: each network's announcement status (not announced, pending with the next
  try, posted with a link, failed with the error, cancelled), with "Announce now" and "Retry".
- Without credentials the tab says the network isn't set up, and nothing is sent.

**CSP impact: none.** Admin pages of the existing kinds, MudBlazor components only, no new script,
no inline `style` written by our code (the preview's line breaks come from a CSS class).
`CspSnapshots.txt` byte-identical. Anything that seems to need a CSP change: stop and report.

**Secrets:** the Mastodon access token, the Bluesky app password and the Bluesky session tokens
never appear in logs, exception messages stored in the database, DTOs, test output or commits.
Tests use invented values like `test-token-0f3a`.

## Decisions (made; don't re-open)
- **Posts only.** Pages are never announced: the editor doesn't offer it, and the API refuses it
  (400) and never queues a page.
- **Queue on the transition to Published only**: Draft → Published or Scheduled → Published, by
  `PublishAsync` or by the scheduler, in the **same transaction** as the status change. Publishing
  an already published post queues nothing. Per network, a row is queued only if the post's flag
  for that network is on, the post has **no link** for that network yet (a hand-entered URL means
  it was announced elsewhere), and there is no row, or the row is `Cancelled`. A `Posted` row is
  never sent again, a `Failed` row waits for Retry.
- **One outbox row per post and network** (unique), states `Pending`, `Posted`, `Failed`,
  `Cancelled`. The row id is Mastodon's `Idempotency-Key`.
- **The text is composed at send time** from the post's current title, subtitle, announcement text
  and slug (owner: an edit while pending sends the current text). Post URL:
  `{Blog:BaseUrl without trailing slash}/post/{slug}`.
- **Uncertain sends:** every attempt after the first (`Attempts` > 1 after the claim, including an
  attempt after an expired lease) first checks the account's latest posts for the post URL. Found
  → `Posted` with that post's id and URL, no send. Not found → send.
- **Retry window 6 hours** from `QueuedAt`. Backoff after a failed attempt: 1, 2, 4, 8, 16, 32,
  then 60 minutes, or the platform's rate-limit reset if later. When the next attempt would fall
  after `QueuedAt` + 6 h, the row becomes `Failed` ("Gave up after 6 hours: <last error>").
- **Errors that can't heal fail at once** (no retry): HTTP 400, 401, 403, 404, 422 from a send or
  from Bluesky's `createSession`, and custom text over a network's limit. Timeouts, network errors,
  408, 429 and 5xx retry.
- **Unpublish cancels** that post's `Pending` rows (same transaction). A row in flight still records
  its result: a successful send always ends `Posted` (it happened), whatever the state was.
- **Retry and "Announce now" are one endpoint**: it sets the row (created if absent) to `Pending`,
  `QueuedAt` = now, `NextAttemptAt` = now, keeps `Attempts` (so a retry checks before sending).
- **On success the post gets the link** through `SocialLinkService.ApplyAsync` with the returned
  ids, the other platform unchanged, unless the post meanwhile got a link for that platform by
  hand; then the hand-entered link stays and the row just records its remote URL.
- **Default text**: title, then a blank line and the subtitle if there is one. If the default
  doesn't fit a network's limit, shorten the subtitle (then the title) with "…" to fit; never fail
  on the default. **Custom text** over the limit of a network whose flag is on is refused on save
  (400, `Detail` starting "AnnouncementText") and, if it got in anyway, fails at send time.
- **Counting**: both networks count grapheme clusters (`StringInfo.LengthInTextElements`).
  Mastodon counts any URL as 23; Bluesky counts the URL as written. Limits: Mastodon 500,
  Bluesky 300. The announcement is `{body}\n\n{postUrl}`.
- **Credentials and readiness:** new settings `Social:Mastodon:AccessToken`,
  `Social:Bluesky:AppPassword` (secrets, from `blog-api-secrets`), `Social:Bluesky:Service`
  (default `https://bsky.social`, the PDS for `createSession`/`uploadBlob`/`createRecord`). A
  network is **ready** when `Social:Enabled`, its existing setting (server / DID), its credential
  and `Blog:BaseUrl` are all set. Not ready: nothing is queued for it, and the sender never sends
  for it (rows already queued wait; they don't fail).
- **No retries in the HTTP pipeline for posting** (Polly must never re-send a POST): a new named
  client `SocialHttp.Announce` like the others, but a 30 s timeout (blob uploads).
- **Sender:** `SocialAnnounceHostedService`, job id 6, tick every 30 s, at most 5 claims per tick,
  lease 5 min, sends through `SocialPlatformGates` (so it never overlaps the fetcher on one
  platform). Claiming increments `Attempts` and sets `LastAttemptAt`.
- Existing posts: the migration sets both flags **on** for posts that are Draft or Scheduled, and
  **off** for published posts and all pages, so republishing an old post doesn't announce it by
  surprise. New posts: on by default (API create with the flags null → on for a post, off for a page).

## Item 1: data, settings, DTOs and the text rules
- Migration `SocialAnnouncements` (expand-only, `<summary>` like 12a's):
  - `posts`: `AnnouncementText` (text, null), `AnnounceMastodon` and `AnnounceBluesky` (bool, not
    null, default false), then the data update from the decisions (check how `Status`/`Kind` are
    stored before writing the SQL; follow AGENTS.md "Rules for data migrations").
  - table `SocialAnnouncements`: `Id` (uuid PK), `PostId` (FK, cascade), `Platform` (int),
    `State` (int), `Attempts`, `QueuedAt`, `NextAttemptAt` (null when not pending; indexed),
    `LeaseUntil`, `LastAttemptAt`, `LastError` (≤ 500), `RemoteId`, `RemoteUrl`, `PostedAt`,
    `SentText`; unique (`PostId`, `Platform`).
- `SocialOptions`: the three settings above; computed `MastodonCanAnnounce` / `BlueskyCanAnnounce`
  (readiness incl. `Blog:BaseUrl`, which you pass in or bind; your choice, tested).
- Shared (`src/Croft.Shared/Social/AnnouncementText.cs`), used by API and Web: limits, `Default(title,
  subtitle)`, `Fit…` for the default, `Compose(body, postUrl)`, `MastodonLength`, `BlueskyLength`.
- DTOs: `CreatePostRequest`/`UpdatePostRequest` gain `string? AnnouncementText = null,
  bool? AnnounceMastodon = null, bool? AnnounceBluesky = null` (update: null = unchanged; text ""
  = back to the default, stored as null); `PostDto` gains the three. In
  `Contracts/Social/`: `AnnouncementState` enum, `AnnouncementStatusDto(Platform, State, Attempts,
  QueuedAt, LastAttemptAt?, NextAttemptAt?, LastError?, RemoteUrl?, PostedAt?)`,
  `PostAnnouncementsDto(MastodonReady, BlueskyReady, PostUrl, Items)`.
- Save validation: custom text over the limit of a network whose (resulting) flag is on → 400
  "AnnouncementText is too long for Bluesky (312 of 300 characters)." / "… for Mastodon …".
- Tests: migration guard passes; counting (ASCII, emoji ZWJ sequence = 1, combining accents,
  CJK; Mastodon URL = 23 whatever its length; Bluesky URL as written); the default fitter (long
  subtitle shortened with "…" to exactly the limit, long title, no subtitle); create defaults per
  kind; update null/""/value for the text; the 400s with exact messages; existing-row data update
  (a scratch DB with a draft, a scheduled and a published post and a page, then the migration).
  Break-it-once: count with `string.Length`; the emoji test fails.

## Item 2: queueing, cancelling, the endpoints
- Queue in `PublishAsync` (transition only) and in `PublishDuePostsAsync` (inside its transaction,
  for the posts it just published; it uses `ExecuteUpdateAsync`, so insert the rows explicitly),
  cancel in `UnpublishAsync`, all by one internal service (e.g. `AnnouncementQueue`).
- `GET api/posts/{id:guid}/announcements` (Admin, Editor) → `PostAnnouncementsDto` (404 unknown
  post). `POST api/posts/{id:guid}/announcements/{platform}` (Admin) → 202 with the row; 400 for a
  page or a network that isn't ready; 409 if the post isn't published, already has a link for that
  network, or the row is `Posted`. Rows in `EndpointAuthorizationTable`.
- Tests (API, scratch DB): publish a draft → two pending rows (one per ready network, none for a
  not-ready one); publish again → no change; scheduled post published by `PublishDuePostsAsync` →
  rows in the same transaction (roll it back: no rows); flag off → no row; hand-linked → no row;
  page → none; unpublish → `Cancelled`; republish → `Pending` again; `Posted` row → never re-queued;
  the endpoint's codes and roles. Break-it-once: drop the previous-status check; "publish again"
  fails.

## Item 3: the Mastodon poster
- `MastodonAnnouncer`: `POST https://{server}/api/v1/statuses`, `Authorization: Bearer`, header
  `Idempotency-Key: {row id}`, JSON `{"status": text, "visibility": "public"}` → `id`, `url`.
- Check for an earlier send: `GET /api/v1/accounts/lookup?acct={Account}` → id, then
  `GET /api/v1/accounts/{id}/statuses?limit=20&exclude_replies=true&exclude_reblogs=true`
  (unauthenticated); found = a status whose `content` contains the post URL or whose `card.url`
  equals it.
- Outcomes: posted (id, url), retry (with rate-limit reset), fail (permanent, with a short reason
  that never includes the token or the response body beyond the API's `error` string, capped).
- Record a real response shape as a fixture (`docs/briefs/12d-fixtures/mastodon-status-created.json`
  and `mastodon-account-statuses.json`; invented account, `.example` URLs for anything not the
  configured server) and copy it like 12a's fixtures.
- Tests with `StubHandler`: request method, URL, headers (bearer, idempotency key = row id), body;
  201/200 → posted; 401/422 → permanent; 429 with `X-RateLimit-Reset` → retry at that time;
  500 and a timeout → retry; the check finds the status by content and by card; not found.
  Break-it-once: send without the `Idempotency-Key`; its test fails.

## Item 4: the Bluesky poster and the card image
- `BlueskyAnnouncer`, against `Social:Bluesky:Service`:
  1. `com.atproto.server.createSession` `{identifier: Did, password}` → `accessJwt`, `did` (one
     session per send; never logged, never stored).
  2. If the post has a banner: `SocialImageFitter` makes a JPEG ≤ 950,000 bytes (long side ≤ 1200,
     quality 80 → 70 → 60, then scale by 0.75 until it fits or the long side is below 400; refuse
     sources whose side exceeds 8192; null on anything undecodable), then
     `com.atproto.repo.uploadBlob` (`Content-Type: image/jpeg`) → blob. No banner or no fit → card
     without a thumb.
  3. `com.atproto.repo.createRecord` `{repo: did, collection: "app.bsky.feed.post", record: {$type,
     text, createdAt (ISO 8601 UTC), facets: [one link facet over the URL, byteStart/byteEnd in
     UTF-8 bytes], embed: {$type: "app.bsky.embed.external", external: {uri, title, description
     (subtitle or ""), thumb?}}}}` → `uri`, `cid`. Stored URL:
     `https://bsky.app/profile/{did}/post/{rkey}`, URI = `uri`.
- Check for an earlier send: `GET https://public.api.bsky.app/xrpc/app.bsky.feed.getAuthorFeed?actor={did}&limit=30&filter=posts_no_replies`;
  found = a post whose record's external embed `uri` or link facet `uri` equals the post URL.
- Rate limits: Bluesky sends `ratelimit-reset` (Unix seconds); make `SocialRateLimit.RetryAt`
  understand it if it doesn't, with a test.
- Fixtures as in item 3 (`bluesky-create-record.json`, `bluesky-author-feed.json`; invented DID).
- Tests: the three requests in order with their bodies (facet byte offsets checked on a text with
  an emoji and "ü" before the URL), no thumb without a banner, a 3000 × 2000 PNG banner fitted
  under the limit as JPEG with long side ≤ 1200, a corrupt banner → card without thumb, session 401
  → permanent, `createRecord` 5xx → retry, the check found / not found. Break-it-once: count facet
  offsets in characters; the emoji test fails.

## Item 5: the sender
- `SocialAnnounceHostedService` as decided; registered in `Program.cs`; removed in the test
  factories like the fetcher. Per claimed row, outside the lock: load the post, skip and release the
  claim if the network isn't ready, compose the text (custom too long → `Failed`), check first when
  `Attempts` > 1, send, then one short transaction: `Posted` (+ `RemoteId`, `RemoteUrl`,
  `PostedAt`, `SentText`, link the post as decided, invalidate as `SocialLinkService` does for a
  link change), or back off / fail as decided (`LastError` ≤ 500, no secrets).
- Logs: platform, post id, attempt, outcome, HTTP status or exception type. Nothing else.
- Tests (`FakeTimeProvider`, stub platforms): a published post is posted on the next tick and its
  links and `SocialThread` rows appear; two replicas ticking at once send once; lease expiry after
  a "crash" (claim, then no result) → the next attempt checks, finds the post, records `Posted`
  without sending; not found → sends; backoff times exactly 1, 2, 4 … 60 min; the 6-hour cut-off;
  permanent error → `Failed` at once; an edit while pending → the new text is sent; unpublished
  while in flight → success still recorded `Posted`; a hand link added meanwhile is kept; a
  not-ready network leaves its row pending; **no secret in any log line, `LastError` or DTO**
  (capture the logs; the invented token, app password and JWT appear nowhere). Break-it-once: skip
  the check on retries; the "crash" test sends twice and fails.

## Item 6: the editor
- `IBlogApiClient`: `GetAnnouncementsAsync(Guid)` (404 → null), `AnnounceAsync(Guid, SocialPlatform)`
  (the 400/409 `Detail` shown to the user); `FakeBlogApiClient`; `NotWarmed` entries.
- `PostEditModel`/`PostEditorForm` Discussion section (posts only), after the two URL fields:
  checkboxes "Announce on Mastodon when published" and "Announce on Bluesky when published", the
  multiline "Announcement text" (placeholder = the default body; helper: the post's address is
  added at the end), and per network a preview (the composed text, plain text, line breaks by CSS
  class) with "N / 500" / "N / 300", in the error colour when over. The preview's URL is
  `NavigationManager.BaseUri` + `post/{slug}`. Save sends the text with `LinkChange` semantics
  (unchanged → null, emptied → ""), the flags as they are; the text's 400 appears under the field.
- The Discussion tab: an "Announcements" block above the replies, one line per network: "Not set up
  on this site" / "Not announced" (+ "Announce now" when the post is published) / "Pending, attempt
  N, next try HH:mm" / "Posted DD MMM HH:mm" with a link to the remote post / "Failed: <error>"
  (+ "Retry") / "Cancelled when unpublished" (+ "Announce now" when published again). After a click,
  reload the block; errors in the page's existing error area.
- After Publish now: if the announcements read shows pending rows, the page's status message says
  "Published. Announcing on Mastodon and Bluesky." (only the networks with a pending row).
- Tests (bUnit): flags default on for a new post, fields absent for a page; counts for a known text
  and URL, the over-limit class; save sends null/""/text and the flags; each status line and its
  button; Announce now calls once and reloads; the 409 message shown. Break-it-once: send the text
  even when unchanged; the "unchanged → null" test fails.

## Item 7: E2E, accessibility, screenshots, documentation
- **No outbound calls.** The E2E instances keep 12b's settings (no Mastodon server, no DID, no
  credentials), so neither network is ready. Never call Mastodon or Bluesky.
- E2E as admin: a new post has both flags on; typing text updates both counts; a custom text over
  300 with Bluesky on → saving shows the 400 under the field, the rest unchanged; publishing a post
  → the Discussion tab shows "Not set up on this site" for both and no rows exist.
- Axe (serious/critical = 0), light and dark: the editor's Discussion section with the
  announcement fields, the Discussion tab with the Announcements block.
- Screenshots (`ScreenshotTests`): the announcement fields and the Announcements block at 1440 and
  390, light and dark. Look at two and say how they read.
- Docs: README "Social replies" gains "Announcing on publish" (what happens, the three settings,
  the two secrets with the Mastodon scope `write:statuses` only and a Bluesky app password without
  DM access, egress hosts: the Mastodon server, `bsky.social` or the configured service,
  `public.api.bsky.app`); config and secrets tables; `k8s/secrets.example.md` (`blog-api-secrets`
  gains `Social__Mastodon__AccessToken`, `Social__Bluesky__AppPassword`); the configmap comment
  "No secrets" stays true (the secrets go in the Secret).
- Break-it-once: render the Announcements block only for ready networks; the "Not set up" E2E fails.

## Amendment (owner, 2026-10-23): hashtags and language
1. Setting `Social:AnnouncementLanguage` (BCP-47, default "en"; blank = send none). Item 1 (`SocialOptions`, tested default and
   binding). Mastodon: send `"language": "<value>"` in the status JSON (item 3, asserted on the request body). Bluesky:
   `"langs": ["<value>"]` in the post record (item 4, asserted).
2. Bluesky hashtag facets (item 4): every hashtag in the composed text gets a facet `{index: {byteStart, byteEnd}` (UTF-8 bytes,
   covering the `#` too), `features: [{"$type": "app.bsky.richtext.facet#tag", "tag": "<tag without #>"}]}`, alongside the link
   facet. Detection follows Bluesky's own rule (the official @atproto/api `detectFacets`): a `#` or `＃` at the start of the text
   or after whitespace, followed by a run of non-whitespace that is not all digits; trailing punctuation is stripped from the tag;
   tags longer than 64 characters are skipped. A `#` inside the post URL (no whitespace before it) is never a tag. The detection
   goes in Croft.Shared next to the composer if the editor preview can use it, otherwise in the API. Mastodon detects hashtags
   itself. Tests: "Load testing #homelab and #dotnet." gives two tag facets ("homelab", "dotnet", the `.` stripped) with exact
   byte offsets on a text with an emoji and "ü" before them; "#2026" none; "a#b" none; a URL with a fragment none; facets ordered
   by byteStart. Break-it-once: offsets in characters instead of bytes; the emoji test fails.
3. Editor (item 6): helper text under the announcement field: "Hashtags (#homelab) are linked on both networks." No other UI.

## Out of scope
- "Delete announcement" (a later brief), announcing pages, media other than the card image,
  per-network different texts, threads.
- Fixing `PublishAsync` re-stamping an already published post (only don't queue on it).
- Any change to the public reading pages, the fetcher's behaviour, or the CSP.
- Calling the real platforms from any test, or from your shell.
