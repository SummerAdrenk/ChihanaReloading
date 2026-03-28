#!/usr/bin/env python3
from __future__ import annotations

import argparse
import csv
import json
import re
import struct
import sys
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Dict, List, Optional, Sequence, Set, Tuple


HEADER_SIZE = 0x20
RECORD_SIZE = 12
MAGIC_YSTB = b"YSTB"
OPCODE_CONDITIONAL_BRANCH = 0x2C
OPCODE_SET_VALUE = 0x2B
OPCODE_SET_RUNTIME_VALUE = 0x36
OPCODE_MESSAGE_TEXT = 0x6C
OPCODE_MESSAGE_FORMAT = 0x70
OPCODE_MESSAGE_COMMIT = 0x51
OPCODE_MENU_TEXT = 0x35
MESSAGE_NEWLINE = "\n"
NON_SECTION3_OPERANDS = {
    OPCODE_CONDITIONAL_BRANCH: {1, 2},
}
BRANCH_TARGET_LENGTH_OPERANDS = {
    OPCODE_CONDITIONAL_BRANCH: {1, 2},
}
SET_VALUE_NAME_SLOTS = {
    "es.CHAR.NAME": (33,),
    "es.CHAR.NAME.MARK.SET": (33,),
}
SET_VALUE_SYSTEM_SLOTS = {
    "es._mes": (34, 35),
    "es.DIALOG.YESNO.SET": (33, 34),
    "es.DIALOG.SET": (33, 34),
}
# IDA??????case 0x2B ????? key=="ES.SEL.SET" ???????? slot 33..42?
SET_VALUE_SELECTION_SLOTS = {
    "ES.SEL.SET": (33, 34, 35, 36, 37, 38, 39, 40, 41, 42),
}
SET_VALUE_TEXT_SLOT_MIN = 33
SET_VALUE_TEXT_SLOT_MAX = 42
SET_VALUE_GENERIC_TEXT_SLOTS = tuple(range(SET_VALUE_TEXT_SLOT_MIN, SET_VALUE_TEXT_SLOT_MAX + 1))
SET_VALUE_NUMERIC_RE = re.compile(r"^[+-]?(?:\d+|\d+\.\d+)$")
SET_VALUE_ASCII_TOKEN_RE = re.compile(r"^[A-Za-z0-9_.,:+\\\-*=/%@#()<>\\[\\]{}|~`]+$")
MENU_TEXT_OPCODE35_ARG0_SIGS = {
    bytes.fromhex("48030024d816"),
    bytes.fromhex("48030024d916"),
}
EXTRACT_MODE_OPLIST_MSG = "oplist_msg_v555"
EXTRACT_MODE_OPLIST_MENU = "oplist_menu_v555"
EXTRACT_MODE_SET_VALUE = "set_value_v555"
EXTRACT_MODE_SET_VALUE_LEGACY = "set_value_v550"
SET_VALUE_EXTRACT_MODE_PREFIXES = (
    EXTRACT_MODE_SET_VALUE + ":",
    EXTRACT_MODE_SET_VALUE_LEGACY + ":",
)
JSON_ID_LINE_RE = re.compile(r'^\s*"id"\s*:\s*(-?\d+)\s*,?\s*$')


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
    text_role: str = "msg"
    extract_mode: str = "oplist"
    instr_start: Optional[int] = None
    instr_end: Optional[int] = None
    arg_slot: Optional[int] = None
    segment_ids: Optional[List[int]] = None


@dataclass
class TextEdit:
    text_id: Optional[int]
    record_index: Optional[int]
    text: str
    source_path: str = ""
    source_loc: str = ""
    source_line: Optional[int] = None


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


@dataclass
class OperandRecord:
    slot: int
    aux: int
    kind: int
    flags: int
    length: int
    offset: int
    payload: bytes
    uses_section3_payload: bool

    def to_section2_record(self) -> Section2Record:
        return Section2Record(
            kind=self.slot | (self.aux << 8),
            flag=self.kind | (self.flags << 8),
            length=self.length,
            offset=self.offset,
        )


@dataclass
class InstructionRecord:
    index: int
    opcode: int
    arg_count: int
    extra: int
    tail: int
    trace_value: int
    args: List[OperandRecord]

    @property
    def raw_word(self) -> int:
        return (
            (self.tail & 0xFF) << 24
            | (self.extra & 0xFF) << 16
            | (self.arg_count & 0xFF) << 8
            | (self.opcode & 0xFF)
        )


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


def default_compile_error_log_path(output: Path) -> Path:
    _ = output
    return Path(__file__).resolve().parent / "_compile_error.log"


def compact_error_for_console(err: Exception) -> str:
    msg = str(err).replace("\r", "\\r").replace("\n", "\\n")
    two_line_match = re.search(r"two_line_index=([0-9A-Fa-f]{8})", msg)
    if "; " in msg:
        msg = msg.split("; ", 1)[0]
    if two_line_match is not None and "two_line_index=" not in msg:
        msg = f"{msg}; two_line_index={two_line_match.group(1)}"
    if len(msg) > 220:
        msg = msg[:217] + "..."
    enc = getattr(sys.stdout, "encoding", None) or "utf-8"
    try:
        return msg.encode(enc, errors="backslashreplace").decode(enc, errors="strict")
    except Exception:  # noqa: BLE001
        return msg.encode("ascii", errors="backslashreplace").decode("ascii", errors="strict")


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


def clone_operand(arg: OperandRecord) -> OperandRecord:
    return OperandRecord(
        slot=arg.slot,
        aux=arg.aux,
        kind=arg.kind,
        flags=arg.flags,
        length=arg.length,
        offset=arg.offset,
        payload=bytes(arg.payload),
        uses_section3_payload=arg.uses_section3_payload,
    )


def clone_instruction(inst: InstructionRecord) -> InstructionRecord:
    return InstructionRecord(
        index=inst.index,
        opcode=inst.opcode,
        arg_count=inst.arg_count,
        extra=inst.extra,
        tail=inst.tail,
        trace_value=inst.trace_value,
        args=[clone_operand(arg) for arg in inst.args],
    )


