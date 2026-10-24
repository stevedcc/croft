# Brief 15a: GitHub Actions CI on every push to main and every pull request (issue #9)

Read AGENTS.md first. One commit per item. This brief has no C# changes; its "tests" are real
workflow runs on GitHub, so the evidence is run URLs and their summary lines, not red/green unit
tests. **Pushing exception:** unlike the usual implementer rules, you MAY push your branch
`brief/15a` to `origin` (github.com/stevedcc/croft) and open ONE draft pull request from it, so
the workflows run; you may push further commits to that branch. Never push to `main`, never create
tags, never touch repository settings, secrets or rulesets.

**Owner-visible outcome:** a PR to `main` and a push to `main` show the checks `build`, `api-tests`,
`web-tests`, `e2e` (and `codeql`), all green on the current code; the HA E2E category runs nightly.

## Facts
- SDK is pinned by `global.json` (10.0.401, rollForward latestFeature): use `actions/setup-dotnet`
  with `global-json-file: global.json`.
- `tests/Croft.Api.Tests` and the `Category=Container` classes of `tests/Croft.Web.Tests` use
  Testcontainers (Postgres). GitHub's `ubuntu-latest` has Docker; nothing to configure.
- E2E: `dev/run-e2e.sh` needs the compose stack (`docker compose up -d`: Postgres :5432, mock OIDC
  :5556), Playwright browsers chromium AND firefox (`SidebarFooterAlignmentTests` needs firefox;
  install: `node tests/Croft.Web.E2E/bin/Debug/net10.0/.playwright/package/cli.js install --with-deps chromium firefox`
  after `dotnet build`), and `dotnet tool restore`. `compose.yaml` mounts with `:Z` (SELinux); if
  that fails on the runner, override with a CI compose file passed via `-f` rather than editing
  `compose.yaml`. Use `--instances 2` (4-vCPU runner); a plain run also does the DefaultBranding
  and OutputCache passes. The HA suite is the `HaTests` category (excluded by default; see the E2E
  csproj's `VSTestTestCaseFilter`).
- Pin every third-party action by full commit SHA with a `# vX.Y.Z` comment (resolve with
  `gh api repos/<owner>/<repo>/git/ref/tags/<tag>`).
- Minimal `permissions:` per job (`contents: read`; CodeQL needs `security-events: write`).
- Concurrency: cancel in-progress runs of the same PR.

## Item 1: `.github/workflows/ci.yml`
Triggers: `pull_request` (to main), `push` to `main`, `workflow_dispatch`. Jobs:
- `build`: restore, `dotnet build Croft.slnx -c Release -warnaserror` (the repo must stay
  warning-free, AGENTS.md); upload nothing.
- `api-tests` (needs build or rebuilds: prefer one build per job for simplicity, Debug config, since
  the suites run `--no-build` against the Debug output that `dev/run-e2e.sh` also uses):
  `dotnet test tests/Croft.Api.Tests --no-build --logger "trx" --logger "GitHubActions"` is NOT
  required; the plain console logger is fine. Publish the trx as an artifact on failure.
- `web-tests`: `dotnet test tests/Croft.Web.Tests --no-build`.
- `e2e`: compose up, build, browsers, `dev/run-e2e.sh --instances 2`; on failure upload
  `/tmp/blog-e2e-*.log` and Playwright traces/screenshots if the suite writes any
  (check `tests/Croft.Web.E2E` for an output folder).
- Cache NuGet packages (`actions/cache` on `~/.nuget/packages`, keyed on `**/*.csproj` +
  `Directory.Build.props` + `dotnet-tools.json`).
Evidence: the PR's checks green, run URL in the commit message. Then break it once: push a commit
that introduces a compiler warning (e.g. an unused variable in a test) → `build` fails on
`-warnaserror`; revert it. Record both run URLs.

## Item 2: `.github/workflows/nightly.yml`
`schedule` (one cron, 03:00 UTC) + `workflow_dispatch`: the E2E `HaTests` category
(`dev/run-e2e.sh --instances 2 -- --filter "Category=HaTests"`; check the script passes `--`
arguments through and what the HA tests need: read `tests/Croft.Web.E2E` for `HaTests` and
`docs/` for the HA design before writing this; if they need more than the compose stack, say so and
make the job skip with a clear message instead of guessing). Evidence: one `workflow_dispatch` run.

## Item 3: `.github/workflows/codeql.yml` and `.github/dependabot.yml`-free extras
CodeQL for C# (and `actions`), `github/codeql-action` pinned by SHA, on PR, push to main and a
weekly schedule. OpenSSF Scorecard (`ossf/scorecard-action`) weekly, publishing results.
Do NOT add Dependabot config (Renovate is being set up separately). Evidence: one green run each.

## Item 4: README
A short "Continuous integration" section in `README.md` (what runs when, how to run the same
locally: the AGENTS.md commands), and status badges for `ci` and `codeql` under the title.

## Out of scope
Release/image builds (brief 15b), Renovate, branch rulesets (the orchestrator does those),
any change under `src/` or `tests/`.
