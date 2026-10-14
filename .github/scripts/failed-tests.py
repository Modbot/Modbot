#!/usr/bin/env python3
"""Lists the tests that failed, from the per-suite xUnit result files, for the run's summary page.

Usage: failed-tests.py <folder with one <suite>.xml per suite>   (markdown goes to stdout)

The raw job log holds the failures too, but it is hundreds of thousands of characters long and can
only be read signed in, so finding a failing test's name meant downloading it. The summary page is
public and short. Each failure is also printed to stderr as an ::error annotation, which the runner
reads from the log, so the run page names it as well. Reporting only, like slowest-tests.py: a
missing or cut-off file is skipped and
the exit code is always 0; the Test step's own result decides whether the job fails.
"""

import glob
import os
import sys
import xml.etree.ElementTree as ET

MAX_FAILURES = 50
MAX_NAME = 200
MAX_MESSAGE = 400


def one_line(text, limit):
    text = " ".join((text or "").split())
    return text if len(text) <= limit else text[: limit - 3] + "..."


def read_failures(path):
    """Every (name, message) of a failed test in one result file; what was readable if it is cut off."""
    failures = []
    try:
        for _, element in ET.iterparse(path, events=("end",)):
            if element.tag != "test":
                continue
            if (element.get("result") or "") == "Fail":
                failure = element.find("failure")
                message = failure.findtext("message") if failure is not None else ""
                failures.append((element.get("name") or "", message or ""))
            element.clear()
    except ET.ParseError:
        pass
    return failures


def main():
    folder = sys.argv[1] if len(sys.argv) > 1 else "test-results"
    found = []
    for path in sorted(glob.glob(os.path.join(folder, "*.xml"))):
        suite = os.path.splitext(os.path.basename(path))[0]
        for name, message in read_failures(path):
            found.append((suite, name, message))

    if not found:
        return

    print("### Failed tests")
    print()
    print("| Suite | Test | Message |")
    print("|---|---|---|")
    for suite, name, message in found[:MAX_FAILURES]:
        cell_name = one_line(name, MAX_NAME).replace("|", "\\|")
        cell_message = one_line(message, MAX_MESSAGE).replace("|", "\\|")
        print(f"| {suite} | {cell_name} | {cell_message} |")
        # The runner reads annotations from its log; stderr keeps them out of the summary file.
        print(f"::error title={suite}::{one_line(name, MAX_NAME)}: {one_line(message, MAX_MESSAGE)}", file=sys.stderr)
    if len(found) > MAX_FAILURES:
        print()
        print(f"And {len(found) - MAX_FAILURES} more.")
    print()


if __name__ == "__main__":
    try:
        main()
    except Exception as error:  # reporting only: never fail the job
        print(f"Failed tests could not be listed: {error}")
    sys.exit(0)
