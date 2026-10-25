# Brief 16c: the codebase reads as .NET 11 / C# 15 (idiom sweep: code first, then tests)

Read AGENTS.md first and follow it. This is a **refactoring brief**: load `csharp-refactoring` and
`writing-dotnet11-code` (the latter's "Instead of / Write" table is the list of idioms). Behaviour
must not change; **no test assertion changes in phase A, and in phase B only test *code* changes
(assertions, inputs and expected values stay)**. One commit per idiom per phase, each named
"16c A: <idiom>" / "16c B: <idiom>". No pushes except `brief/16c` to `origin` and ONE draft PR.
E2E instances 1..3 are yours.

**Runs after 16a (.NET 11) and 16b (test tooling)**, so a failing suite here is attributable to
this sweep alone. Base: `main` after 16b.

**Owner-visible outcome:** none for readers; the code uses the .NET 11 / C# 15 forms wherever the
skill's table names one, so new code has a modern precedent next to it instead of an older one.

**Why phase A before phase B (owner):** the product code sets the precedent; test code follows.

## Item 0: inventory
Before changing anything, list every site in `src/` and `tests/` that each row of the skill's
table applies to (file:line, idiom, one line of context), with the analyzer-covered rows already
handled by 16a marked "done in 16a". Put the inventory in the PR description and keep the counts;
the final report shows each count reaching zero (or says which sites were deliberately left and
why). Candidates already visible: `src/Croft.Api/Social/AnnounceOutcome.cs` and
`ISocialThreadSource.cs` (abstract record hierarchies → `closed`; every `switch` over them loses
its `_ => throw` default arm, the compiler's exhaustiveness check replaces it);
`OrderByDescending(...).First*()` → `MaxBy`/`MaxByAsync` (EF Core 11 translates it; check the SQL
the way 13e did, one statement, same rows); hand-written `IEqualityComparer<T>` classes
→ `EqualityComparer<T>.Create`; `GroupJoin`+`SelectMany`+`DefaultIfEmpty` → `FullJoin`/`LeftJoin`;
nested-loop `bool` flags → labeled `break`/`continue`; collection construction with a capacity or
a comparer → collection expression arguments; `ComplexProperty(...).Property(...)` →
`Property(e => e.X.Y)`; validation that hits the API inside `IValidatableObject` → async
validation (only if a site exists; otherwise "none"). Unions: only where a value is genuinely one
of several unrelated types (the skill's "Common mistakes"); say per candidate whether it qualifies.

## Phase A: `src/` (Croft.Api, Croft.Shared, Croft.Web)
One commit per idiom, across all three projects. Each commit's message carries the inventory
lines it closes. For every behaviour-sensitive rewrite:
- `closed` hierarchies and the removed default arms: the existing tests that exercise each arm
  stay green; a probe (not committed) that adds a new subtype must fail to compile at every
  switch, which is the point.
- `MaxByAsync`/`FullJoin` in EF queries: capture the SQL before and after (same shape, one
  statement) and the result set on the test data (identical), in the commit message.
- Everything else: the suites.
After phase A: full API and Web suites once (counts identical to `main`'s), E2E once.

## Phase B: `tests/` (Croft.Api.Tests, Croft.Web.Tests, Croft.Web.E2E, dev/screenshot-gate)
The same idioms in test code (helpers, fixtures, fakes, arrangement code), never in what a test
asserts: `Should().Be(...)`/`Assert.*` lines and their expected values are untouched (AGENTS.md
rule 5; a reviewer diffs the assertion lines). One commit per idiom. After phase B: full API and
Web suites, E2E twice, the opt-in `Category=Container|Category=ReadyToRun` (test fixtures touch
them), CSP snapshot unchanged.

## Item: the skill learns from the sweep
Anything the table recommended that turned out worse in practice (less readable, no translation,
an EF 11 limitation) goes into `writing-dotnet11-code/SKILL.md` as a note on that row, with the
file that shows it; a row that found no sites in this codebase gets "(no sites in Croft as of
16c)". The reviewer rule from 16a stays.

## Out of scope
New features, behaviour changes, performance changes beyond what the idiom implies, Blazor
auto-pause or server-triggered pause (brief-level decisions, see the skill), MudBlazor changes,
anything the analyzers already handled in 16a.
