# LogicScope hardware reference

## Scope and safety

This is a 16-channel **sniffer-only** front end. It is not a 5 V-tolerant Teensy GPIO interface: the Teensy pins only see the translators' 3.3 V A-side outputs. Connect DUT ground to analyzer ground. The DUT must provide VCCB in the translator's operating range (1.65–5.5 V); the recommended signal range is 1.8–5.0 V. Do not connect negative, over-5.5 V, or powered-off/undefined target signals. This document is a reference schematic description, not a fabrication-ready PCB release; run electrical-rule, thermal, timing, and hot-plug review before building hardware.

## Block schematic

```text
                  +3V3 (Teensy)                         VCCB (DUT rail)
                       |                                      |
                 100 nF + 10 uF                         100 nF + 10 uF
                       |                                      |
       Teensy GPIO6/7  A1..A8  U1 SN74LVC8T245  B1..B8  220R  DUT CH0..7
          (input) <----[100R]----< DIR=0, OE=0 >----[optional clamp]---->
       Teensy GPIO6/7  A1..A8  U2 SN74LVC8T245  B1..B8  220R  DUT CH8..15
          (input) <----[100R]----< DIR=0, OE=0 >----[optional clamp]---->

  Teensy GND -------------------------------------------------- DUT GND
```

`DIR=0` selects B-to-A data flow for the TI part; `OE` is active-low. Fit 0 Ω straps from DIR and OE to GND for the default sniffer-only build. Optional DNP footprints can route these pins to Teensy GPIOs for a future, reviewed driver mode, but **remove the ground strap before fitting/using a GPIO link**. Do not allow software to drive the transceivers while the DUT is connected in this revision. Provide a 10 kΩ pull-down on each control net so an unpopulated option remains in the safe state.

The DUT rail is connected to `VCCB` (and **not** sourced by the Teensy). A-side is powered from Teensy 3V3. Use one 100 nF ceramic directly at each IC supply pin pair, plus one 10 µF bulk capacitor from each supply rail to GND near the two devices. The series resistors and clamps are not a substitute for an IEC-rated ESD design. The optional B-side clamp footprint should be populated with a low-capacitance part whose working voltage and leakage suit VCCB; avoid an upper clamp to an unpowered DUT rail because it can back-power that rail. Validate edge rate, loading and input threshold at the actual cable/connector length.

**Part-package correction:** SN74LVC8T245DWR is a 20-pin TSSOP (DB package), not TSSOP-24. Check the exact manufacturer's datasheet and suffix when releasing the PCB.

## KiCad-style netlist (logical)

```text
(net 1 "GND"
  (node (ref J1) (pin 2)) (node (ref U1) (pin GND)) (node (ref U2) (pin GND))
  (node (ref J2) (pin GND)) (node (ref J3) (pin GND))
  (node (ref C5) (pin 2)) (node (ref C6) (pin 2)))
(net 2 "+3V3"
  (node (ref J1) (pin 1)) (node (ref U1) (pin VCCA)) (node (ref U2) (pin VCCA))
  (node (ref C1) (pin 1)) (node (ref C3) (pin 1)) (node (ref C5) (pin 1)))
(net 3 "VCCB_DUT"
  (node (ref J2) (pin VCCB)) (node (ref U1) (pin VCCB)) (node (ref U2) (pin VCCB))
  (node (ref C2) (pin 1)) (node (ref C4) (pin 1)) (node (ref C6) (pin 1)))
(net 4 "DIR_SAFE_B_TO_A"
  (node (ref U1) (pin DIR)) (node (ref U2) (pin DIR))
  (node (ref RDIR0) (pin 1)) (node (ref RDIR10K) (pin 1)))
(net 5 "OE_N_SAFE_ENABLED"
  (node (ref U1) (pin OE_N)) (node (ref U2) (pin OE_N))
  (node (ref ROE0) (pin 1)) (node (ref ROE10K) (pin 1)))
; For n = 0..7, U1 handles CHn and U2 handles CH(n+8).
; A_n -> 100R -> corresponding Teensy pin.
; B_n <- 220R <- DUT CHn; optional low-C ESD device from connector node to GND.
; RDIR0/ROE0 are default-fit 0R-to-GND links; RDIR_MCU/ROE_MCU are DNP options.
```

For a PCB, use the real TI pin names (`A1…A8`, `B1…B8`, `VCCA`, `VCCB`, `GND`, `DIR`, `OE`), not this logical pin shorthand. Duplicate the channel resistor/clamp networks 16 times and add explicit test points for 3V3, VCCB, GND, PIT/debug and selected channel nets. The Teensy board is connected through two 1×N headers or a socket; avoid routing the high-speed GPIO input bundle through long stubs.

