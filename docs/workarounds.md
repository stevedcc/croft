# Workaround register

Every place where Croft compensates for the behaviour of a framework, library, browser or
platform, ordered by importance. Read it before an upgrade (.NET 11, MudBlazor, Npgsql,
Playwright, a browser). You can stop reading when it gets too detailed: the entries are in the
order an upgrade is most likely to hurt, and the upper tiers are the ones that matter most.
An agent doing the upgrade reads it to the end.

## What counts as a workaround

Code that exists, or has its shape, only because a dependency behaves a certain way, and that
would change or go if that behaviour changed. A style choice, a security rule that Croft would
keep anyway, or an ordinary use of a documented API is not a workaround; the closing section
"Considered and left out" lists the near misses so a later sweep does not add them again.

## Written against

.NET 11.0.0-rc.2.26504.105 packages (SDK 11.0.100-rc.2.26504.105), MudBlazor 9.11.0,
Npgsql.EntityFrameworkCore.PostgreSQL 11.0.0-rc.2 (Npgsql 10.0.3), Markdig 1.5.0, Playwright
1.64.1, Testcontainers 4.16.0, bUnit 2.12.5, xunit.v3 4.0.2 (the `xunit.v3.mtp-off` package,
runner 4.0.1), NUnit 5.0.0 (NUnit3TestAdapter 6.3.1), Microsoft.NET.Test.Sdk 18.11.0, Postgres 18.
The first sweep covered `blog-framework` at 998583c (2026-10-16) on .NET 10.0.13 (SDK 10.0.401);
brief 16a walked every entry on 11.0 RC 2 (each entry's "Re-checked on 11.0 RC 2" note says what
was run); brief 16b moved the test tooling to the versions above (W-063 retired, W-062 re-checked
on xunit.v3).

## How to use it on an upgrade

For each entry: remove the marked code, run the test named under "Fails without it", then keep
the code (and the entry) or delete both. An entry that says "none: manual check" has no test
that notices; follow the check it describes. After an upgrade, change the version line above.

## How far to trust "Fails without it"

The test names under "Fails without it" come from the first sweep (comments, code shape and the
tests that cover the area). Nobody has yet removed each workaround and watched that test fail.
Only W-050 was verified (a probe on 2026-10-16 showed the DTO-level `Where` does not translate);
every other check is a lead, not proof. W-025 and W-049 are known to have no confirmed failing
test. When you walk an entry, remove the code, run the test, and write down what happened.

## The marker

A marker marks the extent of a workaround, so it can be removed or disabled mechanically.

- **Block:** a start line `WORKAROUND(W-NNN, <library>): <one-line behaviour>` directly above the code and an end line `END WORKAROUND(W-NNN)` directly below it, in the file's comment syntax (`//`, `@* ... *@`, `/* ... */`, `#`, `<!-- ... -->`). Everything between the two lines is the workaround, comments that explain it included.
- **Whole file:** a file that exists only as the workaround carries `WORKAROUND-FILE(W-NNN, <library>): <behaviour>` within its first five lines and no end line.

Find every site with `grep -rn "WORKAROUND" src tests k8s dev` plus the repository root's MSBuild
files, `grep -n "WORKAROUND" *.props *.targets` (only `Directory.Build.props` exists today; grep
reports the missing `*.targets`). `WorkaroundRegisterTests` scans the same places and fails when
a marker has no entry here, an entry has no marker, an ID is malformed or repeated, a marker has
no ID, a start has no end (or an end no start), or blocks of one ID nest. New workarounds get an
entry and a marker in the same commit (AGENTS.md, "Workarounds").

## Tier A: an upgrade can break production without any test failing, or the cost is high

### W-001: Circuit admission depends on how Blazor starts and closes circuits

- **Library:** Blazor Server starts a circuit with `POST /_blazor/negotiate` (or a WebSocket upgrade to `/_blazor` with no `id` query key), treats requests with `id` as reconnects, closes on the `/_blazor/disconnect` beacon, and calls `CircuitHandler.OnCircuitClosedAsync` when retention expires.
- **Our code:** `CircuitAdmissionMiddleware.WouldStartCircuit` recognises a start by exactly that shape and refuses it with 503 at `MaxActive`; `CircuitCountingHandler` moves `CircuitCounter` on open and close.
- **Fails without it:** the `Croft.Web.Tests` `CircuitAdmissionTests` and the E2E `CircuitAdmissionTests` fail if the shape changes, but a transport change (for example WebSockets only, no negotiate) would silently disable the gate on a real deployment unless those tests start a real circuit.
- **Upgrade check:** run both `CircuitAdmissionTests`; re-read how the new Blazor starts and reconnects circuits. **Re-checked on 11.0 RC 2 (16a):** still needed: both `CircuitAdmissionTests` (the unit one and the E2E one start real circuits) pass on 11.0 RC 2; the suites of brief 16a pass with it.

### W-002: The copied .NET 10 template reconnect modal

- **Library:** with `#components-reconnect-modal` in the document, `blazor.web.js` only toggles `components-reconnect-*` classes; without it, it builds a shadow-DOM default whose inline `<style>` the enforced CSP refuses on every circuit drop.
- **Our code:** `Components/Layout/ReconnectModal.razor`, `.razor.css` and `.razor.js` are a copy of the .NET 10 Blazor Web App template's modal; ids, classes, the `components-reconnect-state-changed` event and the `Blazor.reconnect()` / `Blazor.resumeCircuit()` calls are the framework contract. `Components/App.razor` renders it.
- **Fails without it:** E2E `CspTests` (the circuit-drop test expects zero violations) and `CircuitAdmissionTests`.
- **Upgrade check:** diff the three files against the new template on every .NET upgrade, then re-check class names, the event name and the `Blazor.*` calls. **Re-checked on 11.0 RC 2 (16a):** still needed: blazor.web.js 11 still builds a shadow-DOM default with an inline style when the modal is missing; the .NET 11 RC 2 template's three files equal ours (ids, classes, event, `Blazor.reconnect()`/`resumeCircuit()`) apart from our markers and integrity attribute and one CSS formatting change (the nested `&[open]` block is written on one rule in the 11 template; same declarations). Not re-copied: no behaviour change.

### W-003: The search field hides WebKit's search decorations and stops Escape clearing it

