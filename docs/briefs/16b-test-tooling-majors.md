# Brief 16b: test-tooling majors (xunit.v3, NUnit 5, Test.Sdk 18, coverlet 10)

Read AGENTS.md first and follow it. **No application code changes** (`src/` untouched except where a
test-only package forces a csproj change there, which it should not). One commit per item. No
pushes except `brief/16b` to `origin` and ONE draft PR. E2E instances 4..6 are yours
(`dev/run-e2e.sh --instances 3 --first-instance 4`); brief 16a runs its final suites on 1..3 at the
same time: check `pgrep -af "dotnet test|run-e2e"` (excluding your own shell) before every suite
run and never run two full suites at once; do csproj and code migration first, suites last.

**Base:** this branch starts from brief 16a's head (net11.0, SDK 11 RC 2, 11 RC 2 packages), whose
API, Web, E2E and image tests are green; that is your baseline. When 16a has merged to `main`
(the orchestrator tells you), `git fetch origin && git merge --no-edit origin/main` (never rebase)
before your final suites.

**Why one brief, one axis:** the runtime changed in 16a; here only the test tooling changes, so a
failure is attributable. Renovate holds these majors behind a Dependency Dashboard tick (#40);
do NOT tick them, this brief does the bumps itself.

**Owner-visible outcome:** none; every suite reports the same test counts as before (API 1275,
Web 1881, E2E 688 + 11 skipped on 16a's head; re-read them from your baseline run), CI green.

## Facts (NuGet, 2026-10-24)
`xunit.v3` 4.0.2 (replaces `xunit` 2.9.3, which Renovate flags as deprecated), `xunit.runner.visualstudio`
4.0.1, `Microsoft.NET.Test.Sdk` 18.11.0, `coverlet.collector` 10.1.0, `NUnit` 5.0.0,
`NUnit3TestAdapter` 6.3.1 (`NUnit.Analyzers` stays unless 5.0 needs a newer one). Projects:
`tests/Croft.Api.Tests`, `tests/Croft.Web.Tests` (xunit + bunit 2.12.5 + AwesomeAssertions 9.6),
`tests/Croft.Web.E2E` (NUnit + Playwright 1.64.1), `dev/screenshot-gate/capture` and `compare`
(NUnit). 97 xunit test files use `IAsyncLifetime`, `ITestOutputHelper`, `[Collection]`,
`IClassFixture<>` / `ICollectionFixture<>` or assembly attributes. Both xunit projects set a
default `VSTestTestCaseFilter` (opt-in categories) that CI's `image-tests` job overrides with
`--filter`: that mechanism must keep working with the v3 runner.

## Item 0: baseline
On the branch as given: `dotnet build Croft.slnx`, then API and Web suites and one E2E run
(instances 4..6); record the exact counts. These are the numbers every later run must match.

## Item 1: xunit.v3 in Croft.Api.Tests and Croft.Web.Tests
Read xunit's v2 → v3 migration guide (`https://xunit.net/docs/getting-started/v3/migration`)
first; apply exactly what it says: package swap (`xunit` → `xunit.v3`, runner 4.x), the
`IAsyncLifetime` `ValueTask` signatures, `ITestOutputHelper` namespace, `[Theory]` data changes,
assembly-level fixtures/attributes, `TestContext.Current.CancellationToken` where the guide
recommends it for long-running fixtures (Testcontainers), the xunit.runner.json / parallelism
settings (`AssemblyInfo.cs` in the Web tests says how parallel the suite is: keep the same
behaviour), and the `Category` traits the default filter relies on. Keep every assertion line
unchanged (rule 5); only test *code* and project files change. Evidence: both suites at the
baseline counts; `dotnet test --filter "Category=Container|Category=ReadyToRun"` still selects
exactly the five opt-in tests; the default run still excludes them (one run each, counts in the
message). Note v3's live in-flight display and new exit codes if CI's log parsing (`test_filter`
in the project config) needs a change: it must not, say so.

## Item 2: NUnit 5 + adapter 6 in Croft.Web.E2E and dev/screenshot-gate
Read NUnit 5.0's release notes/breaking changes (framework and adapter: `https://docs.nunit.org/`).
Apply what applies (`Assert.That` forms, `TestDelegate`/`ActualValueDelegate` if any, the
`NUnit.NumberOfTestWorkers` runsettings `dev/run-e2e.sh` passes, `[Parallelizable]` semantics,
`Assert.Ignore` usage in the skipping tests). Evidence: E2E at the baseline counts (688 + 11 skipped,
DefaultBranding 1, OutputCache 7) on instances 4..6; the HaTests category still excluded by
default; the screenshot-gate projects build and `dev/screenshot-gate` runs once if it has a dry
mode (read its README).

## Item 3: Test.Sdk 18 and coverlet 10, everywhere
Bump; read their release notes for anything that changes `dotnet test` output or the trx logger
CI uploads on failure. Evidence: all suites once more at the baseline counts.

## Item 4: CI and docs
Push, open the draft PR, `gh pr checks --watch`: `ci` (all five required checks incl.
`image-tests`) and `codeql` green on the runner, which installs the SDK from `global.json`.
AGENTS.md: the "Tests" paragraph if any command changed (it should not); README prerequisites if
the runner package names appear there. Then E2E a second time (instances 4..6).

## Report
Per item: commit, verbatim summary lines vs the baseline, every migration-guide point applied or
"does not apply, because …", anything a test needed beyond mechanical changes (with rule-5
justification), the two E2E runs, the PR run URLs. What is left undone.

## Out of scope
Application code, bunit/AwesomeAssertions/Playwright versions (minors ride Renovate), MudBlazor,
anything from 16c (idioms), the Renovate dashboard ticks.
