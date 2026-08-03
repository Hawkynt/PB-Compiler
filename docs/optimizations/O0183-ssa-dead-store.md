# O0183 — SSA dead-store elimination

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | SSA mid-end |
| **Source** | `CodeGen/Ssa/DeadStore.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF50.BAS` |
| **Split from** | [O0002](O0002-dead-code-elimination.md) (which is now unreachable-statement elimination only) |

## What it is

An aggressive mark-sweep over the SSA form removes assignments to non-escaping
tracked scalars whose version is never really read — either the value is dead
outright, or [O0017](O0017-sccp.md) already folded every read to a constant, so
the store that produced it is pointless.

Liveness is seeded from real (unfolded) reads in statements that are not
themselves removable, and from branch conditions; it then propagates through phi
inputs and the right-hand sides of kept assignments, so a value kept alive only
by a chain of dead copies dies with the chain.

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

Only literal, equate and variable-copy right-hand sides qualify — they cannot
trap and have no side effects, so dropping the store is unobservable. An
escaping variable is not tracked at all, and a right-hand side that could raise
Error 6/9/11 keeps its store.
