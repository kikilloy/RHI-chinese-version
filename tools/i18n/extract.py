#!/usr/bin/env python3
"""Extract user-visible UI strings from the RHI sources into a translation worklist.

Why this exists
---------------
RHI builds its UI in C# (DetailPanelBuilder.*, SettingsHandler.*, DialogService.*) plus one big
MainWindow.xaml, and it uses English text as the translation key (docs/adr/0003). So the worklist is
"every English string a user can read", not "every string literal".

What is dropped (and why)
-------------------------
  * CrashReporter.Log / Debug / Console output - logs stay English so they can be grepped and
    reported (CLAUDE.md, docs/i18n-glossary.md section 1).
  * Paths, URLs, file names, extensions, version numbers, ini keys, registry paths, regexes,
    format specs - machine-readable, translating them breaks things.
  * Internal values ("Direct", "Accelerated", "Stable", "Section:Download") - whitelisted away.
  * Strings already wrapped in Loc.T(...) - already wired.

Outputs (both are regenerated on every run, never hand-edited)
--------------------------------------------------------------
  tools/i18n/worklist.json   machine-readable: every candidate + file/line/kind/wave
  tools/i18n/worklist.md     the report a human reads: counts per file and per wave, plus samples

Usage:
  E:/python/python.exe tools/i18n/extract.py            (from the repo root)
  E:/python/python.exe tools/i18n/extract.py --report   (also print the summary)
"""

from __future__ import annotations

import argparse
import html
import json
import os
import re
import sys
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
APP_ROOT = REPO_ROOT / "RenoDXCommander"
OUT_DIR = Path(__file__).resolve().parent

# -- Which files are in scope at all ------------------------------------------
# Only files that build user-visible UI are scanned. Services, ViewModels, Models and tooling are
# full of internal strings (ini keys, pack ids, log fragments, registry paths) that must never be
# translated - scanning them buries the real worklist in noise.
#
# W1 = shell (window, navigation, settings page, status bar)
# W2 = install/update flow, dialogs, progress text
# W3 = detail panel (7 sections)
# P1 = later batch: component cog windows, tool popups, FAQ text
# First match wins; no match = not scanned.
WAVE_RULES: list[tuple[str, str]] = [
    ("SettingsHandler", "W1"),
    ("MainWindow.xaml", "W1"),
    ("MainWindow.StatusBar", "W1"),
    ("MainWindow.Events.Settings", "W1"),
    ("MainWindow.UISetup", "W1"),
    ("MainWindow.Skeleton", "W1"),
    ("DialogService", "W2"),
    # Component cog windows (MainWindow.Events.Components.*) are P1: they are the deep per-feature
    # settings UI, not the shell. Everything else in MainWindow.Events.* is install/update flow.
    ("DialogService", "W2"),
    ("MainWindow.Events.Components", "P1"),
    ("MainWindow.Events", "W2"),
    ("MainViewModel.Install", "W2"),
    ("MainViewModel.Update", "W2"),
    ("MainViewModel.Activity", "W2"),
    ("Services/DownloadProgress", "W2"),
    ("Services/Net/GitHubApiDiagnostics", "W2"),
    ("InstallEventHandler", "W2"),
    ("MassDeployHandler", "W2"),
    ("MassDlssDeployDialog", "W2"),
    ("UpdateLogWindow", "W2"),
    ("DetailPanelBuilder", "W3"),
    # Component cards own their button labels and status text ("Install RenoDX", "Still ready?"),
    # so the detail panel is not translated without them.
    ("ViewModels/GameCardViewModel", "W3"),
    ("DragDropHandler", "P1"),
    ("PopupHelper", "P1"),
    ("CogDialog", "P1"),
    ("MfgDialog", "P1"),
    ("DlssDefaultsDialog", "P1"),
    ("AddonManagerDialog", "P1"),
    ("SetupWindow", "P1"),
    ("MainWindow.FaqBuilder", "P1"),
]

