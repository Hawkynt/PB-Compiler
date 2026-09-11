# O0057 — Storage narrowing

| | |
|---|---|
| **Status** | 🟡 Partial — integer and lossless IEEE floating scalar/promoted-phi storage narrowing implemented; observable/global/aggregate storage remains separate |
| **Stage** | IR middle end; integer profitability floor supplied by the back end |
| **IR** | 🟡 `Ir/Passes/StorageNarrowing.cs` — range-proven integers and losslessly-single-representable IEEE floats narrow at storage boundaries while arithmetic stays at source width |
| **Gate** | `--optimize` |
| **Verified by** | `StorageNarrowingTests` + `IrVerifier` |
| **Related** | [O0016](O0016-value-fact-analysis.md), [O0012](O0012-float-demotion.md), [O0347](O0347-mixed-precision.md), [C0001](C0001-386-codegen.md) |

## The idea

A value whose facts prove it survives a narrower representation can use that
representation without changing the width at which the BASIC expression is
computed. A `LONG` confined to `0..255`, for example, may occupy a byte or word
cell according to target policy. A `DOUBLE` whose every reaching value is known
to be exactly representable as IEEE binary32 may occupy a `SINGLE` cell. Ordinary
uses are widened back to the declared type before computation.

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

The floating equivalent is a wider variable fed only by values that already carry
`SINGLE` precision, exact `SINGLE` constants, or integer-to-float conversions whose
integer range is wholly exact in binary32. The stored cell may be binary32 while a
subsequent `DOUBLE` computation still executes in binary64.

## IR implementation

`StorageNarrowing` consumes the existing [O0016](O0016-value-fact-analysis.md)
`IrRangeAnalysis` facts in two forms and adds a precision-provenance proof for
floating storage.

### Direct scalar storage

A scalar `IrAlloca` is considered only when it is exactly the kind of slot
`Mem2Reg` can safely own: one element, direct storage-compatible loads/stores,
no GEP, no address escape and no incompatible aliasing view. The pass reuses
`Mem2Reg.IsPromotable` for that proof instead of maintaining a second definition
of "private scalar storage".

For integers the range starts with zero because PB variables are implicitly
zero-initialized, then joins the range of every stored value at that store's block.
If the entire joined range fits a smaller integer type, the slot is replaced:

```text
store i32 value, slot
    => narrow = trunc i32 value to i16
       store i16 narrow, narrow-slot

loaded = load i32, slot
    => narrow = load i16, narrow-slot
       loaded = sext/zext i16 narrow to i32
```

For IEEE floating storage, wider-than-32-bit slots narrow to `f32` only if every
store is proven lossless through the round trip:

```text
store f64 value, slot
    => narrow = fptrunc f64 value to f32
       store f32 narrow, narrow-slot

loaded = load f64, slot
    => narrow = load f32, narrow-slot
       loaded = fpext f32 narrow to f64
```

The proof deliberately does **not** use a small floating interval as evidence of
precision. `1.0 + 2^-40` is tiny in range but not exactly representable as binary32.
Instead O0057 accepts precision provenance it can prove:

- an existing binary32 value widened with `fpext`;
- a wider constant whose value round-trips exactly through binary32, preserving the
  sign of zero and declining NaNs;
- signed/unsigned integer-to-float conversions whose branch-refined integer range
  stays within `[-2^24, 2^24]`, where every integer is exactly representable in
  binary32;
- selects and phis whose alternatives are all independently proven exact.

Wide floating arithmetic itself is not treated as single-precision merely because
its operands or result range are small. Approximate/error-budget precision changes
belong to [O0347](O0347-mixed-precision.md), not this exact storage transform.

### Promoted SSA values

After `mem2reg`, a source variable that merges control flow is represented by an
`IrPhi`. Integer phis use the same range proof as direct storage. Wider IEEE phis
use the same exact-binary32 provenance proof as floating slots. O0057 creates the
narrow phi, converts each incoming value on its predecessor edge, and extends the
result once at the phi boundary.

An integer loop backedge therefore remains conceptually:

```text
wide-next = add i32 wide-current, step
narrow-next = trunc i32 wide-next to i16
narrow-phi = phi i16 [..., narrow-next]
wide-current = sext/zext i16 narrow-phi to i32
```

For a lossless floating merge the analogous boundary is `fptrunc` / `fpext`.
Arithmetic after the extension remains at the declared floating width, so O0057
introduces no new rounding point inside the expression.

The standard pipeline gives O0057 opportunities around both promotion stages:
before the first `mem2reg`, immediately after promotion for SSA phis, after SROA
before `mem2reg2`, and again after the second promotion.

## Target policy

Proving an integer width is target-neutral; deciding whether materializing that
width is profitable is not. `IrPassManager.Standard` therefore exposes
`minimumIntegerStorageBits`.

The current default is 16 bits, matching the x86-16 policy: a `LONG` may become a
word representation, but O0057 does not force byte cells merely because the range
fits one. A backend for which byte storage is profitable can request an 8-bit
floor.

Floating narrowing is currently the exact `f64`/`f80` to `f32` storage case. It is
not enabled by fast-math assumptions and does not consume an error budget.

## Why it is safe

- Integer ranges must contain **every** value stored or carried by the
  representation. Unknown/top ranges do not narrow.
- Floating values must be proven to round-trip through binary32 without numerical
  change; a range alone is insufficient evidence of precision.
- Direct memory narrows only when `Mem2Reg` already proves the storage is private
  and directly accessed. Address-observable storage is rejected.
- Integer arithmetic, shifts, comparisons and overflow behavior remain at the
  original IR width. Floating arithmetic likewise remains at its original IEEE
  width. O0057 inserts conversions only at representation boundaries.
- Signed integer storage is used when the proven range contains negative values;
  otherwise unsigned storage plus zero-extension preserves the represented value.
- `$ERROR NUMERIC ON` does not move an arithmetic operation to a smaller width, so
  this pass does not introduce an earlier narrow-width arithmetic overflow point.
- MBF storage is not treated as IEEE storage and is never narrowed by this rule.
- Every transformed test case is passed through `IrVerifier`, including phi
  dominance and conversion-width checks.

LLVM's `trunc`, `zext`, `sext`, `fptrunc` and `fpext` semantics are the model for
those IR boundaries. In particular, `fptrunc` performs the narrowing conversion and
`fpext` restores the wider floating type; O0057 only creates that pair when its own
proof says the narrowing cannot change the represented numerical value. No external
implementation code was copied or translated.

## Still planned

- Wider whole-program/data-layout cases where storage identity is observable; those
  need their own alias/layout proofs rather than relaxing the scalar safety gate.
- General aggregate/global storage narrowing where layout and ABI identity are
  explicitly proven safe.
- Approximate floating precision reduction remains [O0347](O0347-mixed-precision.md)
  and requires an explicit error budget rather than O0057's exact round-trip proof.
