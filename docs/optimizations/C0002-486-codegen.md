# C0002 — `$CPU 80486` gate

| | |
|---|---|
| **Status** | ✅ Implemented (`BSWAP`, alignment); instruction-selection tuning is ⬜ partial |
| **Stage** | Emitter + assembler |
| **Source** | `Asm/Assembler.Instructions.cs`, `CodeGen/CodeGenerator.cs` — `AlignLoopTop` |
| **Gate** | `$CPU 80486` / `-G486` (`LanguageFeature.Cpu486`); loop alignment additionally needs `$OPTIMIZE SPEED` |
| **Related** | [C0001](C0001-386-codegen.md), [O0041](O0041-branch-layout.md), [R0004](R0004-asm-intrinsics.md) |

## What it is

- **`BSWAP`** for endian flips (the `MKx$`/`CVx` big-endian helpers, graphics
  masks); `XADD` and `CMPXCHG` exposed to inline-asm authors.
- **Alignment-driven layout**: procedure entries are 16-byte aligned (reached
  only by `CALL`, so the pad never executes), and hot **loop tops are NOP-padded
  to a 16-byte boundary** on every loop emitter — the general `FOR`/`DO`, the
  int16 fast `FOR`, and every register-resident, pointer-stepped or
  auto-vectorized loop. The pad runs once on the fall-through entry and is
  skipped by the back-edge, so it is output-invariant
  ([O0041](O0041-branch-layout.md)).
- **Instruction selection tuned to the 486's 1-cycle simple ops**: prefer
  `MOV`/`ADD`/`INC` chains over microcoded instructions (`LOOP` → `DEC CX`/`JNZ`,
  avoid `XLAT`/`ENTER`/`LEAVE`) — partially beneficial on the 386 too.

## Sample

```basic
$CPU 80486
$OPTIMIZE SPEED
DIM v&, s&
s& = CVL(MKL$(v&))          ' an endian round trip
```

## Without the gate

```asm
    mov     ax, [v]
    mov     dx, [v+2]
    xchg    al, ah           ; hand-rolled byte swapping
    xchg    dl, dh
    xchg    ax, dx
```

## With the gate

```asm
    mov     eax, [v]
    bswap   eax
```

## Equivalent BASIC

Unchanged.

## Why it is safe

`BSWAP` is a pure register permutation with a well-defined result, and the
alignment pads are `NOP`s on a path that executes at most once. The gate is a
declaration by the programmer that the target is a 486 — the same contract
`$CPU 80386` already carries.

## What is still planned

DWORD-aligning hot **data** slots is the remaining piece of the alignment work,
and the microcode-avoidance selection rules are applied opportunistically rather
than driven by a per-target cost model ([O0174](O0174-target-cost-models.md)).
