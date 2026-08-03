# O0054 — IR: global dead-code elimination

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR mid-end, module level |
| **Source** | `Ir/Passes/GlobalDce.cs` |
| **Related** | [O0022](O0022-dead-procedure-elimination.md), [O0023](O0023-dead-global-elimination.md), [O0053](O0053-ir-inliner.md) |

## What it is

The module-level counterpart of per-function DCE (LLVM's `globaldce`): functions
and global variables that **nothing references** are removed.

A function is dead when it has no users — no `call`, no taken address — and is
not the program entry `@main`. Clearing a dead function's body drops its
callees' and globals' uses, so removal **cascades to a fixpoint**: a function
that becomes unreferenced once its only caller was deleted (or inlined) is
removed in the next round. Globals are swept after the functions, since a
global's users are instructions.

## Sample

```basic
FUNCTION Helper&(BYVAL v&)
  Helper& = v& + 1
END FUNCTION

SUB Unused
  PRINT Helper&(1)
END SUB

PRINT "main only"
```

## Before

```llvm
@msg = private constant [10 x i8] c"main only"
define i32 @Helper(i32 %v) { ... }
define void @Unused() { %0 = call i32 @Helper(i32 1) ... }
define void @main() { ... }
```

## After

```llvm
@msg = private constant [10 x i8] c"main only"
define void @main() { ... }
```

`Unused` has no users; deleting it drops the only `call` to `Helper`, which then
has none either.

## Equivalent BASIC

```basic
PRINT "main only"
```

## Why it is safe

The user lists are maintained by the IR itself, so "no users" is a fact, not an
estimate. `@main` is pinned as the entry, and a function whose address is taken
has a user by construction. Bodies are cleared before deletion so the cascade
sees accurate use counts at every round.
