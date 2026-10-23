# What changed in .NET 9 and .NET 10

A short summary of the features most relevant to this repo. When in doubt, the official pages are
authoritative:

- C# 14: https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14
- C# 13: https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-13
- .NET 10: https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/overview
- .NET 9: https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-9/overview
- ASP.NET Core 10: https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-10.0
- ASP.NET Core 9: https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-9.0
- EF Core 10: https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew
- EF Core 9: https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-9.0/whatsnew
- Breaking changes in .NET 10: https://learn.microsoft.com/en-us/dotnet/core/compatibility/10.0
- Microsoft's own agent skills (Blazor, Web API, EF Core queries, testing, upgrades): https://github.com/dotnet/skills

## C# 13 (.NET 9)

- `params` collections: `params ReadOnlySpan<T>`, `params IEnumerable<T>`, `params List<T>`.
- `System.Threading.Lock`: `lock` on a `Lock` uses `EnterScope()` instead of `Monitor`.
- Partial properties and indexers.
- `ref` locals and `unsafe` blocks allowed in async methods and iterators; `allows ref struct`.
- `\e` escape character; `^` (from-end index) in object initializers.
- `[OverloadResolutionPriority]`.

## C# 14 (.NET 10)

- **Extension members:** `extension(Receiver r) { ... }` blocks in a static class declare extension
  properties, methods, static members and operators.
  ```csharp
  public static class EnumerableExtensions
  {
      extension<T>(IEnumerable<T> source)
      {
          public bool IsEmpty => !source.Any();
      }
  }
  ```
- **`field` keyword:** the compiler-generated backing field inside an accessor.
- **Null-conditional assignment:** `customer?.Order = GetOrder();` (and compound `?.X += 1`, not `++`).
- `nameof(List<>)` on unbound generics.
- Implicit conversions between arrays, `Span<T>` and `ReadOnlySpan<T>`.
- Lambda parameter modifiers without types: `(text, out result) => ...`.
- Partial constructors and partial events.
- User-defined compound assignment (`+=`) and `++`/`--` operators.

## .NET 9 libraries

- `Guid.CreateVersion7()` (time-ordered UUIDv7).
- LINQ `CountBy`, `AggregateBy`, `Index`.
- `Task.WhenEach` (process tasks as they complete).
- `Base64Url`, `JsonSerializerOptions.Web`, JSON schema exporter.
- `OrderedDictionary<TKey, TValue>`, `ReadOnlySet<T>`.
- `TimeProvider` has been in the BCL since .NET 8; use it instead of `DateTime.UtcNow`.

## .NET 10 libraries and SDK

- LTS, released November 2025, supported for 3 years.
- JSON: option to disallow duplicate properties, strict serializer options, `PipeReader` support.
- `WebSocketStream`; post-quantum cryptography (ML-KEM, ML-DSA).
- File-based apps: `dotnet run app.cs` with `#:package` / `#:sdk` / `#:property` directives.
- `dotnet tool exec` / `dnx` runs a tool without installing it.
- `dotnet test` supports Microsoft.Testing.Platform.

## ASP.NET Core 9

- `MapStaticAssets()`: build-time fingerprinting, precompressed (gzip/Brotli) assets, strong
  caching. Replaces `UseStaticFiles()` for app assets; use `@Assets["path"]` in Razor.
- Built-in OpenAPI document generation: `AddOpenApi()` / `MapOpenApi()`.
- `HybridCache` (L1 + L2 cache with stampede protection).
- Blazor: `RendererInfo` and `AssignedRenderMode`, `[ExcludeFromInteractiveRouting]`, better
  reconnection, `AddAuthenticationStateSerialization`.

## ASP.NET Core 10

- **Blazor:**
  - `NavigationManager.NotFound()` produces a real 404 (status code in SSR, `Router.NotFoundPage` content when interactive).
  - `[PersistentState]` on a property persists prerendered state into the interactive render (replaces manual `PersistentComponentState`).
  - `ReconnectModal` component in the template.
  - `blazor.web.js` is served as a fingerprinted static asset.
  - `ResourcePreloader`.
  - JS interop: `InvokeConstructorAsync`, `GetValueAsync`, `SetValueAsync`.
  - `NavLink` with `Match.All` ignores the query string.
- **Validation:** `builder.Services.AddValidation()` validates minimal-API parameters and Blazor
  forms with DataAnnotations; `[ValidatableType]` for nested types.
- **Server-Sent Events:** `TypedResults.ServerSentEvents(...)`.
- **OpenAPI 3.1** by default, YAML output, XML doc comments included; `OpenApiAny` replaced by `JsonNode`.
- **Auth:**
  - Passkey support in Identity.
  - Cookie auth returns 401/403 for API endpoints instead of redirecting to login.
- **Exception handler:** exceptions handled by an `IExceptionHandler` are no longer logged by
  default (`SuppressDiagnosticsCallback` controls it).

## EF Core 9

- `UseSeeding` / `UseAsyncSeeding`.
- Better LINQ translation (complex types in `GroupBy`/`ExecuteUpdate`, more inlined collections).
- Migrations take a lock and warn about pending model changes.

## EF Core 10

- LTS.
- `LeftJoin` / `RightJoin` LINQ operators translate to SQL.
- `ExecuteUpdateAsync` accepts a regular (block) lambda, so setters can be conditional.
- Named query filters: `HasQueryFilter("SoftDelete", ...)`, `IgnoreQueryFilters(["SoftDelete"])`.
- Complex types can be optional and mapped to JSON; structs are supported.
- Parameterized collections (`Contains` over a list) are translated to padded scalar parameters by
  default; configure with `UseParameterizedCollectionMode`, or force inlining with `EF.Constant`.
- Analyzer warns on string concatenation in raw SQL.
- Inlined constants are redacted in logs.
- Migrations are no longer wrapped in a single transaction.
