#!/usr/bin/env python3
from __future__ import annotations

import argparse
import importlib.util
import json
import re
import shutil
import sys
from datetime import datetime
from pathlib import Path
from typing import Dict, List, Optional, Set, Tuple

SOURCE_RE = re.compile("^\u25c7([^\u25c7]+)\u25c7(.*)$")
TRANS_RE = re.compile("^\u25c6([^\u25c6]+)\u25c6(.*)$")
INLINE_RUBY_RE = re.compile(r"@ruby([^@]+?)@([^@]*)@")
SPEAKER_RE = re.compile("^\u3010([^\u3011]+)\u3011(.*)$")
JP_QUOTE_SPEAKER_RE = re.compile(r"^([^\u300c\u300d\u300e\u300f\uff08\uff09\r\n]{1,48})\u300c([\s\S]*)\u300d$")
JP_QUOTE_OPEN_SPEAKER_RE = re.compile(r"^([^\u300c\u300d\u300e\u300f\uff08\uff09\r\n]{1,48})\u300c([\s\S]*)$")
JP_DQUOTE_SPEAKER_RE = re.compile(r"^([^\u300c\u300d\u300e\u300f\uff08\uff09\r\n]{1,48})\u300e([\s\S]*)\u300f$")
JP_DQUOTE_OPEN_SPEAKER_RE = re.compile(r"^([^\u300c\u300d\u300e\u300f\uff08\uff09\r\n]{1,48})\u300e([\s\S]*)$")
JP_PAREN_SPEAKER_RE = re.compile(r"^([^\u300c\u300d\u300e\u300f\uff08\uff09\r\n]{1,48})\uff08([\s\S]*)\uff09$")
JP_PAREN_OPEN_SPEAKER_RE = re.compile(r"^([^\u300c\u300d\u300e\u300f\uff08\uff09\r\n]{1,48})\uff08([\s\S]*)$")

YSTB_SOURCE_TYPED_RE = re.compile("^\u25c7([0-9A-Fa-f]{1,8})\u25c7([A-Za-z_]+)\u25c7(.*)$")
YSTB_TRANS_TYPED_RE = re.compile("^\u25c6([0-9A-Fa-f]{1,8})\u25c6([A-Za-z_]+)\u25c6(.*)$")


def _load_ystb_tool_exports():
    this_dir = Path(__file__).resolve().parent
    candidates = [
        this_dir / "YSTB_Tool_v490.py",
        this_dir / "ystb_tool.py",
    ]
    for candidate in candidates:
        if not candidate.exists():
            continue
        spec = importlib.util.spec_from_file_location(candidate.stem, candidate)
        if spec is None or spec.loader is None:
            continue
        module = importlib.util.module_from_spec(spec)
        sys.modules[candidate.stem] = module
        spec.loader.exec_module(module)
        return (
            module.build_original_index,
            module.compile_one,
            module.pick_original_for_translation,
        )
    raise ModuleNotFoundError("YSTB tool module not found beside texttwolines.py")


def append_error_log(log_path: Path, source_path: Path, err: Exception) -> None:
    log_path.parent.mkdir(parents=True, exist_ok=True)
    header = (
        "# texttwolines error log\n"
        f"# generated_at: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n"
        "# format: <source_path>\\t<error>\n"
    )
    need_header = (not log_path.exists()) or (log_path.stat().st_size == 0)
    with log_path.open("a", encoding="utf-8") as f:
        if need_header:
            f.write(header)
        f.write(f"{source_path}\t{err}\n")


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
            if nxt == "f":
                out.append("\f")
                i += 2
                continue
            if nxt == "v":
                out.append("\v")
                i += 2
                continue
            if nxt == "\\":
                out.append("\\")
                i += 2
                continue
        out.append(ch)
        i += 1
    return "".join(out)


def strip_inline_ruby(text: str) -> str:
    return INLINE_RUBY_RE.sub(lambda m: m.group(1), text)


def is_standalone_ruby_prefix(text: str) -> bool:
    return INLINE_RUBY_RE.fullmatch(text.strip()) is not None