# -- Lines that are never UI text ---------------------------------------------
NOISE_LINE_PATTERNS = [
    r"CrashReporter\.Log\s*\(",
    r"\bLog\s*\(\s*\$?\"",
    r"Debug\.WriteLine",
    r"Console\.Write",
    r"^\s*//",
    r"^\s*/\*",
    r"^\s*\*",
    r"^\s*using\s",
    r"^\s*namespace\s",
    r"\bnew Regex\s*\(",
    r"Path\.Combine\s*\(",
    r"ProcessStartInfo\s*\(",
    r"Registry\.",
    r"\bthrow new \w*Exception\s*\(",
    r"\bcatch\b",
    r"nameof\s*\(",
    # settings.json keys / dictionary literals: machine-readable, never translated
    r"^\s*s\[",
    # Dictionary initializer rows: ["key"] = "value" - the key is an identifier, the value is data.
    r"^\s*\[",
    # Indexing with a literal key: map["Foo"], dict["Bar"].
    r"\[\s*\$?\"",
    r'^\s*\w+\[\s*\$?"',
    r"TryGetValue\s*\(",
    r'\bRemove\s*\(\s*\$?"',
    r'\bContains\s*\(\s*\$?"',
    r"StringComparison\.",
    r"\bswitch\b.*\bcase\b",
    # Pattern matching: "x is \"A\" or \"B\"" needs constant patterns, so these are never UI text.
    r"\bis\s+\$?\"",
    r"\b(or|and)\s+\$?\"",
]
NOISE_LINE_RE = [re.compile(p) for p in NOISE_LINE_PATTERNS]

# Never translate: component/channel names, platform names, internal values.
WHITELIST_EXACT = {
    # channels / modes / internal values
    "Stable", "Nightly", "Development", "Global", "Custom", "Select", "Off", "Auto", "Direct",
    "Accelerated", "On", "Yes", "No", "None", "Default", "All", "OK",
    # component / project names
    "ReShade", "RenoDX", "Luma", "OptiScaler", "DLSS", "DLSSD", "DLSSG", "FSR", "XeSS",
    "Streamline", "DXVK", "HDR", "NVAPI", "ReLimiter", "Reflex", "Discord", "Nexus",
    "PCGamingWiki", "Steam", "Epic", "GOG", "Xbox", "NVIDIA", "AMD", "Intel", "ReBAR",
    "RHI", "REFramework", "dgVoodoo2", "Special K", "ENB", "MFG", "DXR", "VRAM", "scRGB",
    "GitHub", "LiliumHdr", "UnrealEngine",
    "HDR10", "Ultra+", "Lilium HDR", "Display Commander", "OptiScaler DLSS-NR", "DLSS Swapper",
    # UI canvas labels that stay as-is
    "Simplified PC Gaming",
    # Technical feature names the user asked to keep in English (screenshot feedback, 2026-11-01):
    # "神经网络渲染" and "DLSS 超分辨率技术" are harder to recognise than the originals.
    "Neural Rendering", "Neural Rendering (nvngx_dlssnr.dll)", "DLSS Super Resolution",
    "Ray Reconstruction", "DLSS Enabler",
    # Brush/colour names: XAML converts these strings to a Brush. A translation throws
    # "The value cannot be converted to type Brush" and takes the window down.
    "Transparent", "White", "Black", "Red", "Green", "Blue", "Gray", "Grey", "Orange",
    "Yellow", "Purple",
}

MIN_LENGTH = 3
TECH_EXT_RE = re.compile(
    r"\.(dll|exe|ini|7z|zip|json|md|txt|log|asi|conf|xml|csv|bin|gh|sha|png|jpg|ico|bat|ps1|cfg)$",
    re.IGNORECASE,
)
WINDOWS_PATH_RE = re.compile(r"[A-Za-z]:\\|\\\\[A-Za-z]")
SINGLE_TOKEN_RE = re.compile(r"[A-Za-z][A-Za-z0-9]*")
CAMEL_CASE_RE = re.compile(r"^[A-Z][a-z]+[A-Z]")
VERSION_RE = re.compile(r"v?\d[\w.\-+]*")
PLACEHOLDER_RE = re.compile(r"\{\d+(?::[^}]*)?\}")


