# O0027 — Copy propagation

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Pre-emission analysis over tracked scalars |
| **IR** | ✅ `Mem2Reg` + `Gvn` + `Dce` in `IrPassManager.Standard()` - a copy chain does not survive SSA construction at all; verified by `PortedMidEndOptimizationsTests` |
| **Source** | `CodeGen/OptCopyProp.cs` |
| **Gate** | `--optimize` |
| **Related** | [O0002](O0002-dead-code-elimination.md), [O0017](O0017-sccp.md), [O0046](O0046-ir-gvn.md) |

## What it is

A copy `y = x` — where the right-hand side is a bare read of another tracked
scalar of the same type — makes `y` and `x` the same value. Every read of `y` is
redirected to `x`'s cell and the copy statement disappears.

Copy **chains** resolve to the root: in `b = a : c = b`, both `b` and `c` read
`a`'s cell, and both copies drop.

## Sample

```basic
DIM a%, b%, c%
a% = 7
b% = a%
c% = b%
PRINT c% + b%
```

## Without the optimizer

```asm
    mov     ax, 0007h
    mov     [a], ax
    mov     ax, [a]
    mov     [b], ax
    mov     ax, [b]
    mov     [c], ax
    mov     ax, [c]
    add     ax, [b]
    ...
```

## With the optimizer

```asm
    mov     ax, 0007h
    mov     [a], ax
    mov     ax, [a]
    add     ax, [a]
    ...
```

and with [O0017](O0017-sccp.md) proving `a% = 7`, the whole thing collapses to
`MOV AX,14`.

## Equivalent BASIC

```basic
DIM a%
a% = 7
PRINT a% + a%
```

## Why it is safe

The source must be assigned **at most once**, so its cell is stable across the
copy's live range; a chain resolves to the root whose cell is actually written,
which guarantees a redirected read never lands on a cell nothing wrote. Escaping
variables are not tracked. The pass composes with SSA dead-store elimination
(which then removes the stores that fed only the dropped copies) and is
byte-identical across the full differential harness.
