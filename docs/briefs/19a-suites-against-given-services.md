# Brief 19a: the suites run against a Postgres and a mock IdP they are given

Read AGENTS.md first and follow it. One commit per item, TDD where there is code to test,
before/after evidence for scripts. No pushes except your branch `brief/19a` to `origin` and ONE
draft PR. Every suite goes through `dev/with-suite-lock.sh`. Item 2 and 3 change the E2E suite and
its scripts, so the E2E suite runs at the end (`dev/run-e2e.sh`, all three passes), plus the
"given services" run in Item 3.

No other brief is running. This brief adds **no migration** and changes nothing in `src/`.

## Why

Chargehand's new verify lane (chargehand PR #23) runs Croft's suites in a container that has **no
container runtime**: no Podman socket, no `docker compose`. Postgres and the mock IdP run beside it as
separate containers, reachable by host name (for example `Host=db` and `http://idp:8080/default`).
Today the suites can't use them:

- **API and Web unit suites:** `SharedPostgresContainer.AcquireAsync`
  (`tests/Croft.Api.Tests/Fixtures/SharedPostgresContainer.cs:21-46`, and the Web copy in
  `tests/Croft.Web.Tests/Fixtures/`) always starts a Testcontainer. There is no way to hand it a server.
- **E2E:** `start-local.sh:45-52` exports every `.env` line over whatever is already in the
  environment, so an exported `ConnectionStrings__BlogDb` or `Oidc__Authority` is overwritten.
  `dev/run-e2e.sh:307-313` does the same for the three `Oidc__*` keys.
  `tests/Croft.Web.E2E/WebTest.cs:103-106` and `:182-185` read the connection string from `.env` only.
- **The IdP address is hard-coded** in five places, apart from `.env`:
  - `start-local.sh:56` and `dev/run-e2e.sh:156-164`, `:180`;
  - `tests/Croft.Web.E2E/WebTest.cs:21` (`OAuthAuthority`);
  - `tests/Croft.Web.E2E/MockIdpApi.cs:16` (`OAuthAuthority`).

  `dev/run-e2e.sh` also runs `docker compose up -d` when that address doesn't answer.

Nothing changes for the owner's normal runs: with nothing exported, every value is what it is today.

## Item 1: the unit suites take a given Postgres (`CROFT_TEST_POSTGRES`)

When the environment variable `CROFT_TEST_POSTGRES` is set and not empty, `SharedPostgresContainer`
starts **no** container:
- `AcquireAsync` returns that connection string, and the lease counting stays as it is.
- `ReleaseAsync` disposes nothing.

`PostgresDatabaseFixture` is unchanged. It still creates `blog_class_<guid>` on that server, migrates
it, and force-drops it at class end, so it only ever drops databases it created. When the variable is
unset or empty, the behaviour is exactly today's.

- Put the choice in one small static method both paths go through, testable without the
  environment. For example, `internal static string? GivenServer(string? value)` returns the trimmed
  value, or null when it is null or whitespace. `AcquireAsync` calls it with
  `Environment.GetEnvironmentVariable("CROFT_TEST_POSTGRES")`.
- Do the same in both copies (API and Web). Keep the `WORKAROUND-FILE(W-062, …)` marker line. Add a
  sentence to the class doc comment saying what the variable does and that the lane sets it.
- Tests: in each suite, a test class for `GivenServer` covering null, `""`, `"  "`, and a real
  connection string, which must come back unchanged except for trimming.
  - These classes must not touch a database, so they carry no `Category=Container` label.
  - The Web suite's guard test checks that label against `PostgresDatabaseFixture` users; make sure
    it still passes.
- Evidence (before/after, in the commit message):
  1. `docker compose up -d` (allowed; Postgres on `localhost:5432`, user `blog`).
  2. Run the API suite and the Web suite with
     `CROFT_TEST_POSTGRES='Host=localhost;Port=5432;Database=postgres;Username=blog;Password=blog'`.
  3. While each run is going, `podman ps --filter label=org.testcontainers=true` must show no
     Postgres container. Before the change it shows one.
  4. After the runs, there must be no `blog_class_%` databases left on that server. Show the count:
     `psql` if it is installed, or a one-off `dotnet` snippet, but not a container command.

  Then run both suites once more without the variable and show that they still start their
  Testcontainer.

Break-it-once: make `GivenServer` always return null. The new unit tests for a real connection
string must fail. Revert.

## Item 2: a value in the environment wins over `.env`

One rule everywhere: **a variable that is already set and not empty in the environment is not
overwritten by `.env`**. `.env` only fills what is missing.

