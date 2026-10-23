# Brief 13i: the remaining Minor findings (.NET, architecture, 13e and 13g reviews)

Read AGENTS.md first and follow it. One commit per item, **test first: write the test, run it,
watch it fail for the right reason, then the code** (break-it-once is an extra check, never the
red evidence instead of it). E2E twice at the end, the final whole-branch review.

**Source:** `docs/reviews/2026-10-19-dotnet-code-review.md` (M-x), `docs/reviews/2026-10-19-architecture.md`
(A-9, A-13), and the deferred Minors of the 13e and 13g final reviews (quoted in the items). Each
was checked against `blog-framework` at d2d8dfc (2026-10-20); the file:line is given per item.
Confirm each "Now:" before changing it, and read the finding before its item. Already done
elsewhere, so not here: M-3 (one notify per write, 13c), M-10/11/13/16/19/21 (13e), M-12 (13b),
M-17 (13a/13b), A-10's AuthorId (13b), the README's `croft-web-db` row.

**Owner-visible outcome:** nothing changes for readers. The code does what its comments and
AGENTS.md say, a few per-request costs go away, the failures that were silent are logged, the
migration guard catches the raw-SQL and NOT-NULL cases it claims to, and the five accessibility
leftovers are closed.

**CSP impact: none.** `CspSnapshots.txt` byte-identical. Any change that seems to need a CSP
change: stop and report.

## Item 1: API service tidy-ups (M-4, M-6, M-7, M-8 first half, M-9)
- M-6: remove the dead `catch (UnauthorizedAccessException)` (`PostsController.cs:67`); the
  `ForbiddenException` → 403 path is the contract (pin it with a test if none exists).
- M-7: `PostService.cs:518` uses the injected `DateTimeNormalizer` instead of its inline copy.
  Test: an Unspecified, a Local and a Utc `PublishedAt` give the same instant as the normaliser.
- M-8 (first half only): tag resolution loads the matching tags in one query
  (`Where(t => slugs.Contains(t.Slug))`, `PostService.cs:588`). Test: create/update with five
  tags runs one tag SELECT (capture the SQL like 13e item 4 did). The materialised counts are
  out of scope.
- M-9: `ContentImporter.cs:63` looks up by a dictionary. Test: an import whose post points at a
  media id that isn't in the archive still fails the way it does today (check what `Single`
  throws now and keep that behaviour). No timing tests.
- M-4: `MediaService` streams the upload straight to `IFileStorage.SaveAsync` (no second
  `MemoryStream`, `:48`), and the `FileExtensionContentTypeProvider` (`:39`) is a static field.
  Check first that nothing needs a seekable stream (image dimension probe, hashing); if
  something does, keep one buffer and say why. Tests: the existing upload tests stay green;
  add one that the saved bytes equal the uploaded bytes for a 9 MB file.
Break-it-once: put the per-tag query back → the one-SELECT test fails.

## Item 2: Category and tag reads follow the public-read cache boundary (M-1)
Now: `CategoryService.cs:16,55,84` and `TagService.cs:13,52` cache for every caller;
`PostService` caches only `CacheablePublicRead()` (anonymous) reads.
- Apply the same gate: staff reads bypass the cache. Test: an Admin renames a category, then an
  Admin list read shows the new name immediately even when the invalidation NOTIFY is held back
  (block the listener, as the cache tests do), while an anonymous read still uses the cache.
Break-it-once: remove the gate → the Admin test reads the old name.

## Item 3: Analytics computes its table names once (M-5)
Now: `AnalyticsService.cs:63,104` call `db.Model.FindEntityType` per beacon;
`PageViewRetentionHostedService.cs:60-62` hard-codes `"PageViews"`; `HashIp` re-encodes the HMAC
key per call (check, 13c changed this file).
- Names computed once (static, from the model) and used by both the insert and the retention
  DELETE; the HMAC key bytes computed once.
Tests: a unit test that the retention SQL uses the model's table name (rename the table in a
test model → the SQL follows); the existing analytics and retention tests stay green.
Break-it-once: hard-code the retention name again → the test fails.

## Item 4: Web and shared hot-path and lifetime tidy-ups (M-14, M-15, M-20, M-22, M-23)
- M-14: `TelemetryPrivacy.cs:54` uses a `[GeneratedRegex]`; `:105` avoids `TagObjects.ToArray()`
  (two-pass or a pooled list). Existing telemetry privacy tests must stay green; add one that a
  tag with `?a=1` is still redacted.
