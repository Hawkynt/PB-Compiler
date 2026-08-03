# O0044 — IR: sparse conditional constant propagation

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR mid-end |
| **Source** | `Ir/Passes/Sccp.cs` |
| **Related** | [O0017](O0017-sccp.md) (the AST-tier equivalent), [O0052](O0052-ir-simplify-cfg.md) |

## What it is

The Wegman-Zadeck algorithm on the IR: the constant lattice and CFG reachability
are solved **together**, so a value is proven constant only along edges that can
actually execute, and a branch on a proven-constant condition kills the untaken
arm. After the solve, constant instructions are replaced by their values,
constant conditional branches become unconditional, and the blocks that became
unreachable are deleted.

This is strictly more powerful than running constant folding alone, because it
sees through phis and dead control flow.

## Sample

```basic
DIM mode%, r%
mode% = 0
IF mode% = 1 THEN r% = 10 ELSE r% = 20
PRINT r%
```

## Before

```llvm
  %0 = icmp eq i16 0, 1
  br i1 %0, label %then, label %else
then:
  br label %join
else:
  br label %join
join:
  %r.0 = phi i16 [ 10, %then ], [ 20, %else ]
  call void @rt_print_i16(i16 %r.0)
```

## After

```llvm
  call void @rt_print_i16(i16 20)
```

The `%then` edge is never marked executable, so the phi has one live input.

## Equivalent BASIC

```basic
PRINT 20
```

## Why it is safe

The lattice is monotone (⊤ → constant → ⊥ only), so the solve terminates, and a
value is lowered to a constant only when *every executable* reaching definition
agrees. Blocks are deleted only after being proven unreachable by the same
solve, so no live path loses code.