def parse_instruction_records(blob: YstbBinary, src: Optional[Path] = None) -> List[InstructionRecord]:
    where = str(src) if src is not None else "<memory>"
    if len(blob.section1) != blob.instruction_count * 4:
        raise YstbError(
            f"{where}: section1 size mismatch, expected {blob.instruction_count * 4} got {len(blob.section1)}"
        )
    if len(blob.section4) != blob.instruction_count * 4:
        raise YstbError(
            f"{where}: section4 size mismatch, expected {blob.instruction_count * 4} got {len(blob.section4)}"
        )

    sec2 = pack_records(blob.records)
    out: List[InstructionRecord] = []
    pos = 0
    for idx in range(blob.instruction_count):
        word = struct.unpack_from("<I", blob.section1, idx * 4)[0]
        opcode = word & 0xFF
        arg_count = (word >> 8) & 0xFF
        extra = (word >> 16) & 0xFF
        tail = (word >> 24) & 0xFF
        trace_value = struct.unpack_from("<I", blob.section4, idx * 4)[0]
        args: List[OperandRecord] = []
        for arg_index in range(arg_count):
            if pos + RECORD_SIZE > len(sec2):
                raise YstbError(f"{where}: section2 ended unexpectedly while parsing instruction {idx}")
            slot, aux, kind, flags, length, offset = struct.unpack_from("<4BII", sec2, pos)
            uses_section3_payload = arg_index not in NON_SECTION3_OPERANDS.get(opcode, set())
            if uses_section3_payload and kind == 0:
                # Some opcode/kind=0 operands are immediates carried in length/offset fields.
                # Keep section3 payload only when the range is valid.
                if offset > len(blob.section3) or offset + length > len(blob.section3):
                    uses_section3_payload = False
            payload = b""
            if uses_section3_payload:
                if offset > len(blob.section3) or offset + length > len(blob.section3):
                    raise YstbError(
                        f"{where}: instruction {idx} operand range out of bounds off=0x{offset:X} len={length}"
                    )
                payload = blob.section3[offset : offset + length]
            args.append(
                OperandRecord(
                    slot=slot,
                    aux=aux,
                    kind=kind,
                    flags=flags,
                    length=length,
                    offset=offset,
                    payload=payload,
                    uses_section3_payload=uses_section3_payload,
                )
            )
            pos += RECORD_SIZE
        out.append(
            InstructionRecord(
                index=idx,
                opcode=opcode,
                arg_count=arg_count,
                extra=extra,
                tail=tail,
                trace_value=trace_value,
                args=args,
            )
        )

    if pos != len(sec2):
        raise YstbError(f"{where}: section2 trailing bytes remain after instruction parse")
    return out


def rebuild_blob_from_instructions(blob: YstbBinary, instructions: Sequence[InstructionRecord]) -> YstbBinary:
    section1 = bytearray()
    records: List[Section2Record] = []
    section3 = bytearray()
    section4 = bytearray()

    for idx, inst in enumerate(instructions):
        inst.index = idx
        inst.arg_count = len(inst.args)
        section1.extend(struct.pack("<I", inst.raw_word))
        section4.extend(struct.pack("<I", inst.trace_value))
        for arg in inst.args:
            if arg.uses_section3_payload:
                arg.offset = len(section3)
                arg.length = len(arg.payload)
                section3.extend(arg.payload)
            records.append(arg.to_section2_record())

    return YstbBinary(
        magic=blob.magic,
        version=blob.version,
        instruction_count=len(instructions),
        reserved=blob.reserved,
        section1=bytes(section1),
        records=records,
        section3=bytes(section3),
        section4=bytes(section4),
    )


def decode_literal_value(payload: bytes) -> Optional[int]:
    if len(payload) < 4:
        return None
    tag = payload[:1]
    size = struct.unpack_from("<H", payload, 1)[0]
    body = payload[3:]
    if size != len(body):
        return None
    if tag == b"B" and len(body) == 1:
        return body[0]
    if tag == b"W" and len(body) == 2:
        return struct.unpack_from("<H", body, 0)[0]
    if tag == b"I" and len(body) == 4:
        return struct.unpack_from("<I", body, 0)[0]
    return None


def encode_literal_value(value: int) -> bytes:
    if 0 <= value <= 0xFF:
        return b"B" + struct.pack("<H", 1) + bytes([value])
    if 0 <= value <= 0xFFFF:
        return b"W" + struct.pack("<H", 2) + struct.pack("<H", value)
    if 0 <= value <= 0xFFFFFFFF:
        return b"I" + struct.pack("<H", 4) + struct.pack("<I", value)
    raise YstbError(f"literal value out of range: {value}")


def decode_marshaled_string(payload: bytes, source_encoding: str) -> Optional[str]:
    if len(payload) < 5 or payload[:1] != b"M":
        return None
    size = struct.unpack_from("<H", payload, 1)[0]
    body = payload[3:]
    if size != len(body):
        return None
    if len(body) < 2 or body[:1] != b'"' or body[-1:] != b'"':
        return None
    try:
        return body[1:-1].decode(source_encoding)
    except UnicodeDecodeError:
        return None


def encode_marshaled_string(text: str, target_encoding: str, encoding_errors: str) -> bytes:
    body = text.encode(target_encoding, errors=encoding_errors)
    quoted = b'"' + body + b'"'
    if len(quoted) > 0xFFFF:
        raise YstbError(f"marshaled string too long: {len(quoted)}")
    return b"M" + struct.pack("<H", len(quoted)) + quoted


def decode_message_text_instruction(inst: InstructionRecord, source_encoding: str) -> Optional[str]:
    if inst.opcode != OPCODE_MESSAGE_TEXT:
        return None
    if len(inst.args) != 1:
        return None
    arg = inst.args[0]
    if arg.kind != 0 or not arg.uses_section3_payload:
        return None
    try:
        return arg.payload.decode(source_encoding)
    except UnicodeDecodeError:
        return None