def parse_bin_txt(path: Path) -> List[Tuple[str, str]]:
    lines = path.read_text(encoding="utf-8").splitlines()
    out: List[Tuple[str, str]] = []

    pending_idx: Optional[str] = None
    for line in lines:
        ms = SOURCE_RE.match(line)
        if ms:
            pending_idx = ms.group(1)
            continue

        mt = TRANS_RE.match(line)
        if mt:
            idx = mt.group(1)
            txt = unescape_single_line_text(mt.group(2))
            if pending_idx and pending_idx != idx:
                raise ValueError(f"source/trans index mismatch: {pending_idx} vs {idx}")
            out.append((idx, txt))
            pending_idx = None

    return out


def escape_single_line_text(text: str) -> str:
    return (
        text.replace("\\", "\\\\")
        .replace("\r", "\\r")
        .replace("\n", "\\n")
        .replace("\f", "\\f")
        .replace("\v", "\\v")
    )


def apply_blocks(payload: Dict[str, object], blocks: List[Tuple[str, str]]) -> Tuple[int, int]:
    entries: List[Dict[str, object]] = list(payload.get("entries", []))
    idx_map: Dict[int, Dict[str, object]] = {}
    for e in entries:
        idx_map[int(e["index"])] = e

    touched = 0
    split_blocks = 0

    for block_idx, text in blocks:
        parts = [x for x in block_idx.split("+") if x]
        if not parts:
            continue
        indices = [int(x, 16) for x in parts]

        if len(indices) == 1:
            e = idx_map.get(indices[0])
            if e is None:
                continue
            e["text"] = text
            e["text_plain"] = strip_inline_ruby(text)
            touched += 1
            continue

        i0, i1 = indices[0], indices[1]
        e0 = idx_map.get(i0)
        e1 = idx_map.get(i1)
        if e0 is None:
            continue

        t0 = text
        t1 = e1.get("text", "") if e1 is not None else ""

        orig0 = str(e0.get("text", ""))
        if is_standalone_ruby_prefix(orig0):
            m = INLINE_RUBY_RE.match(text)
            if m:
                t0 = m.group(0)
                t1 = text[m.end() :]
                split_blocks += 1
            else:
                t0 = text
                t1 = e1.get("text", "") if e1 is not None else ""

        e0["text"] = t0
        e0["text_plain"] = strip_inline_ruby(t0)
        touched += 1

        if e1 is not None:
            e1["text"] = t1
            e1["text_plain"] = strip_inline_ruby(t1)
            touched += 1

    payload["entries"] = entries
    return touched, split_blocks


def apply_one(base_json: Path, bin_txt: Path, out_json: Path) -> Tuple[int, int]:
    payload = json.loads(base_json.read_text(encoding="utf-8"))
    if payload.get("format") != "sys3324_text_v1":
        raise ValueError("unsupported JSON format for apply; expected sys3324_text_v1")

    blocks = parse_bin_txt(bin_txt)
    touched, split_blocks = apply_blocks(payload, blocks)

    out_json.parent.mkdir(parents=True, exist_ok=True)
    out_json.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    return touched, split_blocks


def _build_json_stem_index(base_dir: Path) -> Dict[str, List[Path]]:
    out: Dict[str, List[Path]] = {}
    for p in base_dir.rglob("*.json"):
        out.setdefault(p.stem.lower(), []).append(p)
    return out


def _pick_json_for_txt(base_dir: Path, txt_dir: Path, txt_file: Path, stem_idx: Dict[str, List[Path]]) -> Tuple[Path, str]:
    rel = txt_file.relative_to(txt_dir)
    direct = (base_dir / rel).with_suffix(".json")
    if direct.exists():
        return direct, "direct"

    candidates = stem_idx.get(txt_file.stem.lower(), [])
    if len(candidates) == 1:
        return candidates[0], "stem_unique"
    if len(candidates) > 1:
        raise ValueError(f"multiple same-stem JSON found, cannot disambiguate: {txt_file.name}")
    raise FileNotFoundError(f"matching JSON not found: {txt_file.name}")


def _hex8(v: int) -> str:
    return f"{v:08X}"


def _normalize_name(name: str) -> str:
    t = name.strip()
    if not t:
        return ""
    if t.startswith("\u3010") and t.endswith("\u3011"):
        return t
    return f"\u3010{t}\u3011"


