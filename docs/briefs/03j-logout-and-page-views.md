# Brief 03j: back-channel logout, and page views without error logs

Read AGENTS.md first and follow it, including "Finishing a task". One commit per item, TDD,
break-it-once, E2E twice in a row at the end, then the final whole-branch review.

## Item 1: back-channel logout

Logging out of Authentik (or an admin ending the session there) must end the blog session too.
Authentik 2025.8+ supports OIDC Back-Channel Logout 1.0: it POSTs a signed `logout_token` to the
provider's configured logout URI. Today the blog only learns of a logout when its own `/logout` is
used.

- `POST /backchannel-logout` on Blog.Web (form field `logout_token`, anonymous, no antiforgery,
  outside the Blazor router), per the spec
  (https://openid.net/specs/openid-connect-backchannel-1_0.html, section 2.6):
  - validate the token's signature against the IdP's signing keys (the same OIDC configuration the
    login uses), `iss`, `aud` (the client id), `iat` freshness, the
    `events` claim containing `http://schemas.openid.net/event/backchannel-logout`, the presence of
    `sid` or `sub`, and the absence of `nonce`;
  - remove every blog session for that `sid` (or, with only `sub`, every session of that user) from
    the token store; `TokenSessionEvents.OnValidatePrincipal` then rejects the cookie on the next
    request, and an open circuit hits `SessionExpiredException` on its next API call;
  - answer 200 with `Cache-Control: no-store`; 400 for an invalid token. Never log the token.
- To map `sid` to blog sessions, keep the IdP's `sid` claim (from the ID token at login) in the
  token store entry, and look sessions up by it. The store stays server-side.
- Tests: unit tests for each validation rule (a test signing key; one test per rejected case) and
  for session removal by `sid` and by `sub`. E2E against the mock IdP if it can sign a logout
  token for you (check what `navikt/mock-oauth2-server` offers, from its documentation in the
  image or its responses; don't guess); otherwise say so and keep the unit tests as the gate.
- README "Deployment": the owner sets the provider's Logout URI to
  `https://crawford.dev/backchannel-logout` and Logout Method to Back-channel. Don't change any
  manifests for it.
- Break-it-once: skip the signature check; a test must fail.

## Item 2: page views without an error per duplicate

`AnalyticsService.RecordPageViewAsync` inserts a page view and catches the unique-index violation
(same post, visitor and day). The dedupe works, but EF Core logs every violation as a failed
database command at Error level before the catch runs, so a reader refreshing a post fills the
API's log with errors.

- Insert with PostgreSQL's `INSERT ... ON CONFLICT DO NOTHING` on the unique index (`PostId`, `IpHash`, `Day`; take the table and column names from the model, not by hand) (raw SQL
  through EF's parameterised `ExecuteSqlAsync`, or an equivalent that never raises), and drop the
  catch. Keep the "only published posts whose publish time has passed" check.
- Tests: two views from the same visitor on the same day count once, and nothing is logged above
  Information (capture the logs in the API test host). Concurrent requests for the same visitor
  (e.g. 10 in parallel) count once and all return success.
- Break-it-once: go back to the plain insert; the logging test must fail.

## Out of scope

Front-channel logout, ending sessions on other devices from the blog, analytics changes beyond
the insert. Anything else.
