# O0232 — Procedure entry alignment

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | x86 back end (emission) |
| **Source** | `CodeGen/CodeGenerator.Backend.cs` — `EmitBackendFunction`; `CodeGen/CodeGenerator.BackendGenerated.cs` — `EmitBackendGeneratedFunctions`; `CodeGen/CodeGenerator.SemanticMerge.cs` |
| **Gate** | `--optimize` + `$CPU 80486`/`80586` |
| **Split from** | [O0041](O0041-branch-layout.md) |

## What it is

Procedure entry points are aligned to a 16-byte boundary. Because an entry is
reached only by `CALL`, the pad in front of it **never executes** — it is pure
layout, with no run-time cost at all.

Each routed source procedure, middle-end generated helper and O0284 merge helper
is preceded by `AlignCode(16)` when the optimizer is on and the CPU level is 486
or higher.

## Sample

```basic
$CPU 80486
SUB Work
  PRINT "x"
END SUB
```

## With the optimizer

```
  0A10   nop nop nop nop     ; pad, never executed
  0A20   Work:               ; entry on a 16-byte boundary
```

## Why it is safe

Nothing falls through into a procedure entry, so the padding cannot be reached;
the label is bound after the pad, so every `CALL` targets the aligned address.

## Limits

Aligning *every* procedure is a size cost with no benefit for cold ones —
choosing by profile weight is
[O0380](O0380-selective-function-alignment.md).
