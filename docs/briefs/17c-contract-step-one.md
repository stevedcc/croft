# Brief 17c: the pending contracts, step one (issue #15)

Read AGENTS.md first and follow it. One commit per item, TDD, break-it-once where the item names it.
No pushes except your branch `brief/17c` to `origin` and ONE draft PR. No `src/Croft.Web` change,
so no E2E run is needed; E2E instance 13 is yours if you want a smoke check
(`dev/run-e2e.sh --instances 1 --first-instance 13`). Other briefs run at the same time on this
machine: `pgrep -af "dotnet test|run-e2e"` (excluding your own shell) before every suite run, and
never run a full suite while another is running; wait and retry.

## Why two steps, not one drop

AGENTS.md "Pending contracts" lists three contracts that are safe by release age (nothing older than
0.7.3 runs; the contracts waited for 0.6.1, 0.6.4 and 0.6.3). But two of them cannot be a plain
`DROP COLUMN` yet: the current release still *reads* both columns.

- `Posts.ViewCount` is mapped as `Post.LegacyViewCount` (`src/Croft.Api/Data/Configurations/PostConfiguration.cs:35`,
  `src/Croft.Api/Models/Post.cs:48-53`), so every `SELECT` of a Post names it, and every INSERT
  writes it. The column is `bigint NOT NULL` **with no default**
  (`src/Croft.Api/Migrations/20261006152151_InitialCreate.cs:100`).
- `Posts.ShowInNavigation` is a shadow property (`PostConfiguration.cs:43`), which EF also selects
  and inserts (`boolean NOT NULL DEFAULT false`).

A rolling deploy runs the migration in the init containers first, then replaces pods one by one;
for a few minutes 0.7.x pods serve beside the new ones. Dropping either column now would make every
post query of those pods fail with `42703` until they are gone: a visible outage. So this brief is
step one (stop reading and writing the columns; give `ViewCount` a default so the new release's
INSERTs, which no longer name it, succeed), and the drop is step two, one release later, filed
back on #15 by the orchestrator. The `PageViews` contract is a single step: an old pod keeps
inserting into a `varchar(n)` column without noticing, and it truncates to the same bounds already
(`src/Croft.Api/Services/AnalyticsService.cs:17-20, 36-39`).

## Item 1: the migration guard learns max-length tightening

`tests/Croft.Api.Tests/Fixtures/MigrationContractGuard.cs:71-88` flags `AlterColumn(nullable: false)`
and raw SQL `ALTER COLUMN ... TYPE`, but **not** an EF `AlterColumn` that sets or shrinks
`MaxLength` or changes `ColumnType`. Item 3 is exactly that, so the guard would not notice if the
attribute were forgotten.

Extend `DescribeViolation`: an `AlterColumnOperation` whose `MaxLength` is set and either
`OldColumn.MaxLength` is null (unbounded → bounded) or larger (shrunk), or whose `ColumnType`
differs from `OldColumn.ColumnType` when both are set, is a violation
(`AlterColumn(<table>.<column>, max length <n>)` / `(… type <t>)`). Widening (`OldColumn.MaxLength`
smaller or equal) and an `AlterColumn` with no `MaxLength` are not.

Tests in `tests/Croft.Api.Tests/MigrationContractGuardTests.cs`, same shape as the existing fakes
(`TightenNullableMigration` at :149): `TightenMaxLengthMigration` (text → 2048: flagged),
`ShrinkMaxLengthMigration` (4096 → 2048: flagged), `WidenMaxLengthMigration` (512 → 2048: allowed),
`ChangeColumnTypeMigration` (`text` → `jsonb`: flagged), and the attribute exempting
`TightenMaxLengthMigration`. Every existing test in the class stays as it is (rule 5); run the whole
class. Break-it-once: revert the new `switch` arm, the two "flagged" tests must fail.

## Item 2: stop reading and writing `Posts.ViewCount` and `Posts.ShowInNavigation`

