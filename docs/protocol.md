# LogicScope binary protocol v1

Transport: Teensy native USB Serial CDC, treated by the host as an ordered byte stream. There is no baud-rate timing; the host opens the enumerated COM port and sends bytes. All multi-byte integer fields are unsigned, little-endian. Firmware sends raw binary only (never `Serial.print()` diagnostics on this endpoint).

The protocol intentionally matches the requested v1 framing. It has no application-level checksum; USB packet CRC/retry protects the bus, while the 8-bit packet sequence and header-length checks expose many host-visible losses. If an application-level CRC is required, define a versioned protocol extension rather than inserting bytes into v1 packets.

## PC → firmware commands

### `START` — opcode `0x01`

Exactly 9 bytes, in this order:

| Offset | Size | Field | Values |
|---:|---:|---|---|
| 0 | 1 | opcode | `0x01` |
| 1 | 4 | requested sample rate in Hz | u32 LE; implementation rounds to a supported integer PIT divider and rejects unsupported/unsafe requests |
| 5 | 1 | mode | `0` streaming, `1` burst |
| 6 | 1 | trigger channel | `0`…`15`; `0xFF` disables firmware edge triggering |
| 7 | 1 | trigger edge | `0` falling, `1` rising |
| 8 | 1 | pre-trigger percentage | `0`…`100`; used in burst mode |

A host must send the complete fixed-size record. A new `START` while active is rejected with a status/error packet. A start is acknowledged with state `1` (streaming) or `2` (burst); the PC waits for and validates this status before reporting capture started. Self-test outputs may remain enabled during acquisition; stopping such a capture returns to state `4` rather than `0`. In v1, the firmware edge trigger is a single-channel edge; the host can apply pattern/software triggers to streaming captures. The UI can search burst samples for a pattern after upload, but the v1 wire command cannot convey a 16-bit mask/value pattern to the firmware.

### `STOP` — opcode `0x00`

One byte. Stops sampling at a block boundary. In burst mode, firmware uploads the samples already retained (including a completed trigger window if one occurred) before returning to idle or self-test standby. In streaming mode it stops after the current block. `STOP` also disables self-test outputs when sent in self-test standby. A redundant `STOP` in ordinary idle state is ignored without a response.

### `GET_CAPS` — opcode `0x02`

One byte. Firmware replies with exactly 16 bytes (there is no leading marker):

| Offset | Size | Field | Encoding |
|---:|---:|---|---|
| 0 | 4 | `maxBurstRateHz` | u32 LE; firmware-supported configuration ceiling, not a measured guarantee |
| 4 | 4 | `maxStreamRateHz` | u32 LE; firmware-supported configuration ceiling, not a measured guarantee |
| 8 | 4 | `ramSamples` | u32 LE; packed logical `u16` samples available in burst storage |
| 12 | 1 | firmware major | u8 |
| 13 | 1 | firmware minor | u8 |
| 14 | 1 | firmware patch | u8 |
| 15 | 1 | hardware channels | `16` |

Send GET_CAPS while idle (state `0`) or in self-test standby (state `4`), then read exactly the 16-byte response. Firmware emits no unsolicited boot packet, so the unframed reply cannot be prefixed by a status record. If the DMA sampler fails its startup checks, the advertised rate and RAM fields are zero and the host must reject the device. Since the v1 caps record is intentionally unframed, do not interleave it with data/status parsing.

### `SELFTEST` — opcode `0x03`

One byte. Toggles the two hardware-PWM self-test outputs on pins 22 and 23. The outputs generate nominal 1 MHz square waves when enabled. Connect them to two DUT-side test inputs only when the test wiring and `VCCB` rail make the voltage safe; see [hardware.md](hardware.md). A status packet reports the resulting self-test state. This command does not change the 16 capture pins' input direction.

## Firmware → PC packets

### Data — marker `0xAA`

The fixed header is 8 bytes followed by the stated number of samples:

| Offset | Size | Field | Encoding |
|---:|---:|---|---|
| 0 | 1 | marker | `0xAA` |
| 1 | 1 | sequence | u8, increments modulo 256 per data packet |
| 2 | 1 | mode | `0` streaming, `1` burst |
| 3 | 1 | flags | bit field below |
| 4 | 4 | `nsamples` | u32 LE; maximum is 2,048 samples per packet (4 KiB payload) |
| 8 | `2 × nsamples` | sample payload | u16 LE per sample; D0 is bit 0, D15 bit 15 |

Flags in v1:

| Bit | Meaning |
|---:|---|
| 0 | final data packet of a burst/stop upload |
| 1 | firmware edge trigger has fired in this burst |
| 2 | acquisition stopped because the DMA ring overran (data is incomplete) |
| 3 | self-test was active while this packet was formed |
| 4–7 | reserved; sender writes zero, receiver ignores unknown bits |

The source of each payload sample is one raw GPIO6 word plus one raw GPIO7 word. Firmware remaps those captured words to logical D0…D15 before sending. The host therefore **does not** receive raw GPIO-bank words and must not apply a second remap.

### Status — marker `0x55`

Exactly 3 bytes: `[0x55][state:u8][lastError:u8]`.

State values: `0` idle/self-test off, `1` streaming, `2` burst capture/armed, `3` burst upload, `4` self-test enabled and sampler idle. State `4` can also be the terminal state of a capture run while self-test PWM continues; a host that is waiting for capture data treats state `0` or `4` as the end of that run. Error values: `0` none, `1` bad/unsupported command, `2` invalid rate/mode/trigger configuration, `3` DMA ring overrun, `4` start while busy, `5` USB write/transport failure. A successful `START` is followed by state `1` or `2`; a stopped/completed capture ends in state `0`, or state `4` if self-test remains enabled. Unknown values must be tolerated by the host.

## Stream parsing and recovery

1. The host scans for marker `0xAA` or `0x55` while in capture mode. Status is fixed at 3 bytes; data is fixed header plus `2 × nsamples` bytes.
2. Reject data headers with a sample count above the firmware's documented bound before allocating or reading the payload.
3. Track expected sequence modulo 256. A gap increments the dropped-packet counter; it means data was not delivered to the host, not necessarily that the firmware DMA itself lost a PIT event.
4. If a candidate header is invalid, advance by one byte and rescan. Do not trust `nsamples` before validating it.
5. A CDC disconnect or read exception invalidates the in-flight packet and starts device discovery/GET_CAPS again.
6. Do not send text diagnostics on this stream; a debug UART, SWO, or compile-time disabled logging is required.

The packet format contains no capture timestamp. Host time is sample index divided by the *effective* rate. The requested rate is mapped to an integer PIT divider (`150 MHz / divider` in this build); a UI must show the selected/derived rate and must not present the requested value as calibrated timing. Any different clock tree or firmware rate policy requires a protocol version/metadata addition.
