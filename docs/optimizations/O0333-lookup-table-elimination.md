# O0333 — Lookup-table elimination

| | |
|---|---|
| **Status** | 🟡 Partial — total read-only byte tables are eliminated when all 256 entries are an exact constant, identity, XOR-mask or add-constant formula |
| **Stage** | Mid-end |
| **Source** | `Ir/Passes/LookupTableElimination.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `DataRepresentationOptimizationTests` |
| **Related** | [O0332](O0332-lookup-table-generation.md), [O0174](O0174-target-cost-models.md), [O0004](O0004-strength-reduction.md) |

## The idea

The reverse trade. Where a table's contents are a **simple function of the
index**, recomputing can beat loading — and the table itself disappears from the
image.

## Implemented v1

`LookupTableElimination` considers only read-only 256-byte objects whose every
index is provably in the byte domain. It recognizes four exact total functions:
constant, identity, `index XOR mask`, and wrapping `index + constant`.

Generated `.lut.*` objects are deliberately excluded so O0332 and O0333 do not
immediately undo each other. The table is removed only when every use is an
eligible indexed load and the address does not escape.

## Applies to

```basic
DIM identity?(0 TO 255)
FOR i% = 0 TO 255 : identity?(i%) = i% : NEXT
PRINT identity?(n??)
```

For a byte-bounded `n`, the load can become `n` directly and the table vanishes.

## Still planned

- Richer arithmetic/bitwise formula recovery, including useful polynomial forms.
- Initializer-loop recognition rather than requiring an already-materialized
  constant object.
- Target-aware profitability; some 8086 formulas are slower than a table load,
  while the same formula is effectively free on a modern host.
- Wider tables/domains under an explicit size/cost budget.
