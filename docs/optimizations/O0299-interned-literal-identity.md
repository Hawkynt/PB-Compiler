# O0299 — Interned literal identity comparison

| | |
|---|---|
| **Status** | ✅ Implemented/subsumed — literal comparisons fold completely; the IR fold now short-circuits identical interned globals by identity before any byte comparison |
| **Stage** | IR middle-end / emitter constant folding |
| **IR** | ✅ `Ir/Passes/StringConstantFold.cs` — two literal producers backed by the same interned `IrGlobalVariable` are known equal immediately; different literal globals still fold by content |
| **Related** | [O0011](O0011-literal-overlap-pooling.md), [O0298](O0298-string-compare-length-guard.md) |

## The idea

The literal pool is deduplicated and packed
([O0011](O0011-literal-overlap-pooling.md)), so two occurrences of the same
literal have the same address. Comparing **two literal-backed values** is then an
address-and-length comparison rather than a byte comparison.

The useful case is a comparison against a `CONST` string or a `DATA` item that
the folder has already resolved to a pool entry.

## Applies to

```basic
%Mode = "fast"               ' equate, pooled
DIM m$
m$ = %Mode
IF m$ = %Mode THEN ...       ' both sides are the same pool entry
```

## What it needs

- The comparison must be provably between **canonical** pool references — a
  value copied into a dynamic string is a different allocation with the same
  bytes, and comparing addresses there would be wrong.
- Overlap packing means a literal is an (offset, length) pair rather than a
  unique object, so identity is "same offset and same length", not "same
  pointer".
- Realistically narrow: the honest runtime value here is as a fast path inside
  [O0298](O0298-string-compare-length-guard.md), not as a general rewrite.

## What the IR middle-end does

`IrModule.AddStringConstant` interns identical literal bytes to one
`IrGlobalVariable`. `StringConstantFold` already recognizes the two
`rt_str_const` producers around a literal-vs-literal comparison and removes the
whole runtime operation. O0299 is now explicit inside that fold: when both
producers name the same canonical global, the ordering is known to be zero from
identity alone, so the compiler does not scan the literal bytes even while
constant-folding it. If the globals differ, the existing bytewise constant fold
still decides the comparison.

That distinction matters because pointer identity is only a proof in the
canonical-literal case. It is not a replacement for content comparison of
ordinary string handles.

## Why there is no runtime identity pass

Measured 2026-08-06, and two things between them close the runtime form of the
entry.

**The worked example above does not qualify.** `m$ = %Mode` copies the pool bytes
into a DYNAMIC string — a different allocation with the same contents — so
comparing addresses would be wrong, exactly as "what it needs" warns. The example
illustrates the idea and is not a case of it.

**The case that does qualify never reaches a runtime comparison.** Two literals
are folded by the constant folder, and the operands then die with the fold: a
`--dialect pb36` image of `IF "fast" = "fast" THEN …` does not contain the bytes
`fast` anywhere. The same text reaching the same comparison through `READ`/`DATA`
*is* in its image, which is what makes that a measurement rather than a
coincidence — both halves are asserted in `LiteralStringComparisonFoldTests`,
along with the folded answers for `=`, `<>` and the ordering forms, and the
dynamic-copy counter-example.

So the target-code effect O0299 wanted is still subsumed by constant folding:
there is no byte compare left to replace with an address compare. The IR now also
uses the interned identity as the cheapest proof while performing that fold.
What remains of the runtime idea is the narrow fast path inside
[O0298](O0298-string-compare-length-guard.md), where equal handles can prove
equality but unequal handles cannot prove inequality.
