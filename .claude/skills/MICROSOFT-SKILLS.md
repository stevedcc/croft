# Vendored Microsoft skills

Copied from https://github.com/dotnet/skills at commit `9de888b8ce55da34e76a113bd7b2d463d6b3e1e6` (2026-10-14), under the MIT
licence in `MICROSOFT-SKILLS-LICENSE`. Unchanged, except that the Blazor skills carry a `blazor-`
name prefix (their names were generic) with their cross-references renamed to match.

| Skill | Source plugin | Use |
|---|---|---|
| `blazor-support-prerendering` | dotnet-blazor | `[PersistentState]`, `RendererInfo`, `[ExcludeFromInteractiveRouting]` |
| `blazor-fetch-and-send-data` | dotnet-blazor | loading data in components, lifecycle |
| `blazor-author-component` | dotnet-blazor | component structure, disposal, async rules |
| `blazor-collect-user-input` | dotnet-blazor | forms and validation |
| `blazor-coordinate-components` | dotnet-blazor | parameters, cascading values, events |
| `blazor-use-js-interop` | dotnet-blazor | JS interop |
| `optimizing-ef-core-queries` | dotnet-data | EF Core performance and correctness |
| `dotnet-webapi` | dotnet-aspnetcore | API endpoints, HTTP semantics, OpenAPI, ProblemDetails |
| `csharp-refactoring` | dotnet | behaviour-preserving refactoring |
| `analyzing-dotnet-performance` | dotnet-diag | ~50 performance anti-patterns |
| `test-anti-patterns` | dotnet-test | auditing tests (task and final reviewers) |
| `assertion-quality` | dotnet-test | weak or presence-only assertions (task and final reviewers) |
| `migrate-dotnet10-to-dotnet11` | dotnet-upgrade | the .NET 11 upgrade |

**Precedence: AGENTS.md and `writing-dotnet10-code` win** where they disagree. In particular:
pages fetch data and components present it (components don't inject `IBlogApiClient` unless a
spec marks them as service components); Web calls the API through `IBlogApiClient`; services sit
behind interfaces; tests use the frameworks already here (xUnit for API and Web, bUnit for Web
components, NUnit + Playwright for E2E) and assert outcomes (AGENTS.md rule 7). The skills mention
other Microsoft skills that are not vendored here (MSTest writing, coverage, test tagging and
more); ignore those pointers.

To update: re-copy the same paths from a newer commit, re-apply the Blazor prefix and
cross-reference rename, and record the commit here.
