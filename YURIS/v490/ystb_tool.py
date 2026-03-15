#!/usr/bin/env python3
from __future__ import annotations

import argparse
import csv
import json
import re
import struct
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Dict, List, Optional, Sequence, Set, Tuple


HEADER_SIZE = 0x20
RECORD_SIZE = 12
MAGIC_YSTB = b"YSTB"
# 用于判断文本中是否包含日文字符，以辅助文本提取和选项候选判断。覆盖平假名、片假名、常用汉字等
JP_CHAR_RE = re.compile(r"[ぁ-んァ-ヶ一-龯々〆ヵヶ]")


class YstbError(RuntimeError):
    pass


@dataclass
class Section2Record:
    kind: int
    flag: int
    length: int
    offset: int

    def is_valid_range(self, section3_size: int) -> bool:
        if self.length == 0:
            return False
        if self.offset >= section3_size:
            return False
        return (self.offset + self.length) <= section3_size


@dataclass
class YstbBinary:
    magic: bytes
    version: int
    instruction_count: int
    reserved: int
    section1: bytes
    records: List[Section2Record]
    section3: bytes
    section4: bytes

    def to_bytes(self) -> bytes:
        section1_size = len(self.section1)
        section2_size = len(self.records) * RECORD_SIZE
        section3_size = len(self.section3)
        section4_size = len(self.section4)
        header = struct.pack(
            "<4s7I",
            self.magic,
            self.version,
            self.instruction_count,
            section1_size,
            section2_size,
            section3_size,
            section4_size,
            self.reserved,
        )
        section2 = pack_records(self.records)
        return header + self.section1 + section2 + self.section3 + self.section4


@dataclass
class TextEntry:
    text_id: int
    record_index: int
    kind: int
    flag: int
    offset: int
    length: int
    text: str
    option_candidate: bool
    extract_mode: str = "chunk"


@dataclass
class TextEdit:
    text_id: Optional[int]
    record_index: Optional[int]
    text: str


@dataclass
class RangePatch:
    old_off: int
    old_len: int
    new_bytes: bytes
    new_off: int = 0
    delta: int = 0
    owner_record_index: int = -1
    encoding: str = ""

    @property
    def new_len(self) -> int:
        return len(self.new_bytes)

    @property
    def old_end(self) -> int:
        return self.old_off + self.old_len


def append_error_log(log_path: Path, source_path: Path, err: Exception) -> None:
    log_path.parent.mkdir(parents=True, exist_ok=True)
    header = (
        "# ystb_tool error log\n"
        f"# generated_at: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n"
        "# format: <source_path>\\t<error>\n"
    )
    need_header = (not log_path.exists()) or (log_path.stat().st_size == 0)
    with log_path.open("a", encoding="utf-8") as f:
        if need_header:
            f.write(header)
        f.write(f"{source_path}\t{err}\n")


def parse_ystb_bytes(data: bytes, src: Optional[Path] = None) -> YstbBinary:
    where = str(src) if src is not None else "<memory>"
    if len(data) < HEADER_SIZE:
        raise YstbError(f"{where}: file too small")

    magic, version, count, s1, s2, s3, s4, reserved = struct.unpack_from("<4s7I", data, 0)
    if magic != MAGIC_YSTB:
        raise YstbError(f"{where}: unsupported magic {magic!r}, expected {MAGIC_YSTB!r}")
    if s2 % RECORD_SIZE != 0:
        raise YstbError(f"{where}: invalid section2 size {s2}, not multiple of {RECORD_SIZE}")

    expected = HEADER_SIZE + s1 + s2 + s3 + s4
    if expected != len(data):
        raise YstbError(f"{where}: size mismatch header={expected} actual={len(data)}")

    off = HEADER_SIZE
    section1 = data[off : off + s1]
    off += s1
    section2 = data[off : off + s2]
    off += s2
    section3 = data[off : off + s3]
    off += s3
    section4 = data[off : off + s4]

    records: List[Section2Record] = []
    for i in range(0, len(section2), RECORD_SIZE):
        kind, flag, length, rec_off = struct.unpack_from("<HHII", section2, i)
        records.append(Section2Record(kind=kind, flag=flag, length=length, offset=rec_off))

    return YstbBinary(
        magic=magic,
        version=version,
        instruction_count=count,
        reserved=reserved,
        section1=section1,
        records=records,
        section3=section3,
        section4=section4,
    )


