# Brief 10a: writing a post never loses work

Read AGENTS.md first and follow it. Implement inline (`executing-plans`), one commit per item,
TDD, break-it-once for each key test, E2E twice at the end, the final whole-branch review.

**Owner-visible outcome:** the owner can write a long post in one sitting without being sent to
Authentik. If a session does end, nothing typed or chosen in the editor is lost, and the Markdown
box and the Preview stay side by side while scrolling through a long post.

**Evidence (the owner, 2026-10-16 to 18, writing "Load Testing the Blog"):**
- The owner was redirected to Authentik several times while editing: on switching to the new
  post and on Save draft. Web's log showed "Purged 1 expired session(s)".
- The restore prompt brought back the title and body, but the **subtitle, search & link-preview
  description, category, tags and banner were lost**. The post went live without them.
- The Markdown box is 320 px high (`MarkdownEditor.razor.css`, `.md-editor__textarea`
  `min-height: 320px`). The Preview column renders the whole post, so the page is as long as the
  Preview. The owner scrolls the Preview down, then back up to the box.

## Item 1: a session lives as long as its refresh token, not its access token

**Cause (confirmed by reading the code; confirm with the test first):**
- `TokenSessionEvents` stores `ExpiresAt` = the **access token's** expiry, `TokenExpiry.ResolveExpiresAt`.
  The Authentik provider issues 5-minute access tokens and 30-day refresh tokens, with
  `offline_access`.
- `SessionPurgeHostedService.PurgeAsync` deletes `UserSessions` where `ExpiresAt < now`. So any
  session idle for about 5 minutes is deleted, even though its refresh token is valid for 30 days.
- Then `OnValidatePrincipal` (W-043) finds no entry, signs the cookie out, and the next request
  goes to Authentik.

**Do:**
- Add `SessionExpiresAt` to `UserSession` and `TokenEntry`, with a migration.
  `ExpiresAt` keeps its meaning: when the access token expires and must be refreshed.
- Set `SessionExpiresAt` at sign-in and on every refresh:
  - `now + refresh_expires_in` when the token response has it;
  - otherwise `now + Auth:SessionLifetime`, a new setting defaulting to 30 days, validated like the
    other settings (positive, at most 365 days).
- On every successful refresh, move `SessionExpiresAt` forward the same way.
- The purge deletes rows where `SessionExpiresAt < now`. Existing rows get `SessionExpiresAt` =
  `ExpiresAt + Auth:SessionLifetime` in the migration, so nobody signed in today is purged by the
  deployment.
- Keep the `ExpiresAt` index only if a query still uses it. Add an index on `SessionExpiresAt`.

**Tests (Web, real Postgres like the existing purge tests):**
- A session whose access token expired 10 minutes ago and whose `SessionExpiresAt` is in 29 days
  survives `PurgeAsync`. It then refreshes through `UserAccessTokenService` with a fake token
  endpoint, and `SessionExpiresAt` moves forward. This test is **red on today's code**; record the
  failure message in the commit.
- A session whose `SessionExpiresAt` passed is purged.
- The migration test: an existing row gets the computed `SessionExpiresAt`.
- **Break-it-once:** purge on `ExpiresAt` again; the first test must fail.

## Item 2: every session removal is logged and counted with its reason

**Evidence:** `UserAccessTokenService` removes the session silently in two places: with no
refresh token (~line 96), and on 400/401 from the token endpoint (~line 169), where the
`error`/`error_description` are not logged. `OnValidatePrincipal` signs out silently. The owner
couldn't tell from the logs why they were signed out.

