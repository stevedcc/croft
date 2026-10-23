# Brief 12c: the owner links a post to its announcements and moderates its discussion

Read AGENTS.md first and follow it. One commit per item, **test first: write the test, run it,
watch it fail for the right reason, then the code** (break-it-once is an extra check, never the
red evidence instead of it). E2E twice at the end, the final whole-branch review.

**Spec (approved by the owner 2026-10-21):** `docs/superpowers/specs/2026-10-19-social-replies-design.md`.
Read "Linking a post to its announcements", "Blocklist (moderation)", "Caching" and "Briefs" item 3
in full first. Where this brief is more specific than the spec, the brief wins.

**What exists (12a and 12b, accepted and released in 0.6.7):**
- API, `src/Croft.Api/Controllers/DiscussionController.cs` (roles as strings, no named policies):
  - `GET api/posts/{id:guid}/discussion/admin` (Admin, Editor) → `PostDiscussionAdminDto`, or 404.
  - `POST api/posts/{id:guid}/discussion/refresh` (Admin, Editor) → 202. It marks the post's threads
    due now; the fetcher picks them up on its next tick. It does not fetch synchronously.
  - `GET api/discussion/blocks` (**Admin only**) → `IReadOnlyList<SocialBlockDto>`, newest first.
  - `POST api/discussion/blocks` (Admin, Editor), body `CreateSocialBlockRequest` → 201, or 200 with
    the existing block (idempotent), or 400.
  - `DELETE api/discussion/blocks/{id:guid}` (**Admin only**) → 204 or 404. Unhide re-queues that
    platform's threads for a fetch at once.
- Creating a block **deletes** the matching stored replies and their subtrees, so the admin read
  never contains a blocked reply. Hidden items are visible only on the blocks list.
- DTOs in `src/Croft.Shared/Contracts/Social/`: `PostDiscussionAdminDto(Discussion, Threads)`,
  `DiscussionThreadStatusDto(Platform, LastSuccessAt?, LastAttemptAt?, LastError?, NextDueAt?,
  StoredReplies, Stale)`, `DiscussionReplyDto` (with `ExternalId` and `AuthorAccountId`, which is
  what an account block matches), `CreateSocialBlockRequest(Platform, Kind, ExternalId, Label?,
  Note?)`, `SocialBlockDto(Id, Platform, Kind, ExternalId, Label?, Note?, CreatedBy, CreatedAt)`.