def split_message_text_parts(text: str) -> List[str]:
    # v550 export uses literal newline separators between append chunks.
    return text.split(MESSAGE_NEWLINE)

def make_message_text_instruction(template: InstructionRecord, text_bytes: bytes, trace_value: int) -> InstructionRecord:
    args = [
        OperandRecord(
            slot=0,
            aux=0,
            kind=0,
            flags=0,
            length=len(text_bytes),
            offset=0,
            payload=text_bytes,
            uses_section3_payload=True,
        )
    ]
    return InstructionRecord(
        index=template.index,
        opcode=OPCODE_MESSAGE_TEXT,
        arg_count=1,
        extra=template.extra if template.opcode == OPCODE_MESSAGE_TEXT else 0,
        tail=template.tail if template.opcode == OPCODE_MESSAGE_TEXT else 0,
        trace_value=trace_value,
        args=args,
    )


def make_message_format_instruction(template: InstructionRecord, trace_value: int) -> InstructionRecord:
    return InstructionRecord(
        index=template.index,
        opcode=OPCODE_MESSAGE_FORMAT,
        arg_count=len(template.args),
        extra=template.extra,
        tail=template.tail,
        trace_value=trace_value,
        args=[clone_operand(arg) for arg in template.args],
    )


def remap_instruction_target(
    old_target: int,
    target_map: Dict[int, Optional[int]],
    owner_index: int,
    arg_index: int,
) -> int:
    if old_target not in target_map:
        raise YstbError(
            f"instruction {owner_index}: opcode 0x{OPCODE_CONDITIONAL_BRANCH:02X} target {old_target} not found in remap table"
        )
    new_target = target_map[old_target]
    if new_target is None:
        raise YstbError(
            f"instruction {owner_index}: opcode 0x{OPCODE_CONDITIONAL_BRANCH:02X} operand {arg_index} targets the middle of a rebuilt message block"
        )
    return new_target


def remap_instruction_targets(
    instructions: Sequence[InstructionRecord],
    target_map: Dict[int, Optional[int]],
) -> None:
    for inst in instructions:
        target_args = BRANCH_TARGET_LENGTH_OPERANDS.get(inst.opcode)
        if not target_args:
            continue
        for arg_index in target_args:
            if arg_index >= len(inst.args):
                raise YstbError(
                    f"instruction {inst.index}: opcode 0x{inst.opcode:02X} missing operand {arg_index}"
                )
            arg = inst.args[arg_index]
            arg.length = remap_instruction_target(arg.length, target_map, inst.index, arg_index)


def collect_oplist_text_entries(blob: YstbBinary, source_encoding: str) -> List[TextEntry]:
    try:
        instructions = parse_instruction_records(blob)
    except YstbError:
        return []

    out: List[TextEntry] = []
    pending_parts: List[str] = []
    pending_start: Optional[int] = None
    pending_offset = 0
    pending_length = 0
    pending_record_index = 0
    pending_segment_ids: List[int] = []
    text_id = 0

    def flush(end_idx: int) -> None:
        nonlocal pending_parts, pending_start, pending_offset, pending_length, pending_record_index, pending_segment_ids, text_id
        if pending_start is None:
            pending_parts = []
            pending_segment_ids = []
            return
        if pending_parts:
            out.append(
                TextEntry(
                    text_id=text_id,
                    record_index=pending_record_index,
                    kind=OPCODE_MESSAGE_TEXT,
                    flag=OPCODE_MESSAGE_COMMIT,
                    offset=pending_offset,
                    length=pending_length,
                    text=MESSAGE_NEWLINE.join(pending_parts),
                    text_role="msg",
                    extract_mode=EXTRACT_MODE_OPLIST_MSG,
                    segment_ids=list(pending_segment_ids),
                    instr_start=pending_start,
                    instr_end=end_idx,
                )
            )
            text_id += 1
        pending_parts = []
        pending_start = None
        pending_offset = 0
        pending_length = 0
        pending_record_index = 0
        pending_segment_ids = []

    for inst in instructions:
        text = decode_message_text_instruction(inst, source_encoding)
        if text is not None:
            if pending_start is None:
                pending_start = inst.index
                if inst.args:
                    pending_offset = inst.args[0].offset
                    pending_length = inst.args[0].length
                    pending_record_index = inst.index
            pending_parts.append(text)
            pending_segment_ids.append(text_id + len(pending_parts) - 1)
            continue

        if inst.opcode == OPCODE_MESSAGE_FORMAT:
            # Keep in block but do not expose as editable plain text.
            continue

        if inst.opcode == OPCODE_MESSAGE_COMMIT:
            flush(inst.index + 1)
            continue

        if pending_start is not None:
            flush(inst.index)

    if pending_start is not None:
        flush(len(instructions))

    return out


def contains_cjk_char(text: str) -> bool:
    for ch in text:
        code = ord(ch)
        if 0x3040 <= code <= 0x30FF:
            return True
        if 0x3400 <= code <= 0x9FFF:
            return True
        if 0xF900 <= code <= 0xFAFF:
            return True
    return False


def is_probable_visible_set_value_text(text: str) -> bool:
    t = text.strip()
    if not t:
        return False

    if SET_VALUE_NUMERIC_RE.fullmatch(t) is not None:
        return False

    if "/" in t or "\\" in t:
        return False

    if len(t) == 1 and t.isascii() and t.isalnum():
        return False

    if SET_VALUE_ASCII_TOKEN_RE.fullmatch(t) is not None:
        return False

    if any(ch in t for ch in ("。", "、", "！", "？", "「", "」", "『", "』", "（", "）", "【", "】", "…")):
        return True

    if contains_cjk_char(t):
        return True

    if any(ch.isspace() for ch in t) and any(ch.isalpha() for ch in t):
        return True

    return False