def pack_records(records: Sequence[Section2Record]) -> bytes:
    out = bytearray()
    for rec in records:
        out.extend(struct.pack("<HHII", rec.kind, rec.flag, rec.length, rec.offset))
    return bytes(out)


def chunk_is_editable_text(raw: bytes, decoded: str) -> bool:
    if not raw:
        return False
    if b"\x00" in raw:
        return False
    for b in raw:
        if b < 0x20 and b not in (0x09, 0x0A, 0x0D):
            return False
    stripped = decoded.strip()
    if not stripped:
        return False
    for ch in decoded:
        if ord(ch) < 0x20 and ch not in ("\t", "\n", "\r"):
            return False
    return True


def is_option_candidate(text: str) -> bool:
    t = text.strip()
    if not t:
        return False
    if "選択" in t:
        return True
    if t.startswith(("◆", "▼", "▷", "→", "⇒")):
        return True
    if t.startswith(("【", "（", "(")):
        return False
    if len(t) <= 24 and JP_CHAR_RE.search(t) and "。" not in t and "、" not in t:
        return True
    return False



def split_speaker_body(text: str) -> Tuple[Optional[str], str]:
    if text.startswith("【"):
        end = text.find("】")
        if end > 1:
            return text[1:end], text[end + 1 :]
    return None, text


def contains_japanese(text: str) -> bool:
    return JP_CHAR_RE.search(text) is not None


def is_bracketed_name_like(text: str) -> bool:
    t = text.strip()
    if not t.startswith("【"):
        return False
    end = t.find("】")
    if end <= 1:
        return False
    return t.endswith("】") or bool(t[end + 1 :].strip())

def extract_quoted_text_spans(raw: bytes, source_encoding: str) -> List[Tuple[int, int, str]]:
    out: List[Tuple[int, int, str]] = []
    pos = 0
    n = len(raw)
    while pos < n:
        q0 = raw.find(b'"', pos)
        if q0 < 0:
            break
        q1 = raw.find(b'"', q0 + 1)
        if q1 < 0:
            break
        payload = raw[q0 + 1 : q1]
        if payload:
            try:
                text = payload.decode(source_encoding)
            except UnicodeDecodeError:
                pass
            else:
                out.append((q0 + 1, q1, text))
        pos = q1 + 1
    return out


def collect_text_entries(blob: YstbBinary, source_encoding: str) -> List[TextEntry]:
    out: List[TextEntry] = []
    text_id = 0
    sec3 = blob.section3
    direct_record_indices: Set[int] = set()

    # Primary path: direct text chunks
    for idx, rec in enumerate(blob.records):
        if rec.kind != 0 or rec.flag != 0:
            continue
        if not rec.is_valid_range(len(sec3)):
            continue
        raw = sec3[rec.offset : rec.offset + rec.length]
        try:
            text = raw.decode(source_encoding)
        except UnicodeDecodeError:
            continue
        if not chunk_is_editable_text(raw, text):
            continue
        out.append(
            TextEntry(
                text_id=text_id,
                record_index=idx,
                kind=rec.kind,
                flag=rec.flag,
                offset=rec.offset,
                length=rec.length,
                text=text,
                option_candidate=is_option_candidate(text),
                extract_mode="chunk",
            )
        )
        text_id += 1
        direct_record_indices.add(idx)

    for idx, rec in enumerate(blob.records):
        if idx in direct_record_indices:
            continue
        if not rec.is_valid_range(len(sec3)):
            continue

        raw = sec3[rec.offset : rec.offset + rec.length]
        for q_start, q_end, text in extract_quoted_text_spans(raw, source_encoding):
            payload = raw[q_start:q_end]
            if not chunk_is_editable_text(payload, text):
                continue
            if not contains_japanese(text) and not is_bracketed_name_like(text):
                continue
            if not text.strip():
                continue

            out.append(
                TextEntry(
                    text_id=text_id,
                    record_index=idx,
                    kind=rec.kind,
                    flag=rec.flag,
                    offset=rec.offset + q_start,
                    length=q_end - q_start,
                    text=text,
                    option_candidate=is_option_candidate(text),
                    extract_mode="quoted",
                )
            )
            text_id += 1

    return out