@dataclass
class Candidate:
    key: str                 # English source with {0}/{1} placeholders
    file: str
    line: int
    wave: str
    kind: str                # "cs-literal" | "cs-interpolated" | "xaml-attribute"
    raw: str                 # the original source text as written
    args: list[str] = field(default_factory=list)
    needs_review: str = ""   # reason a human should look at it


def wave_for(rel_path: str) -> str | None:
    """Returns the wave for a file, or None when the file is out of the P0/P1 scope."""
    normalized = rel_path.replace(os.sep, "/")
    for pattern, wave in WAVE_RULES:
        if pattern in normalized:
            return wave
    return None


# -- C# string literal scanning -----------------------------------------------

CS_STRING_RE = re.compile(
    r'(?P<verbatim>\$?@")|(?P<interp>\$")|(?P<plain>")',
)


def iter_literals(line: str):
    """Yields (prefix, body) for every string literal on one line."""
    i = 0
    n = len(line)
    while i < n:
        m = CS_STRING_RE.search(line, i)
        if not m:
            return
        start_kind = m.lastgroup
        j = m.end()
        buf = []
        if start_kind == "verbatim":
            while j < n:
                if line[j] == '"':
                    if j + 1 < n and line[j + 1] == '"':
                        buf.append('"')
                        j += 2
                        continue
                    j += 1
                    break
                buf.append(line[j])
                j += 1
            yield ("@{", "".join(buf))
        elif start_kind == "interp":
            while j < n:
                c = line[j]
                if c == "\\":
                    buf.append(line[j : j + 2])
                    j += 2
                    continue
                if c == '"':
                    j += 1
                    break
                buf.append(c)
                j += 1
            yield ("$", "".join(buf))
        else:
            while j < n:
                c = line[j]
                if c == "\\":
                    buf.append(line[j : j + 2])
                    j += 2
                    continue
                if c == '"':
                    j += 1
                    break
                buf.append(c)
                j += 1
            yield ("", "".join(buf))
        i = j


def unescape_cs(body: str) -> str:
    out = []
    i = 0
    n = len(body)
    while i < n:
        c = body[i]
        if c == "\\" and i + 1 < n:
            nxt = body[i + 1]
            mapping = {"n": "\n", "r": "\r", "t": "\t", "\\": "\\", '"': '"', "0": "\0"}
            if nxt in mapping:
                out.append(mapping[nxt])
                i += 2
                continue
            out.append(c)
            i += 1
            continue
        out.append(c)
        i += 1
    return "".join(out)


def interpolate_to_template(body: str) -> tuple[str, list[str], str]:
    """Turns an interpolated string body into an English-as-key template.

    Returns (template, expressions, note). Note is non-empty when the expression is too complex
    to rewrite mechanically - those need a human to type the Loc.T call by hand.
    """
    out: list[str] = []
    expressions: list[str] = []
    i = 0
    n = len(body)
    while i < n:
        c = body[i]
        if c == "{":
            if i + 1 < n and body[i + 1] == "{":
                out.append("{")
                i += 2
                continue
            depth = 1
            j = i + 1
            in_string = False
            while j < n and depth:
                ch = body[j]
                if in_string:
                    if ch == "\\":
                        j += 2
                        continue
                    if ch == '"':
                        in_string = False
                elif ch == '"':
                    in_string = True
                elif ch == "{":
                    depth += 1
                elif ch == "}":
                    depth -= 1
                j += 1
            expr = body[i + 1 : j - 1]
            fmt = ""
            if ":" in expr and '"' not in expr and "??" not in expr:
                head, tail = expr.split(":", 1)
                expr, fmt = head, ":" + tail
            expr = expr.strip()
            if not expr:
                i = j
                continue
            if expr not in expressions:
                expressions.append(expr)
            out.append("{%d%s}" % (expressions.index(expr), fmt))
            i = j
            continue
        if c == "}":
            if i + 1 < n and body[i + 1] == "}":
                out.append("}")
                i += 2
                continue
            out.append("}")
            i += 1
            continue
        out.append(c)
        i += 1

    template = "".join(out)
    note = ""
    for expr in expressions:
        if re.search(r'["()\[\]?+*<>=!|&]', expr) or " " in expr.strip():
            note = "complex interpolation - rewrite by hand"
            break
    return template, expressions, note


