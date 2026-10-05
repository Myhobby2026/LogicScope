# LogicScope capture and export formats

The application writes the following formats. All exports contain the packed logical D0–D15 word; they do not contain the two raw GPIO bank reads. The `.lgs` format and Saleae raw import workflow are LogicScope-specific/export-workflow formats, not undocumented vendor project archives.

## Native `.lgs` v1

A 40-byte little-endian header is followed by one Zstandard frame containing the interleaved sample words.

| Offset | Bytes | Field | Value / encoding |
|---:|---:|---|---|
| 0 | 8 | Magic | ASCII `LGSCOPE1` |
| 8 | 2 | Format version | `1` |
| 10 | 2 | Header size | `40` |
| 12 | 4 | Sample rate | u32 Hz |
| 16 | 2 | Channel count | `16` |
| 18 | 2 | Sample format | `1` = packed unsigned u16, little-endian |
| 20 | 8 | Sample count | u64 |
| 28 | 4 | Uncompressed payload bytes | u32, exactly `sampleCount × 2` |
| 32 | 4 | Compressed payload bytes | u32 |
| 36 | 4 | CRC-32 | IEEE CRC-32 of the uncompressed sample bytes |
| 40 | variable | Payload | One Zstandard frame, `u16` little-endian per sample |

Each sample's bit `n` is logical channel D`n`. The reader validates dimensions, exact file length, decompressed size, and CRC before returning samples. The format contains no trigger index, channel visibility, annotations, or analog values in v1.

## Sigrok / PulseView `.sr` v2

`.sr` is a ZIP archive containing `version` (`2\n`), INI-style `metadata`, and `logic-1`. The generated device metadata declares 16 probes, `unitsize=2`, a sample rate, and names D0 through D15. `logic-1` stores the packed sample words as little-endian 16-bit values. This is a digital-only export; it does not include LogicScope decoder results or UI channel visibility.

## VCD

The VCD export declares 16 one-bit wires in one module scope. The time scale is 1 ns; transition timestamps are rounded to the nearest nanosecond from the supplied sample rate. The first sample is emitted at `#0`. VCD time starts at zero for the exported window; the original absolute sample index is not preserved in VCD timestamps.

## Sample CSV

The first row is:

```text
sample_index,time_seconds,D0,D1,...,D15
```

Rows use invariant-culture numbers, absolute sample indexes from the `SampleWindow`, seconds calculated from the sample rate, and one `0`/`1` column per channel. Decoder results can be exported separately with start/end samples and seconds, category, error flag, and escaped label.

## Saleae Logic 2 raw binary workflow

The `.bin` file contains one packed `u16` little-endian word per sample. A sibling `.bin.import.txt` file records the sample rate, word format, channel/bit order, and sample count for use in Logic 2's raw-binary import dialog. Logic 2 does **not** consume the sidecar, and this export is not a `.sal` or `.logicdata` archive.
