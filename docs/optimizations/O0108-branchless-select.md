# O0108 — Branchless select, min/max and absolute value

| | |
|---|---|
| **Status** | 🟡 Partial — the branchless **abs** ([O0249](O0249-branchless-abs.md)), integer/LONG **min/max** ([O0248](O0248-branchless-minmax.md)) and now integer **sign** (`SGN`, `cwd`/`neg`/`adc`, no branch and no x87) all ship; the general `IF c THEN x = k1 ELSE x = k2` **select** (via `SETcc`/`CMOVcc` under the [O0174](O0174-target-cost-models.md) cost model) remains |
| **Stage** | Emitter |
| **Related** | [O0051](O0051-ir-if-conversion.md), [O0088](O0088-boolean-materialization-sbb.md), [C0001](C0001-386-codegen.md) |
| **Split into** | [O0248](O0248-branchless-minmax.md), [O0249](O0249-branchless-abs.md) |

## Now

`SGN` over an INTEGER argument emits the branchless sign `cwd; neg ax; adc dx,dx;
mov ax,dx` — `cwd` puts the sign mask (0 / -1) in `DX`, `neg` sets the carry iff the
value is non-zero, and `adc dx,dx` forms `2·mask + carry` = -1 / 0 / +1. Four
instructions, no branch, no x87 round-trip (the old path coerced to `DOUBLE` and
`FTST`/`FSTSW`/`SAHF`-branched, or sign-tested `DX:AX` with three branches). Exact
for every int16 including MININT. Optimize-gated, so the faithful build keeps the
branching form (golden gate 250/250); battery-verified over positive, negative, zero
and a MININT variable, self-diffed under DOSBox with the optimizer on and off.

## The idea

A short, data-dependent branch is the worst kind on any pipelined target. Three
idioms replace it:

| Source | Branchless form |
|---|---|
| `IF c THEN x = a ELSE x = b` | `CMOVcc` (686+), or an `SBB` mask blend |
| `IF a > b THEN m = a ELSE m = b` | `CMP`/`CMOVcc`, or packed `PMAXSW` in a vector loop |
| `IF x < 0 THEN x = -x` | `CWD` / `XOR` / `SUB` — the classic three-instruction absolute value |

The IR tier already forms `select` for the diamond
([O0051](O0051-ir-if-conversion.md)); what is missing is the x86-16 selection
that turns it into flag arithmetic rather than back into a branch.

## Applies to

```basic
DIM a%, b%, m%, x%
IF a% > b% THEN m% = a% ELSE m% = b%
IF x% < 0 THEN x% = -x%
```

## Today

```asm
    mov     ax, [a]
    cmp     ax, [b]
    jle     UseB
    jmp     Have
UseB:
    mov     ax, [b]
Have:
    mov     [m], ax
```

## Planned (8086, no `CMOV`)

```asm
    mov     ax, [x]          ; absolute value, branchless
    cwd
    xor     ax, dx
    sub     ax, dx
    mov     [x], ax
```

## What it needs

- A **profitability rule**. Branchless is not automatically better: on an 8086 a
  not-taken branch is nearly free, so the mask arithmetic only wins when the
  branch is unpredictable or the arms are tiny. This is a cost-model decision
  ([O0174](O0174-target-cost-models.md)).
- `CMOVcc` needs a `$CPU 80686` gate — DOSBox does not execute it, so it would
  be encoding-verified only.
- The arms must be side-effect-free, since both are evaluated.
