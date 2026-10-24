# Brief 15d: make the first Renovate PRs green (axe 4.14 finding, IDE0300, CI Postgres wait)

Read AGENTS.md first and follow it (TDD for the behaviour change in item 1; the rest is mechanical).
Three branches, three worktrees, prepared for you:

| Worktree | Branch | Pushes allowed |
|---|---|---|
| `.worktrees/15d-playwright` | `renovate/playwright` (Renovate PR #42) | yes, this branch only |
| `.worktrees/15d-nuget` | `renovate/nuget-packages` (Renovate PR #39) | yes, this branch only |
| `.worktrees/15d` | `brief/15d` (this brief) | yes, this branch only, plus ONE draft PR to `main` |

Pushing to a Renovate branch is the normal way to fix a Renovate PR: Renovate stops rebasing a
branch that has a human commit. Never push to `main`, never rebase or amend Renovate's commit, no
tags, no settings. The PRs are merged by the orchestrator once their `ci` checks pass.

**Owner-visible outcome:** PR #42 (Playwright 1.64.1 + Deque.AxeCore.Playwright 4.14.0) and PR #39
(NUnit 4.6.1, NUnit.Analyzers 4.15.0, bunit 2.12.5, Markdig 1.5.0, Testcontainers 4.16.0,
OpenTelemetry 1.20.1, xunit.runner 3.1.5, dotnet-ef 10.0.13) show 7/7 checks green; CI's
E2E job no longer fails when Postgres needs more than 60 s to start on a busy runner.

## Item 1 (on `renovate/playwright`): the contents panel's pin control has an accessible name that contains its visible text
Now: with axe-core 4.14 (new in this PR), `AccessibilityMatrixTests.Scan_post_390_light` and
`_dark` fail: `[contents panel pinned] label-content-name-mismatch (serious): Elements must have
their visible text as part of their accessible name`
(https://dequeuniversity.com/rules/axe/4.14/label-content-name-mismatch). Find the control: the
test scans the post page at 390 px with the contents panel pinned (read the test for how it pins
it); the control is in `src/Croft.Web/Components/Blog/` or `Layout/` (grep `aria-label` near
"contents"/"pin"). Typical cause: visible text "Contents" (or an icon with text) and
`aria-label="Pin the contents panel"` that does not contain the visible text.
Fix the markup so the accessible name starts with or contains the visible text (e.g. drop the
`aria-label` and use the visible text plus `aria-pressed`, or `aria-label="Contents: pin the panel"`),
keeping the behaviour and the existing tests' locators (grep the E2E and bUnit tests for the old
label and update them only if the label text was the brief's rule change; say so in the message).
Tests: the two failing scans pass; a bUnit test asserts the accessible name contains the visible
text (compute it the simple way: the button's text content is a substring of its `aria-label`, or
there is no `aria-label`). Run locally: `dotnet build`, then the bUnit class and
`dev/run-e2e.sh --instances 2 --first-instance 7 -- --filter "FullyQualifiedName~AccessibilityMatrixTests"`
(instances 7..8 are yours; install the new Playwright browsers first: AGENTS.md's `cli.js install chromium firefox`
from this worktree's build output, since 1.64 ships new browser builds).
Also: AGENTS.md names the Firefox build (`Firefox 155.0, Playwright build firefox-1543`); update it to
what 1.64.1 installs (the install output says).
Break-it-once: restore the old aria-label → the axe scan fails. Commit on `renovate/playwright`,
push; record the PR's `ci` run URL in the report.

## Item 2 (on `renovate/nuget-packages`): IDE0300 in the E2E tests
Now: the Release build (`-warnaserror`) fails with `error IDE0300: Collection initialization can be
simplified` at `tests/Croft.Web.E2E/TagHashtagsTests.cs(71,65)`, `(87,68)`, `SiteFooterTests.cs(73,38)`,
`PublishedDateTests.cs(72,39)`, `ProfileLinksTests.cs(29,61)`, `(45,61)` (and any others the build
shows). Say in the commit message which package bump made the analyzer report them (check the
diff of the PR: NUnit.Analyzers 4.15.0 is the likely one). Fix: collection expressions (`[...]`),
exactly as the diagnostic says; no other refactoring. Verify: `dotnet build Croft.slnx -c Release
-warnaserror` clean locally, then `dotnet test tests/Croft.Web.Tests --no-build` and the API suite
(the bunit/xunit/Testcontainers bumps touch them) and `dev/run-e2e.sh --instances 2 --first-instance 7`
once (NUnit 4.6.1 + adapter 5.2.0 run the E2E suite). Read the release notes of NUnit 4.4-4.6 and
bunit 2.12 for breaking changes and say in the message whether any applied.
Commit on `renovate/nuget-packages`, push; record the PR's `ci` run URL.

## Item 3 (on `brief/15d`): CI waits for Postgres properly
Now: `.github/workflows/ci.yml` e2e job polls `pg_isready` 60 × 1 s; on a busy runner Postgres 18
was still "rejecting connections" at the end (ci run on PR #38). Replace the two hand-rolled loops
with compose healthchecks: a `healthcheck:` for `postgres` (`pg_isready -U blog`, interval 2 s,
retries 90) and for `oauth2` (the `.well-known/openid-configuration` URL; check the image has
`curl` or `wget`, else keep the loop for that one) in `.github/compose.ci.yaml` (CI-only, so
`compose.yaml` stays as it is), and `docker compose up -d --wait --wait-timeout 180`. Evidence:
the PR's `ci` run green; the e2e job log shows the `--wait` line. Break-it-once: `--wait-timeout 1`
→ the step fails fast (run URL), then revert.

## Report
Per item: commit sha, run URL, verbatim summary lines; what is left undone. If a Renovate PR gets
rebased by Renovate while you work (it should not, once you pushed), say so and stop.