def is_probable_menu_text(text: str) -> bool:
    t = text.strip()
    if not t:
        return False

    if SET_VALUE_NUMERIC_RE.fullmatch(t) is not None:
        return False

    if SET_VALUE_ASCII_TOKEN_RE.fullmatch(t) is not None:
        return False

    cjk_count = sum(1 for ch in t if contains_cjk_char(ch))
    if cjk_count == 0:
        return False

    if any(ch in t for ch in ("。", "、", "！", "？", "「", "」", "『", "』", "（", "）", "【", "】", "…", "：")):
        return True

    if cjk_count >= 4:
        return True

    return False


def collect_set_value_text_entries(blob: YstbBinary, source_encoding: str) -> List[TextEntry]:
    try:
        instructions = parse_instruction_records(blob)
    except YstbError:
        return []

    out: List[TextEntry] = []
    text_id = 0
    for inst in instructions:
        if inst.opcode != OPCODE_SET_VALUE:
            continue

        args_by_slot = {arg.slot: arg for arg in inst.args}
        key_arg = args_by_slot.get(0)
        if key_arg is None or key_arg.kind != 3:
            continue

        key = decode_marshaled_string(key_arg.payload, source_encoding)
        if key is None:
            continue

        role = None
        text_slots: Sequence[int] = ()
        collect_all_non_empty = False
        if key in SET_VALUE_NAME_SLOTS:
            role = "name"
            text_slots = SET_VALUE_NAME_SLOTS[key]
        elif key in SET_VALUE_SYSTEM_SLOTS:
            role = "system"
            text_slots = SET_VALUE_SYSTEM_SLOTS[key]
        elif key in SET_VALUE_SELECTION_SLOTS:
            role = "sel"
            text_slots = SET_VALUE_SELECTION_SLOTS[key]
            collect_all_non_empty = True
        if role is None:
            continue

        if collect_all_non_empty:
            for slot in text_slots:
                arg = args_by_slot.get(slot)
                if arg is None or arg.kind != 3 or not arg.uses_section3_payload:
                    continue
                text = decode_marshaled_string(arg.payload, source_encoding)
                if text is None or text == "":
                    continue
                out.append(
                    TextEntry(
                        text_id=text_id,
                        record_index=inst.index,
                        kind=inst.opcode,
                        flag=slot,
                        offset=arg.offset,
                        length=arg.length,
                        text=text,
                        text_role=role,
                        extract_mode=f"{EXTRACT_MODE_SET_VALUE}:{key}",
                        instr_start=inst.index,
                        instr_end=inst.index + 1,
                        arg_slot=slot,
                    )
                )
                text_id += 1
            continue

        chosen_arg: Optional[OperandRecord] = None
        chosen_slot: Optional[int] = None
        chosen_text = ""
        for slot in text_slots:
            arg = args_by_slot.get(slot)
            if arg is None or arg.kind != 3 or not arg.uses_section3_payload:
                continue
            text = decode_marshaled_string(arg.payload, source_encoding)
            if text is None:
                continue
            if chosen_arg is None:
                chosen_arg = arg
                chosen_slot = slot
                chosen_text = text
            if text:
                chosen_arg = arg
                chosen_slot = slot
                chosen_text = text
                break
        if chosen_arg is None or chosen_slot is None:
            continue

        out.append(
            TextEntry(
                text_id=text_id,
                record_index=inst.index,
                kind=inst.opcode,
                flag=chosen_slot,
                offset=chosen_arg.offset,
                length=chosen_arg.length,
                text=chosen_text,
                text_role=role,
                extract_mode=f"{EXTRACT_MODE_SET_VALUE}:{key}",
                instr_start=inst.index,
                instr_end=inst.index + 1,
                arg_slot=chosen_slot,
            )
        )
        text_id += 1

    return out


def collect_menu_text_entries(blob: YstbBinary, source_encoding: str) -> List[TextEntry]:
    try:
        instructions = parse_instruction_records(blob)
    except YstbError:
        return []

    out: List[TextEntry] = []
    text_id = 0
    for inst in instructions:
        if inst.opcode != OPCODE_MENU_TEXT:
            continue
        # IDA reverse alignment: opcode 0x35 is generic and appears in many non-text
        # contexts. Restrict extraction to known menu-help control words (arg0 sig),
        # then only expose marshaled sentence text from arg1.
        if len(inst.args) < 2:
            continue
        arg0 = inst.args[0]
        arg1 = inst.args[1]
        if arg0.kind != 3 or not arg0.uses_section3_payload:
            continue
        if arg0.payload not in MENU_TEXT_OPCODE35_ARG0_SIGS:
            continue
        if arg1.kind != 3 or not arg1.uses_section3_payload:
            continue

        text = decode_marshaled_string(arg1.payload, source_encoding)
        if text is None or not is_probable_menu_text(text):
            continue
        out.append(
            TextEntry(
                text_id=text_id,
                record_index=inst.index,
                kind=inst.opcode,
                flag=arg1.slot,
                offset=arg1.offset,
                length=arg1.length,
                text=text,
                text_role="menu",
                extract_mode=EXTRACT_MODE_OPLIST_MENU,
                instr_start=inst.index,
                instr_end=inst.index + 1,
                arg_slot=arg1.slot,
            )
        )
        text_id += 1
    return out


def collect_opcode36_marshaled_text_entries(blob: YstbBinary, source_encoding: str) -> List[TextEntry]:
    # Disabled in v550 strict mode: extraction is driven by reversed oplist profiles only.
    return []


def _split_text_for_count(text: str, count: int) -> List[str]:
    if count <= 1:
        return [text]
    parts = text.split(MESSAGE_NEWLINE)
    if len(parts) == count:
        return parts
    if len(parts) > count:
        head = parts[: count - 1]
        tail = MESSAGE_NEWLINE.join(parts[count - 1 :])
        return head + [tail]
    return parts + ([""] * (count - len(parts)))


