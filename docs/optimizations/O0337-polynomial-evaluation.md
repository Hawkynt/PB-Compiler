# O0337 — Horner / Estrin polynomial evaluation

| | |
|---|---|
| **Status** | 🟡 Partial — profitable one-variable integer polynomials are rewritten to Horner form; Estrin and floating-point reassociation remain planned |
| **Stage** | Mid-end |
| **Source** | `Ir/Passes/PolynomialEvaluation.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `ArithmeticIdiomOptimizationTests` |
| **Related** | [O0061](O0061-reassociation.md), [O0121](O0121-reduction-tree-balancing.md), [O0343](O0343-transcendental-specialization.md) |

## The idea

`a*x^3 + b*x^2 + c*x + d` evaluated literally repeats powers and multiplies.
Horner's form — `((a*x + b)*x + c)*x + d` — minimizes multiplies in a compact
serial chain, while Estrin can expose more instruction-level parallelism on
wider targets.

## Implemented v1

`PolynomialEvaluation` recognizes integer-only expression trees made from
constants, one variable, `+`, `-` and `*`, up to degree eight. It reconstructs
the coefficient vector modulo the integer bit width and emits Horner form only
when the original tree uses more multiplications than the polynomial degree.

Because integer arithmetic here is exact modulo 2^n, this does not need a
fast-math contract. Floating-point expressions are deliberately rejected.

## Applies to

```basic
y& = x& * x& * x& + 3 * x& * x& + 5 * x& + 7
```

when the IR exposes the repeated literal powers as ordinary integer multiply/add
trees.

## Still planned

- Estrin decomposition using target latency/throughput information.
- Recognition of `^`/power-helper forms.
- Floating-point Horner/Estrin behind explicit reassociation/fast-math semantics.
- Higher degrees when a cost model justifies the compile-time analysis and code
  growth.
