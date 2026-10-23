# Brief 09h: every workaround is listed, marked in the code, and kept in sync by a test

Read AGENTS.md first and follow it. Implement inline (`executing-plans`), one commit per item, TDD
for the sync test, break-it-once, all three suites at the end (E2E twice), the final whole-branch
review. This brief changes no behaviour: only comments, one document, one test, and AGENTS.md.

**Owner-visible outcome:** before an upgrade (.NET 11, MudBlazor, Npgsql, Playwright), one
document lists every place where Croft compensates for a behaviour of a framework, library,
browser or platform, ordered by importance: what it compensates for, what our code does, which
test fails without it, and how to check whether it is still needed. A reader can stop when it
gets too detailed; an agent doing the upgrade reads it to the end. Every listed place carries a
`WORKAROUND(W-NNN, <library>)` marker in the code, so it can be found with one grep and removed
cleanly, and a test fails if the document and the markers drift apart.

**Evidence:** a read-only sweep of `blog-framework` at 998583c, 2026-10-16 (106 candidates). The
orchestrator copies it into your worktree as `.superpowers/09h-inventory.md` (git-ignored, never
commit it). Its codes (B1, M3, E4, …) are used below. Its line numbers are from 998583c and will
have moved; find each place by file and symbol. The inventory may be wrong: where the code doesn't
match what it says, don't invent an entry. Report it in the Ledger and leave it out.

## Running alongside 09f: two phases

Brief 09f (static reading pages) is still running and rewrites some of the files this brief
marks. Until the orchestrator tells you 09f is accepted, **don't edit these files** (09f owns
them):
- `AGENTS.md`;
- `src/Croft.Web/Program.cs`, `src/Croft.Web/Components/App.razor`,
  `src/Croft.Web/Components/Layout/ThemeSwitch.razor(.css)`;
- the pages `Home`, `Post`, `Category`, `Categories`, `Tag`, `StaticPage`, `Search`
  (`.razor` and `.razor.css`);
- `src/Croft.Web/Theme/*`;
- `src/Croft.Web/wwwroot/js/post.js`, `shell.js` and `static-glue.js`;
- every file under `tests/` whose name starts with `Static`, and `tests/Croft.Web.E2E/CspTests.cs`,
  `CircuitAdmissionTests.cs` (E2E and unit), `BrandingEndpointTests.cs`, `BlazorTelemetryTests.cs`,
  `PageLoadRaceTests.cs`, `SearchPageTests.cs`, `PostPageLoadTests.cs`, `ReadingPageLoadTests.cs`.

**Phase 1 (now):**
- item 1, with every entry written. For entries whose sites are in 09f's files, describe the
  code as it is on your branch and add the line "**Pending 09f:** re-check after the merge".
  W-004 to W-006 are headings with "Pending 09f" only.
- item 2 for every site outside 09f's files.
- item 3: the sync test, with a temporary allow-list of the IDs whose markers wait for phase 2.
  The allow-list is a named constant with a comment pointing to this brief.

Then report, and wait. Don't merge on your own.

**Phase 2 (when told 09f is accepted):**
- `git merge --no-edit blog-framework` (AGENTS.md's merge rules apply);
- finish the "Pending 09f" entries against the merged code, and add 09f's own workarounds
  (its markers say `WORKAROUND(<library>)` without an ID);
- the remaining markers;
- empty and delete the allow-list (the sync test must then pass with no exceptions);
- item 4;
- the final verification. A file listed above that 09f did *not* change in the end is fine to
  mark now too.

## Item 1: `docs/workarounds.md`

**Do:** create the register.

- **Header:** what counts as a workaround (code that exists, or has its shape, only because a
  dependency behaves a certain way, and would change or go if that behaviour changed); how to use
  the register on an upgrade (for each entry: remove the marked code, run the named test, then
  keep or delete the entry); the marker format (item 2). Name the versions it was written
  against: .NET 10.0.13 packages, SDK 10.0.401, MudBlazor 9.11.0, Npgsql.EFCore.PostgreSQL
  10.0.3, Markdig 1.4.0, Playwright 1.63.0, Testcontainers 4.15.0, bUnit 2.11.3, xUnit 2.9.3,
  Postgres 18.
- **Entries:** in **exactly the order and with exactly the IDs below**. The order is the
  importance ranking: tier A first. Each entry is a `### W-NNN: <title>` heading followed by:
  - **Library:** the library and the behaviour, one sentence;
  - **Our code:** what it does about it, with file paths and symbol names (no line numbers);
  - **Fails without it:** the test(s) that fail if it is removed, or "none: manual check" and
    how to check by hand;
  - **Upgrade check:** one sentence.
- Merged entries (two codes on one line below) become one entry with all the sites.
- 09f's own workarounds (W-004 to W-006, phase 2) come from the markers 09f left in the code (they say
  `WORKAROUND(<library>)` without an ID) and 09f's commit messages. If 09f added more than these
  three, add them at the end of tier A as W-087 onwards, and say so in the Ledger.