def build_message_instruction_sequence(
    entry: TextEntry,
    original_slice: Sequence[InstructionRecord],
    new_text: str,
    target_encoding: str,
    encoding_errors: str,
    source_by_id: Dict[int, TextEntry],
    edit_origins: Optional[Dict[int, TextEdit]],
    sec3_base: int,
    fallback_format_template: Optional[InstructionRecord] = None,
) -> List[InstructionRecord]:
    text_templates: List[InstructionRecord] = [inst for inst in original_slice if inst.opcode == OPCODE_MESSAGE_TEXT]
    format_templates: List[InstructionRecord] = [inst for inst in original_slice if inst.opcode == OPCODE_MESSAGE_FORMAT]
    has_inline_format = len(format_templates) > 0
    if not text_templates:
        raise YstbError(f"text id {entry.text_id}: missing source text instruction template")
    if not original_slice or original_slice[-1].opcode != OPCODE_MESSAGE_COMMIT:
        raise YstbError(f"text id {entry.text_id}: missing source message commit instruction")

    # Keep inline-format blocks shape-stable until 0x70 placeholder remapping is fully reversed.
    if has_inline_format:
        out = [clone_instruction(inst) for inst in original_slice]
        target_texts = _split_text_for_count(new_text, len(text_templates))
        ti = 0
        for inst in out:
            if inst.opcode != OPCODE_MESSAGE_TEXT:
                continue
            try:
                payload = target_texts[ti].encode(target_encoding, errors=encoding_errors)
            except UnicodeEncodeError as exc:
                raise YstbError(
                    build_encode_error_message(
                        text_id=entry.text_id,
                        new_text=new_text,
                        target_encoding=target_encoding,
                        exc=exc,
                        source_by_id=source_by_id,
                        edit_origins=edit_origins,
                        sec3_base=sec3_base,
                    )
                ) from exc
            inst.args[0].payload = payload
            ti += 1
        return out

    template = text_templates[0]
    trace_value = template.trace_value
    commit_template = clone_instruction(original_slice[-1])
    format_template = format_templates[0] if format_templates else fallback_format_template
    out: List[InstructionRecord] = []
    parts = split_message_text_parts(new_text)
    for pi, part in enumerate(parts):
        try:
            text_bytes = part.encode(target_encoding, errors=encoding_errors)
        except UnicodeEncodeError as exc:
            raise YstbError(
                build_encode_error_message(
                    text_id=entry.text_id,
                    new_text=new_text,
                    target_encoding=target_encoding,
                    exc=exc,
                    source_by_id=source_by_id,
                    edit_origins=edit_origins,
                    sec3_base=sec3_base,
                )
            ) from exc
        out.append(make_message_text_instruction(template, text_bytes, trace_value))
        if pi != len(parts) - 1 and format_template is not None:
            out.append(make_message_format_instruction(format_template, trace_value))
    if not out:
        out.append(make_message_text_instruction(template, b"", trace_value))
    out.append(commit_template)
    return out


