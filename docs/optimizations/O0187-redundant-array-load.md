# O0187 — Redundant array-element load caching

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR middle end |
| **Source** | `Ir/Passes/Gvn.cs` (element address and load numbered by Memory SSA version, `Ir/Analysis/IrMemorySsa.cs`); `Ir/Passes/RedundantMemory.cs` for the block-local case |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF69.BAS` |
| **Split from** | [O0003](O0003-common-subexpression-elimination.md) |

## What it is

A repeated array-element read `a%(i%)` with no intervening write reuses the
first read's value instead of re-reading memory. In the IR an element read is an
address computation (`gep`) followed by a load: `Gvn` numbers the two address
computations as one value, and numbers the two loads as one when Memory SSA
shows both see the same memory version. `RedundantMemory` does the same within
a block, including forwarding a value just stored to the element.

## Sample

```basic
DIM a%(0 TO 99), i%, m%
IF a%(i%) > m% THEN m% = a%(i%)      ' the element is read twice
```

## With the optimizer

The element is read **once** — which, together with
[O0188](O0188-cse-if-condition.md) registering the condition and
[O0034](O0034-redundant-load-elimination.md) dropping the reload, is what makes
the max-scan idiom hand-quality.

## Why it is safe

A store that may alias the element, or a call that may write memory, gives the
second load a different memory version, so it stays. A write to an index
variable changes the address operand, so the two addresses are no longer
congruent. A store the alias analysis proves disjoint — a **different** array,
for instance — does not interrupt the reuse.
