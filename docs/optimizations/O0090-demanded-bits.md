# O0090 — Demanded bits and truncation pushed into the producer

| | |
|---|---|
| **Status** | 🟡 Partial — discarded high-bit AND/OR/XOR work eliminated under `$OPTIMIZE SPEED` |
| **Stage** | IR mid-end |
| **Implementation** | `PowerBasic.Compiler/Ir/Passes/DemandedBits.cs` |
| **Related** | [O0016](O0016-value-fact-analysis.md), [O0057](O0057-storage-narrowing.md), [O0089](O0089-extension-elimination.md) |

## The idea

Compute only the bits that consumers actually observe. If a result is
immediately narrowed, high bits that cannot reach the consumer were computed for
nothing. The IR can erase that work before any target-specific instruction
choice is made.

This is the dual of [O0016](O0016-value-fact-analysis.md)'s known-bits domain:
that one propagates facts *forward* from operands, this one propagates demand
*backward* from consumers.

## Implemented

`DemandedBits` currently recognizes a truncation to N bits and removes a directly
feeding bit operation when the constant operand cannot change any of those N
bits:

- `trunc(and x, C)` when the low N bits of `C` are all one;
- `trunc(or x, C)` when the low N bits of `C` are all zero;
- `trunc(xor x, C)` when the low N bits of `C` are all zero;
- either operand order for the commutative operation.

It runs inside the SSA fixpoint pipeline for `$OPTIMIZE SPEED`, so the now-dead
wide operation is collected by the ordinary DCE pass and the selector never sees
it. The transform is target-neutral: DOS x86, C and LLVM backends all receive the
same smaller graph.

## Example

```basic
DIM a&, c AS BYTE
c = (a& OR &H7FFF0000) AND 255
```

At the relevant SSA boundary, the high-bit OR cannot affect the stored byte and
is removed. There is no compensating machine instruction — this is literal
zero-overhead abstraction erasure.

## Still planned

The full backward demanded-bits dataflow remains broader than this first local
slice:

- propagate demanded widths through `+ - *`, shifts, phis and narrowing stores;
- narrow producers when low N result bits depend only on low N operand bits;
- combine the demand with O0016 known bits so masks and extensions disappear even
  when the proof is not a literal adjacent constant;
- stop propagation at division, comparison, calls and any other wide observation;
- let each target cost model decide whether a semantically narrower operation is
  actually cheaper — see [O0057](O0057-storage-narrowing.md).
