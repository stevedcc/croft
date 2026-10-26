# Brief 17b: the dev scripts cannot false-pass and cannot run two suites at once (issues #72, #73)

Read AGENTS.md first and follow it. One commit per item. Scripts are not unit-testable, so every
item shows before/after evidence from running the thing (rule 1). No pushes except `brief/17b` to
`origin` and ONE draft PR. E2E instances 10..11 are yours
(`dev/run-e2e.sh --instances 2 --first-instance 10 -- --filter "FullyQualifiedName~WarmupTests"`
is a 1-minute run that exercises the whole script; use filters, never the full suite, for
evidence). Other briefs run at the same time: `pgrep -af "dotnet test|run-e2e"` (excluding your
own shell) before every run; never two full suites at once.

## Item 1 (#72): `dev/run-e2e.sh` fails unless the run's summary line was seen

On 2026-10-25 (swap full, OOM kills) `run-e2e.sh` logged `dotnet test exit code 0` at 742 s and
stopped its instances while NUnit kept running and later wrote `Failed! 88, Passed 600`. In CI a
0 is a pass. `dev/run-e2e.sh:215-222` runs `dotnet test … || TEST_RC=$?` with no log; output goes
to the terminal only.

1. Tee the run: `dotnet test … 2>&1 | tee "$RUN_LOG"` with `RUN_LOG=/tmp/blog-e2e-run-${FIRST_INSTANCE}-${LAST_INSTANCE}.log`
   (CI already uploads `/tmp/blog-e2e-*.log`, `.github/workflows/ci.yml:178`) and
   `TEST_RC=${PIPESTATUS[0]}` under `set +e` / `set -e` as `dev/run-large-data-e2e.sh:108-113`
   does. Mind `set -o pipefail` (:41).
2. After the run: require exactly one NUnit summary line in the log (`^\s*(Passed|Failed)!\s+-\s+Failed:\s+\d+`,
   the line `dotnet test` prints per test assembly). None: `TEST_RC=70` with the message
   `run-e2e: dotnet test exited $TEST_RC but printed no test summary; the run did not finish`.
   More than one: 71 (two assemblies would mean the filter is wrong). A summary with
   `Failed:\s+[1-9]` and exit code 0: 72 (`the summary reports failures but dotnet test exited 0`).
   Print the summary line itself in the "E2E run finished" line.
3. Same guard in `dev/run-ha-e2e.sh:67-72` and `dev/run-large-data-e2e.sh:108-113` (the latter
   already tees): apply the pattern through one sourced helper
   (`dev/lib/test-summary.sh`, `require_test_summary <log> <rc>` prints and returns the code),
   not three copies.
4. Investigate the 2026-10-25 shape as far as the evidence allows: can `dotnet test` (Test.Sdk 18,
   NUnit3TestAdapter 6) return before the run ends when the test host is OOM-killed or the
   adapter loses the host? Read the vstest console runner's behaviour on a lost test host (the
   `TestHostProcessExited`/"The active test run was aborted" path) and say in the commit what
   exit code it gives; if it is 0 in any path, the guard above is the fix; if it is never 0, say
   what else could have produced the log the issue describes.

Evidence: (a) a normal filtered run showing the new "finished" line with the summary; (b)
break-it-once: a `dotnet` shim first on `PATH` (in `/tmp`, not committed) that prints a few lines
and exits 0 without a summary: the script must exit 70 with the message; (c) the shim printing
`Failed!  - Failed: 3, Passed: 5, …` and exiting 0: exit 72.

## Item 2 (#73): one suite at a time on this machine

Twice in two days two agents ran full suites concurrently on the dev box and pushed it into swap
(2026-10-23) and OOM kills (2026-10-25, the run of item 1). The instruction "check pgrep first"
did not hold; make it a mechanism.

1. `dev/with-suite-lock.sh <command…>`: takes `flock` on
   `${XDG_CACHE_HOME:-$HOME/.cache}/croft/suite.lock` (create the directory), then runs the
   command with the lock held, exit code passed through. While waiting it prints once
   `with-suite-lock: another suite is running (pid <p>, since <time>, command <c>); waiting …`
   from a sidecar file `suite.lock.owner` the holder writes (`pid|start-time|command`) and
   removes on exit (trap). Re-entrancy: if `CROFT_SUITE_LOCK_HELD=1` is set, run the command
   without locking (so a locked `run-e2e.sh` can call itself for the second and third passes,
   `dev/run-e2e.sh:224-249`, and a locked script can call `start-local.sh`, which builds).
   `--try` exits 75 at once instead of waiting (for callers that must not block). CI: the lock is
   per machine, so it is harmless there; do not special-case `CI`.
2. `dev/run-e2e.sh`, `dev/run-ha-e2e.sh`, `dev/run-large-data-e2e.sh` take the lock around their
   whole body (re-exec under `with-suite-lock.sh` at the top unless `CROFT_SUITE_LOCK_HELD=1`).
3. AGENTS.md "Running things" (:132-138 and the opt-in commands at :157, :170-171): the three
   `dotnet test` commands become `dev/with-suite-lock.sh dotnet test …`, one sentence saying why
   (two suites at once have twice taken the machine down; the lock serialises them per machine),
   and the "check pgrep first" sentence in "Test cadence" (:152 area: "Never run suites on an
   instance another agent is using") gains "the lock serialises suites; `pgrep` is no longer
   needed". Brief 17c edits the "Pending contracts" section of AGENTS.md at the same time; the
   sections do not overlap.
4. README: if the README lists the test commands (grep `dotnet test`), update them the same way.

Evidence: two filtered runs started within a second of each other in two shells
(`dev/run-e2e.sh --instances 1 --first-instance 10 -- --filter …` and the same on 11): the second
prints the waiting line with the first's pid and starts only after the first's "finished" line
(timestamps in the report); `--try` while the first runs exits 75; a run with
`CROFT_SUITE_LOCK_HELD=1` does not wait (the recursive passes work: one `run-e2e.sh` run with no
mode flag and a filter completes all three passes under one lock).

## Item 3: evidence commit
Run `dev/run-e2e.sh --instances 2 --first-instance 10 -- --filter "FullyQualifiedName~WarmupTests"`
once more with everything in place; its complete output goes into this item's commit message
(an empty commit). No other suite is needed: no `src/` or test code changed.

## Report
Per item: what changed, the evidence runs (verbatim lines with timestamps), what item 1.4 found.
The PR's `ci` run URL (`gh pr checks --watch`: CI runs `dev/run-e2e.sh --instances 2`, which now
goes through the lock and the summary guard; that run is the proof CI is unaffected). What is
left undone.

## Out of scope
The IdP readiness gate that brief 17a adds to `run-e2e.sh` after :157 (different lines; merge
when the orchestrator says). Test code. The screenshot-gate scripts.
