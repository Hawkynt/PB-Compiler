# R0002 — Fast graphics primitives

| | |
|---|---|
| **Status** | ✅ Packed pixels, IR DRAW parser and safe leading-span inlining; ⬜ planar batching and general DRAW partial evaluation |
| **Stage** | IR specialization and runtime |
| **Source** | `Ir/Passes/PixelRuntimeInlining.cs`, `Ir/Passes/DrawStringSpecialization.cs`, `Runtime/DrawRuntime*.cs`, `Runtime/DosRuntime.Pixels.cs` |
| **Gate** | Proven packed mode; complete DRAW text or a delimiter-terminated leading movement sequence |
| **Verified by** | `PixelRuntimeInliningTests`, `PixelModeFallbackTests`, `DrawStatementTests`, `ProvenDrawTests`, `DrawMixedStringTests`, `DrawRuntimeErrorTests` |
| **Related** | [R0001](R0001-fast-text-output.md), [O0004](O0004-strength-reduction.md), [O0064](O0064-lea-fusion.md) |

## What it is

In `SCREEN 13` the frame buffer is a flat 320 × 200 byte array at `A000:0000`,
so a pixel is a single byte store at `y * 320 + x`. `PSET`, `PRESET` and `POINT`
use direct segment accesses for that layout. Generic calls first query the active BIOS mode;
non-packed layouts use BIOS pixel services. When the IR proves mode 13, its inlineable pixel body
avoids that mode query and exposes the offset calculation to the ordinary loop optimizers.

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

## What is still planned

Line/box routines already call the mode-aware pixel primitive; circle and flood-fill coverage
still has mode-specific limits. Fast horizontal/vertical/45-degree lines, byte spans and planar
EGA/VGA batches remain work, including their hardware-state, clipping and cursor contracts.

Mixed text can inline a known leading movement sequence ending at `;`, then resume interpretation
at the following byte. The joined allocation remains live until interpretation ends or fails;
mode-check errors release it before raising. Arbitrary interior/suffix spans remain interpreted.
Source concatenation boundaries are not command boundaries: `"R" + n$` cannot be expanded independently. See
[IR-proven DRAW text](../X86-BACKEND.md#ir-proven-draw-text) for the implemented slice and exclusions.