def rebuild_with_oplist_text_edits(
    blob: YstbBinary,
    source_entries: Sequence[TextEntry],
    edit_map: Dict[int, str],
    source_encoding: str,
    target_encoding: str,
    encoding_errors: str,
    source_by_id: Dict[int, TextEntry],
    edit_origins: Optional[Dict[int, TextEdit]] = None,
    force_reencode_all: bool = False,
) -> YstbBinary:
    sec3_base = HEADER_SIZE + len(blob.section1) + len(blob.records) * RECORD_SIZE
    instructions = parse_instruction_records(blob)
    fallback_format_template: Optional[InstructionRecord] = None
    for inst in instructions:
        if inst.opcode == OPCODE_MESSAGE_FORMAT:
            fallback_format_template = inst
            break

    entries_by_start: Dict[int, List[TextEntry]] = {}
    for entry in source_entries:
        if entry.instr_start is None or entry.instr_end is None:
            continue
        entries_by_start.setdefault(entry.instr_start, []).append(entry)
    for start in entries_by_start:
        entries_by_start[start].sort(key=lambda e: e.text_id)

    new_instructions: List[InstructionRecord] = []
    target_map: Dict[int, Optional[int]] = {}
    idx = 0
    while idx < len(instructions):
        entry_group = entries_by_start.get(idx)
        if not entry_group:
            target_map[instructions[idx].index] = len(new_instructions)
            new_instructions.append(clone_instruction(instructions[idx]))
            idx += 1
            continue

        span_end = entry_group[0].instr_end
        if span_end is None or span_end <= idx or span_end > len(instructions):
            raise YstbError(f"text id {entry_group[0].text_id}: invalid instruction span")
        for e in entry_group:
            if e.instr_end != span_end:
                raise YstbError(
                    f"text id {e.text_id}: mismatched instruction span at instr_start={idx}"
                )

        original_slice = instructions[idx:span_end]
        group_is_marshaled = all(should_encode_as_marshaled(e.extract_mode) for e in entry_group)

        if group_is_marshaled:
            if len(original_slice) != 1:
                raise YstbError(
                    f"text id {entry_group[0].text_id}: invalid marshaled instruction span"
                )

            inst = clone_instruction(original_slice[0])
            old_text_by_slot: Dict[int, str] = {}
            for arg in original_slice[0].args:
                if arg.kind != 3:
                    continue
                old_text = decode_marshaled_string(arg.payload, source_encoding)
                if old_text is None:
                    continue
                old_text_by_slot[arg.slot] = old_text

            payload_by_slot: Dict[int, bytes] = {}
            payload_by_arg_index: Dict[int, bytes] = {}
            changed_any = False
            for entry in entry_group:
                new_text = edit_map.get(entry.text_id, entry.text)
                if (not force_reencode_all) and new_text == entry.text:
                    continue
                changed_any = True
                if entry.arg_slot is None:
                    raise YstbError(f"text id {entry.text_id}: missing marshaled slot metadata")

                try:
                    new_payload = encode_marshaled_string(new_text, target_encoding, encoding_errors)
                except UnicodeEncodeError as exc:
                    raise YstbError(
                        build_encode_error_message(
                            text_id=entry.text_id,
                            new_text=new_text,
                            target_encoding=target_encoding,
                            exc=exc,
                            source_by_id=source_by_id,
                            edit_origins=edit_origins,
                            sec3_base=sec3_base,
                        )
                    ) from exc

                if is_set_value_extract_mode(entry.extract_mode):
                    slots_to_update: Set[int] = {entry.arg_slot}
                    # set_value keys may duplicate the same text in multiple slots;
                    # keep duplicate slots synchronized by source text value.
                    for slot, old_text in old_text_by_slot.items():
                        if slot == entry.arg_slot:
                            continue
                        if old_text == entry.text:
                            slots_to_update.add(slot)

                    for slot in slots_to_update:
                        prev = payload_by_slot.get(slot)
                        if prev is not None and prev != new_payload:
                            raise YstbError(
                                f"text id {entry.text_id}: conflicting marshaled writes on slot {slot}"
                            )
                        payload_by_slot[slot] = new_payload
                else:
                    matches = [
                        i
                        for i, arg in enumerate(inst.args)
                        if arg.kind == 3
                        and arg.uses_section3_payload
                        and arg.offset == entry.offset
                        and arg.length == entry.length
                    ]
                    if len(matches) != 1:
                        raise YstbError(
                            f"text id {entry.text_id}: cannot resolve target marshaled operand by range "
                            f"(off=0x{entry.offset:X}, len={entry.length})"
                        )
                    arg_i = matches[0]
                    prev = payload_by_arg_index.get(arg_i)
                    if prev is not None and prev != new_payload:
                        raise YstbError(
                            f"text id {entry.text_id}: conflicting marshaled writes on operand {arg_i}"
                        )
                    payload_by_arg_index[arg_i] = new_payload

            if changed_any:
                touched_slot = False
                for arg_i, arg in enumerate(inst.args):
                    if arg.kind != 3:
                        continue
                    payload = payload_by_arg_index.get(arg_i)
                    if payload is None:
                        payload = payload_by_slot.get(arg.slot)
                    if payload is None:
                        continue
                    arg.payload = payload
                    touched_slot = True
                if (payload_by_slot or payload_by_arg_index) and not touched_slot:
                    raise YstbError(
                        f"text id {entry_group[0].text_id}: target marshaled slot not found"
                    )

            target_map[original_slice[0].index] = len(new_instructions)
            new_instructions.append(inst)
            idx = span_end
            continue

        if len(entry_group) != 1:
            raise YstbError(f"instruction {idx}: multiple text entries share non-set-value span")

        entry = entry_group[0]
        new_text = edit_map.get(entry.text_id, entry.text)
        if (not force_reencode_all) and new_text == entry.text:
            for inst in original_slice:
                target_map[inst.index] = len(new_instructions)
                new_instructions.append(clone_instruction(inst))
        else:
            rebuilt_slice = build_message_instruction_sequence(
                entry=entry,
                original_slice=original_slice,
                new_text=new_text,
                target_encoding=target_encoding,
                encoding_errors=encoding_errors,
                source_by_id=source_by_id,
                edit_origins=edit_origins,
                sec3_base=sec3_base,
                fallback_format_template=fallback_format_template,
            )
            rebuilt_start = len(new_instructions)
            rebuilt_end = rebuilt_start + len(rebuilt_slice) - 1
            for rel, old_inst in enumerate(original_slice):
                if rel == 0:
                    target_map[old_inst.index] = rebuilt_start
                elif rel == len(original_slice) - 1 and old_inst.opcode == OPCODE_MESSAGE_COMMIT:
                    target_map[old_inst.index] = rebuilt_end
                else:
                    target_map[old_inst.index] = None
            new_instructions.extend(rebuilt_slice)
        idx = span_end

    target_map[len(instructions)] = len(new_instructions)
    remap_instruction_targets(new_instructions, target_map)
    return rebuild_blob_from_instructions(blob, new_instructions)



def pack_records(records: Sequence[Section2Record]) -> bytes:
    out = bytearray()
    for rec in records:
        out.extend(struct.pack("<HHII", rec.kind, rec.flag, rec.length, rec.offset))
    return bytes(out)


def split_speaker_body(text: str) -> Tuple[Optional[str], str]:
    if text.startswith("\u3010"):
        end = text.find("\u3011")
        if end > 1:
            return text[1:end], text[end + 1 :]
    return None, text


def classify_text_role(text: str) -> str:
    if text.strip():
        return "msg"
    return "other"


def collect_text_entries(blob: YstbBinary, source_encoding: str) -> List[TextEntry]:
    msg_entries = collect_oplist_text_entries(blob, source_encoding)
    set_value_entries = collect_set_value_text_entries(blob, source_encoding)
    menu_entries = collect_menu_text_entries(blob, source_encoding)
    out = msg_entries + set_value_entries + menu_entries
    for i, e in enumerate(out):
        e.text_id = i
    return out


def text_entry_to_json_item(e: TextEntry) -> Dict[str, object]:
    if e.text_role == "msg":
        speaker, body = split_speaker_body(e.text)
    else:
        speaker, body = None, e.text
    item: Dict[str, object] = {
        "id": e.text_id,
        "record_index": e.record_index,
        "type": e.kind,
        "flag": e.flag,
        "offset": e.offset,
        "length": e.length,
        "text_role": e.text_role,
        "extract_mode": e.extract_mode,
        "speaker": speaker,
        "body": body,
        "text": e.text,
    }
    if e.instr_start is not None:
        item["instr_start"] = e.instr_start
    if e.instr_end is not None:
        item["instr_end"] = e.instr_end
    if e.arg_slot is not None:
        item["arg_slot"] = e.arg_slot
    return item


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
        writer.writerow(
            [
                "id",
                "record_index",
                "type",
                "flag",
                "offset",
                "length",
                "text_role",
                "extract_mode",
                "speaker",
                "body",
                "text",
            ]
        )
        for e in text_entries:
            speaker, body = split_speaker_body(e.text)
            writer.writerow(
                [
                    e.text_id,
                    e.record_index,
                    e.kind,
                    e.flag,
                    e.offset,
                    e.length,
                    e.text_role,
                    e.extract_mode,
                    "" if speaker is None else speaker,
                    body,
                    e.text,
                ]
            )



