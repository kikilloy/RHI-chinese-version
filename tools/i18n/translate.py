#!/usr/bin/env python3
"""Translate the i18n worklist into Simplified Chinese with the local Hy-MT2-7B model.

Pipeline (docs/i18n-pipeline.md):
  extract.py  ->  worklist.json  ->  translate.py  ->  RenoDXCommander/Lang/zh-Hans.json
                                                 ->  validate.py

Design decisions that matter
----------------------------
  * One request per string, never batched. A batch lets the model merge or skip lines, and a
    single bad answer would poison several strings at once.
  * The target file IS the resume state. Every N strings the whole map is rewritten atomically
    (.tmp + replace), so an interrupted run loses at most N strings and a broken write can never
    leave a half-written pack that Loc.Load would choke on.
  * Only user messages. Hy-MT2 has no default system prompt; adding one changes the output.
  * Placeholders are verified per string. If {0} disappears the answer is retried, then dropped -
    a translation with a lost placeholder would throw inside string.Format at runtime.
  * Hard time limit. The caller (a coding agent) must not sit in one call forever; on timeout the
    script saves what it has and exits 2.

Usage:
  E:/python/python.exe tools/i18n/translate.py --wave W1
  E:/python/python.exe tools/i18n/translate.py --wave W1 --max-minutes 18
  E:/python/python.exe tools/i18n/translate.py --wave W1 --limit 20      (smoke test)

Exit codes: 0 = every requested string is translated, 2 = stopped early (rerun to continue).
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
WORKLIST = Path(__file__).resolve().parent / "worklist.json"
OUT_FILE = REPO_ROOT / "RenoDXCommander" / "Lang" / "zh-Hans.json"

ENDPOINT = "http://127.0.0.1:8099/v1/chat/completions"
MAX_TOKENS = 384
SAVE_EVERY = 20
RETRIES = 2

# Kept deliberately small: each entry is only injected when its English word actually appears in
# the string being translated, so short strings stay short and the request stays fast.
GLOSSARY: list[tuple[str, str]] = [
    ("ReShade", "ReShade"), ("RenoDX", "RenoDX"), ("Luma", "Luma"),
    ("OptiScaler", "OptiScaler"), ("DLSS", "DLSS"), ("DLSSD", "DLSSD"), ("DLSSG", "DLSSG"),
    ("FSR", "FSR"), ("XeSS", "XeSS"), ("Streamline", "Streamline"), ("DXVK", "DXVK"),
    ("HDR", "HDR"), ("NVAPI", "NVAPI"), ("Reflex", "Reflex"), ("MFG", "MFG"),
    ("ReLimiter", "ReLimiter"), ("Display Commander", "Display Commander"),
    ("NVIDIA", "NVIDIA"), ("AMD", "AMD"), ("Intel", "Intel"), ("Steam", "Steam"),
    ("Epic", "Epic"), ("GOG", "GOG"), ("Xbox", "Xbox"), ("Nexus", "Nexus"),
    ("Discord", "Discord"), ("PCGamingWiki", "PCGamingWiki"), ("RHI", "RHI"),
    ("Stable", "Stable"), ("Nightly", "Nightly"), ("Development", "Development"),
    ("Framerate", "帧率"), ("Frame Generation", "帧生成"), ("Upscaler", "超分辨率"),
    ("Override", "覆盖"), ("Addon", "插件"), ("Preset", "预设"), ("Shader", "着色器"),
    ("Sharpening", "锐化"), ("Resolution", "分辨率"), ("Settings", "设置"),
    ("Install", "安装"), ("Uninstall", "卸载"), ("Update", "更新"), ("Apply", "应用"),
    ("Cancel", "取消"), ("Close", "关闭"), ("Refresh", "刷新"), ("Mirror", "镜像"),
    ("Integrity", "完整性"), ("Fallback", "回退"), ("Game library", "游戏库"),
    ("Patch notes", "更新日志"), ("Troubleshooting", "故障排查"),
    # Feature names that stay English (user preference): the Chinese ones are harder to spot.
    ("Neural Rendering", "Neural Rendering"), ("DLSS Super Resolution", "DLSS Super Resolution"),
    ("Ray Reconstruction", "Ray Reconstruction"), ("DLSS Enabler", "DLSS Enabler"),
]

PLACEHOLDER_RE = re.compile(r"\{\d+(?::[^}]*)?\}")
QUOTE_TRIM = " \t\r\n\"'\u201c\u201d\u300c\u300d"


def build_prompt(source: str) -> str:
    hints = [
        f"{en} 翻译成 {zh}"
        for en, zh in GLOSSARY
        if en.lower() in source.lower() and en != zh
    ]
    keep = [
        f"{en} 翻译成 {en}"
        for en, zh in GLOSSARY
        if en == zh and re.search(r"\b" + re.escape(en) + r"\b", source)
    ]

    # Exact shape of the official templates from the model card (README_CN.md):
    #   Terminology:  "参考下面的翻译：<pairs> ... 将以下文本翻译为 X，注意只需要输出翻译后的结果，不要额外解释："
    #   Default:      "将以下文本翻译为 X，注意只需要输出翻译后的结果，不要额外解释：" + blank line + source
    # Anything extra (for example "keep these placeholders intact") gets echoed as part of the
    # translation, which is how the first run produced placeholder-count mismatches. Do not add
    # constraint lines here; the model already preserves {0}/{1} on its own.
    parts: list[str] = []
    if hints or keep:
        parts.append("参考下面的翻译：")
        parts.extend(hints)
        parts.extend(keep)

    parts.append("将以下文本翻译为 简体中文，注意只需要输出翻译后的结果，不要额外解释：")
    parts.append("")
    parts.append(source)
    return "\n".join(parts)


def call_model(prompt: str, temperature: float) -> str:
    payload = {
        "messages": [{"role": "user", "content": prompt}],
        "temperature": temperature,
        "top_p": 0.6,
        "top_k": 20,
        "repeat_penalty": 1.05,
        "max_tokens": MAX_TOKENS,
        "stream": False,
    }
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(ENDPOINT, data=data, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=180) as resp:
        body = json.loads(resp.read().decode("utf-8"))
    return body["choices"][0]["message"]["content"]


def clean(answer: str) -> str:
    text = answer.strip().strip(QUOTE_TRIM)
    for prefix in ("翻译：", "翻译:", "译文：", "译文:"):
        if text.startswith(prefix):
            text = text[len(prefix):].strip()
    # The model sometimes echoes "source -> translation". Keep the right-hand side.
    for arrow in ("->", "\u2192", "=>"):
        if arrow in text:
            text = text.rsplit(arrow, 1)[1].strip().strip(QUOTE_TRIM)
    if "\n" not in text:
        text = re.sub(r"\s{2,}", " ", text)
    return text


def placeholders_of(text: str) -> list[str]:
    return PLACEHOLDER_RE.findall(text)


CJK_CHAR = "\u4e00-\u9fff"
# Braces count as Latin for spacing purposes: "v{0}版本" must become "v{0} 版本".
CJK_ALPHA_LEFT = re.compile(r"(?<=[" + CJK_CHAR + r"])(?=[A-Za-z0-9{])")
CJK_ALPHA_RIGHT = re.compile(r"(?<=[A-Za-z0-9}])(?=[" + CJK_CHAR + r"])")
SENTENCE_END = ".!?。！？:："


def apply_style_rules(source: str, text: str) -> str:
    """Enforces docs/i18n-glossary.md section 3 on the model output.

    1. No formal "you": the glossary fixes the informal form for the whole UI.
    2. Half-width space between Chinese and Latin letters/digits ("安装 ReShade 插件").
    3. No sentence-ending period when the English source has none (buttons, labels, headings).
    """
    text = text.replace("您", "你")
    text = CJK_ALPHA_LEFT.sub(" ", text)
    text = CJK_ALPHA_RIGHT.sub(" ", text)
    text = re.sub(r"[ ]{2,}", " ", text).strip()

    if source.rstrip() and source.rstrip()[-1] not in SENTENCE_END:
        stripped = text.rstrip("。.")
        # Only drop it when this is a single short sentence, not a paragraph.
        if stripped and stripped.count("。") == 0 and len(stripped) <= 60:
            text = stripped
    return text


def translate_one(source: str) -> tuple[str | None, str]:
    """Returns (translation, note). Translation None means: keep English."""
    prompt = build_prompt(source)
    expected = placeholders_of(source)

    note = ""
    for attempt in range(RETRIES + 1):
        temperature = 0.7 if attempt == 0 else 0.3
        try:
            raw = call_model(prompt, temperature)
        except (urllib.error.URLError, TimeoutError, OSError, KeyError, json.JSONDecodeError) as exc:
            note = f"request failed: {exc}"
            time.sleep(1.0)
            continue

        text = clean(raw)
        if not text:
            note = "empty answer"
            continue
        if expected and placeholders_of(text) != expected:
            note = f"placeholder mismatch: {placeholders_of(text)} != {expected}"
            continue
        if not expected and placeholders_of(text):
            note = "invented a placeholder"
            continue
        return apply_style_rules(source, text), ""

    return None, note


def load_existing() -> dict[str, str]:
    if not OUT_FILE.exists():
        return {}
    try:
        with OUT_FILE.open(encoding="utf-8-sig") as fh:
            data = json.load(fh)
        return {k: v for k, v in data.items() if isinstance(k, str) and isinstance(v, str)}
    except (OSError, json.JSONDecodeError) as exc:
        print(f"! existing pack unreadable ({exc}) - starting from scratch", file=sys.stderr)
        return {}


def save(map_: dict[str, str]) -> None:
    OUT_FILE.parent.mkdir(parents=True, exist_ok=True)
    tmp = OUT_FILE.with_suffix(".json.tmp")
    ordered = {k: map_[k] for k in sorted(map_)}
    tmp.write_text(json.dumps(ordered, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(tmp, OUT_FILE)


def main() -> int:
    # Windows consoles default to GBK; translated strings contain characters it cannot encode.
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass

    ap = argparse.ArgumentParser()
    ap.add_argument("--wave", default="W1", help="W1, W2, W3 or ALL")
    ap.add_argument("--limit", type=int, default=0, help="translate at most N strings (smoke test)")
    ap.add_argument("--max-minutes", type=float, default=18.0, help="wall-clock budget")
    args = ap.parse_args()

    if not WORKLIST.exists():
        print("! tools/i18n/worklist.json missing - run extract.py first", file=sys.stderr)
        return 1

    worklist = json.loads(WORKLIST.read_text(encoding="utf-8"))
    wanted = [
        e for e in worklist["entries"]
        if args.wave.upper() == "ALL" or e["wave"] == args.wave.upper()
    ]
    existing = load_existing()
    missing = [e for e in wanted if e["key"] not in existing]
    todo = missing[: args.limit] if args.limit else missing

    print(f"wave {args.wave}: {len(wanted)} strings in scope, "
          f"{len(wanted) - len(missing)} already done, {len(todo)} to translate")
    if not todo:
        return 0

    started = time.monotonic()
    deadline = started + args.max_minutes * 60
    done = 0
    failed: list[tuple[str, str]] = []

    for entry in todo:
        if time.monotonic() > deadline:
            print(f"\n! time budget reached after {done} strings - saved, rerun to continue")
            save(existing)
            return 2

        key = entry["key"]
        translation, note = translate_one(key)
        if translation is None:
            failed.append((key, note))
            print(f"  FAIL {key!r} - {note}")
        else:
            existing[key] = translation
            done += 1
            if done % 10 == 0 or done == len(todo):
                elapsed = time.monotonic() - started
                rate = done / elapsed if elapsed else 0
                print(f"  {done}/{len(todo)}  {rate:.2f}/s  {key!r} -> {translation!r}")

        if done and done % SAVE_EVERY == 0:
            save(existing)

    save(existing)
    elapsed = time.monotonic() - started
    print(f"\ndone: {done} translated in {elapsed:.1f}s ({len(existing)} total in pack)")
    if failed:
        print(f"{len(failed)} string(s) could not be translated and stay English:")
        for key, note in failed[:20]:
            print(f"  - {key!r}: {note}")
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
