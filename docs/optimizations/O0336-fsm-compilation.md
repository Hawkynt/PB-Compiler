# O0336 — Finite-state-machine compilation

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0029](O0029-select-jump-table.md), [O0154](O0154-swar-search.md), [O0335](O0335-perfect-hash-data.md) |

## The idea

Character-classification chains — `IF c >= "0" AND c <= "9" THEN … ELSEIF c = " "
THEN …` — are a state machine written as branches. Compiled into a **table**
(one entry per byte value, giving the class or the next state) the whole chain
becomes one indexed load, and the classification can then be vectorized
([O0154](O0154-swar-search.md)).

Parsers, tokenizers and input validators in the corpus are full of these chains.

## Applies to

```basic
DIM c$, i%
FOR i% = 1 TO LEN(s$)
  c$ = MID$(s$, i%, 1)
  IF c$ >= "0" AND c$ <= "9" THEN
    ...
  ELSEIF c$ = " " OR c$ = CHR$(9) THEN
    ...
  END IF
NEXT
```

## What it needs

- Recognition of a **classification chain**: a run of mutually exclusive tests on
  one byte-valued expression, with no side effects in the conditions.
- Table generation (256 bytes — cheap) and the equivalence proof that the table
  answers exactly what the chain did for every byte value, including the ones no
  branch matched.
- Multi-state machines need the arms to update a state variable in a recognizable
  way; single-classification is the tractable first step.