- **Library:** `type=search` brings a native clear button and decorations in WebKit and Blink, and Escape clears the field natively; the field never had either and must not grow them.
- **Our code:** `Components/Pages/Search.razor.css` hides the `::-webkit-search-*` parts (`display: none`, `-webkit-appearance: none`); `wwwroot/js/shell.js` calls `preventDefault` on Escape for `type=search` inputs. (Since 09i the field is plain markup on Croft's own tokens, so it no longer copies MudBlazor classes or declarations; that part of the old entry is gone.)
- **Fails without it:** `SearchPageTests` and the E2E `StaticSearchTests` cover the field; none is known to assert that the native clear button or Escape-clear is absent, so check by hand in a WebKit or Blink browser.
- **Upgrade check:** re-check in Safari and Chrome after a browser upgrade; drop the CSS if `type=search` stops adding decorations. **Re-checked on 11.0 RC 2 (16a):** The browser is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-004: Enhanced navigation resets `<html>` attributes to the server's copy

- **Library:** Blazor's enhanced navigation patches `<html>` (`data-theme`, the `sidebar-collapsed` class) and the theme switch back to the server's markup, which knows neither (script sets them).
- **Our code:** `shell.js` `restoreSavedState` puts the saved theme and sidebar state back on `<html>`; `static-glue.js` `boot` calls it and re-syncs the theme switch (`showThemeMode`) after every enhanced navigation.
- **Fails without it:** E2E `StaticGlueTests.ThemeAndSidebarChoices_SurviveAnEnhancedNavigation`.
- **Upgrade check:** remove both calls and run that test; .NET 11 may preserve `<html>` attributes across enhanced navigation. **Re-checked on 11.0 RC 2 (16a):** still needed: with the `restoreSavedState`/`showThemeMode` calls removed from `static-glue.js`, `StaticGlueTests.ThemeAndSidebarChoices_SurviveAnEnhancedNavigation` fails on 11.0 RC 2 (enhanced navigation still resets `<html>`).

### W-006: `data-enhance` on the search GET form

- **Library:** a plain `<form method="get">` is a full page load; Blazor handles it as an enhanced navigation only with the `data-enhance` attribute.
- **Our code:** `Components/Pages/Search.razor` marks its GET form `data-enhance`.
- **Fails without it:** E2E `StaticSearchTests.Enter_SearchesByUrl_ShowsTheResults_WithoutAFullReload`.
- **Upgrade check:** remove the attribute and run that test; behaviour of `data-enhance` on forms may change in .NET 11. **Re-checked on 11.0 RC 2 (16a):** still needed: without `data-enhance`, `StaticSearchTests.Enter_SearchesByUrl_ShowsTheResults_WithoutAFullReload` fails on 11.0 RC 2.

### W-007: The staff-area CSP boundary

- **Library:** MudBlazor grids, dialogs and tabs write inline `style` attributes; the CSP is fixed per document and enhanced navigation keeps the first document's policy.
- **Our code:** `AdminAreaCspContributor` adds `style-src-attr 'unsafe-inline'` only for `/admin` and `/editor` documents; `AdminArea.LinkAttributes` gives every link that crosses the boundary `data-enhance-nav="false"` and `target="_top"` (enhanced navigation skips the attribute; the interactive router skips non-`_self` targets), so the target loads as a fresh document with its own policy. `SidebarNav.razor` sets the same two attributes by hand on the log-in and log-out links (server endpoints).
- **Fails without it:** `AdminAreaCspContributorTests`, `AreaBoundaryTests`, E2E `CspTests`.
- **Note (10c):** the same fixed-per-document rule is why the policy differs by document kind (static reading page, interactive document: `CspDocumentKind`, decided from the endpoint when the response starts) and why `CspPolicySnapshotTests` pins one complete header per kind.
- **Retired edge (13a):** the not-found and error pages are static documents now (the policy of a reading page), so enhanced navigation to `/not-found` no longer meets an interactive page with a static policy; the edge in the 10c review is gone.
- **Upgrade check:** a MudBlazor release that stops writing style attributes lets the loosening go; a Blazor change to router link interception or enhanced navigation breaks the boundary trick. **Re-checked on 11.0 RC 2 (16a):** still needed: MudBlazor 9.11 (unchanged) still writes style attributes; `AdminAreaCspContributorTests`, `AreaBoundaryTests` and E2E `CspTests` pass on 11.0 RC 2. The staff documents' style hashes changed with .NET 11's `Math.Round` (CSP snapshot commit of 16a), the boundary did not.

### W-008: `HeadOutlet` renders only one `HeadContent` provider

- **Library:** `HeadOutlet` renders the content of exactly one `HeadContent` per page (a second provider replaces the first).
- **Our code:** every head-emitting surface carries the feed link itself: `SeoHead.razor` and each page's own `<HeadContent><RobotsNoIndex /><FeedAlternateLink /></HeadContent>` (Admin pages, `DraftsPage`, `DraftReviewPage`, `NotFound`, `Error`, `Search`), with `FeedAlternateLink.razor` as the shared element (not marked: it is the feed link itself and survives the workaround; also affected, no marker: `RobotsNoIndex.razor`, which follows the same rule).
- **Fails without it:** `FeedLinkTests` and `SeoHeadTests` (they assert exactly one feed link per page).
- **Upgrade check:** if .NET 11 merges several `HeadContent` providers, this duplication becomes a double-emit bug; remove the per-page copies and put the link in one place. **Re-checked on 11.0 RC 2 (16a):** still needed: `FeedLinkTests` and `SeoHeadTests` (exactly one feed link per page) pass on 11.0 RC 2, so `HeadOutlet` still renders one provider (a merge would double the link and fail them).

### W-009: The markdown editor's rendered/produced value guard

- **Library:** when a render diff writes the bound value back to a textarea it overwrites characters typed while the batch was in flight (measured: 7 of 20 characters lost on burst typing into a 60K post).
- **Our code:** `MarkdownEditor.razor` keeps `_renderedValue` and `_producedValue`; the rendered value follows `Value` only for changes from outside the textarea.
- **Fails without it:** `MarkdownEditorTests` and E2E `LongPostEditorTests` (the 20-keystroke test).
- **Upgrade check:** replace the guard with a plain `@bind` and see whether text is lost. **Re-checked on 11.0 RC 2 (16a):** still needed: with the guard replaced by a plain bound value (the rendered value always following `Value`), `MarkdownEditorTests.MarkdownEditor_UserTyping_IsNeverRewrittenByItsOwnRenders` and E2E `LongPostEditorTests.Editor_TwentyKeysTypedIntoASaved60KPost_EachSurvives_AndTheStoredTextEndsWithThem` fail on 11.0 RC 2.

### W-010: SignalR hub `MaximumReceiveMessageSize` sized for the post limit

- **Library:** SignalR's default 32 KiB receive limit aborts the circuit on one editor input event past about 16,301 ASCII characters, because the event carries the textarea value twice.
- **Our code:** `Program.cs` (`AddHubOptions`) sets `MaximumReceiveMessageSize = 2_097_152` (2^21), derived from the 200,000-character `PostContent` limit. `PersistentStateBudget` (150,000 JSON bytes) keeps `[PersistentState]` hand-overs inside the same limit: persisted state travels about 10.7 times its JSON size in the circuit start message (measured: JSON bytes as base64 in the state dictionary, protected, base64 again), so a 200,000-character post of `€` signs (1.2 MB of JSON) took the circuit down; `PostEditPage` and `DraftReviewPage` drop state over the budget and the circuit fetches again.
- **Fails without it:** E2E `LongPostEditorTests` (it asserts no reconnect, `Editor_200KEuroFill_...` also covers the budget).
- **Upgrade check:** set the default back and run `LongPostEditorTests`; re-derive the size if the SignalR or blazorpack payload shape changes; re-measure the ~10.7x encoding factor and the budget (`PersistentStateBudgetTests` pins the boundaries). **Re-checked on 11.0 RC 2 (16a):** still needed: with the `MaximumReceiveMessageSize` line removed (SignalR's default), the `LongPostEditorTests` fill tests (20K, 60K, 200K, the euro fills) fail on 11.0 RC 2. The ~10.7x encoding factor was not re-measured; `PersistentStateBudgetTests` pass.

### W-011: Prerender-to-interactive DOM swap races

- **Library:** Blazor replaces the prerendered DOM a few milliseconds after the page's first script runs, hands element references over without DOM properties on some navigations, and drops DOM events dispatched before the first interactive render.
- **Our code:** `wwwroot/js/editor.js` (`watchSelection` resolves its root at use time); `WebTest.WaitForInteractiveAsync` waits for `data-interactive="on"` (set by `shell.js` from the first interactive render); the E2E classes use Playwright's retrying expects (also affected, no marker: `HomeTests`, `AccessibilityTests`, `PostPageTests`; no marker, because a class-wide marker would wrap the checks themselves). `post.js` has MutationObserver waiters for `.toc__link` and `.toc-inline`; `shell.js` `init` sets `data-interactive` on the interactive render.
- **Fails without it:** E2E `PostPageTests` (the race test), `ScrollSpyTests`, `EditorTests`, `LargeDataTests`; remove each waiter in turn to see which are still needed.
- **Upgrade check:** .NET 11 may change enhanced navigation or prerender DOM reuse; run the four classes above and watch for flakes. **Re-checked on 11.0 RC 2 (16a):** still needed: the four classes (`PostPageTests`, `ScrollSpyTests`, `EditorTests`, `LargeDataTests`; LargeData is opt-in and was not run) pass on 11.0 RC 2 in the full E2E runs of 16a; the waiters were not removed one by one.

### W-012: `FocusOnNavigate` steals the fragment scroll; `h1:focus` outline hidden

- **Library:** `<FocusOnNavigate Selector="h1">` focuses the heading programmatically, which pulls the viewport back to the hero after a cold load with a `#fragment`; the focus shows a ring.
- **Our code:** `Components/Routes.razor` keeps `FocusOnNavigate`; `wwwroot/js/post.js` (`scrollToHash` and a `focusin` listener) re-asserts the scroll when the h1 gets focus while a hash is set; `wwwroot/app.css` sets `h1:focus { outline: none }`.
- **Fails without it:** E2E `PostPageTests` (the TOC and fragment cases) for the scroll; `AccessibilityTests` for the outline.
- **Upgrade check:** remove the `focusin` listener and see whether `FocusOnNavigate` still steals the scroll. **Re-checked on 11.0 RC 2 (16a):** re-checked on 11.0 RC 2, kept: with the `focusin` listener removed from `post.js`, `PostPageTests` (the TOC and fragment cases) still pass. Not retired, because that test was never shown to fail without the listener on .NET 10 either (see "How far to trust"), so the pass proves nothing about .NET 11; a manual check (a cold load of a post with `#fragment`, Chrome and Safari) decides.

### W-014: The API's exception handler logs unhandled exceptions itself

- **Library:** the .NET 10 exception-handler middleware no longer logs exceptions a handler handled.
- **Our code:** `ApiExceptionHandler` logs 500s at Error with the exception (`LogUnhandledException`) and mapped statuses at Information without a stack trace.
- **Fails without it:** `ApiExceptionHandlerLogTests`.
- **Upgrade check:** if a later version logs in the middleware again, 500s double-log; remove the handler's log then. **Re-checked on 11.0 RC 2 (16a):** still needed: `ApiExceptionHandlerLogTests` passes on 11.0 RC 2, and a probe printing every Error-level entry of the 500 case showed only `Croft.Api.Errors.ApiExceptionHandler` (the .NET 11 middleware does not log a handled exception either, so there is no double log).

### W-015: MudBlazor typography variables overridden with `!important`

- **Library:** MudBlazor writes its `--mud-typography-*-family` variables as inline custom properties on `<html>` at runtime.
- **Our code:** `wwwroot/app.css` overrides them in `:root` with `!important`.
- **Fails without it:** none: manual check (fonts in the browser); `ThemeTests` cover theme switching but not the font families.
- **Upgrade check:** if MudBlazor moves the variables to a `<style>` block or renames them, drop or update the overrides. **Re-checked on 11.0 RC 2 (16a):** MudBlazor 9.11 is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-016: `touch-action: none` and the pointer gestures on the outline row

- **Library:** a drag that starts on the outline row would scroll the page, and a pull consumes the click that follows it (a 3 px move counts as a drag).
- **Our code:** `Components/Blog/ContentsPanel.razor.css` sets `touch-action: none` on `.toc-inline__row`, `.toc-inline__handle` and `.toc-inline__strip`; `wwwroot/js/post.js` handles the pointer gestures, including 09f item 6's touch slop.
- **Fails without it:** E2E `MobileOutlineTests` and `MobileContentsPanelTests` (Chromium touch emulation only; real iOS and Android are a manual check).
- **Upgrade check:** run both classes after a browser or Playwright upgrade; re-test on a real phone. **Re-checked on 11.0 RC 2 (16a):** The browser and Playwright is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-017: The phone drawer's `100dvh`, own scrolling and safe-area inset

- **Library:** Safari's toolbar covers `100vh`, so the drawer's last item sat under it; the home-indicator inset (`env(safe-area-inset-bottom)`) is non-zero only with `viewport-fit=cover`.
- **Our code:** `wwwroot/app.css` sizes `.shell > .sidebar` with `height: 100vh; height: 100dvh; overflow-y: auto` and pads `#sidebar-nav` with `calc(12px + env(safe-area-inset-bottom))`; `Components/App.razor` sets `viewport-fit=cover`.
- **Fails without it:** E2E `PhoneDrawerScrollTests` (390x500 viewport, Chromium, so it checks the CSS contract, not Safari).
- **Upgrade check:** real Safari verification is manual; revisit when `dvh` is universal and Safari's viewport model changes. **Re-checked on 11.0 RC 2 (16a):** The browser is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-018: The outline's natural-height measurement and scroll-anchoring handling

- **Library:** browser scroll anchoring and layout timing: with the pinned or open state classes applied the panel's box measures the row (48 px plus border) instead of the natural block, and resetting the inline height restarts the margin transition, pulsing the footprint for a frame, which scroll anchoring chases so the pin flips back.
- **Our code:** `wwwroot/js/post.js` `measurePanelFootprint` removes the state classes, measures the list block's own box, then restores them, and leaves the inline height and `margin-bottom` alone; `applyPanelState` always sets `height` and `margin-bottom` so they add up to the footprint (the drawer overflows its slot instead of moving the text).
- **Fails without it:** E2E `MobileOutlineTests`, `PostRailStickyTests` and `ScrollSpyTests` (Chromium only; Safari behaviour is a manual check).
- **Upgrade check:** after a browser or Playwright upgrade run those classes, then clear the inline height in `measurePanelFootprint` and see whether the outline sticks at 49 px again. **Re-checked on 11.0 RC 2 (16a):** The browser is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-085: The previous post's scroll listeners are torn down after an enhanced navigation

- **Library:** an enhanced navigation disposes no component and runs no page unload, so listeners and observers started for the previous post stay.
- **Our code:** `static-glue.js` `boot` calls `blogPost.teardownPost()` when the new page has no outline or no post (`post.js` `teardownPost`, `teardownScrollSpy`, `teardownContentsPanel`).
- **Fails without it:** E2E `StaticGlueTests.LeavingAPost_ByEnhancedNavigation_LeavesNoScrollHandlerBehind` and `AfterFiveEnhancedRoundTrips_EverythingStillWorks_AndAScrollFiresTheSpyOnce`.
- **Upgrade check:** remove the two calls and run both tests. **Re-checked on 11.0 RC 2 (16a):** still needed: with the `teardownPost()` call removed, `StaticGlueTests.LeavingAPost_ByEnhancedNavigation_LeavesNoScrollHandlerBehind` fails on 11.0 RC 2 (`AfterFiveEnhancedRoundTrips...` passed).

### W-086: The three-icon `ThemeSwitch` driven by `data-mode`

- **Library:** a static reading page has no circuit, so nothing re-renders the button when the theme changes.
- **Our code:** `ThemeSwitch.razor` renders all three icons and `ThemeSwitch.razor.css` shows the one picked by the button's `data-mode`; `static-glue.js` `showThemeMode` changes `data-mode`, title and label itself.
- **Fails without it:** E2E `StaticGlueTests.ThemeSwitch_TheVisibleIconFollowsTheMode_OnFirstLoad_AndAfterAnEnhancedNavigation`.
- **Upgrade check:** if the reading pages get a circuit again (or a static `@onclick` works), a single re-rendered icon is enough. **Re-checked on 11.0 RC 2 (16a):** still needed: the reading pages are still static (no circuit); `StaticGlueTests.ThemeSwitch_TheVisibleIconFollowsTheMode_...` passes on 11.0 RC 2.

### W-087: Page scripts are started again on `enhancedload`

- **Library:** an enhanced navigation swaps the page's DOM without loading a new document, so module scripts do not run again.
- **Our code:** `static-glue.js` registers `boot` for Blazor's `enhancedload` event as well as the first load; every start is idempotent or tears the previous one down.
- **Fails without it:** E2E `StaticGlueTests.AfterFiveEnhancedRoundTrips_EverythingStillWorks_AndAScrollFiresTheSpyOnce` and `ReadingPageNavigationTests`.
- **Upgrade check:** remove the listener and run those tests. **Re-checked on 11.0 RC 2 (16a):** still needed: with the `enhancedload` listener removed, `StaticGlueTests.AfterFiveEnhancedRoundTrips_EverythingStillWorks_AndAScrollFiresTheSpyOnce` (both viewports) and `ReadingPageNavigationTests.CategoryNavigation_ClientSide_ShowsCorrectContent` fail on 11.0 RC 2.

### W-098: The output cache's serializer changes the case of a header value

- **Library:** ASP.NET Core's output cache stores a response's headers in its own binary format, which keeps well-known strings once and returns them in their canonical spelling. The value `allow` of Blazor's `blazor-enhanced-nav` response header comes back as `Allow` (the `Allow` header name is a well-known string). `blazor.web.js` tests `"allow" !== headers.get("blazor-enhanced-nav")`, case-sensitively, and then falls back to a full page load: enhanced navigation would silently stop working on every cached page.
- **Our code:** `Caching/ReadingPageCache.cs` puts `allow` back in an `OnStarting` callback for the requests the reading-page policy caches.
- **Fails without it:** unit `OutputCacheReadingPageTests.ACachedPage_KeepsTheEnhancedNavigationHeader_ExactlyAsBlazorSentIt` and `SecondAnonymousHit_IsServedFromTheCache_ByteIdentical_AndMakesNoApiCall`.
- **Upgrade check:** remove the callback and run those tests; if the cached header is `allow` again, delete it. The client side (`blazor.web.js`) is the other half: re-grep it for `blazor-enhanced-nav` on a Blazor upgrade. **Re-checked on 11.0 RC 2 (16a):** still needed: with the `OnStarting` callback removed, `OutputCacheReadingPageTests.SecondAnonymousHit_IsServedFromTheCache_ByteIdentical_AndMakesNoApiCall` fails on 11.0 RC 2 (the replayed header differs); blazor.web.js 11 still compares `"allow"!==t.headers.get("blazor-enhanced-nav")`.

### W-099: The output cache's key includes the request's scheme and host

- **Library:** ASP.NET Core's output cache builds its key from the method, scheme, host, path and the vary rules, and offers no way to leave the scheme and host out. A reading page, the feed, the sitemap and an image are the same for every host and scheme.
- **Our code:** `Caching/ReadingPageCache.cs` sets fixed stand-ins for the host and scheme before the cache middleware (for the requests the policies cache: reading pages, the feed and sitemap, images) and puts the request's own values back for the render, so the warm-up's loopback requests (127.0.0.1, http) fill the entry readers get (https, the public host), and a request with a made-up `Host` header mints no entry.
- **Fails without it:** unit `OutputCacheReadingPageTests.AnotherHostOrScheme_ForTheSamePath_IsTheSameEntry` and `MediaCacheTests.EachImageIsItsOwnEntry_AndTheHostHeaderDoesNotMintMore`.
- **Upgrade check:** if `OutputCacheOptions` gains a way to drop the host and scheme from the key, use it and delete the swap. **Re-checked on 11.0 RC 2 (16a):** still needed: `OutputCacheOptions` in ASP.NET Core 11 has the same members as 10 (`SizeLimit`, `MaximumBodySize`, `DefaultExpirationTimeSpan`, `UseCaseSensitivePaths`); `CacheVaryByRules.VaryByHost` exists in both but covers the host only, not the scheme, so the swap stays; `AnotherHostOrScheme_ForTheSamePath_IsTheSameEntry` passes.

## Tier B: an upgrade breaks a test or start-up; the fix is upgrade work

### W-019: `MudThemeProvider` rendered at start-up to hash its style blocks

- **Library:** MudBlazor generates its inline `<style>` text inside the component; a CSP hash must cover the byte-exact DOM text; the provider always emits an extra chart style block and has no switch for it.
- **Our code:** `MudThemeProviderCss.StyleTextsAsync` renders the provider with Blazor's `HtmlRenderer` (MudBlazor services and a no-op `IJSRuntime`; it assumes static rendering never reaches `OnAfterRender`); `MudThemeStyleCspContributor` hashes every block in both theme modes. `ViewsChart.razor` is why the chart block exists, but compensates for nothing itself.
- **Fails without it:** `MudThemeStyleCspTests` (expectations come from a fresh independent render, and the served page is hashed) and E2E `CspTests`.
- **Upgrade check:** any MudBlazor change to style-block text, count or order fails the test. **Re-checked on 11.0 RC 2 (16a):** still needed: MudBlazor 9.11 unchanged; `MudThemeStyleCspTests` pass on 11.0 RC 2. The hashed text itself changed: MudBlazor rounds its palette shades with `Math.Round`, which .NET 11 corrects (16a's CSP snapshot commit), so this render-at-start-up approach is what kept the served policy right.

### W-020: The copied MudBlazor scrollbar CSS

- **Library:** the scrollbar `<style>` block `MudThemeProvider` emits inline would need a CSP hash.
- **Our code:** `MainLayout.razor` sets `DefaultScrollbar="true"` (no inline block) and `wwwroot/app.css` carries the rules byte for byte between `mudblazor-scrollbar-copy:begin` and `:end` (exempt from the no-colour-values rule).
- **Fails without it:** `MudThemeStyleCspTests` (the copy equals a fresh render, modulo CRLF), `BrandingThemeCssTests`.
- **Upgrade check:** when the rules change, update the copy in `app.css`. **Re-checked on 11.0 RC 2 (16a):** MudBlazor 9.11 is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-021: The session advisory lock around the whole migration chain

- **Library:** EF's migration lock is released at each migration's commit while the pending list is computed once, so two concurrent `Migrate` runs over a chain replay the same migration and collide on DDL; wrapping `Migrate` in an outside transaction triggers `MigrationsUserTransactionWarning` and disables the lock. Npgsql's default 30 s command timeout would fail a queued migrator's wait.
- **Our code:** `BackgroundJobLock.RunAsync` takes `pg_advisory_lock` on a pinned-open connection with a manual unlock; `AcquireAsync` lifts the command timeout (`SetCommandTimeout(0)`) around the acquire only; both apps' `MigrationRunner.RunAsync` call it. Also described in AGENTS.md "Migrations" and the init containers in `k8s/`.
- **Fails without it:** `Croft.Api.Tests` `ConcurrentMigrationTests`, `Croft.Web.Tests` `ConcurrentWebMigrationTests` (they force the overlap with interceptors, see W-060), `BackgroundJobLockTests` (35 s `pg_sleep` hold).
- **Upgrade check:** a change in EF migration locking (for example EF 11 locking the whole run) makes the wrapper redundant. **Re-checked on 11.0 RC 2 (16a):** still needed: with both `MigrationRunner`s calling `MigrateAsync` directly, `ConcurrentMigrationTests` ("42701: column \"Kind\" of relation \"Posts\" already exists") and `ConcurrentWebMigrationTests` ("42P07: relation \"UserSessions\" already exists") fail on EF Core 11 RC 2: EF 11 still locks per migration.

### W-022: The LISTEN/NOTIFY cache listener

- **Library:** Npgsql delivers notifications only while the connection is waiting or active; notifications sent while it was down are lost; the `Notification` event is synchronous, so nothing may block in it.
- **Our code:** `CacheListenerService` listens on its own connection outside EF's pool (own Application Name, `KeepAlive = 15`), hands each notification to a `Channel<CacheGroup>` drained on a background loop, clears the whole cache after every (re)connect `LISTEN`, and reconnects with backoff.
- **Fails without it:** `PublicReadCacheTests` and `ReadReplicaRoutingTests`.
- **Upgrade check:** an Npgsql major upgrade can change `Notification` or `WaitAsync` semantics. **Re-checked on 11.0 RC 2 (16a):** Npgsql 10.0.3 (the ADO driver; only the EF provider moved to 11) is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-023: Loads in `OnParametersSetAsync` keyed on the URL, `[PersistentState]`

- **Library:** Blazor reuses a page instance and re-runs the parameter lifecycle only when a parameter value changes (a pager click changes only the query string), and `OnInitializedAsync` runs twice on a full load because of prerender.
- **Our code:** the listing pages (`Home`, `Category`, `Tag`, `Categories`, `Search`) declare `?page=N` (and `q`) as `[SupplyParameterFromQuery]` properties so a pager click re-runs the lifecycle; `MainLayout.razor` keeps `[PersistentState]` for the sidebar state, and since brief 13e so do the admin and editor pages that load in `OnInitializedAsync` or `OnParametersSetAsync` (`TagsPage`, `MediaGrid`, `PostAnalyticsPage`, `PostEditPage`, `DraftReviewPage`) so a full page load fetches once. Since 09f the reading pages are static SSR and no longer use `[PersistentState]`; their `OnParametersSetAsync` loads (keyed on the URL, dropping a superseded load) are the loads themselves, so they carry no marker.
- **Fails without it:** `ParameterChangeTests`, `PageLoadRaceTests`, `ReadingPageLoadTests`, `MainLayoutTests`, E2E `ReadingPageApiCountTests` and `ReadingPageNavigationTests`.
- **Upgrade check:** a .NET 11 change to `PersistentState` or parameter-change detection shows up in those tests. **Re-checked on 11.0 RC 2 (16a):** still needed: the named unit tests and E2E `ReadingPageApiCountTests`/`ReadingPageNavigationTests` pass on 11.0 RC 2.

### W-024: `NavigationManager.NotFound()` for 404s

- **Library:** `NavigateTo("/not-found")` redirects with HTTP 200, whereas `NavigationManager.NotFound()` makes the renderer set 404.
- **Our code:** `NotFound.razor` calls `Navigation.NotFound()` (`/not-found` itself keeps answering 200); `Post.razor`, `StaticPage.razor`, `Category.razor` and `Tag.razor` do the same for unknown slugs.
- **Fails without it:** E2E `NotFoundTests` and `StaticPageTests` (they observe the status code; bUnit cannot).
- **Upgrade check:** run both E2E classes. **Re-checked on 11.0 RC 2 (16a):** still needed: E2E `NotFoundTests` and `StaticPageTests` pass on 11.0 RC 2.

### W-025: `BlazorDisableThrowNavigationException`

- **Library:** the .NET 10 switch makes static-SSR `NavigateTo` stop throwing `NavigationException`.
- **Our code:** `Croft.Web.csproj` sets it to `true` (present since the scaffold commit, without a comment).
- **Fails without it:** none confirmed: `PostsPageTests`, the OIDC login tests and E2E `AuthenticationTests` are the ones to run, but nobody has yet shown that one of them fails without it.
- **Upgrade check:** .NET 11 may make the behaviour the default or drop the switch; remove the property and run those tests. **Re-checked on 11.0 RC 2 (16a):** re-checked on 11.0 RC 2, kept: ASP.NET Core 11 still reads the switch (`Sdk.Server.targets` maps `BlazorDisableThrowNavigationException` to the runtime option; the default is unchanged). With the property removed, `PostsPageTests` and E2E `AuthenticationTests` still pass, as on .NET 10: still no confirmed failing test.

### W-026: `ContentSecurityFrameAncestorsPolicy = null`

- **Library:** Blazor writes its own `frame-ancestors` CSP header on rendered responses, which would collide with ours.
- **Our code:** `Program.cs` sets `ContentSecurityFrameAncestorsPolicy = null` on the interactive render mode so `CspMiddleware` is the only CSP writer.
- **Fails without it:** `CspHeaderTests` (asserts a single CSP header).
- **Upgrade check:** remove the line and run `CspHeaderTests`. **Re-checked on 11.0 RC 2 (16a):** still needed: with `ContentSecurityFrameAncestorsPolicy = null` removed, `CspHeaderTests.PageResponse_CarriesExactlyOneCspHeader_WithTheBasePolicy` and `ReportOnly_TrueWritesTheReportOnlyHeaderName` fail on 11.0 RC 2.

### W-027: Rate-limiter exemption via `StaticAssetDescriptor` endpoint metadata

- **Library:** `MapStaticAssets` files are recognisable only by their endpoint metadata (`Microsoft.AspNetCore.StaticAssets.StaticAssetDescriptor`), not by file extension, and the metadata is visible only because `UseRateLimiter` runs after `UseRouting`.
- **Our code:** `Program.cs` `IsRateLimiterExempt` reads that metadata, so generated endpoints (`theme.css`, `robots.txt`, the feed) are not exempted by mistake.
- **Fails without it:** `PageRequestRateLimiterTests`.
- **Upgrade check:** it breaks if the metadata type or its registration changes. **Re-checked on 11.0 RC 2 (16a):** still needed: `PageRequestRateLimiterTests` pass on 11.0 RC 2 (`StaticAssetDescriptor` gained an `Order` property; the type and its registration are the same).

### W-028: Data Protection application name and the key entity shape

- **Library:** key rings are isolated by application name, so importing file keys into the database ring keeps cookies readable only under the same name; the key table has the shape of the framework's `DataProtectionKey` entity.
- **Our code:** `Program.cs` calls `SetApplicationName("blog-web")` on both the file and the EF ring; `DataProtectionKeyConfiguration` follows the framework entity's columns (also affected, no marker: `DataProtectionKeyImporter`, a feature that inserts key XML in that shape).
- **Fails without it:** `DataProtectionKeysTests`, `DataProtectionKeyImportTests`, `SharedDataProtectionKeysTests`.
- **Upgrade check:** the `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` entity shape is the version-sensitive part. **Re-checked on 11.0 RC 2 (16a):** still needed: `DataProtectionKeysTests`, `DataProtectionKeyImportTests`, `SharedDataProtectionKeysTests` pass with Microsoft.AspNetCore.DataProtection.EntityFrameworkCore 11.0.0-rc.2 (no Web migration was needed: `has-pending-model-changes` reports none).

### W-029: `HasSentinel(...)` on properties with a database default

- **Library:** EF omits a property equal to its CLR default from the INSERT when a database default exists, so an explicit `false` would lose to the database default `true` with the usual `false` sentinel.
- **Our code:** `TagConfiguration` sets `HasDefaultValue(true).HasSentinel(true)` on `CountsTowardsRelated`; `PostConfiguration` sets `HasDefaultValue(1).HasSentinel(1)` on `ReadingMinutes` (and `Post.ReadingMinutes` starts at 1), so a stored 0 is written as 0.
- **Fails without it:** `TagsControllerTests` (the false round trip); `ContentExportTests.Export_WritesEveryFieldOfThePublishedPost_WithRelationsByStableKey` (a stored `ReadingMinutes` of 0).
- **Upgrade check:** newer EF versions may change sentinel handling. **Re-checked on 11.0 RC 2 (16a):** still needed: `TagsControllerTests` and the `ContentExportTests` case pass on EF Core 11 RC 2.

### W-102: hashtag lists as a nullable `jsonb` column through a value converter

- **Library:** EF 10 maps a list of records to a column as a complex collection (`ComplexCollection(...).ToJson()`), but that column is `NOT NULL` with a `'{}'` default, which the expand-only migration rule and an array value both rule out; and EF never runs a value converter on a NULL column value, so a NULL would read as `null` instead of an empty list.
- **Our code:** `HashtagsJson` (a value converter to `jsonb`, an empty list stored as NULL, property access mode "property") and the coalescing setters of `Category.Hashtags` and `Tag.Hashtags`.
- **Fails without it:** `TagsControllerTests.UpdateTag_HashtagsOnlyChange_ShowsInTheAnonymousCachedRead` (a tag that never had hashtags reads back from a NULL column).
- **Upgrade check:** re-try the complex collection on a newer EF; if it can be nullable, drop the converter. **Re-checked on 11.0 RC 2 (16a):** re-checked on EF Core 11 RC 2, kept: a probe (EF 11 + Npgsql 11 RC 2) maps `ComplexCollection(t => t.Hashtags, b => b.ToJson())` on a nullable `List<Hashtag>?` to a nullable `jsonb` column with no default (`IsNullable=True`), so the original obstacle is gone. Not replaced here: the complex collection's JSON shape would have to equal what `HashtagsJson` stores (existing rows, and old pods writing during a rolling deploy), and the model change brings a migration; that is a design change for its own brief, not an upgrade step.

### W-030: Timestamps truncated to microseconds

- **Library:** Postgres timestamps have microsecond resolution; .NET ticks are 100 ns.
- **Our code:** `ScheduledPublisherHostedService.PublishDuePostsAsync` truncates the stamp (`now.AddTicks(-(now.Ticks % 10))`) so rows the tick writes can be recognised after the round trip.
- **Fails without it:** `ScheduledPublisherTests` (the recognised-rows case).
- **Upgrade check:** re-run it after an Npgsql or EF upgrade. **Re-checked on 11.0 RC 2 (16a):** still needed: `ScheduledPublisherTests` pass on 11.0 RC 2 (Npgsql 10.0.3, timestamps still microseconds).

### W-031: Page-view insert as raw `ON CONFLICT DO NOTHING`

- **Library:** a unique violation through `SaveChanges` is logged by Postgres as an error, and EF has no `ON CONFLICT DO NOTHING`.
- **Our code:** `AnalyticsService.InsertPageViewOnceAsync` runs the insert through `ExecuteSqlAsync`; `AnalyticsTables` builds the statement once per model, with table and column names read from the EF model; the id is supplied by hand.
- **Fails without it:** `AnalyticsControllerTests`.
- **Upgrade check:** replace it if EF gains the clause. **Re-checked on 11.0 RC 2 (16a):** still needed: EF Core 11 has no `ON CONFLICT` (no upsert) API; `AnalyticsControllerTests` pass.

### W-093: View count as a raw `ON CONFLICT DO UPDATE` upsert

- **Library:** EF has no `INSERT ... ON CONFLICT DO UPDATE`, and a read-then-write counter loses updates under concurrency.
- **Our code:** `AnalyticsService.IncrementViewCountAsync` runs one `ExecuteSqlAsync` statement; `AnalyticsTables` builds it once per model, with table and column names read from the EF model.
- **Fails without it:** `PostViewCountsTests`.
- **Upgrade check:** replace it if EF gains the clause. **Re-checked on 11.0 RC 2 (16a):** still needed: EF Core 11 has no upsert API; `PostViewCountsTests` pass.

### W-032: The token store upsert with hard-coded column names

- **Library:** EF has no upsert.
- **Our code:** `PostgresTokenStore.UpsertAsync` is one `INSERT ... ON CONFLICT` statement naming EF's default (property-name) columns.
- **Fails without it:** `SharedSessionStoreTests`, `SharedSessionRefreshTests`.
- **Upgrade check:** a column or naming-convention change breaks it. **Re-checked on 11.0 RC 2 (16a):** still needed: `SharedSessionStoreTests`, `SharedSessionRefreshTests` pass on 11.0 RC 2 (column names unchanged; the Web model has no pending changes).

### W-033: Hand-written partial indexes with enum integers in the filters

- **Library:** configuration-class partial indexes would churn on later `migrations add`, and the filters encode the stored integer values of `PostStatus` and `PostKind`.
- **Our code:** the `PublishedAtPartialIndexes` migration creates both indexes by hand; `PostConfiguration` says why it declares none.
- **Fails without it:** `PostPartialIndexTests`, `PublicPageQueryBaselineTests`.
- **Upgrade check:** check that the enum integers still match the filters. **Re-checked on 11.0 RC 2 (16a):** still needed: `PostPartialIndexTests`, `PublicPageQueryBaselineTests` pass on EF Core 11 RC 2 (the enum integers are unchanged).

### W-035: Framework meter names subscribed by string

- **Library:** OpenTelemetry subscribes to meters by name; .NET 10 added the Blazor and SignalR meters.
- **Our code:** `CroftTelemetry` has hard-coded `CommonMeters` and `WebMeters` lists; `SubscribedFrameworkMeters` exposes them to tests.
- **Fails without it:** `BlazorTelemetryTests` (states the exact expected list), `ApiTelemetryTests`, `WebTelemetryTests`, `TraceCompositionTests`.
- **Upgrade check:** new meters must be added by hand. **Re-checked on 11.0 RC 2 (16a):** still needed: `BlazorTelemetryTests` (the exact meter list), `ApiTelemetryTests`, `WebTelemetryTests`, `TraceCompositionTests` pass on 11.0 RC 2; no new framework meter added by hand.

### W-036: The icon catalogue built by reflection over `Icons.Material.Filled`

- **Library:** depends on `Icons.Material.Filled` being public static string fields holding SVG path markup.
- **Our code:** `IconCatalog` reflects over the constants once into a name-to-path map; `CategoryIcon.razor` renders the path as a raw `MarkupString` with a Folder fallback.
- **Fails without it:** `IconCatalogTests` (more than 1000 constants in 9.11.0, sorted), `CategoryIconTests`.
- **Upgrade check:** a change in how MudBlazor represents icons breaks rendering. **Re-checked on 11.0 RC 2 (16a):** MudBlazor 9.11 is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-037: `MudAutocomplete` parameters

- **Library:** the default clips the dropdown at ten items even when more were fetched; the toggle button has no accessible name; the field cannot host per-value markup.
- **Our code:** `PostEditorForm.razor` sets `MaxItems="@((int?)null)"`, `AdornmentAriaLabel`, `Clearable`, `ResetValueOnEmptyText="false"`, `CoerceValue="false"`; the item template carries the icons.
- **Fails without it:** `PostEditorFormTests` (the 15-category test) and E2E `AccessibilityTests`.
- **Upgrade check:** re-run both after a MudBlazor upgrade. **Re-checked on 11.0 RC 2 (16a):** MudBlazor 9.11 is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-038: The data grid's page-size selector hidden

- **Library:** MudBlazor's rows-per-page selector renders as an unnamed ARIA combobox (serious axe `aria-input-field-name`).
- **Our code:** `PostsGrid.razor` sets `PageSizeSelector="false"` on `MudDataGridPager`.
- **Fails without it:** E2E `AccessibilityTests`.
- **Upgrade check:** re-enable the selector on a MudBlazor version that labels it. **Re-checked on 11.0 RC 2 (16a):** MudBlazor 9.11 is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-039: Underlined links in post bodies

- **Library:** MudBlazor's global `a { text-decoration: none }` reset leaves inline links colour-only (axe link-in-text-block).
- **Our code:** `PostBody.razor.css` underlines `.markdown-body ::deep a`.
- **Fails without it:** E2E `AccessibilityTests`.
- **Upgrade check:** re-run it after a MudBlazor upgrade. **Re-checked on 11.0 RC 2 (16a):** MudBlazor 9.11 is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-040: `InvokeAsync(StateHasChanged)` after `MudDataGrid.ServerData`

- **Library:** `MudDataGrid.ServerData` can complete its loader outside the renderer's dispatcher.
- **Our code:** `PostsList.razor` (the posts and pages lists) and `DraftsPage.razor` marshal back with `await InvokeAsync(StateHasChanged)` so the page's own state renders.
- **Fails without it:** `PostsPageTests`, `PagesPageTests`, `DraftsPageTests`.
- **Upgrade check:** relevant on a MudBlazor upgrade. **Re-checked on 11.0 RC 2 (16a):** MudBlazor 9.11 is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-041: Tag and category rows merged by id

- **Library:** a duplicate `@key` crashes the circuit (Blazor's diff behaviour); the app-level cause is an idempotent create racing a list load.
- **Our code:** `TagsPage.razor` `AddCategoryRow` and `AddTagRow` add a row only if its id is absent.
- **Fails without it:** `TagsPageTests` (the race tests).
- **Upgrade check:** re-run them after a Blazor upgrade. **Re-checked on 11.0 RC 2 (16a):** still needed: `TagsPageTests` (the race tests) pass on 11.0 RC 2.

### W-042: JS interop guards in `DraftReviewPage.DisposeAsync`

- **Library:** a statically rendered (prerender) component is disposed where JS interop is forbidden, and during circuit shutdown the call is cancelled or throws `JSDisconnectedException`.
- **Our code:** `DisposeAsync` skips the unwatch unless the watch started and catches `JSDisconnectedException` and `TaskCanceledException`; `JsModuleReference.DisposeAsync` (used by `PostBody`, `MainLayout` and `ThemeSwitch` to release their imported modules) skips a module never imported and catches the same two.
- **Fails without it:** `DraftReviewPageTests`, `JsModuleDisposalTests` (the disconnected-circuit case) and E2E `EditorTests` (remove the guards and watch for prerender or disposal errors).
- **Upgrade check:** re-run both after a Blazor upgrade. **Re-checked on 11.0 RC 2 (16a):** still needed: `DraftReviewPageTests`, `JsModuleDisposalTests` and E2E `EditorTests` pass on 11.0 RC 2 (16a added the same `JSDisconnectedException` guard to every JS interop call for the new BL0016 analyzer).

### W-043: A 401 inside a circuit forces a login redirect

- **Library:** inside an interactive circuit no new HTTP request happens, so cookie `OnValidatePrincipal` never runs.
- **Our code:** `BlogApiClient.SendAsync` turns a 401 for an authenticated user, or an authenticated user with no access token at all, into a forced-load redirect to `/login` (`EndSessionAsync`), after `SessionExpiryHooks` have run so the post editor can write its draft backup first; `SessionExpiredException` is an `HttpRequestException` so pages that handle a failed call keep the circuit alive; `TokenSessionEvents.OnValidatePrincipal` separately rejects principals whose server-side session is gone.
- **Fails without it:** `BlogApiClientTests`, `TokenSessionEventsTests`, `PostEditPageSessionTests`, E2E `SessionLossTests`.
- **Upgrade check:** re-run all four after an ASP.NET Core authentication change. **Re-checked on 11.0 RC 2 (16a):** still needed: `BlogApiClientTests`, `TokenSessionEventsTests`, `PostEditPageSessionTests` and E2E `SessionLossTests` pass on 11.0 RC 2. ASP.NET Core 11 now hands refreshed principals to circuits, which does not cover a session that is gone (no token at all).

### W-044: The page-view beacon body read as raw JSON

- **Library:** `navigator.sendBeacon` posts JSON as `text/plain`, so content-type-checked request JSON helpers would reject it.
- **Our code:** `wwwroot/js/analytics.js` sends the beacon; `Program.cs` reads it with `JsonSerializer.DeserializeAsync` on the raw body.
- **Fails without it:** E2E `AnalyticsViewCountTests`.
- **Upgrade check:** re-run it after an ASP.NET Core minimal-API upgrade. **Re-checked on 11.0 RC 2 (16a):** still needed: E2E `AnalyticsViewCountTests` pass on 11.0 RC 2.

### W-045: `/backchannel-logout` mapped outside the router

- **Library:** the Blazor catch-all would demand an antiforgery token for the IdP's form POST, and `ReadFormAsync` throws `InvalidDataException` or `IOException` on a malformed body.
- **Our code:** `Program.cs` maps the endpoint itself, outside the router, with those exceptions mapped to 400.
- **Fails without it:** `BackchannelLogoutEndpointTests`.
- **Upgrade check:** re-run it after a Blazor or form-parsing change. **Re-checked on 11.0 RC 2 (16a):** still needed: `BackchannelLogoutEndpointTests` pass on 11.0 RC 2.

### W-046: `id_token_hint` on sign-out

- **Library:** this is IdP-dependent: Authentik rejects the end-session request with 400 without `id_token_hint`, and the OIDC handler does not add it because `SaveTokens` is false.
- **Our code:** `TokenSessionEvents.LogoutAsync` reads the ID token from the server-side store, passes it through `HttpContext.Items`, and `OnRedirectToIdentityProviderForSignOut` sets `ProtocolMessage.IdTokenHint`; without a stored token the sign-out stays local.
- **Fails without it:** `TokenSessionEventsTests`.
- **Upgrade check:** re-test against the real IdP on every OIDC handler upgrade. **Re-checked on 11.0 RC 2 (16a):** still needed: `TokenSessionEventsTests` pass with Microsoft.AspNetCore.Authentication.OpenIdConnect 11.0.0-rc.2; not re-tested against the real IdP (needs the production IdP).

### W-048: The cloned culture with ISO short dates

- **Library:** `CultureInfo.GetCultureInfo` returns a cached read-only instance, and MudBlazor's date and time pickers use the culture's short date (US or UK order otherwise).
- **Our code:** `AppCulture.ConfigureDefaults` clones `en-GB`, sets `ShortDatePattern = "yyyy-MM-dd"` and sets the default thread cultures; `BlogBunitContext` applies the same call, so component tests exercise it.
- **Fails without it:** E2E `SchedulingTests` (ISO dates through the pickers); no unit test is known to depend on it.
- **Upgrade check:** also relevant to ICU data changes. **Re-checked on 11.0 RC 2 (16a):** still needed: E2E `SchedulingTests` pass on 11.0 RC 2 (ICU unchanged by this upgrade).

### W-050: Filtering on the post count before projecting

- **Library:** EF cannot compose a `Where` over a projected DTO's computed `PostCount`. Verified on 2026-10-16 with a probe against the shipped packages: `Select(new CategoryDto(...)).Where(d => d.PostCount > 0)` throws "The LINQ expression ... could not be translated".
- **Our code:** `CategoryService.LoadTopAsync` filters and orders on the count expression, then projects.
- **Fails without it:** the category list tests (`CategoriesControllerTests`).
- **Upgrade check:** a newer EF may translate the DTO-level form; try the probe again. **Re-checked on 11.0 RC 2 (16a):** still needed: with the DTO-level form (`Select(new CategoryDto(...)).Where(d => d.PostCount > 0).OrderByDescending(d => d.PostCount)`), `CategoriesControllerTests.ListCategories_WithTop_ReturnsInUseCategoriesByPublishedPostCount` fails on EF Core 11 RC 2 (500: the query still does not translate).

### W-051: The second Markdig renderer for heading text

- **Library:** Markdig's `AutoIdentifiers` builds heading ids from tag-free text.
- **Our code:** `MarkdownRenderer.GetPlainText` renders a heading with a second `HtmlRenderer` (`EnableHtmlForInline = false`) so the TOC text and the generated id agree.
- **Fails without it:** `MarkdownRendererTests`, `TableOfContentsTests`.
- **Upgrade check:** Markdig changes to id generation or rendering APIs would break TOC links. **Re-checked on 11.0 RC 2 (16a):** Markdig 1.5 is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-090: Script integrity digests read from the `MapStaticAssets` ETags

- **Library:** ASP.NET Core's component-side resource collection (`@Assets`) maps a name to its fingerprinted URL but carries only `label`, and the endpoints' `StaticAssetDescriptor` properties drop the `integrity` digest that the build writes into the endpoints manifest. What the descriptor keeps is the identity variant's `ETag`, the quoted base64 SHA-256 of the file.
- **Our code:** `Services/StaticAssetIntegrity.cs` builds `sha256-<etag>` from that ETag; `App.razor` and `ReconnectModal.razor` render it as the `integrity` attribute of every `<script src>` (10c item 3).
- **Fails without it:** unit `ScriptIntegrityTests` (every script carries the digest of the file served at its URL) and E2E `CspScriptTests` (every reading page script has `integrity`, in three engines).
- **Upgrade check:** after a .NET upgrade look at `ResourceAsset.Properties` and `StaticAssetDescriptor.Properties` for an `integrity` entry; if one is there, read it from `@Assets` and delete the service. Note it relies on the ETag staying the SHA-256 of the content; `ScriptIntegrityTests` fails if it does not. **Re-checked on 11.0 RC 2 (16a):** still needed: on 11.0 RC 2 neither `StaticAssetDescriptor.Properties` nor `ResourceAsset.Properties` carry an integrity entry at run time (probe: `fingerprint`, `label`, `original-resource` only; the build manifest has `integrity`, the runtime drops it); `ScriptIntegrityTests` pass, so the ETag is still the SHA-256 of the content.

### W-091: Trusted Types on the reading pages are reported, not enforced

- **Library:** `blazor.web.js`'s enhanced navigation parses the fetched document with `DOMParser.parseFromString` (all three engines; Firefox also sets `HTMLScriptElement.text` when it re-creates the JSON-LD script), which `require-trusted-types-for 'script'` blocks. Measured in the 10c spike: it is the only violator once highlight.js's `innerHTML` is out of our code (`post.js` builds the highlighted DOM itself).
- **Our code:** `Security/Csp/TrustedTypesCspContributor.cs` puts `require-trusted-types-for 'script'; trusted-types 'none'` (no `default` policy, ever) in `Content-Security-Policy-Report-Only` on static documents only; the enforcing header never carries them.
- **Fails without it:** E2E `TrustedTypesTests.EveryTrustedTypesViolationOnTheReadingPages_ComesFromBlazorWebJs` (it expects the reports, and that they all come from `blazor.web.js`) and unit `CspPolicySnapshotTests.OnlyAStaticReadingPage_ShipsTrustedTypes_AndOnlyInReportOnly`.
- **Upgrade check:** remove when `blazor.web.js` is Trusted-Types clean: move the two directives into the enforced policy (base policy, static documents) and delete the report-only path if nothing else uses it. The owner may instead drop `blazor.web.js` from the reading pages (plain navigation), which does the same. **Re-checked on 11.0 RC 2 (16a):** still needed: blazor.web.js 11 has no Trusted Types support (no `trustedTypes` in the file); E2E `TrustedTypesTests` pass on 11.0 RC 2 (the reports still all come from blazor.web.js).

### W-092: A static reading page gets no antiforgery token

- **Library:** Blazor persists `AntiforgeryStateProvider.CurrentToken` while prerendering every Razor component endpoint, form or no form. Generating the token stores the antiforgery cookie and adds `Cache-Control: no-cache, no-store` and `Pragma: no-cache`. `[RequireAntiforgeryToken(false)]` on the page does not stop it (tried in 10c item 5).
- **Our code:** `Security/StaticDocumentAntiforgery.cs` wraps `IAntiforgery` (registered in `Program.cs`) and hands a static document an empty token set instead of calling the framework; interactive documents and every POST are untouched.
- **Fails without it:** unit `CookieTests.StaticReadingPage_SetsNoCookie_ForAnAnonymousVisitor` and `StaticReadingPage_IsNotMarkedNoStore_ByATokenItNeverNeeded`; E2E `ReadingPageCookieTests`.
- **Upgrade check:** remove the wrapper and run those tests; if Blazor stops generating the token for pages that render no form, delete the wrapper. **Re-checked on 11.0 RC 2 (16a):** still needed: with the `IAntiforgery` wrapper removed, `CookieTests.StaticReadingPage_SetsNoCookie_ForAnAnonymousVisitor` (all cases) and `StaticReadingPage_IsNotMarkedNoStore_ByATokenItNeverNeeded` fail on 11.0 RC 2. ASP.NET Core 11's change (static SSR defers antiforgery to the middleware) does not stop token generation while `UseAntiforgery` runs.

### W-094: JS interop after a render can run before the browser has applied the render batch

- **Library:** Blazor Server runs `OnAfterRenderAsync` (and the JS interop call in it) once the batch is on its way, but the browser can apply the batch after it runs that call, so a `document.getElementById` for an element that batch creates comes back empty. Measured on the Categories & Tags rename cell: the Rename button's focus call ran with the previous DOM still in place.
- **Our code:** `wwwroot/js/editor.js` `focusIn` looks the element up again on each animation frame, up to 60 frames; `Components/Admin/NameEditor.razor` calls it after each render that must move focus.
- **Fails without it:** E2E `AdminKeyboardTests.Rename_FocusMovesIntoTheField_EnterSaves_EscapeCancelsAndReturnsFocusToRename` (flaky: focus is left on `<body>` after Enter or Escape).
- **Upgrade check:** replace the retry with a single lookup, run that test several times in a row; keep the retry if it ever fails. **Re-checked on 11.0 RC 2 (16a):** still needed: with the retry replaced by a single lookup, `AdminKeyboardTests.Rename_FocusMovesIntoTheField_EnterSaves_EscapeCancelsAndReturnsFocusToRename` fails on 11.0 RC 2 for both `categories` and `tags`, on the first run.

### W-095: MudBlazor's default error red is below 4.5:1 on the surface

- **Library:** MudBlazor's default `Error` is `#F44336` in the light palette (3.68:1 on white) and `#F64E62` in the dark one (4.30:1 on `#1E293B`); text-variant error buttons (the Delete buttons of the media library and the categories and tags tables) draw their label in it and fail axe's `color-contrast`.
- **Our code:** `wwwroot/app.css` colours enabled `.mud-button-text-error` labels from `--error-text` (`#C62828` light, 5.6:1; `#FCA5A5` dark, 7.6:1), defined on `:root` in three theme blocks (a marked region `BrandingThemeCssTests` allows, like the scrim's) and styled without a layout prefix, because the dialogs MudBlazor opens are rendered outside the layout's shell. It is a stylesheet rule and not a `MudTheme` `Error` colour on purpose: a different palette changes the theme's inline style, and the CSP pins that style's hash (`CspSnapshots.txt`). Error is not a branding token.
- **Fails without it:** `PaletteGuardTests.TheErrorTextColour_ReachesAaOnTheDefaultBackgroundAndSurface_InBothModes` pins the values; the E2E `AccessibilityAdminMatrixTests` case for the media page and `AccessibilityDialogMatrixTests.ATextVariantErrorButtonInADialog_HasAtLeast45To1Contrast` are the ones that notice the rendered colour.
- **Upgrade check:** remove the block, run the media page matrix case; keep it while MudBlazor's default stays below 4.5:1. **Re-checked on 11.0 RC 2 (16a):** still needed: MudBlazor 9.11 unchanged; the default error red is set by MudBlazor's palette, not by the `Math.Round`-dependent shades. `PaletteGuardTests` and the E2E accessibility matrices pass on 11.0 RC 2.

### W-100: the standard resilience handler's retry strategy rejects zero retries

- **Library:** `AddStandardResilienceHandler` (Microsoft.Extensions.Http.Resilience over Polly) validates `Retry.MaxRetryAttempts` to be 1 or more; setting 0 fails the first `CreateClient` with an `OptionsValidationException` ("must be between 1 and 2147483647").
- **Our code:** `SocialHttp.AddSocialClients` (the `SocialMastodon`, `SocialBluesky` and `SocialAvatars` clients, brief 12a) sets `MaxRetryAttempts = 1` and a `ShouldHandle` that is always false, so the strategy is in the pipeline and never retries: the fetch schedule is the retry.
- **Fails without it:** `SocialClientsTests.AFailingAnswer_IsNotRetried_TheScheduleRetries` and `ANetworkError_IsNotRetried` (the standard strategy would retry a 503 and a connection error three times).
- **Upgrade check:** if the strategy accepts 0 (or the handler gets a switch to turn retries off), use it and delete the always-false predicate. **Re-checked on 11.0 RC 2 (16a):** Microsoft.Extensions.Http.Resilience 10.11 is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-101: HEAD requests are answered as the GET they stand for

- **Library:** ASP.NET Core routing matches a request only against endpoints that list its method. `MapGet` endpoints and the Razor components router list GET alone, so `HEAD /`, `/robots.txt`, `/feed.xml` and every other page answered an error (404 on a local instance; issue #5 saw 405 on the live site) and link checkers and uptime monitors that use HEAD reported the site as broken.
- **Our code:** `HeadRequestMiddleware` (`Services/HeadRequestMiddleware.cs`, registered in `Program.cs` before routing): a HEAD runs the whole pipeline as a GET, so rate limiting, antiforgery, circuit admission and the output cache treat it exactly as they treat the GET (the cache stores the page the GET would have, never an empty one), and the response body is discarded.
- **Fails without it:** `HeadRequestTests` (status, Content-Type, CSP and `Cache-Control` equal to the GET's, no body, on `/`, a post, `/robots.txt`, `/feed.xml`, `/sitemap.xml`, `/health/live`; a HEAD before a GET leaves the GET the full page).
- **Upgrade check:** if routing or `MapGet` starts to answer HEAD for GET endpoints by itself, remove the middleware and run `HeadRequestTests`. **Re-checked on 11.0 RC 2 (16a):** still needed: with `HeadRequestMiddleware` removed, every `HeadRequestTests` case fails on 11.0 RC 2 (routing still matches GET endpoints only).

## Tier C: values tuned from measurements; re-measure on an upgrade

### W-052: Circuit retention (20 circuits, 60 s)

- **Library:** Blazor's defaults (100 disconnected circuits kept for 3 minutes) OOM-killed the pods under reload churn, because a reload never reconnects to its old circuit.
- **Our code:** `CircuitSettings` defaults to 20 circuits for 60 s (configurable); `Program.cs` applies them to `CircuitOptions`.
- **Fails without it:** `CircuitLimitTests` pins the defaults, not the memory behaviour; the real check is the reload-churn procedure in `k8s/README.md` ("Blazor circuits and memory").
- **Upgrade check:** re-measure if circuit memory or defaults change (the roughly 0.9 Mi per circuit figure is .NET 10 specific). **Re-checked on 11.0 RC 2 (16a):** re-checked on 11.0 RC 2, kept: `CircuitLimitTests` pin the defaults and pass; the ~0.9 Mi per circuit figure was not re-measured (needs the cluster's reload-churn procedure, `k8s/README.md`).

### W-053: Workstation GC for croft-web

- **Library:** Server GC's baseline is 167-189 Mi against 76-79 Mi for Workstation; the container GC cap is 75% of the memory limit and the OOM kills came from non-GC memory.
- **Our code:** `Croft.Web.csproj` sets `ServerGarbageCollection` to `false` (a site can choose Server GC with `DOTNET_gcServer=1`).
- **Fails without it:** `WorkstationGcTests` (reads `Croft.Web.runtimeconfig.json`).
- **Upgrade check:** re-measure on .NET 11 (GC and container-awareness changes). **Re-checked on 11.0 RC 2 (16a):** re-checked on 11.0 RC 2, kept: `WorkstationGcTests` passes (the runtimeconfig still has `System.GC.Server: false`); not re-measured on .NET 11 (needs a load test on the cluster).

### W-054: The connection-pool cap and auto-prepare

- **Library:** Npgsql's default pool of 100 per process broke Postgres at about 55 req/s with `53300 too many clients` (`k8s/README.md`, "Database connections").
- **Our code:** `BlogDbConnectionString` caps `Maximum Pool Size` at 20 and exposes `Max Auto Prepare` (default off), both as validated settings.
- **Fails without it:** `ConnectionPoolSettingsTests` (`MaxPoolSize=3`) and `ReadReplicaRoutingTests` pin the settings, not the load behaviour.
- **Upgrade check:** re-measure under load after an Npgsql upgrade. **Re-checked on 11.0 RC 2 (16a):** Npgsql 10.0.3 (the pool lives in the ADO driver) is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2; not re-measured under load.

### W-055: `ndots: 2`

- **Library:** the cluster default `ndots=5` made every fully-qualified lookup walk the search list, part of the 2026-10-14 DNS stalls.
- **Our code:** `k8s/web-deployment.yaml` and `k8s/api-deployment.yaml` set `dnsConfig` `ndots` to 2.
- **Fails without it:** `K8sBaseTests.BothDeployments_SetDnsConfigNdotsToTwo`.
- **Upgrade check:** Kubernetes or CoreDNS changes may make it redundant. **Re-checked on 11.0 RC 2 (16a):** Kubernetes and CoreDNS is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-056: `preStop` sleep and termination grace period

- **Library:** the ingress and endpoints drop a terminating pod asynchronously, so requests land on a dying pod without a delay.
- **Our code:** both deployments `preStop` with `sleep 10` and set `terminationGracePeriodSeconds: 40` (10 s sleep plus 30 s to finish in-flight work).
- **Fails without it:** `K8sBaseTests.AppContainers_SleepTenSecondsInPreStop` and the grace-period test.
- **Upgrade check:** sidecar and endpoint-slice behaviour changes matter. **Re-checked on 11.0 RC 2 (16a):** Kubernetes is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

## Tier D: browser CSS

### W-057: Prefixed `-webkit-line-clamp`

- **Library:** multi-line clamping uses the prefixed box model (`display: -webkit-box`, `-webkit-box-orient`).
- **Our code:** `PostCard.razor.css` (title, subtitle, summary) and `RelatedPosts.razor.css` (title).
- **Fails without it:** `CardCssContractTests` (pins `-webkit-line-clamp` 3 and 2).
- **Upgrade check:** the unprefixed `line-clamp` can replace it once supported everywhere. **Re-checked on 11.0 RC 2 (16a):** The browser is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-089: A hidden mirror measures where each source line of the Markdown box starts

- **Library:** a `textarea` gives no position for a source line once long lines wrap, so the Preview's scroll sync cannot compute it from `lineHeight`.
- **Our code:** `editor.js` `sourceLineTops`: one hidden div per source line with the textarea's box metrics, rebuilt when the text or the width changes.
- **Fails without it:** none for short lines (`EditorLayoutTests.ScrollingTheMarkdownBox_BringsThePreviewToTheBlockOfItsTopLine` uses unwrapped lines). Manual check: scroll a post with long wrapped paragraphs and see the Preview follow.
- **Upgrade check:** a browser or CSS upgrade that exposes line boxes of a textarea would replace the mirror. **Re-checked on 11.0 RC 2 (16a):** The browser is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

## Tier E: test infrastructure (no production effect; upgrade work for the suites)

### W-058: The hand-rolled blazorpack circuit client

- **Library:** depends on the Blazor Server wire protocol: the `StartCircuit` signature and arguments, the `<!--Blazor:{json}-->` component markers in prerendered HTML, and the MessagePack and VarInt framing.
- **Our code:** `Croft.Web.Tests/Fixtures/BlazorCircuitClient` opens a real circuit with no browser, so circuit metrics fire.
- **Fails without it:** `BlazorTelemetryTests`, `CircuitAdmissionTests`.
- **Upgrade check:** the highest breakage risk on a .NET 11 Blazor change (`StartCircuit` arguments, marker format). **Re-checked on 11.0 RC 2 (16a):** still needed: `BlazorTelemetryTests` and `CircuitAdmissionTests` pass on 11.0 RC 2, so the hand-rolled client still matches the .NET 11 `StartCircuit` arguments and marker format.

### W-059: The migration contract guard calls `Migration.Up` by reflection

- **Library:** `Migration.Up` is protected, and `IMigrationsModelDiffer` cannot attribute a change to one migration.
- **Our code:** `MigrationContractGuard` instantiates each migration and invokes `Up` by reflection against a `MigrationBuilder("Npgsql")`; only the reflection call is marked, the guard itself is the mandated expand/contract check.
- **Fails without it:** `MigrationContractGuardTests`.
- **Upgrade check:** fragile across EF Core majors if the `Migration` API changes. **Re-checked on 11.0 RC 2 (16a):** still needed: `MigrationContractGuardTests` pass on EF Core 11 RC 2 (also over the new `DotNet11Snapshot` migration).

### W-060: Test interceptors keyed on EF and Npgsql command shapes

- **Library:** EF `DbCommandInterceptor` event timing and the statements EF and Npgsql issue (`LOCK TABLE`, the history `INSERT` and read, a statement starting with `UPDATE`).
- **Our code:** `MidChainRendezvousInterceptor` (Web), `HeldCommandInterceptor` and `HeldResultInterceptor` (API) match on command text.
- **Fails without it:** `ConcurrentMigrationTests`, `ConcurrentWebMigrationTests`, `SharedSessionRefreshTests`, `ScheduledPublisherTests`.
- **Upgrade check:** test determinism depends on EF issuing those commands; re-run after an EF upgrade. **Re-checked on 11.0 RC 2 (16a):** still needed: `ConcurrentMigrationTests`, `ConcurrentWebMigrationTests`, `SharedSessionRefreshTests`, `ScheduledPublisherTests` pass on EF Core 11 RC 2.

### W-061: The HA tests' own cookie jar

- **Library:** .NET's `CookieContainer` refuses to send the OIDC handler's `secure` correlation cookies over plain http, whereas browsers except localhost.
- **Our code:** `HaTests.CookieJar` carries cookies by hand.
- **Fails without it:** `HaTests` (via `dev/run-ha-e2e.sh`) dies with "Correlation failed" with a normal jar.
- **Upgrade check:** a .NET change to `CookieContainer` localhost handling would remove the need. **Re-checked on 11.0 RC 2 (16a):** re-checked on 11.0 RC 2 only by reasoning, kept: `HaTests` were not run (`dev/run-ha-e2e.sh` runs Podman pods with bind mounts, outside what AGENTS.md "Containers" lets an agent run).

### W-062: The reference-counted shared Postgres container

- **Library:** xUnit v2 had no assembly-level fixture. xunit.v3 has one (`[assembly: AssemblyFixture]`), but it is created for every run of the assembly, also a filtered run that selects no database test (brief 16b checked it with a probe on 4.0.2: a run filtered to a class that does not take the fixture still constructed it); the Web suite's `Category!=Container` run on a machine without a container runtime and the API's opt-in image run would then start (or fail to start) a container they do not use.
- **Our code:** `SharedPostgresContainer` (API and Web test projects) starts the container for the first class and disposes it after the last; the image tag mirrors `compose.yaml`.
- **Fails without it:** every database test class.
- **Upgrade check:** switch to an assembly fixture once xunit creates one only for runs that select a test using it.

### W-064: E2E instance mapping from NUnit worker numbers

- **Library:** NUnit numbers parallel workers 1..N, so `w mod N` residues are distinct only because of that.
- **Our code:** `WebTest.WorkerNumber` parses NUnit's worker identity; `Properties/AssemblyInfo.cs` fixes the default worker count at 1 (also affected, no marker: `dev/run-e2e.sh`, which raises it to the instance count).
- **Fails without it:** `dev/run-e2e.sh` with 3 instances (two fixtures on one instance would race).
- **Upgrade check:** a change in NUnit's worker ids breaks the mapping. **Re-checked on 11.0 RC 2 (16a):** NUnit 4.6 is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-065: Polling with a synchronous XHR; `FillAsync` and newlines

- **Library:** Playwright's `waitForFunction` does not await async predicates (a pending promise counts as truthy); `FillAsync` fires one input event only for newline-free text (newlines burst per line and hang above about 22K characters).
- **Our code:** `AdminTests` and `CircuitAdmissionTests` (the wait until A's circuit slot is free) poll with a synchronous XHR; `LongPostEditorTests` fills newline-free text.
- **Fails without it:** `AdminTests` and `LongPostEditorTests`.
- **Upgrade check:** re-run both on a Playwright upgrade. **Re-checked on 11.0 RC 2 (16a):** Playwright 1.64 is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-067: The blocked-storage shadow getter

- **Library:** relies on Chromium letting an own throwing `localStorage` getter shadow the configurable accessor; not guaranteed in WebKit or Firefox.
- **Our code:** `DraftBackupTests.BlockStorageAccessAsync`.
- **Fails without it:** the blocked-storage cases of `DraftBackupTests`.
- **Upgrade check:** Chromium only; re-run on a Chromium upgrade. **Re-checked on 11.0 RC 2 (16a):** Chromium is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-068: Tap before drag in the outline tests

- **Library:** Chromium suppresses the synthetic click of a tap that directly follows a synthetic drag (back-to-back CDP touch sequences).
- **Our code:** `MobileOutlineTests` orders the tap first.
- **Fails without it:** `MobileOutlineTests`.
- **Upgrade check:** re-run after a Chromium upgrade. **Re-checked on 11.0 RC 2 (16a):** Chromium is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-069: Departure modelled as navigation to `about:blank`

- **Library:** Playwright's context close runs no `pagehide` handlers, so `/_blazor/disconnect` is not sent and a retained circuit keeps refusing.
- **Our code:** the E2E `CircuitAdmissionTests` navigates the leaving page to `about:blank`.
- **Fails without it:** E2E `CircuitAdmissionTests`.
- **Upgrade check:** re-run after a Playwright upgrade. **Re-checked on 11.0 RC 2 (16a):** Playwright is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-070: The CSP probe reads the element, not `sample`

- **Library:** Chrome fills `securitypolicyviolation.sample` only with `'report-sample'`, which the policy must not grow.
- **Our code:** `CspViolationProbe` reads the offending element's text or attribute and captures Chrome's "Refused to ..." console lines.
- **Fails without it:** E2E `CspTests`.
- **Upgrade check:** re-run after a Chromium upgrade. **Re-checked on 11.0 RC 2 (16a):** Chromium is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-071: Chromium tolerances and the JavaScript-disabled limits

- **Library:** Chromium rounds the "0" advance to whole pixels (hence 2ch tolerances); with `JavaScriptEnabled=false` Playwright still allows locator queries and screenshots but not `EvaluateAsync`; the clipboard read follows a circuit round trip.
- **Our code:** `ReadingMeasureTests` (tolerance), `ThemeTests` (no `EvaluateAsync` with JavaScript off), `AdminTests.ClipboardReadEqualsAsync` (waits on the outcome). Only Chromium is installed, so the Safari and WebKit CSS in this register is not exercised by the suite.
- **Fails without it:** those classes.
- **Upgrade check:** re-run them after a Playwright or Chromium upgrade. **Re-checked on 11.0 RC 2 (16a):** Playwright and Chromium is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-072: The screenshot gate's Chromium flags

- **Library:** pixel determinism needs `--disable-gpu`, `--disable-lcd-text`, `--disable-skia-runtime-opts` and `--disable-partial-raster`.
- **Our code:** `dev/screenshot-gate/capture/CaptureTests.cs`.
- **Fails without it:** none: manual check (the gate's compare step reports diffs).
- **Upgrade check:** re-baseline on any Playwright or Chromium upgrade. **Re-checked on 11.0 RC 2 (16a):** Playwright and Chromium is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2; the gate was not run.

### W-073: MudBlazor picker interaction in E2E

- **Library:** the clock is a mask of pointer-event wedges, and the popover is placed on a `requestAnimationFrame` after its open class lands, so a bounding box read too early is the unplaced -9999px one.
- **Our code:** `SchedulingTests` and `PageWritingTests.PickUtcTimeAsync` click element-relative positions on the clock mask and wait for the picker to detach.
- **Fails without it:** `SchedulingTests`, `PageWritingTests`.
- **Upgrade check:** class names such as `.mud-picker-time-clock-mask` and `.mud-time-picker-minute` change with MudBlazor. **Re-checked on 11.0 RC 2 (16a):** MudBlazor 9.11 is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-074: `MudPopoverProvider` and focus in bUnit autocomplete tests

- **Library:** MudBlazor renders dropdowns through `MudPopoverProvider`, and `MudAutocomplete` searches on focus.
- **Our code:** `PostEditorFormTests` add the provider to the tree and trigger `onfocus`.
- **Fails without it:** the tests themselves.
- **Upgrade check:** re-run after a MudBlazor upgrade. **Re-checked on 11.0 RC 2 (16a):** MudBlazor 9.11 is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-075: The loopback TCP bridge for client spans

- **Library:** `WebApplicationFactory` hosts have no real socket, so HttpClient's client span and `traceparent` injection (in the socket transport) cannot be exercised.
- **Our code:** `LoopbackApiBridge` relays requests from a loopback port to the in-memory handler.
- **Fails without it:** `TraceCompositionTests`.
- **Upgrade check:** re-run it after an ASP.NET Core test-host change. **Re-checked on 11.0 RC 2 (16a):** still needed: `TraceCompositionTests` pass with Microsoft.AspNetCore.Mvc.Testing 11.0.0-rc.2.

### W-076: bUnit `UploadFiles` on a worker thread

- **Library:** bUnit's `UploadFiles` blocks until the handler finishes.
- **Our code:** `MediaGridTests` dispatch it on a worker thread.
- **Fails without it:** `MediaGridTests`.
- **Upgrade check:** re-run after a bUnit upgrade. **Re-checked on 11.0 RC 2 (16a):** bUnit 2.12 is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-077: `BunitPersistentComponentState` for persisted state

- **Library:** bUnit has no prerender-to-interactive cycle, so `[PersistentState]` restoration must be set up by hand: the state must be registered before other services are resolved, and bUnit does not register the framework's `[PersistentState]` value provider (`AddSupplyValueFromPersistentComponentStateProvider`), without which a `[PersistentState]` property is neither persisted nor restored in a test.
- **Our code:** `BlogBunitContext` registers `BunitPersistentComponentState` early and the value provider after it. Since 09f only `MainLayout` uses `[PersistentState]`; `MainLayoutPersistentStateTests` exercise it (persist after a fetch, restore without one).
- **Fails without it:** `MainLayoutPersistentStateTests` (the persisted state is never found), not confirmed.
- **Upgrade check:** re-run after a bUnit upgrade. **Re-checked on 11.0 RC 2 (16a):** bUnit 2.12 is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-078: The CSP test filter waits for `OnStarting`

- **Library:** a startup filter's middleware runs before `CspMiddleware` creates the feature, so the addition must wait for the `OnStarting` callbacks.
- **Our code:** `CspHeaderTests.FrameSourceProbeFilter`.
- **Fails without it:** `CspHeaderTests`.
- **Upgrade check:** relies on callback ordering; re-run after an ASP.NET Core upgrade. **Re-checked on 11.0 RC 2 (16a):** still needed: `CspHeaderTests` pass on 11.0 RC 2 (callback ordering unchanged).

### W-079: The cache eviction test waits on the count; `EvictionReason.Replaced`

- **Library:** `IMemoryCache` eviction bookkeeping lands on the thread pool, and `EvictionReason.Replaced` fires after the newer `Set` already owns the key.
- **Our code:** `PublicReadCacheTests` and `PublicReadCacheKeyTests` wait on the concrete count; `MemoryPublicReadCache` ignores the callback when the cache holds a different value for the key, so the newer entry's tracking survives.
- **Fails without it:** `PublicReadCacheKeyTests` (an expired not-found replaced by a refill must stay invalidatable).
- **Upgrade check:** re-run after a `Microsoft.Extensions.Caching` upgrade. **Re-checked on 11.0 RC 2 (16a):** still needed: `PublicReadCacheKeyTests` pass on 11.0 RC 2 (Microsoft.Extensions.Caching from the 11 shared framework).

### W-080: Telemetry tests wait on the exported item

- **Library:** the server-side span ends just after the client sees the response, and framework metrics land on a collection timer.
- **Our code:** `TelemetryCapture.WaitForSpanAsync` and `WaitForMetricAsync` wait for the matching exported item.
- **Fails without it:** `ApiTelemetryTests`, `WebTelemetryTests`.
- **Upgrade check:** re-run after an OpenTelemetry upgrade. **Re-checked on 11.0 RC 2 (16a):** still needed: `ApiTelemetryTests`, `WebTelemetryTests` pass on 11.0 RC 2 (OpenTelemetry 1.20.1 unchanged).

### W-103: Image-building tests pass BUILDPLATFORM and TARGETARCH to Testcontainers

- **Library:** Testcontainers' `ImageFromDockerfileBuilder` (1) pulls the Dockerfile's base images itself before building, substituting only the build arguments passed to it: for the digest-pinned `FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:…` (Renovate's pins) it asks the engine for OS "$BUILDPLATFORM" ("no image found in manifest list for architecture "amd64", variant "", OS "$BUILDPLATFORM"", on Podman); and (2) builds through the Docker Engine API's legacy builder, which, unlike BuildKit and buildah, predefines neither BUILDPLATFORM nor TARGETARCH, so `--platform=$BUILDPLATFORM` expands to nothing ("failed to parse platform : "" is an invalid OS component of """, on Docker) and `publish -a "$TARGETARCH"` would get an empty architecture.
- **Our code:** `Fixtures/DockerfileBuildPlatform` (`WithHostBuildPlatform()`: BUILDPLATFORM `linux/<arch>` and TARGETARCH `<arch>`, the host's amd64 or arm64), called by `MigrateContainerTests` and `ApiImageSkiaSharpContainerTests`; both Dockerfiles declare `ARG BUILDPLATFORM` and `ARG TARGETARCH` before the first `FROM` (global args: BuildKit keeps its automatic values, the legacy builder takes the build arguments).
- **Fails without it:** `MigrateContainerTests` (both) and `ApiImageSkiaSharpContainerTests` (`Category=Container`), on Podman (pre-pull) and on Docker (legacy builder, the GitHub runner's `image-tests` job).
- **Upgrade check:** on a Testcontainers upgrade (BuildKit support, or a pre-pull that resolves build args), remove the `WithHostBuildPlatform()` calls and the global `ARG` lines and run `Category=Container` on Podman and in CI; if they pass, retire W-103. **Re-checked on 11.0 RC 2 (16a):** still needed: Testcontainers 4.16 unchanged; `Category=Container` passes on Podman with the `sdk:11.0`/`aspnet:11.0` images (CI's Docker run is the PR's).

### W-104: Debug builds of the Web app leave out SDK 11's dotnet-watch browser tools

- **Library:** the .NET 11 SDK (`Microsoft.NET.Sdk.StaticWebAssets.DotNetWatch.targets`, RC 2) adds build-only static web assets to every Debug build of a Blazor Web app, among them a JS initializer (`Microsoft.NET.Sdk.BlazorWeb.DotNetWatch.lib.module.js`). A full page load then ends in a `<!--Blazor-Web-Initializers:...-->` comment that an enhanced-navigation response does not carry, and every page fetches `/_framework/dotnet-browser-tools/hot-reload-settings.json` (a 404 outside `dotnet watch`). Publish output never contains them (a Release publish has no `lib.module.js` at all), so production is unaffected; the suites, which run Debug builds, would see documents production never serves.
- **Our code:** the `CroftOmitDotNetWatchBrowserTools` target in `src/Croft.Web/Croft.Web.csproj` clears `DotNetWatchBrowserToolsAssetPrefix` before `ResolveProjectStaticWebAssets`, which skips the SDK's `_DefineDotNetWatchBrowserToolsAssetsCore` (its condition needs the prefix). `EnableHotReloadInRuntimeConfigDevFile` (the switch the SDK documents) is left alone, because it also turns off Hot Reload's runtime options. The cost: `dotnet watch` loses whatever these browser tools add for the Web app (not measured; the owner's hand testing does not use `dotnet watch`, as far as the repo shows: no script or doc runs it).
- **Fails without it:** `OutputCacheReadingPageTests.AnEnhancedNavigationRequest_GetsTheSameResponseAsAFullLoad` (all six reading routes: "differ on line ... `<!--Blazor-Web-Initializers:WwogICJfZnJh...`").
- **Upgrade check:** on each SDK update, delete the target and run that test; if the SDK stops emitting the initializer in Debug builds (or offers a switch for the browser tools alone), use that and retire W-104. The target relies on the SDK's private target and property names: if they change, the test fails again, which is the signal. **Re-checked on 11.0 RC 2 (16a):** added in 16a for SDK 11 RC 2 (item 2); its upgrade check applies from the next SDK on.

### W-105: URL waits after in-circuit navigations are `Expect(Page).ToHaveURLAsync`

- **Library:** Playwright .NET's `Page.WaitForURLAsync` checks the frame's URL on the calling thread and then subscribes to the "navigated" event; the connection delivers events on another thread (inferred from the probe below and the server-side source, which emits each "navigated" event once and never replays it; the .NET client source was not read), so a navigation landing between the two steps is never seen and the wait runs to its timeout although the URL is right. A same-document navigation (Blazor's `NavigateTo` inside the circuit is a `pushState`) is one a click's completion races with. Reproduced by a probe (a page `pushState`s after 0 to 3 ms, 2000 waits): 10 timeouts with `WaitForURLAsync`, 0 with `ToHaveURLAsync`.
- **Our code:** `AdminTests.ExpectUrlAsync` and `ExpectEditorUrlAsync`, used for Save draft, Publish now and the posts-list row actions.
- **Fails without it:** none deterministically (a rare timeout, about 1 in 15 full runs before the change); manual check: the probe above.
- **Upgrade check:** on a Playwright upgrade, re-run the probe against `WaitForURLAsync`; if it no longer times out, the helpers can go back to it (or stay: they are correct either way).

## Tier F: deployment and dev tooling

### W-081: Network-policy labels for K3s and Traefik

- **Library:** assumes the K3s and Traefik chart labels (`kube-system`, `app.kubernetes.io/name=traefik`), that the CNI lets node-local kubelet probes through, and that a single `from` list item means AND (two items would mean OR).
- **Our code:** the assumption comment in `k8s/components/network-policies/kustomization.yaml` (marked; the rest of the file wires the component) and the `from` items in `web-ingress.yaml` and `api-ingress.yaml`.
- **Fails without it:** `K8sPlacementAndPolicyTests` pin the YAML shape; a real cluster check is needed after a CNI or ingress change.
- **Upgrade check:** verify on a real cluster after changing CNI, ingress or K3s. **Re-checked on 11.0 RC 2 (16a):** K3s, Traefik and the CNI is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-082: `USER $APP_UID` and port 8080 in the Dockerfiles

- **Library:** the .NET 8+ images listen on 8080 by default and run as a user named `app`, and `runAsNonRoot` can only verify a numeric user (`APP_UID=1654`, matched by `fsGroup`).
- **Our code:** `src/Croft.Api/Dockerfile` and `src/Croft.Web/Dockerfile`.
- **Fails without it:** `K8sBaseTests`.
- **Upgrade check:** .NET 11 image changes (UID or port). **Re-checked on 11.0 RC 2 (16a):** still needed: the `aspnet:11.0` image (RC 2, `DOTNET_VERSION=11.0.0-rc.2.26504.105`) still sets `APP_UID=1654` and `ASPNETCORE_HTTP_PORTS=8080`; `K8sBaseTests` pass.

### W-083: The mock OIDC proxy rewrites only `token_endpoint`

- **Library:** the mock IdP derives the issuer from the forwarded Host and has no observability of its own.
- **Our code:** `dev/ha-oidc-proxy.cs` forwards everything untouched except the discovery document's `token_endpoint`.
- **Fails without it:** `HaTests` (token counting).
- **Upgrade check:** after a pin bump of `mock-oauth2-server`, re-run `dev/run-ha-e2e.sh`. **Re-checked on 11.0 RC 2 (16a):** mock-oauth2-server is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2; `HaTests` not run (see W-061).

### W-084: Podman `--remote`, `:Z` and `0.0.0.0` in `dev/ha-local.sh`

- **Library:** the sandbox's single-uid user namespace cannot run local rootless Podman; SELinux needs the `:Z` relabel on the mounted config; `host.containers.internal` forwards to a non-loopback host address.
- **Our code:** `PODMAN=(podman --remote)`, the `:ro,Z` Traefik mount and the apps bound to `0.0.0.0` in `dev/ha-local.sh`.
- **Fails without it:** none: manual check (`dev/run-ha-e2e.sh` fails at start-up).
- **Upgrade check:** re-verify on a Podman or sandbox change. **Re-checked on 11.0 RC 2 (16a):** Podman and the sandbox is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-088: ReadyToRun publish in the Dockerfiles, pinned to the Pi 4's instruction sets

- **Library:** the SDK's crossgen2 decides which arm64 instruction sets the precompiled code may assume. .NET 10 targeted `armv8.0-a` for `linux-arm64`, which the Raspberry Pi 4 (Cortex-A72) supports; .NET 11's crossgen2 also requires LSE ("Atomics"), which the Pi 4 lacks (`/proc/cpuinfo`: `fp asimd evtstrm crc32 cpuid`), so the runtime would reject the image's ReadyToRun code there and JIT-compile everything again, silently. Without ReadyToRun code a new Web pod spent about five minutes at 1.2 to 1.5 cores while the tiered JIT recompiled the hot paths (load test 2026-10-16, results README finding 3).
- **Our code:** `-p:PublishReadyToRun=true` on the `dotnet publish` line of `src/Croft.Api/Dockerfile` and `src/Croft.Web/Dockerfile`; since 11.0 RC 2 also `PublishReadyToRunCrossgen2ExtraArgs` = `--instruction-set:neon,crc,-lse` for `linux-arm64` in `Directory.Build.props` (the Dockerfiles copy it). The `-lse` matters: `--instruction-set:neon,crc` alone (or `armv8-a`) still lets crossgen2's optimistic light-up require Atomics. The framework's own precompiled code in the `aspnet:11.0` base image is built for the new target and is not covered by this: on the Pi 4 it is JIT-compiled at start (`writing-dotnet11-code/whats-new.md`), so start-up to ready on those nodes is expected to be slower than on .NET 10 (not measured here).
- **Fails without it:** `ReadyToRunPublishTests` (`--filter "Category=ReadyToRun"`; it publishes with the Dockerfile's own line for linux-arm64 and reads the ReadyToRun header and machine type of the published assemblies) for ReadyToRun itself. The instruction-set pin has no automated check; manual check (16a, 11.0 RC 2): publish `src/Croft.Web` with the Dockerfile's flags for `-a arm64` plus `-p:PublishReadyToRunCrossgen2ExtraArgs=--map` and read `obj/Release/net11.0/linux-arm64/R2R/Croft.Web.map`: the eager import is `ReadyToRunInstructionSets_ArmBase+AdvSimd+Atomics` without the pin and `ReadyToRunInstructionSets_ArmBase+AdvSimd+Crc32` with it (Croft.Shared and the package assemblies, e.g. MudBlazor, the same).
- **Upgrade check:** re-checked on 11.0 RC 2: still needed while the cluster has Pi 4 nodes. On each SDK update repeat the manual check above (crossgen2's option names: `crossgen2 --help`, "--instruction-set"); drop the pin once no node lacks LSE (the planned x86 mini PCs and any Armv8.1+ board have it).

### W-096: sign-out answers a 200 page with a meta refresh, not a redirect

- **Library:** Chromium applies the CSP `form-action` directive to the redirect hop of a form submission: a `POST /logout/complete` answered with a 302 to the IdP's end-session endpoint is blocked under `form-action 'self'` ("Sending form data to ... violates the following Content Security Policy directive: form-action 'self'"), and the page stays where it was. The policy is not widened (brief 13b); W-047 (retired) was the earlier GET-chain way round the same check.
- **Our code:** `SignOutInterstitial.RenderContinue` and the `POST /logout/complete` endpoint in `Program.cs`: the store entry is removed, the cookie cleared, and the redirect the OIDC handler wrote is replaced by a 200 page with `<meta http-equiv="refresh">` and a visible link to the same URL.
- **Fails without it:** E2E `SidebarAuthLinksTests` (the Chromium sign-out round trip) and `DraftBackupTests.Logout_ClearsEveryDraftBackupKey`; unit `SignOutInterstitialTests`.
- **Upgrade check:** a browser that stops applying `form-action` to redirects lets the endpoint go back to a 302; re-test in Chromium on every Playwright upgrade. **Re-checked on 11.0 RC 2 (16a):** The browser is not part of this upgrade (unchanged); the suites of brief 16a pass with it on 11.0 RC 2.

### W-097: Kestrel reports wildcard listening addresses that `Uri` cannot parse

- **Library:** ASP.NET Core's `IServerAddressesFeature` lists the addresses the server was told to bind, as written: `http://+:8080`, `http://*:8080`, `http://[::]:8080`, `http://0.0.0.0:8080`. The first two are not valid URIs, and none of them is an address a client can connect to.
- **Our code:** `WarmupRunner.LoopbackOf` in `Croft.Shared/Warmup/WarmupRunner.cs` splits the address by hand and rebuilds it as `scheme://127.0.0.1:port/` for the warm-up's loopback requests.
- **Fails without it:** unit `WarmupLoopbackAddressTests` (every address form above, and `http://localhost:5000`).
- **Upgrade check:** if `IServerAddressesFeature` starts reporting bound, parseable endpoints, the hand parsing can go. **Re-checked on 11.0 RC 2 (16a):** still needed: on 11.0 RC 2 (as on 10) Kestrel reports `http://+:N` and `http://*:N` as `http://[::]:N` and keeps `http://0.0.0.0:N` (probe with `UseUrls`): parseable, but wildcard listen addresses, not endpoints a client can call, so the loopback rewrite stays; `WarmupLoopbackAddressTests` pass.

## Considered and left out

Codes are from the 2026-10-16 sweep. Each is something a later sweep may find again; none
compensates for a dependency's behaviour.

- B18: the `HttpClient` handler scope rule is a design rule documented in AGENTS.md, not code that works around anything.
- B22: `AddServerHeader = false` is a hardening choice, not a compensation.
- B24: the optional `IProblemDetailsService` fallback is defensive coding.
- B25: `MapInboundClaims = false` is a deliberate identity-handling choice, kept even if the defaults change.
- B29: the backchannel-logout validator reads the raw payload because the rules need exact JSON shapes, not because a library misbehaves.
- B30: the ordinal `StartsWith` in `LocalUrl` is a correctness rule, not a workaround.
- E7: `ExecuteUpdateAsync` for the counter is the normal way to increment atomically.
- E9: the three-argument `ILike` with an escape character is the documented API used correctly.
- E11: sorting tag names in memory is a deliberate ordering rule.
- E13: normalising to UTC is Npgsql's documented requirement for `timestamptz`.
- E18: the test fixture's pool clear and `DROP DATABASE ... WITH (FORCE)` are documented Npgsql and Postgres usage.
- K1: sticky sessions are documented, not coded (the site's ingress sets them).
- K4: the media volume needs `ReadWriteMany` for a rolling update; a design requirement.
- K5: ordered env entries follow Kubernetes' documented `$(VAR)` expansion.
- K6: the probe design is an operational choice.
- K11: SIGTERM first in `dev/ha-kill.sh` is graceful-shutdown design.
- K13: the SDK pin is a repository rule, not a compensation.
- K14: the Postgres version and CNPG notes are a dependency list, not workarounds.
- S6: the banner fallback layering is a layout choice.
- S7: container queries on `main` are a layout choice (the containing-block effect is documented in `editor.js`).
- S8: CSSOM position writes follow from the CSP, which Croft keeps.
- S9: `@onmousedown:preventDefault` is ordinary event handling.
- S10: the guarded `localStorage` access and the sign-out interstitial are defensive design.
- S11: the inline theme script allowed by hash is a CSP design.
- S13: the font loading and fluid typography are design.
- T11: `AddMemoryCache` `SizeLimit` is the documented way to bound the cache.
- L2: the axe gate is a test policy; the MudBlazor causes it finds are W-037, W-038 and W-039.

## Retired

IDs that no longer have an entry. They are never reused. These are plain lines, not headings, so
`WorkaroundRegisterTests` checks that none of them has a `###` entry or a marker.

- W-066: the URL-wait hang capture (`AdminTests.WaitForUrlCapturingAsync` and its in-flight request tracking, a diagnostic for a rare 15 s hang in `WaitForURLAsync`) was retired in brief 17a: the hang was Playwright .NET's `WaitForURLAsync` missing a navigation between its URL check and its event subscription (see W-105), and the three waits are `Expect(Page).ToHaveURLAsync` now.
- W-005: the MudBlazor dark-palette append on static pages (the palette variables stayed light there) was removed when brief 09i took MudBlazor off the reading pages; `Theme/MudDarkPaletteCss.cs` is deleted and `Branding.ThemeHash` is retired.
- W-047: the OIDC authority in `form-action` (`OidcFormActionCspContributor`) was removed in brief 10c at the owner's decision: sign-in is a 302 challenge and sign-out a GET link chain (`/logout` to `/logout/interstitial` to `/logout/complete` to a redirect), so no form of ours posts to the IdP and Chrome's form-redirect check never applied (brief 13b made `/logout/complete` a POST, so the check applies again: see W-096); `SidebarAuthLinksTests` and `DraftBackupTests.Logout_ClearsEveryDraftBackupKey` pass in Chromium and Firefox with `form-action 'self'`.
- W-063: `BlogBunitContext`'s `IAsyncLifetime` (xUnit 2 disposed a test class through `IDisposable`, the bUnit context's synchronous dispose, while MudBlazor's dialog services are `IAsyncDisposable` only) was removed in brief 16b: xunit.v3 disposes a test class through `IAsyncDisposable` when it implements it, and `BunitContext` does, so the bUnit dialog tests pass with the context's own `DisposeAsync`.
- W-013: forwarded headers switched on by `ASPNETCORE_FORWARDEDHEADERS_ENABLED`, which clears the trusted proxies and networks and so trusted every peer, was removed in brief 13b (review 2026-10-19, finding 8): the Web calls `UseForwardedHeaders` itself, once, first in the pipeline, with `ForwardedHeaders:KnownNetworks` (the k8s base sets the k3s pod network) and `ForwardLimit` 1, and the API handles no forwarded headers; `ForwardedHeadersOnceTests` pins the single pass and the untrusted-peer case.
- W-034: the raw ADO command for `pg_try_advisory_xact_lock` in `BackgroundJobLock` was retired in brief 16a (11.0 RC 2): EF's scalar `SqlQuery<bool>` with the column aliased `"Value"` composes the bare function call fine (`SELECT pg_try_advisory_xact_lock({jobId}) AS "Value"` on the tick's own transaction); `BackgroundJobLockTests`, `ScheduledPublisherTests` and the full API and Web suites pass with it. The probe is now an EF command, visible to command interceptors like every other.
- W-049: the second, explicit `CircuitSettings.Read` call in the Web's `Program.cs` was retired in brief 16a (11.0 RC 2), as its upgrade check says: `CircuitLimitTests` (an invalid `Croft:Web:Circuits:*` value fails start-up) passes without it, because ASP.NET Core 11 still materialises `CircuitOptions` during host start and the options callback's own `CircuitSettings.Read` throws there.
