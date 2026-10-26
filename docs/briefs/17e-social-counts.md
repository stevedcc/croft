# Brief 17e: likes, reposts and replies on the hero, the cards and the discussion (issue #8)

Read AGENTS.md first and follow it. One commit per item, TDD, break-it-once for each key test.
Load `writing-dotnet11-code`, the `blazor-*` skills and `dotnet-webapi` before writing code. No
pushes except `brief/17e` to `origin` and ONE draft PR. E2E instances 7..9 are yours
(`dev/run-e2e.sh --instances 3 --first-instance 7`). Other briefs run on this machine at the same
time: `pgrep -af "dotnet test|run-e2e"` (excluding your own shell) before every suite run; never
run a full suite while another one runs; wait and retry.

**Owner-visible outcome.** Every post that is linked to Mastodon or Bluesky shows, as a small
overlay on its hero image and on its card on the home page (and on the category, tag and search
cards, which are the same component), how many likes, reposts and replies its announcements have
on the two networks together; the discussion section shows the split per network next to each
reply action. The numbers come with the replies the fetcher already collects, on its schedule
(10 min / hourly / daily, `src/Croft.Api/Social/SocialSchedule.cs:56-66`), never per page view.

Owner decisions: the overlay sits on the hero image; every card on the home page gets it, not only
the featured one; the overlay shows the platforms' own counts (what Mastodon and Bluesky report),
the discussion section's existing "N replies" stays Croft's count of the replies it shows.

## Facts (read these files first)
- Nothing reads, stores or returns a count today. Bluesky's `getPostThread` answer carries
  `thread.post.likeCount`, `repostCount`, `replyCount` and the parser drops them
  (`src/Croft.Api/Social/BlueskyThreadParser.cs:131-136`, root used for `uri` and `threadgate`).
  Mastodon's fetch is `GET /api/v1/statuses/{id}/context` only (`MastodonThreadSource.cs:29-30`),
  which has no counts; the status itself (`GET /api/v1/statuses/{id}`) has `favourites_count`,
  `reblogs_count`, `replies_count`. The recorded `mastodon-status.json` (counts 0 / 1 / 2) is
  unused today; `bluesky-thread.json` root has 63724 / 9545 / 8675.
- `SocialFetchOutcome.Fetched(Replies, PlatformBusyUntil)` (`ISocialThreadSource.cs:12-22`) has no
  slot for counts. `SocialFetchHostedService.StoreAsync` (:231-293) hashes the replies
  (`SocialSnapshot.cs`, reply fields only) and invalidates `CacheGroup.Discussion` only when the
  hash changes; an equal hash updates the times with `ExecuteUpdateAsync` and invalidates nothing.
- `SocialThread` (`Models/SocialThread.cs`) is one row per post and platform: the place for counts.
- Reads: `PostsController.cs:13-24` list (`PagedResult<PostSummaryDto>`, cached under
  `CacheGroup.Posts`, key `posts:list…`, projected in `PostService.ToSummaries` :695) and `:27`
  `GET api/posts/{slug}` (`PostDto`); `DiscussionController.cs:13` (`PostDiscussionDto`, cached
  under `Discussion`, anonymous reads only). `CacheGroup.Discussion` is deliberately outside
  `All` (`PublicReadCache.cs:21-24`): "a discussion refresh must not flush the posts". Keep that
  rule: counts are attached to the post DTOs **after** the posts cache, from a counts dictionary
  cached under `Discussion`, so a count change never flushes `Posts`.
- Web: `Pages/Post.razor:30` `<PostHero Post="@_post" />`, `:49` `<DiscussionSection …>`;
  `Blog/PostHero.razor` (`.post-hero__media` absolute, the fallback's icon top-right at
  `inset: 14px 18px auto auto`, content block at the bottom, 360 px / 240 px on phones);
  `Blog/PostCard.razor:59-99` (the card is one `<a>`; the chip is a sibling link top-left, z 3;
  the Featured overlay is bottom, z 2; List media is `max(34%, 96px)` wide). Home, Category, Tag,
  Search all render `PostCard` through `PostList`. Reading pages are static SSR with no inline
  style and a strict CSP; icons are inline SVG (`Layout/ProfileIcon.razor`, and
  `Admin/CategoryIcon.razor` through `IIconCatalog`, W-036). The output cache (1 min) may show a
  count up to a minute late: accepted.
- Tests to extend are listed in each item; the E2E instances have no social servers: data is
  seeded straight into the tables (`tests/Croft.Web.E2E/DiscussionSeed.cs:40-63` `LinkAsync`).
