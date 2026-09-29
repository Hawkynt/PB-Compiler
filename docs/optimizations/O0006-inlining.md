# O0006 — Procedure inlining

| | |
|---|---|
| **Status** | ✅ Implemented (non-recursive procedures within a size budget) |
| **Stage** | IR middle end |
| **Source** | `Ir/Passes/Inliner.cs` — `Run`, `IsInlinable`, `InlineCall`; run by `Ir/Passes/IrMiddleEndPipeline.cs` — `RunNativeModule` after the pass sweep and followed by another; `Ir/Passes/GlobalDce.cs` removes the callee once nothing calls it |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF30.BAS` (mixed eligible/ineligible callees, side-effecting and nested arguments) |
| **Related** | [O0018](O0018-interprocedural-constant-propagation.md), [O0021](O0021-register-parameters.md), [O0022](O0022-dead-procedure-elimination.md), [O0053](O0053-ir-inliner.md) |
| **Split into** | [O0200](O0200-trivial-method-inlining.md), [O0201](O0201-inlined-procedure-purge.md) |

## What it is

A small `SUB`/`FUNCTION` is replaced by its body at the call site: the frame
setup, the `CALL`, the `RET` and the argument push/pop traffic all disappear.
The larger point is that the callee body becomes visible to the caller's
optimizer, which is why `RunNativeModule` runs the whole pass sweep again after
inlining.

**This page covers the procedure inline itself**; inlining trivial TYPE
methods ([O0200](O0200-trivial-method-inlining.md)) and purging a
fully-inlined procedure ([O0201](O0201-inlined-procedure-purge.md)) are separate
entries.

Mechanics (`InlineCall`):

- the call site's block is split, and everything after the call becomes a
  continuation block;
- the callee's blocks are cloned into the caller (`IrCloner`) with each
  parameter mapped to the call's argument value — the arguments were already
  evaluated **once**, in order, at the call site;
- the callee's own allocas are cloned too, so two inlinings of the same
  procedure never share a local;
- each cloned `ret` becomes a branch to the continuation, and the call's result
  is the single returned value or a phi over the returns.

The same inliner handles **trivial TYPE methods and properties** in `pb36`:
the `THIS` receiver is the ordinary BYREF argument it is, so `o.Count` on an
anonymous property is as cheap as a field access, and a hand-written
`FUNCTION Sum() = THIS.x + THIS.y` inlines the same way.

## Sample

```basic
FUNCTION Twice%(BYVAL v%)
  Twice% = v% * 2
END FUNCTION

DIM n%, r%
n% = 21
r% = Twice%(n%)
PRINT r%
```

## Without the optimizer

```asm
    mov     ax, [n]
    push    ax               ; argument
    call    Twice
    mov     [r], ax
    ...
Twice:                        ; a whole frame for one multiply
    push    bp
    mov     bp, sp
    sub     sp, <locals>
    ...                       ; frame zeroing
    mov     ax, [bp+6]
    shl     ax, 1
    mov     [bp-2], ax       ; result slot
    mov     ax, [bp-2]
    mov     sp, bp
    pop     bp
    ret     2
```

## With the optimizer

```asm
    mov     ax, [n]
    mov     [bp-8], ax       ; argument temp (evaluated once)
    mov     ax, [bp-8]
    shl     ax, 1
    mov     [r], ax
```

…and `Twice` is not emitted at all.

## Equivalent BASIC

```basic
DIM n%, r%, t%
n% = 21
t% = n%
r% = t% * 2
PRINT r%
```

## Why it is safe

`IsInlinable` refuses a declaration, a direct self-call, and a callee of more
than 64 IR instructions (8 under `$OPTIMIZE SIZE`, 256 in the extra
`$OPTIMIZE SPEED` rounds). A caller or callee with an armed error handler, and
one containing inline assembly, is never inlined into or out of: the handler's
block address and the asm block's references to its own frame cannot be
cloned. The callee is removed afterwards only by `GlobalDce`, when it has no
remaining caller and its address is not taken.

A procedure can opt out explicitly with the `pb36` **`NOINLINE`** modifier, which
keeps it as its own inspectable code.

## Limits

Directly recursive callees and bodies above the size budget stay calls; the
budget and profile-weighted variants are described in
[O0053](O0053-ir-inliner.md).
