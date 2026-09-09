# O0278 — Global variable localization

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Whole-program |
| **IR** | ✅ `Ir/Passes/LocalizeGlobals.cs` — a scalar global whose only user is one function becomes an alloca there, after which `Mem2Reg` promotes it and every value pass sees it. The condition that makes it legal is NOT "only one function uses it": a global keeps its value between calls and a local does not, so the pass also requires a store in the ENTRY block with no load of the same global before it - which makes that store dominate every load, so whatever a previous call left cannot be observed. Registered with `AddModulePass`; verified by `LocalizeGlobalsTests` and `IrPassObservableEquivalenceTests` |
| **Related** | [O0023](O0023-dead-global-elimination.md), [O0165](O0165-readonly-global-propagation.md), [O0005](O0005-register-residency.md) |

## The idea

A `DIM SHARED` global that only **one** procedure ever touches is not really
global: converting it to a local (or to a private `STATIC`) exposes it to every
analysis that stops at globals today — register residency, SSA tracking, dead
stores, range facts.

DOS-era BASIC declares globals liberally, so this pattern is common in the
corpus.

## Applies to

```basic
DIM SHARED temp%             ' only Work touches it

SUB Work
  temp% = 0
  FOR i% = 0 TO 99 : temp% = temp% + i% : NEXT
  PRINT temp%
END SUB
```

## What it needs

- A whole-program **use census** per global — which
  [O0023](O0023-dead-global-elimination.md) already computes, with the same
  conservative guards (address taken, `COMMON`, exported, `DIM … AT`).
- A decision between *local* (a fresh value per invocation) and *static* (a
  value that persists): only the latter preserves the semantics when the
  procedure is re-entered, so `STATIC` is the safe default and `LOCAL` needs a
  proof that no value survives across calls.
