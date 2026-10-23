# Brief 03h: run the test suites in parallel

Read AGENTS.md first and follow it, including "Finishing a task". Requires brief 03g (isolated E2E
instances) accepted. Use `subagent-driven-development` with the items below as the tasks; one
commit per item, then the final whole-branch review.

**Why:** the owner wants the suites to use the machine (2026-10-09). Today the E2E suite is fully
serial (`[assembly: LevelOfParallelism(1)]` and `[Parallelizable(ParallelScope.None)]` on all 15
fixtures, ~11 minutes), and every API test class shares one `[Collection("Postgres")]`, so they run
one after another. The machine is an i7-6700K: **4 cores / 8 threads**, 32 GB RAM. Measure; don't
assume more parallelism is faster.

## Item 1: E2E across N isolated instances

- `dev/run-e2e.sh [--instances N] [-- <dotnet test args>]`: starts instances 1..N with
  `./start-local.sh --e2e --instance i` (03g), waits until all are healthy, runs the E2E suite with
  N NUnit workers, stops every instance it started (also on failure or Ctrl+C), and prints the
  summary line. Default N comes from item 4's measurements.
- NUnit runs **fixtures** in parallel (`ParallelScope.Fixtures`, `LevelOfParallelism(N)`); tests
  inside a fixture stay sequential. Each worker uses its own instance: map the NUnit worker to an
  instance index once per fixture and give the fixture that instance's `E2E_WEB_URL` /
  `E2E_API_URL`. Two fixtures must never share an instance at the same time.
- Anything that is global per instance (the home page's featured post, "page 1 contains X",
  analytics counts) is then safe because only one fixture uses an instance at a time. Anything
  global across instances (the mock IdP, files under the repo) must not be written by tests.
- Screenshot tests and anything else that writes to a shared folder: give each worker its own
  output folder or keep that fixture `NonParallelizable`; say which.
- Evidence: the suite green with N = 1, 2 and 3, twice each.

## Item 2: API tests in parallel

- Keep one Postgres Testcontainer for the whole test run, but give **each test class its own
  database** in it (created at class start from the migrations, dropped at the end), so classes no
  longer need the shared `[Collection("Postgres")]` and xUnit can run them in parallel.
- Any test that relies on another class's data or on run order gets fixed.
- Evidence: the suite green three times in a row; wall time before and after.

## Item 3: Web unit tests

They already run in parallel (xUnit's default across classes). Check for shared static state that
would make that unsafe (static caches, `CultureInfo.DefaultThreadCurrentCulture` from `AppCulture`,
static fakes); fix any you find so the suite stays order-independent. Evidence: green five times in
a row with xUnit's maximum parallel threads raised to 8.

## Item 4: measure and choose

On this machine, from a fresh start each time, record wall time, peak memory of all `Blog.Api` and
`Blog.Web` processes together, and peak CPU for E2E with N = 1, 2, 3 (and 4 if 3 is still faster).
Pick the default N as the smallest N within ~10% of the best time. Put the table in the report and
the chosen N in `dev/run-e2e.sh` and AGENTS.md.

## Item 5: everyone uses it

- AGENTS.md: agents run E2E with `dev/run-e2e.sh`.
- `dev/opencode-sandbox.sh --verify`: run E2E through `dev/run-e2e.sh` (the owner reinstalls the
  root-owned copy afterwards; say so in the report).

## Out of scope

The slow "render every row" pages, CI, anything else.
