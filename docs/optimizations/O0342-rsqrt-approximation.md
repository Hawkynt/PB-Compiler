# O0342 — Reciprocal square-root approximation

| | |
|---|---|
| **Status** | 🟨 Partial — canonical `1/SQR(x)` now exposes LLVM's current rsqrt contract; hardware rsqrt/refinement remains target-selected |
| **Stage** | IR middle end + target lowering |
| **Gate** | Optimizer + `$OPTIMIZE SPEED` / `-OZF` |
| **IR** | `FpFastMath`: canonical rsqrt gives `sqrt` `contract+afn` and its `FDiv` `arcp+contract+afn`; LLVM receives the complete pattern-specific permission set |
| **Related** | [O0341](O0341-reciprocal-approximation.md), [O0343](O0343-transcendental-specialization.md) |

## What is implemented

For the canonical `1 / sqrt(x)` shape the two operations carry the freedoms a
target optimizer needs to recognize reciprocal-square-root lowering:

- the `sqrt` call is permitted to participate in the contraction and to use an
  approximate implementation (`contract`, `afn`);
- the division is permitted to use a reciprocal, participate in the contraction,
  and use the approximate rsqrt path (`arcp`, `contract`, `afn`).

The extra permissions are shape-specific: ordinary division does not acquire `afn`,
and an unrelated math call does not acquire `contract`. `FpFastMath` also never
manufactures a permission that the selected optimization objective did not grant.

This matches current LLVM rsqrt formation: contraction legality is carried on both
the division and square root, while target lowering decides whether the available
accuracy contract permits a native estimate or requires a refinement sequence. The
target-neutral IR does not pretend that the 16-bit x87 has an rsqrt instruction.

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
bypass PowerBASIC error behavior in strict mode. Without SPEED none of `afn`,
`arcp`, or `contract` is present, so the ordinary `SQR` + division semantics remain
required.