def _split_name_msg(item: Dict[str, object], dumped_names: Optional[Set[str]] = None) -> Tuple[str, str]:
    speaker = item.get("speaker")
    body = item.get("body")

    text = str(item.get("text", ""))
    if not text and isinstance(body, str):
        text = body

    def is_dumped_name(raw_name: str) -> bool:
        if dumped_names is None:
            return True
        name = _normalize_name(raw_name)
        return bool(name) and (name in dumped_names)

    if isinstance(speaker, str):
        body_text = body if isinstance(body, str) else ""
        m_text = SPEAKER_RE.match(text)
        if m_text:
            return _normalize_name(m_text.group(1)), m_text.group(2)
        return _normalize_name(speaker), body_text

    m = SPEAKER_RE.match(text)
    if m and is_dumped_name(m.group(1)):
        return _normalize_name(m.group(1)), m.group(2)

    m = JP_QUOTE_SPEAKER_RE.match(text)
    if m and is_dumped_name(m.group(1)):
        return _normalize_name(m.group(1)), f"\u300c{m.group(2)}\u300d"

    m = JP_QUOTE_OPEN_SPEAKER_RE.match(text)
    if m and ("\u300d" not in m.group(2)) and is_dumped_name(m.group(1)):
        return _normalize_name(m.group(1)), f"\u300c{m.group(2)}"

    m = JP_DQUOTE_SPEAKER_RE.match(text)
    if m and is_dumped_name(m.group(1)):
        return _normalize_name(m.group(1)), f"\u300e{m.group(2)}\u300f"

    m = JP_DQUOTE_OPEN_SPEAKER_RE.match(text)
    if m and ("\u300f" not in m.group(2)) and is_dumped_name(m.group(1)):
        return _normalize_name(m.group(1)), f"\u300e{m.group(2)}"

    m = JP_PAREN_SPEAKER_RE.match(text)
    if m and is_dumped_name(m.group(1)):
        return _normalize_name(m.group(1)), f"\uff08{m.group(2)}\uff09"

    m = JP_PAREN_OPEN_SPEAKER_RE.match(text)
    if m and ("\uff09" not in m.group(2)) and is_dumped_name(m.group(1)):
        return _normalize_name(m.group(1)), f"\uff08{m.group(2)}"

    if isinstance(body, str):
        return "", body
    return "", text


def _classify_ystb_text_role(name: str, msg: str) -> str:
    if name.strip():
        if msg.strip():
            return "name_msg"
        return "name"
    if msg.strip():
        return "msg"
    return "other"


def _collect_dumped_names_from_entries(entries: List[Dict[str, object]]) -> Set[str]:
    out: Set[str] = set()
    for item in entries:
        if not isinstance(item, dict):
            continue

        mode = str(item.get("extract_mode", "")).upper()
        role = str(item.get("text_role", "")).strip().lower()
        is_name_def = mode.startswith("SET_VALUE:ES.CHAR.NAME") and ("MARK.SET" not in mode)
        if not is_name_def and role != "name":
            continue

        speaker = item.get("speaker")
        if isinstance(speaker, str):
            name = _normalize_name(speaker)
            if name:
                out.add(name)

        text = str(item.get("text", "")).strip()
        if text:
            if text.startswith("\u3010") and text.endswith("\u3011") and len(text) >= 2:
                text = text[1:-1]
            name = _normalize_name(text)
            if name:
                out.add(name)
    return out


def _collect_dumped_names_from_json_files(json_files: List[Path]) -> Set[str]:
    out: Set[str] = set()
    for p in json_files:
        try:
            payload = json.loads(p.read_text(encoding="utf-8-sig"))
        except Exception:  # noqa: BLE001
            continue
        if str(payload.get("format", "")).upper() != "YSTB":
            continue
        entries = payload.get("text_entries", [])
        if not isinstance(entries, list):
            continue
        out.update(_collect_dumped_names_from_entries(entries))
    return out


def _ystb_entry_index(item: Dict[str, object], payload: Dict[str, object]) -> int:
    sections = payload.get("sections", {})
    section1_size = int(sections.get("section1_size", 0))
    section2_size = int(sections.get("section2_size", 0))
    sec3_base = 0x20 + section1_size + section2_size

    if "offset" in item:
        return sec3_base + int(item["offset"])
    if "record_index" in item:
        return int(item["record_index"])
    if "id" in item:
        return int(item["id"])
    return 0


