# O0057 — Storage narrowing

| | |
|---|---|
| **Status** | 🟡 Partial — integer scalar and promoted-phi storage narrowing implemented; generic floating storage narrowing remains with the float-demotion / mixed-precision work |
| **Stage** | IR middle end; profitability floor supplied by the back end |
| **IR** | 🟡 `Ir/Passes/StorageNarrowing.cs` — range-proven integer allocas and SSA phis narrow at storage boundaries while arithmetic stays at source width |
| **Gate** | `--optimize` |
| **Verified by** | `StorageNarrowingTests` + `IrVerifier` |
| **Related** | [O0016](O0016-value-fact-analysis.md), [O0012](O0012-float-demotion.md), [O0347](O0347-mixed-precision.md), [C0001](C0001-386-codegen.md) |

## The idea

A value whose facts prove it fits a narrower integer type can use that narrower
**representation** without changing the width at which the BASIC expression is
computed. A `LONG` confined to `0..255`, for example, may occupy a byte or word
cell according to target policy, but `LONG` arithmetic using that value still runs
at 32 bits.

The distinction is the safety argument: O0057 narrows storage, not language
semantics.

## Applies to

```basic
DIM count&, i&
FOR i& = 0 TO 200
  count& = count& + 1
NEXT
```

If the range lattice proves the loop-carried values fit a word, their carried
representation may be 16 bits. Uses that require a `LONG` see an extension back to
32 bits.

## IR implementation

`StorageNarrowing` consumes the existing [O0016](O0016-value-fact-analysis.md)
`IrRangeAnalysis` facts in two forms.

### Direct scalar storage

A scalar `IrAlloca` is considered only when it is exactly the kind of slot
`Mem2Reg` can safely own: one element, direct storage-compatible loads/stores,
no GEP, no address escape and no incompatible aliasing view. The pass reuses
`Mem2Reg.IsPromotable` for that proof instead of maintaining a second definition
of "private scalar storage".

The range starts with zero because PB variables are implicitly zero-initialized,
then joins the range of every stored value at that store's block. If the entire
joined range fits a smaller integer type, the slot is replaced:

```text
store i32 value, slot
    => narrow = trunc i32 value to i16
       store i16 narrow, narrow-slot

loaded = load i32, slot
    => narrow = load i16, narrow-slot
       loaded = sext/zext i16 narrow to i32
```

The `trunc` is after the source-width computation; the extension is before any
ordinary source-width consumer.

### Promoted SSA values

After `mem2reg`, a source variable that merges control flow is represented by an
`IrPhi`. When the whole phi range fits a smaller representation, O0057 creates a
narrow phi, truncates each incoming value on its predecessor edge, and extends the
result once at the phi boundary.

A loop backedge therefore remains conceptually:

```text
wide-next = add i32 wide-current, step
narrow-next = trunc i32 wide-next to i16
narrow-phi = phi i16 [..., narrow-next]
wide-current = sext/zext i16 narrow-phi to i32
```

The recurrence arithmetic is still 32-bit. Only the carried representation is
narrow.

The standard pipeline gives O0057 opportunities around both promotion stages:
before the first `mem2reg`, immediately after promotion for SSA phis, after SROA
before `mem2reg2`, and again after the second promotion.

## Target policy

Proving a width is target-neutral; deciding whether materializing that width is
profitable is not. `IrPassManager.Standard` therefore exposes
`minimumIntegerStorageBits`.

The current default is 16 bits, matching the x86-16 policy: a `LONG` may become a
word representation, but O0057 does not force byte cells merely because the range
fits one. A backend for which byte storage is profitable can request an 8-bit
floor.

This keeps target cost policy out of the range proof and avoids hard-coding 8086
tradeoffs into shared IR.

## Why it is safe

- The range must contain **every** value stored or carried by the representation.
  Unknown/top ranges do not narrow.
- Direct memory narrows only when `Mem2Reg` already proves the storage is private
  and directly accessed. Address-observable storage is rejected.
- Integer arithmetic, shifts, comparisons and overflow behavior remain at the
  original IR width. O0057 inserts conversions only at representation boundaries.
- Signed storage is used when the proven range contains negative values; otherwise
  unsigned storage plus zero-extension preserves the represented value.
- `$ERROR NUMERIC ON` does not move an arithmetic operation to a smaller width, so
  this pass does not introduce an earlier narrow-width overflow point.
- Every transformed test case is passed through `IrVerifier`, including phi
  dominance and conversion-width checks.

LLVM's `trunc`, `zext` and `sext` semantics are the model for those IR boundaries:
`trunc` discards high bits, while zero/sign extension reconstructs the represented
unsigned/signed value at the wider type. No external implementation code was copied
or translated.

## Still planned

- General floating **storage** narrowing beyond the exact float-demotion and
  mixed-precision cases already handled by O0012/O0347.
- Backend-specific cost plumbing for targets that want an 8-bit floor by default.
- Wider whole-program/data-layout cases where storage identity is observable; those
  need their own alias/layout proofs rather than relaxing the scalar safety gate.
