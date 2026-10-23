# Brief 10e: pods start reliably and warm up fast

Read AGENTS.md first and follow it. Implement inline (`executing-plans`), one commit per item,
TDD, break-it-once for each key test, E2E twice at the end, the final whole-branch review.

**Owner-visible outcome:**
- A database restart can no longer leave Croft pods stuck forever: the `migrate` step retries,
  and if the database stays away it fails and Kubernetes restarts it.
- New pods reach full speed much sooner, because the images ship precompiled (ReadyToRun) code.

## Item 1: `migrate` retries, then exits with an error; it never hangs

**Evidence (production, 2026-10-17 to 18):**
- During a CloudNativePG restart at about 16:20 UTC+2, the `migrate` init containers of a
  croft-web and a croft-api pod got `Npgsql.NpgsqlException: Failed to connect to …:5432 ---> SocketException (111): Connection refused`.
- They logged `Unhandled exception. System.InvalidOperationException: An exception has been raised that is likely due to a transient failure.`
  (from `BackgroundJobLock.RunAsync` → `Program.cs` line ~302), **and then never exited**. The
  container stayed `Running` with 0 restarts for 19–32 hours, so the pods stayed in `Init:0/1`,
  and the site ran on half its pods until the owner deleted them.

**Cause (a hypothesis; confirm it first and record the evidence in the commit):**
- In the image, `dotnet` is PID 1 (`ENTRYPOINT ["dotnet", …]`, no init process).
- An unhandled exception makes the runtime call `abort()`, which raises `SIGABRT`. The kernel
  doesn't deliver signals with the default action to a namespace's PID 1, so the process may
  never die.
- **Confirm it** with a Testcontainers test that runs the built image (or `mcr.microsoft.com/dotnet/aspnet:10.0`
  with the published output mounted) with `migrate` and an unreachable database:
  - as PID 1, the container doesn't exit within 60 s;
  - with an init process, or with the fix below, it exits.
- If the hypothesis is wrong, stop and report what you found before fixing anything.

**Do:**
- **`migrate` in both Croft.Api and Croft.Web never ends in an unhandled exception:**
  - it retries connection failures (the transient errors Npgsql and EF classify as transient)
    with exponential backoff: 1, 2, 4, 8, 16, 30, 30 … seconds;
  - it stops after `Database:MigrateTimeout`, a new validated setting defaulting to 5 minutes;
  - then it logs one error with the last exception, sets the exit code to 1, and returns
    normally;
  - a non-transient failure (a migration that fails) is not retried: it logs, sets exit code 1
    and returns.
  - Each retry logs one Warning line with the attempt number and the delay.
- **The images also defend against any other unhandled exception at PID 1:** check whether the
  Kubernetes deployments, or the Dockerfiles, run an init process (`tini`, or the deployment's
  `shareProcessNamespace`).
  - The Dockerfiles are in this repo; the k8s manifests in `k8s/` may be the framework's
    samples. Fix the repo's files, and list in your report what the site's deployments need,
    because the site repository isn't yours.
  - The preferred fix is in the image, so it travels with Croft: install `tini` from the
    distribution's packages in the final stage and use `ENTRYPOINT ["tini", "--", "dotnet", …]`.
    If the aspnet image doesn't have `tini`, use `dotnet`'s own handling: verify whether
    `DOTNET_DbgEnableMiniDump`, `COMPlus_` settings or `Environment.Exit` change the PID 1
    behaviour, and pick the smallest reliable option. Rule it, citing the test.
  - The fix must keep `USER $APP_UID` and the W-082 block.

**Tests:**
- **Unit or integration (no container):** the retry policy with a fake clock:
  - backoff delays in order;
  - stops at the timeout with exit code 1;
  - a non-transient failure exits at once with exit code 1;
  - success on the third attempt exits 0 after exactly two warnings.
- **Container (Testcontainers):** the image, run with `migrate` against an address that refuses
  connections and `Database:MigrateTimeout=00:00:10`, exits with code 1 within 30 s.
- **Container:** an unhandled exception at PID 1, for example a malformed branding folder for
  Web's normal start, also exits within 30 s.
- **Break-it-once:** remove the init process or exit handling; the second container test must
  hang and time out (assert the timeout). Remove the retry loop; the backoff test must fail.
- If the container tests are slow, mark them with the category the long tests already use, so
  the default run can skip them, and say in the report how to run them.

## Item 2: the images ship ReadyToRun code

**Evidence:**
- A new Web pod burns 1.2–1.5 cores for about 5 minutes after taking load, because the tiered
  JIT recompiles the hot paths (load test 2026-10-16, results README finding 3). That's why an
  autoscaler made things worse under load.
- .NET 10's ReadyToRun target for Linux arm64 is `armv8.0-a`, which the cluster's Raspberry Pi 4
  (Cortex-A72) supports.
- Note for the future: .NET 11 raises that target to `armv8.0-a + LSE`, so on the Pi 4, R2R code
  would be rejected and JIT-compiled.

**Do:**
- In both Dockerfiles, publish with `-p:PublishReadyToRun=true` for the target architecture.
  crossgen2 cross-compiles from the build platform, so the `--platform=$BUILDPLATFORM` build stage
  stays as it is.
- Keep the build reproducible: the R2R compiler package comes from the SDK's normal restore,
  with no new feeds.
- Add a **workaround-register entry** (`docs/workarounds.md`, next free ID) with markers in both
  Dockerfiles:
  - "The ReadyToRun target is armv8.0-a on .NET 10. **Upgrade check for .NET 11:** the default
    target becomes armv8.0-a + LSE, which the Raspberry Pi 4 lacks. Either pin crossgen2's
    instruction set to armv8-a (find the property; record it), or accept the JIT fallback."
  - The register's sync test must stay green.
- Record the image sizes before and after for both images in the commit message
  (`podman image inspect` or `docker image inspect`; build only for linux/arm64 and
  linux/amd64 as the Dockerfile already allows).

**Tests:**
- **A check that the published assemblies are ReadyToRun:**
  - publish `Croft.Web` and `Croft.Api` for `linux-arm64` with the same flags as the Dockerfile;
  - open `Croft.Web.dll`, `Croft.Api.dll` and `Croft.Shared.dll` with `System.Reflection.PortableExecutable.PEReader`;
  - assert that each has a ReadyToRun header (the CorHeader's `ManagedNativeHeaderDirectory` is
    non-empty, and the machine type is arm64).
  - Put it under `dev/` as a script, or as a test in the long-tests category; the publish step is
    slow.
- **Break-it-once:** publish without the flag; the check must fail.
- The full suites stay green. R2R changes no behaviour.

## Out of scope

- Re-enabling the autoscaler. The orchestrator measures the cold-start cost on the cluster after
  deployment, and the owner decides.
- Native AOT. Blazor Server doesn't support it.
- The site repository's manifests (list what they need in your report).