def export_ystb_one(json_path: Path, out_txt: Path, shared_dumped_names: Optional[Set[str]] = None) -> int:
    payload = json.loads(json_path.read_text(encoding="utf-8-sig"))
    if str(payload.get("format", "")).upper() != "YSTB":
        raise ValueError("unsupported JSON format: export-ystb requires format=YSTB")

    entries = payload.get("text_entries", [])
    if not isinstance(entries, list):
        raise ValueError("JSON missing text_entries list")

    local_dumped_names = _collect_dumped_names_from_entries(entries)
    if shared_dumped_names is None:
        active_dumped_names = local_dumped_names
    else:
        active_dumped_names = set(shared_dumped_names)
        active_dumped_names.update(local_dumped_names)

    lines: List[str] = []
    count = 0
    for item in entries:
        if not isinstance(item, dict):
            continue
        idx = _hex8(_ystb_entry_index(item, payload))
        name, msg = _split_name_msg(item, active_dumped_names)

        if name:
            n = escape_single_line_text(name)
            lines.append(f"\u25c7{idx}\u25c7name\u25c7{n}")
            lines.append(f"\u25c6{idx}\u25c6name\u25c6{n}")
            lines.append("")
            count += 1

        if msg.strip():
            m = escape_single_line_text(msg)
            lines.append(f"\u25c7{idx}\u25c7msg\u25c7{m}")
            lines.append(f"\u25c6{idx}\u25c6msg\u25c6{m}")
            lines.append("")
            count += 1

    if not lines:
        if out_txt.exists():
            out_txt.unlink()
        return 0

    out_txt.parent.mkdir(parents=True, exist_ok=True)
    out_txt.write_text("\n".join(lines).rstrip() + "\n", encoding="utf-8")
    return count


def export_ystb_cmd(args: argparse.Namespace) -> None:
    inp = Path(args.input_json)
    out = Path(args.out_txt)
    error_log = Path(args.error_log) if args.error_log else None

    if inp.is_file():
        out_file = out
        if out.suffix.lower() != ".txt":
            out_file = out / f"{inp.stem}.txt"
        try:
            n = export_ystb_one(inp, out_file)
            if n == 0:
                print(f"[SKIP] {inp}: no exportable name/msg entries")
            else:
                print(f"[OK] export done blocks={n} out={out_file}")
        except Exception as e:  # noqa: BLE001
            if error_log is not None:
                append_error_log(error_log, inp, e)
            raise
        return

    if not inp.is_dir():
        raise ValueError(f"input not found: {inp}")

    out.mkdir(parents=True, exist_ok=True)
    json_files = sorted(p for p in inp.rglob("*.json") if p.is_file())
    if not json_files:
        raise RuntimeError(f"no json files found in: {inp}")
    shared_dumped_names = _collect_dumped_names_from_json_files(json_files)

    ok = 0
    skip = 0
    fail = 0
    for p in json_files:
        try:
            payload = json.loads(p.read_text(encoding="utf-8-sig"))
            if str(payload.get("format", "")).upper() != "YSTB":
                print(f"[SKIP] {p}: non-YSTB JSON")
                skip += 1
                continue
            rel = p.relative_to(inp)
            out_file = (out / rel).with_suffix(".txt")
            n = export_ystb_one(p, out_file, shared_dumped_names)
            if n == 0:
                print(f"[SKIP] {p}: no exportable name/msg entries")
                skip += 1
            else:
                print(f"[OK] {p} -> {out_file} blocks={n}")
                ok += 1
        except Exception as e:  # noqa: BLE001
            if error_log is not None:
                append_error_log(error_log, p, e)
            print(f"[FAIL] {p}: {e}")
            fail += 1
    print(f"[OK] batch export done ok={ok} skip={skip} fail={fail} out={out}")


