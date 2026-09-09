# O0347 — Mixed-precision computation

| | |
|---|---|
| **Status** | 🟡 Partial — exact binary32 basic-arithmetic narrowing is implemented; general expression/error-budget analysis remains planned |
| **Stage** | IR middle end |
| **Gate** | Ordinary optimizer for the exact cases |
| **IR** | `FpSimplify.NarrowDemandedPrecision` |
| **Related** | [O0012](O0012-float-demotion.md), [O0057](O0057-storage-narrowing.md), [O0346](O0346-fp-classification-simplification.md) |

## What is implemented

The middle end recognizes a binary32 operation that was widened to binary64 and
immediately rounded back to binary32:

```text
fptrunc (op (fpext a:f32 to f64), (fpext b:f32 to f64)) to f32
```

for `op = fadd, fsub, fmul, fdiv`. Both source operands must be proven finite and
non-NaN; division additionally requires the divisor to be proven non-zero. The
sequence is replaced by the corresponding binary32 operation directly.

This is not an approximation. For round-to-nearest arithmetic, binary64's 53-bit
significand is sufficiently wider than binary32's 24-bit significand that double
rounding of addition/subtraction, multiplication and division of binary32 inputs
through binary64 is innocuous: rounding the binary64 result to binary32 produces
the same correctly rounded binary32 value as doing the operation directly in
binary32. The published proofs cover gradual-underflow cases as well.

The strict finite/non-NaN guard is deliberately stronger than the numerical
double-rounding theorem. It keeps NaN payload/signalling behavior out of the
rewrite; division also avoids the exceptional zero-divisor cases. Because the
accepted subset is bit-result preserving for ordinary IEEE values, it belongs in
normal optimization and does not require `$OPTIMIZE SPEED`.

## Deliberate boundary

This does **not** narrow arbitrary expression trees. Once a rounded intermediate
feeds another operation, changing its precision changes the next operation's
input and the one-operation double-rounding result no longer proves the whole tree
equivalent. Accumulators, chained operations and arbitrary `DOUBLE -> SINGLE`
demotion therefore still need propagated error/exactness analysis.

The same theorem does not justify `DOUBLE` arithmetic through x87 `EXT`: binary80
has 64 significand bits, far short of the roughly twice-binary64 precision needed
for the corresponding basic-operation guarantee.

The broader "this value is really an integer" case remains
[O0012](O0012-float-demotion.md).

## References / licensing

- Pierre Roux, *Innocuous Double Rounding of Basic Arithmetic Operations*, Journal
  of Formalized Reasoning 7(1), 2014. The paper formally proves the binary32 via
  binary64 result for addition/subtraction, multiplication, division and square
  root, including gradual-underflow cases. Published CC BY 3.0.
- LLVM Language Reference, floating-point semantics and `fpext`/`fptrunc`, used to
  cross-check the IR's round-to-nearest and NaN boundaries.

No external implementation code was copied or translated. The transform and
regressions are original code derived from the published arithmetic theorem and
PB-Compiler's existing IR contracts; no dependency was added.