**Do:**
- One `[LoggerMessage]` at Information per removal or sign-out, with these reasons:
  - `NoRefreshToken`;
  - `RefreshRejected`, with the HTTP status and the response's `error` and `error_description`,
    never the token or the body otherwise;
  - `EntryMissing` (from `OnValidatePrincipal`);
  - `Purged` (the purge's count, as today);
  - `BackChannelLogout`.
- Add a `reason` tag to a counter in `WebMetrics`, e.g. `croft.web.session.removed{reason}`.
  Keep the existing `TokenRefreshFailed` counter unchanged (dashboards outside this repository
  may use it) and add the new one beside it.
- Log the session id's first 8 hex characters only, never the whole id.

**Tests:**
- Each reason produces exactly one log entry with that reason, using a fake logger.
- The `RefreshRejected` entry carries `invalid_grant` and the description from a fake 400
  response, and **no** token value appears in any captured log text.
- **Break-it-once:** remove the `RefreshRejected` log call; its test must fail.

## Item 3: the draft backup keeps every editor field

**Evidence:** `draft-backup.js` stores only `title` and `body` (lines 30–34, 67–68);
`DraftBackup.cs` mirrors that.

**Do:**
- Back up **every** field of `PostEditModel` that the form edits:
  - title, subtitle, excerpt (the "Search & link-preview description"), Markdown body;
  - category (by id), tags (by name, in order);
  - banner (media id, plus the URL and alt text for the thumbnail);
  - publish date and publish time.
- Restore puts every field back, the banner thumbnail included.
- The backup record gets a `version: 2`. A version-1 record (title and body only) still restores
  those two fields.
- **Guard against new fields being forgotten:** a test enumerates `PostEditModel`'s public
  settable properties and fails, naming the property, when one is neither in the backup nor on a
  short, explicit exclusion list in the test. Each exclusion needs a reason in a comment, for
  example a computed or server-owned value.
- Keep today's triggers: backup on every model change, the restore prompt on load.
- Keep the storage key scheme, `new` → the post id after the first save. Don't change when the
  backup is cleared.

**Tests:**
- bUnit or unit: the backup record round-trips every field.
- The property-enumeration test.
- The version-1 record test.
- **E2E (the owner's scenario):**
  1. Fill every field on a new post, including a banner chosen from the library, a category and
     two tags.
  2. Reload the page without saving. That is the same as returning from Authentik.
  3. Accept the restore prompt.
  4. Assert every field's value, the banner thumbnail's `src`, and the tag chips.
  5. Save draft, then assert the saved post has every value, through the API or the edit page.
- **Break-it-once:** drop `subtitle` from the backup; the E2E and the enumeration test must fail.

## Item 4: losing the session in the editor costs nothing

**Design (decided; don't substitute in-place re-authentication):**
- When the editor's API client gets a `SessionExpiredException`, or the circuit is about to
  navigate to `/login`, the editor:
  1. writes the backup immediately (not on the next change);
  2. then lets today's redirect to `/login` happen, with the editor's URL as the return URL.
- When the editor loads with a backup **and** the user arrived from a sign-in, it restores
  without asking. It then shows a short notice: "Your session ended, so you signed in again. Your
  unsaved changes were restored." Use the existing `FeedbackPanel`, or a dismissible line above
  the form.
- "Arrived from a sign-in" is a flag the login callback sets: a one-shot query parameter on the
  return URL, or a short-lived marker in the backup record written before the redirect. Pick
  one, rule it, and remove the parameter from the address bar after use.
- Every other load with a backup keeps today's prompt, for example opening the editor in a new
  tab the next day.

Why not re-authenticate in place: the circuit keeps the principal it started with, so a sign-in
in a popup gives the cookie a new session id the circuit never sees. Making the circuit adopt it
needs a session-id reuse rule. That is security-sensitive and is out of scope for this brief.

**Tests (E2E):**
1. On a new post, fill every field.
2. Delete the user's `UserSessions` row from the test database, the same thing the purge did.
3. Click Save draft.
4. Assert:
   - the browser passes through `/login` (the E2E mock identity provider, `MockIdpApi.cs`,
     signs in again; extend it if it can't do a second sign-in in the same test);
   - it returns to the editor;
   - every field is restored without a prompt;
   - the notice is shown;
   - a second Save draft then saves every value.
- **Break-it-once:** skip the immediate backup write before the redirect, and type a last
  character just before Save. The last character must be missing and the test must fail. Write
  the test so it checks the last keystroke.

## Item 5: the Markdown box and the Preview stay side by side

**Design (decided):** from 960 px wide, where the editor already has two columns, the editor
fits the viewport instead of growing with the Preview.
- **The Preview column** (`.post-edit__side`) is `position: sticky`, with `top` equal to the
  page's top gap. Its height is the viewport minus that gap (`100dvh − top`), and it scrolls on
  its own (`overflow-y: auto`). The Feedback tab works the same way.
- **The Markdown box** grows to fill the viewport height: at least today's 320 px, and as tall
  as the viewport minus the toolbar and a margin, so it is about as tall as the Preview. The user
  can still resize it vertically.
- **Scroll sync:** when the Markdown box scrolls or its caret moves, the Preview scrolls to the
  block that holds the top visible source line.
  - In the preview only, `MarkdownRenderer` adds `data-source-line="<n>"` to every top-level
    block. Markdig blocks carry `Line`.
  - **Public pages must stay byte-identical:** the attribute is opt-in through a renderer
    option, which the public pages never set.
  - The sync script lives in `editor.js`, which only interactive documents load, and adds no
    inline script or style.
  - Scrolling the Preview by hand does not move the Markdown box. Sync is one-way and
    throttled, using `requestAnimationFrame`.
- Under 960 px nothing changes: the columns stack, as today.

**Tests:**
- **Layout:** at 1440 × 900, with a post of 200 paragraphs:
  - the Markdown box is at least 600 px high;
  - after the page scrolls by 400 px, the Preview column's top stays within 2 px of its sticky
    `top`;
  - the Preview column scrolls internally (`scrollHeight > clientHeight`).
- **Scroll sync:** with a 200-paragraph post with headings, set the Markdown box's `scrollTop`
  so that the source line of `## Heading 120` is the top line. Within 500 ms, the Preview's
  `## Heading 120` element is within the top third of the Preview column.
- **Public pages:** a renderer unit test shows that a public render has no `data-source-line`,
  and that the preview option adds it to every top-level block. The existing reading-page
  snapshot or E2E tests stay green unchanged.
- **At 390 px**, the columns still stack, and the box keeps its 320 px minimum.
- **Break-it-once:** remove the sticky rule; the layout test must fail.

## Out of scope

- In-place re-authentication without a redirect (see item 4).
- Any change to the public pages' rendered HTML.
- CSP changes. Brief 10c moves `editor.js` and `draft-backup.js` and tightens the policy after
  this brief.
- The Featured card, excerpt truncation, the masthead and the default banner (brief 10b).

## Owner action noted in the brief, not yours

The owner may raise Authentik's access token validity as a stopgap. Item 1's fix must not
depend on it: test with 5-minute access tokens.
