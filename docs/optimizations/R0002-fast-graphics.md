# R0002 — Fast graphics primitives

| | |
|---|---|
| **Status** | ✅ Implemented for mode-13h pixels (`PSET`/`PRESET`/`POINT`); `LINE`/`CIRCLE`/`PAINT`/`DRAW` codegen is ⬜ planned |
| **Stage** | Emitter + runtime |
| **Source** | `CodeGen/CodeGenerator.Extras.cs`, `Runtime/DosRuntime.*` |
| **Gate** | `SCREEN 13` |
| **Verified by** | pixel readback through `POINT`; the screen-capture oracle |
| **Related** | [R0001](R0001-fast-text-output.md), [O0004](O0004-strength-reduction.md), [O0064](O0064-lea-fusion.md) |

## What it is

In `SCREEN 13` the frame buffer is a flat 320 × 200 byte array at `A000:0000`,
so a pixel is a single byte store at `y * 320 + x`. `PSET`, `PRESET` and `POINT`
are emitted as **direct** segment accesses — there is no BIOS per-pixel path at
all, so "fast graphics" holds by construction rather than by measurement.

The address arithmetic itself is the interesting part: `y * 320 + x` is exactly
the expression [O0003](O0003-common-subexpression-elimination.md) caches and
[O0004](O0004-strength-reduction.md) reduces to shifts.

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
    push    0A000h
    pop     es
    mov     ax, [y]
    ...                      ; y*320 via shifts (O0004), cached across the row (O0003)
    add     ax, [x]
    mov     di, ax
    mov     es:[di], al      ; the whole pixel write
```

## Equivalent BASIC

```basic
DEF SEG = &HA000
POKE y% * 320& + x%, c%
```

## Why it is safe

Mode 13h's linear layout is architectural, so the address computation is exact;
`POINT` reads back through the same expression, which is what the pixel-readback
test asserts.

## What is still planned

`LINE`, `PRESET` spans, `CIRCLE`, `PAINT` and `DRAW` are parsed and bound but
codegen answers `not yet generated: LineStmt`. Two oracle-verified notes for
whoever implements them:

- the parser rejects the relative form `LINE (x,y)-STEP(dx,dy), c, BF`, which
  PBC 3.50 accepts (it compiles PB-VGAEditor's `SPRITE.SUB`);
- `STEP`'s base differs by position — the cursor for a first/only point, the
  first point for a `LINE`'s endpoint — so it belongs in `ParsePoint` as a flag
  on the point, not as a statement-level switch.

Then the fast forms follow: spans as `REP STOSB` (`STOSD` under 386),
run-sliced Bresenham lines, and planar EGA/VGA writes batched per plane (map
mask set once per span, not per pixel).
