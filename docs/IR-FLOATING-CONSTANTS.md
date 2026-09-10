# Exact floating constants in the IR

`IrConstantFloat` has two representation paths:

- IEEE binary32/binary64 values keep the existing managed `double` payload (with binary32 rounded on construction).
- IEEE x87 double-extended (`IrType.F80`) values carry an `IrFloat80` raw representation: the 16-bit sign/exponent field and the explicit 64-bit significand.

The F80 representation is intentionally bit-exact rather than a numeric wrapper around `double`. x87 double-extended has a wider exponent and an explicit 64-bit significand, so a binary64 payload cannot represent arbitrary extended values, NaN payloads, signed zero encodings, or the extra eleven significand bits.

## Exact-host subset

Existing lowering commonly constructs an F80 value from a managed `double`. `IrFloat80.FromDouble` widens that value exactly; every binary64 value is representable in x87 extended precision.

Code that wants to evaluate a constant with .NET floating arithmetic must call `TryGetDoubleExact`. It succeeds only when converting the stored F80 bits to binary64 loses no information. The compatibility `Value` property has the same rule and throws for an arbitrary extended value instead of silently rounding it.

This is deliberate. The middle-end does not contain a second software implementation of x87 arithmetic. Constant folding, integer recovery, demotion, and other host-double transforms simply decline an F80 operation they cannot evaluate exactly. The target therefore receives the original operation and exact constant.

## Identity and propagation

F80 constant identity is the full 80-bit representation. GVN, SCCP, and interprocedural constant propagation compare those bits, so values which differ only in the eleven significand bits beyond binary64 remain distinct. SCCP clones F80 constants from the raw representation rather than routing them through `double`.

The same raw-bit rule is conservative for signed zero and NaN payloads. It agrees with the IR's storage-level floating equality rules: optimizations must not merge byte-distinct constants merely because a host floating comparison considers their numeric values equivalent.

## Text and LLVM

LLVM spells exact `x86_fp80` bit constants with the legacy hexadecimal `0xK` form: four hexadecimal digits for sign/exponent followed by sixteen for the explicit significand. `IrFloat80.ToLlvmHexString` is the single formatter used by `LlvmEmitter` and the debug `IrPrinter`.

This also fixes a prior emitter limitation: the old `FormatFp80(double)` regenerated an extended value from binary64 and emitted zero for binary64 subnormal, infinity, and NaN inputs. The emitter now writes the stored 80 bits directly.

## Analysis boundary

Analyses whose numeric domain is represented by `double` do not invent binary64 endpoints for an arbitrary F80 value. `FpDomainAnalysis` can still retain classification facts such as finite/non-NaN for a canonical extended value, but reports no known numeric interval unless the exact value fits binary64. `FpSimplify` follows the same rule.

## Backend boundary

This change makes arbitrary extended constants representable and optimizable without loss inside the IR and makes LLVM emission bit-exact. It does not claim that every other backend can materialize an arbitrary x87 literal yet. Backends whose literal transport is only binary32/binary64 must either consume the exact-host subset or explicitly decline; they must not decimalize or narrow an arbitrary F80 value.

## References and licensing

The representation follows the architectural x87 double-extended format documented by Intel: one sign bit, a 15-bit exponent, and an explicit 64-bit significand. LLVM's Language Reference defines `x86_fp80` and the exact `0xK` hexadecimal constant form.

No implementation code was copied or translated from LLVM or Intel material. The implementation is original and derives only the public data format and interoperability requirements. No dependency was added.
