# O0359 — Verified arithmetic lowering

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Offline tooling → emitter |
| **Related** | [O0056](O0056-reciprocal-division.md), [O0078](O0078-multiply-decomposition.md), [O0355](O0355-superoptimized-peepholes.md) |

## The idea

Constant multiply, divide and modulo sequences are exactly the place where a
clever lowering is most tempting and most dangerous: the magic-number reciprocal
([O0056](O0056-reciprocal-division.md)), the shift/add chain
([O0078](O0078-multiply-decomposition.md)), the bias-and-shift signed divide
([O0190](O0190-divide-power-of-two.md)).

Each is only correct for a specific input range and rounding rule. Rather than
argue it by hand, **prove** it — by exhaustive check over the 16-bit domain
(which is 65 536 cases, entirely feasible) or by an SMT query for the 32-bit
one — against PB's exact semantics: truncation toward zero, dividend-signed
remainder, and the dialect's wrap behaviour.

## What it needs

- A machine-checkable statement of PB's arithmetic semantics — which is valuable
  on its own, since it is currently expressed as code plus `docs/QUIRKS.md`.
- A generator that emits the verified (multiplier, shift, bias) triples as a
  table the emitter consults.
- Integration with the differential harness: a verified lowering still has to
  produce byte-identical *program output*, which is a stronger claim than
  arithmetic equivalence when `$ERROR` traps are involved.
