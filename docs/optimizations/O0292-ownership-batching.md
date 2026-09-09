# O0292 — Ownership operation batching

| | |
|---|---|
| **Status** | ✅ Implemented (straight-line repeats + non-empty counted loops) |
| **Stage** | Mid-end |
| **IR** | `PowerBasic.Compiler/Ir/Passes/OwnershipBatching.cs` |
| **Related** | [O0291](O0291-handle-ownership-elision.md), [O0290](O0290-loop-temporary-reuse.md), [O0028](O0028-loop-invariant-code-motion.md) |

## The idea

Where a dup/free pair cannot be removed, it can often be **moved out of a
loop**: acquire once before, release once after, instead of per iteration.
Repeated operations on the same handle within a straight-line run likewise
collapse to one.

## Applies to

```basic
DIM i%, s$, t$
FOR i% = 1 TO 1000
  t$ = s$                    ' dup + free every iteration, same value each time
  PRINT LEFT$(t$, i%)
NEXT
```

## IR implementation

PowerBASIC's dynamic-string protocol makes this stricter than ordinary ARC. `rt_str_dup` is a real
heap copy, not a retain-count increment, and many string runtime entries consume the owned handle they
receive. The pass therefore works from explicit SSA ownership shapes rather than treating every pair of
calls with the right names as movable.

Two cases are implemented:

- **Straight-line repeated assignment.** For `dup(s) ... dup(s); free(first-copy)`, when the first copy
  has no intervening observer and the later owner is used only by ordinary ownership plumbing, the
  first copy remains the owner and the second allocation/free pair disappears. A different source or
  any read of the intermediate owner stops the rewrite.
- **Counted-loop batching.** A pointer phi whose latch value is `rt_str_dup(invariant)` and whose only
  in-loop use of the old owner is the matching `rt_str_free` is converted to one duplicate and one
  release in the preheader. The existing `CountedLoop` matcher supplies the compile-time non-zero trip
  proof. The pass additionally requires a pure header, makes the assignment execute at the body entry
  before observable work, rejects every side exit, rejects consuming uses of the source, and requires
  the incoming owner to have no other live use that moving its release could invalidate.

The non-zero requirement is intentional. On a zero-trip loop the incoming `t$` remains the value of
`t$`; freeing it in the preheader would corrupt that path. Likewise, `EXIT FOR`, `GOTO` out of the
region, `RETURN`, an armed error handler, or inline assembly leaves the optimization disabled until a
future lifetime analysis can place balanced cleanup on every edge.

## What it needs

- The counted-loop / invariance machinery used by [O0028](O0028-loop-invariant-code-motion.md), with a
  stronger ownership-specific proof rather than generic call speculation.
- Exact ownership balance. A moved release must not shorten a live handle's lifetime, and a batched
  duplicate may not be handed to a consumer more than once.
- Conservative escape behavior. The implemented patterns keep raw heap-handle identity unobservable;
  anything outside the normal dup/free plumbing is left alone.
