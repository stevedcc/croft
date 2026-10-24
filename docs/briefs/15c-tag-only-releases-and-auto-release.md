# Brief 15c: a release is a tag; dependency merges release themselves

Read AGENTS.md first. One commit per item. No C# behaviour changes except the version source
(item 1). Evidence is real GitHub runs. **Pushing exception:** you MAY push your branch `brief/15c`
to `origin` and open ONE draft PR, and push further commits to it. You MAY create and push tags
ONLY of the form `v0.0.0-test.N` (prerelease: `ImagePolicy` ranges exclude prereleases, so nothing
deploys) and delete them again at the end (`git push origin --delete <tag>`; the tag ruleset
protects `v*` from deletion: if it refuses, leave them and say so). Never a stable `vX.Y.Z` tag,
never a push to `main`, no repository settings, secrets or rulesets.

**Owner decisions (settled):**
- Versions stay three-part semver. **Feature releases bump the minor** (next: 0.7.0); **dependency
  and fix releases bump the patch** (0.6.10, 0.6.11, …). Four-part versions are out: metadata-action,
  Flux ImagePolicy and the GitRepository semver ref all parse semver.
- A merged Renovate PR that passed CI on `main` releases itself as the next patch tag, within the
  hour. Code merges still wait for a hand-made tag.

**Owner-visible outcome:** `git tag -a v0.7.0 && git push origin v0.7.0` is the whole manual
release (no version-bump PR); the footer shows "Running on Croft v0.7.0" from the tag. After
Renovate's PR merges, a `v0.6.N+1` tag appears by itself, release.yml runs on it, and crawford.dev
rolls out (Flux image automation, already live) with no hands on the keyboard.

## Facts
- `Directory.Build.props` holds `<Version>0.6.9</Version>`, "the single source of the released
  version"; the footer reads the assembly's informational version (see the comment there and
  `AppVersionTests` in `tests/Croft.Web.Tests`). The Dockerfiles run `dotnet publish` with no
  version argument.
- `release.yml` (brief 15b) decides "release vs test" by `github.event_name == 'push' &&
  startsWith(github.ref, 'refs/tags/v')`; `workflow_dispatch` is the `ci-test-<sha>` path.
- A tag pushed with `GITHUB_TOKEN` does **not** trigger `on: push: tags` workflows (GitHub rule), so
  an automatic release must dispatch `release.yml` on the tag ref explicitly, or use a token that
  is not `GITHUB_TOKEN`. Use the dispatch: no extra secret.
- The `main` ruleset requires the four `ci` checks on PRs and blocks direct pushes; tags `v*` cannot
  be updated or deleted (ruleset "protect release tags"). Creating a tag from a workflow is allowed.
- `ci.yml` runs on `push` to `main`; `workflow_run` can chain on its completion.

## Item 1: the tag is the version
- The Dockerfiles take `ARG VERSION` (default empty) and, when set, publish with
  `-p:Version=$VERSION`; `release.yml` passes `VERSION=<tag without v>` as a build arg
  (`build-args:` of build-push-action) for a release, and `0.0.0-ci-test.<short sha>` for the test
  path. `Directory.Build.props` keeps its value as the default for local builds and tests; its
  comment says the release version comes from the tag.
- Tests: the existing `AppVersionTests` keep passing (local build, no arg). A new API test is NOT
  needed; the evidence is the smoke job: it already starts each image, so extend it to also read
  the version the image reports (find the cheapest way: the Web footer on `/`? the `/health/live`
  body? an env/`--version`? read `src/*/Program.cs` and `AppVersion` code; if nothing exposes it
  without a database, add `X-Croft-Version` as a response header on `/health/live` in BOTH apps,
  with a unit test each, and call that the one C# change) and fail when it differs from the tag.
- Evidence: a `v0.0.0-test.1` tag push → release.yml run green, smoke shows the version
  `0.0.0-test.1`, images on GHCR tagged `0.0.0-test.1` only (no `latest`, since it is a
  prerelease: check `release.yml`'s STABLE logic treats it so). Break-it-once: drop the build arg
  → the smoke version check fails (run URL in the commit message).

## Item 2: `release.yml` accepts a dispatch on a tag as a release
- `RELEASE` becomes "the ref is a `v*` tag" regardless of event (`startsWith(github.ref,
  'refs/tags/v')`); the `ci-test` path is "the ref is not a tag". Guard: a `workflow_dispatch` on a
  tag ref must still refuse a non-`v*` tag.
- Evidence: `gh workflow run release.yml --ref v0.0.0-test.2 -f test_tag=ci-test` runs the
  release path for that tag (same outputs as item 1's push-triggered run). Record both run URLs.

## Item 3: `auto-release.yml`
- Trigger: `workflow_run` of `ci` with `conclusion == success` on branch `main` (check
  `github.event.workflow_run.head_branch` and `.conclusion`; `workflow_run` always runs from the
  default branch's workflow file).
- Steps: checkout `github.event.workflow_run.head_sha` (`fetch-depth: 0` for tags); decide whether
  the merge is dependency-only: the files changed between the commit's first parent and the
  commit (`git diff --name-only HEAD^ HEAD`) are ALL within the allow-list
  `**/*.csproj`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`,
  `dotnet-tools.json`, `src/*/Dockerfile`, `compose.yaml`, `.github/compose.ci.yaml`,
  `.github/workflows/*.yml`, `renovate.json5`, and the head commit's author or committer is
  Renovate (`renovate[bot]`, or the squash/rebase author of its PRs: check what the merges on
  `main` look like with `git log --format='%an <%ae> | %cn <%ce>' -5`). If not: a `::notice::`
  "not a dependency-only merge: no release" and success. Otherwise: next patch = the highest
  stable `v*` tag + 1 on the patch (no prerelease tags count; `git tag --list 'v*' | sort -V`),
  `git tag -a vX.Y.Z -m "Croft X.Y.Z (dependencies)"` on that sha, push the tag, then
  `gh workflow run release.yml --ref vX.Y.Z` (permissions: `contents: write`, `actions: write`),
  and a step summary naming the tag and the dispatched run.
- Concurrency group `auto-release` (cancel-in-progress false): two merges in quick succession get
  two consecutive patch tags, in order.
- Evidence (no stable tag may be created by you, so prove the parts): a `workflow_dispatch`
  variant of the job with inputs `sha` and `dry_run=true` that computes and prints the decision
  and the next tag without tagging; run it against (a) the merge commit of a Renovate PR on `main`
  (find one with `gh pr list --state merged --author app/renovate`; if none is merged yet, the
  head of an open Renovate PR branch, noting that the branch check is skipped in dry-run) →
  "would tag v0.6.10" (or the current next patch), (b) the "Croft 0.6.9" commit → "not a
  dependency-only merge". Both run URLs in the commit message. Also run actionlint on all three
  workflows.

## Item 4: docs
- README "Releases": the manual release is a tag; the patch/minor convention; the auto-release and
  its allow-list; how to hold back (pause: close the Renovate PR, or `suspend` the Flux
  ImageUpdateAutomation). AGENTS.md "Running things" gets one sentence pointing there.
- `renovate.json5`: add `"automergeSchedule"` is NOT wanted; leave automerge as is. Only check the
  automerge rules still make sense with auto-release (patch/digest/pin automerge → release) and
  say so in the commit message.

## Out of scope
A stable tag (the orchestrator tags 0.6.9/0.7.0), the site repo, Flux manifests, changing CI
jobs, any C# beyond the version header of item 1.
