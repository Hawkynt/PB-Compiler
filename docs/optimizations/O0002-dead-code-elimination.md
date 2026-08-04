# O0002 — Dead-code and dead-store elimination

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Pre-emission pruner (statements) + SSA mid-end (stores) |
| **IR** | ✅ `Ir/Passes/Dce.cs` + `DeadStoreElim` in `IrPassManager.Standard()`; verified by `PortedMidEndOptimizationsTests` |
| **Source** | `CodeGen/OptPruner.cs`, `CodeGen/Ssa/DeadStore.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF50.BAS` (dead stores), the full differential battery |
| **Related** | [O0017](O0017-sccp.md), [O0022](O0022-dead-procedure-elimination.md), [O0023](O0023-dead-global-elimination.md), [O0027](O0027-copy-propagation.md) |
| **Split into** | [O0183](O0183-ssa-dead-store.md) |

## What it is

**This page covers unreachable-statement elimination.** Anything after an
unconditional transfer (`GOTO`, `END`, `EXIT`, `RETURN`, `RESUME`) up to the next
label can never run, so it is not emitted. The pruner walks recursively into
`IF`, `SELECT` and loop blocks before emission.

Removing *stores* whose value is never read is the SSA-tier pass
[O0183](O0183-ssa-dead-store.md).

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

- Only literal, equate and variable-copy right-hand sides qualify as removable
  stores: they cannot trap and have no side effects, so dropping the store is
  unobservable. A store whose RHS could raise Error 6/9/11 stays.
- A variable that escapes (BYREF argument, `VARPTR`/`VARSEG`, inline asm, any
  opaque statement) is not tracked at all.
- Statements with compile-time effects — `DATA`, equates, `DEF`*type*,
  metastatements — survive the unreachable sweep even in dead positions,
  because their effect is on the compiler, not the program.

## Limits

- The SSA form bails on post-test loops, `GOTO`/labels, `GOSUB` and `ON ERROR`;
  bodies it cannot model precisely keep every store.
- Dead *frame* stores (spill cells whose last reader load forwarding removed)
  need instruction-level recording — see
  [O0065](O0065-dead-frame-store-elimination.md).
