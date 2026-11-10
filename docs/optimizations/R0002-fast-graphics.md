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
match the original pixel routine. The shared ordered-byte accumulator now also handles in-bounds vertical and
diagonal Mode 12h Bresenham strokes, every edge of a styled box and `DRAW`
strokes that reuse `rt_line`. `CIRCLE` feeds its ordered mirror points through
the same planar cache, and bounded positive-aspect elliptical arcs use it after
a conservative runtime proof. The generic BIOS path remains for unproved
geometry, atypical colors or device state.
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

The Mode 12h rectangle and byte-mask paths avoid per-pixel BIOS interrupts for in-bounds
solid fills, horizontal/vertical/diagonal styled `LINE`, `DRAW` line segments, CIRCLE mirror
points, and a proven subset of aspect-ratio elliptical arcs. Nonoptimized compilations,
unproved VGA state and unsupported color or clipping cases retain the original runtime.
`PSET`, `POINT`, `PAINT`, `GET/PUT` and graphics glyph output are not yet broadly
accelerated. In particular, `PRINT` follows its DOS output contract, not a Mode 12h
font renderer. See the [staged Mode 12h PRD](../prd/mode12-planar-graphics-acceleration.md).

## What is still planned

Further graphics batching still requires stronger VGA hardware-state contracts, optimized
`PAINT`, generalized sprite blits, screen-mode-specific clipping and cursor behavior, and
hardware performance validation. Mode 13h styled lines avoid a BIOS query at every pixel,
but partially masked word/dword writes remain unproven and are not widened blindly.

### De-DRAW: partially evaluate proven macro strings into typed graphics IR

`DrawStringSpecialization` consumes compiler-proven Latin-1 string literals after the existing
string-folding passes (including pure, constant-foldable string operations). It also recognizes
**multiple concatenated constant leaves** to the left of the first dynamic fragment, rather
than stopping at the first literal. A partially known prefix is specialized only at a complete
movement plus explicit `;` command boundary. The original joined string remains owned by
`rt_draw_resume`, which resumes at the exact byte offset, with the original parser and
failure cleanup. `"R" + n# R0002 — Fast graphics primitives

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
match the original pixel routine. The shared ordered-byte accumulator now also handles in-bounds vertical and
diagonal Mode 12h Bresenham strokes, every edge of a styled box and `DRAW`
strokes that reuse `rt_line`. `CIRCLE` feeds its ordered mirror points through
the same planar cache, and bounded positive-aspect elliptical arcs use it after
a conservative runtime proof. The generic BIOS path remains for unproved
geometry, atypical colors or device state.
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

The Mode 12h rectangle and byte-mask paths avoid per-pixel BIOS interrupts for in-bounds
solid fills, horizontal/vertical/diagonal styled `LINE`, `DRAW` line segments, CIRCLE mirror
points, and a proven subset of aspect-ratio elliptical arcs. Nonoptimized compilations,
unproved VGA state and unsupported color or clipping cases retain the original runtime.
`PSET`, `POINT`, `PAINT`, `GET/PUT` and graphics glyph output are not yet broadly
accelerated. In particular, `PRINT` follows its DOS output contract, not a Mode 12h
font renderer. See the [staged Mode 12h PRD](../prd/mode12-planar-graphics-acceleration.md).

## What is still planned

Further graphics batching still requires stronger VGA hardware-state contracts, optimized
`PAINT`, generalized sprite blits, screen-mode-specific clipping and cursor behavior, and
hardware performance validation. Mode 13h styled lines avoid a BIOS query at every pixel,
but partially masked word/dword writes remain unproven and are not widened blindly.

 cannot be expanded independently: that concatenation boundary
does not delimit a DRAW command.

For entirely known text, the IR now contains the executable commands instead of a string parser:

- `C`, `S`, `A` and `TA` become typed state stores to the existing shared runtime cells.
  `TA` is normalized only for valid input in [-360,360]; invalid values remain runtime errors.
- `Ppaint,border` updates the same foreground/paint state and calls the existing `rt_paint`
  wrapper. **A partially known string containing `P` is not specialized**, since PAINT can fail
  and the dynamic joined string must be released before the error is raised.
- Absolute `M` commands, which ignore scale/rotation, become `rt_line` calls plus explicit
  cursor, endpoint, style and color IR. Relative movements are similarly decompiled only when
  the same proven prefix sets **S4 and A0/TA0** and both delta components are in the
  signed-16-bit multiplication-safe range [-8192,8191]. This preserves the runtime's
  `S4` multiply-before-divide semantics; inherited settings from an earlier DRAW must never
  be guessed from the startup defaults.
- `B` (blank) and `N` (draw without cursor update) retain their exact cursor and raster
  semantics. Other movement types still call the shared `rt_draw_move` IR helper, which
  eventually invokes `rt_line` under the current scale, rotation and mode.

### Cross-statement video-state proof barriers

The drawing cursor is a logical BASIC state; the hardware mode, selected VGA
registers, plane latches and current page are separate observable states.
Never use one as proof of another. Compiler optimizations spanning DRAW,
LINE, PSET, POINT, PAINT or other source statements must revalidate the
active mode/hardware configuration after any effect not explicitly known
harmless.

The forward `IrScreenModeAnalysis` proves a screen mode only after a
correctly typed `rt_screen_mode` call with a constant argument. Runtime
pixel calls are allowed to preserve this fact **only** when their full
BASIC call ABI matches the compiler's expected signature. Other calls
and indirect calls (including calls to arbitrary BASIC procedures),
inline assembly, `INP`/`OUT`, `PEEK`, unproved `POKE`, and unknown far
memory loads/stores invalidate this proof. A narrowly certified
`DEF SEG = &HA000` packed framebuffer POKE is a documented exception:
it cannot program the controller, but it is still an observable pixel
write and is not generally movable or erasable.

`IrVgaRegionAnalysis` separately records configuration, latch and
observation epochs; its reuse queries are valid only in forward order
within one basic block. All unknown device effects and branches fence
reuse. In particular, `PEEK` can observe or modify VGA read latches even
when it does not change the BIOS video mode number; an arbitrary `OUT`
can change memory decoding while BIOS still reports `SCREEN 12`.
Runtime low-level planar paths also independently certify controller
addressing before using their byte-mask accelerators.

The DRAW decompiler is restricted to one proven uninterrupted macro
string/prefix. It never folds separate DRAW statements across a CALL or
moves drawing across arbitrary BASIC operations. If inter-statement
fusion, overdraw elimination or a persistent virtual drawing cursor is
added later, require the same mode, register, latch, memory-alias and
observer-effect proofs at every intervening instruction before allowing
such a rewrite. Do not infer safety solely from a known screen number.

The specialization is deliberately conservative around error handlers, inline assembly, unusual
runtime ABI/cell definitions, dynamic numerical parameters, `X` substring execution, and
unrepresentable numeric arguments. Differential tests compare optimized and interpreter
framebuffers and constant-prefix/dynamic-suffix behavior; IR tests verify that the string parser
and redundant movement dispatch disappear only for proved cases.

**Future candidates, not yet enabled:** Collapsing four DRAW line segments into a `LINE ... ,B`
rectangle, overdraw-elimination, converting arbitrary shapes into `CIRCLE`, and moving a constant
suffix across a dynamic fragment. These demand bit-exact proofs for intermediate pixel writes,
clipping, fill topology, logical raster operations, style phase, B/N cursor effects and runtime
errors. Merely recognizing a visually rectangular or overwritten shape is not sufficient.

See [IR-proven DRAW text](../X86-BACKEND.md#ir-proven-draw-text) for the implemented pipeline.
