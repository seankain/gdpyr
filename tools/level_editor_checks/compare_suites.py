#!/usr/bin/env python3
"""Is the scenario suite on one ground within its own spread on another? (docs/LEVEL_EDITOR.md §11, LE0)

    python3 tools/level_editor_checks/compare_suites.py --base slab1.txt slab2.txt --against flat1.txt flat2.txt

Each file is what scripts/playtest.sh printed for the whole suite. The suite is not the same twice
on one ground -- its scenarios run bots, and some of its claims are near their edges -- so one run
says little. The base runs give each scenario the verdicts it has been seen to have, and each claim
the values it has been seen to take; a run against them is inside the spread where its verdict is
one of those and its values lie between theirs. Exits 1 when a scenario is outside, 2 on a file it
cannot read as a suite.
"""
import argparse
import re
import sys

SCENARIO = re.compile(r'^(?P<name>\S.*?) \.{2,} (?P<verdict>PASS|FAIL)\s+\(')
CLAIM = re.compile(r'^  (?P<claim>\S.*?)\s+(?P<value>-?[\d.]+(?:\.\.-?[\d.]+)?|\d+/\d+ seeds)\s+(?P<rest>.*)$')


def numbers(value):
    """A claim's value as (low, high): '3..5', '0', '1/4 seeds'."""
    if value.endswith('seeds'):
        hit = float(value.split('/')[0])
        return hit, hit
    parts = value.split('..')
    try:
        # A range's upper end may run on from a negative lower one: '-65552..-65552'.
        return float(parts[0]), float(parts[-1])
    except ValueError:
        return None


def read(path):
    suite, current = {}, None
    for line in open(path, encoding='utf-8', errors='replace'):
        line = line.rstrip('\n')
        scenario = SCENARIO.match(line)
        if scenario:
            current = suite.setdefault(scenario['name'], {'verdict': scenario['verdict'], 'claims': {}})
            continue
        claim = CLAIM.match(line) if current is not None else None
        if claim and not claim['claim'].startswith('traces:'):
            current['claims'][claim['claim']] = (numbers(claim['value']), 'FAILED' not in claim['rest'])
    if not suite:
        sys.exit(f"{path}: no scenario in it")
    return suite


def main():
    parser = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    parser.add_argument('--base', nargs='+', required=True)
    parser.add_argument('--against', nargs='+', required=True)
    args = parser.parse_args()
    base = [read(path) for path in args.base]
    against = [read(path) for path in args.against]

    names = sorted(set().union(*(run.keys() for run in base + against)))
    for label, runs in (('base', base), ('against', against)):
        for path, run in zip(args.base if label == 'base' else args.against, runs):
            passed = sum(1 for s in run.values() if s['verdict'] == 'PASS')
            print(f"{label:8} {path}: {passed} of {len(run)} pass")

    outside, differ = [], []
    for name in names:
        seen = {run[name]['verdict'] for run in base if name in run}
        got = [run[name]['verdict'] if name in run else 'absent' for run in against]
        if not seen or any(verdict not in seen for verdict in got):
            outside.append(f"{name[:90]}: base {'/'.join(sorted(seen)) or 'absent'}, against {'/'.join(got)}")
            continue
        if len(seen) > 1 or len(set(got)) > 1:
            differ.append(f"{name[:90]}: base {'/'.join(run[name]['verdict'] for run in base if name in run)}, against {'/'.join(got)}")

        # Every claim's values, where the base has them as numbers: inside the base's own range?
        for claim in sorted(set().union(*(run[name]['claims'].keys() for run in against if name in run))):
            spans = [run[name]['claims'][claim][0] for run in base if name in run and claim in run[name]['claims']]
            spans = [span for span in spans if span]
            if not spans:
                continue
            low, high = min(span[0] for span in spans), max(span[1] for span in spans)
            for run in against:
                value = run.get(name, {}).get('claims', {}).get(claim, (None, True))[0]
                if value and (value[0] < low or value[1] > high):
                    outside.append(f"{name[:70]}: `{claim}` {value[0]:g}..{value[1]:g}, base {low:g}..{high:g}")

    print(f"\n{len(names)} scenarios; {len(differ)} differ between runs of one ground or the other, inside the base's spread:")
    for line in differ:
        print(f"  {line}")
    print(f"\n{len(outside)} outside the base's spread:")
    for line in outside:
        print(f"  {line}")
    return 1 if outside else 0


if __name__ == "__main__":
    sys.exit(main())
