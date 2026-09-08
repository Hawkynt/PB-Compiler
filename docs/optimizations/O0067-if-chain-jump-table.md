# O0067 — `IF`-chain → jump table

| | |
|---|---|
| **Status** | ✅ Done |
| **Stage** | Emitter + IR middle end |
| **Source** | `CodeGen/CodeGenerator.cs` — `TryEmitIfChainJumpTable`; `Ir/Passes/SwitchFormation.cs` |
| **Gate** | optimizer enabled |
| **Verified by** | `OptimizerTests`, `SwitchFormationTests` |
| **Related** | [O0029](O0029-select-jump-table.md), [O0032](O0032-short-circuit-conditions.md) |

## The idea

[O0029](O0029-select-jump-table.md) turns a dense `SELECT CASE` into a word jump
table. A chain of **mutually exclusive equality tests** on the same variable is
the same dispatch written differently, and DOS-era code writes it constantly —
often because the source predates `SELECT CASE` or was translated from a
line-numbered dialect.

## Applies to

```basic
DIM k%
IF k% = 1 THEN
  PRINT "one"
ELSEIF k% = 2 THEN
  PRINT "two"
ELSEIF k% = 3 THEN
  PRINT "three"
ELSEIF k% = 4 THEN
  PRINT "four"
ELSE
  PRINT "?"
END IF
```

## Today

Without dispatch recovery there can be up to four compares and four branches
before the last arm runs:

```asm
    mov     ax, [k]
    cmp     ax, 0001h
    je      Arm1
    cmp     ax, 0002h
    je      Arm2
    cmp     ax, 0003h
    je      Arm3
    cmp     ax, 0004h
    je      Arm4
    jmp     Default
```

## Optimized

The direct emitter reuses the same jump-table machinery as
[O0029](O0029-select-jump-table.md):

```asm
    mov     ax, [k]
    dec     ax
    cmp     ax, 0003h
    ja      Default
    shl     ax, 1
    mov     bx, ax
    jmp     word ptr [Table+bx]
```

`EmitIf` calls `TryEmitIfChainJumpTable`, which recognizes a chain whose every
condition is `<same integer variable> = <foldable constant>` (either operand
order), synthesizes the equivalent `SelectStmt` — **reusing the original subject
and constant expression nodes**, so the model's type and constant-fold queries
still resolve — and hands it to `TryEmitSelectJumpTable`. The two forms then
share every rule and emit byte-for-byte identical code.

The IR path performs the same recovery one level earlier. `SwitchFormation` is
now part of `IrPassManager.Standard`, after the ordinary value/CFG transforms and
tail-recursion lowering. It reads the surviving compare chain as a set of values
and replaces it with one target-neutral `IrSwitch`. The pass manager then runs
another fixpoint sweep, so DCE removes comparisons made dead by the new
terminator. This applies to `--emit-c`, `--emit-llvm`, and the routed x86-16 path
instead of depending on a downstream compiler to rediscover the source-level
construct.

`IrSwitch` deliberately does **not** mean "always emit a jump table". It records
the dispatch semantics once; the target chooses the profitable representation.
The x86-16 selector can choose a dense table, compressed table, membership mask,
perfect hash, or compare tree, while LLVM remains free to perform its own
target-specific switch lowering.

### Why it is sound

- **Same dispatch semantics.** First-match-wins: a value appearing in two arms
  keeps the earlier one, exactly as the top-to-bottom chain would.
- **One subject.** Every comparison leaf must use the same integer SSA value;
  mixed-variable conditions decline rather than being guessed into a switch.
- **Pure absorbed blocks only.** An intermediate test block must be reached only
  from the chain, contain no phis/address-taken entry, and contain only pure
  compare/cast/binary work whose values do not escape that block.
- **Fixed-width equality.** Case constants are normalized to the subject width,
  so signed and unsigned spellings of the same bit pattern agree.
- **Conservative size bounds.** Fewer than three distinct values or more than
  256 enumerated values remain as compares; unsupported strings, floats,
  unsigned ordering predicates, and unsafe CFG shapes also remain untouched.
- **Optimizer gate.** `IrPassManager.Legalize()` does not run switch formation,
  so `--no-optimize` preserves the faithful compare-chain representation.

## Equivalent BASIC

```basic
SELECT CASE k%
  CASE 1 : PRINT "one"
  CASE 2 : PRINT "two"
  CASE 3 : PRINT "three"
  CASE 4 : PRINT "four"
  CASE ELSE : PRINT "?"
END SELECT
```

The focused middle-end regression compiles an actual `IF`/`ELSEIF` equality
chain, including reversed operand order (`11 = k`), through
`IrPassManager.Standard` and asserts that it contains one `IrSwitch` with the
four expected cases. Existing switch-formation tests cover duplicate values,
ranges, exclusions, mixed variables, strings, enumeration limits, and cleanup of
the original comparison chain.