def parse_ystb_txt(path: Path) -> Dict[Tuple[int, str], str]:
    lines = path.read_text(encoding="utf-8").splitlines()
    out: Dict[Tuple[int, str], str] = {}
    pending: Optional[Tuple[int, str]] = None

    for line in lines:
        ms = YSTB_SOURCE_TYPED_RE.match(line)
        if ms:
            key = (int(ms.group(1), 16), ms.group(2).strip().lower())
            pending = key
            continue

        mt = YSTB_TRANS_TYPED_RE.match(line)
        if mt:
            key = (int(mt.group(1), 16), mt.group(2).strip().lower())
            text = unescape_single_line_text(mt.group(3))

            if pending is not None and pending != key:
                raise ValueError(f"source/trans key mismatch: {pending} vs {key}")

            if key[1] not in {"name", "msg"}:
                pending = None
                continue

            old = out.get(key)
            if old is not None and old != text:
                out[key] = text
            else:
                out[key] = text
            pending = None

    return out


def _speaker_from_name(name_line: str) -> str:
    t = name_line.strip()
    if t.startswith("\u3010") and t.endswith("\u3011") and len(t) >= 2:
        t = t[1:-1]
    return t.strip()


def _detect_name_msg_style(item: Dict[str, object], dumped_names: Optional[Set[str]] = None) -> str:
    text = str(item.get("text", ""))

    def is_dumped_name(raw_name: str) -> bool:
        if dumped_names is None:
            return True
        name = _normalize_name(raw_name)
        return bool(name) and (name in dumped_names)

    m = SPEAKER_RE.match(text)
    if m and is_dumped_name(m.group(1)):
        return "bracket"

    m = JP_QUOTE_SPEAKER_RE.match(text)
    if m and is_dumped_name(m.group(1)):
        return "quote"

    m = JP_QUOTE_OPEN_SPEAKER_RE.match(text)
    if m and ("\u300d" not in m.group(2)) and is_dumped_name(m.group(1)):
        return "quote_open"

    m = JP_DQUOTE_SPEAKER_RE.match(text)
    if m and is_dumped_name(m.group(1)):
        return "dquote"

    m = JP_DQUOTE_OPEN_SPEAKER_RE.match(text)
    if m and ("\u300f" not in m.group(2)) and is_dumped_name(m.group(1)):
        return "dquote_open"

    m = JP_PAREN_SPEAKER_RE.match(text)
    if m and is_dumped_name(m.group(1)):
        return "paren"

    m = JP_PAREN_OPEN_SPEAKER_RE.match(text)
    if m and ("\uff09" not in m.group(2)) and is_dumped_name(m.group(1)):
        return "paren_open"

    speaker = item.get("speaker")
    if isinstance(speaker, str) and speaker.strip():
        return "bracket"
    return "plain"


def _write_name_msg_to_item(item: Dict[str, object], style: str, new_name: str, new_msg: str) -> None:
    if new_name.strip():
        speaker = _speaker_from_name(new_name)
        if style in {"quote", "quote_open", "dquote", "dquote_open", "paren", "paren_open"}:
            item.pop("speaker", None)
            item.pop("body", None)
            item["text"] = f"{speaker}{new_msg}"
        else:
            item["speaker"] = speaker
            item["body"] = new_msg
            item["text"] = f"\u3010{speaker}\u3011{new_msg}"
    else:
        item["speaker"] = None
        item["body"] = new_msg
        item["text"] = new_msg


def apply_ystb_txt_payload(
    payload: Dict[str, object],
    edits: Dict[Tuple[int, str], str],
    shared_dumped_names: Optional[Set[str]] = None,
) -> int:
    if str(payload.get("format", "")).upper() != "YSTB":
        raise ValueError("unsupported JSON format for apply-ystb; expected format=YSTB")

    entries = payload.get("text_entries", [])
    if not isinstance(entries, list):
        raise ValueError("JSON missing text_entries list")

    local_dumped_names = _collect_dumped_names_from_entries(entries)
    if shared_dumped_names is None:
        active_dumped_names = local_dumped_names
    else:
        active_dumped_names = set(shared_dumped_names)
        active_dumped_names.update(local_dumped_names)

    touched = 0
    for item in entries:
        if not isinstance(item, dict):
            continue

        idx = _ystb_entry_index(item, payload)
        key_name = (idx, "name")
        key_msg = (idx, "msg")

        old_name, old_msg = _split_name_msg(item, active_dumped_names)
        has_name_edit = key_name in edits
        has_msg_edit = key_msg in edits
        if not has_name_edit and not has_msg_edit:
            continue

        new_name = edits.get(key_name, old_name)
        new_msg = edits.get(key_msg, old_msg)

        if has_name_edit:
            name_for_compare = new_name
        else:
            name_for_compare = old_name

        if name_for_compare != old_name or (has_msg_edit and new_msg != old_msg):
            style = _detect_name_msg_style(item, active_dumped_names)
            _write_name_msg_to_item(item, style, new_name, new_msg)
            item["text_role"] = _classify_ystb_text_role(new_name, new_msg)
            touched += 1

    return touched


