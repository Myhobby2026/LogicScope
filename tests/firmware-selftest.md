# Firmware loopback self-test procedure

1. Build/upload the `firmware/` PlatformIO project for a Teensy 4.1.
2. Set the hardware VCCB rail to 3.3 V, share ground, and confirm all 16 analyzer inputs are idle before connecting test signals.
3. Connect Teensy test output pin 22 to DUT-side `CH0`, and pin 23 to DUT-side `CH1`, through the intended input/test-series network. This test is only approved at a VCCB compatible with the Teensy 3.3 V output; never drive a 1.8 V DUT rail from 3.3 V. If a safe rail/test driver is unavailable, use an external level-matched signal generator instead.
4. Send `GET_CAPS` (`02`) and read 16 response bytes. Check `hwChannels == 16` and use the advertised firmware RAM sample count.
5. Send `SELFTEST` (`03`). Firmware starts two independent hardware PWM outputs (nominal 1 MHz) on pins 22/23 and returns status state 4. The capture pins remain inputs.
6. Start a short burst (e.g. 2 MSa/s, no trigger) or streaming capture through the C# host. The PWM continues while capture is active. Verify D0 and D1 transition at the expected phase/frequency and that other channels are quiet. Data packets should have the self-test-active flag. Because the two PWM channels need not be phase locked to one another, this checks channel presence/order, not inter-bank skew.
7. Stop acquisition from the host. Firmware returns to self-test standby (state 4) while leaving the PWM outputs enabled. Send `SELFTEST` (`03`) again; the outputs stop and firmware returns to idle state 0.
8. Repeat at the planned VCCB minimum, nominal and maximum using a voltage-safe source. For DMA acceptance, compare a hardware timer/strobe against the sampled edge count and run a long streaming soak while checking packet sequence gaps, DMA-overrun status, and host throughput.

The self-test is a wiring aid, not a calibrated 1 MHz standard. Verify actual PWM frequency and duty cycle on an oscilloscope at the DUT connector before using it as a measurement reference.
