"""Summarise a k6 CSV (`--out csv=...`) in 30-second buckets: arrival rate, p50/p95/p99 latency,
failure rate and MB/s, split into pages and assets when the requests carry a `kind` tag (both
scripts here set it).

    python3 loadtest/summarise.py results/ramp-plain.csv
"""
import collections
import csv
import sys

BUCKET = 30


def quantile(values: list[float], q: float) -> float:
    if not values:
        return 0.0
    values = sorted(values)
    return values[min(len(values) - 1, int(len(values) * q))]


def main(path: str) -> None:
    durations = collections.defaultdict(lambda: collections.defaultdict(list))
    requests = collections.defaultdict(collections.Counter)
    failures = collections.defaultdict(collections.Counter)
    received = collections.Counter()
    start = None
    for row in csv.DictReader(open(path, newline="")):
        t = int(float(row["timestamp"]))
        start = start if start is not None else t
        bucket = (t - start) // BUCKET
        tags = dict(part.split("=", 1) for part in row.get("extra_tags", "").split("&") if "=" in part)
        kind = tags.get("kind", "request")
        metric = row["metric_name"]
        if metric == "http_req_duration":
            durations[bucket][kind].append(float(row["metric_value"]))
        elif metric == "http_req_failed":
            requests[bucket][kind] += 1
            failures[bucket][kind] += float(row["metric_value"]) == 1.0
        elif metric == "data_received":
            received[bucket] += float(row["metric_value"])

    kinds = sorted({k for b in durations.values() for k in b}, key=lambda k: (k != "page", k))
    for bucket in sorted(durations):
        parts = [f"+{bucket * BUCKET:5d}s"]
        for kind in kinds:
            values = durations[bucket][kind]
            n = requests[bucket][kind]
            if not n:
                continue
            fail = 100 * failures[bucket][kind] / n
            parts.append(
                f"{kind} {n / BUCKET:6.1f}/s p50 {quantile(values, .5):6.0f} p95 {quantile(values, .95):6.0f}"
                f" p99 {quantile(values, .99):6.0f} ms fail {fail:5.1f}%"
            )
        parts.append(f"{received[bucket] / BUCKET / 1e6:6.1f} MB/s")
        print(" | ".join(parts))


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    main(sys.argv[1])
