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

## Refinement: bounded-range fold

When the two sides are a **lower and an upper bound on the same 16-bit signed
variable against constants** — `x% >= 0 AND x% <= 15`, in any order and with the
constant on either side — the pair collapses to a single **unsigned** compare
(`TryEmitRangeCheckBranch`):

```asm
    mov     ax, [x]
    cmp     ax, 000Fh        ; (x - lo) <=u (hi - lo); lo = 0 here so no subtract
    ja      EndIf            ; unsigned above the window -> out of range
    ...                      ; PRINT "in range"
EndIf:
```

`(x - lo)` read as unsigned is in `[0, hi-lo]` exactly when `x ∈ [lo, hi]`: a value
below `lo` wraps to a large unsigned number and a value above `hi` exceeds the
window, so one `JA`/`JBE` decides both bounds. `x` is evaluated once. One subtract
and one branch replace two signed compares and two branches. Gated on `--optimize`
(the faithful build keeps the two-compare chain byte-for-byte); verified by a
self-differential DOSBox run of four range forms — `>=0 AND <=15`, a non-zero low
bound, a negative low bound, and the constant-on-the-left spelling — over the whole
input range, identical to `$OPTIMIZE OFF`, plus a regression test that the window
compare takes the unsigned `JA` rather than the signed `JG`.

## Why it is safe

Over comparison results — which are always exactly −1 or 0 — the bitwise
`AND`/`OR` **equal** the logical operators, so the branch chain computes the same
condition. Skipping the second operand is unobservable only if evaluating it had
no effect, which `IsPure` guarantees: no calls, no array index a `$ERROR BOUNDS`
trap could raise, no intrinsics. So `IF x AND 3` — a genuine bitwise mask — never
qualifies; only a tree of comparisons does.
