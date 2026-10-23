# Brief 03: make the Kubernetes deployment work

Read AGENTS.md first and follow it, including "Finishing a task". You can't reach the real cluster;
verify with local Docker and manifest validation as described below. One commit per item.

Target cluster: K3s on **ARM64** (Raspberry Pi) nodes, Longhorn storage class, CloudNativePG
operator, **Traefik** ingress controller (not nginx; the cluster uses `IngressRoute` resources),
cert-manager. Images live in the owner's private registry: name them
`registry.crawford.dev/blog-api:0.1.0` and `registry.crawford.dev/blog-web:0.1.0`. The nodes mirror
that registry to the in-cluster registry without authentication, so the deployments need no
`imagePullSecrets`. On this machine `docker` is podman (with `qemu-aarch64` emulation installed).

Public hosts: `crawford.dev` (Web, at the root of the domain), `www.crawford.dev`
(redirects to `crawford.dev`) and `api.crawford.dev` (API, used by the AI editor account). The
Authentik authority stays a placeholder (`https://authentik.example.com/...`); the owner sets it at
deploy time.

## 1. Container images

- The cluster is ARM64. Both Dockerfiles cross-build without emulation: `FROM --platform=$BUILDPLATFORM`
  for the SDK stage, `dotnet publish -a $TARGETARCH`, and the migrations bundle with the matching
  runtime (`linux-arm64` for `arm64`, `linux-x64` for `amd64`), so `--platform linux/arm64` (deploy)
  and `--platform linux/amd64` (local checks) both work.

- Add `src/Blog.Api/Dockerfile` and `src/Blog.Web/Dockerfile`, both built with the repo ROOT as the
  context (they need `Blog.Shared`). Add a root `.dockerignore` (bin/, obj/, .git/, tests/, uploads/, .env).
- Multi-stage: `mcr.microsoft.com/dotnet/sdk:10.0` to build and publish,
  `mcr.microsoft.com/dotnet/aspnet:10.0` to run. Run as the image's non-root `app` user; listen on 8080.
- The API image also contains an EF Core migrations bundle at `/app/efbundle`, built with
  `dotnet tool restore` and then `dotnet ef migrations bundle --self-contained -r <runtime>` (see above).

## 2. Migrations in the cluster

- The API deployment gets an initContainer using the API image that runs
  `/app/efbundle --connection "$(ConnectionStrings__BlogDb)"`. The app container starts only after
  migrations succeed.

## 3. Database credentials from CloudNativePG

- Remove the hand-written `blog-db-app-secret` (with `CHANGE_ME`) from `k8s/cnpg-cluster.yaml`. Let
  CNPG generate its app secret (`blog-db-app`) by bootstrapping `initdb` with `database: blog`,
  `owner: app` and no `secret:`. Remove the redundant `managed.roles` block.
- The API builds its connection string from that secret in the pod spec. Map the secret's `password`
  key to an env var `DB_PASSWORD`, then set
  `ConnectionStrings__BlogDb=Host=blog-db-rw;Database=blog;Username=app;Password=$(DB_PASSWORD)`.
  The order of env entries matters for `$(...)` expansion.

## 4. Config and secrets split per app

- Replace the shared `blog-config` / `blog-secrets` with `blog-api-config`, `blog-web-config` and
  `blog-web-secrets`. The Web pod must not receive the database connection string.
- No real or placeholder secret values in the repo. Delete `k8s/secret.yaml`. Add
  `k8s/secrets.example.md` documenting the `kubectl create secret generic ...` commands for:
  - Web: `Authentik__ClientId`, `Authentik__ClientSecret`;
  - API: `Analytics__HashKey` (generate with `openssl rand -base64 32`);
  - both Web and API: `Analytics__IngestKey`, the same value in both (generate it once with
    `openssl rand -base64 32`). Both apps refuse to start outside Development without their keys.
- Web's `ApiBaseUrl` points at the in-cluster service, `http://blog-api.blog.svc.cluster.local/`.
  Browsers no longer talk to the API for images; the Web proxy does.

## 5. Audience fails fast

- API: when not in Development, throw at startup if `Authentik:Audience` is empty, with a clear
  message. Set `Authentik__Audience` in `blog-api-config` to a placeholder that's obviously wrong
  (`SET-TO-AUTHENTIK-CLIENT-ID`), and document in README that it must equal the Authentik client ID.
