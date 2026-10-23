# Public-release re-check (2026-10-23)

A re-check of [the 2026-10-19 readiness review](2026-10-19-public-release-readiness.md) at
`blog-framework` 521a2c9 ("Version 0.6.8"), about 300 commits later. Scope: secrets, real people's
data, site-specific data, licences of what was added since, and the status of every 2026-10-19
finding. Scanned: an exported copy of the tree (`git archive`), 1,213 files.

**Result: ready to publish.** No secret, no real person's data, no Critical or Important finding.
M-1 (a stale number in the README) was fixed straight after this re-check.

## Owner decisions (2026-10-23)

- **The docs stay public** (briefs, specs, plans, reviews, retros, fixtures). They are the record of
  how Croft was built and are meant to be read. M-4 ("the owner's infrastructure map is spread
  across the briefs") is accepted: the host names it lists (`crawford.dev`, `api.`, `registry.`,
  `authentik.`, `pki.`) are public in DNS and in Certificate Transparency logs anyway.
- **Capacity figures stay public.** The briefs and measurements say how much traffic the
  Raspberry Pi cluster takes before it breaks. Accepted: a homelab is easy to overload anyway.

## Scans

| Check | How | Result |
|---|---|---|
| Secrets | gitleaks (`zricethezav/gitleaks:latest`, `dir` mode, values redacted) over the whole tree | 4 hits, all false positives: the RFC 6455 sample `Sec-WebSocket-Key` (twice, `CircuitAdmissionTests.cs`), a Blazor persistent-state key derived from a component name (`MainLayoutPersistentStateTests.cs`), a dummy JWT with a two-character signature (`TokenStoreProtectionTests.cs`) |
| `.env.example` | read | dev-only values (`secret…`, `dev…`, localhost URLs) |
| Real people in the social fixtures | every `handle`, `acct`, `username`, `display_name`, URL host and reply text in `docs/briefs/12a-fixtures`, `docs/briefs/12d-fixtures`, `tests/Croft.Api.Tests/Fixtures/Social` | invented throughout: `reader<hex>` / `Reader <hex>`, `owner`, `did:plc:test…`, hosts `social.example`, `mastodon.example`, `blog.example`, texts "Example text <hex>." |
| Personal identifiers outside `docs/` | grep for the owner's names, handles, DID, e-mail, workstation name, private address ranges | only intended hits: `LICENSE` copyright, the README linking crawford.dev and Chargehand, a comment in `dev/black-watch-banner.py`, and the private ranges in `SocialEgressGuard` (the SSRF guard) and its tests |
| Images | listed every image in the tree | only the neutral default branding (`branding-default/`, `branding.example/`) and an empty E2E test image; no photographs |
| Tracked but ignored files | `git ls-files -ci --exclude-standard` | none |
| Licences of packages added since 0.6.2 | AngleSharp (MIT), AwesomeAssertions (Apache-2.0), Markdig (BSD-2), Microsoft.Extensions.Http.Resilience (MIT), SkiaSharp + NativeAssets (MIT; Skia BSD-3) | all open source; SkiaSharp and Skia are already in `THIRD-PARTY-NOTICES.md` |

## The 2026-10-19 findings

| Finding | Status |
|---|---|
| I-1 `croft-web-db` Secret missing from the README | fixed (13d): README "What the app needs" |
| I-2 `.env` is read only by `start-local.sh` | fixed: README "Why the script and not `dotnet run`" |
| I-3 migrations skipped silently | fixed: the script applies them and stops with an error if it cannot |
| I-4 owner's handle and e-mail in `src/` and `tests/` | fixed: none left outside `docs/` |
| I-5 Web tests need a container runtime | fixed: README "Running tests" says so |
| I-6 notices cover only four icons | fixed: highlight.js, fonts, MudBlazor, Material icons, SkiaSharp listed |
| I-7 no image build step; ReadWriteMany | fixed (13d): "Building the images" and the storage note |
| M-1 "about 160 requests a second" without evidence | fixed: the README now cites [the 2026-10-23 runs](../measurements/2026-10-23-load-test.md) (600 req/s plain, 800 compressed, limited by TLS on the Pis); the 2026-10-16 note says it is superseded |
| M-2 OIDC scopes hard-coded | fixed: `Oidc__Scopes` configurable |
| M-3 stale smoke-test runbook | fixed (13d) |
| M-4 infrastructure map in the docs | accepted by the owner (above) |
| M-5 brand assets and mockups in `docs/` | fixed: moved to the private site repository; two neutral HTML mockups remain |
| M-6 FluentAssertions 8.x licence | fixed: AwesomeAssertions 9.6.0 |
| M-7 README E2E steps | fixed: `dev/run-e2e.sh` |
| M-8 git identity | still the owner's call at publish time: history is not published (one fresh commit), so only that commit's author matters; use `steve@crawford.dev` or the GitHub noreply address, not the personal Gmail address |

## Before the push

1. Create the single public commit from the tree at the release tag, authored with the chosen
   address.
2. The owner's final look, then push and switch the repository to public.
