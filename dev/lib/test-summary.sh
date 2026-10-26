#!/usr/bin/env bash
# Sourced by dev/run-e2e.sh, dev/run-ha-e2e.sh and dev/run-large-data-e2e.sh.
#
# require_test_summary <log> <rc>
#   A `dotnet test` exit code of 0 is only believed when the run's log holds exactly one NUnit
#   summary line ("Passed!  - Failed: 0, Passed: 5, ..." / "Failed!  - Failed: 3, ..."), which
#   `dotnet test` prints once per test assembly when the run ends (issue #72: on 2026-10-25 a run
#   logged exit code 0 while NUnit was still running). Sets TEST_SUMMARY_LINE to the summary line
#   (empty when there is not exactly one), prints the reason to stderr and returns the code the
#   caller should exit with:
#     rc                  the summary is there and agrees with rc (including a non-zero rc)
#     70                  no summary line: the run did not finish
#     71                  more than one summary line (two assemblies: the filter is wrong)
#     72                  the summary reports failures but dotnet test exited 0
#   A non-zero rc with a missing or doubled summary keeps rc (the failure is already visible) but
#   still says why.
TEST_SUMMARY_PATTERN='^[[:space:]]*(Passed|Failed)!?[[:space:]]+-[[:space:]]+Failed:[[:space:]]+[0-9]+'

require_test_summary() {
    local log="$1" rc="$2" count
    TEST_SUMMARY_LINE=""
    count="$(grep -cE "$TEST_SUMMARY_PATTERN" "$log" 2>/dev/null || true)"
    count="${count:-0}"
    if [ "$count" -eq 0 ]; then
        echo "run-e2e: dotnet test exited $rc but printed no test summary; the run did not finish" >&2
        [ "$rc" -eq 0 ] && return 70
        return "$rc"
    fi
    if [ "$count" -gt 1 ]; then
        echo "run-e2e: dotnet test printed $count test summaries (expected one; is the filter selecting two assemblies?)" >&2
        [ "$rc" -eq 0 ] && return 71
        return "$rc"
    fi
    TEST_SUMMARY_LINE="$(grep -E "$TEST_SUMMARY_PATTERN" "$log" | sed 's/^[[:space:]]*//')"
    if [ "$rc" -eq 0 ] && printf '%s\n' "$TEST_SUMMARY_LINE" | grep -qE 'Failed:[[:space:]]+[1-9]'; then
        echo "run-e2e: the summary reports failures but dotnet test exited 0" >&2
        return 72
    fi
    return "$rc"
}
