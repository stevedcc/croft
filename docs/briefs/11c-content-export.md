# Brief 11c: content export and import (JSON plus Markdown)

Read AGENTS.md first and follow it. One commit per item, TDD, break-it-once for each key test,
E2E once at the end (this brief adds no pages), the final whole-branch review.

**Owner-visible outcome:**
- Every night the site's content is also saved as plain files: one JSON file per post or page
  with every field, and its Markdown body beside it as a `.md` file the JSON names.
- Anyone can read or diff those files without a database, and one post can be recovered by
  hand.
- The same files can rebuild an empty database, and a round-trip test proves nothing is lost.

**Context:** the database already has point-in-time backups (CloudNativePG to Azure) and the
media files are backed up by restic. This export is the second, database-independent layer the
owner asked for: "JSON plus the .md beside it, reference the filename from the json".

**CSP impact: none** (no pages, no endpoints; two console commands of the API image).

## Item 1: `export` command

**Do:**
- `dotnet Croft.Api.dll export --out <dir>`, dispatched like `migrate` (`Program.cs`,
  `args is ["migrate"]`): it runs, writes, exits 0; on any error it logs and exits 1 (reuse the
  10e pattern: no unhandled exception, no hang at PID 1).
- Read-only: a `AsNoTracking` read of the content tables, in one `REPEATABLE READ` transaction
  so the files are consistent with each other. It must work against a read replica (no
  writes, no migrations, no advisory locks).
- Layout (deterministic, so two exports of unchanged content are byte-identical):
  ```
  <dir>/manifest.json            formatVersion: 1, exportedAt, croftVersion, counts per kind
  <dir>/posts/<slug>.json        one per post (every status: drafts and scheduled too)
  <dir>/posts/<slug>.md          its MarkdownContent, exactly (UTF-8, no BOM, bytes unchanged)
  <dir>/pages/<slug>.json, .md   the same for pages
  <dir>/categories.json          all categories
  <dir>/tags.json                all tags
  <dir>/media.json               all MediaAsset rows (metadata only; the files are restic's)
  ```
- Each post/page JSON holds **every persisted field** of `Post`, with relations by stable key:
  `id`, `slug`, `kind`, `status`, `title`, `subtitle`, `excerpt`, `summary`, `readingMinutes`,
  `navigationPlacement`, `navigationOrder`, `publishedAt`, `createdAt`, `updatedAt`,
  `contentEditedAt`, `authorId`, `authorName`, `bannerImageId`, `categoryId` (plus `categorySlug`
  for readers), `tagIds` (plus `tagSlugs`), `mediaAssetIds`, `viewCount`, `feedback` (the post's
  Feedback rows, every field), and `markdownFile` (the `.md` file's name, relative to the JSON).
- Not exported, with the reason in a code comment: `SearchVector` (derived, rebuilt by the
  database), `PageViews` (analytics, not content; they have their own retention).
- JSON: `System.Text.Json`, camelCase, enums as strings, UTC ISO 8601 timestamps with `Z`,
  indented, properties in a fixed order, arrays sorted by a stable key (slug or id), trailing
  newline.
- **Completeness guard:** a unit test enumerates the public settable properties of `Post`,
  `Category`, `Tag`, `MediaAsset` and `Feedback` and fails when one is neither exported nor in
  the documented exclusion list (the same idea as the draft backup v2 guard in 10a). A new field
  can never be silently missed.
- The written directory is replaced atomically: write into `<dir>.tmp-<random>`, then swap, so a
  crash never leaves a half export where the previous good one was.
- `k8s/README.md`: how a site runs it (a CronJob with the API image and the database secret,
  writing to a volume that a backup tool picks up), including that it may read from the `-r`
  (replica) service.

**Tests (integration, against a real Postgres like the existing API tests):**
- Seed: two posts (one draft, one published with banner, category, two tags, media, feedback),
  one footer page, every field non-default; Markdown with front-matter-looking text, CRLF lines,
  emoji and a non-Latin script. Export: the files exist as specified; each `.md` equals
  `MarkdownContent` byte for byte; `markdownFile` points to it; counts in the manifest match.
- Two exports of unchanged content are byte-identical (except `manifest.exportedAt`).
- The completeness guard fails when you add a dummy property to `Post` (break-it-once).
- An unreachable database → exit 1 within the migrate timeout, no output directory replaced.

## Item 2: `import` command and the round trip

**Do:**
- `dotnet Croft.Api.dll import --from <dir>` restores an export into a database that has **no
  posts, pages, categories, tags or media rows** (after `migrate`). Any existing content →
  refuse with exit 1 and a clear message; never merge, never overwrite.
- Preserves ids, slugs, timestamps, statuses, view counts, feedback and relations exactly.
  `SearchVector` gets rebuilt by the normal path. Reads `formatVersion`; an unknown version →
  exit 1.
- The Markdown comes from the `.md` file named by `markdownFile`, never from anywhere else.
  A missing or mismatched file → exit 1 naming it, and nothing is written (one transaction).
- Media rows are restored as metadata; the command checks whether each file exists under the
  media root and logs a Warning per missing file (restic restores files separately), but does
  not fail.

**Tests:**
- **Round trip:** seed as in item 1 → export A → import A into a fresh database → export B →
  A and B identical (except `exportedAt`). This is the test that proves nothing is lost.
- Import into a database with one post → exit 1, nothing changed.
- A `.md` file deleted from the export → exit 1, the database still empty.
- `formatVersion: 2` → exit 1.
- Break-it-once: skip `subtitle` in the import; the round-trip test must fail.

## Out of scope
- The site's CronJob, volume and restic wiring (the orchestrator does that in the site repo).
- Exporting analytics/page views.
- An admin UI for export or import.
- Partial or merging imports.
