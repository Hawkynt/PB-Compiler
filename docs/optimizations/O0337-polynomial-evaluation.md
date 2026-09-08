# O0337 — Horner / Estrin polynomial evaluation

| | |
|---|---|
| **Status** | ✅ Implemented — profitable one-variable integer polynomials choose between Horner and Estrin plans |
| **Stage** | Mid-end |
| **Source** | `Ir/Passes/PolynomialEvaluation.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `ArithmeticIdiomOptimizationTests` |
| **Related** | [O0061](O0061-reassociation.md), [O0121](O0121-reduction-tree-balancing.md), [O0174](O0174-target-cost-models.md), [O0344](O0344-fp-reassociation.md) |

## The idea

`a*x^3 + b*x^2 + c*x + d` evaluated literally repeats powers and multiplies.
Horner's form — `((a*x + b)*x + c)*x + d` — minimizes the serial multiply
chain, while Estrin groups coefficients around shared powers such as `x^2` and
`x^4` to expose more instruction-level parallelism.

## IR implementation

`PolynomialEvaluation` recognizes integer expression DAGs made from constants,
one variable, `+`, `-` and `*`, up to degree eight. It reconstructs the
coefficient vector modulo the integer bit width, then builds both candidate
plans before mutating the IR:

- Horner evaluation folds zero/one coefficients while constructing the serial
  form. In particular, a monic polynomial starts from `x` rather than emitting
  a redundant `1*x`.
- Estrin evaluation repeatedly pairs adjacent coefficients and evaluates the
  reduced polynomial in `x^2`, then `x^4`, and so on. Those powers are shared
  plan nodes, so the emitted IR is a DAG rather than duplicated power trees.
- Estrin replaces Horner only when it uses no more multiplications, no more
  arithmetic operations, and has a strictly shorter multiplication-dependency
  depth. This keeps the target-neutral pass from paying speculative extra work
  for parallelism before [O0174](O0174-target-cost-models.md) can justify it.

Profitability is based on what the old expression can actually release. The
pass computes the fixed point of arithmetic nodes whose every user is also
being removed after the polynomial root is replaced. A shared `x*x` with an
outside user therefore remains live and is not counted as a saved
multiplication. The selected plan must use strictly fewer multiplications than
that removable set.

Integer addition and multiplication are exact modulo `2^n` in this IR, so these
reassociations preserve every bit pattern without a fast-math contract.

## Applies to

```basic
y& = x& * x& * x& + 3 * x& * x& + 5 * x& + 7
```

when the IR exposes the literal powers as ordinary integer multiply/add trees.
The monic cubic above becomes the equivalent Horner chain
`((x + 3) * x + 5) * x + 7`.

Higher-degree shapes can instead select Estrin when its shared powers shorten
the multiply dependency chain without increasing arithmetic work.

## Floating point

Strict floating-point expressions remain untouched because reassociation changes
rounding. The IR now has an explicit fast-math contract; SPEED-mode floating
reassociation and balanced evaluation are handled by
[O0344](O0344-fp-reassociation.md). O0337 deliberately does not duplicate that
policy or its flag propagation.

## Still planned

- Target latency/throughput costing that can deliberately spend an extra
  operation when the target proves the shorter dependency chain profitable.
- Recognition of `^`/power-helper forms before they expand into ordinary
  multiplication trees.
- Higher degrees when a cost model justifies the additional compile-time
  analysis and possible code growth.
