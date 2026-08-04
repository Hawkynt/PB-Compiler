# O0225 — SSA construction (CFG, dominators, phi placement)

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Mid-end infrastructure |
| **Source** | `CodeGen/Ssa/ControlFlowGraph.cs`, `DominatorTree.cs`, `SsaForm.cs`, `ScalarLiveness.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `PowerBasic.Compiler.Tests/CodeGen/SsaTests.cs` (CFG, dominators/frontiers, renaming) |
| **IR** | ✅ `Ir/IrDominators.cs` + `Ir/Passes/Mem2Reg.cs` — the same Cytron construction on the IR: dominance frontiers place the phis, a dominator-tree walk renames. PB zero-initializes, so a slot with no reaching store reads as its type's zero rather than undef. Verified by `Mem2RegTests` and, end to end, by every pass downstream that requires SSA |
| **Split from** | [O0017](O0017-sccp.md) (which is now the SCCP solve itself) |

## What it is

The substrate every other SSA pass stands on:

1. **`ControlFlowGraph`** builds basic blocks over straight-line code,
   `IF`/`ELSEIF`/`ELSE`, `FOR`, and pre-test or infinite `DO-WHILE`/`UNTIL`
   loops (back edge to the header, so loop phis form), with
   `EXIT SUB/FUNCTION/FOR/DO` and `END` as region exits and `EXIT`/`ITERATE`
   wired to the enclosing loop. `SELECT CASE` is modeled as an opaque multi-way
   branch — every arm and the no-match path reachable — so its bodies are
   analyzed rather than skipped.
2. **`DominatorTree`** computes immediate dominators (Cooper-Harvey-Kennedy) and
   dominance frontiers (Cytron).
3. **`SsaForm`** places phi functions and renames every non-escaping integral
   scalar local/global into versioned values, mapping each read to the version
   that reaches it.

## Why it is safe

It **bails** on post-test loops, `GOTO`/labels, `GOSUB`, `ON ERROR` and anything
else it cannot model precisely, so every graph it produces is sound by
construction — a body it does not understand is simply not optimized. Variables
that escape via a BYREF call, an address intrinsic or any opaque statement are
conservatively dropped from the renaming.

## Consumers

[O0017](O0017-sccp.md) (SCCP), [O0183](O0183-ssa-dead-store.md) (dead stores),
and — once it accepts more shapes — every planned loop and memory pass.
