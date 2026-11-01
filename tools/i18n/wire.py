#!/usr/bin/env python3
"""One-shot wiring helper: wraps worklist strings in Loc.T(...) at their exact source position.

This is a one-off migration tool for the W1 wave, not part of the long-term pipeline. It rewrites
only the literal that the worklist recorded (file + line + raw text), so anything the extractor
skipped stays untouched.

Rows it refuses to touch (reported, not silently skipped):
  case labels, Tag/Name/Key assignments, comparisons, and anything else where a method call is not
  a valid C# expression.

Usage:
  E:/python/python.exe tools/i18n/wire.py --wave W1 --dry-run
  E:/python/python.exe tools/i18n/wire.py --wave W1
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from collections import defaultdict
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
WORKLIST = Path(__file__).resolve().parent / "worklist.json"

REFUSE_LINE = re.compile(
    r"^\s*case\b|^\s*Tag\s*=|^\s*Name\s*=|^\s*Key\s*=|\.Contains\s*\(|\.StartsWith\s*\(|"
    r"\.EndsWith\s*\(|\.Equals\s*\(|^\s*return\s|\bconst\s|TaskName|SectionKey|"
    r"\bis\s+\$?\"|\b(or|and)\s+\$?\""
    # Comparisons must stay English: "x == \"Ready\"" is a value check, not display text.
    r"|==\s*\$?\"|\$?\"\s*==|!=\s*\$?\"|\$?\"\s*!="
)

# Switch-expression arms look like  "ReShade" => (a, b): a method call is not a constant pattern.
SWITCH_ARM = re.compile(r"^\s*[\"']")


def cs_literal(text: str) -> str:
    out = (
        text.replace("\\", "\\\\")
        .replace('"', '\\"')
        .replace("\n", "\\n")
        .replace("\r", "\\r")
        .replace("\t", "\\t")
    )
    return '"' + out + '"'


def boundary_ok(line: str, start: int, end: int) -> bool:
    before = line[start - 1] if start > 0 else " "
    after = line[end] if end < len(line) else " "
    return before not in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_" and \
        after not in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_"


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass

    ap = argparse.ArgumentParser()
    ap.add_argument("--wave", default="W1")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    worklist = json.loads(WORKLIST.read_text(encoding="utf-8"))
    entries = [
        e for e in worklist["entries"]
        if e["wave"] == args.wave.upper() and e["kind"].startswith("cs-") and not e["needs_review"]
    ]
    review = [
        e for e in worklist["entries"]
        if e["wave"] == args.wave.upper() and e["kind"].startswith("cs-") and e["needs_review"]
    ]

    # One English string often appears in several places ("Ready", "Install", "Info"). The
    # worklist stores them in occurrences; wiring only the first would leave the rest English.
    # Only plain literals can be expanded this way - an interpolated string's raw text depends on
    # the expressions in that particular line, so those stay limited to their recorded position.
    by_file: dict[str, list[dict]] = defaultdict(list)
    for e in entries:
        by_file[e["file"].replace("\\", "/")].append(e)
        if e["kind"] != "cs-literal":
            continue
        for occ in e.get("occurrences", []):
            if occ.get("wave") != args.wave.upper():
                continue
            if occ["file"].replace("\\", "/") == e["file"]:
                continue
            if occ["kind"] != "cs-literal":
                continue
            clone = dict(e)
            clone["file"] = occ["file"]
            clone["line"] = occ["line"]
            by_file[occ["file"].replace("\\", "/")].append(clone)

    total = 0
    refused: list[str] = []
    notfound: list[str] = []

    for rel, items in sorted(by_file.items()):
        path = REPO_ROOT / rel
        if not path.exists():
            print(f"! missing {rel}")
            continue
        lines = path.read_text(encoding="utf-8-sig").splitlines(keepends=True)

        # Group by line, rewrite right-to-left so earlier offsets stay valid.
        per_line: dict[int, list[dict]] = defaultdict(list)
        for e in items:
            per_line[e["line"]].append(e)

        changed_here = 0
        for lineno in sorted(per_line, reverse=True):
            idx = lineno - 1
            if idx < 0 or idx >= len(lines):
                notfound.append(f"{rel}:{lineno}")
                continue
            line = lines[idx]
            if REFUSE_LINE.search(line) or SWITCH_ARM.match(line):
                for e in per_line[lineno]:
                    refused.append(f"{rel}:{lineno} {e['key'][:50]!r}")
                continue
            if "Loc.T(" in line:
                continue

            # Right-to-left within the line.
            ordered = sorted(per_line[lineno], key=lambda e: len(e["raw"]), reverse=True)
            for e in ordered:
                pos = line.find(e["raw"])
                if pos < 0 or not boundary_ok(line, pos, pos + len(e["raw"])):
                    notfound.append(f"{rel}:{lineno} {e['key'][:40]!r}")
                    continue
                call = f"Loc.T({cs_literal(e['key'])}" + "".join(f", {a}" for a in e["args"]) + ")"
                line = line[:pos] + call + line[pos + len(e["raw"]):]
                changed_here += 1
            lines[idx] = line

        if changed_here and not args.dry_run:
            text = "".join(lines)
            if "using RenoDXCommander.Localization;" not in text:
                # Insert after the last using line at the top of the file.
                parts = text.split("\n")
                last_using = -1
                for i, ln in enumerate(parts[:80]):
                    if ln.startswith("using "):
                        last_using = i
                if last_using >= 0:
                    parts.insert(last_using + 1, "using RenoDXCommander.Localization;")
                else:
                    parts.insert(0, "using RenoDXCommander.Localization;")
                text = "\n".join(parts)
            path.write_text(text, encoding="utf-8")
        total += changed_here
        print(f"{rel}: {changed_here}")

    print(f"\nwired {total} strings" + (" (dry run)" if args.dry_run else ""))
    if review:
        print(f"\nleft for a human {len(review)} (complex interpolation):")
        for e in review[:25]:
            print(f"  - {e['file']}:{e['line']} {e['key'][:60]!r}")
    if refused:
        print(f"\nrefused {len(refused)} (not a valid call position):")
        for r in refused[:25]:
            print("  - " + r)
    if notfound:
        print(f"\nnot located {len(notfound)}:")
        for r in notfound[:25]:
            print("  - " + r)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
