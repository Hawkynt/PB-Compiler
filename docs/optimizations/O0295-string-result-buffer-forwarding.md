# O0295 — String result-buffer forwarding

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Whole-program + emitter |
| **Related** | [O0102](O0102-return-value-forwarding.md), [O0286](O0286-allocation-elimination.md), [O0282](O0282-internal-calling-convention.md) |

## The idea

A string-returning `FUNCTION` builds its result in a fresh allocation, returns
the handle, and the caller assigns it — freeing whatever was there. If the caller
instead **provides the destination**, the callee writes straight into it: one
allocation instead of two, and no handoff.

This is the classic named-return-value optimization, applied to PB's string
protocol.

## Applies to

```basic
FUNCTION Pad$(s$, BYVAL n%)
  Pad$ = s$ + SPACE$(n% - LEN(s$))
END FUNCTION

DIM dst$
dst$ = Pad$(src$, 40)        ' the callee could have built it in dst$
```

## What it needs

- An internal convention that passes the destination
  ([O0282](O0282-internal-calling-convention.md)), with the ownership proof that
  the compiler sees every call site.
- The destination's **old value** must be freed at exactly the right moment —
  before the callee starts writing, but only once the arguments have been read
  (they may alias it).
