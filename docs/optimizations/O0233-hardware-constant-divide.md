# O0233 — Hardware divide for constant divisors

| | |
|---|---|
| **Status** | 🟡 Partial — the inline form is not implemented on the IR path; the 386 build of `rt_ldiv`/`rt_lmod` divides in hardware for every divisor |
| **Stage** | x86 back end (instruction selection) + runtime |
| **Source** | `Backend/InstructionSelector.cs` — `SelectWideDivide` (always calls `rt_ldiv`/`rt_lmod`); `Runtime/DosRuntime.Math.cs` — `EmitLongDivide` |
| **Gate** | `--optimize` + `$CPU 80386`, constant divisor with \|d\| ≥ 2 |
| **Verified by** | `tests/diff/DIFF71.BAS` |
| **Split from** | [C0001](C0001-386-codegen.md) |

## What it is

A `LONG` divide or modulo by a compile-time-constant divisor of magnitude ≥ 2
uses the hardware `IDIV`/`DIV` instead of the `rt_ldiv`/`rt_lmod` runtime
routines. The hardware truncates toward zero and takes the dividend's sign for
the remainder — which is exactly PB's `\` and `MOD`.

Not implemented inline on the IR path; the syntax-level version was retired with
the direct emitter. The instruction selector lowers every 32-bit `SDiv`/`SRem` to
a call to `rt_ldiv`/`rt_lmod`. On an optimized 386+ target those routines take a
hardware path themselves: after the divide-by-zero check they load the operands
into EAX/EBX and run `CDQ` + `IDIV EBX`, falling back to the word-at-a-time
routine only for `MININT \ -1`. The call and the operand staging remain, so the
"with" listing below describes the planned inline form.

## Sample

```basic
$CPU 80386
DIM n&, q&
q& = n& \ 10
```

## Without / with

```asm
    mov     ax, [n]          ; without: a runtime long-division routine
    mov     dx, [n+2]
    mov     bx, 000Ah
    xor     cx, cx
    call    rt_ldiv

    mov     eax, [n]         ; with
    cdq
    mov     ecx, 0000000Ah
    idiv    ecx
```

## Why it is safe

The `|d| >= 2` gate rules out both hardware traps: divide-by-zero and the
`MININT \ -1` overflow. So the runtime path is dropped **only** where it could
not have trapped, and the Error-11 guard is unnecessary by construction.
Variable divisors keep `rt_ldiv`/`rt_lmod`.
