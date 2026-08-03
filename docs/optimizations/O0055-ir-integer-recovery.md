# O0055 — IR: integer recovery

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR mid-end |
| **Source** | `Ir/Passes/IntegerRecovery.cs` |
| **Related** | [O0013](O0013-promotion-lowering.md) (the x86 equivalent), [O0043](O0043-ir-instcombine.md) |

## What it is

PowerBASIC computes integral `+`, `-` and `*` **in floating point** — that is why
`PRINT A% * B%` shows `9E+8`. The front end therefore lowers those statements to
`sitofp` / `fadd` / `fmul` / `fptosi` chains, which the integer-oriented back
ends cannot select well.

This pass recovers the integer form: a value stored back into an integer
(`fptosi(float-tree) to iN`), where the float tree is built **only** from
`sitofp(iN)` leaves, integer-valued float constants and `fadd`/`fsub`/`fmul`, is
rewritten to the integer tree `add`/`sub`/`mul` over the same `iN` values.

## Sample

```basic
DIM a%, b%, c%
c% = a% * 2 + b% * 3
```

## Before

```llvm
  %0 = sitofp i16 %a to double
  %1 = fmul double %0, 2.0
  %2 = sitofp i16 %b to double
  %3 = fmul double %2, 3.0
  %4 = fadd double %1, %3
  %5 = fptosi double %4 to i16
  store i16 %5, ptr @c
```

## After

```llvm
  %0 = mul i16 %a, 2
  %1 = mul i16 %b, 3
  %2 = add i16 %0, %1
  store i16 %2, ptr @c
```

## Equivalent BASIC

Unchanged — the *stored* value is identical; only the observable-through-PRINT
wide form would differ, which is why the rewrite is restricted to trees whose
result is stored into an integer.

## Why it is safe

The result stored is taken mod 2ᴺ either way, and modular arithmetic commutes
with the intermediate wrapping:

```
(a*2 + b*3) mod 2ᴺ  ==  ((a*2 mod 2ᴺ) + (b*3 mod 2ᴺ)) mod 2ᴺ
```

— exactly the argument the direct x86 codegen uses for the same statements
([O0013](O0013-promotion-lowering.md)). A tree containing anything else (a
division, a non-integral constant, a call) is left alone.

Giving such functions a genuine integer IR is what lets the in-house x86-16 back
end select them at all: it handles integers, not FP operations plus conversions.