Tiers are `##` sections with a one-line explanation each.

**Tier A: an upgrade can break production without any test failing, or the cost is high**
- W-001 B4: circuit admission depends on how Blazor starts and reconnects circuits (negotiate, `id` key, disconnect beacon).
- W-002 S12: the copied .NET 10 template reconnect modal (otherwise CSP violations on every circuit drop).
- W-003 M3 and S3: the search input wears MudBlazor classes and copied CSS, and hides WebKit's search decorations (09f item 3 turned it into a GET form: describe it as it is after 09f).
- W-004 (09f): enhanced navigation resets `<html>` attributes to the server's copy; the saved theme and sidebar state are restored.
- W-005 (09f): MudBlazor palette variables stay light on static pages (describe what 09f did about it).
- W-006 (09f): `data-enhance` on the search GET form.
- W-007 M2: the staff-area CSP boundary (`data-enhance-nav="false"`, `target="_top"` on crossing links).
- W-008 T6: `HeadOutlet` renders only one `HeadContent` provider, so every head surface emits the feed link.
- W-009 B16: the markdown editor's rendered/produced value guard (typed characters lost otherwise).
- W-010 B1: SignalR hub `MaximumReceiveMessageSize` sized for the 200,000-character post limit.
- W-011 B10 and P2: prerender-to-interactive DOM swap races (the MutationObserver waits, `data-interactive`, retrying expects).
- W-012 B8 and B9: `FocusOnNavigate` steals the fragment scroll; `h1:focus` outline hidden.
- W-013 B21: forwarded headers enabled by environment variable trust every proxy.
- W-014 B23: the API's exception handler logs unhandled exceptions itself (double logging if .NET re-adds it).
- W-015 M4: MudBlazor typography variables overridden with `!important`.
- W-016 S4: `touch-action: none` and the pointer gestures on the outline row, including 09f item 6's touch slop.
- W-017 S1: the phone drawer's `100dvh`, own scrolling and safe-area inset (Safari's toolbar).
- W-018 S5: the outline's natural-height measurement and scroll-anchoring handling in `post.js`.

