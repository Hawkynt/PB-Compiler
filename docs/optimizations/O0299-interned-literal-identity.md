# O0299 — Interned literal identity comparison

| | |
|---|---|
| **Status** | ✅ Subsumed — the only reachable case constant-folds, and that is now pinned (`LiteralStringComparisonFoldTests`); no dedicated pass is wanted |
| **Stage** | Emitter |
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
- Realistically narrow: the honest value here is as a fast path inside
  [O0298](O0298-string-compare-length-guard.md), not as a general rewrite.

## Why there is nothing to build

Measured 2026-08-06, and two things between them close the entry.

**The worked example above does not qualify.** `m$ = %Mode` copies the pool bytes
into a DYNAMIC string — a different allocation with the same contents — so
comparing addresses would be wrong, exactly as "what it needs" warns. The example
illustrates the idea and is not a case of it.

**The case that does qualify never reaches a comparison.** Two literals are folded
by the constant folder, and the operands then die with the fold: a `--dialect pb36`
image of `IF "fast" = "fast" THEN …` does not contain the bytes `fast` anywhere.
The same text reaching the same comparison through `READ`/`DATA` *is* in its image,
which is what makes that a measurement rather than a coincidence — both halves are
asserted in `LiteralStringComparisonFoldTests`, along with the folded answers for
`=`, `<>` and the ordering forms, and the dynamic-copy counter-example.

So the effect O0299 wanted is already had, by [O0001](O0001-constant-folding.md)
rather than by an identity check. What remains of the idea is the narrow fast path
inside [O0298](O0298-string-compare-length-guard.md) that this page already
identifies as its honest value — and that one is a *runtime* question about equal
handles, not a compile-time identity of pool references.
