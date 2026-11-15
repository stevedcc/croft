# Secrets

No secret values (real or placeholder) live in this repository. Create the Secrets below in the
`blog` namespace **before** applying the base: both apps refuse to start outside Development
without their keys. Values in angle brackets are placeholders for settings that are not secret.

## Keeping the values out of plain text

A Secret's value can leak through more doors than the Secret itself. The commands below keep
three of them shut:

- **No value on a command line.** An argument such as `--from-literal=password=...` stays in the
  shell history and, while the command runs, is visible in the process list (`ps`) to every user
  of the machine. The commands below read each value with `IFS= read -rs` (not echoed, not kept in
  the history, spaces and backslashes kept as typed) and hand it to `kubectl` as a file: `--from-file=<key>=<(printf '%s' "$VAR")`. `printf`
  is a shell builtin, so the value never becomes a process argument either, and `printf '%s'` adds
  no trailing newline.
- **No client-side `kubectl apply` for Secrets.** A plain `kubectl apply -f secret.yaml` stores
  the whole manifest, values included, in the `kubectl.kubernetes.io/last-applied-configuration`
  annotation: in plain text if the manifest used `stringData`. `kubectl describe secret`, the
  command meant to be safe for Secrets, hides the values but prints annotations in full. Use
  `kubectl create` or `kubectl replace` for Secrets, as the commands below do, or
  `kubectl apply --server-side --field-manager=<a name of your own>`. The field manager matters:
  a bare `kubectl apply --server-side` (kubectl's default manager) deliberately keeps an existing
  annotation and updates it with every apply. To find old copies:
  `kubectl get secret -A -o json | jq -r '.items[] | select(.metadata.annotations["kubectl.kubernetes.io/last-applied-configuration"] // "" | test("\"(data|stringData)\"")) | "\(.metadata.namespace)/\(.metadata.name)"'`,
  and to remove one: `kubectl -n <namespace> annotate secret <name> kubectl.kubernetes.io/last-applied-configuration-`
  (with CloudNativePG, read the next point first: any change to a role's password Secret makes
  the operator set that password again).
- **No statement logging on the database while a password changes.** With CloudNativePG,
  changing a managed role's password secret makes the operator run
  `ALTER ROLE ... PASSWORD '...'`. PostgreSQL's `log_statement` set to `ddl`, `mod` or `all`
  writes that statement, password included, to the server log. Keep `log_statement: none` (the
  default) on a cluster whose role passwords come from Secrets; slow queries are still logged
  through `log_min_duration_statement`. A statement that **fails** is logged in full whatever
  `log_statement` says (`log_min_error_statement`, default `error`), so a failed `ALTER ROLE` would
  still carry the password; `log_min_error_statement: panic` closes that too, at the price of the
  failing statement's text in the server log (the error itself is still logged).

Generate the analytics ingest key **once** and use the same value in both secrets — the Web app
sends it with every page-view beacon and the API only counts beacons that carry it:

```bash
ANALYTICS_INGEST_KEY="$(openssl rand -base64 32)"
```

Run the blocks in bash or zsh (they use process substitution, `<(...)`), all in the same shell:
the ingest key above is used by two of them. Each block ends by unsetting what it read; the ingest
key is unset at the end of the `blog-api-secrets` block. Paste a block, then type each value at its
prompt. If the prompt instead shows the block's next line as the "value" (a terminal or multiplexer
without bracketed paste), paste the lines one at a time.

## `blog-db` (database connection)

The API container and its migration init container read these five keys and build
`ConnectionStrings__BlogDb` from them (`k8s/api-deployment.yaml`). They are the key names
CloudNativePG's generated app secret uses, so pointing the base at such a secret needs no
changes; anything else works too as long as the keys match.

```bash
printf 'Database password: ' >&2; IFS= read -rs DB_PASSWORD; echo
kubectl -n blog create secret generic blog-db \
  --from-literal=host='<database host>' \
  --from-literal=port='5432' \
  --from-literal=dbname='blog' \
  --from-literal=username='<database user>' \
  --from-file=password=<(printf '%s' "$DB_PASSWORD")
unset DB_PASSWORD
```

## `croft-web-db` (Web database)

The Web app's own small Postgres database (shared token sessions and Data Protection keys) is
what lets any Web pod serve any signed-in user. The base requires this Secret: the Web `migrate`
init container and the `web` container both read `ConnectionStrings__WebDb` from its single
`connection-string` key (`k8s/web-deployment.yaml`). Only the single-instance overlay (README.md)
runs with no Web database at all, deletes both readers of this key, and needs no Secret.

```bash
printf 'Web database password: ' >&2; IFS= read -rs WEB_DB_PASSWORD; echo
kubectl -n blog create secret generic croft-web-db \
  --from-file=connection-string=<(printf 'Host=%s;Port=5432;Database=croft_web;Username=%s;Password=%s' \
    '<database host>' '<database user>' "$WEB_DB_PASSWORD")
unset WEB_DB_PASSWORD
```

(A password containing `;` or `"` must be quoted inside the connection string, as Npgsql
documents; a generated password made of letters and digits avoids the question.)

