# Brief 12e: categories and tags carry hashtags, and the default announcement uses them

Read AGENTS.md first and follow it. One commit per item, **test first: write the test, run it,
watch it fail for the right reason, then the code** (break-it-once is an extra check, never the
red evidence instead of it). E2E twice at the end, then the final whole-branch review.

**Source:** GitHub issue stevedcc/croft #7 and the owner's decisions of 2026-10-23 (below).
Checked against `main` at 19883b2; confirm each "Now:" before changing it.

**Why:** link-only posts from a small account get little reach; discovery on Mastodon and Bluesky
is driven by hashtags. The owner's own category and tag names ("AI Development") have no
following, so each category and tag maps to **established hashtags the owner chooses**, not to a
hashtag generated from its name.

**Owner decisions (settled, do not reopen):**
- A category or tag has a list of 0..10 hashtags. Each applies to **both** networks unless marked
  **Mastodon only** or **Bluesky only**.
- The default announcement text appends **at most 3** hashtags: the category's first, then the
  tags', deduplicated, in `#CamelCase` as the owner typed them (screen readers read CamelCase word by
  word; both networks match case-insensitively).
- When the default text is too long, **shorten the subtitle first and keep the hashtags**.
- A custom announcement text (the owner typed one) is sent as typed: no hashtags are added to it.

**Owner-visible outcome:** on Tags & categories, each row gets its hashtags; the editor's two
announcement previews show the hashtags each network will get; a post published with the default
text goes out as `Title\n\nSubtitle\n\n#Homelab #SelfHosted #DotNet\n\nhttps://…/post/slug`, and on
Bluesky those are tag facets (12d's `BlueskyFacets.All` already finds typed hashtags; check that it
finds these too).

**CSP impact: none.** `CspSnapshots.txt` stays byte-identical. Anything that seems to need a CSP
change: stop and report.

**Parallel with 12f:** 12f edits `SocialAnnounceHostedService.cs` (the outcome log line),
`AnnouncementService.cs`, `BlueskyAnnouncer.cs`, `PostEditPage.razor`, `PostEditorForm.razor` and
`PostMeta.razor`. Keep your changes in those files to the lines named here, so the two branches
merge cleanly; the orchestrator runs a merge check while both are open.

## Item 1: data, rules, DTOs, export

Now: `Category` (`src/Croft.Api/Models/Category.cs`) and `Tag` (`Models/Tag.cs`) have no hashtags;
`CategoryDto`, `TagDto`, `UpdateCategoryRequest`, `UpdateTagRequest` (`src/Croft.Shared/Contracts/`)
neither.

What to do:
- Shared contract (`src/Croft.Shared/Contracts/Social/`): `public enum HashtagNetworks { Both,
  MastodonOnly, BlueskyOnly }` and `public record HashtagDto(string Name, HashtagNetworks Networks)`.
  `Name` is stored **without** the `#`.
- Model: `Category.Hashtags` and `Tag.Hashtags`, a list kept in order, mapped to one **nullable
  `jsonb` column `Hashtags`** on each table (EF Core JSON mapping; null reads as an empty list).
  Expand-only migration: two nullable columns, nothing else.
