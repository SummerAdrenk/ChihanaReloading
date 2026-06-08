# Image Read Chain

This note records the Start.exe image loading chain verified from:

- `G:\CHS\Kaguya\BB\16#おっぱいでかいナースとイチャコラエロ×２入院生活！？\export-for-ai\export-for-ai_Start.exe`
- original archive backup samples under `...\bk\*.arc`

## Archive Read Layer

Main entry: `Start.exe!sub_465390`.

The LINK entry flag handling is an `if / else if / else if` chain:

| Flag | Engine branch | Payload layout |
| --- | --- | --- |
| `flags & 1` | `sub_435040` | `u32le unpackedSize` followed by LINK LZSS bitstream |
| `flags & 2` | `sub_435540` + `sub_4301D0` | raw `BMR` stream at payload offset 0 |
| `flags & 4` | `sub_446590` | format-specific XOR transform |

Important: `flags & 2` payload must start directly with ASCII `BMR`.
`sub_435540` compares the first three bytes of the payload against the BMR magic and returns `payload + 0x10` to the BMR decoder. There is no BMP-name or extension prefix in the engine path.

Verified original samples:

| Archive | Entry | Flags | Raw payload head |
| --- | --- | --- | --- |
| `bk\bgd.arc` | `bg_black.bmp` | `2` | `42 4D 52 ...` (`BMR`) |
| `bk\bgd.arc` | `bg_white.bmp` | `2` | `42 4D 52 ...` (`BMR`) |
| `bk\bgd.arc` | `左から右へ.alp` | `2` | `42 4D 52 ...` (`BMR`) |
| `bk\cg00.arc` | `cg01_1.bmp` | `4` | plaintext BMP header, encrypted pixel area |

## LINK LZSS

Verified from `sub_435040`.

- Initial window position is `1`.
- Control bits are MSB-first.
- `1 + 8 bits`: literal byte.
- `0 + 12 bits offset + 4 bits length`: copy from 4 KiB ring buffer.
- Offset `0` terminates the stream.
- Copy length is `encodedLength + 2`.
- Outer LINK payload stores `u32le unpackedSize` before this bitstream.

No `flags & 1` image entry was found in the BB16 sample manifests, but the engine branch is real and is now implemented separately from BMR.

## LINK BMR

Verified from `sub_435540`, `sub_4301D0`, `sub_42FE60`, and samples.

The payload starts at byte 0 with:

| Offset | Field |
| --- | --- |
| `0x00` | `BMR` |
| `0x03` | RLE step, `0` means no RLE stage |
| `0x04` | final decoded size |
| `0x08` | BWT primary index |
| `0x0C` | Huffman/MTF/BWT intermediate size |
| `0x10` | Huffman bitstream size |
| `0x14` | Huffman bitstream |

Decode order:

1. Huffman decode.
2. Undo MTF.
3. Inverse BWT.
4. Optional RLE expand if step is not zero.

## LINK Encryption

Verified from `sub_441650`, `sub_446590`, `sub_444590`, and `sub_4446F0`.

`sub_441650` requires a non-empty key whose size is a multiple of 32 bytes.

For LINK5/LINK6 entries, `sub_446590` calls `sub_4446F0`. It XORs by 32-byte key blocks and repeats over the key size. Full dwords are XORed normally. Tail bytes follow the engine's little-endian remainder behavior:

| Remaining bytes | Key byte order |
| --- | --- |
| 1 | `key[0]` |
| 2 | `key[1], key[0]` |
| 3 | `key[2], key[1], key[0]` |

Known encrypted payload ranges:

| Format | Range |
| --- | --- |
| BMP | from BMP pixel offset, normally `0x36` for these samples |
| `AP-0` / `AP-1` | from `0x0C` |
| `AP-2` / `AP-3` | from `0x18` |
| `AN00` / `AN10` | each raw frame pixel payload |
| `AN20` / `AN21` | each image payload block, after control/branch tables |
| `PL00` / `PL10` | frame pixel payload ranges |