def looks_like_ui_text(text: str) -> str | None:
    """Returns a reject reason, or None when the string is plausible UI text."""
    t = text.strip()
    if len(t) < MIN_LENGTH:
        return "too short"
    if t in WHITELIST_EXACT:
        return "whitelisted name"
    if "://" in t:
        return "url"
    if WINDOWS_PATH_RE.search(t):
        return "windows path"
    if TECH_EXT_RE.search(t):
        return "file name"
    if not re.search(r"[A-Za-z]", t):
        return "no letters"
    if t.startswith("Section:"):
        return "internal tag"
    # "[FreezeDiag] ...", "[{0:HH:mm:ss.fff}] [Enqueue] ...": log and timing format strings.
    if t.startswith("[") and "]" in t:
        return "debug format"
    # Command lines (/Query /TN, -applaunch ...) must never be translated.
    if re.match(r"^(/[A-Za-z]|--?[A-Za-z])", t):
        return "command line"
    # "channel={0}, useNormalReShade={1}", "Handles={0} Threads={1}" - diagnostic format strings.
    if re.search(r"[A-Za-z_]\w*=\{\d", t):
        return "technical format"
    # Regex patterns ("Unreal Engine \d") - translating one breaks the match.
    if re.search(r"\\[dDwWsSbB]", t):
        return "regex pattern"
    # Command lines with switches: reg.exe "add ... /v X /t REG_DWORD /f", 7z "x \"..\" -o\"..\" -y".
    if re.search(r"\s/[a-zA-Z]\s", t) or re.match(r"^x\s+\"", t) or re.search(r"\s-[a-zA-Z]\"", t):
        return "command line"
    if re.search(r"\b(HKLM|HKCU|SOFTWARE\\|REG_DWORD|REG_SZ)\b", t):
        return "registry"
    if re.search(r"%[0-9]*[sdfux]", t):
        return "printf format"
    if re.fullmatch(r"[\w.\-+]+@[\w.\-]+", t):
        return "email"
    if " " not in t:
        if VERSION_RE.fullmatch(t):
            return "version"
        if SINGLE_TOKEN_RE.fullmatch(t):
            # A single capitalised word is usually a button label, but identifiers and enum
            # values look the same, so keep it and flag it for a quick look.
            if t[0].islower():
                return "identifier"
            # "TitleBarButtonPressedBackground": a resource key, not a label.
            if CAMEL_CASE_RE.match(t):
                return "resource key"
            # "Souperman9", "DLSSG20": identifiers and handles, not words.
            if any(ch.isdigit() for ch in t):
                return "identifier with digits"
            return None
        return "single token"
    # Multi-word: reject things that look like code.
    if re.search(r"[{};=<>|]", t) and not PLACEHOLDER_RE.search(t):
        return "code-like"
    if re.fullmatch(r"[\W\d_ ]+", t):
        return "no words"
    return None


