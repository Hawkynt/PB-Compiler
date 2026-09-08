# O0333 — Lookup-table elimination

| | |
|---|---|
| **Status** | 🟡 Partial — total read-only byte tables are eliminated when all 256 entries are an exact constant, identity, AND-mask, OR-mask, XOR-mask or add-constant formula |
| **Stage** | Mid-end |
| **Source** | `Ir/Passes/LookupTableElimination.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `DataRepresentationOptimizationTests`, `LookupTableEliminationTests` |
| **Related** | [O0332](O0332-lookup-table-generation.md), [O0174](O0174-target-cost-models.md), [O0004](O0004-strength-reduction.md) |

## The idea

The reverse trade. Where a table's contents are a **simple function of the
index**, recomputing can beat loading — and the table itself disappears from the
image.

## Implemented v1

`LookupTableElimination` considers only read-only 256-byte objects whose every
index is provably in the byte domain. It recognizes six exact total functions:
constant, identity, `index AND mask`, `index OR mask`, `index XOR mask`, and
wrapping `index + constant`.

The bitwise-mask candidates are derived from values fixed by their respective
operations (`table[255]` for AND and `table[0]` for OR), then verified against all
256 entries before any rewrite is allowed. A near match therefore remains a
table rather than being reconstructed from a heuristic sample.

Generated `.lut.*` objects are deliberately excluded so O0332 and O0333 do not
immediately undo each other. The table is removed only after a whole-object proof
has accounted for every use as an eligible indexed load and the address does not
escape; the rewrite therefore does not have a partial-mutation refusal path.

## Applies to

```basic
DIM identity?(0 TO 255)
FOR i% = 0 TO 255 : identity?(i%) = i% : NEXT
PRINT identity?(n??)
```

For a byte-bounded `n`, the load can become `n` directly and the table vanishes.

Likewise, a table containing `index AND &H5A` or `index OR &HA5` can become one
bitwise IR operation and release the 256-byte object.

## Still planned

- Richer arithmetic/bitwise formula recovery, including useful polynomial forms.
- Initializer-loop recognition rather than requiring an already-materialized
  constant object.
- Target-aware profitability; some 8086 formulas are slower than a table load,
  while the same formula is effectively free on a modern host.
- Wider tables/domains under an explicit size/cost budget.