def apply_ystb_one(
    base_json: Path,
    txt_file: Path,
    out_json: Path,
    shared_dumped_names: Optional[Set[str]] = None,
) -> int:
    payload = json.loads(base_json.read_text(encoding="utf-8-sig"))
    edits = parse_ystb_txt(txt_file)
    touched = apply_ystb_txt_payload(payload, edits, shared_dumped_names)

    out_json.parent.mkdir(parents=True, exist_ok=True)
    out_json.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    return touched


def apply_ystb_cmd(args: argparse.Namespace) -> None:
    base_json = Path(args.base_json)
    in_txt = Path(args.bin_txt)
    out_json = Path(args.out_json)
    error_log = Path(args.error_log) if args.error_log else None

    if base_json.is_file() and in_txt.is_file():
        out_file = out_json
        if out_json.suffix.lower() != ".json":
            out_file = out_json / base_json.name
        try:
            touched = apply_ystb_one(base_json, in_txt, out_file)
            print(f"[OK] {in_txt} -> {out_file} entries={touched}")
        except Exception as e:  # noqa: BLE001
            if error_log is not None:
                append_error_log(error_log, in_txt, e)
            raise
        return

    if not (base_json.is_dir() and in_txt.is_dir()):
        raise ValueError("apply-ystb requires file+file or dir+dir")

    out_json.mkdir(parents=True, exist_ok=True)
    txt_files = sorted(p for p in in_txt.rglob("*.txt") if p.is_file())
    if not txt_files:
        raise RuntimeError(f"no txt files found in: {in_txt}")

    stem_idx = _build_json_stem_index(base_json)
    base_json_files = sorted(p for p in base_json.rglob("*.json") if p.is_file())
    shared_dumped_names = _collect_dumped_names_from_json_files(base_json_files)
    ok = 0
    skip = 0
    fail = 0
    for t in txt_files:
        try:
            matched_json, match_mode = _pick_json_for_txt(base_json, in_txt, t, stem_idx)
            rel = t.relative_to(in_txt)
            out_file = (out_json / rel).with_suffix(".json")
            touched = apply_ystb_one(matched_json, t, out_file, shared_dumped_names)
            print(f"[OK] {t} -> {out_file} entries={touched} match={match_mode}")
            ok += 1
        except (FileNotFoundError, ValueError) as e:
            if error_log is not None:
                append_error_log(error_log, t, e)
            print(f"[SKIP] {t}: {e}")
            skip += 1
        except Exception as e:  # noqa: BLE001
            if error_log is not None:
                append_error_log(error_log, t, e)
            print(f"[FAIL] {t}: {e}")
            fail += 1

    print(f"[OK] apply-ystb done ok={ok} skip={skip} fail={fail} out={out_json}")


