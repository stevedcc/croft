# Brief 13b: security review Minors (sign-out CSRF, pod hardening, proxies, tokens at rest, sessions, report limiter, missing sub, AuthorId)

Read AGENTS.md first and follow it. One commit per item, TDD, break-it-once for each key test,
E2E twice at the end, the final whole-branch review.

**Source:** `docs/reviews/2026-10-19-security.md` findings 6-12, and the architecture review's note
that public DTOs leak `AuthorId`. Each was checked against `blog-framework` at 165bf4f
(2026-10-20); briefs 13e–13h landed since (0.6.4, dbc5043), so line numbers may have moved and
13h already moved the security headers into `OnStarting`: confirm each "Now:" before its item.
Read the finding before its item.

**Owner-visible outcome:** a page a staff member visits can't sign them out; the pods run with no
capabilities, a read-only root filesystem and no API token; only the ingress decides a reader's
address; a copy of the Web database yields no usable tokens when a key-ring certificate is
configured; a session ends at `Auth:SessionLifetime` in every deployment; `/csp-report` can't be
used to flood logs or memory; a token without `sub` is refused; anonymous readers never see an
author's IdP subject.

**CSP impact: none.** The sign-out page already has `form-action 'self'`
(`tests/Croft.Web.Tests/CspSnapshots.txt:21`); it keeps exactly that policy.
`CspSnapshots.txt` must be byte-identical before and after. If any item seems to need a CSP
change, stop and report.

## Item 1: sign-out is a POST with antiforgery (finding 6)
Now: `GET /logout` → interstitial (`src/Croft.Web/Authentication/SignOutInterstitial.cs`, a
constant HTML string with `<script src="/js/logout.js">`) → `GET /logout/complete`
(`Program.cs:921-925`) removes the session, signs out and goes to the IdP end-session endpoint.
The auth cookie is `SameSite=Lax`, so a cross-site link to `/logout/complete` signs a staff
member out of the blog and of the IdP.
- `GET /logout` stays (the sidebar link); it only renders the interstitial and changes nothing.
- The interstitial is rendered per request with an antiforgery token (`IAntiforgery`,
  HTML-encoded) in a `<form method="post" action="/logout/complete">` with a visible "Continue
  signing out" submit button (works without JavaScript). `logout.js` clears the draft backups as
  now, then submits the form (`form.requestSubmit()`); no other script change.
- `POST /logout/complete` validates the token (400 without or with a wrong one, session kept);
  `GET /logout/complete` no longer signs out (405, or a redirect to `/logout`; pick one, pin it).
Tests: a GET to `/logout/complete` with a valid auth cookie leaves the session in the store and
the cookie valid; a POST without the token → 400 and session kept; with the token → session
removed and redirect to the IdP end-session URL with `id_token_hint`; E2E sign-out round trip
still works with JavaScript and with JavaScript disabled; `CspSnapshots.txt` unchanged.
Break-it-once: drop the antiforgery validation → the no-token test fails.

## Item 2: pods without privileges (finding 7)
Now: only pod-level `runAsNonRoot: true` and `fsGroup: 1654` (`k8s/api-deployment.yaml:54`,
`k8s/web-deployment.yaml:57`); no container `securityContext` anywhere, init containers included.
- Every container, init containers included: `allowPrivilegeEscalation: false`,
  `capabilities: { drop: [ALL] }`, `readOnlyRootFilesystem: true`; pod:
  `seccompProfile: { type: RuntimeDefault }`, `automountServiceAccountToken: false`.
- Writable paths become explicit: an `emptyDir` at `/tmp` for both apps (the runtime's
  diagnostics sockets and ASP.NET's large-form buffering use it); uploads stay on the media PVC;
  the Web's Data Protection keys stay in the Web database (or the single-instance overlay's
  `/keys` volume — check the overlay in `k8s/README.md` still works read-only elsewhere).
- Prove the apps start and serve with a read-only root: run both images with
  `podman run --read-only --tmpfs /tmp --cap-drop=ALL --security-opt no-new-privileges` against
  the dev database and hit `/health/ready`, a reading page, an upload and a migration; record the
  commands and results in the commit.
- Network policies stay an optional component (their selectors are site-specific); the README
  says what they add and that egress rules belong in the site overlay. No egress policy here.
