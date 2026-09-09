# O0275 — Cold-code outlining

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Mid-end + layout |
| **Related** | [O0105](O0105-hot-cold-splitting.md), [O0367](O0367-exception-handler-outlining.md), [O0402](O0402-layout-aware-outlining.md) |

## The idea

Extract error paths, rare cases and exceptional cleanup **out of** a hot
procedure into a separate cold procedure, so the hot body shrinks — which makes
it cheaper to inline, easier to keep in one cache line or page, and denser to
fetch.

Where [O0105](O0105-hot-cold-splitting.md) *relocates* a block, outlining
*extracts* it into its own callable unit, which is what allows the hot remainder
to be treated as a small function.

## Applies to

```basic
SUB Parse(s$)
  IF LEN(s$) = 0 THEN
    PRINT "empty input"      ' cold: several statements and two literals
    PRINT "usage: ..."
    EXIT SUB
  END IF
  ...                        ' hot
END SUB
```

## Implementation

`ColdCodeOutlining` is a module pass in the SPEED pipeline, immediately before
inlining. It currently targets the extraction shape that is both useful and
fully provable in target-neutral SSA: a single-entry branch arm that never
rejoins the hot side and terminates in `ret` / `unreachable`.

- external SSA values read by the cold region become helper parameters;
- values defined in the region must have no users outside it, so there are no
  live-outs to synthesize;
- the extraction boundary is capped at four live-ins to keep call/argument cost
  bounded;
- address-taken blocks, indirect branches, armed error handlers and inline
  assembly are left alone;
- the generated helper is `NOINLINE`, preventing the following inliner from
  undoing the transform;
- a region must contain at least three IR instructions, because the caller-side
  replacement is a call plus a return.

Without profile metadata the pass only acts on conservative static coldness
signals instead of guessing from block size. An `unreachable`-terminating side
is preferred, a terminal side exit from a loop is cold relative to the
continuing loop path, and the equality outcome of `x = 0` / `ptr = NULL` is
considered unlikely (with `<>` making the false edge the unlikely one), matching
the public branch-probability heuristic used by LLVM. If neither side has such a
signal, the branch is left unchanged.

This is intentionally separate from [O0402](O0402-layout-aware-outlining.md):
O0275 supplies the safe extraction mechanics and a target-neutral cost floor;
O0402 can later replace the static decision with cache-line/page/segment-aware
layout costs.

## What it needs

- A cost model that counts the *hot* body's size, not the procedure's
  ([O0402](O0402-layout-aware-outlining.md)). The current implementation uses a
  conservative IR-instruction floor until target layout information is available.
- Live-value analysis at the extraction boundary: implemented for live-ins; the
  outlined fragment is required to have no live-outs, which is exactly the
  dead-end (`EXIT SUB`, `END`, error) case this optimization is aimed at first.
