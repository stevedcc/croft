# Brief 18a: tests that pass alone, and one suite lock for every user (issues #79, #80, #89, #82, #88)

Read AGENTS.md first and follow it. One commit per item, TDD (for test-only items: show the test
failing for the reason in the issue first, then passing), break-it-once where the item names it.
No pushes except your branch `brief/18a` to `origin` and ONE draft PR. Every suite goes through
`dev/with-suite-lock.sh` (AGENTS.md "Running things"). Your E2E instances are **4..6**
(`dev/run-e2e.sh --instances 3 --first-instance 4`; single classes: `--instances 1 --first-instance 4`).
Never use instance 1: its ports are the ones item 2 is about.

Other briefs running at the same time: 18b (workflows, `Directory.Build.props`, lock files),
18c (a migration, `RetiredPostColumnsTests.cs`, AGENTS.md "Pending contracts"), 18d (branding:
`src/Croft.Web/Branding/`, `App.razor`, `Program.cs` near the `/branding` endpoints). Don't edit
their files. If item 2's fix has to go into `src/Croft.Web/Program.cs`, keep it away from the
`/branding` endpoints and say so in the commit message.

## Item 1: `PageViewRetentionTests` stops depending on test order (#79)

**What is wrong.** `tests/Croft.Api.Tests/PageViewRetentionTests.cs` runs every test against one
database per class (tests run one at a time). `result.Deleted` counts every row the purge removed,
not only the test's own. `Purge_DeletesOnlyRowsMoreThanNinetyDaysOld_AndKeepsViewCountTotals`
(:26) leaves its day −90 and day −1 rows behind. When it runs before `Purge_HonoursRetentionDaysThirty`
(:69), the 30-day purge also deletes the −90 row and :79 fails with `Expected result.Deleted to be 1,
but found 2`. Seen in 17e's full API run and reproduced alone on 874996a.

**What to do.** The purge is table-wide by nature, so its count can't be scoped to one test's
rows. The class owns its database, and its tests run one at a time, so **each purge test starts
from an empty `PageViews` table**: a private helper `ClearPageViewsAsync()` (`DELETE FROM "PageViews"`)
called first in every test that asserts `result.Deleted` against `PageViews`: :26, :69, :84 and :101.
The renamed-table test (:44) already clears its own table. Put a short comment on the helper saying
why (the count is table-wide; the database belongs to this class). Every assertion keeps its
expected value (rule 5): only the starting state changes.

**Tests.** First prove the order dependency. Run the 90-day test, then the 30-day test, in one
process, before the fix: for example a temporary `[TestCaseOrderer]` or an xUnit
`--filter` run that shows both run and :79 fails. Paste that output into the commit message, then
remove the temporary orderer. After the fix, run the same ordering again (passes) and the whole
class (passes). Break-it-once: remove the `ClearPageViewsAsync()` call from the 30-day test and
rerun the ordering. :79 must fail again.

## Item 2: the warm-up contract tests stop binding port 5000 (#80)

**What is wrong (reproduced while writing this brief, 2026-10-27).** With `127.0.0.1:5000`
occupied (a Python listener standing in for E2E instance 1), the Web suite's
`WebWarmupContractTests.EveryWarmupRequest_RunsInsideTheCroftWarmupActivity` fails:

```
System.IO.IOException : Failed to bind to address http://127.0.0.1:5000: address already in use.
  at ...AddressBinder.DefaultAddressStrategy.BindAsync
  at ...KestrelServerImpl.StartAsync
  at Program.<Main>$ in src/Croft.Web/Program.cs:line 1067
  at ...WebApplicationFactory`1.StartServer()
  at Croft.Warmup.Contract.WarmupHostSetup.Launch[TEntry] in tests/Croft.Web.Tests/Warmup/WarmupContractTests.cs:line 74