- DTOs: `CategoryDto` and `TagDto` gain `IReadOnlyList<HashtagDto> Hashtags` (last positional
  parameter, default empty); `UpdateCategoryRequest` and `UpdateTagRequest` gain
  `IReadOnlyList<HashtagDto>? Hashtags = null` where **null leaves the list unchanged** (the Related
  switch's save and a rename must not wipe it).
- Rules, in `src/Croft.Shared/Social/Hashtags.cs` (used by API validation and the Web form, so both
  agree): a leading `#` or `＃` is stripped and surrounding white space trimmed; the name is 1..64
  characters of letters, digits and `_` (`\p{L}`, `\p{Nd}`, `_`), with at least one letter, and
  must be a hashtag `BlueskyFacets` finds when written as `#Name` (assert this in a test, don't
  duplicate the regex); at most 10 per category or tag; two entries that differ only in case are a
  duplicate. A violation is a 400 problem detail whose message names the field and the hashtag, in
  the style of the existing validation messages.
- Content export (`ContentFormat.cs`, `CategoryEntry`, the tag entry): add an optional `Hashtags`
  field; an archive without it imports with no hashtags. `formatVersion` stays 1 (an additive
  optional field); if the format's tests say otherwise, follow them and explain.
- The category and tag caches carry the new field (they cache the DTOs; check the invalidation
  still covers an update that changes only the hashtags).

Tests (API): create a category, `PUT` it with `["#Homelab", "SelfHosted" (BlueskyOnly)]` → a GET
returns `Homelab` (Both), `SelfHosted` (BlueskyOnly) in that order; a later `PUT` that renames only
(Hashtags null) keeps both; `Hashtags: []` clears them; `"#9"`, `"two words"`, an 11th entry,
`homelab` beside `Homelab` → 400 each with the message naming the hashtag. The same for a tag,
plus the Related-switch save keeps the list. Export → import round trip keeps the lists; an
archive written without the field imports with empty lists. Migration guard test passes (no
contracting operation).
Break-it-once: map the update's null as "clear" → the rename-keeps-hashtags test fails.

## Item 2: the default text with hashtags, and its fitting

Now: `AnnouncementText.Default(title, subtitle)` and `FitDefault(platform, title, subtitle,
postUrl)` (`src/Croft.Shared/Social/AnnouncementText.cs`) know nothing of hashtags.

What to do, all in `AnnouncementText` (shared, so the editor's preview and the sender agree):
- `public static IReadOnlyList<string> SelectHashtags(SocialPlatform platform,
  IReadOnlyList<HashtagDto> category, IEnumerable<IReadOnlyList<HashtagDto>> tags)`: the category's
  entries in order, then each tag's in the order the post lists its tags (by name, ordinal ignore
  case, as `PostService` sorts them); drop those not for this platform; drop case-insensitive
  duplicates, keeping the first; take at most `MaxHashtags = 3`. Returns `#Name` strings.
- `Default(title, subtitle, hashtags)`: title, then `\n\n` subtitle when there is one, then `\n\n`
  and the hashtags joined by single spaces when there are any. The two-argument overload stays
  (the editor's placeholder uses it) and means "no hashtags".
- `FitDefault(platform, title, subtitle, hashtags, postUrl)`, in this order until it fits:
  1. the full text;
  2. the subtitle cut grapheme by grapheme, ending in `…` (as today), hashtags kept;
  3. no subtitle, all hashtags;
  4. no subtitle, hashtags dropped from the **end** one at a time;
  5. no hashtags, the title cut as today.
  The existing four-argument overload keeps its behaviour (no hashtags).

Tests (Web unit project or a Shared test, wherever `AnnouncementText`'s tests live now), with exact
expected strings:
- Selection: category `[Homelab, SelfHosted(BlueskyOnly)]`, tags `DotNet: [DotNet]`, `Kubernetes:
  [homelab, Kubernetes(MastodonOnly)]` → Mastodon `#Homelab #DotNet #Kubernetes`, Bluesky
  `#Homelab #SelfHosted #DotNet` (the `homelab` duplicate dropped, cap 3). No category → the tags'
  only. No hashtags anywhere → the text is exactly today's default.
- Fitting on Bluesky (limit 300, URL counted as written): build a title, subtitle and URL whose
  full text is 320 graphemes; the result keeps all three hashtags, ends the subtitle in `…`, and is
  ≤ 300 by `BlueskyLength(Compose(...))`. A title that alone leaves room for only one hashtag →
  step 4 keeps exactly the first. Work out each fixture's lengths in the test's comments.
- Mastodon counts the URL as 23: the same text that needs cutting on Bluesky fits whole on
  Mastodon.
Break-it-once: swap steps 2 and 4 (drop hashtags before cutting the subtitle) → the 320-grapheme
test fails.

## Item 3: the sender uses it

Now: `SocialAnnounceHostedService.Compose` (`:200-219`) calls the four-argument `FitDefault`; the
post query (`:195-197`) reads no category or tags.

What to do: the query also projects the category's hashtags and each tag's (one query; check the
SQL, no N+1); `Compose` passes `SelectHashtags(...)` to the new `FitDefault`. A custom
`AnnouncementText` path is untouched. Change only these lines of the file (12f edits its log line).

Tests (API, the 12d sender tests' fakes): a post with category and tags carrying hashtags, default
text → the Mastodon fake receives `Title\n\nSubtitle\n\n#Homelab #DotNet #Kubernetes\n\n<url>` and
the Bluesky fake a text with three `app.bsky.richtext.facet#tag` facets whose byte ranges cover
`#Homelab`, `#SelfHosted`, `#DotNet`; the same post with a custom text → sent unchanged, no tag
facets added beyond those in the custom text. Capture the SQL: the post read is one statement.
Break-it-once: call the old `FitDefault` again → the Mastodon text test fails.

## Item 4: Tags & categories edits the hashtags

Now: `/admin/tags` (`src/Croft.Web/Components/Pages/Admin/TagsPage.razor`) edits names, the
category icon and the tag's Related switch.

What to do: a component `Components/Admin/HashtagListEditor.razor` (parameters
`IReadOnlyList<HashtagDto> Value`, `EventCallback<IReadOnlyList<HashtagDto>> ValueChanged`, `string
Label`; one row per hashtag: a text field "Hashtag", a select "Networks" with **Both / Mastodon only
/ Bluesky only**, a Remove button with an accessible name that includes the hashtag; an "Add
hashtag" button; the Shared rules' message shown under the row that breaks them, before any save).
`TagsPage` shows a row's hashtags as `#A #B` text in a new "Hashtags" column and edits them with
this component in the row's existing edit mode, saved by the row's existing Save. A hint under the
editor: "Use established hashtags in CamelCase, e.g. #SelfHosted. Up to 3 per announcement:
category first, then tags." The category and tag tables share the component (no duplicated
markup). Interactive page as today; follow the `blazor-*` skills.

Tests:
- bUnit for `HashtagListEditor`: add, change the network, remove → `ValueChanged` with the expected
  list; `two words` shows the rule's message and raises no change.
- E2E: on `/admin/tags`, create a category, add `#Homelab` (Both) and `SelfHosted` (Bluesky only),
  Save, **reload the page**: the column shows `#Homelab #SelfHosted` and the editor reopens with
  Bluesky only on the second. Rename the category, Save, reload: the hashtags are still there.
- Accessibility test for the page stays green (the existing axe pass covers the new controls).
Break-it-once: drop `Hashtags` from the page's update request → the reload assertion fails.

## Item 5: the editor's previews show them

Now: `AnnouncementPreview.razor:29-30` composes `FitDefault(Platform, Title, Subtitle, PostUrl)`;
the editor knows the post's category id and tag names but not their hashtags.

What to do: `AnnouncementPreview` gets `[Parameter] public IReadOnlyList<string> Hashtags` (the
selected `#Name`s for its platform) and uses the five-argument `FitDefault`. The editor page works
out each platform's list with `SelectHashtags` from the categories and tags it already loads (the
DTOs now carry hashtags; if the editor loads tags only by name, load the `TagDto`s it needs once,
with `[PersistentState]` per AGENTS.md "Interactivity", not per keystroke). Changing the category
or a tag in the form updates both previews and their counts at once. The text field's placeholder
stays the two-argument default. Touch `PostEditPage.razor` / `PostEditorForm.razor` only where the
previews get their parameters (12f edits the action buttons and the announcements polling).

Tests:
- bUnit for `AnnouncementPreview`: with hashtags, the shown text and the count are exactly those of
  `Compose(FitDefault(...5 args...), url)`.
- E2E (in `AnnouncingTests`): give a category `#Homelab` (Both) and `SelfHosted` (Bluesky only)
  through the API, open a new post, pick that category: the Mastodon preview's text ends in
  `#Homelab` before the URL, the Bluesky preview's in `#Homelab #SelfHosted`, and each count equals
  the length of the shown text by that network's rule (compute it in the test from the text, as
  12d's count tests do). Pick no category: the hashtags disappear from both.
Break-it-once: don't pass `Hashtags` to the previews → the E2E fails on the Mastodon text.

## Item 6: documentation

The admin and announcing documentation (wherever 12d documented the default text and its fitting;
find it) describes hashtags, the cap of 3, the order, the per-network choice and the fitting order.
Screenshots: if 12d's `ScreenshotTests` capture Tags & categories or the editor's previews, update
them through the test, not by hand.

## Out of scope
- Suggesting hashtags, checking their popularity, or generating them from names.
- Hashtags in a custom announcement text, or in anything other than announcements (pages, feeds,
  structured data).
- Like/repost counts (#8) and everything in 12f.
