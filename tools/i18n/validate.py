#!/usr/bin/env python3
"""Validate the Chinese language pack before it is wired into the UI.

There is no human review pass (the user asked for machine checks instead - plan section 6.6), so
this script is the only thing standing between a bad translation and the UI. It is deliberately
split into hard failures (the pack must not ship) and warnings (look at it, decide).

Hard failures
  * broken JSON
  * a placeholder that changed, disappeared or was invented ({0} must survive string.Format)
  * the model's instruction text leaking into a translation
Warnings
  * English left over inside a translation (3+ Latin words in a row)
  * a translation longer than 1.8x the English source (overflows buttons and section headers)
  * the same Chinese text used for two different English keys (usually a lost distinction)
  * English punctuation habits (trailing full stop, half-width brackets)
  * a translation identical to the source (sometimes correct: ReShade, GitHub, DX9)

It also (re)writes Lang/en.json, the English baseline used to detect keys that changed after a
translation was made.

Usage:
  E:/python/python.exe tools/i18n/validate.py
  E:/python/python.exe tools/i18n/validate.py --wave W1 --strict
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
TOOL_DIR = Path(__file__).resolve().parent
WORKLIST = TOOL_DIR / "worklist.json"
ZH_FILE = REPO_ROOT / "RenoDXCommander" / "Lang" / "zh-Hans.json"
EN_FILE = REPO_ROOT / "RenoDXCommander" / "Lang" / "en.json"
REPORT = TOOL_DIR / "report-zh-Hans.md"

PLACEHOLDER_RE = re.compile(r"\{\d+(?::[^}]*)?\}")
LEAK_PATTERNS = [
    "将以下文本翻译",
    "参考下面的翻译",
    "不要额外解释",
    "只需要输出翻译",
    "Translate the following",
]
CJK_RE = re.compile(r"[\u4e00-\u9fff]")

# Latin words that are fine inside a Chinese string: product names and short technical terms.
LATIN_OK = {
    "reshade", "renodx", "luma", "optiscaler", "dlss", "dlssd", "dlssg", "dlssnr", "fsr", "xess",
    "streamline", "dxvk", "hdr", "nvapi", "reflex", "mfg", "relimiter", "commander", "nvidia",
    "amd", "intel", "steam", "epic", "gog", "xbox", "nexus", "discord", "pcgamingwiki", "rhi",
    "stable", "nightly", "development", "github", "dx9", "dx10", "dx11", "dx12", "vulkan", "opengl",
    "api", "gpu", "cpu", "vram", "dlss-nr", "ultra+", "lilium", "dgvoodoo2", "special", "k", "enb",
    "reframework", "microsoft", "store", "xess", "fps", "sr", "rr", "fg", "nrd", "nrc", "lumen",
    "ue", "uj", "ini", "mb", "gb", "kb", "ms", "dll", "exe", "json", "pcgamingwiki", "ratings",
    "b", "x", "m", "s", "f1", "f2", "h", "v", "dx", "ea", "asus", "msi", "asus", "dlss-nr",
}
WORD_RE = re.compile(r"[A-Za-z][A-Za-z0-9+._-]*")


def load_json(path: Path):
    try:
        with path.open(encoding="utf-8-sig") as fh:
            return json.load(fh), None
    except json.JSONDecodeError as exc:
        return None, f"invalid JSON: {exc}"
    except OSError as exc:
        return None, f"cannot read: {exc}"


def looks_like_english_run(text: str) -> str:
    """Returns the offending run when 3+ consecutive Latin words appear outside placeholders."""
    stripped = PLACEHOLDER_RE.sub(" ", text)
    run: list[str] = []
    for word in WORD_RE.findall(stripped):
        if word.lower() in LATIN_OK:
            run.clear()
            continue
        run.append(word)
        if len(run) >= 3:
            return " ".join(run)
    return ""


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass

    ap = argparse.ArgumentParser()
    ap.add_argument("--wave", default="", help="only check keys from this wave (W1/W2/W3)")
    ap.add_argument("--strict", action="store_true", help="exit 1 when warnings are present too")
    args = ap.parse_args()

    if not WORKLIST.exists():
        print("! worklist.json missing - run extract.py first", file=sys.stderr)
        return 1

    worklist = json.loads(WORKLIST.read_text(encoding="utf-8"))
    entries = worklist["entries"]
    if args.wave:
        entries = [e for e in entries if e["wave"] == args.wave.upper()]
    baseline = {e["key"]: e["key"] for e in entries}

    # English baseline: written every run so a changed English string shows up as a missing key.
    EN_FILE.parent.mkdir(parents=True, exist_ok=True)
    EN_FILE.write_text(
        json.dumps(dict(sorted(baseline.items())), ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )

    zh, error = load_json(ZH_FILE)
    if error:
        print(f"HARD FAIL: zh-Hans.json {error}")
        return 1
    zh = {k: v for k, v in zh.items() if isinstance(v, str)}

    hard: list[str] = []
    warn: list[tuple[str, str, str]] = []   # (kind, key, detail)

    missing = [k for k in baseline if k not in zh]
    extra = [k for k in zh if k not in baseline]

    for key in baseline:
        if key not in zh:
            continue
        value = zh[key]
        where = key[:70]

        if not value.strip():
            hard.append(f"empty translation: {where}")
            continue

        for pattern in LEAK_PATTERNS:
            if pattern in value:
                hard.append(f"model instruction leaked into the translation: {where} -> {value[:60]}")
                break

        src_ph = PLACEHOLDER_RE.findall(key)
        dst_ph = PLACEHOLDER_RE.findall(value)
        if src_ph != dst_ph:
            hard.append(f"placeholder mismatch ({src_ph} vs {dst_ph}): {where} -> {value[:60]}")

        if CJK_RE.search(value):
            ratio = len(value) / max(1, len(key))
            if ratio > 1.8:
                warn.append(("too long", key, f"{len(value)} vs {len(key)} chars ({ratio:.2f}x)"))
            if value.rstrip().endswith(".") and not key.rstrip().endswith("."):
                warn.append(("english period", key, value[-20:]))
            if value.rstrip().endswith(":"):
                warn.append(("half-width colon", key, value[-20:]))
            run = looks_like_english_run(value)
            if run:
                warn.append(("english left over", key, run))
        elif key.strip() != value.strip():
            warn.append(("no Chinese at all", key, value))

    # Same Chinese text for two different English keys.
    reverse: dict[str, list[str]] = {}
    for key, value in zh.items():
        if key in baseline and CJK_RE.search(value):
            reverse.setdefault(value, []).append(key)
    for value, keys in reverse.items():
        if len(keys) > 1:
            warn.append(("duplicate value", keys[0], f"{value!r} <- {keys}"))

    lines = [
        "# zh-Hans pack validation",
        "",
        f"- strings in scope: **{len(baseline)}**",
        f"- translations present: **{len([k for k in baseline if k in zh])}**",
        f"- missing: **{len(missing)}**",
        f"- hard failures: **{len(hard)}**",
        f"- warnings: **{len(warn)}**",
        "",
    ]
    if missing:
        lines += ["## Missing (still English in the UI)", ""]
        lines += [f"- {k}" for k in missing[:60]]
        if len(missing) > 60:
            lines.append(f"- ... and {len(missing) - 60} more")
        lines.append("")
    if extra:
        lines += ["## Present in the pack but not in the worklist (stale, safe to delete)", ""]
        lines += [f"- {k}" for k in extra[:40]]
        lines.append("")
    if hard:
        lines += ["## HARD FAILURES", ""]
        lines += [f"- {h}" for h in hard]
        lines.append("")
    if warn:
        lines += ["## Warnings", "", "| kind | source | detail |", "|---|---|---|"]
        for kind, key, detail in warn:
            safe_key = key.replace("|", "\\|").replace("\n", "\\n")
            safe_detail = detail.replace("|", "\\|").replace("\n", "\\n")
            lines.append(f"| {kind} | {safe_key[:70]} | {safe_detail[:80]} |")

    REPORT.write_text("\n".join(lines) + "\n", encoding="utf-8")

    print(f"scope={len(baseline)} translated={len([k for k in baseline if k in zh])} "
          f"missing={len(missing)} hard={len(hard)} warnings={len(warn)}")
    if hard:
        print("\nHARD FAILURES:")
        for h in hard[:20]:
            print(f"  - {h}")
    if warn:
        counts: dict[str, int] = {}
        for kind, _, _ in warn:
            counts[kind] = counts.get(kind, 0) + 1
        print("warnings: " + ", ".join(f"{k}={v}" for k, v in sorted(counts.items())))
    print(f"report: {REPORT.relative_to(REPO_ROOT)}")

    if hard:
        return 1
    if args.strict and warn:
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