1. Migration (`dotnet tool run dotnet-ef migrations add RetiredPostColumnsDefault -p src/Croft.Api -s src/Croft.Api`):
   `Up` is one raw statement, `ALTER TABLE "Posts" ALTER COLUMN "ViewCount" SET DEFAULT 0;` (an
   expand: it passes the guard's regex, and it is not a contract, so no attribute), `Down` drops
   the default. Doc comment on the class: why (the two-step reason above, in two lines), and that
   the drop is step two.
2. Model: delete `Post.LegacyViewCount` and its mapping, and the `ShowInNavigation` shadow
   property (`PostConfiguration.cs:33-43`). **The scaffolded migration will contain `DropColumn`
   for both: delete those operations from `Up` (and the `AddColumn` from `Down`) and keep the
   snapshot as scaffolded** (the snapshot no longer knows the columns; step two drops them with a
   hand-written `DropColumn`). Say so in the migration's doc comment, so the step-two implementer
   knows the scaffold will not generate the drop.
3. `src/Croft.Api/Services/ContentExport/ContentFormat.cs:21, 43, 52-53`: remove the
   `PostShowInNavigation` constant and both exclusions;
   `tests/Croft.Api.Tests/ContentExportCompletenessTests.cs` (doc comment at :21) stays green
   (its `TheExportAndTheExclusionList_NameOnlyPropertiesThatExist` would fail if the exclusions stayed).
4. Tests that name the columns on the **current** schema keep working because the columns stay:
   `tests/Croft.Api.Tests/PostViewCountsTests.cs:25,44,153` (`StampAsync` selects `"ViewCount"`
   raw; keep the assertion at :44 exactly, it still means "a view never rewrites the Posts row";
   the C# record it projects into is test code and may keep the name) and
   `PostSummaryBackfillTests.cs:39,43,170,244` (raw INSERTs with `"ViewCount"`: unchanged). Tests
   that migrate to an older target (`NavigationPlacementMigrationTests`,
   `SocialAnnouncementsMigrationTests`, `PostViewCountsTests` :22-23,104-113,160) are untouched.
5. New test, `RetiredPostColumnsTests` (scratch database via `PostgresDatabaseFixture`, the
   pattern of `NavigationPlacementMigrationTests.cs:20-40`):
   - `AnInsertThroughTheModel_SucceedsWithoutNamingTheRetiredColumns`: migrate to latest, create a
     Post through `BlogDbContext` (no raw SQL), `SaveChangesAsync` succeeds, then raw
     `SELECT "ViewCount", "ShowInNavigation" FROM "Posts" WHERE "Id" = …` returns `0` and `false`.
     Break-it-once: comment out the `ALTER … SET DEFAULT` statement, the insert must fail with
     `23502` (not-null violation).
   - `TheModel_DoesNotSelectTheRetiredColumns`: run any Posts query with `ToQueryString()` and
     assert the SQL names neither `"ViewCount"` nor `"ShowInNavigation"`. Break-it-once: restore
     the shadow property, the test must fail.
   - `TheRetiredColumns_StillExist_ForPodsOfThePreviousRelease`: after migrating to latest,
     `information_schema.columns` lists both columns on `Posts`, and `ViewCount` has
     `column_default` `0`. (This is the step-one guarantee; step two deletes this test.)

## Item 3: `PageViews.Referrer` and `UserAgent` get their bounds (the one-step contract)

1. `src/Croft.Api/Data/Configurations/PageViewConfiguration.cs`: `Referrer` `HasMaxLength(2048)`,
   `UserAgent` `HasMaxLength(512)` (the constants in `AnalyticsService.cs:17,20`: reference them,
   do not repeat the numbers).
2. Migration `PageViewTextBounds`, `[ContractMigration("Referrer and UserAgent have been truncated to 2048/512 on write since 0.6.3; no pod older than that runs")]`
   (`src/Croft.Shared/ContractMigrationAttribute.cs`). `Up`: first the trim, two raw statements
   `UPDATE "PageViews" SET "Referrer" = left("Referrer", 2048) WHERE length("Referrer") > 2048;` and
   the same for `UserAgent` with 512, then the scaffolded `AlterColumn`s. One transaction is fine
   here: retention keeps about 90 days (`PageViewRetentionHostedService.cs:12`), truncation has
   been live since 0.6.3, so the UPDATEs touch few rows, and the type change holds an
   `ACCESS EXCLUSIVE` lock only for one scan of the table. Doc comment says this. Postgres
   `left()` counts code points and the API truncates UTF-16 units
   (`AnalyticsService.Truncate`, :68-77, surrogate-safe), so a trimmed row is never longer than
   what the API would have written.
3. `AnalyticsService.cs:36-37`: the comment "unbounded text for now" is now wrong; replace it
   with the fact (the columns are bounded since this release; the truncation stays as the
   first line of defence, and so that a beacon never fails on length).
4. Tests: `PageViewTextBoundsMigrationTests` (scratch database, migrate to the previous migration,
   insert raw `PageViews` rows with a 3000-character referrer, a 600-character user agent and a
   referrer of exactly 2048 characters, migrate to latest): the long ones are exactly their first
   2048 / 512 characters, the exact one is unchanged, and `information_schema.columns` reports
   `character_maximum_length` 2048 and 512. Break-it-once: remove the two UPDATEs, the migration
   must fail on the long rows (`22001`, value too long), which the test reports as a failure.
   The existing `AnalyticsControllerTests` at :188-250 stay as they are and must stay green; the
   guard test `CompiledMigrations_ContainNoContractingOperations` passes because of the attribute
   (temporarily remove the attribute: item 1's guard must now flag this migration; put it back).

## Item 4: AGENTS.md and the register

`AGENTS.md:267-279` ("Pending contracts"): replace the three entries with one:
`Posts.ShowInNavigation` and `Posts.ViewCount` are unmapped since this release (brief 17c; the
new release neither reads nor writes them, `ViewCount` has a default); drop both in a
hand-written `[ContractMigration]` `DropColumn` migration once no pod older than this release runs,
and delete `RetiredPostColumnsTests.TheRetiredColumns_StillExist_ForPodsOfThePreviousRelease`.
Keep the header lines 267-268. Nothing else in AGENTS.md changes (brief 17b edits the
"Running things" section of the same file at the same time; the sections do not overlap).

## Item 5: suites
`dotnet build Croft.slnx`, then `dotnet test tests/Croft.Api.Tests --no-build`,
`dotnet test tests/Croft.Web.Tests --no-build`, and
`dotnet test tests/Croft.Api.Tests --no-build --filter "Category=Container"` (the images run
`migrate`, and this brief adds two migrations). Summary lines verbatim into this item's commit
message (an empty commit is fine) and the report.

## Report
Per item: what changed, the break-it-once evidence (which line, which test failed, restored), the
`ToQueryString()` output of item 2's test (so the reviewer sees the columns gone), the suite lines,
the PR's `ci` run URL (`gh pr checks --watch`). What is left undone.

## Out of scope
The drop itself (step two). Committing per batch inside a migration (EF runs each migration in
one transaction; not needed for these row counts). Anything in `src/Croft.Web`.
