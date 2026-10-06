#!/usr/bin/env python3
"""Turns the per-suite xUnit result files into markdown for the run's summary page.

Usage: slowest-tests.py <folder with one <suite>.xml per suite>   (markdown goes to stdout)

The files come from `-result-xml`, which writes one <test name= type= method= time= result=>
element per test (a theory row counts as a test), `time` being seconds. This is reporting only:
whatever is missing, half written or unreadable is skipped, a note says so, and the exit code is
always 0, so a problem here can never turn a green run red. The suite's own exit code decides that.
"""

import glob
import os
import sys
import xml.etree.ElementTree as ET

TOP_TESTS = 40
TOP_CLASSES = 15
MAX_NAME = 140


def read_tests(path):
    """Every (class, name, seconds, result) in one result file; what was readable if it is cut off."""
    tests = []
    note = None
    try:
        for _, element in ET.iterparse(path, events=("end",)):
            if element.tag != "test":
                continue
            try:
                seconds = float(element.get("time", "0") or 0)
            except ValueError:
                seconds = 0.0
            tests.append((element.get("type") or "", element.get("name") or "", seconds, element.get("result") or ""))
            element.clear()
    except ET.ParseError as error:
        note = f"unreadable after {len(tests)} tests ({error})"
    return tests, note


def clean(text, suite):
    """A name for a table cell: the suite's namespace dropped, one line, no pipes, not too long."""
    prefix = suite + "."
    if text.startswith(prefix):
        text = text[len(prefix):]
    text = " ".join(text.split()).replace("|", "\\|")
    return text if len(text) <= MAX_NAME else text[: MAX_NAME - 3] + "..."


def main():
    folder = sys.argv[1] if len(sys.argv) > 1 else "test-results"
    files = sorted(glob.glob(os.path.join(folder, "*.xml")))

    print("### Slowest tests")
    print()

    if not files:
        print("No per-test timing files were written (a suite that was stopped for running over its time limit writes none).")
        return

    all_tests = []   # (suite, class, name, seconds)
    suites = []      # (suite, count, failed, total seconds, note)

    for path in files:
        suite = os.path.splitext(os.path.basename(path))[0]
        tests, note = read_tests(path)
        failed = sum(1 for t in tests if t[3].lower() == "fail")
        suites.append((suite, len(tests), failed, sum(t[2] for t in tests), note))
        all_tests.extend((suite, t[0], t[1], t[2]) for t in tests)

    print("Seconds are what the tests themselves took. Tests run several at a time, so a suite's total can be larger than its minutes above.")
    print()
    print("| Suite | Tests | Failed | Test seconds | Note |")
    print("|---|---|---|---|---|")
    for suite, count, failed, total, note in sorted(suites, key=lambda s: -s[3]):
        print(f"| {suite} | {count} | {failed} | {total:.0f} | {note or ''} |")
    print()

    print(f"#### The {TOP_TESTS} slowest tests")
    print()
    print("| Seconds | Suite | Test |")
    print("|---|---|---|")
    for suite, _, name, seconds in sorted(all_tests, key=lambda t: -t[3])[:TOP_TESTS]:
        print(f"| {seconds:.1f} | {suite} | {clean(name, suite)} |")
    print()

    classes = {}
    for suite, klass, _, seconds in all_tests:
        entry = classes.setdefault((suite, klass), [0, 0.0, 0.0])
        entry[0] += 1
        entry[1] += seconds
        entry[2] = max(entry[2], seconds)

    print(f"#### The {TOP_CLASSES} test classes with the most test time")
    print()
    print("| Test seconds | Tests | Slowest test | Suite | Class |")
    print("|---|---|---|---|---|")
    for (suite, klass), (count, total, slowest) in sorted(classes.items(), key=lambda c: -c[1][1])[:TOP_CLASSES]:
        print(f"| {total:.0f} | {count} | {slowest:.1f} | {suite} | {clean(klass, suite)} |")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:  # reporting must never fail the job
        print(f"The slowest-tests report could not be made: {error}")
    sys.exit(0)
