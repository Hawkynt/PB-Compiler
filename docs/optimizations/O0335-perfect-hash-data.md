# O0335 — Perfect-hash generation for static key sets

| | |
|---|---|
| **Status** | 🟡 Partial — unique unsorted static integer searches become verified `IrSwitch` dispatch and reuse the existing target perfect-hash selector where profitable |
| **Stage** | Mid-end + target dispatch |
| **Source** | `Ir/Passes/StaticSearchRecognition.cs`, existing `IrSwitch` target dispatch |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` |
| **Verified by** | `StaticDispatchOptimizationTests` |
| **Related** | [O0100](O0100-perfect-hash-dispatch.md), [O0334](O0334-binary-search-recognition.md), [O0336](O0336-fsm-compilation.md) |

## The idea

A fixed set of keys admits a compact compile-time dispatch strategy. Where
[O0100](O0100-perfect-hash-dispatch.md) applies this to control flow, this entry
starts from a data-search loop and canonicalizes it into the same dispatch form.

## Implemented v1

For a canonical counted search over a unique read-only 8/16-bit integer table,
`StaticSearchRecognition` replaces the loop with an `IrSwitch` when the data is
not a sorted binary-search candidate. Each case returns the original table
index; the original failure block remains the mandatory default verification
path.

The existing target switch lowering is then free to choose its current
perfect-hash implementation, jump table, mask or decision tree according to the
set and target. No gperf code or generated implementation was copied.

## Applies to

```basic
DATA 17, 2, 91, 4, 33
FOR i% = 0 TO 4
  IF keys?(i%) = key?? THEN EXIT FOR
NEXT
```

when the table is a unique constant integer set and the loop has no other side
effects.

## Still planned

- String keys such as keywords, command names and extensions.
- Record lookup where the matched key selects a data record rather than merely
  returning the original index.
- Dedicated data-perfect-hash cost/search logic independent of control-flow
  switch lowering.
- More general verification/fallback shapes and non-canonical search loops.