Tests (`K8sBaseTests`): every container in both deployments has the four settings, the pod the two,
`/tmp` is an `emptyDir` mount. Break-it-once: remove `readOnlyRootFilesystem` from the Web
container → the test fails naming it.

## Item 3: only the ingress decides a client's address (finding 8)
Now: `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` in both manifests (W-013) trusts every peer; Web
`Program.cs:77-80` sets no `KnownNetworks`; the API configures nothing.
- Remove the env var from both manifests. Web: `ForwardedHeadersOptions` with `KnownNetworks`
  from a new setting `ForwardedHeaders:KnownNetworks` (CIDR list) and `ForwardLimit = 1`; the k8s
  base sets it to the k3s default pod network `10.42.0.0/16` with a README line saying to change
  it for another cluster; empty = trust no proxy (local dev). Keep the "processed once"
  guarantee (`ForwardedHeadersOnceTests`, `Program.cs:482-485`): one middleware pass only.
- API: no forwarded headers at all (its visitor address comes only from `X-Visitor-IP` with the
  ingest key). `AnalyticsController.ResolveVisitorIp` parses `X-Visitor-IP` with
  `IPAddress.TryParse` and ignores anything else.
- Update W-013 in `docs/workarounds.md` (removed or reworded, with the reason).
Tests: Web with `KnownNetworks=10.42.0.0/16`: a request from 10.42.1.5 with `X-Forwarded-For:
203.0.113.7` → client address 203.0.113.7; the same header from 192.0.2.9 → 192.0.2.9 (header
ignored); with the setting empty → header ignored. API: `X-Forwarded-For` never changes
`RemoteIpAddress`; `X-Visitor-IP: not-an-ip` with a valid key → the socket peer is used.
Break-it-once: put `KnownNetworks.Clear()` back → the untrusted-peer test fails.

## Item 4: tokens at rest are protected (finding 9)
Now: `UserSession.AccessToken/RefreshToken/IdToken` plain text (`PostgresTokenStore.cs:92-101`);
the key ring is in the same database, unprotected.
- `PostgresTokenStore` protects the three values with `IDataProtectionProvider.CreateProtector(
  "Croft.Web.TokenStore.v1")` on write and unprotects on read. A row that fails to unprotect (an
  existing plaintext row after the upgrade, a rotated-away key) is treated as a missing session:
  the user signs in again. Say so in the release notes line of the commit.
- Optional key-ring encryption from a PEM pair, the shape of a `kubernetes.io/tls` Secret mount:
  settings `DataProtection:CertificatePath` and `DataProtection:CertificateKeyPath` (no password)
  → `X509Certificate2.CreateFromPemFile` → `ProtectKeysWithCertificate`. The key must be **RSA**
  (the key ring is encrypted with EncryptedXml, which can't use EC keys): an EC or unreadable
  certificate fails startup with a message saying so. Rotation: optional
  `DataProtection:PreviousCertificatePaths` (list of cert/key pairs) →
  `UnprotectKeysWithAnyCertificate`, so keys encrypted under last year's certificate still read.
  Expiry is not checked when decrypting (say so in the README; renew yearly, keep the old pair
  until its keys have expired). When unset, start with one Warning that the key ring is stored
  unencrypted. Without it, a full copy of the Web database still yields the tokens (key ring and
  data together) — the README says that plainly.
- The production deployment: the site's Secret is `croft-dataprotection-cert` (type
  `kubernetes.io/tls`, RSA 3072, issued by the cluster's step-ca through its `croft-keyring`
provisioner: key usage keyEncipherment only, extended key usage emailProtection only, so nothing
accepts it for TLS; valid to 2036-10-17). Add to
  the k8s base an **optional** Secret volume (`optional: true`) mounted read-only at
  `/etc/croft/dataprotection` with the two settings pointing at `tls.crt`/`tls.key`, so a site
  without the Secret still starts (with the Warning). README: how to issue one. Any RSA certificate works (Data Protection checks neither chain
  nor expiry); recommend one that nothing accepts for TLS (no clientAuth/serverAuth extended key
  usage, and not *no* EKU, which Go treats as "any"), e.g. a self-signed one or a CA provisioner
  with a keyEncipherment-only template; key made locally, never on the CA.
