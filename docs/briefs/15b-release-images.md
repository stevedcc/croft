# Brief 15b: release workflow — multi-arch images to GHCR, signed (issue #10)

Read AGENTS.md first. One commit per item. No C# changes; evidence is real workflow runs.
**Pushing exception:** you MAY push your branch `brief/15b` to `origin` (github.com/stevedcc/croft)
and open ONE draft pull request from it, and push further commits to it. Never push to `main`,
never create or move tags, never touch repository settings, secrets or rulesets. The workflow may
push images to GHCR only under the test tag named below.

**Owner-visible outcome:** pushing a tag `vX.Y.Z` publishes `ghcr.io/stevedcc/croft-api:X.Y.Z`
and `ghcr.io/stevedcc/croft-web:X.Y.Z` (plus `X.Y` and `latest` for non-prerelease tags) as one
OCI index each with `linux/amd64` and `linux/arm64`, signed with cosign (keyless, GitHub OIDC),
with SLSA build provenance attached, and a GitHub Release created from the tag with the image
digests in its notes.

## Facts
- `src/Croft.Api/Dockerfile` and `src/Croft.Web/Dockerfile` cross-publish: `FROM
  --platform=$BUILDPLATFORM ... sdk:10.0` + `dotnet publish -a $TARGETARCH`; the final stage has no
  `RUN`, so `docker buildx build --platform linux/amd64,linux/arm64` needs no QEMU. Build context is
  the repo root (check the Dockerfiles' COPY paths).
- GHCR login: `docker/login-action` with `GITHUB_TOKEN` (`packages: write`); `id-token: write` for
  cosign keyless; `attestations: write` + `actions/attest-build-provenance` for provenance;
  `contents: write` only in the job that creates the Release.
- Pin every third-party action by full commit SHA with a `# vX.Y.Z` comment.
- Tags are the owner's release gate: the workflow triggers on `push: tags: ['v*.*.*']` and on
  `workflow_dispatch` with an input `test_tag` (default `ci-test`) that builds and pushes ONLY
  `ghcr.io/stevedcc/croft-*:ci-test-<short sha>`, signs it, and creates NO Release and no
  `latest`/`X.Y` tags.

## Item 1: `.github/workflows/release.yml`
One job per image via a matrix (`api`, `web`), each: checkout, `docker/setup-buildx-action`,
login, `docker/metadata-action` for the tags above (semver from the git tag; the dispatch test tag
otherwise), `docker/build-push-action` with both platforms, `provenance: false` on the build step
and `actions/attest-build-provenance` on the pushed digest instead (one attestation per image),
`sigstore/cosign-installer` + `cosign sign --yes <image>@<digest>`. A final job (tag pushes only)
creates the GitHub Release (`gh release create` with `--generate-notes`, appending both digests and
`cosign verify` instructions with the expected `--certificate-identity-regexp` and
`--certificate-oidc-issuer`).
Evidence: one `workflow_dispatch` run from your branch (GitHub allows dispatch on a branch's
workflow file once the file exists on the default branch — if it does not yet, say so in the report
and test as far as `act` or a push-triggered dry-run lets you; do NOT work around it by pushing to
main or tagging). After the run: `docker manifest inspect ghcr.io/stevedcc/croft-api:ci-test-<sha>`
shows two platforms; `cosign verify` with the identity regexp succeeds. Put both outputs in the
commit message.

## Item 2: image smoke test in the same workflow
After the push, a job that runs each image for both architectures where it can (`linux/amd64`
natively; `linux/arm64` via QEMU `docker/setup-qemu-action` only if it starts within a minute,
else amd64 only and say so) with `docker run --rm` and checks `dotnet Croft.Api.dll --version`-style
startup is NOT available — use the `migrate` entry's `--help`/dry behaviour or `/health/live` of
the Web image with a dummy `BlogApi__BaseUrl`; read `src/*/Program.cs` to find a startup path that
needs no database and no IdP. If none exists, the smoke test is `docker run --rm <image> dotnet
Croft.Api.dll --help` style and the report says why.

## Item 3: documentation
`README.md`: a "Releases" section (tag → images on GHCR, how to verify a signature, that the
cluster still pulls `registry.crawford.dev` until the site repo switches), and `k8s/README.md` gets
the GHCR image names next to the existing ones.

## Out of scope
CI tests (brief 15a), Renovate, the site repo, anything under `src/` or `tests/`, pushing a real
`v*` tag.
