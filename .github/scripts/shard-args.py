#!/usr/bin/env python3
"""Writes the xUnit arguments one Api test shard runs with, from .github/test-shards/api.json.

Usage: shard-args.py <shard> <args-file> <result-xml> <long-running-seconds>

The Api test suite is run as several CI jobs, each with a class filter. xUnit takes the filter from
`-class "<namespace>.<class>"` (type names are fully qualified and matched exactly; given more than
once they are an OR) and `-class- "..."` (the same, for leaving classes out; given more than once
they are an AND). Mixing them with `-namespace`, `-filter` or any other way of filtering is not
allowed by xUnit, so nothing else is used.

  - a shard listed in the file runs exactly its classes: one `-class` line per class;
  - the shard named by "remainder" runs everything the others list NOT: one `-class-` line for every
    class of every listed shard. A test class nobody listed therefore still runs, in that shard.

The file written is an xUnit response file: one argument per line, no quoting, and the command
line is then only `@@ <args-file>` (xUnit says nothing else may be on it). It holds every argument
of the run, the filter and `-longRunning` and `-result-xml` included.

A problem that would make a shard run the wrong tests (an unknown shard, a class in two shards, a
badly written name) exits 1 with an ::error:: line. A name that matches no class in the source
(renamed or deleted) is only a ::warning::: the class then runs in the remainder shard, so no test
is lost, only the balance suffers. This script never runs a test.
"""

import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
SHARD_FILE = os.path.join(REPO, ".github", "test-shards", "api.json")
TEST_SOURCES = os.path.join(REPO, "tests", "Modbot.Api.Tests")

NAME = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$")
NAMESPACE = re.compile(r"^\s*namespace\s+([A-Za-z0-9_.]+)\s*;")
CLASS = re.compile(r"^\s*(?:(?:public|internal|private|protected|sealed|static|abstract|partial)\s+)*class\s+([A-Za-z0-9_]+)")


def fail(message):
    print(f"::error::{message}")
    sys.exit(1)


def load_shards():
    try:
        with open(SHARD_FILE, encoding="utf-8") as handle:
            data = json.load(handle)
    except (OSError, ValueError) as error:
        fail(f"{SHARD_FILE} could not be read: {error}")
    shards = data.get("shards")
    remainder = data.get("remainder")
    if not isinstance(shards, dict) or not shards or not isinstance(remainder, str) or not remainder:
        fail('api.json must hold a "shards" object (shard name to list of classes) and a "remainder" shard name.')
    if remainder in shards:
        fail(f'The remainder shard "{remainder}" must not list classes of its own: it runs whatever the others do not.')
    seen = {}
    for shard, classes in shards.items():
        if not isinstance(classes, list):
            fail(f'Shard "{shard}" in api.json must be a list of class names.')
        for name in classes:
            if not isinstance(name, str) or not NAME.match(name):
                fail(f'Shard "{shard}" lists "{name}", which is not a fully qualified class name (Namespace.Class, no wildcards).')
            if name in seen:
                fail(f'{name} is listed for both {seen[name]} and {shard}.')
            seen[name] = shard
    return shards, remainder, seen


def classes_in_sources():
    found = set()
    for folder, _, files in os.walk(TEST_SOURCES):
        if {"obj", "bin"} & set(folder.split(os.sep)):
            continue
        for file in files:
            if not file.endswith(".cs"):
                continue
            namespace = None
            with open(os.path.join(folder, file), encoding="utf-8-sig") as handle:
                for line in handle:
                    match = NAMESPACE.match(line)
                    if match:
                        namespace = match.group(1)
                        continue
                    match = CLASS.match(line)
                    if match and namespace:
                        found.add(f"{namespace}.{match.group(1)}")
    return found


def main():
    if len(sys.argv) != 5:
        fail("usage: shard-args.py <shard> <args-file> <result-xml> <long-running-seconds>")
    shard, args_file, result_xml, long_running = sys.argv[1:]
    if not long_running.isdigit() or int(long_running) < 1:
        fail(f"<long-running-seconds> must be a positive integer, not {long_running!r}.")

    shards, remainder, listed = load_shards()
    if shard != remainder and shard not in shards:
        fail(f'"{shard}" is not a shard in api.json (shards: {", ".join(list(shards) + [remainder])}).')

    in_sources = classes_in_sources()
    if not in_sources:
        print(f"::warning::No test classes were found under {TEST_SOURCES}; the shard names could not be checked.")
    else:
        for name, owner in sorted(listed.items()):
            if name not in in_sources:
                print(f"::warning::{name} (listed for {owner} in api.json) is not a test class any more; remove or rename it there.")

    lines = ["-longRunning", long_running, "-result-xml", result_xml]
    if shard == remainder:
        for name in sorted(listed):
            lines += ["-class-", name]
        print(f"{shard}: every Api test class except the {len(listed)} listed for {', '.join(shards)}.")
    else:
        for name in shards[shard]:
            lines += ["-class", name]
        print(f"{shard}: {len(shards[shard])} listed Api test classes.")

    with open(args_file, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(lines) + "\n")


if __name__ == "__main__":
    main()
