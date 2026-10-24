# Brief 16a: .NET 11 (RC 2, go-live) across Croft

Read AGENTS.md first and follow it. Load `migrate-dotnet10-to-dotnet11` (the migration itself) and,
from item 5 on, `writing-dotnet11-code` (it applies once the TFM is `net11.0`). One commit per item,
test first where behaviour changes; the rest is evidenced by suite runs. No pushes except your
branch `brief/16a` to `origin` and ONE draft PR. E2E instances 1..3 are yours (nothing else runs on
this machine during this brief; still check `pgrep -af "dotnet test|run-e2e"` before a suite).

**Owner decisions (settled):** .NET 11 RC 2 goes to production (go-live licence; "a small blog,
nothing crazy"); the release after this brief is **v0.7.0** (features bump the minor). Linux Arm64
has no new hardware baseline (the LSE requirement is Windows-only; the Pi 4 nodes stay), x86-64-v2
is met by any node we would add.

**Owner-visible outcome:** crawford.dev runs on .NET 11 with the footer saying 0.7.0; nothing a
reader or the admin sees changes; the rolling update needs no downtime (an empty-operations
migration at most; see item 3).

**CSP impact: none.** `CspSnapshots.txt` byte-identical.

## Facts
- Today: `net10.0`, SDK `10.0.402` in `global.json` (`rollForward: latestFeature`), Microsoft.*
  packages `10.0.13`, `Npgsql.EntityFrameworkCore.PostgreSQL` and `Npgsql.OpenTelemetry` `10.0.3`,
  `Microsoft.Extensions.Http.Resilience` `10.11.0`, `MudBlazor` `9.11.0`, `dotnet-ef` in
  `dotnet-tools.json`, images `mcr.microsoft.com/dotnet/sdk:10.0` / `aspnet:10.0` (digest-pinned by
  Renovate; W-103's global `ARG` lines must stay).
- On NuGet (checked 2026-10-24): `Microsoft.EntityFrameworkCore`, `Microsoft.AspNetCore.*`
  `11.0.0-rc.2.26504.105`; `Npgsql.EntityFrameworkCore.PostgreSQL` `11.0.0-rc.2`. Check
  `Npgsql.OpenTelemetry`, `Microsoft.Extensions.Http.Resilience` (its own versioning), `dotnet-ef`,
  `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore`
  for their 11 RC 2 versions; MudBlazor stays 9.11 (a net10.0 library runs on net11.0).
- SDK 11 RC 2 is installed in `~/dotnet` and `/opt/dotnet` by the owner before you start
  (`~/dotnet/dotnet --list-sdks` shows 11.0.100-rc.2.x; use that exact version in `global.json`).
  CI installs from `global.json` (actions/setup-dotnet resolves an exact prerelease version; if it
  does not, say so with the run output).
- The two skills: `.claude/skills/migrate-dotnet10-to-dotnet11/` (breaking changes, written against
  Preview 3: anything in RC 2 beyond it you find in the official "What's new"/"Breaking changes"
  pages, cite them) and `.claude/skills/writing-dotnet11-code/` (written against RC 1).
- AGENTS.md "Workarounds": every `docs/workarounds.md` entry has an "Upgrade check"; an upgrade
  brief walks the whole register.

## Item 1: toolchain and packages
`global.json` → the installed RC 2 SDK; `TargetFramework` `net11.0` in every project (find where it
is set: `Directory.Build.props` or each csproj); all Microsoft.* / EF / Npgsql / dotnet-ef to their
11 RC 2 versions; `LangVersion` stays default (no `preview`). Dockerfiles: `sdk:11.0` and
`aspnet:11.0` (check the tags exist on MCR with `podman manifest inspect`; if RC images are only
under `11.0-rc` or similar, use that tag and say so; drop the stale `@sha256` digests, Renovate
re-pins them). `dotnet build Croft.slnx -warnaserror` clean is this item's evidence (fix compiler
errors here; analyzer *warnings* that are new in SDK 11 go to item 6, so if the build fails only on
new IDE/CA warnings, list them in the message and temporarily note them; do not disable rules).

## Item 2: breaking-change walk
With the migration skill: go through its list and the RC 2 pages, and for each entry say in the
commit message "applies / does not apply, because …", with the file for the ones that apply.
Known candidates in Croft:
- **Unhandled `BackgroundService` exceptions stop the host**: `SocialAnnounceHostedService`,
  the scheduled-publish service, `PageViewRetentionHostedService`, warm-up, the cache
  listeners: each `ExecuteAsync` must catch and log what it already tolerates; add a test per
  service that a thrown exception inside one tick does not end the service (red first).
- **ZipArchive CRC validation**: content export/import (`ContentExporter`/`ContentImporter`): a
  truncated archive now throws `InvalidDataException`; test the importer's error path with a
  corrupt archive (red first if the message/behaviour changes).
- **`dotnet publish --self-contained` parsing**: the Dockerfiles pass `--self-contained false`;
  verify the published output is framework-dependent (the `Category=ReadyToRun` test checks).
- System.Text.Json defaults, `HttpClient`/resilience changes, OpenTelemetry's ASP.NET Core
  instrumentation (the dotnet11 skill's table mentions `.AddSource("Microsoft.AspNetCore")`):
  check `Croft.Web`/`Croft.Api` telemetry setup and the `TelemetryPrivacy` tests.
- Blazor Server in .NET 11 (circuit pausing on inactive tabs, relative navigation): read the
  skill's Blazor section; apply what AGENTS.md "Interactivity" allows, and nothing that changes a
  reading page.

## Item 3: EF Core 11 and the snapshot
`dotnet tool run dotnet-ef migrations add DotNet11Snapshot -p src/Croft.Api -s src/Croft.Api`.
Expected: empty `Up`/`Down` with a changed `BlogDbContextModelSnapshot.cs` (annotation changes).
If `Up` is not empty, every operation must be expand-only (the migration guard test decides; a
contracting one means stop and report). Run the API suite (migrations run from scratch per class),
the `Category=Container` tests (the `migrate` entry in the images) and `ConcurrentWebMigrationTests`.

## Item 4: the workaround register walk
For every entry in `docs/workarounds.md`, run its "Upgrade check" on .NET 11. An entry whose
behaviour is fixed: remove the compensating code and its markers, move the ID to "Retired" with
the reason; an entry still needed: say "re-checked on 11.0 RC 2, still needed" in its text. One
commit per retired workaround (its named test must still pass without it), one commit for the
re-check notes. `WorkaroundRegisterTests` green.

## Item 5: suites and images
Full API, Web and E2E (`dev/run-e2e.sh`, default instances) twice at the end, the opt-in
`Category=Container|Category=ReadyToRun`, and a local image build for both platforms the way the
release workflow does (`podman build --platform linux/amd64,linux/arm64` or two builds): the
images start and `/health/live` answers 200 with `X-Croft-Version` (see `release.yml`'s smoke
job). CSP snapshot unchanged.

## Item 6: the new idioms get analyzers and the skill gets the truth
- `.editorconfig`: list the IDE/CA rules that are new in SDK 11 or newly cover a C# 15 construct
  (read the SDK's analyzer release notes or diff `dotnet format --verify-no-changes
  --diagnostics` output); raise the ones matching `writing-dotnet11-code`'s table to `warning`;
  fix what lights up (one commit). Don't touch rules that have no entry in the table.
- `writing-dotnet11-code/SKILL.md` and `whats-new.md`: correct anything RC 2 changed since RC 1
  (every API in the table must compile on this SDK; verify by using each once in a scratch file,
  not committed); update the "Written against" line.
- AGENTS.md: the SDK line (`10.0.4xx` → the 11 band), the `writing-dotnet11-code` row ("now
  applies"), the pending-contracts list untouched. README: the prerequisites.
- AGENTS.md, the skills table: a reviewer rule next to `test-anti-patterns` / `assertion-quality`:
  **task and final reviewers judge new or changed C# against `writing-dotnet11-code`'s defaults
  table; an old-style construct in new code where the table names the .NET 11 form is a finding.**
  (Owner, 2026-10-24: agents know the new features but default to the surrounding older code
  unless something makes them choose.)

## Out of scope
The `v0.7.0` tag (orchestrator, after verify), Renovate config, the site repo and flux repo
(the k8s base is unchanged unless item 2 needs an env var), MudBlazor 10 (if one exists, a
separate brief), rewriting code that isn't touched (AGENTS.md "C# style").