def text_entry_to_json_item(e: TextEntry) -> Dict[str, object]:
    speaker, body = split_speaker_body(e.text)
    return {
        "id": e.text_id,
        "record_index": e.record_index,
        "type": e.kind,
        "flag": e.flag,
        "offset": e.offset,
        "length": e.length,
        "option_candidate": e.option_candidate,
        "speaker": speaker,
        "body": body,
        "text": e.text,
    }


def write_json_export(path: Path, blob: YstbBinary, text_entries: Sequence[TextEntry], source_encoding: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    obj = {
        "tool": "ystb_tool",
        "format": "YSTB",
        "magic": blob.magic.decode("ascii", errors="replace"),
        "version": blob.version,
        "instruction_count": blob.instruction_count,
        "source_encoding": source_encoding,
        "sections": {
            "section1_size": len(blob.section1),
            "section2_size": len(blob.records) * RECORD_SIZE,
            "section3_size": len(blob.section3),
            "section4_size": len(blob.section4),
            "record_count": len(blob.records),
        },
        "text_entries": [text_entry_to_json_item(e) for e in text_entries],
    }
    path.write_text(json.dumps(obj, ensure_ascii=False, indent=2), encoding="utf-8")


def write_ins_tsv(path: Path, text_entries: Sequence[TextEntry]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="") as f:
        writer = csv.writer(f, delimiter="\t")
        writer.writerow(["id", "record_index", "type", "flag", "offset", "length", "option_candidate", "text"])
        for e in text_entries:
            writer.writerow(
                [
                    e.text_id,
                    e.record_index,
                    e.kind,
                    e.flag,
                    e.offset,
                    e.length,
                    1 if e.option_candidate else 0,
                    e.text,
                ]
            )



def compose_text_from_json_item(path: Path, item: Dict[str, object], idx: int) -> str:
    has_speaker = "speaker" in item
    has_body = "body" in item
    if has_speaker or has_body:
        speaker = item.get("speaker")
        body = item.get("body")

        if speaker is None:
            if body is None:
                return ""
            if not isinstance(body, str):
                raise YstbError(f"{path}: text_entries[{idx}].body is not string")
            return body

        if not isinstance(speaker, str):
            raise YstbError(f"{path}: text_entries[{idx}].speaker is not string")

        if body is None:
            body_text = ""
        elif isinstance(body, str):
            body_text = body
        else:
            raise YstbError(f"{path}: text_entries[{idx}].body is not string")

        return f"【{speaker}】{body_text}"

    text = item.get("text")
    if not isinstance(text, str):
        raise YstbError(f"{path}: text_entries[{idx}].text is not string")
    return text


def load_text_edits_from_json(path: Path) -> List[TextEdit]:
    obj = json.loads(path.read_text(encoding="utf-8-sig"))
    entries = obj.get("text_entries")
    if not isinstance(entries, list):
        raise YstbError(f"{path}: JSON missing text_entries list")

    out: List[TextEdit] = []
    for i, item in enumerate(entries):
        if not isinstance(item, dict):
            raise YstbError(f"{path}: text_entries[{i}] is not object")
        text = compose_text_from_json_item(path, item, i)
        text_id = parse_optional_int(item.get("id"), path, f"text_entries[{i}].id")
        rec_idx = parse_optional_int(item.get("record_index"), path, f"text_entries[{i}].record_index")
        out.append(TextEdit(text_id=text_id, record_index=rec_idx, text=text))
    return out


def load_text_edits_from_ins_tsv(path: Path) -> List[TextEdit]:
    out: List[TextEdit] = []
    with path.open("r", encoding="utf-8", newline="") as f:
        reader = csv.DictReader(f, delimiter="\t")
        required = {"id", "record_index", "text"}
        if reader.fieldnames is None or not required.issubset(set(reader.fieldnames)):
            raise YstbError(f"{path}: invalid TSV header, required columns: {sorted(required)}")
        for i, row in enumerate(reader):
            text = row.get("text", "")
            text_id = parse_optional_int(row.get("id"), path, f"row[{i}].id")
            rec_idx = parse_optional_int(row.get("record_index"), path, f"row[{i}].record_index")
            out.append(TextEdit(text_id=text_id, record_index=rec_idx, text=text))
    return out


def parse_optional_int(value: object, path: Path, field_name: str) -> Optional[int]:
    if value is None:
        return None
    if isinstance(value, int):
        return value
    if isinstance(value, str):
        t = value.strip()
        if t == "":
            return None
        try:
            return int(t, 10)
        except ValueError as exc:
            raise YstbError(f"{path}: invalid integer in {field_name}: {value!r}") from exc
    raise YstbError(f"{path}: invalid integer type in {field_name}: {type(value).__name__}")


def load_text_edits(path: Path) -> List[TextEdit]:
    lower = path.name.lower()
    if lower.endswith(".json"):
        return load_text_edits_from_json(path)
    if lower.endswith(".ins.tsv"):
        return load_text_edits_from_ins_tsv(path)
    raise YstbError(f"{path}: unsupported translation format, expected .json or .ins.tsv")


def load_filter_text(explicit_path: Optional[Path], translation_path: Path) -> Set[str]:
    chosen: Optional[Path] = None
    if explicit_path is not None:
        chosen = explicit_path
    else:
        sibling = translation_path.parent / "filter_text.txt"
        if sibling.exists():
            chosen = sibling
    if chosen is None:
        return set()
    if not chosen.exists():
        raise YstbError(f"filter_text not found: {chosen}")

    out: Set[str] = set()
    for line in chosen.read_text(encoding="utf-8-sig").splitlines():
        t = line.strip()
        if not t:
            continue
        if t.startswith("#"):
            continue
        out.add(t)
    return out


def ranges_overlap(a_off: int, a_len: int, b_off: int, b_len: int) -> bool:
    a_end = a_off + a_len
    b_end = b_off + b_len
    return not (a_end <= b_off or b_end <= a_off)


def truncate_encoded_prefix(data: bytes, max_len: int, encoding: str) -> bytes:
    if len(data) <= max_len:
        return data
    if max_len <= 0:
        return b""

    cut = data[:max_len]
    if not encoding:
        return cut

    while cut:
        try:
            cut.decode(encoding, errors="strict")
            return cut
        except UnicodeDecodeError:
            cut = cut[:-1]
    return b""


def build_edit_map(edits: Sequence[TextEdit], source_entries: Sequence[TextEntry], src: Path) -> Dict[int, str]:
    by_id = {e.text_id: e for e in source_entries}
    by_record: Dict[int, List[TextEntry]] = {}
    for e in source_entries:
        by_record.setdefault(e.record_index, []).append(e)

    out: Dict[int, str] = {}
    for i, edit in enumerate(edits):
        target: Optional[TextEntry] = None

        if edit.text_id is not None:
            target = by_id.get(edit.text_id)

        if target is None and edit.record_index is not None:
            candidates = by_record.get(edit.record_index, [])
            if len(candidates) == 1:
                target = candidates[0]
            elif len(candidates) > 1:
                raise YstbError(
                    f"{src}: ambiguous record_index on row {i}: {edit.record_index}; "
                    "multiple text entries share this record, use id to disambiguate"
                )

        if target is None:
            raise YstbError(f"{src}: cannot map edit row {i} to source text entry")

        old = out.get(target.text_id)
        if old is not None and old != edit.text:
            raise YstbError(f"{src}: conflicting edits on text id {target.text_id}")
        out[target.text_id] = edit.text
    return out


def rebuild_with_text_edits(
    blob: YstbBinary,
    source_entries: Sequence[TextEntry],
    edit_map: Dict[int, str],
    source_encoding: str,
    target_encoding: str,
    encoding_errors: str,
    force_reencode_all: bool = False,
) -> YstbBinary:
    if encoding_errors not in {"strict", "replace", "ignore"}:
        raise YstbError(f"invalid encoding_errors: {encoding_errors}")

    source_by_id = {e.text_id: e for e in source_entries}
    patches_by_range: Dict[Tuple[int, int], RangePatch] = {}
    range_text_value: Dict[Tuple[int, int], str] = {}

    for text_id, new_text in edit_map.items():
        src_entry = source_by_id.get(text_id)
        if src_entry is None:
            raise YstbError(f"text id {text_id} is not editable text in source")
        if (not force_reencode_all) and new_text == src_entry.text:
            continue

        enc = target_encoding
        try:
            new_bytes = new_text.encode(enc, errors=encoding_errors)
        except UnicodeEncodeError as exc:
            raise YstbError(
                f"text id {text_id} encode failed with {enc}: {exc}"
            ) from exc

        key = (src_entry.offset, src_entry.length)
        old_text = range_text_value.get(key)
        if old_text is not None and old_text != new_text:
            raise YstbError(
                f"same source range has conflicting edited text: text id {text_id} "
                f"(record {src_entry.record_index})"
            )
        range_text_value[key] = new_text
        patches_by_range[key] = RangePatch(
            old_off=src_entry.offset,
            old_len=src_entry.length,
            new_bytes=new_bytes,
            owner_record_index=src_entry.record_index,
            encoding=enc,
        )

    if not patches_by_range:
        return YstbBinary(
            magic=blob.magic,
            version=blob.version,
            instruction_count=blob.instruction_count,
            reserved=blob.reserved,
            section1=blob.section1,
            records=[Section2Record(r.kind, r.flag, r.length, r.offset) for r in blob.records],
            section3=blob.section3,
            section4=blob.section4,
        )

    patches = sorted(patches_by_range.values(), key=lambda p: (p.old_off, p.old_len))
    for i in range(1, len(patches)):
        prev = patches[i - 1]
        cur = patches[i]
        if ranges_overlap(prev.old_off, prev.old_len, cur.old_off, cur.old_len):
            raise YstbError("edited text ranges overlap")

    sec3_size = len(blob.section3)
    valid_ranges: List[Tuple[int, int, int, int]] = []
    for idx, rec in enumerate(blob.records):
        if rec.is_valid_range(sec3_size):
            valid_ranges.append((idx, rec.offset, rec.length, rec.offset + rec.length))

    for p in patches:
        has_partial_overlap = False
        for idx, off, length, end_pos in valid_ranges:
            if not ranges_overlap(off, length, p.old_off, p.old_len):
                continue
            fully_contained = off <= p.old_off and p.old_end <= end_pos
            if not fully_contained:
                has_partial_overlap = True

        if has_partial_overlap and p.new_len != p.old_len:
            if p.new_len > p.old_len:
                fitted = truncate_encoded_prefix(p.new_bytes, p.old_len, p.encoding)
                p.new_bytes = fitted + (b" " * (p.old_len - len(fitted)))
            else:
                p.new_bytes = p.new_bytes + (b" " * (p.old_len - p.new_len))

    section3_mut = bytearray(blob.section3)
    cumulative_delta = 0
    for p in patches:
        cur_off = p.old_off + cumulative_delta
        p.new_off = cur_off
        old_slice = bytes(section3_mut[cur_off : cur_off + p.old_len])
        src_old = blob.section3[p.old_off : p.old_off + p.old_len]
        if old_slice != src_old:
            raise YstbError("internal relocation mismatch while applying edits")
        section3_mut[cur_off : cur_off + p.old_len] = p.new_bytes
        p.delta = p.new_len - p.old_len
        cumulative_delta += p.delta

    new_records: List[Section2Record] = []
    for idx, rec in enumerate(blob.records):
        new_rec = Section2Record(kind=rec.kind, flag=rec.flag, length=rec.length, offset=rec.offset)
        if rec.is_valid_range(sec3_size):
            rec_end = rec.offset + rec.length
            shift_before = 0
            delta_inside = 0
            for p in patches:
                if p.old_end <= rec.offset:
                    shift_before += p.delta
                    continue
                if rec.offset <= p.old_off and p.old_end <= rec_end:
                    delta_inside += p.delta
                    continue
                if ranges_overlap(rec.offset, rec.length, p.old_off, p.old_len):
                    if p.delta != 0:
                        raise YstbError(
                            f"record {idx} has unsupported overlap with edited range "
                            f"off=0x{p.old_off:X} len={p.old_len}"
                        )
                    continue

            new_rec.offset = rec.offset + shift_before
            new_rec.length = rec.length + delta_inside
            if new_rec.length < 0:
                raise YstbError(f"record {idx} new length became negative")
        new_records.append(new_rec)

    return YstbBinary(
        magic=blob.magic,
        version=blob.version,
        instruction_count=blob.instruction_count,
        reserved=blob.reserved,
        section1=blob.section1,
        records=new_records,
        section3=bytes(section3_mut),
        section4=blob.section4,
    )


def decompile_one(
    src: Path,
    out_dir: Path,
    source_encoding: str,
) -> Tuple[Optional[Path], Optional[Path], int]:
    data = src.read_bytes()
    blob = parse_ystb_bytes(data, src)
    text_entries = collect_text_entries(blob, source_encoding)
    if not text_entries:
        return None, None, 0

    stem = src.stem
    json_path = out_dir / f"{stem}.json"
    ins_path = out_dir / f"{stem}.ins.tsv"
    write_json_export(json_path, blob, text_entries, source_encoding)
    write_ins_tsv(ins_path, text_entries)
    return json_path, ins_path, len(text_entries)


def compile_one(
    original_ybn: Path,
    translation: Path,
    output_ybn: Path,
    source_encoding: str,
    target_encoding: str,
    filter_text_path: Optional[Path],
    encoding_errors: str,
    force_reencode_all: bool = False,
) -> None:
    blob = parse_ystb_bytes(original_ybn.read_bytes(), original_ybn)
    source_entries = collect_text_entries(blob, source_encoding)
    edits = load_text_edits(translation)
    edit_map = build_edit_map(edits, source_entries, translation)
    full_reencode = force_reencode_all or (target_encoding.lower() != source_encoding.lower())
    if full_reencode:
        for e in source_entries:
            edit_map.setdefault(e.text_id, e.text)

    rebuilt = rebuild_with_text_edits(
        blob=blob,
        source_entries=source_entries,
        edit_map=edit_map,
        source_encoding=source_encoding,
        target_encoding=target_encoding,
        encoding_errors=encoding_errors,
        force_reencode_all=full_reencode,
    )
    output_ybn.parent.mkdir(parents=True, exist_ok=True)
    output_ybn.write_bytes(rebuilt.to_bytes())


def iter_ybn_files(path: Path) -> List[Path]:
    if path.is_file():
        return [path]
    if not path.is_dir():
        raise YstbError(f"input not found: {path}")
    return sorted([p for p in path.rglob("*") if p.is_file() and p.suffix.lower() == ".ybn"], key=lambda p: str(p).lower())


def translation_stem(path: Path) -> str:
    lower = path.name.lower()
    if lower.endswith(".ins.tsv"):
        return path.name[:-8]
    return path.stem


def collect_translation_files(trans_dir: Path) -> List[Path]:
    if trans_dir.is_file():
        return [trans_dir]
    if not trans_dir.is_dir():
        raise YstbError(f"translation path not found: {trans_dir}")

    candidates: List[Path] = []
    candidates.extend(sorted(trans_dir.rglob("*.json"), key=lambda p: str(p).lower()))
    candidates.extend(sorted(trans_dir.rglob("*.ins.tsv"), key=lambda p: str(p).lower()))

    picked: Dict[Tuple[str, str], Path] = {}
    for p in candidates:
        rel = p.relative_to(trans_dir)
        key = (str(rel.parent).replace("\\", "/").lower(), translation_stem(rel).lower())
        old = picked.get(key)
        if old is None:
            picked[key] = p
            continue
        if old.suffix.lower() == ".json":
            continue
        if p.suffix.lower() == ".json":
            picked[key] = p
    return sorted(picked.values(), key=lambda p: str(p).lower())


def build_original_index(original_dir: Path) -> Tuple[Dict[Tuple[str, str], Path], Dict[str, List[Path]]]:
    by_key: Dict[Tuple[str, str], Path] = {}
    by_stem: Dict[str, List[Path]] = {}
    for p in original_dir.rglob("*"):
        if not p.is_file() or p.suffix.lower() != ".ybn":
            continue
        rel = p.relative_to(original_dir)
        key = (str(rel.parent).replace("\\", "/").lower(), p.stem.lower())
        by_key.setdefault(key, p)
        by_stem.setdefault(p.stem.lower(), []).append(p)
    return by_key, by_stem


def pick_original_for_translation(
    original_dir: Path,
    translation_dir: Path,
    translation_file: Path,
    by_key: Dict[Tuple[str, str], Path],
    by_stem: Dict[str, List[Path]],
) -> Tuple[Path, str, str, Path]:
    rel = translation_file.relative_to(translation_dir)
    stem = translation_stem(rel)
    direct = original_dir / rel.parent / f"{stem}.ybn"
    if direct.exists():
        return direct, "direct", stem, rel.parent

    key = (str(rel.parent).replace("\\", "/").lower(), stem.lower())
    hit = by_key.get(key)
    if hit is not None:
        return hit, "parent+stem", stem, rel.parent

    same_stem = by_stem.get(stem.lower(), [])
    if len(same_stem) == 1:
        return same_stem[0], "stem_unique", stem, rel.parent
    if len(same_stem) > 1:
        raise YstbError(f"multiple source ybn with same stem: {stem}")
    raise YstbError(f"no matching source ybn for translation stem: {stem}")


def decompile_cmd(args: argparse.Namespace) -> None:
    src = Path(args.input)
    out_dir = Path(args.out_dir)
    error_log = Path(args.error_log) if args.error_log else None

    if src.is_file():
        try:
            json_path, ins_path, entry_count = decompile_one(src, out_dir, args.source_encoding)
            if json_path is None or ins_path is None:
                print(f"[SKIP] {src}: no text entries")
            else:
                print(f"[OK] {src} -> {json_path}, {ins_path} entries={entry_count}")
        except Exception as exc:  # noqa: BLE001
            if error_log is not None:
                append_error_log(error_log, src, exc)
            raise
        return

    files = iter_ybn_files(src)
    if not files:
        raise YstbError(f"no .ybn files found in {src}")

    ok = 0
    skip = 0
    fail = 0
    for f in files:
        try:
            rel = f.relative_to(src)
            file_out_dir = out_dir / rel.parent
            json_path, ins_path, entry_count = decompile_one(f, file_out_dir, args.source_encoding)
            if json_path is None or ins_path is None:
                print(f"[SKIP] {f}: no text entries")
                skip += 1
                continue
            print(f"[OK] {f} -> {json_path.name}, {ins_path.name} entries={entry_count}")
            ok += 1
        except YstbError as exc:
            msg = str(exc)
            if "unsupported magic" in msg:
                print(f"[SKIP] {f}: {msg}")
                skip += 1
                continue
            if error_log is not None:
                append_error_log(error_log, f, exc)
            print(f"[FAIL] {f}: {exc}")
            fail += 1
        except Exception as exc:  # noqa: BLE001
            if error_log is not None:
                append_error_log(error_log, f, exc)
            print(f"[FAIL] {f}: {exc}")
            fail += 1
    print(f"[OK] decompile done ok={ok} skip={skip} fail={fail} out={out_dir}")


def compile_cmd(args: argparse.Namespace) -> None:
    original = Path(args.original)
    translation = Path(args.translation)
    output = Path(args.output)
    error_log = Path(args.error_log) if args.error_log else None
    filter_path = Path(args.filter_text) if args.filter_text else None

    if original.is_file() and translation.is_file():
        out_file = output
        if output.suffix.lower() != ".ybn":
            out_file = output / original.name
        try:
            compile_one(
                original_ybn=original,
                translation=translation,
                output_ybn=out_file,
                source_encoding=args.source_encoding,
                target_encoding=args.text_encoding,
                filter_text_path=filter_path,
                encoding_errors=args.encoding_errors,
                force_reencode_all=args.force_reencode_all,
            )
            print(f"[OK] {translation} -> {out_file}")
        except Exception as exc:  # noqa: BLE001
            if error_log is not None:
                append_error_log(error_log, translation, exc)
            raise
        return

    if not (original.is_dir() and translation.is_dir()):
        raise YstbError("compile requires file+file or dir+dir")

    output.mkdir(parents=True, exist_ok=True)
    trans_files = collect_translation_files(translation)
    if not trans_files:
        raise YstbError(f"no translation files found in {translation}")

    by_key, by_stem = build_original_index(original)
    ok = 0
    skip = 0
    fail = 0
    for trans_file in trans_files:
        try:
            src_ybn, mode, stem, rel_parent = pick_original_for_translation(
                original_dir=original,
                translation_dir=translation,
                translation_file=trans_file,
                by_key=by_key,
                by_stem=by_stem,
            )
            out_ybn = output / rel_parent / f"{stem}.ybn"
            compile_one(
                original_ybn=src_ybn,
                translation=trans_file,
                output_ybn=out_ybn,
                source_encoding=args.source_encoding,
                target_encoding=args.text_encoding,
                filter_text_path=filter_path,
                encoding_errors=args.encoding_errors,
                force_reencode_all=args.force_reencode_all,
            )
            print(f"[OK] {trans_file} -> {out_ybn} match={mode}")
            ok += 1
        except YstbError as exc:
            if error_log is not None:
                append_error_log(error_log, trans_file, exc)
            print(f"[SKIP] {trans_file}: {exc}")
            skip += 1
        except Exception as exc:  # noqa: BLE001
            if error_log is not None:
                append_error_log(error_log, trans_file, exc)
            print(f"[FAIL] {trans_file}: {exc}")
            fail += 1
    print(f"[OK] compile done ok={ok} skip={skip} fail={fail} out={output}")


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description="YSTB Tool")
    sub = p.add_subparsers(dest="cmd", required=True)

    pd = sub.add_parser("decompile", help="decompile .ybn into JSON + INS TSV")
    pd.add_argument("input", help="input .ybn file or directory")
    pd.add_argument("out_dir", help="output directory")
    pd.add_argument("--source-encoding", default="cp932", help="source text encoding (default: cp932)")
    pd.add_argument("--error-log", default="", help="optional error log path")
    pd.set_defaults(func=decompile_cmd)

    pc = sub.add_parser("compile", help="rebuild .ybn from JSON or INS TSV")
    pc.add_argument("original", help="source .ybn file or directory")
    pc.add_argument("translation", help="edited .json/.ins.tsv file or directory")
    pc.add_argument("output", help="output .ybn file or directory")
    pc.add_argument("--source-encoding", default="cp932", help="source text encoding (default: cp932)")
    pc.add_argument(
        "--text-encoding",
        default="cp932",
        help="target writeback encoding, e.g. gbk (default: cp932)",
    )
    pc.add_argument(
        "--filter-text",
        default="",
        help="deprecated: ignored (partial source-encoding writeback disabled)",
    )
    pc.add_argument(
        "--encoding-errors",
        default="strict",
        choices=["strict", "replace", "ignore"],
        help="encoding error behavior (default: strict)",
    )
    pc.add_argument(
        "--force-reencode-all",
        action="store_true",
        help="re-encode all editable text entries with target encoding, not only changed lines",
    )
    pc.add_argument("--error-log", default="", help="optional error log path")
    pc.set_defaults(func=compile_cmd)

    return p


def main() -> None:
    parser = build_parser()
    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()








