# O0300 — ASCII string specialization

| | |
|---|---|
| **Status** | ✅ Implemented for compile-time-proven ASCII strings |
| **Stage** | IR middle-end (`strfold`) |
| **Source** | `Ir/Passes/StringConstantFold.cs` |
| **Gate** | `--optimize`; every byte of the operand must be known and `< 0x80` |
| **Verified by** | `PowerBasic.Compiler.Tests/Ir/AsciiStringSpecializationTests.cs` |
| **Related** | [O0154](O0154-swar-search.md), [O0153](O0153-swar-arithmetic.md), [R0003](R0003-string-engine.md) |

## The idea

`UCASE$`, `LCASE$` and case-insensitive comparison cannot in general assume a
7-bit alphabet: bytes above 127 belong to the active DOS code page, and mapping
them as though they were ASCII would be a semantic change.

The IR already has one stronger source of truth than an assertion: a pooled
string literal carries its exact byte vector. When the complete vector is
7-bit ASCII, case conversion is therefore a compile-time operation. The pass
maps only `A`–`Z` / `a`–`z`, interns the result and deletes both the runtime case
call and the old single-use literal producer.

That is stronger than routing the value to a specialized run-time loop: for the
proved-constant case there is no loop left to optimize.

## Applies to

```basic
s$ = UCASE$("status: ok")
```

and, because `strfold` runs to a fixpoint, to literal expressions that become a
single literal first:

```basic
s$ = UCASE$("status: " + "ok")
```

## Before

The lowering spells the expression as an owned string handle followed by the
general case routine:

```text
%literal = call ptr @rt_str_const(...)
%upper   = call ptr @rt_str_ucase(%literal)
```

The runtime then walks every byte.

## After

For a literal whose bytes are all below `80h`:

```text
%upper = call ptr @rt_str_const(@"STATUS: OK", 10)
```

No case-conversion call remains. Ordinary global DCE can subsequently discard
the now-unreferenced original literal.

## Soundness boundary

The proof is intentionally narrow:

- every input byte must be present in the IR;
- every byte must be `< 128`;
- only the ASCII case pairs are changed, using the fixed `0x20` delta;
- the literal producer must have exactly one user, preserving the string
  runtime's consuming-handle ownership rule;
- dynamic strings, `CHR$(128..255)`, file/input data and any other value whose
  byte domain is not proved stay on `rt_str_ucase` / `rt_str_lcase`.

This follows the 7-bit code positions specified by RFC 20 / US-ASCII and the
portable `toupper`/`tolower` mapping defined for the POSIX locale. No external
implementation code is copied.

## Still worth doing

A future module-wide ASCII fact (for example an explicit `$OPTION ASCII`) could
legally specialize *dynamic* strings too. At that point the natural runtime
implementation is the SWAR path from [O0153](O0153-swar-arithmetic.md): word-wide
on 8086 and dword-wide on 386+, with the general code-page-aware routine kept
for values without the fact. That broader assertion is deliberately not
invented here; an optimization hint that cannot be proven or explicitly opted
into is merely a miscompile with better marketing.

Case-insensitive comparison should use the same fact once such an IR/runtime
operation exists; the current middle-end has no separate case-insensitive
string-compare primitive to specialize.
