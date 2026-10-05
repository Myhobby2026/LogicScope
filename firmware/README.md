# Teensy 4.1 firmware

Build with PlatformIO Core 6 and the Teensy platform:

```sh
cd firmware
pio run -e teensy41
pio run -e teensy41 -t upload
```

The `platformio.ini` selects Teensyduino/Arduino USB Serial. Open the board's native USB COM port in LogicScope; the firmware does not emit text diagnostics on `Serial`.

## Capture path

- PIT channel 0 is clocked from the standard 150 MHz `F_BUS_ACTUAL`/IPG domain. The requested rate is quantized to an integer divider; the host should derive the effective rate as `150 MHz / divider`.
- XBARA1 connects `PIT_TRIGGER0` to `DMA_CH_MUX_REQ30`, edge qualified at XBAR output 0. Two eDMA channels subscribe to `DMAMUX_SOURCE_XBAR1_0`; each transfers one 32-bit bank word per timer event.
- Each bank has a four-slot scatter/gather ring of 8,192-word blocks in DMA-visible OCRAM. A completed raw pair is cache-invalidated, remapped and processed only in the foreground. The per-block interrupt does bounded slot bookkeeping; it never reads GPIO.
- A sample payload is packed to one little-endian `u16`, D0 at bit 0 and D15 at bit 15. No raw GPIO register is read by the CPU in the sample loop.
- The trigger module supports single-channel rising/falling edges and an internal mask/value matcher. The current v1 START record exposes edge triggering; adding firmware pattern fields needs a versioned command extension.
- Streaming packets contain 2,048 samples (4 KiB payload) except a possible final smaller packet. Burst capture storage is 131,072 packed samples. Both figures are reported through GET_CAPS.

The `maxBurstRateHz` and `maxStreamRateHz` capabilities are configured ceilings (20 MSa/s and 10 MSa/s in this source), **not validated performance claims**. The design has not been characterized for missed XBAR requests, inter-bank skew, worst-case USB stalls, or uninterrupted sustained transfer. The ring detects a consumer overrun and stops rather than silently claiming lossless capture; DMA request overrun itself must still be verified with a hardware counter/strobe test before claiming a production sample rate.

## Self-test

Command `0x03` toggles hardware PWM outputs on Teensy pins 22 and 23 at nominal 1 MHz. With the analyzer powered down, connect those test outputs through suitable resistors to DUT-side input pads for channels 0 and 1. Verify VCCB and voltage limits first. Do not use a 3.3 V test output directly with a 1.8 V VCCB rail. After powering up, `START` is permitted while self-test is active; the PWM continues during capture, and data packets carry the self-test flag. Stopping the capture returns the device to self-test standby (state 4); send `0x03` again to disable the outputs. See `../tests/firmware-selftest.md`.
