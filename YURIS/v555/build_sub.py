#!/usr/bin/env python3
from __future__ import annotations

import argparse
import re
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Tuple

SRC_RE = re.compile(r"^◇([0-9A-Fa-f]{1,8})◇([A-Za-z_]+)◇(.*)$")
TRANS_RE = re.compile(r"^◆([0-9A-Fa-f]{1,8})◆([A-Za-z_]+)◆(.*)$")
DEFAULT_DELIMITER = "|||@@|||"


@dataclass
class PairEntry:
    kind: str  # "name" or "msg" or "name-msg"
    translated: str
    original: str
    file_name: str
    idx_hex: str


def unescape_single_line_text(text: str) -> str:
    out: List[str] = []
    i = 0
    n = len(text)
    while i < n:
        ch = text[i]
        if ch == "\\" and i + 1 < n:
            nxt = text[i + 1]
            if nxt == "n":
                out.append("\n")
                i += 2
                continue
            if nxt == "r":
                out.append("\r")
                i += 2
                continue
            if nxt == "\\":
                out.append("\\")
                i += 2
                continue
        out.append(ch)
        i += 1
    return "".join(out)


def escape_sub_field(text: str, delimiter: str) -> str:
    return (
        text.replace("\\", "\\\\")
        .replace("\t", "\\t")
        .replace("\r", "\\r")
        .replace("\n", "\\n")
        .replace(delimiter, "\\d")
    )


def normalize_kind(raw_type: str) -> Optional[str]:
    t = raw_type.strip().lower()
    if t in {"name", "speaker"}:
        return "name"
    if t in {"msg", "message", "text", "line"}:
        return "msg"
    return None


def iter_txt_files(txt_dir: Path) -> Iterable[Path]:
    return sorted([p for p in txt_dir.rglob("*.txt") if p.is_file()], key=lambda p: p.as_posix().lower())


def parse_one_file(path: Path) -> List[PairEntry]:
    lines = path.read_text(encoding="utf-8-sig").splitlines()
    entries: List[PairEntry] = []

    pending_by_key: Dict[Tuple[str, str], str] = {}

    for line in lines:
        src = SRC_RE.match(line)
        if src:
            idx_hex = src.group(1).upper()
            kind = normalize_kind(src.group(2))
            if not kind:
                continue
            source_text = unescape_single_line_text(src.group(3))
            key = (idx_hex, kind)
            pending_by_key[key] = source_text
            continue

        trans = TRANS_RE.match(line)
        if not trans:
            continue

        idx_hex = trans.group(1).upper()
        kind = normalize_kind(trans.group(2))
        if not kind:
            continue
        translated_text = unescape_single_line_text(trans.group(3))
        key = (idx_hex, kind)

        source_text = pending_by_key.pop(key, None)
        if source_text is None:
            # No paired source line.
            continue

        entries.append(
            PairEntry(
                kind=kind,
                translated=translated_text,
                original=source_text,
                file_name=path.name,
                idx_hex=idx_hex,
            )
        )

    return entries


def inject_name_msg_rows(entries: List[PairEntry]) -> List[PairEntry]:
    """Relabel the first msg immediately following a name as ame-msg."""
    out: List[PairEntry] = []
    pending_name: Optional[PairEntry] = None

    for e in entries:
        if e.kind == "name":
            out.append(e)
            pending_name = e
            continue

        if e.kind == "msg" and pending_name is not None:
            out.append(
                PairEntry(
                    kind="name-msg",
                    translated=e.translated,
                    original=e.original,
                    file_name=e.file_name,
                    idx_hex=e.idx_hex,
                )
            )
            pending_name = None
            continue

        out.append(e)

    return out

def build_rows(
    entries: Iterable[PairEntry],
    drop_identical: bool,
    drop_empty: bool,
    dedupe_by_kind_translated: bool,
) -> Tuple[List[PairEntry], int, int, int]:
    out: List[PairEntry] = []
    seen: Dict[Tuple[str, str], str] = {}
    dropped_identical = 0
    dropped_empty = 0
    dropped_dedupe = 0

    for e in entries:
        if drop_identical and e.translated == e.original:
            dropped_identical += 1
            continue
        if drop_empty and (not e.translated.strip() or not e.original.strip()):
            dropped_empty += 1
            continue

        if dedupe_by_kind_translated:
            k = (e.kind, e.translated)
            prev = seen.get(k)
            if prev is None:
                seen[k] = e.original
            else:
                # In dedupe mode, always keep the first occurrence.
                dropped_dedupe += 1
                continue

        out.append(e)

    return out, dropped_identical, dropped_empty, dropped_dedupe


