# Brief 13d: public release readiness (review findings I-1…I-7, M-1…M-3, M-5…M-8)

Read AGENTS.md first and follow it. One commit per item, TDD where behaviour changes (items 4,
6), before/after evidence for documentation and build changes (AGENTS.md rule 1), E2E twice at
the end, the final whole-branch review.

**Source:** `docs/reviews/2026-10-19-public-release-readiness.md`. Read each finding before its item.

**Owner-visible outcome:** a stranger can clone Croft, run it locally and deploy it from the README
alone; every claim in the README is backed in the repository; every bundled third-party piece is
attributed; nothing in the code, tests or docs images identifies the owner's site; the test
dependencies are open source.

**Owner decisions (2026-10-19):** the briefs and specs keep the infrastructure hostnames (M-4: no
change). The UI-redesign spec's brand assets and mockup images go (M-5: the banner photo is not the
owner's and its provenance is unknown). The public commit author will be steve@crawford.dev (the
orchestrator does that when publishing; not part of this brief).

**CSP impact: none.**

## Item 1: deploying from the README works (I-1, I-7, M-3)

- README "Deploying a site": the `croft-web-db` Secret and the Web database the k8s base requires
  (what, why, how to create it; match `k8s/secrets.example.md`); building and pushing the images
  (`docker`/`podman build` for both Dockerfiles, multi-arch note, the `images:` override); the media
  PVC needs **ReadWriteMany** (why: several Web/API pods; what to do on a cluster without RWX —
  one API replica, or an RWX storage class).
- `docs/runbooks/smoke-test.md`: bring it up to date with the current endpoints and pages, or
  delete it and say where its content went.
- Evidence: walk the README yourself as a stranger would (`kubectl kustomize k8s` renders; every
  command named exists; every Secret the manifests reference is documented). List each place you
  had to fix in the commit message.

## Item 2: local development works as written (I-2, I-3, M-7)

- The manual `dotnet run` path: either make it work (the apps read `.env` in Development only — a
  small loader, or `--launch-profile`/`launchSettings.json` with the needed values), or remove it
  from the README and point to `./start-local.sh`. Pick the simpler correct one and say why.
- Migrations: README says exactly what runs them (k8s: the `migrate` init container; locally:
  `start-local.sh` after `dotnet tool restore`, or `dotnet Croft.Api.dll migrate`); make
  `start-local.sh` **fail loudly** instead of silently skipping migrations when `dotnet-ef` is
  missing (or run `dotnet tool restore` itself).
- README E2E section: describe `dev/run-e2e.sh` as the way, matching AGENTS.md.
- Evidence: a fresh clone in /tmp, follow the README to a running local site (show the commands
  and that the home page answers 200), and `start-local.sh` without the tool → a clear error.

## Item 3: container-dependent tests are labelled (I-5)

- The Croft.Web.Tests classes that need a container runtime (Testcontainers) carry a category
  (e.g. `Category=Container` like the API's) or move to an integration project; README and
  AGENTS.md say which suites need Docker/Podman and how to run without it
  (`--filter "Category!=Container"`). Don't weaken or skip anything by default: the full default run
  stays the same set of tests for a developer with a container runtime.
- Evidence: with no container socket, the filtered run passes; with one, the default run still runs
  all 1088+.

## Item 4: nothing in code or tests identifies the site (I-4)

- `steve@crawford.dev` and any other owner-specific value in `src/` and `tests/` (doc comments and
  test data included) become `example.com`-style values. Add a test that fails if `crawford` (case-
  insensitive) appears in any file under `src/` or `tests/` (allow-list nothing; if something truly
  must stay, stop and report).
- Break-it-once: add `crawford` to a comment → the test fails.

## Item 5: third-party notices are complete (I-6)

- `THIRD-PARTY-NOTICES.md` lists every bundled third-party piece with source, version, licence and
  where its licence text lives: highlight.js, the three font families (OFL, Reserved Font Names),
  the vendored Microsoft skills (`.claude/skills/`, per `MICROSOFT-SKILLS.md`), the Material search
  glyph in `Search.razor` (Apache-2.0, add its attribution), the existing four icons, and anything
  else you find bundled in `wwwroot` or `src` (search, don't assume).

## Item 6: claims match the code (M-1, M-2)

- M-1: add `docs/measurements/2026-10-16-load-test.md` summarising the cells2 run that supports
  "about 160 requests a second" (the orchestrator supplies the numbers below), and link it from the
  README sentence. Numbers (Croft 0.6.0, four Raspberry Pi 4, cells layout, k6 open-model ramp,
  2-minute steps, public page mix): 40 req/s p95 37 ms; 80: 46–49 ms; 110–120: 68–87 ms;
  **160: p50 32 ms, p95 270 ms, 0 errors**; 169: p95 677 ms; 200: load shedding (503) starts;
  231: break. 0.6.1 on 2026-10-18 after a placement fix: 160 req/s p95 758 ms (red node hotter;
  see the measurement's notes).
- M-2: the OIDC scope list becomes configuration (`Oidc:Scopes`, default today's list), and the
  README says what the `groups` claim is for and what to do with a provider that names it
  differently (the existing role-group mapping). Test: configured scopes reach the authorize
  request; the default is unchanged. Break-it-once: ignore the setting → the test fails.

## Item 7: open-source test dependencies (M-6)

- Replace FluentAssertions 8.x with **AwesomeAssertions** (the Apache-2.0 fork of FluentAssertions 7,
  same API) in every test project. Mechanical: package and namespace changes only; any API
  difference you hit gets the smallest equivalent rewrite. No assertion weakened (rule 5): the test
  count and every test's assertions stay the same.
- Evidence: all suites pass with the same totals as before; `grep -r FluentAssertions` finds
  nothing.

## Item 8: the redesign spec ships no brand assets or unlicensed images (M-5, M-8)

- Remove from `docs/superpowers/specs/2026-10-04-ui-redesign/`: `crawford-tartan.svg`, `logo.svg`,
  `favicon.ico`, `apple-touch-icon.png`, and every mockup image or HTML that embeds the banner
  photograph or the site's branding (`mockups/*.png`, and the mockup HTML files with embedded
  `data:` images of it). Keep the spec and plan text; where they referenced a removed image, a
  one-line note "mockup removed before publication (site branding, image of unknown provenance)".
  (The orchestrator keeps copies in the private site repository.)
- M-8's other points: fix tracked-but-ignored files and the two stale names the review lists.
- Evidence: `git ls-files` shows none of the removed files; no `data:image` in `docs/superpowers`
  that shows the site's branding or a photograph.

## Out of scope
- M-4 (hostnames in briefs: owner keeps them).
- The commit author (the orchestrator, at publication).
- Findings of the security and architecture reviews (briefs 13a, 13b, 13c).
