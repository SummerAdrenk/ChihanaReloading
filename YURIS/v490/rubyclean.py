#!/usr/bin/env python3
from __future__ import annotations

import argparse
import re
from datetime import datetime
from pathlib import Path
from typing import List, Tuple


TRANS_WITH_TYPE_RE = re.compile(r"^(◆[^◆]+◆[^◆]+◆)(.*?)(\r?\n)?$")
TRANS_SIMPLE_RE = re.compile(r"^(◆[^◆]+◆)(.*?)(\r?\n)?$")

RUBY_AT_IDX_RE = re.compile(r"@ruby#([0-9A-Fa-f]{1,8}):([^@]+?)@([^@]*)@")
RUBY_AT_RE = re.compile(r"@ruby([^@]+?)@([^@]*)@")
RUBY_BRACKET_RE = re.compile(r"《([^《》]+?)[｜|][^《》]*》")


def append_error_log(log_path: Path, source_path: Path, err: Exception) -> None:
    log_path.parent.mkdir(parents=True, exist_ok=True)
    header = (
        "# rubyclean error log\n"
        f"# generated_at: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n"
        "# format: <source_path>\\t<error>\n"
    )
    need_header = (not log_path.exists()) or (log_path.stat().st_size == 0)
    with log_path.open("a", encoding="utf-8") as f:
        if need_header:
            f.write(header)
        f.write(f"{source_path}\t{err}\n")


def strip_ruby_markup(text: str) -> str:
    out = text
    while True:
        old = out
        out = RUBY_AT_IDX_RE.sub(r"\2", out)
        out = RUBY_AT_RE.sub(r"\1", out)
        out = RUBY_BRACKET_RE.sub(r"\1", out)
        if out == old:
            return out


def clean_line(line: str) -> Tuple[str, bool]:
    m = TRANS_WITH_TYPE_RE.match(line)
    if m:
        prefix, body, eol = m.group(1), m.group(2), (m.group(3) or "")
        cleaned = strip_ruby_markup(body)
        return f"{prefix}{cleaned}{eol}", cleaned != body

    m = TRANS_SIMPLE_RE.match(line)
    if m:
        prefix, body, eol = m.group(1), m.group(2), (m.group(3) or "")
        cleaned = strip_ruby_markup(body)
        return f"{prefix}{cleaned}{eol}", cleaned != body

    return line, False


def clean_one(src: Path, dst: Path) -> Tuple[int, int]:
    text = src.read_text(encoding="utf-8-sig")
    lines = text.splitlines(keepends=True)

    changed_lines = 0
    touched_lines = 0
    out: List[str] = []
    for line in lines:
        new_line, changed = clean_line(line)
        if line.startswith("◆"):
            touched_lines += 1
        if changed:
            changed_lines += 1
        out.append(new_line)

    dst.parent.mkdir(parents=True, exist_ok=True)
    dst.write_text("".join(out), encoding="utf-8")
    return touched_lines, changed_lines


def clean_cmd(args: argparse.Namespace) -> None:
    inp = Path(args.input)
    out = Path(args.output)
    error_log = Path(args.error_log) if args.error_log else None

    if inp.is_file():
        out_file = out
        if out.suffix.lower() != ".txt":
            out_file = out / inp.name
        try:
            touched, changed = clean_one(inp, out_file)
            print(f"[OK] {inp} -> {out_file} touched={touched} changed={changed}")
        except Exception as e:  # noqa: BLE001
            if error_log is not None:
                append_error_log(error_log, inp, e)
            raise
        return

    if not inp.is_dir():
        raise ValueError(f"input not found: {inp}")

    out.mkdir(parents=True, exist_ok=True)
    txt_files = sorted(p for p in inp.rglob("*.txt") if p.is_file())
    if not txt_files:
        raise RuntimeError(f"no txt files found: {inp}")

    ok = 0
    fail = 0
    for p in txt_files:
        try:
            rel = p.relative_to(inp)
            out_file = out / rel
            touched, changed = clean_one(p, out_file)
            print(f"[OK] {p} -> {out_file} touched={touched} changed={changed}")
            ok += 1
        except Exception as e:  # noqa: BLE001
            if error_log is not None:
                append_error_log(error_log, p, e)
            print(f"[FAIL] {p}: {e}")
            fail += 1
    print(f"[OK] done ok={ok} fail={fail} out={out}")


def build_parser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(description="清ruby的")
    ap.add_argument("input", help="input txt file or directory")
    ap.add_argument("output", help="output txt file or directory")
    ap.add_argument("--error-log", default="", help="optional error log path")
    ap.set_defaults(func=clean_cmd)
    return ap


def main() -> None:
    parser = build_parser()
    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
