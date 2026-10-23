# Brief 07k: a tag can stay out of "Related posts"

Read AGENTS.md first and follow it. One item: implement inline (`executing-plans`), TDD,
break-it-once for each key test, E2E twice at the end, the final whole-branch review. Brief 08a runs
at the same time (Web state for HA: token store, Data Protection, health, the editor's draft
backup); it does not touch tags or related posts. When the orchestrator says another brief was
accepted, `git fetch origin` and `git merge --no-edit origin/blog-framework` (never rebase).

**Owner-visible outcome:** the owner tags AI-assisted posts "AI-assisted", switches that tag's
"Counts towards related posts" off on the tags admin page, and from then on two posts that share
only that tag are not shown as related to each other. The tag still shows on the cards and the post
page, and `/tag/ai-assisted` still lists its posts.

## Item 1: per-tag "counts towards related posts"

**Now:** related posts are ranked in `Croft.Api/Services/PostService.cs:160-162` by
`2 × (shared tags) + 1 × (same category)`, then newest first. Every tag counts, so a provenance tag
such as "AI-assisted" outranks the topic: two unrelated AI-assisted posts score 2, a same-category
post without shared tags scores 1.

**Do:**
- `Tag` gets `CountsTowardsRelated` (bool, NOT NULL, default `true` in the database and in C#), in
  a new migration. It is additive (a column with a default), so it follows the expand rule; no
  `[ContractMigration]`. Existing tags keep counting.
- The related-posts ranking counts only shared tags whose `CountsTowardsRelated` is true. The
  category bonus and the tie-break are unchanged.
- `TagDto` carries `CountsTowardsRelated`. Every place that builds a `TagDto` from a `Tag` passes
  the real value (no default parameter that could hide a missed site); a `TagDto` built for a tag
  that doesn't exist yet (the editor's new-tag suggestion) uses `true`.
- `UpdateTagRequest` gains `bool? CountsTowardsRelated` after `Name`: `null` leaves it unchanged,
  so a Web of the previous release (no field) keeps working against this API during a rolling
  deploy. `Name` stays required and behaves as now.
- Tags admin page (`Components/Pages/Admin/TagsPage.razor`): a new column "Related" between
  "Posts" and "Actions", holding a switch per tag, on = counts. Toggling saves at once through
  `UpdateTagAsync` (name unchanged) and shows the result like a rename does; on failure the switch
  returns to its saved state and the page shows the error the way the page shows other save
  errors. Accessible name per switch: "Counts towards related posts: {tag name}". At 390 px wide
  the table must not overflow the page (check the existing admin-table behaviour and follow it).
- Public pages don't change: tags look the same on cards, the post page and the tag page.

**Tests:**
- API (`RelatedPostsControllerTests`, each test creating its own posts, tags and category with
  unique names): (a) posts A and B share only a tag with the flag off, and C shares A's category:
  for A, C is related and B is not; (b) the same tag with the flag on: B ranks above C (as today);
  (c) a tag with the flag off still appears in A's `Tags` and its tag page lists A.
- API: a new tag has `CountsTowardsRelated` true; `PUT` with `CountsTowardsRelated: false` stores
  it and returns it; `PUT` with only `Name` leaves it as it was; the field is returned in the tag
  list.
- Migration: the existing migration contract guard passes without `[ContractMigration]`.
- bUnit (`TagsPageTests`): the switch reflects the tag's value, toggling calls `UpdateTagAsync`
  with the tag's current name and the new value, and a failed save puts the switch back.
- E2E (one test, its own posts and tag): sign in as admin, create two posts sharing only a new tag
  and a third in the first post's category, switch the tag off on the tags page, open the first
  post: the related list shows the third post and not the second; the tag pill is still on the
  post.
- Break-it-once: remove the flag condition from the ranking; test (a) fails.

## Facts

- E2E: `dev/run-e2e.sh --instances 3 --first-instance 5` (08a uses 9-11; check `ss -ltn` anyway).
- The `test-runner` subagent may be on an exhausted model: run builds and suites yourself, output
  to a file, read the summary lines. Do not use `pattern-applier`.

## Out of scope

Categories; the related-posts scoring weights; any public-page change; an "AI-assisted" field on
posts (the owner uses a plain tag); 08a's files.
