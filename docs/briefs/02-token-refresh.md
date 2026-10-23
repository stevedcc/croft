# Brief 02: access-token refresh in the Web app

Read AGENTS.md first and follow it, including "Finishing a task". This is a design-heavy task:
implement the design below. If it doesn't fit, stop and explain; don't substitute a simpler one.

## Problem

The Web app stores the access token as a claim inside the auth cookie at login, and never refreshes
it. The mock IdP issues 1-hour tokens; production Authentik defaults to about 5 minutes. After expiry,
every API call made by an admin or editor fails with 401 while the cookie says they're still logged in.

## Design (decided)

Server-side token store with refresh, in the Web app:

1. **Scopes:** add `offline_access` to the OIDC scopes (Authentik only issues refresh tokens with it).
2. **Session key:** in `OnTokenValidated`, generate a random session id and add it as a claim
   `blog_sid`. **Remove** the `access_token` claim from the cookie entirely.
3. **Store:** an `ITokenStore` interface (get/set/remove by session id) with an in-memory
   implementation registered as a singleton. Entries hold the access token, refresh token, and
   access-token expiry (UTC). Save the tokens from the token endpoint response in `OnTokenValidated`.
4. **Token service:** `IUserAccessTokenService.GetAccessTokenAsync(ClaimsPrincipal user, CancellationToken)`:
   - no `blog_sid` or no store entry → return null;
   - token valid for more than 60 seconds → return it;
   - otherwise refresh: POST `grant_type=refresh_token` to the token endpoint taken from the OIDC
     configuration (`OpenIdConnectOptions.ConfigurationManager`), with client id and secret. Store
     the new access token, the new refresh token if one is returned (rotation; keep the old one if
     not), and the new expiry from `expires_in`;
   - concurrent calls for the same session must trigger only ONE refresh (per-session `SemaphoreSlim`);
   - refresh fails (e.g. 400 `invalid_grant`) → remove the entry and return null;
   - use `TimeProvider` (injected) for all time checks.
5. **Callers:**
   - `BlogApiClient` (scoped, inside the circuit) gets the user from `AuthenticationStateProvider` and
     calls the token service per request. Delete `CircuitAccessTokenProvider` and `TokenInitializer`.
   - The `/api/media/{id}` proxy calls the token service with `HttpContext.User`.
6. **Expired sessions:** in the cookie handler's `OnValidatePrincipal`, reject the principal and sign
   out when its `blog_sid` has no store entry (e.g. after a pod restart). The user is then simply
   logged out instead of being "logged in" with no token.
7. **Logout:** `/logout` removes the store entry before signing out.
8. Update AGENTS.md's Conventions section to describe the new token flow (it currently describes
   `CircuitAccessTokenProvider`).

## Tests and evidence

- Unit tests for the token service, using a fake `TimeProvider` and a fake token-endpoint
  `HttpMessageHandler`:
  - a fresh token is returned with no HTTP call;
  - a near-expiry token triggers a refresh; the new tokens are stored; a rotated refresh token
    replaces the old one;
  - 10 concurrent calls near expiry result in exactly one refresh request;
  - `invalid_grant` → null, and the entry is removed.
- Evidence against the real mock IdP: show (e.g. with curl) that its token endpoint accepts
  `grant_type=refresh_token` for a token issued to `blog-web`.
- E2E: the existing suites still pass. If mock-oauth2-server can be configured with a short token
  expiry WITHOUT overriding the claims from `dev/login.html`, add an E2E test that stays on an
  admin page past expiry and then saves a post successfully. If it can't, say so in the completion
  report; don't fake it with sleeps.
- Mutation check (rule 7): make the refresh path return the old expired token. Show the refresh unit
  tests fail, then revert.

Note: in Development the API doesn't validate the audience. The mock IdP's refreshed access tokens
carry `aud: default` rather than `blog-web`; that's a mock quirk, not something to fix.

## Out of scope
Persisting the token store across restarts, multi-replica support, Kubernetes changes.
