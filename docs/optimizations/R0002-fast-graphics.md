# R0002 — Fast graphics primitives

| | |
|---|---|
| **Status** | ✅ Packed pixel reads/writes/fusion, horizontal/rectangle/PAINT spans and IR DRAW; ⬜ planar batching and general DRAW partial evaluation |
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
Final native lowering forms direct far loads/stores. Only compiler-certified packed stores may
fuse; the numeric address A000h alone is not proof. POINT loads carry a non-discardable device
effect so the packer's final pure-instruction cleanup cannot erase an unused observation.

In-bounds solid horizontal lines, filled rectangles and discovered PAINT scanlines compute their
span addresses once in Mode 13h, using REP STOSW on 8086 or CPU-gated REP STOSD with word/byte tails on 386+.
Mode 12h solid filled rectangles now have a separate, guarded four-plane VGA path: Write Mode 2
broadcasts one 4-bit color to all planes, full eight-pixel groups use REP STOSB, and masked
edge bytes explicitly preload the VGA latches before writing. GC/SQ port indices and modified
registers are restored. It is not a generic A000h wide-write transformation.

For Mode 12h horizontal `LINE` with a 16-bit style, the optimized path also
accumulates **selected pixel positions** into an eight-bit mask for each VGA
byte, then emits one Write Mode 3 masked color write per affected byte. VGA
configuration is hoisted to the start of the line, while `rt_gstyle` still
rotates once per visited position (including skipped ones), in the correct
forward/reverse direction. The result preserves gaps and existing background
colors; a final graphics cursor update and restored VGA register/index state
match the original pixel routine. The existing generic Bresenham path still
handles vertical/diagonal/styled edges not yet lowered to byte masks.
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

## Planar follow-up scope

The new Mode 12h rectangle path eliminates the BIOS pixel call per filled pixel but does not
yet accelerate `PSET`, `POINT`, styled/diagonal `LINE`, `CIRCLE`, aspect-ratio arcs and
ellipses, `PAINT`, `GET/PUT` actions, or graphics glyph output. In particular, `PRINT`
currently follows its DOS output contract, not a Mode 12h font renderer. See the
[staged Mode 12h PRD](../prd/mode12-planar-graphics-acceleration.md).

## What is still planned

Circle and flood-fill coverage still has mode-specific limits. Fast vertical/45-degree lines and
planar EGA/VGA batches remain work, including hardware-state, clipping and cursor contracts.

Mixed text can inline a known leading movement sequence ending at `;`, then resume interpretation
at the following byte. The joined allocation remains live until interpretation ends or fails;
mode-check errors release it before raising. Arbitrary interior/suffix spans remain interpreted.
Source concatenation boundaries are not command boundaries: `"R" + n$` cannot be expanded independently. See
[IR-proven DRAW text](../X86-BACKEND.md#ir-proven-draw-text) for the implemented slice and exclusions.
