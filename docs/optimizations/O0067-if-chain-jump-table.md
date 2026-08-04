# O0067 — `IF`-chain → jump table

| | |
|---|---|
| **Status** | ✅ Done |
| **Stage** | Emitter |
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

Up to four compares and four branches before the last arm runs:

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

## Now

The same jump table [O0029](O0029-select-jump-table.md) emits — in fact the
*identical* code:

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
share every rule and emit byte-for-byte identical code (a regression test
compiles an equality `IF`-chain and the matching `SELECT CASE` and asserts the
images are equal).

### Why it is sound

- **Same dispatch semantics.** First-match-wins: a value appearing in two arms
  keeps the earlier one (`byValue.TryAdd`), exactly as the top-to-bottom chain
  would. Verified byte-identical against the genuine oracle, including a
  reversed-operand test (`5 = i`) and a duplicate-constant arm.
- **Subject read once.** The recognizer requires a bare variable (a pure read),
  so evaluating it a single time in the table dispatch matches the chain, which
  re-reads the same unchanging value at each `ELSEIF`.
- **Conservative decline.** Any non-equality condition, a range/comparison
  selector, a different variable, or a set too small or sparse (`< 4` values, or
  not dense enough for `TryEmitSelectJumpTable`) makes the helper return with
  nothing emitted, and `EmitIf` falls back to the ordinary compare chain.

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

Native-only, in `CodeGenerator.EmitIf`. The IR back ends lower an `IF`-chain to a
sequence of compares-and-branches that LLVM's own `simplifycfg`/switch-formation
(and the C compiler's) turn into a jump table, so the C/LLVM output tabulates
without a dedicated IR pass.
