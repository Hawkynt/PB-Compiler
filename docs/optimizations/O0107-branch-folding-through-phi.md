# O0107 — Branch folding through phi

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | SSA mid-end |
| **Related** | [O0017](O0017-sccp.md), [O0045](O0045-ir-correlated-value-propagation.md), [O0106](O0106-trace-formation.md) |

## The idea

When a join block branches on a phi whose incoming values are known *per edge*,
the branch's outcome is known per predecessor. Specializing the join for each
incoming edge removes the test entirely on those paths.

```
      x = 1              x = 2
         \                 /
          v = phi(1, 2)
          if v = 1 ...          <- decided on both edges
```

## Applies to

```basic
DIM c%, mode%, r%
IF c% > 0 THEN mode% = 1 ELSE mode% = 2
' ... straight-line code ...
IF mode% = 1 THEN r% = 10 ELSE r% = 20
```

## Today

`mode%` is a phi with two constant inputs; SCCP cannot lower it to a single
constant (the inputs disagree), so the second `IF` is emitted and branched on.

## Planned

The second test is folded on each path, and the two assignments to `r%` move
into the arms of the first `IF`:

```basic
IF c% > 0 THEN
  mode% = 1 : r% = 10
ELSE
  mode% = 2 : r% = 20
END IF
```

## What it needs

- Either **jump threading on the SSA form** (duplicate the join per incoming
  edge when the branch becomes decidable) or the trace/superblock machinery of
  [O0106](O0106-trace-formation.md) — they are the same transformation seen from
  two directions.
- A duplication budget, and phi updates for every successor of the split block.
- It subsumes a common DOS-era idiom: a "mode" flag set once and tested
  repeatedly afterwards.
