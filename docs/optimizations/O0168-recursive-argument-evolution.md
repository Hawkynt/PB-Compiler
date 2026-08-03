# O0168 — Recursive argument evolution analysis

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Whole-program analysis |
| **Related** | [O0167](O0167-tail-call-fact-propagation.md), [O0110](O0110-general-induction-variables.md), [O0134](O0134-recurrence-shortening.md) |

## The idea

A recursive procedure's parameters usually follow a **recurrence**: `n - 1`,
`acc + n`, `ptr + stride`, `lo`/`mid+1` in a binary search. Recognizing the
evolution gives the same facts a loop's induction variables give:

- a **range** for the parameter across all recursive activations;
- a **depth bound**, which turns a recursion into a bounded loop and makes
  [O0132](O0132-compile-time-loop-evaluation.md) applicable;
- an **induction-variable** treatment for a pointer parameter, so array walks
  step rather than recompute ([O0110](O0110-general-induction-variables.md)).

## Applies to

```basic
FUNCTION Fact&(BYVAL n%)
  IF n% <= 1 THEN
    Fact& = 1
  ELSE
    Fact& = n% * Fact&(n% - 1)
  END IF
END FUNCTION
```

## Today

`OptPureFold` interprets this call **only when `n%` is a literal**; with a
variable argument nothing is known about the recursion at all.

## Planned

`n%` is recognized as decreasing by 1 with a base case at 1, giving a depth bound
of `n%` and a range of `[1, n%]` for every activation — which lets the checks
inside the body be dropped and the recursion be converted to a loop.

## What it needs

- Matching the recursive call's arguments against the parameters of the current
  activation, which is a small pattern match once the call graph is available.
- A **termination** argument for the recurrence (monotone toward the base case),
  which is also the fact [O0161](O0161-function-summaries.md) wants to record.
- The wrap check, as everywhere: `n - 1` stops being monotone once it wraps.
