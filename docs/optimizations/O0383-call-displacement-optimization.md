# O0383 — Call displacement optimization

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0361](O0361-weighted-call-graph-clustering.md), [O0386](O0386-caller-callee-colocation.md), [O0384](O0384-branch-island-minimization.md) |

## The idea

Place callers and callees so that direct calls use the **compact encoding**. On
x86-16 a near `CALL rel16` is 3 bytes while a far `CALL seg:off` is 5 — so
keeping a caller and its callee in the same segment is worth real bytes on every
call site, and is a hard requirement for the single-segment model this compiler
targets.

On architectures with a limited call range the same placement decision is a
correctness matter, not only a size one
([O0384](O0384-branch-island-minimization.md)).

## What it needs

- Call weights ([O0361](O0361-weighted-call-graph-clustering.md)) so the bytes
  are spent where the calls are.
- Awareness of the segment structure in the image writer — which for the current
  single-combined-segment layout means every call is already near, and the
  optimization only becomes real once multi-segment images exist
  (`docs/ROADMAP.md`).
