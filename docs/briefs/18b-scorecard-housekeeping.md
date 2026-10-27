# Brief 18b: Scorecard and CodeQL housekeeping (issue #90)

Read AGENTS.md first and follow it. One commit per item. Most items here are workflows and
configuration, which can't be unit tested: per rule 1, show before/after evidence from running the
thing (a command's output, a parse, a dry run) in each commit message. No pushes except your branch
`brief/18b` to `origin` and ONE draft PR. **Never push a tag, create a Release, dispatch a
workflow, or change repository settings.** The release items are proven at the next real release
by the orchestrator. Every suite goes through `dev/with-suite-lock.sh`. No `src/Croft.Web` change,
so no E2E run.

Other briefs running at the same time: 18a (tests, `dev/with-suite-lock.sh`, AGENTS.md's lock
paragraph), 18c (a migration, AGENTS.md "Pending contracts"), 18d (branding in `src/Croft.Web`).
Don't edit their files.

## Where Scorecard stands (fetched 2026-10-27, scan of e14e9a9: 6.3)

```
0 Security-Policy      | security policy file not detected
9 Pinned-Dependencies  | Warn: nugetCommand not pinned by hash: .github/workflows/ci.yml:45
                       | Info: 33/33 GitHub-owned and 8/8 third-party actions pinned, 4/4 container images pinned
-1 Signed-Releases     | no releases found
3 Branch-Protection    | (repository settings: not this brief)
```

Facts that correct the issue text: private vulnerability reporting is **already enabled**
(`gh api repos/stevedcc/croft/private-vulnerability-reporting` → `true`). `release.yml` already
creates a GitHub Release per tag (:301-358, v0.7.0 to v0.7.4 exist) and attests both images, but
the attestations are pushed to the registry only (`push-to-registry: true`, :149-153), and
`gh release view v0.7.4 --json assets` shows **0 assets**. Scorecard judges releases by their
signature/provenance **assets**. Confirm that, and the exact suffixes it accepts (e.g. `.sigstore.json`,
`.intoto.jsonl`), from the Scorecard source (`checks/raw/signed_releases.go` and
`checks/evaluation/signed_releases.go` in ossf/scorecard) before item 3. Quote the lines in the
commit message.

## Item 1: SECURITY.md

Add `SECURITY.md` at the repository root: supported versions (only the latest release tag gets
fixes, and the moving image tags `X.Y` / `latest` follow it); how to report (GitHub private
vulnerability reporting, the link
`https://github.com/stevedcc/croft/security/advisories/new`; no public issues for vulnerabilities);
what to expect (acknowledgement within 7 days; it's a one-maintainer project, so say so plainly);
scope (this repository's code and images; a site that runs Croft reports to that site's owner).
No e-mail address in the file (rule 3 spirit: no personal contact data in the repo). Link it
from the README's security section (`## Security headers and Content Security Policy` is the
nearest; add one sentence at its start, or a short `## Reporting a vulnerability` section just
before it).

Evidence: `gh api` isn't needed. Show the rendered headings (`grep '^#' SECURITY.md`) and the README link.

## Item 2: pin the NuGet restore by hash (lock files)

`.github/workflows/ci.yml:44` runs `dotnet restore Croft.slnx`. Scorecard wants NuGet packages
pinned by hash: lock files (`packages.lock.json`) restored in locked mode.

1. `Directory.Build.props`: `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>`, with
   a comment saying why (content hashes for every package, Scorecard Pinned-Dependencies), and
   `<RestoreLockedMode Condition="'$(CI)' == 'true'">true</RestoreLockedMode>`, so CI refuses a
   restore that doesn't match the committed lock files while local restores keep updating them.
   Don't touch `<Version>` (AGENTS.md: README "Releases").
2. Generate the lock files (`dotnet restore Croft.slnx`) and commit every `packages.lock.json` it writes.
3. **The Dockerfiles restore with a runtime identifier** (`dotnet publish -a $TARGETARCH`), so
   their restore needs runtime-specific entries a plain lock file may not have. Find out what
   happens: run the opt-in image tests (`dev/with-suite-lock.sh dotnet test tests/Croft.Api.Tests
   --no-build --filter "Category=Container"` and `"Category=ReadyToRun"`; they publish exactly as
   the Dockerfiles do). If locked mode breaks them, the Dockerfiles keep an unlocked restore
   (`CI` isn't set in the image build, so the condition above already does that) and you say so.
   If the only fix is adding `<RuntimeIdentifiers>` or changing a Dockerfile, **stop and report
   (rule 8)**: that changes the images and needs the orchestrator.
4. Renovate: check in Renovate's docs that its NuGet manager updates `packages.lock.json` when it
   bumps a package (quote the docs line in the commit message), so the automerge path keeps
   working. If it doesn't without a config change, add the smallest one to `renovate.json5`.

Evidence: `dotnet restore Croft.slnx --locked-mode` succeeds on the committed files. Changing one
`PackageReference` version locally without restoring makes `--locked-mode` fail (break-it-once,
then revert). Paste both outputs. Run the API and Web suites once (`--no-build` after
`dotnet build Croft.slnx`), plus the two opt-in categories from step 3.

## Item 3: release assets Scorecard can see

In `release.yml`, attach the provenance of both images to the GitHub Release as assets:
- `actions/attest-build-provenance` already runs per image. Give it an `id` and keep its output
  `bundle-path` (the Sigstore bundle of the attestation). Upload it as a workflow artifact named per
  image, as the job already does for the digests (`digest-*`).
- The `release` job downloads them and passes them to `gh release create` (or a `gh release upload`
  right after) as `croft-api-<version>.<suffix>` and `croft-web-<version>.<suffix>`. Use the
  suffix the Scorecard source names for **provenance** if a Sigstore bundle legitimately is one
  (it contains a DSSE envelope with an in-toto SLSA statement). If Scorecard's provenance suffix
  expects a different format than the bundle, use the signature suffix that honestly matches what
  the file is. Never name a file for a format it isn't.
- Add to the notes' verification section how to check an asset:
  `gh attestation verify oci://<image>@<digest> --repo stevedcc/croft --bundle <asset>`.
- The branch test path (`workflow_dispatch` on a branch) creates no Release and must stay that way.
- Permissions stay least-privilege: no new write permission on any job other than what the upload
  needs (`contents: write` on `release` already exists).

Evidence: the workflow still parses: run `actionlint` from its container image
(`podman run --rm -v` isn't allowed by AGENTS.md "Containers", so instead use
`go run github.com/rhysd/actionlint/cmd/actionlint@<a version at least two weeks old>` if Go is
installed, or the release binary downloaded into the scratch directory, checksum verified against
the release's checksums file). Show the actionlint output for all workflows before and after. In
the PR description, write how the orchestrator checks it at the next release (asset names, one
`gh attestation verify --bundle` command).

## Item 4: CodeQL analyses `src/`, not test stubs

Two open alerts are test stubs returning a fake token: `cs/sensitive-data-transmission` at
`tests/Croft.Api.Tests/Social/BlueskyAnnouncerTests.cs:40` and `SocialAnnounceTests.cs:614`
(alerts #15 and #16). For a compiled language in `build-mode: manual`, `paths-ignore` in a CodeQL
config doesn't stop extraction of code the build compiles (check this in GitHub's CodeQL docs and
quote it). So the fix is the build step: in `.github/workflows/codeql.yml`, the C# job builds the
two apps and the shared library only (`dotnet build src/Croft.Api/Croft.Api.csproj` and
`src/Croft.Web/Croft.Web.csproj`; `Croft.Shared` comes with them), not `Croft.slnx`. Add a
one-line comment on why. The `actions` language job is unchanged.

Evidence: both `dotnet build` commands succeed in your worktree after deleting its `src/*/bin`
and `src/*/obj`, and their output names no `tests/` project. The alerts closing is checked after merge by the orchestrator
(they close as fixed once the default branch scan no longer finds them).

## Finally

API and Web suites once after the last change (with the opt-in `Container` and `ReadyToRun`
categories from item 2), summary lines in your report. Then `requesting-code-review`.

## Out of scope

- Branch-Protection settings, CII badge, fuzzing, and anything else that's a repository setting
  or a decision for the owner.
- Changing a Dockerfile (item 2 step 3 says when to stop).
- `auto-release.yml` / `nightly.yml`, unless item 3 needs `auto-release.yml`'s dispatch to pass
  something new (it shouldn't: the release workflow runs on the tag ref either way).
