# O0067 — `IF`-chain → jump table

| | |
|---|---|
| **Status** | ⬜ Planned |
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

## Planned

The same shape [O0029](O0029-select-jump-table.md) already emits:

```asm
    mov     ax, [k]
    dec     ax
    cmp     ax, 0003h
    ja      Default
    shl     ax, 1
    mov     bx, ax
    jmp     word ptr [Table+bx]
```

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

## What it needs

- A recognizer over `IF`/`ELSEIF` chains: every condition must be
  `<same pure lvalue> = <integer constant>` with distinct constants, and the
  subject must be provably unmodified by the arms.
- The **density test** and the table emission are already there — this is
  recognition work, not codegen work.
- Care with `$ERROR BOUNDS`/side effects: a subject expression that could trap
  must be evaluated exactly once, which the table form does anyway.
