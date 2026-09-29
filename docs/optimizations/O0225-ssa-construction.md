# O0225 — SSA construction (CFG, dominators, phi placement)

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Mid-end infrastructure |
| **Source** | `Ir/IrDominators.cs` (dominator tree, dominance frontiers), `Ir/Passes/Mem2Reg.cs` — `Promote`, `IsPromotable`, `PlacePhis`, `Rename`; the CFG is the IR's own basic blocks built by `Ir/IrLowering.cs` |
| **Gate** | none for construction — `Mem2Reg.RunForFaithfulSelection` runs in the unoptimized `Legalize` pipeline too; `--optimize` uses `Mem2Reg.Run` |
| **Verified by** | `PowerBasic.Compiler.Tests/Ir/Mem2RegTests.cs`, `Ir/IrDominatorsTests.cs`, and, end to end, every pass downstream that requires SSA |
| **Split from** | [O0017](O0017-sccp.md) (which is now the SCCP solve itself) |

## What it is

The substrate every other SSA pass stands on:

1. **The CFG** is explicit in the IR: `IrLowering` turns every control
   construct it lowers into basic blocks and branches, so there is no separate
   graph to build.
2. **`IrDominators`** computes immediate dominators (Cooper-Harvey-Kennedy) and
   dominance frontiers (Cytron).
3. **`Mem2Reg`** places phis at the iterated dominance frontier of each
   promotable slot's stores and renames along a dominator-tree walk. PB
   zero-initializes, so a slot with no reaching store reads as its type's zero
   rather than undef.

## Why it is safe

Only an `alloca` whose every use is a direct load or store of its own storage
type is promoted. A slot whose address escapes (a BYREF argument, a GEP, the
address stored elsewhere), a narrower or wider field view, an MBF cell and a
closure-environment cell stay in memory, as does a global, which is not an
`alloca`. The unoptimized entry additionally keeps a source variable that is
written but never read, as faithful emission requires.

## Consumers

[O0017](O0017-sccp.md) (SCCP), [O0183](O0183-ssa-dead-store.md) (dead stores),
GVN, LICM and every other value-based pass in the IR middle end.
