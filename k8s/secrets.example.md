# Secrets

No secret values (real or placeholder) live in this repository. Create the Secrets below in the
`blog` namespace **before** applying the base: both apps refuse to start outside Development
without their keys. The commands use placeholder values.

Generate the analytics ingest key **once** and use the same value in both secrets — the Web app
sends it with every page-view beacon and the API only counts beacons that carry it:

```bash
ANALYTICS_INGEST_KEY="$(openssl rand -base64 32)"
```

## `blog-db` (database connection)

The API container and its migration init container read these five keys and build
`ConnectionStrings__BlogDb` from them (`k8s/api-deployment.yaml`). They are the key names
CloudNativePG's generated app secret uses, so pointing the base at such a secret needs no
changes; anything else works too as long as the keys match.

```bash
kubectl -n blog create secret generic blog-db \
  --from-literal=host='<database host>' \
  --from-literal=port='5432' \
  --from-literal=dbname='blog' \
  --from-literal=username='<database user>' \
  --from-literal=password='<database password>'
```

## `croft-web-db` (Web database)

The Web app's own small Postgres database (shared token sessions and Data Protection keys) is
what lets any Web pod serve any signed-in user. The base requires this Secret: the Web `migrate`
init container and the `web` container both read `ConnectionStrings__WebDb` from its single
`connection-string` key (`k8s/web-deployment.yaml`). Only the single-instance overlay (README.md)
runs with no Web database at all, deletes both readers of this key, and needs no Secret.

```bash
kubectl -n blog create secret generic croft-web-db \
  --from-literal=connection-string='Host=<database host>;Port=5432;Database=croft_web;Username=<database user>;Password=<database password>'
```

## `blog-web-secrets` (Web app)

`Oidc__ClientSecret` is the provider-issued client secret of the "blog" application — an
independent credential, never the client ID itself. The client ID is not a secret (it travels in
every login redirect) and lives in `Oidc__ClientId` in `k8s/configmap.yaml`; the API's
`Oidc__Audience` must equal that same client ID.

```bash
kubectl -n blog create secret generic blog-web-secrets \
  --from-literal=Oidc__ClientSecret='<oidc client secret>' \
  --from-literal=Analytics__IngestKey="$ANALYTICS_INGEST_KEY"
```

## `blog-api-secrets` (API)

`Analytics__HashKey` hashes visitor IPs before they are stored; generate it separately.

```bash
kubectl -n blog create secret generic blog-api-secrets \
  --from-literal=Analytics__HashKey="$(openssl rand -base64 32)" \
  --from-literal=Analytics__IngestKey="$ANALYTICS_INGEST_KEY"
```

Optional, for announcing posts on Mastodon and Bluesky when they are published (README, "Announcing on
publish"): add the two credentials to the same Secret. A network whose credential is missing is not set up, and
nothing is sent to it. Use the narrowest credentials there are: a Mastodon access token with the scope
`write:statuses` only, and a Bluesky app password without access to direct messages.

```bash
kubectl -n blog patch secret blog-api-secrets --type merge -p "$(jq -n \
  --arg token '<mastodon access token>' --arg password '<bluesky app password>' \
  '{stringData: {Social__Mastodon__AccessToken: $token, Social__Bluesky__AppPassword: $password}}')"
```

(`Social__Mastodon__AccessToken` and `Social__Bluesky__AppPassword` are the two keys. The ConfigMap's "No secrets"
comment stays true: they live here, not there.)

## `croft-dataprotection-cert` (optional: encrypts the Data Protection key ring)

Optional. Without it the Web starts and logs one Warning that its key ring is stored unencrypted, and
a full copy of the Web database then yields the session tokens (`README.md`, "Encrypting the key
ring"). The Secret holds an **RSA** certificate and its private key, in PEM form, as `tls.crt` and
`tls.key`. Make the key on your own machine, never on the CA. A self-signed certificate is enough
(nothing checks its chain or expiry); this one is valid for ten years, can be used for nothing but
encrypting a key, and is accepted for no TLS role:

```bash
openssl req -x509 -newkey rsa:3072 -nodes -days 3650 -subj "/CN=croft-keyring" \
  -keyout tls.key -out tls.crt \
  -addext "basicConstraints=critical,CA:FALSE" \
  -addext "keyUsage=critical,keyEncipherment" \
  -addext "extendedKeyUsage=emailProtection"
kubectl -n blog create secret tls croft-dataprotection-cert --cert=tls.crt --key=tls.key
shred -u tls.key
```

A certificate from your CA works as well if its template allows only `keyEncipherment` and the
`emailProtection` extended key usage (generate the key and the signing request locally, send only
the request to the CA).
