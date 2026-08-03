# O0040 — Identical-code folding (link-time tail merge)

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Assembler, after the peephole / jump threading, before fixup resolution |
| **Source** | `Asm/Assembler.TailMerge.cs` |
| **Gate** | `--optimize` + `$OPTIMIZE SIZE` (off by default) |
| **Related** | [O0022](O0022-dead-procedure-elimination.md), [O0011](O0011-literal-overlap-pooling.md), [P0006](P0006-header-squeeze.md) |

## What it is

Two procedures that compile to the **same bytes** need only one copy. Regions
recorded via `BeginFoldRegion`/`EndFoldRegion` are compared, and each duplicate's
entry label is re-bound to the survivor while its bytes are removed from the
image.

This is the classic identical-code-folding size win, and it shows up more often
than one would expect in generated code: monomorphized generic instantiations,
`pb36` property accessors, and near-duplicate handlers frequently emit
byte-identical bodies.

## Sample

```basic
$OPTIMIZE SIZE

SUB ClearA
  PRINT "clear"
END SUB

SUB ClearB
  PRINT "clear"
END SUB

CALL ClearA
CALL ClearB
```

## Without folding

```
Procedures
  0A12  ClearA        ; 14 bytes
  0A20  ClearB        ; the same 14 bytes
```

## With folding

```
Procedures
  0A12  ClearA        ; 14 bytes
                      ; ClearB is bound to 0A12
```

```asm
    call    0A12h      ; CALL ClearA
    call    0A12h      ; CALL ClearB — same address
```

## Equivalent BASIC

```basic
SUB Clear
  PRINT "clear"
END SUB
CALL Clear
CALL Clear
```

## Why it is safe

Congruence is exact, not heuristic. Two regions fold only when:

- their **raw bytes** match (fixup placeholder bytes are zeros before
  resolution, so raw comparison is meaningful), **and**
- their **fixups** match position for position — internal targets normalized to
  region-relative offsets, external targets compared by label identity;
- nothing outside the duplicate references any label bound inside it except the
  entry label.

It runs after the peephole and jump threading (regions are tracked by labels, so
earlier cuts shifted them correctly) and before fixups are resolved.
