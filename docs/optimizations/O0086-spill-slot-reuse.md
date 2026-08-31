# O0086 — Spill-slot reuse

| | |
|---|---|
| **Status** | 🟨 Partial — direct-emitter CSE temporaries reuse physical 4-byte frame slots across proven-dead top-level runs; within-run coloring, argument staging and backend spill slots remain planned |
| **Stage** | Frame layout |
| **Related** | [O0003](O0003-common-subexpression-elimination.md), [O0019](O0019-zero-elision.md), [O0065](O0065-dead-frame-store-elimination.md) |

## The idea

Compiler temporaries — CSE slots, argument staging cells, spill locations — need
not each own a permanent frame cell. Temporaries whose live ranges provably do
not overlap can share one slot, shrinking the frame, shortening the prologue's
zero fill ([O0019](O0019-zero-elision.md)) and keeping more accesses inside the
short `[BP-disp8]` addressing form.

## Implemented slice

The direct emitter's CSE analysis already has an exact conservative lifetime
boundary: a hard barrier clears the whole live-expression cache. At a top-level
barrier no CSE value from the preceding run can be reloaded afterwards, so the
next independent run may restart physical slot numbering at zero.

Thus two CSE pairs separated by a call can occupy one physical 4-byte frame
cell even though they are different expressions. `SlotCount` is the maximum
simultaneous slot demand of any such run rather than the sum of every structural
CSE key seen in the procedure.

The allocator deliberately does **not** recycle a cell merely because a scalar
write invalidated one expression inside a still-live run. Nor does a barrier in
a nested inherited `IF`/`SELECT` analysis reset physical slots: an outer
dominating value can still be used by a sibling path. Those cases keep monotonic
slot assignment until a whole top-level run is proven dead.

## Example

```basic
DECLARE SUB Barrier

a% = x% * 7
b% = x% * 7               ' CSE slot 0
Barrier                    ' hard lifetime boundary
c% = y% * 9
d% = y% * 9               ' reuses physical slot 0
```

Before this slice the two structural CSE keys reserved two 4-byte cells for the
whole procedure. After it they reserve one.

## Still planned

- true define-to-last-use intervals for safe coloring **within** one straight-line
  run;
- sharing argument-staging cells and backend allocator spill locations;
- wider packing across control-flow regions once interference/liveness is
  explicit rather than inferred from the CSE cache;
- measuring the secondary [O0019](O0019-zero-elision.md) win from the smaller
  frame and its shorter initialization.

The implemented rule intentionally uses the CSE analysis's existing lifetime
proof instead of inventing a parallel alias/control-flow model.
