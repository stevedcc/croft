# Brief 07c: the code is named Croft

Read AGENTS.md first and follow it. One commit per item, TDD, break-it-once for every key test, E2E
twice at the end (the rename touches `src/Blog.Web`), the final whole-branch review.

The framework is published as **Croft**. Today every project, folder, namespace and assembly is
`Blog.*` (evidence: `Blog.slnx`; `src/Blog.Api`, `src/Blog.Web`, `src/Blog.Shared`;
`tests/Blog.Api.Tests`, `tests/Blog.Web.Tests`, `tests/Blog.Web.E2E`; `App.razor:30` links
`Blog.Web.styles.css`; the Dockerfiles publish and run `Blog.*.dll`; the k8s base uses images
`blog-api`/`blog-web`). Nothing a visitor sees may change.

## What changes and what stays

**Renamed** (`Blog` → `Croft` as the product prefix): the solution file (`Croft.slnx`), the six
project folders and `.csproj` files, root namespaces, `namespace`/`using`/`@using`/`@namespace`
lines, assembly names, the isolated-CSS bundle link (`Croft.Web.styles.css`), the Dockerfiles'
paths and entrypoints (`Croft.Api.dll`, `Croft.Web.dll`), the k8s base's two `image:` names
(`croft-api`, `croft-web`), and every path in scripts and docs that names a project
(`start-local.sh`, `dev/*.sh`, `.dockerignore`, `AGENTS.md`, `README.md`,
`branding.example/README.md`). Use `git mv` for folders and files so history follows them.

**Kept exactly** (they are runtime contracts or the domain word "blog", not the product name):
- configuration sections and keys: `Blog:` / `Blog__*`, `ConnectionStrings:BlogDb`;
- k8s resource **names and labels** (`blog-api`, `blog-web`, `blog-db`, `blog-branding`, namespace
  `blog`, the `blog-api.blog.svc.cluster.local` URL): a site overlay must see no change;
- the database name, the EF migration ids (`20261006152151_InitialCreate`) and the
  `__EFMigrationsHistory` rows;
- the `blog_sid` cookie and claim, the `blog-theme` localStorage key, the mock IdP client id
  `blog-web`, the `/tmp/blog-*` pid and log file names;
- domain type names (`BlogDbContext`, the `Components/Blog` folder and the like).

**EF model snapshot:** the Designer and `BlogDbContextModelSnapshot.cs` name entities by CLR name
(`"Blog.Api.Models.Post"`); those strings become `"Croft.Api.Models.Post"` along with the
namespaces. Check: `dotnet tool run dotnet-ef migrations has-pending-model-changes -p src/Croft.Api
-s src/Croft.Api` reports no changes. Never add or regenerate a migration.

## Item 1: the contract tests (RED)

In `tests/Blog.Web.Tests` (it moves with item 3), add `RepositoryNamingTests.cs`, walking the
repo root from the test (as `K8sBaseTests` finds it) and skipping `.git`, `bin`, `obj`,
`node_modules`, `uploads` and `docs`:
- no file name or path segment matches `Blog\.(Api|Web|Shared)` or equals `Blog.slnx`;
- no text file contains `\bBlog\.(Api|Web|Shared)\b` or `Blog.slnx`;
- `Croft.slnx` exists and lists exactly the six `Croft.*` projects.

Commit it failing (say so in the message). It stays red until item 3 and is the rename's measure.

**Rephrase 07b's guard.** `K8sBaseTests.NoFileUnderK8s_MentionsCrawfordOrAuthentik` names one
site, which a framework must not do. Replace it with a generic rule that is stronger:
- every `http://` or `https://` value in the config maps has a host that is `example.com`, ends in
  `.example.com`, or ends in `.svc.cluster.local`;
- every Ingress host ends in `example.com`;
- every container `image:` has no registry part (no `/`, no `:` tag);
- keep the "no `authentik`" check (it names a provider, not a site).
Update the image-name assertions to `croft-api`/`croft-web` in item 3, not here.
Break-it-once: set the Web config map's `Blog__BaseUrl` to `https://blog.example.org`; the URL
test must fail. Afterwards, `git grep -niI crawford -- ':!docs' ':!LICENSE'` prints nothing.

## Item 2: Croft.Shared, by hand (the pattern)

`git mv src/Blog.Shared src/Croft.Shared`, rename its `.csproj`, change its namespaces, and every
`using Blog.Shared…` / `Blog.Shared.` reference and `ProjectReference` in the other projects.
Update `Blog.slnx` to the new path (the solution file itself is renamed in item 3). Build, run the
API and unit suites: green. Record this commit's sha; item 3 applies its pattern.

## Item 3: apply pattern from item 2 to the other five projects and the solution

Apply pattern from item 2's commit to, in this order, `Blog.Api` (with the snapshot strings above),
`Blog.Web` (with `App.razor`'s bundle link and `@namespace`/`@using` in every `_Imports.razor`),
`Blog.Api.Tests`, `Blog.Web.Tests`, `Blog.Web.E2E`; then `git mv Blog.slnx Croft.slnx`. One commit
per project is fine; item 3 is complete when the solution builds and `RepositoryNamingTests` is
green. Update `K8sBaseTests`' image names here, together with the k8s `image:` lines.

Break-it-once for the visible risk: change `App.razor`'s link back to `Blog.Web.styles.css`;
`LayoutTests.NavItem_ActiveLink_HasPrimaryStrongBackgroundAndWhiteText` (E2E) must fail (it
measures the scoped CSS on the active nav link). Restore it.

## Item 4: scripts, containers and docs

Update every path in `start-local.sh`, `stop-local.sh`, `dev/*.sh` (including
`run-large-data-e2e.sh`'s `*Blog.Web/bin*` process match), `.dockerignore`, both Dockerfiles,
`AGENTS.md`, `README.md` and `branding.example/README.md`. `./start-local.sh` must bring the dev
stack up and `./stop-local.sh` down. Build both images from the repo root with podman
(`-f src/Croft.Api/Dockerfile -t croft-api .`, likewise `croft-web`) and run each once with
`podman run --rm` for a few seconds: the log must not contain "The application to execute does not
exist" (a wrong entrypoint); configuration errors are expected and fine. Report both log tails.

## Out of scope

Class names, configuration keys, k8s resource names, routes, cookies, migrations' content, any
visible change, `docs/` (history stays as written), publishing, the harness.
