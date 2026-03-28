#!/usr/bin/env python3
from __future__ import annotations

import argparse
import struct
from pathlib import Path
from typing import Iterable, Tuple


MAGIC = b"YSTB"
HEADER_SIZE = 0x20


def _xor_range(buf: bytearray, start: int, length: int, key4: bytes) -> None:
    if length <= 0:
        return
    end = start + length
    if start < 0 or end > len(buf):
        raise ValueError(f"range out of bounds: start=0x{start:X} len={length} size={len(buf)}")
    for i in range(length):
        buf[start + i] ^= key4[i & 3]


def xor_ystb_inplace(buf: bytearray, key: int) -> bool:
    if len(buf) < HEADER_SIZE:
        return False

    if bytes(buf[:4]) != MAGIC:
        return False

    key4 = struct.pack("<I", key & 0xFFFFFFFF)
    version = struct.unpack_from("<I", buf, 4)[0]

    if 200 < version < 300:
        code_seg_size, args_seg_size = struct.unpack_from("<II", buf, 8)
        off = HEADER_SIZE
        _xor_range(buf, off, code_seg_size, key4)
        off += code_seg_size
        _xor_range(buf, off, args_seg_size, key4)
    else:
        inst_index_size, args_index_size, args_data_size, line_numbers_size = struct.unpack_from("<IIII", buf, 0x0C)
        off = HEADER_SIZE
        _xor_range(buf, off, inst_index_size, key4)
        off += inst_index_size
        _xor_range(buf, off, args_index_size, key4)
        off += args_index_size
        _xor_range(buf, off, args_data_size, key4)
        off += args_data_size
        _xor_range(buf, off, line_numbers_size, key4)

    return True


def parse_key_text(text: str) -> int:
    t = text.strip()
    if not t:
        raise ValueError("empty key text")
    if t.lower().startswith("0x"):
        return int(t, 16) & 0xFFFFFFFF
    try:
        return int(t, 16) & 0xFFFFFFFF
    except ValueError:
        return int(t, 10) & 0xFFFFFFFF


def read_key_file(path: Path) -> int:
    if not path.exists():
        raise FileNotFoundError(f"key file not found: {path}")
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        t = line.strip()
        if not t:
            continue
        if t.startswith("#"):
            continue
        return parse_key_text(t)
    raise ValueError(f"no key value in key file: {path}")


def iter_input_files(path: Path) -> Iterable[Tuple[Path, Path]]:
    if path.is_file():
        yield path, Path(path.name)
        return
    if not path.is_dir():
        raise FileNotFoundError(f"input path not found: {path}")
    for p in sorted(path.rglob("*"), key=lambda x: str(x).lower()):
        if p.is_file():
            yield p, p.relative_to(path)


def xor_one_file(src: Path, dst: Path, key: int) -> bool:
    data = bytearray(src.read_bytes())
    if not xor_ystb_inplace(data, key):
        return False
    dst.parent.mkdir(parents=True, exist_ok=True)
    dst.write_bytes(data)
    return True


def run_cmd(args: argparse.Namespace) -> None:
    print("Hint: dec [target=ysbin] [output=ysbin_dec], enc [target=ysbin_dec] [output=ysbin_enc]")

    target = Path(args.target)
    output = Path(args.output)

    key = parse_key_text(args.key) if args.key else read_key_file(Path(args.key_file))
    print(f"[INFO] key=0x{key:08X}")

    if target.is_file():
        out_file = output
        if output.exists() and output.is_dir():
            out_file = output / target.name
        elif output.suffix.lower() != ".ybn":
            out_file = output / target.name

        ok = xor_one_file(target, out_file, key)
        if ok:
            print(f"[OK] xor {target} -> {out_file}")
        else:
            print(f"[SKIP] {target}: not YSTB")
        return

    output.mkdir(parents=True, exist_ok=True)
    ok = 0
    skip = 0
    fail = 0
    for src, rel in iter_input_files(target):
        try:
            dst = output / rel
            if xor_one_file(src, dst, key):
                print(f"[OK] xor {src} -> {dst}")
                ok += 1
            else:
                skip += 1
                if args.print_skips:
                    print(f"[SKIP] {src}: not YSTB")
        except Exception as exc:  # noqa: BLE001
            print(f"[FAIL] {src}: {exc}")
            fail += 1

    print(f"[OK] done ok={ok} skip={skip} fail={fail} out={output}")


def build_parser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(description="YSTB XOR tool")
    sub = ap.add_subparsers(dest="cmd", required=True)

    pd = sub.add_parser("dec", help="decode xor: default ysbin -> ysbin_dec")
    pd.add_argument("target", nargs="?", default="ysbin", help="input file or directory (default: ysbin)")
    pd.add_argument("output", nargs="?", default="ysbin_dec", help="output file or directory (default: ysbin_dec)")
    pd.add_argument("--key-file", default="Key.txt", help="key file path (default: Key.txt)")
    pd.add_argument("--key", default="", help="override key text, e.g. 0x12345678")
    pd.add_argument("--print-skips", action="store_true", help="print skipped non-YSTB files")
    pd.set_defaults(func=run_cmd)

    pe = sub.add_parser("enc", help="encode xor: default ysbin_dec_new -> ysbin_enc")
    pe.add_argument("target", nargs="?", default="ysbin_dec_new", help="input file or directory (default: ysbin_dec_new)")
    pe.add_argument("output", nargs="?", default="ysbin_enc", help="output file or directory (default: ysbin_enc)")
    pe.add_argument("--key-file", default="Key.txt", help="key file path (default: Key.txt)")
    pe.add_argument("--key", default="", help="override key text, e.g. 0x12345678")
    pe.add_argument("--print-skips", action="store_true", help="print skipped non-YSTB files")
    pe.set_defaults(func=run_cmd)

    return ap


def main() -> None:
    parser = build_parser()
    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:  # noqa: BLE001
        print(f"[FAIL] {exc}")
        raise SystemExit(1)
