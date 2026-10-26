#!/usr/bin/env bash
# Run one k6 script from this folder in a container and summarise it.
#
#   BASE=https://blog.example RATES=20,40,80 loadtest/run.sh http-ramp.js ramp-plain
#
# Writes results/<name>.csv, results/<name>-summary.json, results/<name>.log and
# results/<name>-steps.txt (summarise.py) under this folder. Passes BASE, PAGES, RATES, STEP, ENC,
# ASSETS, PREVUS and MAXVUS through to k6. Uses podman, or docker when podman is missing.
# Run it from a machine outside the cluster under test, and check the cluster is idle first.
set -euo pipefail

[ $# -eq 2 ] || { sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'; exit 2; }
script="$1" name="$2"
here="$(cd "$(dirname "$0")" && pwd)"
[ -f "$here/$script" ] || { echo "no script $here/$script" >&2; exit 2; }
: "${BASE:?set BASE, e.g. BASE=https://blog.example}"
engine="$(command -v podman || command -v docker)" || { echo "needs podman or docker" >&2; exit 2; }

mkdir -p "$here/results"
# The k6 container reads the asset list as its own user: a file written with a tight mode is "permission denied".
[ -f "$here/${ASSETS:-assets.json}" ] && chmod a+r "$here/${ASSETS:-assets.json}"
: > "$here/results/$name.csv"; : > "$here/results/$name-summary.json"
chmod a+w "$here/results/$name.csv" "$here/results/$name-summary.json"

env_args=()
for v in BASE PAGES RATES STEP ENC ASSETS PREVUS MAXVUS; do
    [ -n "${!v:-}" ] && env_args+=(-e "$v=${!v}")
done
userns=(); [ "$(basename "$engine")" = podman ] && userns=(--userns=keep-id)
# No :Z on the mount (it would relabel the repository folder): the k6 container runs unlabelled instead.
label=(--security-opt label=disable)

echo "$name: $script against $BASE, started $(date +%T)"
status=0
"$engine" run --rm -i "${userns[@]}" "${label[@]}" -v "$here:/lt" -w /lt "${env_args[@]}" docker.io/grafana/k6 run --quiet \
    --out "csv=/lt/results/$name.csv" --summary-export "/lt/results/$name-summary.json" "/lt/$script" \
    > "$here/results/$name.log" 2>&1 || status=$?
echo "$name: k6 exited $status at $(date +%T) (a threshold abort exits 99)"
python3 "$here/summarise.py" "$here/results/$name.csv" > "$here/results/$name-steps.txt"
tail -n 5 "$here/results/$name-steps.txt"