def repack_ystb_cmd(args: argparse.Namespace) -> None:
    (
        build_original_index,
        compile_one,
        pick_original_for_translation,
    ) = _load_ystb_tool_exports()

    original = Path(args.original_ybn)
    base_json = Path(args.base_json)
    trans_txt = Path(args.trans_txt)
    out_ybn = Path(args.out_ybn)
    temp_json = Path(args.temp_json_dir) if args.temp_json_dir else (out_ybn / "_tmp_apply_json")
    error_log = Path(args.error_log) if args.error_log else None
    filter_path = Path(args.filter_text) if args.filter_text else None

    if not original.is_dir():
        raise ValueError(f"original ybn dir not found: {original}")
    if not base_json.is_dir():
        raise ValueError(f"base json dir not found: {base_json}")
    if not trans_txt.is_dir():
        raise ValueError(f"translation txt dir not found: {trans_txt}")

    if temp_json.exists():
        shutil.rmtree(temp_json)
    temp_json.mkdir(parents=True, exist_ok=True)

    txt_files = sorted(p for p in trans_txt.rglob("*.txt") if p.is_file())
    if not txt_files:
        raise RuntimeError(f"no txt files found in: {trans_txt}")

    stem_idx = _build_json_stem_index(base_json)
    base_json_files = sorted(p for p in base_json.rglob("*.json") if p.is_file())
    shared_dumped_names = _collect_dumped_names_from_json_files(base_json_files)
    applied_jsons: List[Path] = []

    apply_ok = 0
    apply_skip = 0
    apply_fail = 0
    for t in txt_files:
        try:
            matched_json, match_mode = _pick_json_for_txt(base_json, trans_txt, t, stem_idx)
            rel = t.relative_to(trans_txt)
            out_file = (temp_json / rel).with_suffix(".json")
            touched = apply_ystb_one(matched_json, t, out_file, shared_dumped_names)
            print(f"[OK] apply {t} -> {out_file} entries={touched} match={match_mode}")
            applied_jsons.append(out_file)
            apply_ok += 1
        except (FileNotFoundError, ValueError) as e:
            if error_log is not None:
                append_error_log(error_log, t, e)
            print(f"[SKIP] apply {t}: {e}")
            apply_skip += 1
        except Exception as e:  # noqa: BLE001
            if error_log is not None:
                append_error_log(error_log, t, e)
            print(f"[FAIL] apply {t}: {e}")
            apply_fail += 1

    if not applied_jsons:
        print("[SKIP] no txt matched to json, nothing to repack")
        return

    out_ybn.mkdir(parents=True, exist_ok=True)
    by_key, by_stem = build_original_index(original)

    pack_ok = 0
    pack_skip = 0
    pack_fail = 0
    for trans_file in sorted(applied_jsons, key=lambda p: str(p).lower()):
        try:
            src_ybn, mode, stem, rel_parent = pick_original_for_translation(
                original_dir=original,
                translation_dir=temp_json,
                translation_file=trans_file,
                by_key=by_key,
                by_stem=by_stem,
            )
            out_file = out_ybn / rel_parent / f"{stem}.ybn"
            compile_one(
                original_ybn=src_ybn,
                translation=trans_file,
                output_ybn=out_file,
                source_encoding=args.source_encoding,
                target_encoding=args.text_encoding,
                filter_text_path=filter_path,
                encoding_errors=args.encoding_errors,
                force_reencode_all=args.force_reencode_all,
            )
            print(f"[OK] pack {trans_file} -> {out_file} match={mode}")
            pack_ok += 1
        except Exception as e:  # noqa: BLE001
            if error_log is not None:
                append_error_log(error_log, trans_file, e)
            print(f"[FAIL] pack {trans_file}: {e}")
            pack_fail += 1

    print(
        "[OK] repack-ystb done "
        f"apply_ok={apply_ok} apply_skip={apply_skip} apply_fail={apply_fail} "
        f"pack_ok={pack_ok} pack_skip={pack_skip} pack_fail={pack_fail} out={out_ybn}"
    )


