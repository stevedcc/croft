# Smoke test

A manual pass over a running stack, for checking a build by hand. The automated suites are in
AGENTS.md, "Running things"; this file is only the by-hand walk.

## Automated

```bash
dotnet build Croft.slnx
dotnet test tests/Croft.Api.Tests --no-build
dotnet test tests/Croft.Web.Tests --no-build
dev/run-e2e.sh
```

The API suite and the container-backed Web tests need a container runtime (Docker or Podman).
Without one, run `dotnet test tests/Croft.Web.Tests --no-build --filter "Category!=Container"`.

## Manual local flow

1. Start PostgreSQL and the mock OIDC provider: `docker compose up -d`.
2. Start the stack: `./start-local.sh` (restores `dotnet-ef`, applies the migrations, starts the
   API on :5000 and the Web app on :5002). Check `curl -sf http://localhost:5000/health` and
   `curl -sf http://localhost:5002/health/live`.
3. Optionally seed sample content: `dev/seed-dev-data.sh`.
4. Open http://localhost:5002/. The home page, `/categories`, `/search?q=...`, `/feed.xml` and
   `/sitemap.xml` answer without signing in.
5. Sign in at `/admin` as `admin@example.com` (the mock login page ignores the password) and create
   a post in `/admin/posts/new`: save a draft, then schedule it a minute ahead, or publish it now.
   The scheduled publisher publishes it when the time comes.
6. Upload a banner and an inline image (`/admin/media`, or the post form) and check the post page
   shows them.
7. As `editor@example.com`, open `/editor`, open a draft and leave feedback on a text selection.
8. View the published post anonymously and check `/admin/posts/{id}/analytics` counts the view.
9. Stop with `./stop-local.sh`.
