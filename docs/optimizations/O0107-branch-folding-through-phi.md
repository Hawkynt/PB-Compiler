# O0107 — Branch folding through phi

| | |
|---|---|
| **Status** | 🟨 Partial — `SimplifyCfg` threads unconditional predecessors around PHI-only conditional blocks when the incoming `i1` is constant; general expression folding, critical edges and code duplication remain planned |
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
          if v ...              <- decided on both edges
```

## Implemented slice

`SimplifyCfg` now handles the no-duplication form directly. For an unconditional
predecessor `P` of a block `B`, it redirects `P` to the selected successor when:

- `B` consists only of leading phi nodes and an `IrCondBr`;
- the branch condition is one of those phis;
- the condition's incoming value from `P` is a constant `i1`;
- the selected successor is neither `P` nor `B`; and
- every successor phi can be translated without cloning a value defined in `B`.

Successor phis are updated edge-by-edge. If a successor receives one of `B`'s
phis, the new `P` edge receives that phi's incoming value from `P`, preserving
the exact SSA value that would have flowed through the original two-edge path.
The removed `P -> B` incoming entries are then deleted from `B`'s phis and the
existing CFG cleanup collects any newly unreachable/trivial blocks.

This is intentionally conservative. A block containing executable work is not
bypassed, even when that work is pure, because doing so would require proving it
unneeded or cloning/speculating it. Likewise the first slice does not split
critical edges or rewrite predecessor terminators other than a single `IrBr`.

## Applies to

```basic
DIM c%, mode%, r%
IF c% > 0 THEN mode% = 1 ELSE mode% = 2
' ... straight-line code ...
IF mode% THEN r% = 10 ELSE r% = 20
```

When lowering exposes the second condition directly as a boolean phi, each
incoming edge can now bypass its redundant test.

## Still planned

- Fold comparisons and other side-effect-free expressions whose operands become
  constant after substituting predecessor-specific phi inputs.
- Split/redirect critical predecessor edges safely.
- Duplicate a small profitable join block under a code-size budget when the
  deciding expression is not the only executable instruction.
- Integrate profitability/frequency information with [O0106](O0106-trace-formation.md)
  if/when trace formation exists.

The broader forms are conventional jump threading. The implemented slice stays
inside `SimplifyCfg` because it is purely a CFG/phi rewrite and requires no new
path-sensitive value lattice.
