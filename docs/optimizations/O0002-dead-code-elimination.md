# O0002 — Dead-code and dead-store elimination

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR lowering (statements) + IR middle end (blocks, values, stores) |
| **Source** | `Ir/IrLowering.cs` — `LowerStatements`; `Ir/Passes/SimplifyCfg.cs` — `RemoveUnreachable`; `Ir/Passes/Dce.cs`; `Ir/Passes/Mem2Reg.cs`; `Ir/Passes/DeadStoreElim.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF50.BAS` (dead stores), `PortedMidEndOptimizationsTests`, the full differential battery |
| **Related** | [O0017](O0017-sccp.md), [O0022](O0022-dead-procedure-elimination.md), [O0023](O0023-dead-global-elimination.md), [O0027](O0027-copy-propagation.md) |
| **Split into** | [O0183](O0183-ssa-dead-store.md) |

## What it is

**This page covers unreachable-statement elimination.** Anything after an
unconditional transfer (`GOTO`, `END`, `EXIT`, `RETURN`, `RESUME`) up to the next
label can never run, so it is not emitted. IR lowering skips every statement
that follows a block terminator until the next label, at every nesting level;
`SimplifyCfg` then deletes any block no path from the entry (or from an
address-taken label) reaches.

Removing *stores* whose value is never read is
[O0183](O0183-ssa-dead-store.md): `Mem2Reg` turns promotable scalars into SSA
values, `Dce` deletes a value nothing uses, and `DeadStoreElim` removes memory
stores that are overwritten or never read.

## Sample

```basic
DIM x%, y%
x% = 5
y% = x% * 2
PRINT y%
GOTO Done
PRINT "never"
Done:
END
```

## Without the optimizer

```asm
    mov     ax, 0005h
    mov     [x], ax          ; store kept
    ...                      ; y% = x% * 2 computed from the cell
    mov     [y], ax
    ...                      ; PRINT y%
    jmp     Done
    ...                      ; PRINT "never"  <- emitted, unreachable
Done:
    call    rt_exit
```

The literal `"never"` also occupies bytes in the string pool.

## With the optimizer

SCCP proves `x% = 5` and `y% = 10`; both stores lose their last real reader and
the unreachable `PRINT` never reaches the assembler:

```asm
    mov     ax, 000Ah
    call    rt_print_i16
    call    rt_print_nl
Done:
    call    rt_exit
```

## Equivalent BASIC

```basic
PRINT 10
END
```

## Why it is safe

- `Dce` removes an unused instruction only when its effect contract
  (`IrEffects`) says it may be discarded; writes, traps, I/O and other
  observable effects stay, so a right-hand side that could raise Error 6/9/11
  is still computed.
- `Mem2Reg` promotes only an alloca whose uses are all direct loads and stores;
  a variable whose address escapes (BYREF argument, `VARPTR`/`VARSEG`, inline
  asm) stays in memory, and `DeadStoreElim` removes its stores only when an
  alias query proves no load or call can observe them.
- Statements with compile-time effects — `DATA`, equates, `DEF`*type*,
  metastatements — survive the unreachable sweep even in dead positions,
  because their effect is on the compiler, not the program.

## Limits

- `DeadStoreElim`'s overwrite check is block-local; a store to memory that is
  read somewhere survives unless a later store in the same block completely
  overwrites it first.
- Dead *frame* stores (spill cells whose last reader load forwarding removed)
  need instruction-level recording — see
  [O0065](O0065-dead-frame-store-elimination.md).
