# Brief 12f: publish dates that stay put, a meta line that says what it means, and the announcing loose ends

Read AGENTS.md first and follow it. One commit per item, **test first: write the test, run it,
watch it fail for the right reason, then the code** (break-it-once is an extra check, never the
red evidence instead of it). E2E twice at the end, then the final whole-branch review.

**Source:** GitHub issues stevedcc/croft #1 to #6 (quoted in the items). Each "Now:" was checked
against `main` at 19883b2 (2026-10-23); confirm it before changing it.

**Owner-visible outcome:** clicking Publish on a published post no longer makes it "new"; lost
dates can be set back; the post page says "Published on …, last updated on …" and "last updated"
only moves when the words change; the Discussion tab notices by itself that an announcement went
out; Bluesky links read `/profile/<handle>/…`; `curl -I` on the site answers 200.

**CSP impact: none.** `CspSnapshots.txt` stays byte-identical. Anything that seems to need a CSP
change: stop and report.

**Migrations: none expected.** If one turns out to be needed, it is expand-only (AGENTS.md).

## Item 1: Publishing a published post keeps its date; the date can be set back (#1)

Now: `PostService.PublishAsync` (`src/Croft.Api/Services/PostService.cs:565`) uses
`request.PublishedAt ?? now` and writes it into `PublishedAt` whatever the status. The editor's
**Publish now** (`PostEditPage.razor:421`) sends the clock's moment explicitly, so the API fix
alone is not enough. Two posts on crawford.dev lost their dates this way. (Unpublish clears
`PublishedAt`, `PostService.cs:625`; that stays as it is.)

What to do:
- API: on a post whose status is already `Published`, a `PublishRequest` with no `PublishedAt`
  changes nothing about the date or status (`UpdatedAt` may move; nothing is queued, as today).
  An explicit `PublishedAt` in the past sets it (back-dating, also on an already published post).
  An explicit future moment schedules, as today. A Draft or Scheduled post with no date publishes
  at now, as today.
