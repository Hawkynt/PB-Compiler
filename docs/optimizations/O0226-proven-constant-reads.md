# O0226 — Cross-block proven-constant reads

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Expressions.cs` — `TryEmitProvenConstant` |
| **Gate** | `--optimize`; **disabled** under `$ERROR OVERFLOW/NUMERIC` |
| **Verified by** | `tests/diff/DIFF48.BAS`, `DIFF49.BAS` |
| **Split from** | [O0017](O0017-sccp.md) |

## What it is

The emitter folds each read that [O0017](O0017-sccp.md) proved constant —
constant propagation **across blocks**, which the local folder
([O0001](O0001-constant-folding.md)) cannot do because it sees one expression at
a time.

## Sample

```basic
DIM k%, r%
k% = 7
IF flag% THEN PRINT "x"
r% = k% * 2                  ' k% is 7 here, across the branch
```

## With the optimizer

```asm
    mov     ax, 000Eh        ; the read folded, then the multiply folded
    mov     [r], ax
```

## Why it is safe

Every stored value is wrapped to its variable's type, so a proven constant is
the exact value the program computes. Folding is **disabled under `$ERROR
OVERFLOW/NUMERIC`**: a folded constant would skip the runtime trap the real
arithmetic must still raise, which would change observable behavior.
