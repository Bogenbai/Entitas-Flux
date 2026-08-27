#!/usr/bin/env bash
# Runs the same benchmarks against this fork and against the upstream Entitas it forked
# from, then prints them side by side.
#
# Both ship an assembly called Entitas.dll, so they cannot live in one process; the
# benchmark code is compiled twice instead (-p:UseUpstream=true) and the two reports are
# compared. Read allocations first: they are deterministic, while timings from a busy
# machine move by tens of percent between runs.
#
# Usage: ./benchmarks/compare-with-upstream.sh [benchmark filter]
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$SCRIPT_DIR/Entitas.Benchmarks"
FILTER="${1:-*CoreBenchmarks*}"
OUT="$SCRIPT_DIR/.compare"

rm -rf "$OUT"

echo "== upstream Entitas 1.14.1 =="
dotnet run -c Release --project "$PROJECT" -p:UseUpstream=true -- --filter "$FILTER" --artifacts "$OUT/upstream" >/dev/null

echo "== Entitas-Flux (this repo) =="
dotnet run -c Release --project "$PROJECT" -- --filter "$FILTER" --artifacts "$OUT/fork" >/dev/null

python3 - "$OUT" <<'PY'
import glob, os, re, sys

def read(root):
    rows = {}
    for path in glob.glob(os.path.join(root, "results", "*-report-github.md")):
        for line in open(path):
            cells = [c.strip() for c in line.strip().strip("|").split("|")]
            if len(cells) < 3 or cells[0] in ("Method", "") or set(cells[0]) <= set("-: "):
                continue
            rows[cells[0]] = (cells[1], cells[-1])
    return rows

out = sys.argv[1]
upstream, fork = read(os.path.join(out, "upstream")), read(os.path.join(out, "fork"))

def number(value):
    digits = re.sub(r"[^\d.]", "", value.replace(",", ""))
    return float(digits) if digits else 0.0

def ratio(new, old):
    a, b = number(new), number(old)
    return f"{a / b:.2f}x" if b else "—"

print()
print(f"{'Benchmark':<28}{'upstream':>14}{'flux':>14}{'':>3}{'alloc up':>12}{'alloc flux':>12}{'':>3}")
print("-" * 90)
for name in upstream:
    if name not in fork:
        continue
    up_time, up_alloc = upstream[name]
    fx_time, fx_alloc = fork[name]
    print(f"{name:<28}{up_time:>14}{fx_time:>14}{ratio(fx_time, up_time):>8}"
          f"{up_alloc:>12}{fx_alloc:>12}{ratio(fx_alloc, up_alloc):>8}")
print()
print("Ratios below 1.00x mean this fork is faster / allocates less.")
PY