- Editor: **Publish now** sends no date (`PublishedAt: null`); the API decides "now" for an
  unpublished post. On a **Published** post the button row also shows **Set date**, enabled
  only when the date/time fields hold a moment in the past that differs from the stored
  `PublishedAt` (otherwise disabled, with a hint line in the same pattern as Schedule's
  `post-schedule-hint`: "Set date needs a past date and time that differs from the published
  one."). Set date calls the publish endpoint with that moment. The date fields of a published post
  show its stored `PublishedAt` on load (check they already do).
- If an existing test asserts the old behaviour (republish resets the date), this brief changes
  that rule: replace it with the new rule's test and say so in the commit message, quoting #1.

Tests:
- API: published post with `PublishedAt` = 2026-10-01T09:00Z, `POST …/publish` with an empty body
  → `PublishedAt` still 2026-10-01T09:00Z (read back with a fresh GET); status Published; no new
  announcement row. Same post, body `PublishedAt` = 2026-09-15T08:00Z → read back 2026-09-15T08:00Z.
  Draft with an empty body → `PublishedAt` = the fake clock's now.
- E2E: create and publish a post, back-date it through the API to 2026-10-01T09:00Z, open the
  editor, click **Publish now**, then load the public post page: the meta line shows 1 October
  2026. Then set the date fields to 15 September 2026 08:00 and click **Set date**: the post page
  shows 15 September 2026 and the home page lists the post after a post published on 20 September.
Break-it-once: restore `?? now` on the published path → the empty-body API test fails; make
Publish now send the clock again → the E2E fails.

## Item 2: "Published on X, last updated on Y", and edits that are edits (#2)

Now: the post page shows `15 October 2026 · 1 min read · Updated 20 October 2026`
(`src/Croft.Web/Components/Blog/PostMeta.razor:36-52`). `PostService.UpdateAsync` sets
`ContentEditedAt = now` on every save (`PostService.cs:525`), also when only an announcement flag
or a link changed.

What to do:
- `PostMeta` gets `[Parameter] public bool LongForm { get; set; }`. With `LongForm`:
  - not edited on a later UTC day: **Published on 18 October 2026 · 7 min read**
  - edited on a later UTC day: **Published on 18 October 2026, last updated on 23 October 2026 · 7 min read**
  - no reading time (pages): the same without the ` · N min read` part.
  - no `PublishedAt` (drafts in the editor preview): as today.
- `PostHero` and `PageSurface` pass `LongForm="true"` (so static pages and the editor preview read
  the same way); `PostCard` does not, and cards keep `18 October 2026 · 7 min read` with no
  "Updated" part. Structured data (`datePublished` / `dateModified`) is unchanged.
- `UpdateAsync` moves `ContentEditedAt` only when `Title`, `Subtitle` or `MarkdownContent`
  differs (ordinal) from the stored value. `UpdatedAt` moves on every save as today.

Tests:
- bUnit for `PostMeta`: the four texts above exactly (published 2026-10-18T10:00Z, edited
  2026-10-18T22:00Z → no "last updated"; edited 2026-10-23T00:05Z → "last updated on 23 October
  2026"; with and without reading minutes), and the card form with an edit on a later day shows no
  "updated".
- API: save a post changing only `AnnounceMastodon` (fake clock advanced one day) →
  `ContentEditedAt` unchanged; change only the subtitle → it moves to the clock's now.
- E2E: a post edited (through the API, with a back-dated publish) on a later day shows the long
  line on the post page and only the date on its home-page card.
Break-it-once: set `ContentEditedAt = now` unconditionally again → the flag-only API test fails.

## Item 3: The Discussion tab refreshes until every announcement is Posted or Failed (#3)

Now: after **Announce now**, the tab keeps showing "Pending, next try …" until a reload, although
the sender posted within seconds (first real run: both networks on attempt 1). The block is read
once by `LoadAnnouncementsAsync` (`PostEditPage.razor:693`).

What to do: while the Discussion tab is loaded and any row is `Pending`, `PostEditPage` reads the
announcements again every 5 s, with an `ITimer` from the injected `TimeProvider` (the page
already uses `Time.CreateTimer` for the preview, `:880`). It stops when no row is Pending, starts
again after Announce now / Retry / Publish queues rows, and is disposed with the page. A failed
read keeps what was shown and keeps polling (log through `ApiFailureLog`, as `PublishedTextAsync`
does). Only during the interactive phase (`RendererInfo.IsInteractive`), never in prerender.

Tests:
- bUnit with `FakeTimeProvider`: a Pending row, advance 5 s → the announcements are read a second
  time; the second answer has the row Posted, advance 5 s twice more → no third read. Dispose the
  page while Pending, advance 5 s → no read after dispose.
- E2E (in `AnnouncingTests`, seeding with `AnnouncementSeed` as 12d did): seed a Pending row, open
  the Discussion tab, then update the row to Posted with a remote URL straight in the outbox table:
  within 15 s, without a reload, the tab shows Posted and the link (wait on that text, no sleeps).
Break-it-once: don't start the timer → the E2E times out on the Posted text.

## Item 4: Bluesky announcement links use the handle (#4)

Now: `BlueskyAnnouncer.Link` (`src/Croft.Api/Social/BlueskyAnnouncer.cs:187`) builds
`https://bsky.app/profile/did:plc:…/post/…` from the `at://` URI; the post's Bluesky link and the
outbox row's `RemoteUrl` get that form.

What to do: the `createSession` answer carries `handle`; `ReadSession` returns it too. A posted
announcement's URL is `https://bsky.app/profile/<handle>/post/<rkey>` when the handle is a valid
handle (not `handle.invalid`, matches the handle syntax the link parser already accepts), else the
DID form as today. The earlier-send lookup (`FindPost`, `:196`) uses the feed item's
`post.author.handle` the same way. `RemoteId` stays the `at://did/…` URI: the DID remains the
stable identity. Confirm that applying the posted link to the post (`RecordPostedAsync` →
`SocialLinkService.ApplyAsync` with the remote id) makes **no** `resolveHandle` call; if it does,
pass what is needed so it doesn't, and say so. Existing rows are not rewritten (out of scope).

Tests (fixtures in `docs/briefs/12d-fixtures` or the 12d test doubles; invented `.example`
handles): session answer with `handle: "owner.example"` → `RemoteUrl` =
`https://bsky.app/profile/owner.example/post/<rkey>`, `RemoteId` the DID URI, the post's
`BlueskyUrl` the handle form, the fake handle resolver called zero times; `handle.invalid` → DID
form; the earlier-send path with an author handle → handle form.
Break-it-once: return the DID form always → the first test fails.

## Item 5: HEAD answers like GET (#5)

Now: `curl -I https://<site>/` and `/robots.txt` answer **405**. Link checkers and uptime monitors
that use HEAD report the site as broken.

Confirm first: on an `./start-local.sh --e2e` instance, record `curl -sI` status for `/`, a post,
`/robots.txt`, `/sitemap.xml`, `/feed.xml`, `/branding/theme.css`, a fingerprinted static asset,
`/api/media/<id>`, `/health/live`. Put the table in the commit message (before and after).

What to do: every Web route that answers GET answers HEAD with the same status and headers
(Content-Type, Cache-Control, CSP, security headers) and no body. Choose the mechanism after the
probe (e.g. `MapMethods(…, ["GET", "HEAD"], …)` for the minimal endpoints, and whatever Razor
components need); if it is code that exists only because the framework maps GET without HEAD, it
gets a `docs/workarounds.md` entry and markers (AGENTS.md "Workarounds"). Constraints:
- A HEAD must never store an empty page in the output cache, and must not count as a page view.
- Antiforgery, rate limiting and `CircuitAdmissionMiddleware` treat HEAD as the safe method it is
  (no new bypass of anything a GET goes through).
- The API host is out of scope.

Tests: Web integration tests (the existing `WebApplicationFactory` style) for HEAD on `/`, a post,
`/robots.txt` and `/feed.xml`: status 200, same Content-Type and CSP header as the GET, zero body
bytes. With the output cache on (`OutputCache` category, as 14b's tests): HEAD `/` then GET `/` →
the GET body is the full page. E2E not needed beyond the suite staying green.
Break-it-once: remove the HEAD mapping for `/robots.txt` → its HEAD test gets 405.

## Item 6: Deferred minors from 12b, 12c and 12d (#6)

- **12b:** `DiscussionReply.Initial` (`src/Croft.Web/Components/Blog/DiscussionReply.razor:65`)
  throws on a lone high surrogate (`char.ConvertToUtf32`); the same code is in
  `Components/Admin/DiscussionAdminReply.razor:56`. A lone surrogate gives `"?"`. bUnit test for
  both with `"\uD83D"` and `"\uD83Dabc"`. Add IPv6 rows to `MastodonServerNameTests` (bracketed
  literal and a zone id; whatever the current rule says they should give, check the parser and
  assert that, don't change the rule).
- **12c:** `LinkChange` (`PostEditPage.razor:592`) compares the trimmed field with the untrimmed
  loaded value; compare with `loaded?.Trim()`. Test: a loaded link with surrounding spaces and an
  unchanged field sends `null` (unchanged).
- **12d:**
  - The announcement outcome log line (`SocialAnnounceHostedService.cs:373`) gains the HTTP status
    when there is one (`{Status}`, empty otherwise). Test with the `CapturingLogger` style used in the
    API tests: a 503 retry logs `503`.
  - A manual **Retry** (`AnnouncementService.AnnounceAsync` on a Failed row) resets `Attempts` to
    0, so a failed retry waits the first backoff step (1 min), not 60. Test: Failed row with
    Attempts 5, Retry, the sender fails once → `NextAttemptAt` = now + the first backoff step.
  - `SocialOptions.MastodonCanAnnounce` also requires `Mastodon.Account` (without it the earlier-send
    check cannot run). Test: everything set but Account → nothing is queued for Mastodon, and the
    Discussion tab's readiness text says Mastodon is not set up (check the existing wording and keep
    it). Production has `Social__Mastodon__Account` set, so nothing changes there.

One commit for this item is fine (they are all small); list each sub-point in the message.

## Out of scope
- Hashtags for categories and tags (#7, brief 12e), like/repost counts (#8).
- Rewriting stored DID-form Bluesky links, restoring the two lost dates on crawford.dev (the
  owner does that with Set date after release).
- HEAD on the API host.