**Tier B: an upgrade breaks a test or start-up; the fix is upgrade work**
- W-019 B32 and M12: `MudThemeProvider` rendered at start-up to hash its style blocks (chart block included).
- W-020 M1: the copied MudBlazor scrollbar CSS.
- W-021 E1 and E2: the session advisory lock around the whole migration chain, with the lifted command timeout.
- W-022 E4: the LISTEN/NOTIFY cache listener (own connection, synchronous event handed to a Channel, clear on reconnect).
- W-023 B11: loads in `OnParametersSetAsync` keyed on the URL, `[PersistentState]` (what remains after 09f).
- W-024 B12: `NavigationManager.NotFound()` for 404s.
- W-025 B7: `BlazorDisableThrowNavigationException`.
- W-026 B5: `ContentSecurityFrameAncestorsPolicy = null` (one CSP writer).
- W-027 B6: rate-limiter exemption via `StaticAssetDescriptor` endpoint metadata.
- W-028 B26: Data Protection application name and the EF key entity shape for the key import.
- W-029 E8: `HasSentinel(true)` on the tag flag.
- W-030 E12: timestamps truncated to microseconds.
- W-031 E6: page-view insert as raw `ON CONFLICT DO NOTHING`.
- W-032 E15: the token store upsert with hard-coded column names.
- W-033 E14: hand-written partial indexes with enum integers in the filters.
- W-034 E3: raw ADO command for `pg_try_advisory_xact_lock`.
- W-035 E17: framework meter names subscribed by string.
- W-036 M9: the icon catalogue built by reflection over `Icons.Material.Filled`.
- W-037 M7: `MudAutocomplete` parameters (no 10-item cap, toggle label, no coercion).
- W-038 M6: the data grid's page-size selector hidden (unnamed combobox).
- W-039 M5: underlined links in post bodies (MudBlazor's link reset).
- W-040 B14: `InvokeAsync(StateHasChanged)` after `MudDataGrid.ServerData`.
- W-041 B15: tag/category rows merged by id (a duplicate `@key` crashes the circuit).
- W-042 B13: JS interop guards in `DraftReviewPage.DisposeAsync`.
- W-043 B17: a 401 inside a circuit forces a login redirect (no cookie validation inside a circuit).
- W-044 B19: the page-view beacon body read as raw JSON (`sendBeacon` sends `text/plain`).
- W-045 B20: `/backchannel-logout` mapped outside the router.
- W-046 B27: `id_token_hint` on sign-out (Authentik rejects sign-out without it).
- W-047 B28: the OIDC authority in `form-action` (Chrome checks redirects).
- W-048 B31: the cloned culture with ISO short dates (MudBlazor pickers).
- W-049 B3: the second, explicit circuit-settings parse at start-up.
- W-050 E10: filtering on the post count before projecting (verify first: the inventory marked it "likely").
- W-051 L1: the second Markdig renderer for heading text that matches `AutoIdentifiers`.

**Tier C: values tuned from measurements; re-measure on an upgrade**
- W-052 B2: circuit retention (20 circuits, 60 s).
- W-053 K9: Workstation GC for croft-web.
- W-054 E5: the connection-pool cap and auto-prepare.
- W-055 K2: `ndots: 2`.
- W-056 K3: `preStop` sleep and termination grace period.

**Tier D: browser CSS**
- W-057 S2: prefixed `-webkit-line-clamp`.

**Tier E: test infrastructure** (no production effect; upgrade work for the suites)
- W-058 T3: the hand-rolled blazorpack circuit client.
- W-059 E16: the migration contract guard calls `Migration.Up` by reflection.
- W-060 L3: test interceptors keyed on EF/Npgsql command shapes (`LOCK TABLE`).
- W-061 T13: the HA tests' own cookie jar (`CookieContainer` refuses `secure` cookies over http).
- W-062 T1: the reference-counted shared Postgres container (xUnit v2 has no assembly fixture).
- W-063 M10: async disposal of the bUnit context (MudBlazor dialog services).
- W-064 T4: E2E instance mapping from NUnit worker numbers.
- W-065 P1: polling with a synchronous XHR; `FillAsync` and newlines.
- W-066 P3: the URL-wait hang capture (a diagnostic, not a fix).
- W-067 P4: the blocked-storage shadow getter (Chromium only).
- W-068 P5: tap before drag in the outline tests.
- W-069 P6: departure modelled as navigation to `about:blank` (context close runs no `pagehide`).
- W-070 P7: the CSP probe reads the element, not `sample`.
- W-071 P8: Chromium tolerances and the JS-disabled limits.
- W-072 P9: the screenshot gate's Chromium flags.
- W-073 M11: MudBlazor picker interaction in E2E.
- W-074 M8: `MudPopoverProvider` and focus in bUnit autocomplete tests.
- W-075 T2: the loopback TCP bridge for client spans.
- W-076 T7: bUnit `UploadFiles` on a worker thread.
- W-077 T8: `BunitPersistentComponentState` for persisted state.
- W-078 T9: the CSP test filter waits for `OnStarting`.
- W-079 T10: the cache eviction test waits on the count (`IMemoryCache` bookkeeping on the pool), and `EvictionReason.Replaced` in `PublicReadCache`.
- W-080 T12: telemetry tests wait on the exported item.

**Tier F: deployment and dev tooling**
- W-081 K7: network-policy labels for K3s/Traefik; one `from` item means AND.
- W-082 K8: `USER $APP_UID` and port 8080 in the Dockerfiles.
- W-083 K12: the mock OIDC proxy rewrites only `token_endpoint`.
- W-084 K10: Podman `--remote`, `:Z` and `0.0.0.0` in `dev/ha-local.sh`.

**Not workarounds** (leave out; list them in one closing section "Considered and left out", one line each, so the next sweep doesn't re-add them): B18, B22, B24, B25, B29, B30, E7, E9, E11, E13, E18, K1, K4, K5, K6, K11, K13, K14, S6, S7, S8, S9, S10, S11, S13, T11, L2.

**Tests:** none for the document itself (item 3 checks it).

## Item 2: the markers

**Do:** every site of every entry is marked with its extent, so a workaround can be removed or
disabled mechanically for testing (amended by the orchestrator: start and end markers, not a
single line). In the file's comment syntax: `//` (C#, JS), `@* … *@` (Razor), `/* … */` (CSS),
`#` (shell, YAML, Dockerfile), `<!-- … -->` (csproj, XML, HTML).
- **Block:** a start line `WORKAROUND(W-NNN, <library>): <one-line behaviour>` directly above the
  code and an end line `END WORKAROUND(W-NNN)` directly below it. Always a pair, even for a single
  line or attribute: everything between them is the workaround and nothing else. Where a comment
  already explains the behaviour, the start sits above it and the comment stays inside the block.
- **Whole file:** a file that exists only as a workaround (a copied template, a hand-rolled
  client) gets `WORKAROUND-FILE(W-NNN, <library>): <behaviour>` within its first 5 lines and no end
  marker.
- Where a block cannot be wrapped without changing code (inside a Razor attribute list, a single
  expression, a continued shell command), wrap the smallest enclosing statement or element. If even
  that is impossible, stop and list it in the Ledger.
- The existing `mudblazor-scrollbar-copy` markers in app.css stay; the W- pair goes outside them.
- Replace 09f's ID-less markers with their IDs (and pair them). An entry with several sites has
  several markers. Don't change code, only comments. Markdown docs (README, k8s/README.md,
  AGENTS.md) get no markers.

Also fix the stale class comment in `tests/Croft.Web.E2E/LongPostEditorTests.cs` (inventory
T14): `ReconnectModal.razor` is rendered by `App.razor` now.

**Tests:** the build and all three suites stay green (comments only).

## Item 3: the sync test

**Do:** a unit test in `tests/Croft.Web.Tests` (next to `K8sBaseTests`, which already reads
repository files) that:
- collects every `WORKAROUND(W-NNN,`, `END WORKAROUND(W-NNN)` and `WORKAROUND-FILE(W-NNN,` marker
  under `src/`, `tests/`, `k8s/`, `dev/` and the Dockerfiles, skipping `bin/`, `obj/`,
  `node_modules/` and vendored `wwwroot/lib/`;
- collects every `### W-NNN:` heading in `docs/workarounds.md`;
- asserts: every marker's ID has an entry; every entry has at least one marker; IDs are unique
  and well-formed (`W-` plus three digits); a marker without an ID (`WORKAROUND(` followed by a
  library name) fails the test; every start has exactly one matching END with the same ID later
  in the same file; no END without a start; blocks of the same ID don't nest; `WORKAROUND-FILE`
  appears only within the first 5 lines. Failure messages name the file and line of each offender.

**Tests:** the test itself, TDD. Break-it-once, four times (the fourth, added with the start/end
markers: delete one END marker, the unpaired start fails): delete one marker (the entry
without a marker fails the test); add `WORKAROUND(W-999, Test)` to a source file (the marker
without an entry fails); turn one marker back into an ID-less one (fails). Quote each failure
in the commit message.

## Item 4: the rule

**Do:** AGENTS.md: a short "Workarounds" section under Conventions. Code that works around a
dependency's behaviour gets a register entry and a marker in the same commit. The sync test
enforces it. Upgrade briefs walk the register. Add the register to the `migrate-dotnet10-to-dotnet11`
row of the skills table ("walk docs/workarounds.md").

## Out of scope

- Removing or changing any workaround, even one that looks unnecessary (say so in the Ledger;
  the upgrade briefs decide).
- Line numbers in the register.
