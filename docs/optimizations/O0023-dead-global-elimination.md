# O0023 — Dead global / data tree-shaking

| | |
|---|---|
| **Status** | ✅ Implemented (simple scalar module globals, self-contained main) |
| **Stage** | Whole-program analysis, before emission |
| **Source** | `CodeGen/OptDeadGlobals.cs` |
| **Gate** | `--optimize`, no unit compile, nothing linked |
| **Related** | [O0022](O0022-dead-procedure-elimination.md), [O0002](O0002-dead-code-elimination.md), [P0003](P0003-bss.md) |

## What it is

The DATA dimension of the tree-shaker. A fully-owned simple scalar module global
that no reachable code ever **reads** is dead: its data slot contributes
nothing, and so does every pure store to it (`global = <pure rhs>`).

References are classified soundly — a global occurrence is a **read** unless it
is exactly a top-level `AssignStmt` whose target is `NameExpr(global)`. Every
other form keeps it live: `INCR`/`SWAP`, an array index, a BYREF argument,
`VARPTR`/`VARSEG`, a dotted-name `MemberExpr`, any operand position.

Because a `CODEPTR(P)` that appears **only** as the RHS of a store to a dead
global is not a live edge to `P`, the dead-global set, the dead-store set and
the live-procedure set are solved together to a **fixpoint**: a procedure kept
alive only by a never-read function pointer cascades to dead as well.

## Sample

```basic
DIM SHARED counter%     ' read below -> live
DIM debugFlag%          ' only ever written -> dead
DIM hook&               ' holds a CODEPTR nothing calls -> dead

SUB Handler
  PRINT "handler"
END SUB

debugFlag% = 1
hook& = CODEPTR32(Handler)
counter% = counter% + 1
PRINT counter%
```

## Without the optimizer

```
Data
  3C98    2  counter
  3C9A    2  debugFlag
  3C9C    4  hook
Procedures
  0A12  Handler
```

plus the two stores in the code stream.

## With the optimizer

```
Data
  3C98    2  counter
```

`debugFlag%` and `hook&` lose their slots and their stores; `hook&` was the only
reference to `Handler`, so the cascade takes the procedure body too
([O0022](O0022-dead-procedure-elimination.md)).

## Equivalent BASIC

```basic
DIM SHARED counter%
counter% = counter% + 1
PRINT counter%
```

## Why it is safe

Hard conservative guards keep a global no matter what the read analysis says:

- its address is taken (`VARPTR`/`VARSEG`/`STRPTR`/… and the `32` variants);
- it is `SHARED`, `COMMON` or exported;
- it is an array, UDT, string, BCD or FIX value, or declared `DIM … AT`;
- it is a PB internal cell;
- a store's RHS could **trap** — a function call, or (under `$ERROR
  NUMERIC/OVERFLOW/BOUNDS`) arithmetic or an array read, since dropping the
  store would skip the Error 6/9 the program is observed to raise.

The pass is restricted to a self-contained main, so `pb35` and unoptimized
output stay byte-identical.
