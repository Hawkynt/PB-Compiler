# O0006 — Procedure inlining

| | |
|---|---|
| **Status** | ✅ Implemented (small leaf procedures; trivial TYPE methods and properties) |
| **Stage** | Pre-emission analysis + emitter |
| **IR** | ✅ `Ir/Passes/Inliner.cs`, run by `CodeGenerator.BackendProcs` after the pass sweep and followed by another - the point of inlining is not the call overhead but that the callee body becomes visible to the caller's optimizer, and nothing sees it until the passes run again |
| **Source** | `CodeGen/OptInlining.cs`, `CodeGen/CodeGenerator.Procs.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF30.BAS` (mixed eligible/ineligible callees, side-effecting and nested arguments) |
| **Related** | [O0018](O0018-interprocedural-constant-propagation.md), [O0021](O0021-register-parameters.md), [O0022](O0022-dead-procedure-elimination.md), [O0053](O0053-ir-inliner.md) |
| **Split into** | [O0200](O0200-trivial-method-inlining.md), [O0201](O0201-inlined-procedure-purge.md) |

## What it is

A small **leaf** `SUB`/`FUNCTION` is emitted as its body at every call site: the
frame setup, the `CALL`, the `RET` and the argument push/pop traffic all
disappear.

**This page covers the leaf-procedure inline itself**; inlining trivial TYPE
methods ([O0200](O0200-trivial-method-inlining.md)) and purging a
fully-inlined procedure ([O0201](O0201-inlined-procedure-purge.md)) are separate
entries.

Mechanics:

- `BYVAL` scalar arguments evaluate **once** into fresh per-inline frame temps,
  preserving evaluation order and side effects;
- every read and write of a parameter, body local or the result variable is
  remapped onto those temps, so two inlinings — or a self-mutating `BYVAL`
  parameter at two call sites — never collide;
- body locals start zeroed exactly like a real frame;
- a `FUNCTION`'s result is the value left in the result temp; the trivial
  single-result-assignment `FUNCTION` is a fast path that emits the expression
  straight into the registers with no result temp at all.

The same machinery inlines **trivial TYPE methods and properties** in `pb36`:
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

The gate is deliberately conservative: BASIC calling convention only, not
`STATIC`, no `ON ERROR`, no closure/capture, `BYVAL` scalar parameters only, and
a body of at most a few plain scalar assignments and `LOCAL` declarations — no
calls, loops, labels, `GOTO`/`GOSUB`/`RETURN`, `EXIT`, `SELECT` or nested
procedures. The reachability purge additionally requires a self-contained main
and bails the moment a procedure's address is taken (`CODEPTR`) or the program
uses any error handling. Anything uncertain falls back to the genuine call, so
the output stays byte-identical.

A procedure can opt out explicitly with the `pb36` **`NOINLINE`** modifier, which
keeps it as its own inspectable code.

## Limits

Recursive and non-leaf callees, and inlining above a size budget, live in the IR
mid-end's inliner ([O0053](O0053-ir-inliner.md)) for the C/LLVM back ends.