def compose_text_from_json_item(path: Path, item: Dict[str, object], idx: int) -> str:
    text = item.get("text")
    if not isinstance(text, str):
        raise YstbError(f"{path}: text_entries[{idx}].text is not string")
    return text


def scan_json_id_lines(raw: str) -> Tuple[Dict[int, int], List[int]]:
    id_line_map: Dict[int, int] = {}
    id_line_order: List[int] = []
    for line_no, line in enumerate(raw.splitlines(), start=1):
        m = JSON_ID_LINE_RE.match(line)
        if m is None:
            continue
        try:
            text_id = int(m.group(1), 10)
        except ValueError:
            continue
        id_line_map[text_id] = line_no
        id_line_order.append(line_no)
    return id_line_map, id_line_order


def format_edit_position(edit: Optional[TextEdit]) -> str:
    if edit is None:
        return "translation=<unknown>"
    parts: List[str] = []
    if edit.source_path:
        parts.append(f"translation={edit.source_path}")
    if edit.source_line is not None:
        parts.append(f"line {edit.source_line}")
    if edit.source_loc:
        parts.append(edit.source_loc)
    if not parts:
        if edit.text_id is not None:
            return f"text_id={edit.text_id}"
        if edit.record_index is not None:
            return f"record_index={edit.record_index}"
        return "translation=<unknown>"
    return ", ".join(parts)


def describe_illegal_chars(exc: UnicodeEncodeError) -> str:
    obj = exc.object
    if not isinstance(obj, str):
        return repr(obj)

    bad = obj[exc.start : exc.end]
    if not bad:
        return "<unknown>"

    out: List[str] = []
    for ch in bad:
        if ch == "\n":
            shown = "\\n"
        elif ch == "\r":
            shown = "\\r"
        elif ch == "\t":
            shown = "\\t"
        elif ch == " ":
            shown = "<space>"
        else:
            shown = ch
        out.append(f"{shown}(U+{ord(ch):04X})")
    return ", ".join(out)


def build_encode_error_message(
    text_id: int,
    new_text: str,
    target_encoding: str,
    exc: UnicodeEncodeError,
    source_by_id: Dict[int, TextEntry],
    edit_origins: Optional[Dict[int, TextEdit]],
    sec3_base: int,
) -> str:
    entry = source_by_id.get(text_id)
    if entry is None:
        entry_desc = "entry=<unknown>"
        two_line_desc = "two_line_index=<unknown>"
    else:
        entry_desc = (
            f"entry(record_index={entry.record_index}, role={entry.text_role}, "
            f"extract_mode={entry.extract_mode})"
        )
        two_line_desc = f"two_line_index={sec3_base + entry.offset:08X}"

    origin = edit_origins.get(text_id) if edit_origins is not None else None
    origin_desc = format_edit_position(origin)

    preview = new_text.replace("\r", "\\r").replace("\n", "\\n")
    if len(preview) > 120:
        preview = preview[:117] + "..."

    illegal = describe_illegal_chars(exc)
    return (
        f"text id {text_id} encode failed with {target_encoding}: "
        f"illegal chars [{illegal}]; {two_line_desc}; {origin_desc}; {entry_desc}; "
        f"reason={exc.reason}; text='{preview}'"
    )


def load_text_edits_from_json(path: Path) -> List[TextEdit]:
    raw = path.read_text(encoding="utf-8-sig")
    obj = json.loads(raw)
    entries = obj.get("text_entries")
    if not isinstance(entries, list):
        raise YstbError(f"{path}: JSON missing text_entries list")

    id_line_map, id_line_order = scan_json_id_lines(raw)

    out: List[TextEdit] = []
    for i, item in enumerate(entries):
        if not isinstance(item, dict):
            raise YstbError(f"{path}: text_entries[{i}] is not object")
        text = compose_text_from_json_item(path, item, i)
        text_id = parse_optional_int(item.get("id"), path, f"text_entries[{i}].id")
        rec_idx = parse_optional_int(item.get("record_index"), path, f"text_entries[{i}].record_index")

        source_line: Optional[int] = None
        if text_id is not None:
            source_line = id_line_map.get(text_id)
        if source_line is None and i < len(id_line_order):
            source_line = id_line_order[i]

        out.append(
            TextEdit(
                text_id=text_id,
                record_index=rec_idx,
                text=text,
                source_path=str(path),
                source_loc=f"text_entries[{i}]",
                source_line=source_line,
            )
        )
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
            out.append(
                TextEdit(
                    text_id=text_id,
                    record_index=rec_idx,
                    text=text,
                    source_path=str(path),
                    source_loc=f"row[{i}]",
                    source_line=i + 2,
                )
            )
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


def is_set_value_extract_mode(extract_mode: str) -> bool:
    for prefix in SET_VALUE_EXTRACT_MODE_PREFIXES:
        if extract_mode.startswith(prefix):
            return True
    return False


def should_encode_as_marshaled(extract_mode: str) -> bool:
    if extract_mode == EXTRACT_MODE_OPLIST_MENU:
        return True
    return is_set_value_extract_mode(extract_mode)


