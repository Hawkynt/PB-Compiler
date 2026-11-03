# R0002 — Fast graphics primitives

| | |
|---|---|
| **Status** | ✅ Packed pixel reads/writes/fusion, horizontal/rectangle/PAINT spans and IR DRAW; ⬜ planar batching and general DRAW partial evaluation |
| **Stage** | IR specialization and runtime |
| **Source** | `Ir/Passes/PixelRuntimeInlining.cs`, `Ir/Passes/DrawStringSpecialization.cs`, `Runtime/DrawRuntime*.cs`, `Runtime/DosRuntime.Pixels.cs` |
| **Gate** | Proven packed mode; `$OPTION VRAMPACK ON\|OFF` independently controls only pixel-store batching (pb36); complete DRAW text or a delimiter-terminated leading movement sequence |
| **Verified by** | `PixelRuntimeInliningTests`, `PixelModeFallbackTests`, `DrawStatementTests`, `ProvenDrawTests`, `DrawMixedStringTests`, `DrawRuntimeErrorTests` |
| **Related** | [R0001](R0001-fast-text-output.md), [O0004](O0004-strength-reduction.md), [O0064](O0064-lea-fusion.md) |

## What it is

In `SCREEN 13` the frame buffer is a flat 320 × 200 byte array at `A000:0000`,
so a pixel is a single byte store at `y * 320 + x`. `PSET`, `PRESET` and `POINT`
use direct segment accesses for that layout. Generic calls first query the active BIOS mode;
non-packed layouts use BIOS pixel services. When the IR proves mode 13, its inlineable pixel body
avoids that mode query and exposes the offset calculation to the ordinary loop optimizers.
Final native lowering forms direct far loads/stores. Only compiler-certified packed stores may
fuse; the numeric address A000h alone is not proof. POINT loads carry a non-discardable device
effect so the packer's final pure-instruction cleanup cannot erase an unused observation.

In-bounds solid horizontal lines, filled rectangles and discovered PAINT scanlines compute their
span addresses once, using REP STOSW on 8086 or CPU-gated REP STOSD with word/byte tails on 386+.
Other modes, patterned lines and out-of-range geometry retain their existing paths. Graphics
cursor updates and ES restoration are shared with or matched to those paths.

The address arithmetic itself is the interesting part: `y * 320 + x` is exactly
the expression [O0003](O0003-common-subexpression-elimination.md) caches and
[O0004](O0004-strength-reduction.md) can reduce to shifts or an induction recurrence.
Supported `DRAW` commands exposed by string folding call shared typed-IR movement operations,
not an emitter-local macro expansion. Dynamic text uses an IR interpreter with the same state.

## Sample

```basic
SCREEN 13
DIM x%, y%
FOR y% = 0 TO 199
  FOR x% = 0 TO 319
    PSET (x%, y%), (x% XOR y%) AND 15
  NEXT
NEXT
```

## Emitted

```asm
    ; packed pixel primitive: AX=offset computed by IR, DL=colour
    push    es
    push    di
    push    bx
    mov     di, ax
    mov     bx, 0A000h
    mov     es, bx
    mov     es:[di], dl
    pop     bx
    pop     di
    pop     es
    ret
```

## Equivalent BASIC

```basic
DEF SEG = &HA000
POKE y% * 320& + x%, c%
```

## Why it is safe

Mode 13h's linear address formula is used only with a proven mode or the runtime's mode dispatch.
Unknown calls, assembly and raw memory invalidate the mode proof. Pixel readback and explicit
COM/MZ execution are checked against captured genuine QuickBASIC files. This does not prove
clipping, pages, viewports or complete graphics error semantics.

## Independently disabling VRAM store packing

In the `pb36` dialect, `$OPTION VRAMPACK OFF` disables only the
late native **width-changing** coalescing of already-certified Mode 13h
pixel byte stores:

```basic
$CPU 80386
$OPTION VRAMPACK OFF
SCREEN 13
PSET (10,5),1
PSET (11,5),2
PSET (12,5),3
PSET (13,5),4
```

Four individual framebuffer byte writes remain, and direct `PSET`/`POINT`
specialization, generic RAM store packing, all unrelated SSA optimizations,
CPU selection, and pre-existing runtime `REP STOS` scanline fast paths are
unchanged. `$OPTION VRAMPACK ON` restores the default; when directives
repeat, the last one applies to the compilation unit. Both directives
require `pb36`: neither is claimed to be accepted by original PBC 3.50.

**The hardware aperture is not a sufficient legality proof.** Physical
`A0000h-BFFFFh` can be a VGA memory window, but the selected subrange,
chain-4/odd-even/planar addressing, latches, write mode, and segment:offset
aliasing determine the effect of accesses. Treat arbitrary `DIM ... AT`,
`POKE`, and raw ASM writes as independent observable operations unless
the relevant framebuffer semantics have been proven. In particular,
`segment=0xA000` alone is not a video-store packing certificate.

## What is still planned

Circle and flood-fill coverage still has mode-specific limits. Fast vertical/45-degree lines and
planar EGA/VGA batches remain work, including hardware-state, clipping and cursor contracts.

Mixed text can inline a known leading movement sequence ending at `;`, then resume interpretation
at the following byte. The joined allocation remains live until interpretation ends or fails;
mode-check errors release it before raising. Arbitrary interior/suffix spans remain interpreted.
Source concatenation boundaries are not command boundaries: `"R" + n$` cannot be expanded independently. See
[IR-proven DRAW text](../X86-BACKEND.md#ir-proven-draw-text) for the implemented slice and exclusions.