## `blog-web-secrets` (Web app)

`Oidc__ClientSecret` is the provider-issued client secret of the "blog" application — an
independent credential, never the client ID itself. The client ID is not a secret (it travels in
every login redirect) and lives in `Oidc__ClientId` in `k8s/configmap.yaml`; the API's
`Oidc__Audience` must equal that same client ID.

```bash
printf 'OIDC client secret: ' >&2; IFS= read -rs OIDC_CLIENT_SECRET; echo
kubectl -n blog create secret generic blog-web-secrets \
  --from-file=Oidc__ClientSecret=<(printf '%s' "$OIDC_CLIENT_SECRET") \
  --from-file=Analytics__IngestKey=<(printf '%s' "$ANALYTICS_INGEST_KEY")
unset OIDC_CLIENT_SECRET
```

## `blog-api-secrets` (API)

`Analytics__HashKey` hashes visitor IPs before they are stored; generate it separately.

```bash
kubectl -n blog create secret generic blog-api-secrets \
  --from-file=Analytics__HashKey=<(openssl rand -base64 32 | tr -d '\n') \
  --from-file=Analytics__IngestKey=<(printf '%s' "$ANALYTICS_INGEST_KEY")
unset ANALYTICS_INGEST_KEY
```

Optional, for announcing posts on Mastodon and Bluesky when they are published (README, "Announcing on
publish"): add the two credentials to the same Secret. A network whose credential is missing is not set up, and
nothing is sent to it. Use the narrowest credentials there are: a Mastodon access token with the scope
`write:statuses` only, and a Bluesky app password without access to direct messages.

```bash
printf 'Mastodon access token: ' >&2; IFS= read -rs MASTODON_TOKEN; echo
printf 'Bluesky app password: ' >&2; IFS= read -rs BLUESKY_PASSWORD; echo
kubectl -n blog patch secret blog-api-secrets --type merge --patch-file <(
  MASTODON_TOKEN="$MASTODON_TOKEN" BLUESKY_PASSWORD="$BLUESKY_PASSWORD" jq -n \
    '{stringData: {Social__Mastodon__AccessToken: env.MASTODON_TOKEN, Social__Bluesky__AppPassword: env.BLUESKY_PASSWORD}}')
unset MASTODON_TOKEN BLUESKY_PASSWORD
```

(`jq` reads the two values from its environment, not its arguments, so they do not show in `ps`.
`Social__Mastodon__AccessToken` and `Social__Bluesky__AppPassword` are the two keys. The ConfigMap's
"No secrets" comment stays true: they live here, not there. `kubectl patch` adds no
`last-applied-configuration` annotation.)

## `croft-dataprotection-cert` (optional: encrypts the Data Protection key ring)

Optional. Without it the Web starts and logs one Warning that its key ring is stored unencrypted, and
a full copy of the Web database then yields the session tokens (`README.md`, "Encrypting the key
ring"). The Secret holds an **RSA** certificate and its private key, in PEM form, as `tls.crt` and
`tls.key`. Make the key on your own machine, never on the CA. A self-signed certificate is enough
(nothing checks its chain or expiry); this one is valid for ten years, can be used for nothing but
encrypting a key, and is accepted for no TLS role:

```bash
(umask 077
openssl req -x509 -newkey rsa:3072 -nodes -days 3650 -subj "/CN=croft-keyring" \
  -keyout tls.key -out tls.crt \
  -addext "basicConstraints=critical,CA:FALSE" \
  -addext "keyUsage=critical,keyEncipherment" \
  -addext "extendedKeyUsage=emailProtection"
kubectl -n blog create secret tls croft-dataprotection-cert --cert=tls.crt --key=tls.key
shred -u tls.key)
```

A certificate from your CA works as well if its template allows only `keyEncipherment` and the
`emailProtection` extended key usage (generate the key and the signing request locally, send only
the request to the CA).

## Changing a value later

Replace the whole Secret rather than applying a manifest over it: build it with
`kubectl create ... --dry-run=client -o yaml` and pipe it into `kubectl replace -f -`. `replace`
writes exactly the keys you pass and **drops every other key**, so list every key the Secret holds
(`kubectl -n blog get secret <name> -o json | jq '.data | keys'`), take the ones you keep from the
live Secret as the example does with the ingest key, and give the non-secret ones their real values,
not the placeholders above. For example, changing only the OIDC client secret:

```bash
printf 'New OIDC client secret: ' >&2; IFS= read -rs OIDC_CLIENT_SECRET; echo
kubectl -n blog create secret generic blog-web-secrets --dry-run=client -o yaml \
  --from-file=Oidc__ClientSecret=<(printf '%s' "$OIDC_CLIENT_SECRET") \
  --from-file=Analytics__IngestKey=<(kubectl -n blog get secret blog-web-secrets -o jsonpath='{.data.Analytics__IngestKey}' | base64 -d) \
  | kubectl replace -f -
unset OIDC_CLIENT_SECRET
```

then restart the Deployment that reads it (`kubectl -n blog rollout restart deployment/<name>`):
environment variables are read once, at start-up.
