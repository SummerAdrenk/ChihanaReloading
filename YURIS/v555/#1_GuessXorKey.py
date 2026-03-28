#!/usr/bin/env python3
from __future__ import annotations

import argparse
import struct
from pathlib import Path


MAGIC = b"YSTB"
HEADER_SIZE = 0x20


def guess_xor_key_from_bytes(data: bytes) -> int:
    if len(data) < HEADER_SIZE:
        raise ValueError("file too small")
    if data[:4] != MAGIC:
        raise ValueError("not YSTB")

    version = struct.unpack_from("<I", data, 4)[0]

    if 200 < version < 300:
        code_seg_size, args_seg_size = struct.unpack_from("<II", data, 8)
        if (code_seg_size + args_seg_size) < 0x10:
            return 0
        off = 0x2C
    else:
        inst_index_size = struct.unpack_from("<I", data, 0x0C)[0]
        args_data_size = struct.unpack_from("<I", data, 0x14)[0]
        if args_data_size == 0:
            return 0
        off = HEADER_SIZE + inst_index_size + 0x8

    if off + 4 > len(data):
        raise ValueError(f"cannot read key at offset 0x{off:X}")

    return struct.unpack_from("<I", data, off)[0] & 0xFFFFFFFF


def guess_xor_key(path: Path) -> int:
    return guess_xor_key_from_bytes(path.read_bytes())


def build_parser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(description="YSTB_GuessXorKey")
    ap.add_argument("ystb_file", help="ystb/ybn file path (choose a large file for best result)")
    ap.add_argument("--out", default="Key.txt", help="key output file (default: Key.txt)")
    ap.add_argument("--print-only", action="store_true", help="print key only, do not write file")
    return ap


def main() -> None:
    args = build_parser().parse_args()
    p = Path(args.ystb_file)
    if not p.is_file():
        raise FileNotFoundError(f"file not found: {p}")

    key = guess_xor_key(p)
    key_text = f"0x{key:X}"

    print("YSTB_GuessXorKey:")
    print("  Choose the largest ystb file as possible.")
    print(f"[OK] {p} -> {key_text}")

    if not args.print_only:
        out = Path(args.out)
        out.write_text(key_text + "\n", encoding="utf-8")
        print(f"[OK] write key -> {out}")


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:  # noqa: BLE001
        print(f"[FAIL] {exc}")
        raise SystemExit(1)