- `start-local.sh:45-52`: skip a key whose variable is already set and not empty. Note that `.env`
  repeats `Oidc__Authority` (in the API and Web blocks): a repeat of a key `.env` itself just set must
  still be allowed. Take care to tell "set before the script started" apart from "set by an earlier
  `.env` line". Comment the rule in one line.
- `dev/run-e2e.sh:307-313`: the same rule for the three `Oidc__*` keys.
- `tests/Croft.Web.E2E/WebTest.cs:103-106` and `:182-185`: one helper. For example,
  `static string BlogDbConnectionString()` returns `ConnectionStrings__BlogDb` from the environment
  when set, otherwise from `.env` as today. Both call sites use it, which also removes the duplicated
  `.env` reading.
- Evidence: say in the commit message how you showed the precedence. A unit test in the E2E project
  for the helper is fine if it doesn't need a running instance. Its full proof is Item 3's run.

## Item 3: one IdP address, `Oidc__Authority`

The mock IdP's address comes from `Oidc__Authority`: from the environment, else from `.env`, else
the default `http://localhost:5556/default`. The default is in today's `.env.example`, so a fresh
clone behaves as it does now.
- `start-local.sh:56-60`: check `$Oidc__Authority/.well-known/openid-configuration`, and print that
  address in the error and in the summary at `:251`.
- `dev/run-e2e.sh`:
  - the reachability check (`:156-164`) and `MOCK_AUTHORITY` (`:180`) use that address;
  - **`docker compose up -d` runs only when the address is the default.** A given address that
    doesn't answer is an error (exit 1, naming the address), never a compose start;
  - the `docker compose logs` hint at `:229-230` is printed only for the default address too.
- `tests/Croft.Web.E2E/WebTest.cs:21` and `MockIdpApi.cs:16`: `OAuthAuthority` reads
  `Oidc__Authority` from the environment, falling back to `http://localhost:5556/default`. These
  become `static readonly` fields or properties instead of `const`; fix the uses that need a
  constant.
  - `dev/run-e2e.sh` already exports `Oidc__Authority` to the test process (`:305-313`).
  - Check `CircuitAdmissionTests.cs:71-75`, which passes the same keys on.
- Leave alone: `HaTests.cs`, `dev/ha-local.sh` and the large-data scripts (they have their own IdP
  setup), and `.github/workflows/`.

**The given-services run.** This is the evidence for Items 2 and 3 together. It must fail before
your changes and pass after:

1. In your clone, set `.env`'s `ConnectionStrings__BlogDb` to `Host=nowhere.invalid;…` and both of
   its `Oidc__Authority` lines to `http://nowhere.invalid:5556/default`. Keep a copy and restore it
   afterwards.
2. Export:
   - `ConnectionStrings__BlogDb='Host=127.0.0.1;Database=blog;Username=blog;Password=blog'`;
   - `Oidc__Authority=http://127.0.0.1:5556/default`.
3. Run `dev/run-e2e.sh --instances 2`.

   The suite must pass. Every value now comes from the environment, and the IdP's issuer is
   `http://127.0.0.1:5556/default`. Any place still hard-coding `localhost:5556`, for example
   `MockIdpApi`'s back-channel logout token, then fails on an issuer mismatch, and any place still
   reading `.env` fails on `nowhere.invalid`. Record both the failing run (before) and the passing
   run (after), with their summary lines, in the Item 3 commit message.

Then restore `.env` and run the full `dev/run-e2e.sh` (default instances, all three passes), plus
the API and Web suites, as the final evidence.

Break-it-once: put `http://localhost:5556/default` back as a `const` in `MockIdpApi.cs` only, and
rerun just `BackchannelLogoutTests` in the given-services setup. It must fail. Revert.

## Item 4: AGENTS.md

In "Running things", after the "Without a container runtime" paragraph, add a short paragraph called
**"Against given services"**. It should say:
- `CROFT_TEST_POSTGRES` points the API and Web suites at an existing Postgres. They then start no
  container; each class creates and drops its own `blog_class_*` database.
- For E2E, export `ConnectionStrings__BlogDb` and `Oidc__Authority`. Exported values win over
  `.env`, and `dev/run-e2e.sh` starts `docker compose` only for the default IdP address.
- Chargehand's verify lane runs the suites this way.

Keep it to five lines or fewer.

## Out of scope

- Chargehand's lane config (`lane.json`), the runner image and the services' own setup. That is the
  orchestrator's job.
- The opt-in `Category=Container` and `Category=ReadyToRun` API tests: they build images and stay
  local-only.
- CI (`.github/workflows/`), the HA and large-data E2E setups, and `compose.yaml`.
- Any change to `src/`.
