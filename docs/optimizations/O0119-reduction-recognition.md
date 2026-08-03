# O0119 — General reduction recognition

| | |
|---|---|
| **Status** | ⬜ Planned (the sum shape is recognized for register residency and pointer stepping — [O0005](O0005-register-residency.md), [O0030](O0030-induction-variable-strength-reduction.md)) |
| **Stage** | Mid-end |
| **Related** | [O0005](O0005-register-residency.md), [O0120](O0120-multiple-accumulators.md), [O0145](O0145-vector-reduction.md), [O0073](O0073-algorithmic-idiom-catalog.md) |

## The idea

A **reduction** is a loop-carried variable updated by an associative operation
with the loop's own values: sum, product, minimum, maximum, `AND`, `OR`, `XOR`,
count. Recognizing the pattern *as a reduction* — rather than as an arbitrary
loop-carried dependency — is what unlocks:

- prioritizing the reduction value for a register
  ([O0005](O0005-register-residency.md));
- splitting it into several accumulators
  ([O0120](O0120-multiple-accumulators.md));
- vectorizing it with a horizontal combine at the end
  ([O0145](O0145-vector-reduction.md));
- replacing it with a closed form when the inputs are affine
  ([O0020](O0020-idiom-replacement.md) already does this for the arithmetic
  series).

## Applies to

```basic
DIM i%, s%, m%, f%, a%(0 TO 999)
FOR i% = 0 TO 999
  s% = s% + a%(i%)                          ' sum
  IF a%(i%) > m% THEN m% = a%(i%)           ' maximum
  f% = f% XOR a%(i%)                        ' xor-fold / checksum
NEXT
```

## Today

The sum is recognized by name in `FindAccumulator`; the max and the xor-fold are
ordinary loop-carried variables that happen to be eligible for residency.

## Planned

All three are classified as reductions with an identity element and a combining
operator, and the downstream passes act on that classification rather than on
syntax.

## What it needs

- A recognizer over the SSA loop phi: `phi(init, op(phi, x))` where `op` is one
  of the associative set and `x` is loop-varying.
- The **min/max form is a conditional**, not an operator, so it needs the
  branch shape recognized too (`IF a > m THEN m = a`).
- Associativity is exact for the integer operators PB has (modulo 2ⁿ), which is
  what makes reassociation legal here where it is not for floats.
