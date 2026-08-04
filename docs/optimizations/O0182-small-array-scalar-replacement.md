# O0182 — Small local array scalar replacement

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **IR** | ✅ `Ir/Passes/ScalarReplaceArrays.cs` — in `IrPassManager.Standard()` after SCCP (a subscript is only constant once the index arithmetic has folded) and before the value passes; verified by `IrPassObservableEquivalenceTests` |
| **Related** | [O0059](O0059-scalar-replacement.md), [O0036](O0036-constant-subscript-folding.md), [O0002](O0002-dead-code-elimination.md) |

## The idea

A tiny, non-escaping local array whose subscripts are all compile-time constants
is not really an array: it is N independent variables that happen to share a
name. Replacing it with scalars makes every element eligible for constant
propagation, dead-store elimination and register residency — none of which apply
to array elements today.

This is [O0059](O0059-scalar-replacement.md) applied to arrays instead of
`TYPE`s, and it is the easier of the two because the "fields" are uniform.

## Applies to

```basic
SUB Blend
  LOCAL w%(0 TO 3), i%, s%
  w%(0) = 1 : w%(1) = 3 : w%(2) = 3 : w%(3) = 1
  s% = w%(0) + w%(1) + w%(2) + w%(3)
  PRINT s%
END SUB
```

## Today

Four stores to memory, four loads back, plus the array's allocation and zero
fill — for a value that is a compile-time constant.

## Planned

The four elements become scalars, SCCP folds them, the stores die, and the whole
body is `PRINT 8`.

## What it needs

- **Non-escaping** and **all-constant-subscript** conditions: any variable index,
  `VARPTR`, BYREF pass, `ERASE`, `REDIM`, `ARRAY` statement or inline-asm
  reference disqualifies the array outright.
- A size bound (a handful of elements), since the replacement creates one
  variable per element.
- It composes with [O0036](O0036-constant-subscript-folding.md), which already
  makes such an access a bare displacement — this goes one step further and
  removes the memory entirely.
