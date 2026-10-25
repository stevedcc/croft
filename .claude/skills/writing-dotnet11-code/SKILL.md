---
name: writing-dotnet11-code
description: Use when writing, modifying or reviewing C#, Razor or EF Core code in this repo once its projects target net11.0 (.NET 11, C# 15, ASP.NET Core 11, EF Core 11). Before that upgrade, use writing-dotnet10-code instead.
---

# Writing .NET 11 code

## Overview

Applies once `TargetFramework` is `net11.0` (check `src/*/*.csproj`); until then this skill does not
apply and `writing-dotnet10-code` is the rule. After the upgrade **both** apply: everything in
`writing-dotnet10-code` (its defaults table and its Discipline section) still holds, and this skill
adds the .NET 11 / C# 15 defaults on top. Older-style code nearby is not a precedent; don't rewrite
code you aren't otherwise touching.

Written against .NET 11 **RC 2** (SDK 11.0.100-rc.2.26504.105, October 2026; every API in the
table below was compiled once on it by brief 16a). If an API below doesn't compile on the GA SDK in
`global.json`, build first, then check [whats-new.md](whats-new.md); don't fall back to the old
pattern because you're unsure. Never set `<LangVersion>preview</LangVersion>`, so C# 15's
memory-safety features (pointer relaxations, `unsafe(...)` expressions) are not used here.

## Defaults

| Instead of | Write |
|---|---|
| `new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ... }`, `new List<T>(capacity)` then `AddRange` | Collection expression arguments: `HashSet<string> s = [with(StringComparer.OrdinalIgnoreCase), ..names];`, `List<T> l = [with(capacity: n), ..items];`. *16c:* `ConcurrentDictionary` and `FrozenSet` take `with(comparer)` too (`src/Croft.Shared/ReservedSlugs.cs`: one `FrozenSet` instead of a `HashSet` then `ToFrozenSet`; `src/Croft.Web/SiteFiles/SiteFilesService.cs`: instead of `new[] { .. }.ToFrozenSet(comparer)`). Not convertible in C# 15: a dictionary with `[key] = value` entries (no dictionary expressions; `src/Croft.Web/Services/IconCatalog.cs` keeps its initializer), a field declared as the interface `IReadOnlySet<T>` (a collection expression can only target it if the declaration changes to a concrete type, CS9174; Croft keeps the interface, and for `src/Croft.Web/Branding/BrandProfile.cs` a `FrozenSet` would also reorder the names its error message lists), and `Queue<T>`/`Stack<T>` (no `Add`) |
| An abstract base record plus a `_ => throw` default arm in every `switch` over its subtypes | `public closed record class Outcome;` with its subtypes in the same assembly: the compiler knows every case, so a `switch` that handles each is exhaustive with no default arm. Mark intermediate types `closed` too if the switch goes deeper. *16c:* a switch **statement** over a closed type is exhaustive (for "not all code paths return a value") only when it also has `case null:`, even for a non-nullable variable; a switch expression compiles without a null arm, but on RC 2 a null input then throws `SwitchExpressionException` at runtime, so keep `or null` where the old default handled null (`AnnounceErrors.cs`). A default arm that did something (not `throw`) becomes an explicit case with the same body (`src/Croft.Api/Social/SocialAnnounceHostedService.cs` `RecordAsync`, `AnnounceErrors.cs` `HttpStatusOf`) |
| A result type modelled as a class with nullable fields or a `Kind` enum | A union when the cases are unrelated types: `public union LoadResult(PostDto, NotFound, Invalid);` with exhaustive `switch`. ASP.NET Core 11 accepts unions as JSON body/response types (OpenAPI `anyOf`). C# 15 unions are a first step: a union over existing types, no inline case declarations (those belong to a later C# version). If a shape you want isn't expressible, use a `closed` record hierarchy (no sites in Croft as of 16c: the two outcome hierarchies have related, nested cases and are `closed` records; `StartupCommand`'s `Kind` cases are not existing types) |
| A boolean flag or `goto` to leave nested loops | Labeled jumps: `outer: foreach (...) { foreach (...) { if (done) break outer; if (skip) continue outer; } }` (IDE0410 points them out) (no sites in Croft as of 16c; IDE0410 is a build error here) |
| `OrderByDescending(x => x.K).FirstOrDefaultAsync()` to get the element with the largest key | EF Core 11 `MaxByAsync(x => x.K)` / `MinByAsync`, translated to SQL (no EF sites in Croft as of 16c; in-memory `MinBy` in `tests/Croft.Api.Tests/Fixtures/FakeTimerTimeProvider.cs`) |
| `GroupJoin` + `SelectMany` + `DefaultIfEmpty` on both sides for an outer join of two sets | `FullJoin(inner, outerKey, innerKey, (o, i) => ...)` (LINQ and EF Core 11, `FULL JOIN` in SQL); `LeftJoin`/`RightJoin` from .NET 10 for one side (no sites in Croft as of 16c) |
| A hand-written `IEqualityComparer<T>` class that compares one key | `EqualityComparer<T>.Create(x => x.Key)`. *16c:* the selector's parameter is `T?`, so with nullable on write `x => x?.Key` or handle null (`x => x.Key` is CS8602, an error under warnings-as-errors). (No sites in Croft as of 16c) |
| `modelBuilder.Entity<E>().ComplexProperty(e => e.Details).Property(d => d.X)` | `modelBuilder.Entity<E>().Property(e => e.Details.X)`; keys and indexes may use complex-type paths: `HasIndex(e => e.Address.PostalCode)` (no sites in Croft as of 16c: no complex types in the model) |
| Validation that must hit the database or the API inside `IValidatableObject.Validate` (blocking) | `AsyncValidationAttribute` / `IAsyncValidatableObject`, validated by `Validator.ValidateObjectAsync`, by minimal APIs (`AddValidation()`) and by Blazor forms. In RC 2 an `AsyncValidationAttribute` must still override the synchronous `IsValid(object?, ValidationContext)` (abstract; throw `NotSupportedException` if only the async path may run), and `IAsyncValidatableObject` extends `IValidatableObject`, so `Validate` is implemented too (no sites in Croft as of 16c) |
| `services.AddOpenTelemetry().WithTracing(t => t.AddAspNetCoreInstrumentation())` | `.AddSource("Microsoft.AspNetCore")`: ASP.NET Core 11 emits the HTTP server span with the semantic-convention attributes itself; drop `OpenTelemetry.Instrumentation.AspNetCore` once nothing else needs it. `MemoryCache` publishes its own metrics too. *16c:* not taken in Croft: `AddAspNetCoreInstrumentation(o => o.Filter = ...)` drops the health-probe spans (`src/Croft.Shared/Telemetry/CroftTelemetry.cs`), and `AddSource` has no filter, so the swap would be a behaviour change; filter in a processor or sampler first |
| `NavigateTo($"{currentBase}/child")` string building | `NavigateTo("child", new NavigationOptions { RelativeToCurrentUri = true })`, `<NavLink href="child" RelativeToCurrentUri="true">` (no sites in Croft as of 16c: every `NavigateTo` is an absolute route) |
| Label text duplicated next to a form field | `<DisplayName For="@(() => Model.Field)" />` with `[Display(Name = ...)]` on the model (localizable) (no sites in Croft as of 16c: the plain `<label>`s label plain inputs, not `EditForm` fields) |

