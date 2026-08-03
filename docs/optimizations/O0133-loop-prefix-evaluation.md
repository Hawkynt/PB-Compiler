# O0133 — Loop prefix evaluation

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0132](O0132-compile-time-loop-evaluation.md), [O0115](O0115-loop-peeling.md), [O0025](O0025-pure-function-folding.md) |

## The idea

A loop that cannot be fully evaluated at compile time — because a later
iteration reads something unknown — can often have its **first N iterations**
evaluated anyway. The compiler emits the resulting state and starts the runtime
loop from iteration N.

It is loop peeling ([O0115](O0115-loop-peeling.md)) where the peeled iterations
are not merely simplified but **executed**.

## Applies to

```basic
DIM i%, acc&, n%
acc& = 1
FOR i% = 1 TO n%             ' n% is unknown...
  acc& = acc& * i%
NEXT
```

## Today

Every iteration runs, including the first few whose values are fully determined.

## Planned

```basic
' iterations 1..4 evaluated at compile time
IF n% <= 4 THEN
  acc& = <the exact value for n%>   ' a small table or a switch
ELSE
  acc& = 24                          ' state after i% = 4
  FOR i% = 5 TO n%
    acc& = acc& * i%
  NEXT
END IF
```

## What it needs

- The evaluator from [O0025](O0025-pure-function-folding.md) plus a **state
  snapshot** mechanism: what the peeled prefix leaves in each variable.
- A guard for the case where the real trip count is **shorter** than the
  evaluated prefix — that is what the `IF n% <= 4` branch above is for, and
  getting it wrong changes the program's output.
- A budget, since the prefix is unrolled code plus data.
