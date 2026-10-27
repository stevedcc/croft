# Brief 18e: a visit across midnight UTC counts once (issue #81)

Read AGENTS.md first and follow it. One commit per item, TDD, break-it-once where named. No pushes
except your branch `brief/18e` to `origin` and ONE draft PR. Every suite goes through
`dev/with-suite-lock.sh`. No `src/Croft.Web` change, so no E2E run is needed.

Other briefs running at the same time: 18a (tests incl. `PageViewRetentionTests.cs`,
`dev/with-suite-lock.sh`), 18b (workflows, `Directory.Build.props`, `packages.lock.json`), 18c (a
migration, `RetiredPostColumnsTests.cs`), 18d (branding in `src/Croft.Web`). Don't edit their files.
This brief adds **no migration** (18c has one in flight).

## What is wrong now

`AnalyticsService.RecordPageViewAsync` (`src/Croft.Api/Services/AnalyticsService.cs:24-51`) stores a
view under (post, `HashIp(ip, day)`, day). A unique index (`PageViewConfiguration.cs:18`) and
`INSERT … ON CONFLICT DO NOTHING` (`AnalyticsTables.cs:40-41`) drop a repeat. The IP hash is salted
with the day (`HashIp`, :129-135) on purpose: a reader's rows can't be linked across days. Because
the key is a **calendar** day, a reader who loads a post at 23:59:50 UTC and reloads at 00:00:10 UTC
gets two different keys and counts twice.

## The rule (owner decision, 2026-10-27)

**A view in the first 30 minutes of a UTC day doesn't count if the same reader already has a view
of the same post on the previous UTC day.** Such a view is a duplicate: no row is inserted, the
count isn't incremented, and `ApiMetrics.PageViewDuplicate()` is recorded, the same as a same-day
repeat. A view from that reader later on the same day (from 00:30:00 on) counts as usual, so it's
still at most one view per reader per post per day, and a visit that crosses midnight counts once.

How: the request carries the raw IP, so the server can compute **yesterday's** hash,
`HashIp(ip, day.AddDays(-1))`, and look for that row. Nothing new is stored, and stored rows stay
unlinkable across days. That's the point of this design: don't add a cross-day hash or any new
column.

- A private constant `MidnightGrace = TimeSpan.FromMinutes(30)` with a doc comment giving the rule
  and why (the day-salted hash, issue #81).
- Only when `now - day.ToDateTime(TimeOnly.MinValue) < MidnightGrace`: one `AnyAsync` on
  `db.PageViews` for `PostId == postId && Day == day.AddDays(-1) && IpHash == yesterdaysHash`. It's
  served by the unique index (PostId, IpHash, Day). If it finds a row, record the duplicate metric and return
  before the insert. Outside the window there is no extra query.
- Put the check after the `countable` check and the truncation, before `InsertPageViewOnceAsync`.
  Don't touch the `W-031` / `W-093` blocks.
- A race between two requests in the same instant that straddle midnight isn't handled (it's
  harmless, and at most one extra view). Say so in one line in the doc comment.

## Item 1: the rule, test first

Tests in `tests/Croft.Api.Tests/AnalyticsControllerTests.cs`, through the HTTP endpoint with
`FakeTimeProvider`, the same shape as `RecordPageView_SameIpSameDay_CountsOnce_NextDayTwice`
(:130-148). Each test creates its own published post (with `publishedAt` before the clock), so
counts are scoped to that post:

1. `AcrossMidnight_AReloadCountsOnce`: view at 23:59:50 UTC, advance 20 s, view again. The count
   (`GetViewCountAsync`) is 1 and the row count (`GetPageViewRowCountAsync`) is 1. Red before the change.
2. `JustInsideTheGrace_IsADuplicate_AtItsEdgeItCounts`: a view at 23:50:00, then 00:29:59 →
   count 1. Then advance to 00:30:00 → count 2 (the edge counts). Work out the clock advances in
   the test from absolute times, not accumulated deltas, and write the times in comments.
3. `AnotherReaderAfterMidnight_Counts`: a view from 203.0.113.5 at 23:59:50, then one from
   203.0.113.6 at 00:00:10 → count 2.
4. `YesterdaysReader_LaterToday_Counts`: a view at 10:00, then 00:10 the next day (a duplicate,
   count 1), then 14:00 that day → count 2, rows 2.
5. Unchanged and still green: `RecordPageView_SameIpSameDay_CountsOnce_NextDayTwice` (it moves from
   12:00 to 12:00, outside the grace).

Also assert the duplicate metric for test 1 the way `ApiMetricsTests.cs` observes
`PageViewDuplicate` (look there for the listener; don't build a new one).

Break-it-once: set `MidnightGrace` to `TimeSpan.Zero`. Tests 1, 2 and 4 must fail, and 3 and 5 must
still pass. Revert.

## Item 2: say what is counted

Find where the README and code comments describe the dedupe ("once per visitor per day" or
similar: `grep -rn -i "per day\|dedupe" README.md src/Croft.Api`), and add the midnight rule in one
sentence where it's described. If the README doesn't describe it, change only the comments.

## Finally

API and Web suites once after the last change, summary lines in your report. Then
`requesting-code-review`.

## Out of scope

- Changing the key, the hash, the index or the table (no migration).
- The E2E test `AnalyticsViewCountTests` (#69).
- Time zones other than UTC for the day boundary.
