# O0349 — x87 value retention across expressions

| | |
|---|---|
| **Status** | 🟨 Partial — private single-use TBYTE values are retained; general multi-use/loop residency remains planned |
| **Stage** | Machine IR, after selection and before scheduling/allocation |
| **Gate** | Optimizer (`MachineOptimizationState`) |
| **Source** | `Backend/X87StackOptimizer.cs` |
| **Related** | [C0003](C0003-x87-scheduling.md), [O0348](O0348-x87-stack-scheduling.md), [O0005](O0005-register-residency.md) |

## What is implemented

A private TBYTE temporary with exactly one writer and one reader can disappear
when its consumer can use the already-resident x87 value. The simplest case is
literal adjacency:

```text
FSTP tbyte tmp
FLD  tbyte tmp
```

which is no operation at all when `tmp` has no other reference. O0348 extends
the same retention through a proven-safe right arithmetic subtree.

The reference census is function-wide, not merely block-local. A second reader
keeps the materialization, so the pass does not duplicate a value or silently
change which consumer observes it.

## Precision and barriers

Only TBYTE temporaries qualify. F32/F64 stores are semantic rounding boundaries
and remain. Retention does not cross calls, inline assembly, terminators,
clobbers or an x87 operation whose stack effect the pass does not explicitly
model.

The machine scheduler orders every pair of x87 users against each other, so the
ordinary dependency scheduler cannot subsequently interleave the retained x87
sequence incorrectly.

## Remaining scope

This does not yet keep a multiply-used value resident across arbitrary
statements or loop iterations. Such residency needs a real x87 depth allocator,
path-aware flush placement and interaction with every call/error/asm boundary.
The implemented subset captures the common single-use expression-tree spills
without assuming that broader machinery exists.