def extract_cs(path: Path, rel: str, wave: str) -> tuple[list[Candidate], Counter]:
    rejects: Counter = Counter()
    found: list[Candidate] = []
    try:
        text = path.read_text(encoding="utf-8-sig", errors="replace")
    except OSError as exc:
        print(f"  ! cannot read {rel}: {exc}", file=sys.stderr)
        return found, rejects

    for lineno, line in enumerate(text.splitlines(), start=1):
        if any(rx.search(line) for rx in NOISE_LINE_RE):
            continue
        if "Loc.T(" in line:
            continue
        if "//" in line:
            code_part, _, _ = line.partition("//")
            if not code_part.strip():
                continue
            line = code_part

        for prefix, body in iter_literals(line):
            if prefix == "$":
                template, args, note = interpolate_to_template(body)
                key = unescape_cs(template)
                raw = "$" + '"' + body + '"'
                kind = "cs-interpolated"
            elif prefix == "@{":
                key = unescape_cs(body)
                args, note, raw, kind = [], "", '@"' + body + '"', "cs-literal"
            else:
                key = unescape_cs(body)
                args, note, raw, kind = [], "", '"' + body + '"', "cs-literal"

            # An interpolated string that ends right before another quote holds a string literal
            # inside its holes ("{(n == 1 ? "" : "s")}"). Our single-line scanner stops at that
            # quote, so the recorded literal is only a prefix - mark it for a human instead of
            # letting the wiring pass replace half a string.
            if kind == "cs-interpolated":
                end = line.find(raw[1:]) if raw else -1
                if end >= 0:
                    after = line[end + len(raw) - 1 : end + len(raw)]
                    if after == '"':
                        note = "complex interpolation - nested string literal"

            reason = looks_like_ui_text(key)
            if reason:
                rejects[reason] += 1
                continue
            found.append(Candidate(key=key, file=rel, line=lineno, wave=wave, kind=kind,
                                   raw=raw, args=args, needs_review=note))
    return found, rejects


# -- XAML scanning ------------------------------------------------------------

XAML_ATTR_RE = re.compile(
    r'\b(Text|Content|PlaceholderText|Header|Description|Title)="([^"{}]*(?:\{[^"]*)?)"'
)


