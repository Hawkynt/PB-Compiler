# O0183 — SSA dead-store elimination

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR middle end |
| **Source** | `Ir/Passes/Mem2Reg.cs`; `Ir/Passes/Sccp.cs`; `Ir/Passes/Dce.cs`; `Ir/Passes/DeadStoreElim.cs` for memory that stays in memory |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF50.BAS` |
| **Split from** | [O0002](O0002-dead-code-elimination.md) (which is now unreachable-statement elimination only) |

## What it is

Under the optimizer `Mem2Reg` promotes every alloca whose uses are all direct,
same-width loads and stores — a non-escaping scalar variable — into SSA values:
each load is replaced by the value that reaches it and every store to the slot
disappears. A variable that is written but never read therefore leaves no store,
and one whose reads [O0017](O0017-sccp.md) (`Sccp`) folds to constants leaves no
store either. `Dce` then removes the right-hand-side computations nothing uses
any more, cascading through their operands, so a value kept alive only by a chain
of dead copies dies with the chain.

A variable whose address escapes stays in memory; there `DeadStoreElim` removes
stores to unread compiler-private frame objects
([O0065](O0065-dead-frame-store-elimination.md)) and stores completely overwritten
later in the same block ([O0048](O0048-ir-dead-store-elimination.md)).

## Sample

```basic
DIM x%
x% = 5
PRINT x%
```

## Without / with

```asm
    mov     ax, 0005h        ;  <- store removed
    mov     [x], ax          ;  <-
    mov     ax, 0005h        ; the read was folded by SCCP
    call    rt_print_i16
```

becomes

```asm
    mov     ax, 0005h
    call    rt_print_i16
```

## Why it is safe

Promotion only applies where every access is visible in the use graph, so no
pointer, BYREF argument or `VARPTR` can observe the removed store. `Dce` only
deletes an instruction whose effect contract (`IrEffects`) allows discarding it:
a right-hand side that could raise Error 6/9/11, write memory or perform I/O
stays. The faithful (`--no-optimize`) path uses `Mem2Reg.RunForFaithfulSelection`,
which keeps a variable that is written but never read.
