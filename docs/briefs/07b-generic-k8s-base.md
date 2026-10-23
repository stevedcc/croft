# Brief 07b: `k8s/` becomes a generic kustomize base

Read AGENTS.md first and follow it. One commit per item, TDD (manifest-contract tests), break-it-once
for every key test, the final whole-branch review. This brief changes no application code, so E2E
is not required; run the API and unit suites at the end.

The design is `docs/superpowers/specs/2026-10-10-site-branding-design.md`, section "Kubernetes:
generic base, site overlay" (approved); read it whole. Brief 07a runs at the same time on `src/`,
`tests/`, `branding.example/`, `start-local.sh` and `AGENTS.md`. **You own** `k8s/`, `README.md`,
`tests/Blog.Web.Tests/K8sBaseTests.cs` (new) and `tests/Blog.Web.Tests/Blog.Web.Tests.csproj`
(to add YamlDotNet). Never edit anything else.

**The contract with 07a:** Web reads its branding from `/app/branding`; the ConfigMap is
`blog-branding`. Configuration keys: `Oidc__Authority`, `Oidc__Audience` (API),
`Oidc__ClientId` (Web), `Oidc__ClientSecret` (Web, a Secret), `Oidc__RoleGroups__Admin`,
`Oidc__RoleGroups__Editor` (both), `Blog__BaseUrl` (Web), `ConnectionStrings__BlogDb` (API).

Today (evidence): `k8s/` holds crawford.dev's deployment — `registry.crawford.dev` images, a
CloudNativePG `Cluster`, Traefik `IngressRoute`s and `Middleware` (`traefik.io/v1alpha1`), a
cert-manager `Certificate`, a Longhorn storage class on the media PVC, `Authentik__*` keys with
crawford values, `secrets.tswap.tmpl`, and the API's connection string hard-codes
`Host=blog-db-rw;Database=blog;Username=app` with the password from Secret `blog-db-app`.

## Item 1: the base

- `k8s/kustomization.yaml` (`apiVersion: kustomize.config.k8s.io/v1beta1`) listing every
  remaining manifest; namespace `blog` stays.
- Only `v1`, `apps/v1` and `networking.k8s.io/v1` resources. Delete from `k8s/`:
  `cnpg-cluster.yaml`, `certificate.yaml`, `secrets.tswap.tmpl`, the Traefik resources. (They move
  to the site repository; git history keeps them.)
- `ingress.yaml`: a plain `networking.k8s.io/v1` Ingress, host `blog.example.com`, path `/` to
  the Web Service, no `ingressClassName`, no `tls`, no annotations.
- Images `blog-api` and `blog-web` (no registry, no tag) for overlays to set with `images:`.
- The media PVC has no `storageClassName`.
- Config maps: the contract's keys with placeholders (`https://idp.example.com/application/o/blog/`,
  `your-client-id`, `Admin`, `Editor`, `https://blog.example.com`); no `Authentik__*` keys.
- Database: both the migration initContainer and the API container take `DB_HOST`, `DB_PORT`,
  `DB_NAME`, `DB_USER`, `DB_PASSWORD` from Secret `blog-db` keys `host`, `port`, `dbname`,
  `username`, `password` (CloudNativePG's app secret uses these key names), declared before
  `ConnectionStrings__BlogDb` =
  `Host=$(DB_HOST);Port=$(DB_PORT);Database=$(DB_NAME);Username=$(DB_USER);Password=$(DB_PASSWORD)`
  (Kubernetes expands `$(VAR)` only from entries above it; keep the existing comment saying so).
- Web mounts ConfigMap `blog-branding` at `/app/branding`, `readOnly: true`, `optional: true`, so
  the base deploys alone with the default branding.
- Keep every resource **name** as today (Deployments, Services, PVC, config maps, Secrets
  `blog-api-secrets`/`blog-web-secrets`) so a site overlay changes nothing it doesn't mean to.
- `secrets.example.md` rewritten: the Secrets the base expects (`blog-db` and its five keys,
  `blog-web-secrets` with `Oidc__ClientSecret`, `blog-api-secrets` if still used), with
  `kubectl create secret` examples using placeholder values.

Tests — `tests/Blog.Web.Tests/K8sBaseTests.cs` (xUnit + FluentAssertions, YamlDotNet added to the
csproj), reading `k8s/` from the repo root:
- every `*.yaml` in `k8s/` except `kustomization.yaml` is listed in `resources`, and every listed
  file exists;
- every document's `apiVersion` is one of `v1`, `apps/v1`, `networking.k8s.io/v1`;
- no `storageClassName`; the Ingress has no `ingressClassName`, `tls` or annotations, host
  `blog.example.com`;
- no file under `k8s/` contains "crawford" or "Authentik" (case-insensitive);
- the API container and the migrate initContainer both build `ConnectionStrings__BlogDb` from the
  five `blog-db` keys, each declared before it;
- Web has the `blog-branding` volume, `optional: true`, mounted read-only at `/app/branding`;
- the config maps contain exactly the contract's non-secret keys for each app.
Break-it-once: put `storageClassName: longhorn` back on the PVC; a test must fail. The
orchestrator also runs `kubectl kustomize k8s` on your branch (you must not run kubectl).

## Item 2: README "Deploying a site"

Replace the README's "Kubernetes" and "Cutover" sections (and any other crawford.dev, tswap,
CloudNativePG, Traefik, cert-manager or Longhorn instructions) with "Deploying a site":
- what the app needs: the Secrets and keys above, the configuration keys above, a PostgreSQL 18
  database, an OIDC provider (Authentik as one example, with the redirect, post-logout and
  back-channel logout URIs the app uses: read them from `src/Blog.Web/Program.cs`, don't guess);
- the branding folder: point to `branding.example/README.md` (07a writes it) for the format;
- three ways to supply it: a kustomize overlay over `k8s/` with a `configMapGenerator` named
  `blog-branding` (a short overlay example: `resources: [<base>]`, `images:`, the generator, one
  patch), Docker/Podman (`-v ./branding:/app/branding:ro`, plus a compose snippet), and a plain
  run (`Blog__BrandingPath=/path/to/branding dotnet run ...`).
Check: `grep -niE 'crawford|tswap|cnpg|cloudnative|traefik|cert-manager|longhorn' README.md`
prints nothing.

## Out of scope

Application code, `branding.example/` itself (07a), the site repository and its overlay, the
live cluster (nothing is applied), CI.