- M-15 (and A-13's unbounded dictionary): `UserAccessTokenService` removes a session's lock entry
  when the session ends (logout, the purge, 13b's expiry) and disposes it; a faulted refresh
  whose caller gave up is logged (a continuation), not left to `UnobservedTaskException`.
  Tests: after logout of a session that refreshed, the map no longer holds it; a refresh that
  fails after the caller's token fired logs one Warning.
- M-20: `PostBody.razor`, `MainLayout.razor`, `ThemeSwitch.razor` implement `IAsyncDisposable`
  and dispose their `IJSObjectReference`, with the `JSDisconnectedException` guard
  `DraftReviewPage.razor` uses. bUnit tests: disposing each component disposes its module.
- M-22: `Home.razor`, `Category.razor`, `Tag.razor` (and the PostEditPage uses) read the page
  number once (`[SupplyParameterFromQuery]`/one parse per render), no repeated
  `new Uri(Navigation.Uri)`. Existing paging tests stay green.
- M-23: `SecurityTxtChecker.cs:115` prunes with a `foreach` over a snapshot; the `#pragma` at
  `:134` goes, replaced by `[LoggerMessage]` methods.

## Item 5: failures that were silent are logged (13e review Minor)
Now: `MediaGrid`'s load catch, `PostsList.razor` (`:74-80` at the 13e review) and
`DraftsPage.razor` (`:56`) show the error UI but log nothing; the sibling paths log through
`ApiFailureLog` (13e item 1).
- Log through `ApiFailureLog` at Warning, never for `SessionExpiredException`, same as 13e.
Tests: per page, an API failure on load logs one Warning naming the page; a session expiry logs
nothing. Break-it-once: drop one call → its test fails.

## Item 6: the accessibility leftovers (13g review Minors)
- "1 posts": `Category.razor:25` and `Tag.razor:25` pass `PostCount.Text(...)`, as the comment in
  `PostCount.cs` already promises. Test: a category and a tag with one post read "1 post".
- W-095's error colour reaches dialogs: move the `--error-text` definitions from `.shell` to the
  theme blocks on `:root`/`html` and drop the `.shell` prefix from the rule
  (`MainLayout.razor.css:23-40`), keeping the CSP hash unchanged (no theme change). Test: a
  text-variant `Color.Error` button inside a MudDialog has ≥ 4.5:1 in both themes (add it to the
  matrix's dialog scans or measure it directly).
- Schedule disabled for over-long content links its reason: `PostEditorForm.razor:19-20` sets
  `aria-describedby` for both disable reasons. Test (bUnit): over the limit → the button's
  `aria-describedby` names an element whose text explains it.
- Rename `NameEditor.StartAsync` (not async) to `Start`.
- `editor.js` `focusIn` logs `console.warn` when it gives up after its retries (W-094's entry
  mentions the warning).

## Item 7: the migration guard sees raw SQL and NOT-NULL columns (A-9 (a))
Now: `MigrationContractGuard.cs` matches four operation types and ignores `SqlOperation`; an
`AddColumn` with `nullable: false` and no default passes.
- Flag `SqlOperation` whose text matches `DROP|RENAME|SET NOT NULL|ALTER COLUMN .* TYPE` (case
  insensitive) unless the migration carries `[ContractMigration]` (the existing override), and
  `AddColumnOperation { IsNullable: false, DefaultValue: null, DefaultValueSql: null,
  ComputedColumnSql: null }`.
- The existing migrations must pass: any that the new rules flag get `[ContractMigration]`
  with a reason (they're released; say so per migration in the commit).
Tests: a fake migration with `migrationBuilder.Sql("ALTER TABLE x DROP COLUMN y")` fails the
guard; one with a NOT NULL column without a default fails; the same with a default passes.
Break-it-once: remove the SqlOperation rule → the DROP test passes wrongly.
A-9 (b) and (c) are about already-released migrations: not here; add a short "rules for data
migrations" paragraph to AGENTS.md (dual-write during the overlap; data backfills that degrade
public output render the old value until done, commit per batch).

## Item 8: lifetimes and docs (A-13)
- The auth cookie's lifetime follows `Auth:SessionLifetime` (`ExpireTimeSpan`, sliding kept), so
  a staff member isn't signed out after 14 days of absence while the session still has 16 days
  (13b made the session the real limit). Test: the issued cookie's expiry equals the configured
  lifetime.
- `k8s/README.md:95` ("Every reader holds a Blazor circuit") rewritten for the static reading
  pages: circuits are staff-only, sizing follows staff, not readers.
- The optional network-policy component says it assumes Traefik in `kube-system` and how to
  patch the selector (`k8s/components/network-policies/*.yaml` header comment and README).
- The forwarded-headers README section adds one example: a layer-4 load balancer in front of
  the ingress sends PROXY protocol to the ingress, the ingress writes `X-Forwarded-For`, and
  Croft trusts one hop from the ingress's pod network.

## Out of scope
- Caching and output caching: M-2 (stampede, negative cache), M-18 and A-12 (feed/sitemap), A-11
  (search), M-8's materialised counts: the output-cache brief.
- A-10's separate public and staff DTOs, A-13's per-request session memo and scheduler metric:
  0.7.0.