- Test: the API factory in Production mode with an empty audience fails to start.

## 6. Forwarded headers

- Remove the API's manual `Configure<ForwardedHeadersOptions>` and `UseForwardedHeaders()`.
- In both deployments, set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`. This turns on forwarded-header
  handling and trusts the ingress, which is acceptable because the services are ClusterIP and only
  reachable through it. Document this reasoning in a comment in the manifest.
- Web keeps `UseHsts()` outside Development. Don't re-add `UseHttpsRedirection()`; TLS terminates at
  the ingress.

## 7. Data Protection keys (Web)

- When `DataProtection:KeysPath` is configured, Web calls `PersistKeysToFileSystem(...)` and
  `SetApplicationName("blog-web")`. When it isn't configured (local dev), behaviour stays as it is now.
- Add a 100Mi Longhorn PVC `blog-web-keys`, mount it at `/keys`, and set `DataProtection__KeysPath=/keys`.

## 8. Deployment hygiene

- Both deployments: `strategy: type: Recreate` (RWO volumes; Blazor Server circuits are in-memory, so
  keep `replicas: 1`); `securityContext` with `runAsNonRoot: true` and `fsGroup` matching the `app`
  user's group so the volumes are writable; resource requests and limits; image tags as an explicit
  `:0.1.0` with `imagePullPolicy: IfNotPresent` (no `:latest`).
- API: `FileStorage__BasePath=/app/uploads` (the existing PVC mount).

## 9. Ingress for crawford.dev

- Replace `k8s/ingress.yaml` with Traefik `IngressRoute`s (`traefik.io/v1alpha1`, entry point
  `websecure`): `Host(\`crawford.dev\`)` → `blog-web`, `Host(\`api.crawford.dev\`)` → `blog-api`.
- TLS from a cert-manager `Certificate` in the `blog` namespace covering `crawford.dev`,
  `www.crawford.dev` and `api.crawford.dev` (secret `blog-tls`, `ClusterIssuer` `letsencrypt`; the
  owner confirms the issuer name at deploy time). The `IngressRoute`s reference that secret.
- `Host(\`www.crawford.dev\`)` redirects permanently to `https://crawford.dev` with a Traefik
  `Middleware` (`redirectRegex`, `permanent: true`).
- kubeconform skips the Traefik and cert-manager CRDs as missing schemas; that's expected.
- `crawford.dev` is currently served by another app in the cluster, which this blog replaces. Add a
  short "Cutover" section to the README: delete the old app's route for `crawford.dev` (the `hello-world`
  `IngressRoute` in the `default` namespace; two routes claiming the same host make routing
  ambiguous), then apply these, then check the certificate is issued.
  Don't write anything that touches the old app beyond naming that step.

## Verification and evidence

- `docker build` both images from the repo root, for `--platform linux/arm64` and for
  `--platform linux/amd64`. Show all four succeed and run as non-root (`docker run --rm <img> id`),
  and that the arm64 ones are arm64 (`docker run --rm --platform linux/arm64 <img> uname -m` prints
  `aarch64`; it runs under emulation). The functional checks below use the amd64 images.
- With `docker compose up -d` running, run the API image on the compose network against the compose
  Postgres. Run the initContainer step manually (`/app/efbundle --connection ...`) and show it
  succeeds, then show `GET /health` returns 200. Run the Web image with `ApiBaseUrl` pointing at the
  API container and show `GET /` returns 200. Both apps need their analytics keys to start outside
  Development: pass throwaway values generated in the shell (`openssl rand -base64 32`), never
  committed. Also show the Web image serves its static assets: `GET /fonts/SourceSerif4-Regular.woff2`
  and `GET /app.css` return 200, and the home page's HTML contains the `Blog:Name` value
  (`crawford.dev`).
- Validate all manifests with kubeconform, e.g.
  `docker run --rm -v "$PWD/k8s:/k8s" ghcr.io/yannh/kubeconform -strict -ignore-missing-schemas -summary /k8s`.
  The CNPG `Cluster` CRD will be skipped as missing-schema; that's expected.
- Existing test suites still pass.

## Out of scope
Routing changes beyond what's needed, service-mesh (Linkerd) annotations (the cluster no longer runs one), CI pipelines, pushing images to a registry, Helm.
