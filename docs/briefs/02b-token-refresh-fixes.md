# Brief 02b: token refresh fixes

Read AGENTS.md first and follow it, including "How changes flow" (start with
`git pull --ff-only origin blog-framework`) and "Finishing a task". The items are small and independent:
use `subagent-driven-development`, one commit per item.

These correct the design in brief 02, whose wording ("refresh fails → remove the entry") was too broad.
That wording was the orchestrator's mistake, not the implementer's.

## 1. Only real rejections end the session

`UserAccessTokenService.RefreshAsync` currently removes the store entry on **any** failure, so a brief
IdP outage logs the user out.

- Remove the entry (and return null) **only** when the token endpoint responds **400 or 401**
  (`invalid_grant`, `invalid_client`, …), or when there is no refresh token.
- For everything else (`HttpRequestException`, timeout, 5xx, 429, discovery/`GetConfigurationAsync`
  failure, an unparseable 2xx body): **keep the entry unchanged**. Return the current access token if it
  hasn't actually expired yet (`ExpiresAt > now`), otherwise return null. Log a warning with the reason
  (never log token values).
- Tests (fake `TimeProvider` + fake handler): 400 → entry removed; 401 → removed; 503 → kept, and the
  still-valid token is returned; 503 with an already-expired token → null but the entry is kept, and a
  later successful refresh works; network exception → kept; discovery failure → kept.

## 2. Missing or zero `expires_in`

Both `TokenSessionEvents.SaveTokens` and the refresh path treat a missing `expires_in` as 0, which marks
the token stale immediately and triggers a refresh on every request.

- When `expires_in` is missing, zero or invalid, use the access token's own `exp` claim if it is a
  readable JWT (`JsonWebTokenHandler.ReadJsonWebToken`, no validation needed for this purpose);
  otherwise default to 300 seconds.
- Put this in ONE helper used by both places (no duplicated logic).
- Tests: `expires_in` present → used; missing with a JWT `exp` → `exp` used; missing with an opaque token
  → now + 300 s.

## 3. Refresh housekeeping

- Dispose the `HttpResponseMessage` in the refresh path.
- Run the shared refresh under its own timeout (10 s, via a linked `CancellationTokenSource` that is
  NOT tied to the first caller's token), so one caller disconnecting can't abort the refresh other
  callers are waiting for and waste a rotated refresh token. Callers may still stop *waiting* on their
  own token.
- Test: the first caller cancels mid-refresh; a second concurrent caller still receives the refreshed
  token, and exactly one token request was made.

## 4. Expired session inside a live page

Inside an interactive circuit there's no new HTTP request, so `OnValidatePrincipal` never runs, and an
expired session surfaces as an exception from `EnsureSuccessStatusCode`.

- In `BlogApiClient`, when a response is **401** and the current user **is authenticated**, navigate
  to `/login?returnUrl=<current relative URL>` with `forceLoad: true` (inject `NavigationManager`), and
  throw a dedicated `SessionExpiredException` so the calling code stops cleanly. Use the existing
  `LocalUrl.IsLocal` rules for the return URL.
- Anonymous users getting 401 keep today's behaviour.
- Put the 401 check in one place (a private helper used by every call), not in each method.
- Tests (bUnit is not required; use the existing `BlogApiClientTests` style with a fake handler and a
  fake `NavigationManager`): authenticated + 401 → navigation to `/login?returnUrl=…` and
  `SessionExpiredException`; anonymous + 401 → no navigation.

## Out of scope

The short-expiry E2E test (the orchestrator configures the mock IdP and adds it), persisting the token
store, cleaning up per-session lock objects, and any UI changes.
