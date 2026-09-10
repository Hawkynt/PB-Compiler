# O0308 — Speculative overflow elimination

| | |
|---|---|
| **Status** | ✅ Implemented for counted signed add/sub loops, including profitable checked-array preflight for O0026 vectorization |
| **Stage** | IR mid-end + direct emitter consumer |
| **IR** | `Ir/Passes/SpeculativeOverflowElimination.cs` |
| **Emitter** | `CodeGen/CodeGenerator.OverflowVectorization.cs` + the O0026 hook in `CodeGenerator.cs` |
| **Related** | [O0026](O0026-auto-vectorization.md), [O0219](O0219-overflow-check-elimination.md), [O0306](O0306-loop-versioning.md), [O0350](O0350-overflow-check-coalescing.md) |

## The idea

[O0219](O0219-overflow-check-elimination.md) drops a check only when the range
proof succeeds. When it does not, a **single guard** on the inputs can establish
the same fact for a whole region: prove once that the operands are small enough,
then run an unchecked body while retaining the original checked loop as fallback.

O0308 now has two consumers of that idea:

1. the IR middle-end uses an O(1) runtime range guard when one signed `ADD`/`SUB`
   operand is loop-invariant and the other already has a finite SSA interval;
2. the direct emitter may use one read-only O(n) array preflight when the payoff is
   concrete: the successful path immediately enters O0026's packed-integer
   vectorizer.

The second form is intentionally not a generic "scan to delete checks" transform.
Without a consumer that substantially changes the loop, paying another memory
pass would merely exchange O(n) checks for O(n) checks under a different name.

## O(1) IR guard

For an exact counted loop, if one operand of a checked signed `ADD`/`SUB` is
loop-invariant and the other already has a proven SSA interval, O0308 derives the
invariant operand's safe interval and tests it once in the preheader. The true
edge enters a cloned loop with the covered Error 6 branches bypassed; the false
edge enters the original loop unchanged.

For example, if the loop-varying operand of a signed 32-bit addition is known to
be `[0, 999]`, the invariant addend only needs the preheader test
`addend <= 2147482648`. Every addition in the fast loop is then safe, so its
per-iteration overflow branch is redundant.

```basic
$ERROR OVERFLOW ON
DIM i%, n%, bias&
FOR i% = 0 TO n%
  x& = CLNG(i%) + bias&
NEXT
```

The range need not come from a FOR counter. A narrowed value or a dominating
comparison can provide it through `IrRangeAnalysis` as well.

## Array-wide preflight feeding O0026

For the exact element-wise shape O0026 already vectorizes,

```basic
$CPU 80586 SSE2
$OPTIMIZE SPEED
$ERROR OVERFLOW ON
DIM i%, a%(1 TO 100), b%(1 TO 100), c%(1 TO 100)
FOR i% = 1 TO 100
  c%(i%) = a%(i%) + b%(i%)
NEXT
```

packed `PADDW`/`PSUBW` cannot reproduce PB's Error 6 contract by themselves: they
produce the wrapped 16-bit lane result, while ordinary scalar `ADD`/`SUB` exposes
signed overflow through OF. O0308 therefore emits a read-only scalar scan first:

1. walk `a(i)` and `b(i)` without storing anything;
2. perform the same signed 16-bit `ADD` or `SUB` and branch on OF;
3. if every pair is safe, enter the existing O0026 vector body with overflow
   checking disabled for that proven-safe clone;
4. if any pair overflows, restart the original checked scalar FOR from its
   beginning.

The slow edge deliberately does **not** raise Error 6 from the preflight. Because
no destination element has been stored yet, rerunning the checked loop preserves
the original observable order: preceding elements are written normally and the
same first overflowing arithmetic operation raises Error 6 at the same loop
counter value.

This path is profitability-gated. It currently requires at least 32 elements and
at least two complete vectors at the selected width, so AVX-512 requires 64
16-bit elements while MMX/SSE2/AVX2 reach the 32-element floor. It also requires:

- `$OPTIMIZE SPEED` and an enabled O0026 SIMD feature (MMX/SSE2/AVX2/AVX-512);
- a constant step of `1` and a finite constant trip count whose bounds survive
  signed-INTEGER coercion unchanged and whose final `hi + 1` counter value does
  not wrap; in particular `FOR i% = ... TO 32767 STEP 1` is rejected because,
  without `$ERROR NUMERIC`, that counter wraps to `-32768` and the original loop
  continues;
- static rank-1 signed INTEGER source/destination arrays indexed exactly by the
  FOR counter;
- `$ERROR OVERFLOW ON`, but no bounds checking, numeric counter-wrap checking,
  or active resumable-error scope;
- an `ADD` or `SUB` body. Checked multiplication remains scalar.

If O0026's eligibility changes in the future and declines after a successful
preflight, O0308 falls back to the ordinary checked scalar loop rather than
turning an optimization miss into a compiler failure.

## IR legality and profitability

The IR pass declines unless all of these hold:

- the loop has an exact non-zero trip count and at least two iterations;
- it has one CFG entry and one CFG exit, with no `EXIT LOOP` edge or address-taken
  block that could enter the cloned region invisibly;
- the checked operation is the exact signed add/sub Error 6 shape emitted by
  `IrLowering.CheckedArithmetic`;
- exactly one arithmetic operand is loop-invariant and the other has a finite
  range from `IrRangeAnalysis`;
- the loop is small enough that cloning it is within the pass's code-growth
  budget;
- an O(1) runtime bound actually remains after ordinary static range reasoning.

Existing exit phis are extended for the new fast-loop predecessor, and values
defined in the loop but read after it receive an LCSSA-style join in the common
exit. The fallback remains the original checked IR, so when the preheader guard
fails Error 6 is still raised by the same per-iteration check.

Signed multiplication is intentionally still checked in both forms. Its lowering
uses a wider exact product and min/max tests; handling it deserves its own
product-bound proof instead of pretending the add/sub inequalities apply.

## References and licensing

The control-flow structure follows the standard loop-versioning model: a runtime
check selects an optimized clone or the unchanged fallback. LLVM's
`LoopVersioning` utility documents the same single-exit/preheader requirements,
and LLVM's `LoopFlatten` uses loop versioning specifically when an overflow proof
cannot be discharged statically.

The array consumer follows the x86 architectural semantics documented by Intel:
ordinary `ADD`/`SUB` set OF on signed overflow, whereas packed `PADDW`/`PSUBW`
perform non-saturating lane arithmetic. Those instruction semantics are used as
the interoperability contract; no implementation code is taken from Intel or
LLVM.

- Intel® 64 and IA-32 Architectures Software Developer's Manual: https://www.intel.com/content/www/us/en/developer/articles/technical/intel-sdm.html
- LLVM `LoopVersioning`: https://llvm.org/doxygen/classllvm_1_1LoopVersioning.html
- LLVM `LoopFlatten` overflow-versioning path: https://llvm.org/doxygen/LoopFlatten_8cpp_source.html

LLVM is Apache-2.0 WITH LLVM-exception and was used as architectural/behavioral
reference only. The PB-Compiler implementation is independently written against
its own IR, lowering contract, range lattice, and existing O0026 emitter; no LLVM
implementation code is copied or translated.
