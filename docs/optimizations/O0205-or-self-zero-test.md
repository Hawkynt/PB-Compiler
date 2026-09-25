# O0205 — Zero test as `OR reg,reg`

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | x86 back end (machine combiner, before scheduling) |
| **Source** | `Backend/MachineCombiner.cs` — `CombineCompareZero`, `AuxiliaryFlagUnobservableAfter` (run from `Backend/MachineScheduler.cs` for optimized functions) |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF44.BAS` |
| **Split from** | [O0008](O0008-peephole-zero-idiom.md) |

## What it is

A comparison against zero collapses to a register self-test — two bytes instead
of three, with the same ZF and SF. The IR back end emits `TEST reg,reg` rather
than `OR reg,reg`: same size and the same flags for every `Jcc`, without writing
the register.

## Sample

```basic
DIM n%
IF n% = 0 THEN PRINT "zero"
```

## Without / with

```asm
    mov     ax, [n]
    cmp     ax, 0000h        ; 3 bytes
    ; becomes
    mov     ax, [n]
    or      ax, ax           ; 2 bytes (IR back end: test ax, ax)
```

## Why it is safe

`CMP r,0` always leaves OF = 0 and CF = 0, and `TEST` (like `OR`) clears both
too, so every `Jcc` that can follow a zero comparison reads the same answer. The
one flag that differs is AF, which `TEST` leaves undefined; the combiner only
rewrites when every flag reader reachable before AF is overwritten is one of the
equivalent consumers, so inline assembly reading AF (`LAHF`/`PUSHF`) keeps the
`CMP`.

## See also

Reusing flags an earlier ALU instruction already set (so that no compare is
emitted at all) is [O0081](O0081-flag-reuse.md).
