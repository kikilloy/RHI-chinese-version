#!/usr/bin/env python3
"""Merge multi-line string concatenations into single keys, then wire them to Loc.T.

C# dialog bodies are written as several literals joined with +:

    Text = "First sentence. " +
           "Second sentence.\n\n" +
           "Third sentence.",

Each fragment is a separate literal, so the extractor only ever sees the first one. This script
joins a whole run of plain (non-interpolated) literals into one key, records it for translation,
and can rewrite the block into a single Loc.T("...") call.

Only plain literals are merged. An interpolated fragment ($"...") inside the run is reported and
left alone: rewriting it needs the hole expressions, which is a human job.

Usage:
  E:/python/python.exe tools/i18n/merge_strings.py --scan       # list blocks, write merge.json
  E:/python/python.exe tools/i18n/merge_strings.py --wire       # replace merged blocks with Loc.T
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
OUT = Path(__file__).resolve().parent / "merge.json"
ZH_FILE = REPO_ROOT / "RenoDXCommander" / "Lang" / "zh-Hans.json"

FILES = [
    "RenoDXCommander/DialogService.Game.cs",
    "RenoDXCommander/DialogService.Update.cs",
    "RenoDXCommander/DialogService.cs",
    "RenoDXCommander/InstallEventHandler.cs",
    "RenoDXCommander/MainWindow.Events.cs",
    "RenoDXCommander/MainWindow.Events.Install.cs",
    "RenoDXCommander/MassDeployHandler.cs",
    "RenoDXCommander/UpdateLogWindow.cs",
    "RenoDXCommander/SettingsHandler.Channels.cs",
    "RenoDXCommander/SettingsHandler.Accounts.cs",
    "RenoDXCommander/SettingsHandler.General.cs",
    "RenoDXCommander/SettingsHandler.Download.cs",
    "RenoDXCommander/ViewModels/MainViewModel.Install.Components.cs",
    "RenoDXCommander/ViewModels/MainViewModel.Update.cs",
    "RenoDXCommander/ViewModels/MainViewModel.Install.cs",
]

# a fragment: "..." possibly followed by + or a terminator
FRAG = re.compile(r'"((?:[^"\\]|\\.)*)"')
ESC = {"n": "\n", "r": "\r", "t": "\t", '"': '"', "\\": "\\", "0": "\0"}


def unescape(s: str) -> str:
    out = []
    i = 0
    while i < len(s):
        c = s[i]
        if c == "\\" and i + 1 < len(s):
            nxt = s[i + 1]
            if nxt in ESC:
                out.append(ESC[nxt])
                i += 2
                continue
            out.append(c)
            i += 1
            continue
        out.append(c)
        i += 1
    return "".join(out)


def escape_cs(s: str) -> str:
    return (s.replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n")
            .replace("\r", "\\r").replace("\t", "\\t"))


def fragments_of(line: str):
    """Returns (spans, has_interpolation) for every plain literal on the line."""
    if '$"' in line or '@"' in line:
        return [], True
    return [(m.start(), m.end(), m.group(1)) for m in FRAG.finditer(line)], False


def scan_file(rel: str):
    path = REPO_ROOT / rel
    if not path.exists():
        return [], []
    lines = path.read_text(encoding="utf-8-sig").splitlines(keepends=True)
    blocks, skipped = [], []

    i = 0
    while i < len(lines):
        line = lines[i]
        spans, interpolated = fragments_of(line)
        stripped = line.rstrip("\r\n").rstrip()
        # A line that already calls Loc.T(...) is wired; only start a run on a bare literal,
        # otherwise the finished translation gets swallowed into the merged block.
        starts_run = spans and stripped.endswith("+") and len(spans) == 1 and "Loc.T(" not in line
        if not starts_run:
            i += 1
            continue

        # Collect following fragments while each line ends with + and holds exactly one literal.
        parts = [unescape(spans[0][2])]
        j = i
        ok = True
        while lines[j].rstrip("\r\n").rstrip().endswith("+"):
            nxt = lines[j + 1] if j + 1 < len(lines) else ""
            nspans, ninterp = fragments_of(nxt)
            if ninterp or len(nspans) != 1:
                skipped.append({"file": rel, "line": j + 2, "reason": "interpolated fragment"})
                ok = False
                break
            parts.append(unescape(nspans[0][2]))
            j += 1
            if not nxt.rstrip("\r\n").rstrip().endswith("+"):
                break

        if ok and j > i:
            blocks.append({
                "file": rel,
                "start": i + 1,
                "end": j + 1,
                "key": "".join(parts),
            })
            i = j + 1
        else:
            i += 1
    return blocks, skipped


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass

    ap = argparse.ArgumentParser()
    ap.add_argument("--scan", action="store_true")
    ap.add_argument("--wire", action="store_true")
    args = ap.parse_args()

    if args.scan:
        blocks, skipped = [], []
        for rel in FILES:
            b, s = scan_file(rel)
            blocks += b
            skipped += s
        # Only keep blocks whose text is worth translating.
        usable = []
        for b in blocks:
            text = b["key"].strip()
            if len(text) < 12 or not re.search(r"[A-Za-z]{3}", text):
                continue
            if re.search(r"[A-Za-z]:\\|\\\\", text):     # a path inside the sentence is fine, keep it
                pass
            usable.append(b)
        OUT.write_text(json.dumps(usable, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(f"blocks found: {len(blocks)}, usable: {len(usable)}, skipped: {len(skipped)}")
        for b in usable[:6]:
            print(f"  {b['file']}:{b['start']}-{b['end']}  {b['key'][:70]!r}")
        for s in skipped[:6]:
            print(f"  SKIP {s['file']}:{s['line']} {s['reason']}")
        return 0

    if args.wire:
        if not OUT.exists():
            print("! run --scan first", file=sys.stderr)
            return 1
        blocks = json.loads(OUT.read_text(encoding="utf-8"))
        zh = json.loads(ZH_FILE.read_text(encoding="utf-8")) if ZH_FILE.exists() else {}

        by_file: dict[str, list[dict]] = {}
        for b in blocks:
            by_file.setdefault(b["file"], []).append(b)

        wired = 0
        missing = []
        for rel, items in by_file.items():
            path = REPO_ROOT / rel
            lines = path.read_text(encoding="utf-8-sig").splitlines(keepends=True)
            for b in sorted(items, key=lambda x: x["start"], reverse=True):
                key = b["key"]
                if key not in zh:
                    missing.append((rel, b["start"], key[:50]))
                    continue
                s_idx, e_idx = b["start"] - 1, b["end"] - 1
                if s_idx >= len(lines) or e_idx >= len(lines):
                    continue
                start_line = lines[s_idx]
                end_line = lines[e_idx]
                first_q = start_line.find('"')
                last_q = end_line.rfind('"')
                if first_q < 0 or last_q < 0:
                    continue
                new_start = start_line[:first_q] + f'Loc.T("{escape_cs(key)}")' + end_line[last_q + 1:]
                lines[s_idx:e_idx + 1] = [new_start]
                wired += 1
            text = "".join(lines)
            if "using RenoDXCommander.Localization;" not in text:
                parts = text.split("\n")
                last_using = max((i for i, ln in enumerate(parts[:80]) if ln.startswith("using ")), default=-1)
                parts.insert(last_using + 1 if last_using >= 0 else 0, "using RenoDXCommander.Localization;")
                text = "\n".join(parts)
            path.write_text(text, encoding="utf-8")

        print(f"merged blocks wired: {wired}")
        if missing:
            print(f"not translated yet: {len(missing)}")
            for m in missing[:10]:
                print(f"  {m[0]}:{m[1]} {m[2]!r}")
        return 0

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