def count_kinds(entries: Iterable[PairEntry]) -> Tuple[int, int, int]:
    name_count = 0
    msg_count = 0
    name_msg_count = 0
    for e in entries:
        if e.kind == "name":
            name_count += 1
        elif e.kind == "msg":
            msg_count += 1
        elif e.kind == "name-msg":
            name_msg_count += 1
    return name_count, msg_count, name_msg_count


def write_sub(path: Path, rows: List[PairEntry], delimiter: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    header = [
        "# AmaNee.sub v1",
        f"# generated_at: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}",
        f"# format: <kind>{delimiter}<translated>{delimiter}<original>",
        "# kind: name | msg | name-msg",
        "",
    ]
    lines: List[str] = header[:]
    for e in rows:
        lines.append(
            f"{e.kind}{delimiter}{escape_sub_field(e.translated, delimiter)}{delimiter}{escape_sub_field(e.original, delimiter)}"
        )
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description="Build AmaNee.sub from texttwolines txt files.")
    parser.add_argument("--txt-dir", default="ysbin_dec_dump_txt_clean_trans", help="Input txt folder (default: txt)")
    parser.add_argument(
        "--output",
        default="./name.sub",
        help="Output .sub file path",
    )
    parser.add_argument(
        "--drop-identical",
        action="store_true",
        help="Drop lines where translated == original (default: keep)",
    )
    parser.add_argument(
        "--drop-empty",
        action="store_true",
        help="Drop lines where translated/original is empty after strip (default: keep)",
    )
    parser.add_argument(
        "--dedupe",
        action="store_true",
        help="Dedupe by (kind, translated), keep first occurrence only (default: off)",
    )
    parser.add_argument(
        "--delimiter",
        default=DEFAULT_DELIMITER,
        help=f"Field delimiter (default: {DEFAULT_DELIMITER!r})",
    )
    parser.add_argument(
        "--tsv",
        action="store_true",
        help="Use legacy tab delimiter",
    )
    parser.add_argument(
        "--no-name-msg",
        action="store_true",
        help="Do not emit `name-msg` rows (default: emit)",
    )
    args = parser.parse_args()

    txt_dir = Path(args.txt_dir)
    out_file = Path(args.output)

    if not txt_dir.exists():
        raise SystemExit(f"txt dir not found: {txt_dir}")

    txt_files = list(iter_txt_files(txt_dir))
    if not txt_files:
        raise SystemExit(f"no txt files found under: {txt_dir}")

    all_entries: List[PairEntry] = []
    for txt_file in txt_files:
        entries = parse_one_file(txt_file)
        if not args.no_name_msg:
            entries = inject_name_msg_rows(entries)
        all_entries.extend(entries)

    delimiter = "\t" if args.tsv else args.delimiter
    rows, dropped_identical, dropped_empty, dropped_dedupe = build_rows(
        all_entries,
        drop_identical=args.drop_identical,
        drop_empty=args.drop_empty,
        dedupe_by_kind_translated=args.dedupe,
    )
    write_sub(out_file, rows, delimiter)

    name_rows, msg_rows, name_msg_rows = count_kinds(rows)

    print(f"Input txt files  : {len(txt_files)}")
    print(f"Raw pair entries : {len(all_entries)}")
    print(f"Output rows      : {len(rows)}")
    print(f"Name rows        : {name_rows}")
    print(f"Msg rows         : {msg_rows}")
    print(f"NameMsg rows     : {name_msg_rows}")
    print(f"DroppedIdentical : {dropped_identical}")
    print(f"DroppedEmpty     : {dropped_empty}")
    print(f"DroppedDedupe    : {dropped_dedupe}")
    print(f"Delimiter        : {repr(delimiter)}")
    print(f"Output file      : {out_file}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

