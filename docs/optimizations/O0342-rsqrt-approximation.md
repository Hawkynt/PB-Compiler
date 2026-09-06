# O0342 — Reciprocal square-root approximation

| | |
|---|---|
| **Status** | 🟨 Partial — `1/SQR(x)` exposes reciprocal + approximate-function legality; hardware rsqrt/refinement is target-selected |
| **Stage** | IR middle end + target lowering |
| **Gate** | Optimizer + `$OPTIMIZE SPEED` / `-OZF` |
| **IR** | `FpFastMath`: `sqrt` gets `afn`, the enclosing `FDiv` gets `arcp`; LLVM receives both permissions |
| **Related** | [O0341](O0341-reciprocal-approximation.md), [O0343](O0343-transcendental-specialization.md) |

## What is implemented

For the canonical `1 / sqrt(x)` shape the two operations carry the freedoms a
target optimizer needs to recognize reciprocal-square-root lowering:

- the `sqrt` call is permitted to use an approximate implementation (`afn`);
- the division is permitted to use a reciprocal (`arcp`).

LLVM therefore sees the complete relaxed contract and may select an rsqrt
estimate/refinement sequence when the target has one. The target-neutral IR does
not pretend that the 16-bit x87 has such an instruction.

```basic
DIM x!, y!, len!, nx!, ny!
len! = SQR(x! * x! + y! * y!)
nx! = x! / len!
ny! = y! / len!
```

Repeated division by the same computed length can additionally be reduced by
[O0345](O0345-common-denominator-factoring.md).

## Boundary

This pass does not manufacture a target-specific `rsqrt` intrinsic, nor does it
bypass PowerBASIC error behavior in strict mode. Without SPEED neither `afn` nor
`arcp` is present, so the ordinary `SQR` + division semantics remain required.
