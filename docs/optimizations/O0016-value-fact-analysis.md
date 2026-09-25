# O0016 — Value-fact analysis (intervals, known bits, congruences)

| | |
|---|---|
| **Status** | 🟡 Partial — interval and known-bits analyses are implemented as separate IR analyses behind one query facade; the congruence domain and the reduced product are not implemented on the IR path (storage narrowing is [O0057](O0057-storage-narrowing.md)) |
| **Stage** | IR middle end (analyses) |
| **Source** | `Ir/Analysis/IrRangeAnalysis.cs` — `RangeAt`, `Decide`; `Ir/Analysis/ValueRange.cs`; `Ir/Analysis/IrKnownBitsAnalysis.cs` — `For`; `Ir/Analysis/IrValueFacts.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `IrValueFactsTests`, `IrKnownBitsAnalysisTests`, `RangeCheckElimTests`, `DIFF77/78/79/80/87/89/92/93/112.BAS`, `SelfDifferentialTests` |
| **Related** | [O0017](O0017-sccp.md), [O0013](O0013-promotion-lowering.md), [O0057](O0057-storage-narrowing.md) |
| **Split into** | [O0217](O0217-bounds-check-elimination.md), [O0218](O0218-range-comparison-folding.md), [O0219](O0219-overflow-check-elimination.md), [O0220](O0220-divide-guard-elimination.md), [O0221](O0221-operation-narrowing.md), [O0222](O0222-identity-operation-removal.md), [O0223](O0223-constant-result-folding.md), [O0224](O0224-bounded-multiply-off-fpu.md) |

## What it is

Two abstract domains are computed per integer SSA value, each by its own
analysis, because an interval is only one way to be ignorant:

| Domain | Knows | The question only it answers |
|---|---|---|
| `IrRangeAnalysis` | `[lo, hi]` | `x MOD 2` is never 2 |
| `IrKnownBitsAnalysis` | which bits are always 1 / always 0 | `x AND 12` is never 5 |

`IrValueFacts` is a query facade over them (and over the pointer nullness and
alignment analyses): it owns no lattice of its own, and each domain keeps its
own solver and invalidation. `Decide(comparison, block)` answers a comparison
as soon as the range, alignment or nullness domain settles it, and leaves it
intact otherwise.

The retired syntax-level lattice also carried a congruence domain
(`v ≡ r (mod m)`) and combined all three as a reduced product, with facts
flowing between domains. Not implemented on the IR path; the syntax-level
version was retired with the direct emitter.

**This page covers the analyses themselves.** What the facts are *used for* is
a set of separate entries (see *Split into* above): bounds, overflow and
divide-guard elimination, comparison folding, operation narrowing, identity and
constant-result removal, and keeping a bounded multiply off the FPU.

The interval transfer functions cover `+ - *`, signed and (non-negative)
unsigned `\`/`MOD`, `AND`, `OR`/`XOR`, shifts by a constant, and the integer
casts; anything else is the type's whole range. Known bits cover constants,
`AND`/`OR`/`XOR`, truncation and extension, selects and phis.

## How ranges are computed

`IrRangeAnalysis` has two halves:

1. **A global fixpoint over the def-use graph.** Every instruction starts
   empty, constants and type bounds seed the leaves, and the blocks are swept
   in reverse postorder until nothing moves. A phi joins its incoming edges; a
   growing endpoint is **widened** to its type's bound after three sweeps, and
   descending sweeps then recover what widening gave away.
2. **A per-block refinement from dominating branches.** `RangeAt` re-evaluates
   an expression at the block that uses it, intersecting each leaf with what
   the dominating conditional edges prove about it — `i` is at most 10 inside a
   body guarded by `i <= 10`. Most bounds-check elision comes from here.

A result that does not fit its fixed-width type falls back to the type's whole
range (`ValueRange.Fit`), and a truncation keeps its operand's range only when
it already fitted. A sign extension of an unsigned source, or a zero extension
of a possibly negative one, yields the range that conversion can produce rather
than the operand's.

## Sample

```basic
$ERROR BOUNDS ON
DIM a%(0 TO 99), i%, h%
FOR i% = 0 TO 99
  a%(i%) = i%
NEXT
h% = a%(h% AND 63)
PRINT (a%(0) \ 2) * 2 = 1        ' provably FALSE, whatever a%(0) is
```

## Without the optimizer

```asm
    ; per array access, under $ERROR BOUNDS
    mov     ax, [i]
    cmp     ax, 0000h
    jl      rt_err_arr
    cmp     ax, 0063h
    jg      rt_err_arr
    ...
    ; and the comparison is really computed
```

## With the optimizer

```asm
    ; the counter's range is [0,99] and the array's is [0,99]: no check at all
    ...
    ; h% AND 63 is bounded to [0,63], inside [0,99]: no check
    ...
    xor     ax, ax           ; the comparison folded to FALSE (bit 0 is always 0)
```

The last fold came from the retired lattice, whose known-bits transfer reached
through `\` and `*`. `IrKnownBitsAnalysis` does not model division or
multiplication, so these analyses do not fold that comparison.

## Equivalent BASIC

```basic
$ERROR BOUNDS OFF            ' but only for the accesses that provably cannot fail
...
PRINT 0                      ' the impossible comparison
```

## Why it is safe

- **Everything over-approximates.** An unknown leaf is its type's whole range,
  an unmodelled operation is the type's range, and a value loaded from memory
  is never assumed to be anything but its type. A consumer may act only when
  the whole interval qualifies.
- **Every range describes runtime values.** A result that would leave its
  fixed-width type is not kept as a mathematical hull; it falls back to the
  whole type, so a consumer never mistakes `60000` for the value of a wrapped
  INTEGER.
- Bit facts survive two's-complement wrapping (wrapping is arithmetic modulo
  2ⁿ, which leaves the low n bits where they were), so they need no
  dialect-dependent exactness proof. Unsupported operations are unknown.
- Both analyses work on SSA values, so a value has one definition and memory
  is never reasoned about: a variable only has a range once `Mem2Reg` has
  promoted it.

## References

The design was cross-checked against LLVM's `ConstantRange`/value-tracking model,
where binary operators map operand ranges to result ranges, and against GCC's
VRP description, which explicitly propagates ranges of values rather than only
constants. No implementation code was copied; O0016 is independently implemented
for PB's fixed-width and dialect-specific semantics.
