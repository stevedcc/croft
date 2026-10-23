# site-files.example/

A complete example of the site's well-known files folder — the small files a site publishes
without rebuilding Croft: `security.txt`, `humans.txt`, WebFinger, Apple's AASA and anything
else that fits on a single `text/plain` or JSON page.

The dev and E2E stacks mount this folder (`BLOG_SITE_FILES_PATH=$ROOT/site-files.example` in
`start-local.sh`), and each file becomes a public URL at the path relative to the folder:

```
humans.txt                       → /humans.txt
.well-known/security.txt         → /.well-known/security.txt
.well-known/webfinger            → /.well-known/webfinger (item 3)
.well-known/apple-app-site-association → /.well-known/apple-app-site-association
```

## The folder format

Every file is read once at startup, hashed to a strong ETag and served as exact-path,
`Cache-Control: public, max-age=3600`, `X-Content-Type-Options: nosniff`, GET and HEAD only,
no cookies, no CSP document header. The folder's contents can be anything that satisfies:

- **Allowed extensions:** `.txt` (text/plain; charset=utf-8), `.json` (application/json).
- **Allowed names without an extension:** `.well-known/apple-app-site-association`
  (application/json), `.well-known/webfinger` (WebFinger JSON, item 3).
- **Anything else fails startup** (`.html`, `.htm`, `.svg`, `.js`, `.css`, other extensions,
  any other no-extension file). Site files must never be a document or a script on the
  site's origin.
- **64 KiB cap per file.**
- **No shadowing:** a site file at `/robots.txt`, `/sitemap.xml`, `/branding/*`, `/health/*`,
  `/api/*`, `/login`, `/logout`, etc. fails startup naming both. Files with an extension or
  under `.well-known/` can't collide with page slugs (`[a-z0-9-]+`), so no slug check is needed.

## Kubernetes

An optional ConfigMap `blog-site-files` mounted read-only at `/app/site-files`, the same way as
`blog-branding` (optional, so the base deploys alone). See `k8s/README.md` for the overlay and
the 1 MiB ConfigMap limit.

## What the example holds

- `humans.txt` — a one-page team signature. Self-explanatory; an example only.
- `.well-known/security.txt` — a real RFC 9116 file. Real deployments replace the contact,
  expiry and canonical URL with their own.

Other folders (briefs 11a item 2 onwards) will publish more site files from the same folder.