def build_edit_map(
    edits: Sequence[TextEdit],
    source_entries: Sequence[TextEntry],
    src: Path,
) -> Tuple[Dict[int, str], Dict[int, TextEdit]]:
    by_id = {e.text_id: e for e in source_entries}
    by_record: Dict[int, List[TextEntry]] = {}
    for e in source_entries:
        by_record.setdefault(e.record_index, []).append(e)

    out: Dict[int, str] = {}
    origins: Dict[int, TextEdit] = {}
    for i, edit in enumerate(edits):
        target: Optional[TextEntry] = None
        loc = format_edit_position(edit)

        if edit.text_id is not None:
            target = by_id.get(edit.text_id)

        if target is None and edit.record_index is not None:
            candidates = by_record.get(edit.record_index, [])
            if len(candidates) == 1:
                target = candidates[0]
            elif len(candidates) > 1:
                raise YstbError(
                    f"{src}: ambiguous record_index at {loc}: {edit.record_index}; "
                    "multiple text entries share this record, use id to disambiguate"
                )

        if target is None:
            raise YstbError(f"{src}: cannot map edit at {loc} to source text entry")

        old = out.get(target.text_id)
        if old is not None and old != edit.text:
            raise YstbError(f"{src}: conflicting edits on text id {target.text_id} ({loc})")
        out[target.text_id] = edit.text
        origins[target.text_id] = edit
    return out, origins


def rebuild_with_text_edits(
    blob: YstbBinary,
    source_entries: Sequence[TextEntry],
    edit_map: Dict[int, str],
    source_encoding: str,
    target_encoding: str,
    encoding_errors: str,
    edit_origins: Optional[Dict[int, TextEdit]] = None,
    force_reencode_all: bool = False,
) -> YstbBinary:
    if encoding_errors not in {"strict", "replace", "ignore"}:
        raise YstbError(f"invalid encoding_errors: {encoding_errors}")

    source_by_id = {e.text_id: e for e in source_entries}
    oplist_edit_ids: List[int] = []
    plain_edit_ids: List[int] = []
    for text_id in edit_map:
        src_entry = source_by_id.get(text_id)
        if src_entry is None:
            raise YstbError(f"text id {text_id} is not editable text in source")
        if src_entry.instr_start is not None or src_entry.instr_end is not None:
            if src_entry.instr_start is None or src_entry.instr_end is None:
                raise YstbError(f"text id {text_id}: incomplete oplist instruction span")
            oplist_edit_ids.append(text_id)
        else:
            plain_edit_ids.append(text_id)

    if oplist_edit_ids:
        return rebuild_with_oplist_text_edits(
            blob=blob,
            source_entries=source_entries,
            edit_map=edit_map,
            source_encoding=source_encoding,
            target_encoding=target_encoding,
            encoding_errors=encoding_errors,
            source_by_id=source_by_id,
            edit_origins=edit_origins,
            force_reencode_all=force_reencode_all,
        )

    patches_by_range: Dict[Tuple[int, int], RangePatch] = {}
    range_text_value: Dict[Tuple[int, int], str] = {}
    sec3_base = HEADER_SIZE + len(blob.section1) + len(blob.records) * RECORD_SIZE

    for text_id, new_text in edit_map.items():
        src_entry = source_by_id.get(text_id)
        if src_entry is None:
            raise YstbError(f"text id {text_id} is not editable text in source")
        if (not force_reencode_all) and new_text == src_entry.text:
            continue

        enc = target_encoding
        try:
            if should_encode_as_marshaled(src_entry.extract_mode):
                new_bytes = encode_marshaled_string(new_text, enc, encoding_errors)
            else:
                new_bytes = new_text.encode(enc, errors=encoding_errors)
        except UnicodeEncodeError as exc:
            raise YstbError(
                build_encode_error_message(
                    text_id=text_id,
                    new_text=new_text,
                    target_encoding=enc,
                    exc=exc,
                    source_by_id=source_by_id,
                    edit_origins=edit_origins,
                    sec3_base=sec3_base,
                )
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
    edit_map, edit_origins = build_edit_map(edits, source_entries, translation)
    full_reencode = force_reencode_all or (target_encoding.lower() != source_encoding.lower())
    if full_reencode:
        for e in source_entries:
            edit_map.setdefault(e.text_id, e.text)
            edit_origins.setdefault(
                e.text_id,
                TextEdit(
                    text_id=e.text_id,
                    record_index=e.record_index,
                    text=e.text,
                    source_path=str(translation),
                    source_loc=f"text_id {e.text_id} (auto reencode)",
                    source_line=None,
                ),
            )

    rebuilt = rebuild_with_text_edits(
        blob=blob,
        source_entries=source_entries,
        edit_map=edit_map,
        source_encoding=source_encoding,
        target_encoding=target_encoding,
        encoding_errors=encoding_errors,
        edit_origins=edit_origins,
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
    error_log = Path(args.error_log) if args.error_log else default_compile_error_log_path(output)
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
            append_error_log(error_log, translation, exc)
            compact = compact_error_for_console(exc)
            print(f"[FAIL] {translation}: {compact} (details logged: {error_log})")
            raise SystemExit(1)
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
    issue_printed = 0
    issue_suppressed = 0
    max_issue_print = 40
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
            append_error_log(error_log, trans_file, exc)
            msg = str(exc)
            if "encode failed with" in msg:
                fail += 1
                if issue_printed < max_issue_print:
                    compact = compact_error_for_console(exc)
                    print(f"[FAIL] {trans_file}: {compact} (details logged: {error_log})")
                    issue_printed += 1
                else:
                    issue_suppressed += 1
            else:
                skip += 1
                if issue_printed < max_issue_print:
                    compact = compact_error_for_console(exc)
                    print(f"[SKIP] {trans_file}: {compact} (details logged: {error_log})")
                    issue_printed += 1
                else:
                    issue_suppressed += 1
        except Exception as exc:  # noqa: BLE001
            append_error_log(error_log, trans_file, exc)
            fail += 1
            if issue_printed < max_issue_print:
                compact = compact_error_for_console(exc)
                print(f"[FAIL] {trans_file}: {compact} (details logged: {error_log})")
                issue_printed += 1
            else:
                issue_suppressed += 1
    if issue_suppressed > 0:
        print(f"[INFO] {issue_suppressed} more issues suppressed on console; check log: {error_log}")
    print(f"[OK] compile done ok={ok} skip={skip} fail={fail} out={output} log={error_log}")


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description="YSTB Tool V555")
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
