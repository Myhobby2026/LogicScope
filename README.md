# LogicScope

LogicScope is an open-source 16-channel digital logic analyzer reference project for a Teensy 4.1 / i.MX RT1062 device and a Windows 10/11 .NET 8 WPF application. It includes design documentation, the Teensy firmware project, a binary USB CDC protocol, host acquisition/decoding/export libraries, and tests.

> **Hardware and performance status:** this repository is not a released PCB or a calibrated instrument. The firmware and hardware path still require a Teensy 4.1 / PlatformIO build, bench validation, and sustained-throughput testing. Do not treat advertised rate ceilings as measured performance. Read [`docs/architecture.md`](docs/architecture.md) and [`docs/hardware.md`](docs/hardware.md) before connecting a DUT.

## Repository layout

- `docs/architecture.md` — DMA/XBAR design, memory constraints, data path, and bring-up checklist.
- `docs/hardware.md` — input front end, reference BOM/netlist, safety notes, and the verified Teensy pin mapping.
- `docs/protocol.md` — byte-level USB CDC protocol v1 and packet definitions.
- `docs/file-formats.md` — `.lgs`, sigrok `.sr`, VCD, CSV, and Saleae raw-binary layout/caveats.
- `firmware/` — PlatformIO Teensy 4.1 firmware project.
- `app/LogicScope.sln` — Core, Hardware, Decode, Export, WPF App, and xUnit projects.
- `tests/` — host unit tests, native firmware trigger tests, and the firmware self-test procedure.

## Build and test

### Windows desktop application

Install the .NET 8 SDK and a Windows 10/11 x64 desktop workload, then run from the repository root:

```powershell
dotnet test .\app\LogicScope.sln -c Release
dotnet run --project .\app\LogicScope.App\LogicScope.App.csproj -c Release
```

The WPF application discovers CDC COM ports by probing the `GET_CAPS` handshake. A device that has not been built/flashed will not connect; captures can still be opened from `.lgs` files. The UI supports waveform zoom/pan, channel visibility, edge and host-side pattern triggers, protocol decoding, measurements/cursors/bus groups, and the available export formats.

### Teensy firmware

Install PlatformIO Core and the Teensy platform/toolchain, connect a Teensy 4.1, and run:

```sh
pio run -d firmware -e teensy41
pio run -d firmware -e teensy41 -t upload
```

The firmware project targets the Teensy 4.1 Arduino framework. Hardware bring-up must verify the actual pin/core map, GPIO-bank skew, XBAR/DMAMUX request behavior, DMA/cache handling, USB transport, and the selected operating rate. See the checklist in [`docs/architecture.md`](docs/architecture.md).

### Firmware host-side unit tests

Trigger behavior, logical GPIO remapping, command parsing, status/capability records, and USB packet serialization are tested with a native C++17 harness. The harness supplies a small fake `Arduino.h` Serial device and does not replace or mock the Teensy DMA/XBAR hardware path. With a C++17 compiler:

```sh
bash tests/run-firmware-unit-tests.sh
```

On Windows, run the equivalent `g++`/Clang command in the script or use a compatible C++17 compiler. The Teensy on-device self-test and loopback procedure is documented in [`tests/firmware-selftest.md`](tests/firmware-selftest.md).

## Important implementation limits

- The RT1062 on Teensy 4.1 is **single-core**; no dual-core feature is claimed.
- The sampler DMA-captures one 32-bit word from each of GPIO6 and GPIO7. Firmware remaps completed blocks into packed logical 16-bit D0–D15 samples before USB transmission. The host wire format consequently does not contain the two raw GPIO words.
- The fixed pin assignment is preserved, but Teensyduino maps physical pin 18 to GPIO6 bit 17 and pin 19 to GPIO6 bit 16. This differs from the supplied draft's bit labels; compile-time checks and [`docs/hardware.md`](docs/hardware.md) document the corrected mapping without swapping channels.
- Burst storage is bounded to 131,072 packed samples in the current firmware. It is not a 512K-sample capture, and no 100 MSa/s / 30–35 MB/s sustained result is asserted.
- Firmware mask/value pattern triggering is not encoded in protocol v1. The app evaluates patterns on received samples; burst patterns are located host-side after upload and therefore do not provide firmware pre-trigger capture.

## License

LogicScope is licensed under the MIT License; see [`LICENSE`](LICENSE).
