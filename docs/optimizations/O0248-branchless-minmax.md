# O0248 — Branchless min/max

| | |
|---|---|
| **Status** | 🟡 Partial — the `MIN`/`MAX`/`MIN%`/`MAX%` intrinsics with all-INTEGER arguments fold with a signed integer `CMP` (of any arity), and a hand-written `IF a > b THEN m = a ELSE m = b` diamond is recognized and folded to the same code; the true branchless (`CMOV`/mask) forms remain |
| **Stage** | Emitter |
| **Related** | [O0108](O0108-branchless-select.md), [O0119](O0119-reduction-recognition.md), [O0257](O0257-vector-minmax.md) |
| **Split from** | [O0108](O0108-branchless-select.md) |

## The idea

`IF a > b THEN m = a ELSE m = b` is a min/max, and every target has a cheaper
form than a branch: `CMP` + `CMOVcc` on a 686, `PMAXSW`/`PMINSW` in a vector
loop, and on an 8086 a mask built from the carry flag.

Recognizing min/max **by name** matters more than the generic select, because it
is the shape a reduction loop carries
([O0119](O0119-reduction-recognition.md)) and the one the packed instructions
implement directly.

## Applies to

```basic
DIM a%, b%, m%
IF a% > b% THEN m% = a% ELSE m% = b%
```

## Now

The `MIN`/`MAX`/`MIN%`/`MAX%` intrinsics, when every argument and the result are
`INTEGER`, fold with a signed integer compare rather than the x87 path they used
to take (each argument coerced to `DOUBLE`, `FCOM`/`FSTSW`/`SAHF`, the result
coerced back). The accumulator stays in `AX`; each further argument is compared in
`BX` and a `CMP`/`JGE`(max)/`JLE`(min) keeps the winner — any arity, ties keeping
the earlier accumulator exactly as the strict `Ja`/`Jb` FPU fold did. Optimize-gated,
so the faithful build keeps the x87 fold byte-for-byte (golden gate 250/250); the
optimization battery folds `MAX%`/`MIN%` over positives, negatives and a tie and
self-diffs under DOSBox with the optimizer on and off (identical output). This is
the integer reduction shape [O0119](O0119-reduction-recognition.md) carries, now
off the FPU.

The **hand-written diamond** folds too: `IF a REL b THEN m = a ELSE m = b`
(`>`, `>=`, `<`, `<=`, either arm order, a constant in place of an operand for the
clamp form `IF a >= -9 THEN m = a ELSE m = -9`) is recognized in `EmitIf` and
lowered to exactly the intrinsic's integer `CMP`/keep — one store, no re-evaluated
arm. The operands must be pure (a variable read or a constant), since the branch
form evaluates the taken operand a second time in its assignment and the fold once;
a call operand keeps the branch (a regression test pins that the diamond then no
longer matches the intrinsic image). A numeric tie is a non-issue — the two operands
hold the same value, so either choice stores it. Verified byte-identical to the
`MAX%` intrinsic and self-diffed under DOSBox (`dmax`/`dmin`/`dclamp`, optimizer on
and off) in the battery.

## Still planned

- The `SELECT`/ternary spellings of the same idiom.
- The **true branchless** forms: `CMOVcc` on a 686 (encoding-verified only — DOSBox
  has no `CMOV`) and the 8086 carry-mask blend, chosen by the
  [O0174](O0174-target-cost-models.md) cost model (`PreferBranchless`), which keeps
  the compare-and-branch on the predictor-less early parts where it already wins.
- The `LONG`/`SINGLE`/`DOUBLE` argument forms, and packed `PMAXSW`/`PMINSW` in a
  vector loop ([O0257](O0257-vector-minmax.md)).

## What it needs

- A recognizer over the `IF`/`SELECT`/ternary spellings of the same idiom.
- Profitability: on an 8086 a predictable branch beats mask arithmetic, so the
  cost model decides ([O0174](O0174-target-cost-models.md)).
- Signed versus unsigned selection, and the `-32768` edge for the negated forms.
