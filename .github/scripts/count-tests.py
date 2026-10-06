#!/usr/bin/env python3
"""Prints how many tests one xUnit result file (from `-result-xml`) holds, as a bare integer.

Usage: count-tests.py <result.xml>

The CI Test step uses it to put a test count in the run's summary and to fail an Api shard whose
class filter matched (almost) nothing. A missing, empty or cut-off file prints what could be read,
or 0, and the exit code is always 0: the caller decides what a low number means.
"""

import sys
import xml.etree.ElementTree as ET


def count_tests(path):
    count = 0
    try:
        for _, element in ET.iterparse(path, events=("end",)):
            if element.tag == "test":
                count += 1
                element.clear()
    except (OSError, ET.ParseError):
        pass
    return count


if __name__ == "__main__":
    print(count_tests(sys.argv[1]) if len(sys.argv) > 1 else 0)
    sys.exit(0)