Tests (in addition to those below): a test RSA PEM pair configured → key XML encrypted; an EC
pair → startup fails naming RSA; a key encrypted under a "previous" pair is still read after the
current pair changes.
- No column changes (still `text`), so no migration.
Tests: after a sign-in the three columns don't contain the raw token (read the row directly);
read back through the store returns the original; a plaintext row → `GetAsync` returns null; with a
test certificate configured the stored key XML is encrypted (`<encryptedSecret>`).
Break-it-once: store unprotected → the raw-token test fails.

## Item 5: sessions end at Auth:SessionLifetime everywhere (finding 10)
Now: only `SessionPurgeHostedService` (Postgres) reads `SessionExpiresAt`; `InMemoryTokenStore`,
`TokenSessionEvents.OnValidatePrincipal` and `UserAccessTokenService` ignore it; the in-memory
store never purges.
- An entry past `SessionExpiresAt` is missing: in both stores' `GetAsync`, so the principal is
  rejected and the token service never refreshes it.
- The in-memory store purges expired entries on the same schedule (register the purge for it
  too, or purge on access plus a timer — pick one, test it).
Tests: with a fake `TimeProvider`, a session one second past its lifetime → the next request is
signed out and no refresh request reaches the IdP (both stores); the in-memory store's count drops
after the purge interval. Break-it-once: remove the in-memory expiry check → its test fails.

## Item 6: the CSP report limiter can't be rotated around (finding 11)
Now: `CspReportRateLimiter` keyed by the raw address (`CspReportEndpoint.cs:38`), no bucket cap, no
global ceiling; `/csp-report` is exempt from the request limiters (`Program.cs:1011`).
- Key by `RequestLimits.ClientKey` (IPv6 /64), cap the bucket table (`Csp:ReportMaxClients`,
  default 10 000; when full, new clients are refused until the sweep), and a global ceiling
  (`Csp:ReportGlobalPerMinute`, default 600). The class summary's "single-node" sentence is
  corrected (limits are per pod).
- The limiter check runs before the body buffer is allocated.
Tests: 61 reports from 61 addresses of one /64 → the 61st refused; the 10 001st distinct client
refused; 601 reports per minute across clients → the 601st refused; a refused report allocates no
body buffer (counter or allocation probe). Break-it-once: key by the raw address again → the /64 test
fails.

## Item 7: a token without sub is refused (finding 12)
Now: `GetUserId()` falls back to `"unknown"` (`src/Croft.Api/Identity/UserExtensions.cs:7-10`);
five call sites store it or compare with it.
- The API's authentication rejects a token without `sub` (401, logged at Warning once per
  issuer) — in the JWT bearer events, so no endpoint ever sees such a principal. `GetUserId()`
  throws if `sub` is still missing (it cannot be reached; the throw makes that explicit).
Tests: an Editor token without `sub` → 401 on create, update, feedback create/delete and media
upload; nothing is written. Break-it-once: restore the fallback → the update test (Editor B edits
Editor A's draft) succeeds and the test fails.

## Item 8: anonymous reads don't expose AuthorId (architecture review)
Now: `PostDto.AuthorId` (the IdP subject) is returned by `GET /api/posts/{slug}` and
`GET /api/pages/{slug}` to anonymous readers (`PostService.cs:689`).
- `PostDto.AuthorId` becomes nullable and is `null` on public reads (and so in the public read
  cache); staff reads keep it (`PostEditPage.razor:571` uses it).
Tests: an anonymous GET of a published post and page has no `authorId` value; a staff GET has it.
Break-it-once: map it on public reads → the anonymous test fails.

## Item 9: the Web Program.cs comment says what CspMiddleware really does (13h review, minor)
`src/Croft.Web/Program.cs:445-447` says CspMiddleware writes the CSP "again in its own OnStarting".
That rewrite is conditional (`CspMiddleware.cs:155-158`, only when the document stage changed the
header). Today every response the exception handler clears is re-executed as the `/Error`
document, whose stage does change it (`ErrorResponseHeadersTests` pins that). Correct the comment
to say both things. Comment only; no test.

## Out of scope
- Egress network policies (site overlay).
- Moving the key ring out of the database (certificate encryption is the fix here).
- Anything in 13e, 13f, 13g.