def extract_xaml(path: Path, rel: str, wave: str) -> tuple[list[Candidate], Counter]:
    rejects: Counter = Counter()
    found: list[Candidate] = []
    try:
        text = path.read_text(encoding="utf-8-sig", errors="replace")
    except OSError as exc:
        print(f"  ! cannot read {rel}: {exc}", file=sys.stderr)
        return found, rejects

    for lineno, line in enumerate(text.splitlines(), start=1):
        stripped = line.strip()
        if stripped.startswith("<!--"):
            continue
        for m in XAML_ATTR_RE.finditer(line):
            # XAML escapes & as &amp; ("Accounts &amp; Data"); the UI shows the unescaped text,
            # which is what the language pack must be keyed by.
            value = html.unescape(m.group(2).strip())
            if not value or value.startswith("{"):
                continue
            reason = looks_like_ui_text(value)
            if reason:
                rejects[reason] += 1
                continue
            found.append(Candidate(key=value, file=rel, line=lineno, wave=wave,
                                   kind="xaml-attribute", raw=value))
    return found, rejects


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--report", action="store_true", help="print the summary table")
    ap.add_argument("--show-rejects", action="store_true", help="print what was dropped and why")
    args = ap.parse_args()

    all_candidates: list[Candidate] = []
    rejects: Counter = Counter()
    files_scanned = 0

    def in_scope(path: Path) -> tuple[str, str] | None:
        rel = str(path.relative_to(REPO_ROOT))
        parts = [p.lower() for p in path.relative_to(REPO_ROOT).parts]
        if "bin" in parts or "obj" in parts:
            return None
        wave = wave_for(rel)
        return (rel, wave) if wave else None

    for path in sorted(APP_ROOT.rglob("*.cs")):
        scope = in_scope(path)
        if scope is None:
            continue
        rel, wave = scope
        found, rej = extract_cs(path, rel, wave)
        all_candidates += found
        rejects += rej
        files_scanned += 1

    for path in sorted(APP_ROOT.rglob("*.xaml")):
        scope = in_scope(path)
        if scope is None:
            continue
        rel, wave = scope
        found, rej = extract_xaml(path, rel, wave)
        all_candidates += found
        rejects += rej
        files_scanned += 1

    # Dedupe by key, keeping every place it appears (the wiring step needs all of them).
    # When one English string shows up in two waves, it belongs to the EARLIEST one: otherwise a
    # label used in both the shell and a later dialog would be translated only with the dialog and
    # stay English in the shell (this is exactly what happened to "Settings" on the first run).
    wave_order = {"W1": 0, "W2": 1, "W3": 2, "P1": 3}
    by_key: dict[str, Candidate] = {}
    occurrences: dict[str, list[dict]] = defaultdict(list)
    for c in all_candidates:
        occurrences[c.key].append({"file": c.file, "line": c.line, "kind": c.kind, "wave": c.wave})
        existing = by_key.get(c.key)
        if existing is None or wave_order.get(c.wave, 9) < wave_order.get(existing.wave, 9):
            by_key[c.key] = c

    unique = sorted(by_key.values(), key=lambda c: (c.wave, c.file, c.line))

    payload = {
        "generated_by": "tools/i18n/extract.py",
        "source_root": "RenoDXCommander",
        "files_scanned": files_scanned,
        "unique_keys": len(unique),
        "total_occurrences": len(all_candidates),
        "by_wave": dict(Counter(c.wave for c in unique)),
        "needs_review": [c.key for c in unique if c.needs_review],
        "entries": [
            {
                "key": c.key,
                "wave": c.wave,
                "kind": c.kind,
                "file": c.file,
                "line": c.line,
                "raw": c.raw,
                "args": c.args,
                "needs_review": c.needs_review,
                "occurrences": occurrences[c.key],
            }
            for c in unique
        ],
    }

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    (OUT_DIR / "worklist.json").write_text(
        json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )

    # -- Markdown report -----------------------------------------------------
    per_wave_file: dict[str, Counter] = defaultdict(Counter)
    for c in unique:
        per_wave_file[c.wave][c.file] += 1

    lines = [
        "# i18n worklist",
        "",
        "Generated by tools/i18n/extract.py - do not edit by hand.",
        "",
        f"- files scanned: **{files_scanned}**",
        f"- unique candidate strings: **{len(unique)}**",
        f"- occurrences in source: **{len(all_candidates)}**",
        "",
        "## Per wave",
        "",
        "| wave | unique strings |",
        "|---|---|",
    ]
    for wave in sorted(payload["by_wave"]):
        lines.append(f"| {wave} | {payload['by_wave'][wave]} |")

    for wave in sorted(per_wave_file):
        lines += ["", f"### {wave}", "", "| file | strings |", "|---|---|"]
        for file, count in per_wave_file[wave].most_common():
            lines.append(f"| {file} | {count} |")

    lines += ["", "## Dropped (not UI text)", "", "| reason | count |", "|---|---|"]
    for reason, count in rejects.most_common():
        lines.append(f"| {reason} | {count} |")

    if payload["needs_review"]:
        lines += ["", "## Needs a human (complex interpolation)", ""]
        for key in payload["needs_review"][:40]:
            lines.append(f"- {key}")
        if len(payload["needs_review"]) > 40:
            lines.append(f"- ... and {len(payload['needs_review']) - 40} more")

    (OUT_DIR / "worklist.md").write_text("\n".join(lines) + "\n", encoding="utf-8")

    print(f"extract: {files_scanned} files, {len(unique)} unique strings, {len(all_candidates)} occurrences")
    print("waves: " + ", ".join(f"{k}={v}" for k, v in sorted(payload["by_wave"].items())))
    if args.report:
        print()
        print("\n".join(lines))
    if args.show_rejects:
        print("\ndropped:")
        for reason, count in rejects.most_common():
            print(f"  {count:6d}  {reason}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
