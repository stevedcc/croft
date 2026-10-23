# Brief 03d: go-live fixes (login/logout links, logout at Authentik, page title, date culture)

Read AGENTS.md first and follow it, including "Finishing a task". Found on crawford.dev on
2026-10-08, the first day live. One commit per item, TDD, break-it-once, E2E twice in a row at the
end, then the final whole-branch review.

## 1. "Log in" and "Log out" links reach the server

Clicking the sidebar's "Log in" or "Log out" shows the not-found page ("This page has wandered
off"): Blazor's router handles `/login` and `/logout` as page navigations, but they are server
endpoints (`MapGet` in `src/Blog.Web/Program.cs`). Typing the URL works.

- Every link to `/login` or `/logout` (the sidebar in `Components/Layout/SidebarNav.razor`, and any
  other: search `src/Blog.Web` for both paths, including `BlogApiClient`'s login redirect) must
  cause a full page load. Use Blazor's documented opt-out for that, consistently in all places.
- E2E: a test that logs in and out **by clicking the sidebar links** (not by opening `/login`), and
  asserts the mock IdP's login page appears, then the admin section after login, then the
  logged-out sidebar after logout. The existing login helper may keep opening `/login` directly.
- Break-it-once: remove the opt-out from the "Log in" link; the new test must fail.

## 2. Logout also ends the Authentik session

`/logout` signs out of the cookie and then redirects to Authentik's end-session endpoint with only
`post_logout_redirect_uri` and `state`. Authentik answers 400 Bad Request: it needs `id_token_hint`
to accept a post-logout redirect. The ID token is never kept (`SaveTokens = false`; the token
store holds access and refresh tokens only).

- At login (`TokenSessionEvents`), keep the ID token in the server-side token store with the other
  tokens. Never in the cookie, never sent to the browser.
- On sign-out, set `IdTokenHint` on the end-session request
  (`OnRedirectToIdentityProviderForSignOut`) from the store, before the session's tokens are
  removed. If no ID token is stored (session expired, pod restarted), sign out locally and redirect
  to `/` instead of sending Authentik a request it will reject.
- Tests: unit tests that the ID token is stored at login and set as the hint on sign-out, and that
  a missing ID token leads to a local sign-out and `/`. E2E against the mock IdP: logout ends at
  the blog's home page logged out.

## 3. Page title

The home page's `<title>` is "Blog". Every page's title uses the configured `Blog:Name`
("crawford.dev" in production): the home page is just the name; other pages are
"<page title> · <name>" (a post: its title). Test the home page and a post page.

## 4. One date culture: en-GB, never US

> **Owner amendment, 2026-10-08:** numeric dates are ISO 8601 (`2026-10-08`): en-GB culture with the
> short date pattern `yyyy-MM-dd`. The text below predates it and says `08/10/2026`.

The post editor's "Publish date (UTC)" picker shows `10/08/2026` for 8 October (the containers
have no culture set, so .NET uses the invariant culture, which is US order). The analytics chart
labels days with `"M/d"`.

- Set `en-GB` as the default culture (`CultureInfo.DefaultThreadCurrentCulture` and
  `DefaultThreadCurrentUICulture`) at startup in both apps, so MudBlazor's date and time pickers
  and any culture-dependent formatting are day-first (`08/10/2026`) and 24-hour.
- The chart's day labels: `"d MMM"` with en-GB ("8 Oct").
- Leave the existing explicit `"d MMMM yyyy"` formats alone.
- Make sure the images have the culture data: the `aspnet:10.0` base image includes ICU; check the
  apps don't run with invariant globalization.
- Tests: bUnit or E2E that the editor's publish-date picker shows `08/10/2026` for 8 October 2026,
  and that the chart's labels are day-first.

## Out of scope

Back-channel logout, showing times in the owner's time zone instead of UTC, anything else.
