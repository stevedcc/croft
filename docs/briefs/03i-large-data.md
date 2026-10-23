# Brief 03i: pages that list everything stay fast with lots of data

Read AGENTS.md first and follow it, including "Finishing a task". Requires brief 03g (isolated E2E
instances, seed scripts) accepted. Use `subagent-driven-development` with the items below as the
tasks; one commit per item, TDD, break-it-once, E2E twice in a row at the end, then the final
whole-branch review.

**Why:** with the dev database bloated by test runs (3,262 posts, 379 categories, 2,350 tags, 590
media), `/admin/tags` re-rendered ~2,700 editable rows and took seconds per interaction, the Web
process grew to 2.3 GiB, and E2E waits timed out. 03g stops the tests from making that data; this
brief makes the pages cope with it on purpose. A blog that runs for years reaches hundreds of tags
and media items. The bloat also exposed two real bugs (R4's search, R5's selection handler), so a
deliberate large-data run keeps that benefit.

## Item 1: a large-data seed

- `dev/seed-large-data.sh [--instance N]`: loads an isolated E2E instance (03g) with **400
  categories, 3,000 tags, 3,000 published posts** (each with 1 category and 3 tags) **and 600
  media items** (small generated images). Through the API as the mock admin, like
  `dev/seed-e2e.sh`, never raw SQL. Make it fast enough to be usable (parallel requests are fine);
  print how long it took.
- Never run it against the dev database `blog`.

## Item 2: the pages that list everything

Find every page or component that loads a whole table into the browser or renders one element per
row of a table that grows with use. Known: `/admin/tags` (categories and tags, each row editable),
the sidebar's category list (`SidebarNav.razor`, on every page), the media library (`MediaPage` and
the picker in the post editor). Search for others (`GetTagsAsync`, `GetCategoriesAsync`,
`ListGlobalAsync` callers and similar) and list them in the report.

For each, choose the fix that fits how it's used, and say why in the report. For example:

- admin lists: server-side paging and a filter box (API endpoints gain `page`/`pageSize`/`q` and
  return `PagedResult<T>`, like the posts list), or MudBlazor virtualisation where paging would be
  awkward; only the row being edited is an editor;
- the sidebar: categories that have published posts, ordered by post count, capped (for example the
  top 12 plus a "More categories" link to a page listing them all, paged);
- the media picker: paged, newest first, with a filename filter.

Keep existing behaviour that tests rely on (creating, renaming, deleting, the delete-disabled
tooltip); adjust tests only where the paging changes what's on screen, and list each change.

## Item 3: a large-data E2E run with a time budget

- A separate E2E category (`[Category("LargeData")]`), excluded from the normal run, that starts
  from `seed-large-data.sh` on its own instance and checks, against the large data:
  - `/admin/tags`, the media library, the home page and a post page each become interactive within
    **2 seconds** (measure from navigation to the first interactive control responding);
  - create, rename and delete a tag and a category; pick an image in the editor;
  - the Web process stays under **600 MiB** after the run (read `/proc/<pid>/status` VmRSS from the
    run script, not from the test).
- `dev/run-large-data-e2e.sh`: start the instance, seed, run the category, stop the instance,
  print timings.
- Break-it-once: render the full tag list again; the 2-second check must fail.

## Item 4: the remaining long waits

03g restored the four R5.3-era 30 s E2E waits to the suite's normal budget, but about 20
`Timeout = 30000` waits remain (`git grep -n "Timeout = 30000" -- tests/Blog.Web.E2E`), mostly on
the redirect after saving a post. For each: bring it back to the normal budget, or keep it and give
the reason in a one-line comment and in the report (for example, a real slow operation the test
waits for). Your new tests use the normal budget. The large-data run shows the pages are fast enough.

## Out of scope

The parallel E2E runner (03h; your new category must simply run with `dev/run-e2e.sh` once it
exists, nothing more). Playwright-throttled runs. Search ranking. Anything else.
