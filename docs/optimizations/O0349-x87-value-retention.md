# O0349 — x87 value retention across expressions

| | |
|---|---|
| **Status** | ✅ Implemented — private TBYTE values can remain resident across expressions, CFG edges and loop backedges when the x87 stack state is proven consistent |
| **Stage** | Machine IR, after selection and before scheduling/allocation |
| **Gate** | Optimizer (`MachineOptimizationState`) |
| **Source** | `Backend/X87StackOptimizer.cs` |
| **Related** | [C0003](C0003-x87-scheduling.md), [O0348](O0348-x87-stack-scheduling.md), [O0005](O0005-register-residency.md) |

## What is implemented

Selection deliberately materializes floating SSA results in private TBYTE frame
cells. O0349 recognizes cells whose complete machine-IR reference set consists
of one `FSTP` definition plus `FLD` readers and can keep the defined value on
the x87 stack instead of writing and re-reading that cell.

For a multiply-used value the defining `FSTP` disappears. Each reader becomes
an x87 register-stack duplicate:

```text
FLD ST(i)
```

where `i` is proved from the number of transient expression values above the
resident value at that exact point. Intel defines `FLD ST(i)` as pushing a copy
of that x87 stack register, so each consumer receives its own top-of-stack copy
while the original remains available for later consumers.

The existing single-use cases remain: an adjacent private
`FSTP tbyte tmp / FLD tbyte tmp` pair disappears completely, and O0348 can keep
a completed left subtree below a proven-safe right subtree.

## Control-flow residency

Residency is not guessed from layout. The pass builds the machine CFG and a
block-dominator relation. A value may cross an edge only when:

- its defining block dominates every block in the residency region;
- every incoming executable edge to a resident block carries the same resident
  value;
- every outgoing edge either carries the resident value or all outgoing edges
  leave the region together;
- the transient x87 expression stack is empty at every crossed block edge.

A mixed loop branch therefore keeps the value resident on both the backedge and
the exit edge, then discards it in the dominated exit block. This permits a
loop-invariant value defined in a preheader to remain on the x87 stack across
iterations without giving the loop header different stack layouts on different
predecessors.

Several non-overlapping residency regions may be retained in one function.
Overlapping candidates are left materialized rather than trying to allocate two
independent persistent x87 values through the same region; that conservative
choice keeps the stack-state proof local and deterministic.

## Depth, precision and barriers

The hardware stack has eight entries. Every modeled x87 instruction carries a
minimum transient-depth requirement as well as a stack delta. The pass rejects
a candidate if a consumer would address below the transient expression, if any
push could exceed eight entries, or if a block edge would be reached with a
transient value still live.

Only TBYTE temporaries qualify. F32/F64 stores remain semantic rounding
boundaries and are never removed or crossed by the expression-tree retention
proof. Calls, user inline assembly, explicit clobber windows, terminators inside
an unfinished expression, and x87 operations whose stack behavior is not
explicitly modeled stop residency.

The machine IR has no first-class `ST(i)` operand. The generated duplicate uses
the backend's existing canonical inline-assembly node for `FLD ST(i)`, with an
empty integer-register effect. `MachineScheduler` recognizes exactly that
canonical no-name form as an x87-stack user, so ordinary scheduling may still
move unrelated integer work around it but may not reorder it against another
x87 operation. Emission then goes through the existing `TextAssembler`/`Assembler`
path, which already encodes `FLD ST(i)`.

## References

- Intel® 64 and IA-32 Architectures Software Developer’s Manual, `FLD` —
  `FLD ST(i)` pushes a copy of the selected x87 register; `FLD ST(0)` duplicates
  the current stack top.
- LLVM `X86FloatingPoint.cpp` — used as a behavioral/design reference for the
  requirement that x87 liveness and stack positions agree exactly across CFG
  edges. No LLVM implementation code is copied.
