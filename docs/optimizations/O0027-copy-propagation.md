# O0027 — Copy propagation

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR middle end (SSA construction) |
| **Source** | `Ir/Passes/Mem2Reg.cs`, `Ir/Passes/Gvn.cs`, `Ir/Passes/Dce.cs` (in `IrMiddleEndPipeline.Standard`) |
| **Verified by** | `PortedMidEndOptimizationsTests` (`O0027_GivenAChainOfCopies_ThenTheyCollapseToTheSource`) |
| **Gate** | `--optimize` |
| **Related** | [O0002](O0002-dead-code-elimination.md), [O0017](O0017-sccp.md), [O0046](O0046-ir-gvn.md) |

## What it is

A copy `y = x` — where the right-hand side is a bare read of another scalar of
the same type — makes `y` and `x` the same value. There is no separate pass for
it: when `Mem2Reg` promotes the variables to SSA (a module-level variable once
`LocalizeGlobals`, [O0278](O0278-global-variable-localization.md), has made it
a local), the load of `x` and the store to `y` disappear and every later read
of `y` *is* the value `x` held, so the copy never exists as an instruction.

Copy **chains** resolve to the root the same way: in `b = a : c = b`, both `b`
and `c` are `a`'s value, and both copies drop.

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

SSA gives every definition its own value, so a later write to `x` is a new
value and cannot change what `y` already holds; phis merge the copies that
reach a join. `Mem2Reg` promotes only stack slots whose every use is a direct
load or store of the slot's own shape, so a variable whose address escapes stays
in memory and is not propagated through. `Gvn` and `Dce` then remove the
redundant and unused values that remain.