- `ReadingPageApiCountTests` counts API requests per page load: this brief adds none (the counts
  ride on the DTOs the pages already fetch).

## Item 1: contracts (one commit, no behaviour)
- `src/Croft.Shared/Contracts/Social/SocialCountsDto.cs`: `public sealed record SocialCountsDto(int Likes, int Reposts, int Replies)`.
- `PostSummaryDto` and `PostDto` gain `SocialCountsDto? Social = null` **as the last positional
  parameter** (six test files construct `PostSummaryDto` positionally; none changes).
  `PostDiscussionDto` gains `SocialCountsDto? MastodonCounts = null, SocialCountsDto? BlueskyCounts = null` at the end.
- `src/Croft.Api/Social/SocialCounts.cs`: `public sealed record SocialCounts(int Likes, int Reposts, int Replies)`;
  `SocialFetchOutcome.Fetched` gains `SocialCounts? Counts = null`.
- `SocialThread` gains `int? LikeCount`, `int? RepostCount`, `int? ReplyCount`, `DateTime? CountsAt`
  (all nullable: unknown until the first fetch that returns them); configuration, and an additive
  migration `SocialThreadCounts` (passes the guard as is). Update the comment at
  `Models/SocialReply.cs:34` ("no counts" is no longer true: the thread row holds them).
- `tests/Croft.Web.E2E/DiscussionSeed.cs` `LinkAsync` gains an optional
  `(SocialCounts mastodon, SocialCounts bluesky)?` argument that writes the four columns.

## Item 2: the sources return the counts
- Bluesky: `BlueskyThreadParser` reads the root's three counts (missing → `Counts` null, not 0);
  `BlueskyThreadSource` passes them into `Fetched`. Tests in `BlueskyThreadParserTests` /
  `BlueskyThreadSourceTests`: the recorded thread yields `(63724, 9545, 8675)`; a root without the
  fields yields null. Break-it-once: drop the `likeCount` read, the first test must fail.
- Mastodon: after the context request succeeds, `MastodonThreadSource` requests
  `GET https://{server}/api/v1/statuses/{id}` with the same client and limits; parse
  `favourites_count`, `reblogs_count`, `replies_count` (a new `MastodonStatusParser`, or a
  method on the thread parser). A failed or non-2xx status request (including 429) does **not**
  fail the fetch: the replies are still `Fetched`, `Counts` null, and a 429's retry-after still
  reaches `PlatformBusyUntil` (the rate limit is shared). Tests: the recorded status yields
  `(0, 1, 2)`; status 500 → replies fetched, counts null; status 429 → counts null and
  `PlatformBusyUntil` set. `SocialFetchHarness.MastodonAnswerFor` (:82-87, assumes
  `/context` at `segments[^2]`) gets a branch for the status path. Break-it-once: make the status
  request's failure throw, the 500 test must fail.

## Item 3: the fetcher stores the counts and invalidates when they change
`StoreAsync`: both branches write the four columns when `Counts` is not null (a null leaves the
stored values: a count request that failed must not blank the overlay). Equal snapshot hash but
different counts (or first counts) → the thread row is updated **and** `Discussion` is
invalidated (local and NOTIFY, the same path as a changed snapshot; put the counts comparison
beside the hash comparison, not inside `SocialSnapshot`, whose hash stays reply-only).
Tests in `SocialFetchTests` and `DiscussionCacheGroupTests`: counts stored on first fetch; same
replies and same counts → no invalidation; same replies, likes +1 → `Discussion` invalidated and
`Posts` not; counts null on a later fetch → the earlier values stay. Break-it-once: remove the
counts comparison, the "likes +1" test must fail.

## Item 4: the API attaches the counts
- `ISocialCountsReader.GetAsync(ct)`: one query over `SocialThreads` where `LikeCount` is not null,
  returning per post the per-platform counts; cached under `CacheGroup.Discussion`, key
  `discussion:counts`, through `IPublicReadCache.GetOrCreateAsync` like the discussion read.
- The public list (`GET api/posts` with `PublicView`), the post read (`GET api/posts/{slug}`),
  related posts and search attach `Social` **after** their own cache: the sum over the platforms
  that have counts, null when none has. The discussion read fills `MastodonCounts` and
  `BlueskyCounts` from the same reader (it is already under `Discussion`).
