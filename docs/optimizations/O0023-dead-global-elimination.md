# O0023 — Dead global / data tree-shaking

| | |
|---|---|
| **Status** | 🟡 Partial — the native build drops a global only when no emitted code references it; `GlobalDce`'s global sweep runs only on the C/LLVM path |
| **Stage** | IR middle end (module passes) + data layout at emission |
| **Source** | `Ir/Passes/LocalizeGlobals.cs` (a write-first global used by one procedure becomes a local), then `Mem2Reg`/`Dce`; `CodeGen/CodeGenerator.cs` — `SlotOf`, `EmitDataArea` (a data slot exists only once code references it); `Ir/Passes/GlobalDce.cs` with `removeGlobals` (C/LLVM path only) |
| **Gate** | `--optimize` |
| **Related** | [O0022](O0022-dead-procedure-elimination.md), [O0002](O0002-dead-code-elimination.md), [P0003](P0003-bss.md) |

## What it is

The DATA dimension of the tree-shaker. A module global that no code ever
**reads** is dead: its data slot contributes nothing, and so does every pure
store to it.

On the native build there is no dedicated pass for this. The data area is laid
out from the bound program, but a variable's slot is created only when emitted
code first references it (`SlotOf`), so a global whose every access the IR
removed gets no slot. The IR removes such accesses mainly through
`LocalizeGlobals`: a scalar global touched by one non-reentrant procedure, with
only plain loads and stores, whose entry block stores it before any load,
becomes a local; `Mem2Reg` then promotes it and `Dce` drops the stores nothing
reads. `GlobalDce`'s sweep of globals with no users is switched off on the
native build (`removeGlobals: false`), because IR globals are resolved there by
name; the C/LLVM path runs it.

There is no common fixpoint with [O0022](O0022-dead-procedure-elimination.md):
a procedure kept alive only by a function pointer stored into a dead global
goes only when that store has been removed before `GlobalDce` runs.

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
([O0022](O0022-dead-procedure-elimination.md)). This is the intended result; on
the native build it holds only where the stores are removed as described above.

## Equivalent BASIC

```basic
DIM SHARED counter%
counter% = counter% + 1
PRINT counter%
```

## Why it is safe

A global keeps its slot and its stores while any emitted instruction still
references it, so nothing is dropped on a guess. `LocalizeGlobals` declines:

- a global whose address is used other than by a direct load or store (an
  address handed to a call, stored, or indexed into);
- a runtime (`rt_`) cell, an array or any other multi-slot global;
- a global used by more than one procedure, or by a recursive, error-handling
  or inline-assembly procedure;
- a global its procedure may read before writing, since a global keeps its value
  between calls and a local does not.

Removing a store removes only the store; a right-hand side that could trap is
still evaluated. Unoptimized, none of these passes run.