```

The test **already calls `factory.UseKestrel(0)`** (`WarmupContractTests.cs:73`), yet Kestrel binds
with its `DefaultAddressStrategy`, its fallback when no address is configured (localhost:5000).
So `UseKestrel(0)`'s address never reaches the host. The cause is unknown. Candidates to rule in or
out: `WebApplicationFactory.UseKestrel(int)` on .NET 11 RC 2 with the minimal-hosting
`DeferredHostBuilder`; something in `Croft.Web/Program.cs` that resets the Kestrel options or URLs;
the order of `UseKestrel` and `ConfigureWebHost` in `WarmupHostSetup`. The API twin
(`tests/Croft.Api.Tests/Warmup/ApiWarmupContractTests.cs`, which shares `WarmupHostSetup`) failed
the same way in 17c and 17e. Check it with the same listener.

**What to do.** Use `systematic-debugging`: find the actual cause before changing anything, and
write it into the commit message with the evidence. Then fix it at the cause, so the test host
listens on an OS-assigned port (port 0) and the tests keep reading the real address from
`IServerAddressesFeature`, as `Launch` does now. If the cause is framework behaviour (for example
`UseKestrel(int)` not applying under minimal hosting), the compensating code gets a `W-NNN` entry in
`docs/workarounds.md` and markers in the same commit (AGENTS.md "Workarounds"). If the fix needs a
`src/` change only to make a test host bindable, stop and report (rule 8): production code doesn't
bend for a test without the orchestrator's agreement.

**Tests.** Red: both contract tests with `127.0.0.1:5000` held by a listener (start it in the same
shell, wait until `ss -ltn` shows it, kill it afterwards). Green: the same, both pass, and the
assigned port appears in the test's `url.full` match (the test already uses the port as its
marker). Break-it-once: revert the fix, and the Web test must fail with the bind error again.

## Item 3: `HomeTests.CategoryPaging_ClickingPage2_ShowsSecondPageOfSameList` passes alone (#89)

**What is wrong.** `dev/run-e2e.sh --instances 1 --first-instance 4 -- --filter "FullyQualifiedName~HomeTests"`
fails every time (4 of 4 runs in 17e, also on its base 874996a): `"· 11 posts"` isn't found, and
the page shows `10 posts`. It passes in the full suite, so it depends on posts that other classes
publish. `tests/Croft.Web.E2E/HomeTests.cs:226` says the category "holds exactly the 11 paging
fixture posts". Find out why it holds 10 when the class runs alone (look at `EnsureFixturesAsync`
and how the paging posts are created and published: a post that isn't published yet when the page
loads, a count off by one, a fixture shared with another test in the class). Confirm the cause
before fixing.

**What to do.** The test creates and publishes all 11 posts in its own category and waits until
the API reports all 11 published before loading the page (a concrete condition, rule 6: poll the
API's public list for that category until its `TotalCount` is 11, no `Task.Delay`). Assertions
unchanged: `· 11 posts`, 10 cards, page 2 with 1 card, "… 01" on page 2.

**Tests.** The filter command above, 3 runs in a row, all green. Then the full `HomeTests` class
inside the full suite (item 5). Break-it-once: create 10 posts instead of 11. The test must fail
on `· 11 posts`.

## Item 4: the home-page byte budget stops depending on the suite's data (#82)

**What is wrong.** `tests/Croft.Web.E2E/FirstVisitMeasurementTests.cs:27-31, 68-69` holds the home
page's **total** bytes to `Measured × 1.1`, rounded up to 5 KB. The home page shows whatever posts
the instance holds, with their card images, so its total moves with the other classes' data and
worker scheduling. The post page's content is the test's own, so its total is stable.

**What to do.** Keep everything the budget protects that the suite's data can't move. For **home
only**, the total-bytes assertion compares **total bytes minus image bytes**
(`visit.TotalBytes - visit.BytesByKind.GetValueOrDefault("image")`) against a new "after" figure
measured the same way. Image bytes on home are written to the `FIRST-VISIT` line, not asserted.
Font bytes, font requests and every post-page assertion stay exactly as they are (rule 5: the
home total assertion changes on purpose, because the issue redefines what it measures; say so in
the commit message and the class's doc comment). Record the new home figures by running the class
alone on a **fresh** instance (`dev/run-e2e.sh --instances 1 --first-instance 4 -- --filter
"FullyQualifiedName~FirstVisitMeasurementTests"`), and put the arithmetic in the comment above
`Measured` (measured value, ×1.1, rounded up to 5 KB = the budget). Then run it again inside the
full suite. The non-image total must not move by more than the 10 % headroom between the two runs.
Report both numbers.

**Tests.** Break-it-once: lower the home non-image "after" figure by 20 %, and the test must fail
with the bytes message. Revert.

## Item 5: one suite lock for the owner and the agents' sandbox (#88)

**What is wrong (found while writing this brief).** `dev/with-suite-lock.sh` locks
`${XDG_CACHE_HOME:-$HOME/.cache}/croft/suite.lock` (:9, :33). The Chargehand sandbox gives the
agent user (`opencode`) an in-memory `$HOME` and a private `/tmp`, so a sandboxed verify and
the owner's suite take **different** locks and run at the same time. That's #88: the 17b verify
failed `CspTests`, `PostRailStickyTests` and `MobileContentsPanelTests` under load average 11.7
while another full E2E run was live.

**What to do.** The lock directory becomes configurable: `CROFT_SUITE_LOCK_DIR`, default
unchanged. The script must work when that directory is **shared by two users through a common
group** (a setgid directory, mode 2770; the owner creates it outside this brief):
- create the lock file and `suite.lock.owner` group-writable (`umask 0002` inside the script, for
  the files it creates);
- open the lock file for **reading** only (`exec 9<"$LOCK_FILE"`, after creating it if missing),
  so a lock file created by the other user still locks (`flock` works on a read-only descriptor);
- if the holder can't write `suite.lock.owner` (a file the other user left behind), print a warning
  and still run the command under the lock. The owner line is information, not the lock.
Update the header comment and the AGENTS.md paragraph about the lock (one sentence on
`CROFT_SUITE_LOCK_DIR` and why: the sandbox has its own `$HOME`). Leave the "Pending contracts"
section of AGENTS.md alone (18c owns it).

**Evidence** (the script has no automated tests. 17b used before/after runs, see commit
1108188; do the same and paste the output into the commit message): (a) with
`CROFT_SUITE_LOCK_DIR` set to a scratch directory, `dev/with-suite-lock.sh sleep 20` holds it, a
second `dev/with-suite-lock.sh --try true` exits 75, and nothing is created under the default
directory; (b) with the lock file pre-created with mode `0444`, the wrapper still acquires the lock
and a second `--try` exits 75; (c) `stat -c %A` shows group write on the lock file and owner file it
created. Break-it-once: change the open back to `9>` and repeat (b). It must fail (permission
denied), and the output goes in the message.

## Finally

All three suites once, after the last change (Api, Web, E2E on instances 4..6), with the summary
lines in your report. Then `requesting-code-review`. In the PR description, list per issue: cause
found, fix, break-it-once result.

## Out of scope

- #81 (page-view dedupe across UTC midnight): needs an owner decision.
- #69, #24, #25, #26: other E2E issues.
- Creating the shared lock directory or changing the Chargehand sandbox: the orchestrator and
  the owner do that after merge.
- Any test whose assertion isn't named above.
