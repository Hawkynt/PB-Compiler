# O0051 — IR: if-conversion

| | |
|---|---|
| **Status** | ✅ Implemented (simple diamonds) |
| **Stage** | IR mid-end |
| **Source** | `Ir/Passes/IfConversion.cs` |
| **Related** | [O0042](O0042-ir-mem2reg.md), [O0052](O0052-ir-simplify-cfg.md) |

## What it is

A simple diamond becomes a branchless `select`. When a block ends in
`condbr c, T, E`, both `T` and `E` are empty (just a branch), and they merge at
a common block `M` whose only predecessors are `T` and `E`, then each phi in `M`
becomes `select c, valueFromT, valueFromE`, the diamond collapses to a straight
edge to `M`, and `T` and `E` are deleted.

Two branches and a join disappear — and `IF c THEN x = a ELSE x = b`, which
mem2reg leaves as exactly this diamond, is the pattern it was built for.

## Sample

```basic
DIM c%, a%, b%, x%
IF c% > 0 THEN x% = a% ELSE x% = b%
PRINT x%
```

## Before

```llvm
  %c0 = icmp sgt i16 %c, 0
  br i1 %c0, label %then, label %else
then:
  br label %join
else:
  br label %join
join:
  %x.0 = phi i16 [ %a, %then ], [ %b, %else ]
```

## After

```llvm
  %c0 = icmp sgt i16 %c, 0
  %x.0 = select i1 %c0, i16 %a, i16 %b
```

## Equivalent BASIC

```basic
x% = IF(c% > 0, a%, b%)      ' pb36 ternary — but branchless
```

## Why it is safe

Both arms must be **empty** — no instruction that could trap, allocate or have
an effect is ever speculated — and `M` must be reachable only through the
diamond, so no other predecessor's phi input is disturbed. `select` evaluates
both operands, which is exactly why the arms have to be value-only.

On the x86-16 back end the corresponding branchless form is `SETcc`/`SBB` (there
is no `CMOV` before the Pentium Pro); on the C and LLVM back ends the `select`
is emitted directly.