## Blazor Server in .NET 11 (this repo is global Interactive Server; see AGENTS.md "Interactivity")

- **Circuits on inactive tabs** can pause automatically, freeing server memory and the SignalR
  connection: package `Microsoft.AspNetCore.Components.Server.AutoPause`, then
  `app.MapRazorComponents<App>().WithBrowserOptions(o => o.AddAutoPause(p => p.HiddenDelay = ...))`
  (`AddAutoPause` is in the `Microsoft.AspNetCore.Components` namespace).
  Adding a package is a brief-level decision, not something to do in passing.
- **Server-triggered pause** (`Circuit.RequestCircuitPauseAsync`) lets a pod ask its clients to pause
  gracefully before shutdown: the right tool for rolling updates and node drains, instead of
  dropping circuits.
- After an authentication refresh, circuits receive the refreshed `ClaimsPrincipal` and raise
  `AuthenticationStateChanged`; don't add reconnect logic for that.
- `CacheView` caches rendered subtrees **during static SSR only**; it does nothing for interactive
  pages, so it only matters for pages excluded from interactive routing.

## Tooling

- EF Core 11 records the latest migration id in the model snapshot: two branches that each add a
  migration now conflict in `*ModelSnapshot.cs`. **Resolve such a conflict by discarding one branch's
  migration and re-adding it on top of the other**, never by hand-merging the snapshot.
- `dotnet ef` reads defaults from `.config/dotnet-ef.json` (project, startup project); command-line
  options still win.
- `dotnet test` adds `--timeout`, `--maximum-failed-tests` and live in-flight test display.

## Common mistakes

- **Using this skill before the upgrade.** Until the csproj files say `net11.0`, none of the above
  compiles; follow `writing-dotnet10-code`.
- **`default` arms on `closed` hierarchies "just in case".** They switch off the compiler's
  exhaustiveness check, so a new subtype silently falls into the default. Handle every case.
- **Reaching for a union where one type with optional members is clearer.** Unions are for values
  that are genuinely one of several unrelated types.
- **Preview features.** `LangVersion preview`, `EnablePreviewFeatures` and the memory-safety rules
  stay off, as in `writing-dotnet10-code`.
