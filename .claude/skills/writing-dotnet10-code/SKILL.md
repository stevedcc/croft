---
name: writing-dotnet10-code
description: Use when writing or modifying any C#, Razor or EF Core code in this repo (.NET 10, C# 14, ASP.NET Core 10, Blazor, EF Core 10), including reviewing such code, and when existing code nearby looks like C# 8-10 era style.
---

# Writing .NET 10 code

## Overview

This repo targets **.NET 10 / C# 14** (LTS). You already know these features; the problem is
habit. Most C# you were trained on is .NET 6–8 style, and much of this repo was written that way
too. **Older-style code nearby is not a precedent.** Code you write or substantially change follows
the defaults below. Don't rewrite code you aren't otherwise touching; modernising untouched code is
done by its own brief.

If you're unsure an API exists or how it's spelled, check it the cheap way: build. Don't guess, and
don't fall back to the old pattern because you're unsure. The official what's-new pages are in
[whats-new.md](whats-new.md).

## Defaults

| Instead of | Write |
|---|---|
| Constructor that only assigns `private readonly` fields | Primary constructor: `public class TagService(BlogDbContext db, ISlugService slugs) : ITagService`, use `db` directly |
| `new List<T>()`, `Array.Empty<T>()`, `new[] { a, b }`, `.ToList()` just to return a list | Collection expressions: `[]`, `[a, b]`, `[..items]`, `[..first, ..second]` |
| `Id = Guid.NewGuid()` on a new entity | Leave `Id` unset: EF (Npgsql) generates a time-ordered UUIDv7 on insert. Need the id before `SaveChanges`? `Guid.CreateVersion7()`. Random-only values (file names) may keep `NewGuid`; secrets use `RandomNumberGenerator` |
| `DateTime.UtcNow` / `DateTimeOffset.UtcNow` in `src/` | Inject `TimeProvider` (registered in both `Program.cs`) and call `time.GetUtcNow()`; tests use the `FakeTimeProvider` fixture |
| `private readonly object _gate = new();` with `lock` | `private readonly Lock _gate = new();` (`System.Threading.Lock`) |
| Explicit backing field just to validate or trim in a setter | `field` keyword: `public string Name { get; set => field = value.Trim(); }` |
| `if (x is not null) x.Y = v;` | `x?.Y = v;` |
| Static class of `this` methods to add a computed *property* | `extension(Post post) { public bool IsPublished => ...; }` inside a static class |
| `GroupBy(k).Select(g => (g.Key, g.Count()))` in memory | `CountBy(k)`; `AggregateBy(k, seed, f)`; `Select((x, i) => ...)` becomes `Index()` |
| `GroupJoin` + `SelectMany` + `DefaultIfEmpty` in EF | `LeftJoin(inner, outerKey, innerKey, (o, i) => ...)` / `RightJoin` |
| Building `SetProperty` expression trees conditionally | EF 10 `ExecuteUpdateAsync(s => { s.SetProperty(...); if (c) s.SetProperty(...); })` |
| `app.UseStaticFiles()` | `app.MapStaticAssets()` (fingerprinted, compressed; reference assets with `@Assets["..."]`) |
| Blazor page rendering a "not found" message, or setting `StatusCode = 404` | `Navigation.NotFound()`; the `Router` already has `NotFoundPage` |
| Manual `PersistentComponentState` registration | `[PersistentState] public PostDto? Post { get; set; }` |
| `UseExceptionHandler(app => app.Run(...))` lambdas | `IExceptionHandler` class + `AddExceptionHandler<T>()` + ProblemDetails |
| `catch (Exception)` to log and continue | Catch the specific exceptions, or `catch (Exception ex) when (...)`; let the rest propagate |
| `params T[]` | `params ReadOnlySpan<T>` or `params IEnumerable<T>` |
| Hand-written null/range checks on minimal-API inputs | `builder.Services.AddValidation()` + DataAnnotations |

Always: file-scoped namespaces, `is null` / `is not null`, switch expressions and patterns,
`required`/`init` and records for DTOs, raw string literals for multi-line SQL/JSON in tests.

## Discipline

Adapted from Microsoft's C# Expert agent (github/awesome-copilot, `agents/CSharpExpert.agent.md`);
where it conflicts with AGENTS.md (services behind interfaces, the test frameworks in use, outcome assertions), AGENTS.md wins.

- **Least exposure:** `private` > `internal` > `protected` > `public`. Nothing is public by default.
- **Fixing one method? Check its siblings** (the other overloads, the same pattern in the same class
  or in the other app) for the same bug, and fix or report them.
- **No unused members or parameters;** reuse an existing method before writing a near-copy.
- **Guards:** `ArgumentNullException.ThrowIfNull(x)`, `ArgumentException.ThrowIfNullOrWhiteSpace(s)`,
  early. No blanket `!` to silence the nullable warnings.
- **Async end to end:** accept a `CancellationToken` and pass it on; cancellable delays; no
  fire-and-forget (`_ = DoAsync()`); a timeout cancels the work (linked token + `CancelAfter`);
  don't wrap a returned task in `async`/`await` for nothing; `await using` for async disposables.
- **Tests go through public behaviour:** no `InternalsVisibleTo`, no widening visibility for a test,
  and never mock code that is part of this solution (mock only external dependencies).

## Common mistakes

- **Copying the style of the file you're editing.** Follow these defaults in the lines you write.
  If mixing styles in one class would be confusing, convert *that class* and say so in the commit.
- **Using `NewGuid` in tests' expected data while `src/` uses v7.** Assert on behaviour, not on GUID shape.
- **Calling `DateTime.UtcNow` "just once".** It makes the code untestable for time; inject `TimeProvider`.
- **Comments that cite briefs or tasks** ("R3.2: ...", "per brief 04"). Comments explain *why* the
  code is the way it is; the history lives in git.
- **Preview features or package upgrades.** Use only what ships in SDK 10.0.4xx and the pinned
  packages; never add `<LangVersion>preview</LangVersion>`.
