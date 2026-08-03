# O0032 — Short-circuit `AND` / `OR` / `NOT` conditions

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.cs` (condition lowering), `IsPure` |
| **Gate** | `--optimize` |
| **Verified by** | oracle-verified byte-identical (`IF x>0 AND x<100`, triple `OR`, `NOT(AND)`); scenario `AndOrConditionShortCircuits` |
| **Related** | [O0031](O0031-branch-fusion.md), [O0016](O0016-value-fact-analysis.md) |

## What it is

PowerBASIC's `AND`/`OR` are **bitwise** operators, so `IF x > 0 AND x < 100`
literally means "materialize −1/0 for each comparison, bitwise-AND them, test the
result" — four instructions of truth-value machinery per operand plus a
`PUSH`/`POP` to stage the two.

When the whole condition of an `IF`/`WHILE`/`UNTIL`/ternary is an `AND`/`OR`/
`NOT` tree of **comparisons over pure operands**, it is lowered into a chain of
branches instead. `NOT` inverts the branch sense rather than materializing a
value to negate.

## Sample

```basic
DIM x%
IF x% > 0 AND x% < 100 THEN PRINT "in range"
```

## Without the optimizer

```asm
    mov     ax, [x]
    cmp     ax, 0000h
    jle     F1
    mov     ax, 0FFFFh
    jmp     H1
F1: mov     ax, 0000h
H1: push    ax
    mov     ax, [x]
    cmp     ax, 0064h
    jge     F2
    mov     ax, 0FFFFh
    jmp     H2
F2: mov     ax, 0000h
H2: mov     bx, ax
    pop     ax
    and     ax, bx
    test    ax, ax
    jz      EndIf
    ...
EndIf:
```

## With the optimizer

```asm
    cmp     word ptr [x], 0000h
    jle     EndIf
    cmp     word ptr [x], 0064h
    jge     EndIf
    ...                      ; PRINT "in range"
EndIf:
```

Exactly what a person writes by hand — and the second compare is skipped
entirely when the first fails.

## Equivalent BASIC

```basic
IF x% > 0 THEN
  IF x% < 100 THEN PRINT "in range"
END IF
```

or, in `pb36`, `IF x% > 0 ANDALSO x% < 100 THEN …`.

## Why it is safe

Over comparison results — which are always exactly −1 or 0 — the bitwise
`AND`/`OR` **equal** the logical operators, so the branch chain computes the same
condition. Skipping the second operand is unobservable only if evaluating it had
no effect, which `IsPure` guarantees: no calls, no array index a `$ERROR BOUNDS`
trap could raise, no intrinsics. So `IF x AND 3` — a genuine bitwise mask — never
qualifies; only a tree of comparisons does.
