# O0336 — Finite-state-machine compilation

| | |
|---|---|
| **Status** | 🟡 Partial — single-value integer/byte classification chains are recovered as `IrSwitch`; table-driven and multi-state machines remain planned |
| **Stage** | Mid-end |
| **Source** | existing `Ir/Passes/SwitchFormation.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `StaticDispatchOptimizationTests` |
| **Related** | [O0029](O0029-select-jump-table.md), [O0154](O0154-swar-search.md), [O0335](O0335-perfect-hash-data.md) |

## The idea

Character-classification chains — repeated mutually exclusive tests on one
value — are a state-machine-shaped control-flow graph. Recovering one canonical
dispatch operation lets later lowering choose a jump table, mask, perfect hash or
decision tree rather than preserving the source branch chain.

## Implemented v1

The existing `SwitchFormation` pass already recognizes side-effect-free
classification chains over one integer/byte subject, including equality, ranges
and supported Boolean combinations, and enumerates bounded sets into an
`IrSwitch`. This PR adds explicit regression coverage tying that capability to
O0336.

That is the single-classification first step: it removes the branch-chain shape
without introducing a new FSM IR dialect.

## Applies to

```basic
IF c = "0" THEN
  ...
ELSEIF c = " " THEN
  ...
ELSEIF c = "," THEN
  ...
END IF
```

where the lowered tests are mutually exclusive checks of the same byte-valued
subject.

## Still planned

- Table-driven 256-entry character-class maps where that beats switch lowering.
- Multi-state machines whose arms update a recognizable state variable.
- Combining state and input into transition-table lookups.
- Vector/SWAR classification once a table representation exists.
