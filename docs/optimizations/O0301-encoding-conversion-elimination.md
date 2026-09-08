# O0301 — Encoding-conversion elimination

| | |
|---|---|
| **Status** | 🟡 Partial |
| **Stage** | Mid-end |
| **Related** | [O0286](O0286-allocation-elimination.md), [O0089](O0089-extension-elimination.md), [O0303](O0303-formatted-print-specialization.md) |

## The idea

Back-to-back conversions that cancel out should not happen, and a value should be
kept in the **representation its consumers want**. In PB the "encodings" are the
string/number boundary and the fixed/dynamic/ASCIIZ string forms:

| Pattern | Result |
|---|---|
| `VAL(STR$(integer))` | direct integer-to-`VAL` numeric conversion — **implemented for an adjacent/private pair** |
| `VAL(STR$(float))` | unchanged: formatted precision can discard information |
| `STR$(VAL(s$))` | unchanged: `STR$` normalizes spelling and precision |
| ASCIIZ → dynamic → ASCIIZ | planned; truncation/NUL/storage observability need a stronger proof |
| `CHR$(ASC(s$))` | planned only where the empty-string case is proven equivalent |

## Applies to

```basic
DIM n&
n& = 123456
PRINT VAL(STR$(n&))          ' the temporary text representation is unobservable
```

A round trip through a named string variable is a wider data-flow problem: the store
makes the string representation observable until use/escape analysis proves otherwise.
This first slice therefore requires the formatter result to feed `VAL` directly.

The implemented IR form is:

```text
%text = call ptr @rt_str_from_i32(i32 %n)
%value = call f64 @rt_str_val(ptr %text)
```

and becomes:

```text
%value = sitofp i32 %n to f64
```

with the existing widening/narrowing after `VAL` left in place. Unsigned formatter
entries use `uitofp`; signedness is part of the proof, not inferred from the bits.
The same rule covers the 8-, 16-, 32- and 64-bit integer formatter entries.

## Implementation

`Ir/Passes/StringConstantFold.cs` performs the rewrite beside the other runtime-string
semantic folds. This is deliberately a module string pass rather than a generic cast
fold: to a value optimizer the formatter and parser are arbitrary calls, while the
string pass already knows the ownership contract of PB runtime handles.

The rewrite requires the formatter result to have **exactly one user**, the matching
`rt_str_val` call. Both calls consume/produce an owned temporary string handle; when
the pair is removed that handle is never allocated, parsed or freed. A second reader
therefore blocks the rewrite rather than being handed a handle whose producer vanished.

The formatter name and operand type must agree exactly (`i32` with
`rt_str_from_i32`, `u32` with `rt_str_from_u32`, and likewise for the other widths).
This prevents top-bit-set unsigned values from being silently reinterpreted as signed.

## Why the other directions stay unfused

- **Floating STR$ → VAL** is not an identity. PB chooses the printable precision from
  the declared type/dialect; text can therefore lose bits before `VAL` reparses it.
- **VAL → STR$** is visibly not an identity: whitespace, exponent spelling and
  significant digits may be normalized.
- **ASCIIZ load → dynamic handle → ASCIIZ store** is not generically a no-op. The
  store truncates to `capacity - 1`, writes a terminating NUL and can change raw bytes
  after the logical string. Eliminating it needs a proof about the storage state and
  what can observe it, not merely matching call names.
- **CHR$(ASC(s$))** needs an explicit non-empty/equivalent-empty precondition. Runtime
  variants in this repository do not currently expose one universally identical
  empty-string result, so folding the generic form would be a miscompile disguised as
  a tidy algebraic identity.

## References consulted

- PowerBASIC documentation for `STR$`/`VAL`: the functions are complementary for
  numeric text, while `STR$` defines a formatted representation rather than preserving
  the original source spelling. The implementation uses that behavioral contract only;
  no PowerBASIC source code is available or copied.
- LLVM InstCombine documentation/contributor guidance: redundant, target-independent
  representation conversions belong in canonical middle-end simplification when the
  transformation is proven semantics-preserving. LLVM documentation is Apache-2.0 WITH
  LLVM-exception; no LLVM implementation code was copied or translated.

This implementation is original and derived from PB runtime behavior plus the IR's
existing ownership/type contracts; it introduces no dependency and copies no external
implementation code.
