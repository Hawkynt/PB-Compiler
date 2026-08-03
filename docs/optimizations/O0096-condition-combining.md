# O0096 — Adjacent and nested condition combining

| | |
|---|---|
| **Status** | ✅ Implemented — the nested form already collapses. `IF a>0 THEN IF b>0 THEN body` compiles to `CMP a,0 / JLE end / CMP b,0 / JLE end / body` with both arms targeting the one exit label (no intermediate block, no arm-closing jump), because branch fusion, fall-through and jump threading merge the nest just as [O0032](O0032-short-circuit-conditions.md) merges the operator form. Verified in the emitted x86 |
| **Stage** | Emitter |
| **Related** | [O0032](O0032-short-circuit-conditions.md), [O0031](O0031-branch-fusion.md), [O0097](O0097-repeated-comparison-elimination.md) |

## The idea

```basic
IF a% > 0 THEN
  IF b% > 0 THEN ...
END IF
```

is the same control flow as `IF a% > 0 AND b% > 0 THEN …`, but written as two
nested blocks it goes through an intermediate block with its own arm-closing
jump. Merging the nest into one branch chain is the structural counterpart of
[O0032](O0032-short-circuit-conditions.md), which handles the operator form.

## Applies to

```basic
DIM a%, b%
IF a% > 0 THEN
  IF b% > 0 THEN PRINT "both"
END IF
```

## Today

```asm
    cmp     word ptr [a], 0000h
    jle     End1
    cmp     word ptr [b], 0000h
    jle     End2
    ...
End2:
    jmp     End1             ; the inner block's closing jump
End1:
```

## Planned

```asm
    cmp     word ptr [a], 0000h
    jle     End
    cmp     word ptr [b], 0000h
    jle     End
    ...
End:
```

## What it needs

- Recognition of an `IF` whose *entire* body is another `IF` with no `ELSE`,
  which is a purely syntactic test on the bound tree.
- The same purity requirement as [O0032](O0032-short-circuit-conditions.md) is
  **not** needed here: the nesting already short-circuits, so no evaluation is
  skipped that was not skipped before. Only the block structure changes.
- Composes with [O0093](O0093-jump-threading.md), which would remove the same
  jump later at the byte level — doing it at the emitter is cheaper and also
  merges the two arm-end labels.
