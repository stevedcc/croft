# What changed in .NET 11

Summary of the features relevant to this repo, as of .NET 11 RC 2 (GA expected November 2026;
corrected against the RC 2 SDK by brief 16a).
The official pages are authoritative; recheck them at GA:

- C# 15: https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-15
- .NET 11: https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/overview
- ASP.NET Core 11: https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-11
- EF Core 11: https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-11.0/whatsnew
- Breaking changes when upgrading: the `migrate-dotnet10-to-dotnet11` skill in this repo.

## C# 15

- **Collection expression arguments:** `[with(capacity: n), ..items]`, `[with(comparer), a, b]`.
- **Union types:** `public union Pet(Cat, Dog, Bird);` implicit conversion from each case, exhaustive
  `switch`. Runtime types `UnionAttribute` / `IUnion`. A first step: type unions over existing types;
  the rest of the proposal (e.g. inline case declarations, F#-style) comes in later C# versions.
- **Closed hierarchies:** `closed` class or record: only derivable within its assembly, implicitly
  abstract, exhaustive switches over direct descendants; not transitive (mark intermediates `closed`).
- **Extension indexers:** `this[...]` inside an `extension(Receiver r) { }` block.
- **Labeled `break` / `continue`:** `outer: for (...) { ... break outer; }`.
- **Memory safety (preview only):** pointer relaxations, `unsafe(expr)`, `safe` modifier; requires
  `LangVersion preview`, so not used here.

## .NET 11 runtime and libraries

- Runtime Async is on for `net11.0` without opting in: cleaner async stack traces, lower overhead.
  Nothing to write; don't add `EnablePreviewFeatures`.
- Minimum hardware: x86-64-v2; Arm64 on Linux stays armv8.0 (Raspberry Pi still supported), but the
  ReadyToRun (precompiled) target becomes armv8.0 + LSE. The cluster's Raspberry Pi 4s (Cortex-A72,
  `/proc/cpuinfo` features `fp asimd evtstrm crc32 cpuid`, no `atomics`) run .NET 11 but can't use the
  precompiled framework code: expect more JIT work at start-up, so slower pod readiness during
  rollouts. Measure start-up to ready before and after the upgrade. Croft's own assemblies are
  pinned with crossgen2's `--instruction-set:neon,crc,-lse` (`Directory.Build.props`, W-088); the
  `-lse` is needed, `armv8-a` or `neon,crc` alone still lets the optimistic light-up require LSE.
- LINQ: `FullJoin`, tuple-returning `Join`/`GroupJoin` overloads (Enumerable, Queryable,
  AsyncEnumerable).
- `EqualityComparer<T>.Create(keySelector)`; generic `Random.NextInteger<T>`.
- System.Text.Json: union support, JSON Lines output, more polymorphism and source-generation options.
- Async validation in DataAnnotations: `AsyncValidationAttribute`, `IAsyncValidatableObject`,
  `Validator.ValidateObjectAsync`; options validation through `IAsyncStartupValidator`.
- `MemoryCache` emits OpenTelemetry metrics; declarative Activity tracing rules.
- Zstandard in `System.IO.Compression`; new in-memory streams (`StringStream`,
  `ReadOnlyMemoryStream`, ...); `Process` run-and-capture helpers.

## ASP.NET Core 11

- **Blazor:** automatic circuit pause on hidden tabs (`Microsoft.AspNetCore.Components.Server.AutoPause`),
  server-triggered circuit pause (`Circuit.RequestCircuitPauseAsync`), circuits pick up refreshed
  authentication, `DisplayName` component, relative `NavigateTo`/`NavLink`, `Virtualize` can scroll to
  an item, `CacheView` for static SSR subtrees, `EnvironmentView`, static SSR client-side and async
  form validation, `[SupplyParameterFromSession]` and `[SupplyParameterFromTempData]` (namespace
  `Microsoft.AspNetCore.Components.Web`), antiforgery middleware optional (automatic cross-origin
  CSRF protection is on by default), QuickGrid pagination and sorting improvements.
- **Analyzers new in the SDK:** BL0010-BL0016 (among them BL0016: every JS interop call guarded by a
  `try`/`catch`, here `catch (JSDisconnectedException)`; BL0013: a service reading
  `AuthenticationStateProvider` without following `AuthenticationStateChanged`), CA1517, CA1876,
  CA1877, CA2026, CA2027, CA2028, ASP0033-ASP0038; IDE0028 now proposes collection expression
  arguments. `System.Net.Http.Json` is an implicit using.
- **Static assets in Development:** `MapStaticAssets` answers a revalidation (`If-None-Match`) with
  304 (10 always sent 200); a Debug build sends `blazor.web.js` as an effectively uncompressed gzip
  stream.
- **Minimal APIs:** async validation, validation attributes no longer experimental, built-in
  validation localization, union types as body/response types, short-circuit endpoints by attribute.
- **OpenAPI 3.2 by default** (breaking for consumers expecting 3.1), Server-Sent Events described.
- **Native OpenTelemetry tracing:** subscribe to the `Microsoft.AspNetCore` activity source.
- Zstandard response compression; rate limiter returns accurate `Retry-After`; HTTP/3 starts
  earlier; `TimeProvider` in Identity.

## EF Core 11

- Better SQL for to-one includes (pruned joins, shorter `ORDER BY`): automatic.
- `MaxByAsync` / `MinByAsync`, `FullJoin`, GroupBy improvements, redundant `CAST`s removed.
- Complex types: TPT/TPC support, lambda-chained configuration (`Property(e => e.Details.X)`), keys
  and indexes on complex paths, many fixes (complex types now a full alternative to owned types).
- Latest migration id in the model snapshot (divergent migrations conflict in source control).
- `dotnet ef database update <Name> --add`, `.config/dotnet-ef.json` defaults, `migrations remove
  --offline`, `--context "*"`.
- Analyzer: `AsAsyncEnumerable` instead of `ToAsyncEnumerable` on queries.