- Tests (`DiscussionReadTests`, the posts list and post tests): a post with counts on both
  platforms lists `Social` as the sum; counts on one platform only → that platform's numbers; a
  post with no threads → null; **a count change reaches the list without any post write** (seed
  counts, read the list, change the counts the way the fetcher does, invalidate `Discussion`
  only, read again: the new sum; the `Posts` cache entry was not evicted: assert through the
  cache's own counters or a second read of an unchanged list key). Break-it-once: attach the
  counts before the cache instead of after, the last test must fail.

## Item 5: the overlay and the discussion split
- `src/Croft.Web/Services/SocialCountFormat.cs` `Compact(int)`: below 1 000 the number; below
  1 000 000 thousands with one decimal and the trailing `.0` dropped (`1k`, `1.2k`, `63.7k`,
  `999.9k`); from 1 000 000 millions the same way (`1M`, `1.2M`); invariant culture, rounding
  half away from zero. Unit tests for 0, 999, 1000, 1049, 1050, 1200, 63724, 999949, 999950
  (`1M`), 1200000.
- `Components/Blog/SocialCounts.razor` + `.razor.css` (presentational: `[Parameter] SocialCountsDto? Counts`,
  `[Parameter] string Size = "normal" | "compact"`): renders nothing when `Counts` is null;
  otherwise `<span class="social-counts" role="group" aria-label="12 likes, 3 reposts, 4 replies">`
  with three items, each an inline SVG (`IIconCatalog` paths `Favorite`, `Repeat`, `ChatBubble`,
  or the nearest Material names the catalogue has; `aria-hidden="true"`, `fill="currentColor"`)
  and the compact number. Singular/plural in the label. A pill: `background: rgba(15, 23, 42, .72)`,
  `color: #fff`, `--font-ui` 12.5 px (11 px compact), 14 px icons (12 px compact), `gap 10px`,
  `padding 4px 10px`, `border-radius 999px`, `pointer-events: none` (the cards' pill sits inside
  the card link and must not be a click target of its own). White on that pill over a white
  image composites to about 7:1, over black 21:1: always above 4.5:1.
- `PostHero.razor`: `<SocialCounts Counts="@Post.Social" />` inside `.post-hero`, positioned
  `top: 14px; left: 18px; z-index: 2` (top-right belongs to the fallback's icon, the bottom to the
  text block). Phones: `top: 10px; left: 12px`. The editor preview and draft review pass a
  `PostDto` without counts and show nothing.
- `PostCard.razor`: `<SocialCounts Counts="@Post.Social" Size="…" />` inside `.post-card__media`
  (inside the card link; `pointer-events: none`), `top: 8px; right: 8px; z-index: 3` (the chip is
  top-left), `compact` for the List variant, normal for Featured.
- `DiscussionSection.razor:20-29`: after "Reply on Bluesky" a `<span class="discussion__counts">`
  "12 likes · 3 reposts" from `BlueskyCounts`, and the same after the Mastodon form from
  `MastodonCounts`; nothing when null. Styles in the section's own `.razor.css`; static markup,
  no script, no inline style (the CSP).
- bUnit (`tests/Croft.Web.Tests/Components`): `SocialCountsTests` (null renders nothing; the
  label text; the three numbers compact; compact size class), `PostHeroTests` and `PostCardTests`
  (the pill is present with counts, absent without, inside `.post-card__media`),
  `DiscussionSectionTests` (per-platform lines). Break-it-once per class: remove the null guard,
  the "renders nothing" test must fail.
- E2E (`HomeTests`, `PostPageTests`, `DiscussionTests`): a post seeded with
  `LinkAsync(…, counts: (mastodon (5, 2, 1), bluesky (7, 3, 4)))` shows `12`, `5`, `5` on its hero
  pill (`aria-label` "12 likes, 5 reposts, 5 replies") and on its home card; a post linked
  without counts shows no pill; the discussion section shows "5 likes · 2 reposts" and
  "7 likes · 3 reposts" by the right actions. `AccessibilityPublicMatrixTests`' fixture seeds
  counts on its post so the pill is inside the axe scans of home and post in both themes and
  both viewports. Break-it-once: hide the pill with `display: none`, the hero test must fail.

## Item 6: suites
`dotnet build Croft.slnx`; API, Web and E2E (`dev/run-e2e.sh --instances 3 --first-instance 7`)
once each after the last change; `Category=Container` once (a migration was added). Summary lines
verbatim into the last commit message and the report. Then `gh pr checks --watch`.

## Report
Per item: what changed, the break-it-once evidence, suites, the PR's `ci` run URL. Two
screenshots (`Page.ScreenshotAsync` from an E2E probe, 1440 and 390 wide, in `/tmp`, paths in the
report): the hero with the pill, the home page with pills on a featured and a list card. What is
left undone.

## Out of scope
The admin discussion panel; counts on related-post links; history or trends; retrying a failed
status request sooner than the schedule; any change to which replies are shown.
