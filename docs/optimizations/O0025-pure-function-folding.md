# O0025 — Pure-function compile-time evaluation

| | |
|---|---|
| **Status** | ✅ Implemented (integer subset, v1) |
| **Stage** | Whole-program analysis + emitter |
| **Source** | `CodeGen/OptPureFold.cs` — `ClassifyPure`, `Evaluator` |
| **Gate** | `--optimize` |
| **Sample** | [`docs/decompilation/optimizations/30-pure-function-folding.bas`](../decompilation/optimizations/30-pure-function-folding.bas) |
| **Related** | [O0001](O0001-constant-folding.md), [O0018](O0018-interprocedural-constant-propagation.md), [O0022](O0022-dead-procedure-elimination.md) |

## What it is

The compiler **infers** purity instead of requiring a `CONSTEXPR` keyword. A
`FUNCTION` whose result depends only on its `BYVAL` arguments — no I/O, no
global/`SHARED` reads, no `BYREF`, no side-effecting statements, and calling only
other pure functions — is pure. Purity is a greatest fixed point over the call
graph, so mutually-recursive pure helpers all qualify.

When such a function is called with **all-constant arguments**, a small
tree-walking interpreter executes its body and the call is replaced by the
resulting literal. The frame, the `CALL`/`RET` and the whole computation vanish;
once no caller references it, [O0022](O0022-dead-procedure-elimination.md)
purges the body.

## Sample

```basic
FUNCTION Fact&(BYVAL n%)
  LOCAL i%, r&
  r& = 1
  FOR i% = 2 TO n%
    r& = r& * i%
  NEXT
  Fact& = r&
END FUNCTION

PRINT Fact&(10)
```

## Without the optimizer

```asm
    mov     ax, 000Ah
    push    ax
    call    Fact             ; ten iterations at run time
    ...
Fact:
    push    bp
    mov     bp, sp
    ...                      ; loop, multiply, result slot, epilogue
    ret     2
```

## With the optimizer

```asm
    mov     ax, 9800h        ; 3628800 low word
    mov     dx, 0037h        ; high word
    call    rt_print_i32
```

…and `Fact` is not emitted at all.

## Equivalent BASIC

```basic
PRINT 3628800
```

## Why it is safe

- The interpreter wraps every intermediate to its node's static type via
  `WrapToType`, exactly as the runtime ALU would at each operation width, so the
  folded value is **bit-identical** to the executed result — a 16-bit `INTEGER`
  product silently wraps in the interpreter too.
- Integer division or `MOD` by zero, an unmodelled operator, a non-constant
  sub-expression, or exhausting the step (500 000) / recursion (64) budget
  simply **abandons** the fold and emits the genuine call.
- Folding a call to its provably-equal value cannot alter observable behavior,
  which is why the whole `pb36` differential battery is unchanged by this pass.

## Limits (v1 subset)

Integer-typed functions, parameters and locals only, with
`IF`/`SELECT CASE`/`FOR`/`DO-LOOP`/`WHILE` and `EXIT FUNCTION`/`FOR`/`DO`.
Floats, strings, arrays, pointers and intrinsics keep the real call — a roadmap
extension.