For older LINK versions, `sub_444590` uses a different key indexing formula. Current pack output is LINK6, so the implemented re-encryption path follows `sub_4446F0`.

## Static Image Layer

Main chain:

- `sub_46A410`: load sprite command wrapper.
- `sub_465390`: reads/decompresses/decrypts archive entry.
- `sub_411D50`: converts the read buffer to a surface.
- `sub_40D000`: detects BMP or AP/ALP.
- `sub_40FC20`: calls `SurfaceLoadBMP` or `SurfaceLoadALP` from the graphics DLL.

`sub_40D000` format detection:

| Magic | Engine path |
| --- | --- |
| `BM` | BMP, uses BITMAPFILEHEADER/BITMAPINFO and pixel offset |
| `AP-0` | AP/ALP variant 0 |
| `AP-1` | AP/ALP variant 1 |
| `AP-2` | AP/ALP variant 2 |
| `AP-3` | AP/ALP variant 3 |

## PL / Animation Layer

`sub_437490` recognizes `PL` files and dispatches by version:

| Header | Engine mode |
| --- | --- |
| `PL00` | mode `0.0` |
| `PL10` | mode `1.0` |
| `PL20` | mode `2.0` |
| `PL30` | mode `3.0` |
| `PL01` | mode `0.1` |
| `PL11` | mode `1.1` |

For mode `>= 2.1`, `sub_437080` expects an embedded `[PIC]` marker and then dispatches that sub-version through the same picture frame handlers.

The current BB16 sample set did not contain extracted `PLxx` files, so `PL20/PL30/PL01/PL11` are IDA-proven branches but not sample-validated here. The tool implements these branches in `ArcPLT`; `PL30` repack writes the engine-supported raw-diff subpath.

## ANM / PIC Frame Branches

`sub_436FB0` dispatches the image payload branch by floating mode:

| Mode | Function | Meaning |
| --- | --- | --- |
| `0.0` | `sub_436000` | raw frames |
| `0.1` | `sub_4361A0` | first raw frame, following frames are additive diffs |
| `1.0` | `sub_4364C0(..., 0)` | diff frames with byte/RLE-like packed deltas |
| `1.1` | `sub_4364C0(..., 1)` | diff frames with Huffman/block path |
| `2.0` | `sub_436870` | per-frame compressed payload, mode `3` or `4` |
| `3.0` | `sub_436C90` | block-converted diff payload path |

`sub_436870` compression mode:

| Mode | Payload |
| --- | --- |
| `3` | `BMR` |
| `4` | `u32le unpackedSize + LZSS bitstream` |

Any other mode throws through the engine error path.

## Error Branches

Observed/confirmed error handling:

- `sub_465390` throws if an entry is missing, archive seek/read fails, decompression init fails, or key setup fails.
- `sub_435540` returns null when BMR magic is absent; callers then fail during decode/use.
- `CBitIO::Getbit()` throws if BMR Huffman bitstream runs past its declared byte count.
- `sub_46A410` throws the load-sprite failure object if `sub_411D50` fails.
- `sub_437490` / `sub_436FB0` throw on unknown PL/PIC versions or unsupported modes.

## Tool Alignment

Implemented alignment in `LinkArchiveCodec`:

- `flags & 1` is handled as LINK LZSS, not BMR.
- `flags & 2` is strict naked BMR at payload offset 0.
- BMR recompression no longer writes a BMP extension prefix.
- LINK5/6 XOR transform now follows `sub_4446F0` tail-byte behavior.

Implemented alignment in `ArcPLT`:

- `PL00`, `PL01`, `PL10`, `PL11`, `PL20`, and `PL30` are recognized by sorter and handler.
- `PL01` extracts/rebuilds first raw frame plus raw additive diff frames.
- `PL11` extracts/rebuilds first raw frame plus Huffman-only additive diff frames.
- `PL20` extracts/rebuilds frame records with compression mode `3` BMR or `4` LZSS.
- `PL30` extracts block-convert payloads and rebuilds using the engine-supported raw-diff subpath.
