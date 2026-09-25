# O0022 — Dead procedure elimination

| | |
|---|---|
| **Status** | ✅ Implemented (transitive, when the module owns every procedure's callers) |
| **Stage** | IR module pass, after the middle end |
| **Source** | `Ir/Passes/GlobalDce.cs` — `Run`, `FarEntryTargets`; called from `Ir/Passes/IrMiddleEndPipeline.cs` — `RunNativeModule`; `CodeGen/CodeGenerator.Backend.cs` does not emit the procedures it removed |
| **Gate** | `--optimize`; only when `IrModule.OwnsProcedureAbi` (no unit compile, nothing linked) |
| **Related** | [O0006](O0006-inlining.md), [O0023](O0023-dead-global-elimination.md), [O0054](O0054-ir-global-dce.md), [P0001](P0001-runtime-trimming.md) |

## What it is

A whole program's entry point is its top-level code (the synthetic "main").
After the middle end has run, `GlobalDce` removes every other function that
nothing references any more — no call, no taken address (`CODEPTR`/`CALL
DWORD`), and no far-entry thunk naming it, which is how a delegate reaches a
lambda. The code generator then leaves out every procedure the pass removed.
This also takes procedures inlined into every caller.

It is **transitive**: removing a dead function drops its body's uses, so a
procedure reached only from other dead procedures loses its last user and goes
too, to a fixpoint; an entire unused subsystem disappears. The hosted C/LLVM
path runs the same pass.

## Sample

```basic
SUB Used
  PRINT "used"
END SUB

SUB Unused
  CALL AlsoUnused
END SUB

SUB AlsoUnused
  PRINT "never"
END SUB

CALL Used
```

## Without the optimizer

All three procedure bodies are emitted, including `"never"` in the string pool:

```
Procedures
  0A12  Used
  0A34  Unused
  0A56  AlsoUnused
```

## With the optimizer

```
Procedures
  0A12  Used
```

`Unused` is unreachable from main; `AlsoUnused` is reached only from `Unused`,
so the closure drops it too.

## Equivalent BASIC

```basic
SUB Used
  PRINT "used"
END SUB

CALL Used
```

## Why it is safe

- It runs only when the module owns every procedure's callers
  (`IrModule.OwnsProcedureAbi`): not for a `$COMPILE UNIT`, whose procedures
  are exported, and not when a linked foreign object could call one by name.
- Soundness rests on the IR's use lists: every reference to a function — a
  call, an address taken as a value, an argument — is a recorded use, so no
  reference kind can be forgotten. The one reference that is not an operand, a
  far-entry thunk's target, is collected separately (`FarEntryTargets`).
- Unoptimized and `pb35` output is unchanged (gated on `Optimize`).

## Limits

The test is "has no users", not reachability from main, so dead procedures that
call each other (or a dead procedure that calls itself) keep one another alive.
A procedure kept alive only by a `CODEPTR` stored into a never-read global goes
only if the function pipeline has already removed that store (see
[O0023](O0023-dead-global-elimination.md)).
