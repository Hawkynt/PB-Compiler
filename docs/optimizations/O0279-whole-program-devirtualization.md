# O0279 — Whole-program devirtualization

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Whole-program |
| **IR** | ✅ `Ir/Passes/WholeProgramDevirtualization.cs` — exact singleton-target devirtualization over function values, pointer-preserving bitcasts, phi/select merges, fully-visible callback parameters, and closed local pointer cells. Null-capable, multi-target, escaping, cyclic, or signature-incompatible flows decline. Registered after global localization and before IPCP; verified by `WholeProgramDevirtualizationTests` and `IrPassObservableEquivalenceTests` |
| **Related** | [O0271](O0271-indirect-call-promotion.md), [O0307](O0307-speculative-devirtualization.md), [O0022](O0022-dead-procedure-elimination.md) |

## The idea

When the **complete set** of possible targets of an indirect call is known, the
call can be resolved statically. PB has no vtables, but it has procedure
pointers — `CODEPTR32`, typed delegates, lambdas — and those values eventually
flow through ordinary pointer operands in the IR.

If that complete target set contains exactly one non-null procedure, the call
through it *is* a direct call, with no guard needed. The important word is
*complete*: seeing one address-taken function in the module is not evidence that
an arbitrary pointer can name only that function.

## Applies to

```basic
DIM f AS FUNCTION(LONG) AS LONG      ' pb36 typed procedure pointer
f = CODEPTR32(Double&)               ' the only assignment in the program
PRINT f(21)                          ' provably Double&
```

The IR implementation also handles the interprocedural form that a local pass
cannot solve on its own: a procedure takes a callback parameter, every visible
caller passes the same function, and the callee invokes that parameter.

## IR implementation

`WholeProgramDevirtualization` computes a small exact points-to set. It follows
only relations whose semantics are explicit in the IR:

- an `IrFunction` is a singleton target;
- `bitcast`, `phi`, and `select` preserve or join complete target sets;
- a formal callback parameter is complete only when `IpConstantProp`'s existing
  whole-program visibility proof says every call is visible and the function's
  address has not escaped;
- a local pointer cell is complete only when its address never escapes, every
  access is a compatible direct load/store, and a store dominates the load so
  implicit zero initialization cannot be observed.

Before rewriting, the pass also checks the direct function's return and fixed
parameter storage types against the indirect call shape. Opaque pointers do not
carry that signature, so provenance alone is not enough.

The pass runs after `LocalizeGlobals`: when global cleanup exposes a private
cell and the following function sweep promotes it to SSA, O0279 sees the most
precise value flow available. It runs before `IpConstantProp`, so every indirect
call it resolves immediately becomes an ordinary call-graph edge for IPCP.

## Still open

- A complete set with **more than one** target is intentionally left indirect;
  compare-chain/jump-table promotion is [O0271](O0271-indirect-call-promotion.md).
- An **incomplete** set is never guessed. Guarded/speculative devirtualization is
  [O0307](O0307-speculative-devirtualization.md).
- General points-to analysis for escaped/global/far delegate storage needs a
  stronger alias/linkage model before absence from the IR can be treated as
  proof.
- Devirtualizing every remaining procedure-pointer call would also lift the
  program-wide disable that a single `CODEPTR` currently imposes on some
  interprocedural optimizations and register-parameter decisions.