def apply_cmd(args: argparse.Namespace) -> None:
    base_json = Path(args.base_json)
    bin_txt = Path(args.bin_txt)
    out_json = Path(args.out_json)
    error_log = Path(args.error_log) if args.error_log else None

    if base_json.is_file() and bin_txt.is_file():
        out_file = out_json
        if out_json.suffix.lower() != ".json":
            out_file = out_json / base_json.name
        try:
            touched, split_blocks = apply_one(base_json, bin_txt, out_file)
            print(f"[OK] apply done entries={touched} ruby_split={split_blocks} out={out_file}")
        except Exception as e:  # noqa: BLE001
            if error_log is not None:
                append_error_log(error_log, bin_txt, e)
            raise
        return

    if not (base_json.is_dir() and bin_txt.is_dir()):
        raise ValueError("apply requires file+file or dir+dir")

    out_dir = out_json
    out_dir.mkdir(parents=True, exist_ok=True)

    txt_files = sorted(p for p in bin_txt.rglob("*.txt") if p.is_file())
    if not txt_files:
        raise RuntimeError(f"no txt files found in: {bin_txt}")

    stem_idx = _build_json_stem_index(base_json)
    ok = 0
    skip = 0
    fail = 0
    for t in txt_files:
        try:
            matched_json, match_mode = _pick_json_for_txt(base_json, bin_txt, t, stem_idx)
            rel = t.relative_to(bin_txt)
            out_file = (out_dir / rel).with_suffix(".json")
            touched, split_blocks = apply_one(matched_json, t, out_file)
            print(f"[OK] {t} -> {out_file} entries={touched} ruby_split={split_blocks} match={match_mode}")
            ok += 1
        except (FileNotFoundError, ValueError) as e:
            if error_log is not None:
                append_error_log(error_log, t, e)
            print(f"[SKIP] {t}: {e}")
            skip += 1
        except Exception as e:  # noqa: BLE001
            if error_log is not None:
                append_error_log(error_log, t, e)
            print(f"[FAIL] {t}: {e}")
            fail += 1

    print(f"[OK] apply done ok={ok} skip={skip} fail={fail} out={out_dir}")


def main() -> None:
    known = {"apply", "export-ystb", "apply-ystb", "repack-ystb", "-h", "--help"}
    if len(sys.argv) > 1 and sys.argv[1] not in known:
        sys.argv.insert(1, "apply")

    ap = argparse.ArgumentParser(description="two-line text utility")
    sub = ap.add_subparsers(dest="cmd", required=True)

    # 抄！爽！
    pa = sub.add_parser("apply", help="apply SYS3324 two-line txt to SYS3324 JSON")
    pa.add_argument("base_json", help="base JSON file or directory")
    pa.add_argument("bin_txt", help="two-line TXT file or directory")
    pa.add_argument("out_json", help="output JSON file or directory")
    pa.add_argument("--error-log", default="", help="optional error log path")
    pa.set_defaults(func=apply_cmd)

    pe = sub.add_parser("export-ystb", help="export YSTB JSON to two-line name/msg txt")
    pe.add_argument("input_json", help="YSTB JSON file or directory")
    pe.add_argument("out_txt", help="output txt file or directory")
    pe.add_argument("--error-log", default="", help="optional error log path")
    pe.set_defaults(func=export_ystb_cmd)

    py = sub.add_parser("apply-ystb", help="apply YSTB two-line txt to YSTB JSON")
    py.add_argument("base_json", help="base YSTB JSON file or directory")
    py.add_argument("bin_txt", help="translated two-line txt file or directory")
    py.add_argument("out_json", help="output YSTB JSON file or directory")
    py.add_argument("--error-log", default="", help="optional error log path")
    py.set_defaults(func=apply_ystb_cmd)

    pr = sub.add_parser(
        "repack-ystb",
        help="apply-ystb + compile to YBN; only txt files present in translation dir are repacked",
    )
    pr.add_argument("original_ybn", help="original YBN directory")
    pr.add_argument("base_json", help="base YSTB JSON directory")
    pr.add_argument("trans_txt", help="translated two-line txt directory")
    pr.add_argument("out_ybn", help="output YBN directory")
    pr.add_argument("--temp-json-dir", default="", help="temp JSON dir for apply-ystb (default: <out_ybn>/_tmp_apply_json)")
    pr.add_argument("--source-encoding", default="cp932", help="source text encoding (default: cp932)")
    pr.add_argument("--text-encoding", default="cp932", help="target writeback encoding (default: cp932)")
    pr.add_argument("--filter-text", default="", help="deprecated: ignored (partial source-encoding writeback disabled)")
    pr.add_argument(
        "--encoding-errors",
        default="strict",
        choices=["strict", "replace", "ignore"],
        help="encoding error behavior (default: strict)",
    )
    pr.add_argument(
        "--force-reencode-all",
        action="store_true",
        help="re-encode all editable text entries with target encoding, not only changed lines",
    )
    pr.add_argument("--error-log", default="", help="optional error log path")
    pr.set_defaults(func=repack_ystb_cmd)

    args = ap.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()