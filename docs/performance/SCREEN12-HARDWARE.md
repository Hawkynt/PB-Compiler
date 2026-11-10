# SCREEN 12: reproducible VGA hardware measurements

Software CPU dispatch counts, instruction fetches, DOSBox timing and QEMU
TCG timing are **not** physical 8086/80286/80386 measurements. This fixture
lets users measure real performance independently of the correctness oracles.

## Build

With .NET 10 and Bash (the variant verified in Linux CI):

```powershell
dotnet build PB-Compiler.slnx -c Release
bash scripts/build-vga-hardware-bench.sh
```

On Windows, `scripts/build-vga-hardware-bench.ps1` offers an optional PowerShell entry point; its generated binaries must be validated separately. Six DOS EXEs are generated in `build/vga-hardware/`:
`V86OPT.EXE`, `V86REF.EXE`, `V286OPT.EXE`, `V286REF.EXE`,
`V386OPT.EXE`, `V386REF.EXE`. Each uses an explicit CPU directive
and the same measured workload.

## Measure on real hardware

Use VGA-equipped DOS hardware at each indicated CPU tier. Record CPU
frequency, VGA chipset, bus, DOS version and driver settings. Run each
variant in a separate directory or rename the resulting `VGA.TXT` between
runs; repeat several times and compare medians on **the same** machine.

Each run records iteration count, BIOS timer ticks and final pixel probes
for solid BF rectangles, styled LINE and aligned planar GET/PUT. One BDA
0040:006C tick is ~54.925 ms; calculate elapsed milliseconds as
`ticks * 54.925`. The low 16-bit tick counter wraps about once an hour:
avoid tests crossing the wrap or adjust the timing harness. For samples
shorter than several ticks, increase `loops%` in the source and rebuild
**both** variants before comparing.

This timer measures observed machine wall time, including BIOS calls,
VGA wait states and bus contention, not theoretical instruction cycles.
Do not substitute QEMU TCG timing for physical 8086/286/386 timing.

## Separate correctness oracles

`scripts/test-qemu-vga.sh` independently boots a VGA-register oracle
under QEMU and compares BIOS pixels and all four hardware planes with
published VGA semantics. The focused DOSBox workflow separately checks
generated compiler graphics. QEMU (GPL) and NASM (BSD-2-Clause) are
test-only executables installed by CI; no implementation code is copied
from them into the LGPL-3.0 compiler.

The full generated graphics battery still needs a QEMU DOS guest
and physical timing records before P8 hardware acceptance can be closed.
