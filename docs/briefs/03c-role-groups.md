# Brief 03c: configurable role groups

Read AGENTS.md first and follow it, including "Finishing a task". Small brief: work inline, one
commit, then the final whole-branch review as AGENTS.md describes.

**Why:** the owner's Authentik (shared by several apps) has the groups **Blog Admins** and **Blog
Editors**. Today both apps turn every `groups` (and `role`) claim value straight into a role, so
only groups literally named `Admin` / `Editor` work, and any group with those names in any app's
context would grant blog roles. The role names inside the apps stay `Admin` and `Editor`; what
changes is which group grants them.

## 1. The mapping

- New settings `Authentik:RoleGroups:Admin` and `Authentik:RoleGroups:Editor`: the group name that
  grants each role. Defaults when unset: `Admin` and `Editor` (so local development and the mock
  OIDC server, which send those group names, behave exactly as now).
- One mapping used by both apps (`Blog.Shared` is the natural home): a `groups` claim value grants
  a role only when it equals the configured group name (ordinal, case-sensitive). Other group
  values grant nothing. `role` claim values `Admin` / `Editor` keep passing through unchanged (a
  future service account may send roles directly); other `role` values grant nothing either.
- Wire it into the API's `OnTokenValidated` (`src/Blog.Api/Program.cs`) and the Web app's
  `TokenSessionEvents.OnTokenValidated`, replacing the current "every value becomes a role" code.
  No duplicate role claims.

## 2. Configuration

- `k8s/configmap.yaml`: both `blog-api-config` and `blog-web-config` get
  `Authentik__RoleGroups__Admin: "Blog Admins"` and `Authentik__RoleGroups__Editor: "Blog Editors"`.
- AGENTS.md, the paragraph "Roles come only from the `groups` (or `role`) claim": say how the
  mapping works and name the two settings.

## Tests (TDD, AGENTS.md rules 1, 2, 5)

- Unit tests of the mapping: configured name grants the role; the default names work when
  unconfigured; an unmapped group (e.g. `Admin` when the config says `Blog Admins`) grants nothing;
  `role` claim `Admin` passes through; no duplicates when both claims say the same.
- API: an authenticated request whose token carries `groups: ["Blog Admins"]` with that setting
  configured is authorised as Admin on an Admin-only endpoint, and `groups: ["Admin"]` with the
  same setting is refused (403).
- Web: a `TokenSessionEvents` test that the principal gets `ClaimTypes.Role` = `Admin` from
  `groups: ["Blog Admins"]` with the setting configured.
- Break-it-once: revert to "every value becomes a role" and show the unmapped-group test fails.
- Whole suites at the end, E2E twice in a row (the mock OIDC users must still get their roles).

## Out of scope

Anything else. No Authentik changes, no new roles.
