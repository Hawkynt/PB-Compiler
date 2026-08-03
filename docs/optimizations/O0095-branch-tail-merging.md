# O0095 — Common branch-tail merging

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Assembler |
| **Related** | [O0040](O0040-identical-code-folding.md), [O0103](O0103-shared-epilogue.md), [P0006](P0006-header-squeeze.md) |

## The idea

Identical **suffixes** of `THEN`, `ELSE` or `SELECT CASE` arms need to exist
once. Each arm branches into the shared tail instead of carrying its own copy.

[O0040](O0040-identical-code-folding.md) folds whole identical *procedure*
regions; this is the same congruence test applied to the tails of sibling
blocks, which is where duplicated code most often appears in generated output.

## Applies to

```basic
SELECT CASE k%
  CASE 1 : v% = 10 : PRINT v% : CALL Cleanup
  CASE 2 : v% = 20 : PRINT v% : CALL Cleanup
  CASE 3 : v% = 30 : PRINT v% : CALL Cleanup
END SELECT
```

## Today

Each arm emits its own `PRINT v%` and `CALL Cleanup`.

## Planned

```asm
Arm1:
    mov     word ptr [v], 000Ah
    jmp     Tail
Arm2:
    mov     word ptr [v], 0014h
    jmp     Tail
Arm3:
    mov     word ptr [v], 001Eh
Tail:
    ...                      ; PRINT v% : CALL Cleanup, once
```

## Equivalent BASIC

```basic
SELECT CASE k%
  CASE 1 : v% = 10
  CASE 2 : v% = 20
  CASE 3 : v% = 30
END SELECT
PRINT v% : CALL Cleanup
```

## What it needs

- The same byte-plus-fixup congruence test
  [O0040](O0040-identical-code-folding.md) uses, applied backwards from each
  arm's end until the sequences diverge.
- A size-versus-speed judgement: merging adds a taken jump per arm, so on a
  fetch-bound target it wins on size and can lose on time
  ([O0174](O0174-target-cost-models.md)). Natural fit for `$OPTIMIZE SIZE`.