- Post link fields: `CreatePostRequest`/`UpdatePostRequest` end with `string? MastodonUrl,
  string? BlueskyUrl`; `PostDto` carries both. **On update: null = unchanged (link, thread and
  replies kept), "" = clear (deletes that platform's thread and replies), any other value =
  validated and set.** On create, null = no link.
- A bad link is a 400 ProblemDetails whose `Detail` starts with the field name: "MastodonUrl must be
  a post on …", "BlueskyUrl must be a post of the configured Bluesky account.", "… is not
  configured on this site …". There is no per-field `errors` dictionary.
- Web: `IBlogApiClient` has only `GetDiscussionAsync(slug)`. The write pattern to copy is in
  `BlogApiClient.cs` (`SendAsync`, `EnsureSuccessWithDetailAsync`, then `EvictReadingPagesAsync()`,
  which evicts this pod's output-cached reading pages, 14b).
- 12b's public components `DiscussionSection`/`DiscussionReply` in `Components/Blog/`, and the E2E
  seed `tests/Croft.Web.E2E/DiscussionSeed.cs` (`PublishPostAsync`, `LinkAsync`, `AddRepliesAsync`).

**Don't change the API, the DTOs or the public components.** If something you need is missing,
stop and report.

**Owner-visible outcome:**
- In the post editor, under "Discussion", the owner pastes the Mastodon and Bluesky URLs of a post's
  announcements, and can change or clear them. A wrong URL is explained under its field.
- On an existing post's edit page, a "Discussion" tab shows each platform's fetch status, a
  "Refresh now" button, and every stored reply with "Hide reply" and "Hide account".
- A hidden reply leaves the panel and the public post page at once on that Web pod (within a minute
  on the others: accepted in the spec).
- An admin page "Hidden replies" lists every block with "Unhide".

**CSP impact: none.** Admin pages of the existing kinds ("admin page", "post editor"); MudBlazor
components only, no new script, no inline style attribute written by our code.
`CspSnapshots.txt` byte-identical. Anything that seems to need a CSP change: stop and report.

## Decisions (made; don't re-open)
- **Posts only.** The Discussion fields and the Discussion tab appear for posts, never for pages
  (`IsPage`). The API accepts links on pages; the editor simply doesn't offer them.
- **Send a link only when it changed.** On update: the field equal to what was loaded → send null
  (unchanged); emptied → send ""; anything else → send the trimmed value. On create: empty → null.
  This way an unrelated edit never re-validates (or fails on) a link that was saved earlier.
- **Field errors by prefix.** A save that fails with a `Detail` starting `MastodonUrl` or
  `BlueskyUrl` shows that message under the matching field (MudTextField `Error`/`ErrorText`) and
  not in the page's general error line; any other failure keeps today's general error line. The
  field's error clears when its text changes.
- **Hide = create a block, Unhide = delete it.** Labels so the blocks list makes sense without the
  reply: reply block `Label` = `"@{handle}: {first 80 characters of the reply's text}"` (text = the
  `ContentHtml` with tags stripped and whitespace collapsed; "…" appended when cut);
  account block `Label` = `"{display name} (@{handle})"`. `ExternalId` = the reply's `ExternalId`
  for a reply block, its `AuthorAccountId` for an account block.
- **Confirm before hiding and unhiding** (MudBlazor dialog, like the existing admin deletes), with an
  optional "Note" field on the hide dialog (sent as `Note`). Hide account's dialog says it hides every
  reply of that account on every post.
- **Admin only in the Web**, like the edit page today (`[Authorize(Roles = "Admin")]`), even where the
  API also allows Editor.
- **The panel's replies** are listed flat in the API's order (nested answers indented one step per
  level, depth as the DTO gives it), each with avatar (the 12b `/social/avatar/{hash}.webp`
  route, initial fallback), display name, handle, platform, absolute time (UTC), the content
  (rendered as `MarkupString`: the API's sanitiser is the one sanitiser, same comment as 12b), the
  content warning as plain text above it, a "View on …" link (`rel="nofollow ugc noopener"`,
  `target="_blank"`), and the two buttons. A new admin component; don't reuse or change 12b's
  public `DiscussionReply`.
- **Refresh now** shows "Refresh queued. New replies appear within a few minutes." and reloads the
  status after the call; it does not poll.
- **Stale** (`Stale = true`): the status line says "Not shown to readers: no successful fetch for
  48 hours."

## Item 1: Web client methods
- Add to `IBlogApiClient`/`BlogApiClient`: `GetDiscussionAdminAsync(Guid postId, ct)` (404 → null),
  `RefreshDiscussionAsync(Guid postId, ct)`, `GetSocialBlocksAsync(ct)`,
  `CreateSocialBlockAsync(CreateSocialBlockRequest, ct)` (returns the `SocialBlockDto`, 200 or 201),
  `DeleteSocialBlockAsync(Guid id, ct)` (404 → false). Staff calls with the user's token, like the
  existing admin methods.
- Create and delete call `EvictReadingPagesAsync()` after success; refresh and the reads do not.
- `FakeBlogApiClient`: the new methods with recorded calls and seedable results, in its existing style.
- Warm-up drift guard (`tests/Croft.Api.Tests/Warmup/WarmupRoutesDriftTests.cs`): each new method in
  `NotWarmed` with a reason ("staff read" / "write").
- Tests (`BlogApiClient` against a stub handler): each method's route, verb and body; 404 handling;
  create/delete evict the reading pages (assert on the output cache's tag eviction as 14b's tests
  do) and refresh does not. Break-it-once: drop the eviction from `CreateSocialBlockAsync`; the
  eviction test fails.

## Item 2: the editor's Discussion fields
- `PostEditModel` gets `MastodonUrl` and `BlueskyUrl`; loading fills them from `PostDto`.
- `PostEditorForm`: for posts only, after the banner picker, a group with the visible heading
  "Discussion" and two MudTextFields: "Mastodon post URL" (helper "The post that announces this
  one on Mastodon"; the Web doesn't know the server name, so no example host); "Bluesky post URL" (helper
  "The post that announces this one on Bluesky"). `inputmode="url"`, `spellcheck="false"`. Not part
  of the draft backup.
- `BuildCreateRequest`/`BuildUpdateRequest` per the decision above (remove the "not edited here yet"
  echo).
- Field errors per the decision above.
- Tests (bUnit with `FakeBlogApiClient`), for create and update: untouched fields → null; emptied →
  ""; changed → trimmed value; create with empty → null; a page never shows the fields and never
  sends them (null on create and update). A failure whose `Detail` starts `MastodonUrl` shows under
  the Mastodon field and not in the general error line; same for Bluesky; another failure stays in
  the general line; typing in the field clears its error. Break-it-once: always send the field's
  value on update; the "untouched → null" test fails.

## Item 3: the Discussion tab on the edit page
- `PostEditPage`: a third tab "Discussion" beside Preview and Feedback, for an existing post only (not
  on new, not for pages). It loads `GetDiscussionAdminAsync` when the tab is first opened, not on page
  load (the editor must not get slower).
- Content, top to bottom:
  - One status line per linked platform: "Mastodon: last fetched {relative} ({n} replies stored)",
    then "Last attempt failed {relative}: {LastError}" when `LastError` is set, "Next fetch
    {relative}" when `NextDueAt` is set, and the stale line when `Stale`. No linked platform: "No
    Mastodon or Bluesky post is linked. Add one under Discussion in the form." and nothing else.
  - "Refresh now" (disabled while its call runs).
  - "No replies yet." or the replies per the decision above, with "Hide reply" and "Hide account".
- Hide: dialog → `CreateSocialBlockAsync` → reload the admin read (the reply and its subtree are gone;
  for an account, all of that account's replies on this post). Errors in the tab's own `role="alert"`
  line.
- Tests (bUnit): status lines for: fetched OK; failed with error; stale; no links. Refresh calls the
  API once and shows the queued message. Hide reply sends `Kind = Reply`, the reply's `ExternalId`
  and the label format exactly (including the 80-character cut with "…" and the tag stripping);
  Hide account sends `Kind = Account` with `AuthorAccountId`; cancelling the dialog sends nothing.
  The tab is absent on a new post and on a page; opening the editor does not call the admin read
  until the tab is opened. Break-it-once: send `ExternalId` for an account block; its test fails.

## Item 4: the "Hidden replies" page
- `/admin/discussion/blocks`, `[Authorize(Roles = "Admin")]`, the existing admin page chrome (copy
  `TagsPage.razor`: `BlogPageTitle`, the W-008 `HeadContent` block with its markers, `PageHeader`,
  the `admin-table`).
- Table, newest first: Platform, What ("Reply" / "Account"), Label, Note, Hidden by, When (absolute,
  UTC), and "Unhide". Empty: "Nothing is hidden."
- Unhide: confirm dialog ("The reply comes back at the next fetch, if it still exists.") →
  `DeleteSocialBlockAsync` → reload. 404 (someone else unhid it): reload and say "Already unhidden."
- Sidebar: under the Admin links in `SidebarNav.razor`, `Hidden replies` with icon `VisibilityOff`.
- Register the route in `RouteAuthorizationGuardTests` (Admin) and wherever else the guard tests
  demand it.
- Tests (bUnit): rows in order with each column; empty state; Unhide confirm → delete called once →
  row gone; cancel → nothing; 404 → "Already unhidden." Break-it-once: drop the confirm dialog; the
  cancel test fails.

## Item 5: E2E, accessibility, screenshots
- **No outbound calls, no real people.** The E2E instances keep 12b's settings (Social on, no
  Mastodon server, no Bluesky DID). Seed links and replies with `DiscussionSeed` (invented names,
  `.example` hosts) exactly as 12b's tests do. Never call Mastodon or Bluesky.
- Tests, logged in as admin:
  - The editor shows the Discussion fields for a post and not for a page. Saving a post with a
    Mastodon URL gets the API's "not configured" 400 (that is what these instances answer) and the
    message appears under the Mastodon field, the rest of the form unchanged.
  - A seeded linked post with three replies (one with an answer, two by the same account): the
    Discussion tab shows the status line and the replies. Hide reply on the one with an answer →
    it and its answer leave the panel, and the public post page (fetched anonymously afterwards on
    the same instance) no longer shows them. Hide account → both replies of that account leave the
    panel and the public page.
  - The Hidden replies page lists both blocks with their labels; Unhide one → it leaves the list.
  - Refresh now shows the queued message.
- Axe (serious/critical = 0), light and dark: the editor with the Discussion fields, the edit page
  with the Discussion tab open, the Hidden replies page.
- Screenshots in `ScreenshotTests`: the Discussion tab and the Hidden replies page at 1440 and 390,
  light and dark (element or page shots, as 12b did). Look at two of them and say how they read.
- Break-it-once: skip `EvictReadingPagesAsync` in create; the "public page no longer shows them"
  test fails.

## Out of scope
- Announcing on publish, announcement text, outbox, credentials, "Delete announcement" (12d).
- Any API, DTO or public-component change; Editor-role access in the Web.
- Cross-pod eviction (a minute of staleness is accepted); polling after "Refresh now".
- A field for the Mastodon server name in the Web (the Web doesn't know the API's Social settings).