## BOM (reference)

| Ref | Qty | Part / value | Notes |
|---|---:|---|---|
| U1, U2 | 2 | SN74LVC8T245DWR | 8-bit dual-supply non-inverting transceiver, 20-pin TSSOP; confirm package suffix and voltage limits |
| R_A0…15 | 16 | 100 Ω, 1%, 0603 | A-side series resistors; place close to Teensy/transceiver output pin |
| R_B0…15 | 16 | 220 Ω, 1%, 0603 | DUT-side series/input protection; validate timing/load at target rate |
| C1…C4 | 4 | 100 nF, X7R, 10 V, 0603 | One per VCCA/VCCB pin pair per IC, shortest possible loop |
| C5, C6 | 2 | 10 µF, X7R, ≥10 V | One bulk capacitor from each supply rail (3V3 and VCCB) to GND near U1/U2; derate for DC bias |
| RDIR0, ROE0 | 2 | 0 Ω, 0603, fitted | Safe default control straps to GND |
| RDIR10K, ROE10K | 2 | 10 kΩ, 0603 | Pull-downs on DIR and OE_N control nets |
| RDIR_MCU, ROE_MCU | 2 | 0 Ω, 0603, DNP | Future GPIO options; must not be fitted with GND straps |
| D_B0…15 | 0–16 | Low-capacitance ESD protectors, DNP by default | Select after signal-integrity/ESD review; clamp to GND, not VCCB |
| J1 | 1 | Teensy 4.1 socket/header | 3V3, GND, mapped GPIO pins, optional debug/test pins |
| J2 | 1 | DUT power/ground header | VCCB is a DUT-supplied sense/power input; GND common |
| J3 | 1 | 16-channel signal connector | Pin pitch/connector selected for target environment; label CH0…CH15 |

## Fixed Teensy channel mapping

The Teensyduino Teensy 4.1 core's `CORE_PIN*_BIT` definitions were checked against this table. **The supplied mapping transposes the bits for pins 18 and 19.** On an unmodified Teensy 4.1, pin 18 is GPIO6 bit 17 and pin 19 is GPIO6 bit 16. The firmware uses the silicon/core mapping and therefore maps Ch4 (pin 18) from bit 17 and Ch5 (pin 19) from bit 16. The compile-time checks intentionally fail if the installed core disagrees. This correction is required for the pins to correspond to their labeled Teensy pins.

| Logical channel | Teensy pin | GPIO register | GPIO bit (actual core) | Comment |
|---:|---:|---|---:|---|
| 0 | 0 | GPIO6_DR | 3 | |
| 1 | 1 | GPIO6_DR | 2 | |
| 2 | 14 | GPIO6_DR | 18 | |
| 3 | 15 | GPIO6_DR | 19 | |
| 4 | 18 | GPIO6_DR | 17 | Supplied draft stated 16; corrected |
| 5 | 19 | GPIO6_DR | 16 | Supplied draft stated 17; corrected |
| 6 | 20 | GPIO6_DR | 26 | |
| 7 | 21 | GPIO6_DR | 27 | |
| 8 | 6 | GPIO7_DR | 10 | |
| 9 | 7 | GPIO7_DR | 17 | |
| 10 | 8 | GPIO7_DR | 16 | |
| 11 | 9 | GPIO7_DR | 11 | |
| 12 | 10 | GPIO7_DR | 0 | |
| 13 | 11 | GPIO7_DR | 2 | |
| 14 | 12 | GPIO7_DR | 1 | |
| 15 | 13 | GPIO7_DR | 3 | |

The packed logical sample uses `sample & (1u << ch)` for channel `ch`. Firmware only configures these pins as inputs and the DMA transfer source is exactly `GPIO6_DR` or `GPIO7_DR`. On the host, samples are already in logical D0…D15 order (see protocol notes); sending two raw GPIO words would require a different wire format.

## Power and layout review notes

- `VCCB_DUT` is only powered when a valid DUT supply is present. Do not bridge it to Teensy 3V3 or VIN.
- Keep USB shield/chassis grounding and DUT ground strategy deliberate. The DUT and analyzer grounds must be common for the translator to work.
- Check transceiver OE/DIR truth table, power sequencing, partial-power-down (`Ioff`) specifications, maximum input clamp current and `VCCB` decoupling in the exact datasheet revision used for production.
- Do not add large capacitance to the input nodes; 16 × input capacitance plus cable capacitance can alter edges. Validate at both ends of each 220 Ω resistor.
- The 100 Ω and 220 Ω resistor values are starting values from the requested design, not a guaranteed 100 MSa/s signal-integrity solution. At fast edges, route as a controlled, short, low-stub bus and measure channel-to-channel skew.
