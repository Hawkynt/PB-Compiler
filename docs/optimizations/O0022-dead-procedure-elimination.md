# O0022 — Dead procedure elimination

| | |
|---|---|
| **Status** | ✅ Implemented (transitive, fully-owned procedures) |
| **Stage** | Whole-program reachability, before emission |
| **Source** | `CodeGen/OptReachability.cs` |
| **Gate** | `--optimize`; only for *fully-owned* procedures |
| **IR** | ✅ `Ir/Passes/GlobalDce.cs` — a function with no users that is not `@main` is removed, to a fixpoint, so a callee freed by deleting its last caller goes too. Run from `pbc/Driver.cs` on the `--emit-c` / `--emit-llvm` path. It deliberately does NOT run in the hybrid x86 pipeline: there the IR module is not the whole program (anything unrouted is still emitted by the direct path), so removing a function only stops it being routed — measured, that cost six corpus comparisons and saved nothing |
| **Related** | [O0006](O0006-inlining.md), [O0023](O0023-dead-global-elimination.md), [O0054](O0054-ir-global-dce.md), [P0001](P0001-runtime-trimming.md) |

## What it is

A whole program's entry point is its top-level code (the synthetic "main").
Tracing the call graph from there — direct calls, `CODEPTR`/`CALL DWORD`
references recorded in `CallBindings`, and lambdas — marks every procedure that
can actually run. Everything else is not emitted.

It is **transitive**: a procedure reached only from other dead procedures is
itself dead, so an entire unused subsystem disappears in one closure.

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

- Only **fully-owned** procedures are dropped (`IsFullyOwned`: a nested
  procedure, or any procedure in a self-contained main). An exported
  `$COMPILE UNIT` entry point, or a procedure a linked foreign object could call
  by name, is kept regardless of what the local call graph says.
- Soundness rests on `OptReachability.DescendantNodes` visiting every statement
  and expression **by reflection**, so it is complete by construction — no
  reference kind can be forgotten as the AST grows.
- Unoptimized and `pb35` output is unchanged (gated on `Optimize`).

## Limits

A procedure kept alive only by a `CODEPTR` stored into a never-read global is
handled together with [O0023](O0023-dead-global-elimination.md): the dead-global
set, the dead-store set and the live-procedure set are solved to a common
fixpoint.
