# Brief 18c: drop the retired Posts columns, step two (issue #15)

Read AGENTS.md first and follow it. One commit per item, TDD, break-it-once where named. No pushes
except your branch `brief/18c` to `origin` and ONE draft PR. Every suite goes through
`dev/with-suite-lock.sh`. No `src/Croft.Web` change, so no E2E run is needed.

Other briefs running at the same time: 18a (tests incl. `PageViewRetentionTests.cs`,
`dev/with-suite-lock.sh`, AGENTS.md's suite-lock paragraph), 18b (workflows,
`Directory.Build.props`, `packages.lock.json` files), 18d (branding in `src/Croft.Web`). Don't edit
their files. In AGENTS.md, touch only the "Pending contracts" entry.

## Why this is safe now (checked 2026-10-27)

Step one (brief 17c, PR #83, released in v0.7.4) unmapped `Posts.ViewCount` and
`Posts.ShowInNavigation` and gave `ViewCount` `DEFAULT 0`
(`src/Croft.Api/Migrations/20261026133825_RetiredPostColumnsDefault.cs`). Production runs only
0.7.4: `kubectl -n blog get rs` shows `croft-api` 0.7.4 with 3 replicas and every older ReplicaSet
(0.6.7 to 0.7.3) at 0, and the site footer reads `Croft v0.7.4`. No pod that reads either column
is left. The view totals moved to `PostViewCounts` in `20261019175325_PostViewCounts`. Confirm
from that migration that the counts were copied (not only a new table), and quote the statement in
your commit message. If they weren't, stop and report: dropping `ViewCount` would then lose data.

## Item 1: the `[ContractMigration]` that drops both columns

1. Scaffold an empty migration so EF writes the designer file and the `[Migration]` id:
   `dotnet tool run dotnet-ef migrations add DropRetiredPostColumns -p src/Croft.Api -s src/Croft.Api`.
   The snapshot no longer knows the columns, so `Up`/`Down` come out empty, and the snapshot must
   not change (check `git diff --stat`; if it changes, find out why before going on).
2. Write `Up` by hand: `migrationBuilder.DropColumn(name: "ViewCount", table: "Posts")` and the same
   for `ShowInNavigation`. First check for anything that depends on either column (an index, a
   view, a trigger, a constraint: `\d "Posts"` in the test database or the migrations' history) and
   drop it first if there is one. Report what you found, even if it's nothing.
3. `Down` puts them back exactly as step one left them: `ViewCount bigint NOT NULL DEFAULT 0`,
   `ShowInNavigation boolean NOT NULL DEFAULT false`.
4. `[ContractMigration("…")]` with the reason in one line (unmapped since 0.7.4, brief 17c; no
   older pod runs). The class doc comment says the same in two or three lines, in the style of
   `RetiredPostColumnsDefault`.

**Tests (TDD).** In `tests/Croft.Api.Tests/RetiredPostColumnsTests.cs`:
- Red first: a new test `AfterMigrating_TheRetiredColumnsAreGone` (migrate, then `information_schema.columns`
  for both names on `Posts`, expect none). It fails before the migration exists.
- Delete `TheRetiredColumns_StillExist_ForPodsOfThePreviousRelease` (:58-74), as AGENTS.md and its
  own comment say.
- `AnInsertThroughTheModel_SucceedsWithoutNamingTheRetiredColumns` (:26-46) reads the dropped
  columns in raw SQL (:42-45), so it can't stay as it is. Keep the insert and replace the raw read
  with a read of the row back through the model (`Slug`, `Status`). This changes an existing
  test because the brief removes the columns (rule 5: say so in the commit message).
- Update the class doc comment (:12-16): step two is done.
- A `Down` test: migrate to the new migration, then down to `RetiredPostColumnsDefault`, and both
  columns are back with their defaults (`column_default` `0` and `false`). Then migrate up again so
  the class's database is at the latest migration for any later test.
- The migration guard (`MigrationContractGuardTests`) must pass with the attribute. Break-it-once:
  remove the attribute, the guard test must name `DropRetiredPostColumns`. Restore it.

## Item 2: every other test that touches the dropped columns

These tests write or read `Posts.ViewCount` / `ShowInNavigation` in raw SQL (`grep -rn
'"ViewCount"\|ShowInNavigation' tests --include=*.cs`, found while writing this brief):
`SocialAnnouncementsMigrationTests.cs:72`, `PostSummaryBackfillTests.cs:39, 43, 170, 244, 358`,
`NavigationPlacementMigrationTests.cs:58, 83, 91, 100`, `PostViewCountsTests.cs:75, 117, 153, 160`.
Most of them first migrate to a pinned earlier migration (`MigrateAsync(PreviousMigration)`), where
the columns still exist, and those need no change. For each one, decide from the code which schema
the statement runs against. Run the whole API suite after item 1 and fix only the ones that fail
because a statement runs after the drop. Fix them by pinning the migration they're about, never by
removing an assertion. List every file you changed in the commit message, with the reason per file.
(`MigrationContractGuardTests.cs:257` is a fake migration's `AddColumn` and isn't affected.)

## Item 3: AGENTS.md

Delete the `Posts.ShowInNavigation` / `Posts.ViewCount` entry under "Pending contracts"
(AGENTS.md:279-282). If no entry is left, keep the paragraph's heading sentence and add "(none at
the moment)" after it, so the next contract has a place to go.

## Finally

API and Web suites once after the last change, summary lines in your report. Then
`requesting-code-review`. In the PR description, include the rolling-deploy reasoning (why a 0.7.4
pod beside the new release never touches the dropped columns) in three lines, for the orchestrator's
release notes.

## Out of scope

- Any other contract or migration.
- `LegacyViewCount` / `ShowInNavigation` in old migrations' designer files and snapshots of
  earlier migrations: they're history and stay.
