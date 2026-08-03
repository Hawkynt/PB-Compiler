# O0019 — Definite-assignment zero elision

| | |
|---|---|
| **Status** | ✅ Implemented for stack frames; array/heap zero-fill elision is [O0068](O0068-array-zero-fill-elision.md) |
| **Stage** | Emitter (frame prologue) |
| **Source** | `CodeGen/CodeGenerator.Optimize.cs` — `#region O19`, `CanElideFrameZeroing` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF22.BAS` (locals must read `0`/`""` before assignment on every call, with and without elision) |
| **Related** | [O0002](O0002-dead-code-elimination.md), [O0070](O0070-leaf-frame-elision.md) |

## What it is

PowerBASIC guarantees that `LOCAL`s start at 0 / `""` on **every** invocation,
so the prologue zero-fills the whole frame with a `REP STOSW`. When a
straight-line proof shows that no local is ever read before it is assigned, that
fill is unobservable and disappears.

The proof walks a leading prefix of the body accepting whole-variable
assignments and `FOR` headers, and aborts at the first call, branch, label or
read of a still-unassigned local. Dynamic-string handle slots keep their
individual zeroing regardless — their first assignment frees the previous
handle. Main-program frames (temps only) always qualify when no error handler
exists.

## Sample

```basic
SUB Work
  LOCAL a%, b%, c%
  a% = 1
  b% = 2
  c% = a% + b%
  PRINT c%
END SUB
```

## Without the optimizer

```asm
Work:
    push    bp
    mov     bp, sp
    sub     sp, 0006h
    push    ss
    pop     es
    lea     di, [bp-6]
    mov     cx, 0003h
    xor     ax, ax
    rep     stosw            ; every invocation, whether needed or not
    ...
```

## With the optimizer

```asm
Work:
    push    bp
    mov     bp, sp
    sub     sp, 0006h
    ...                      ; the fill is gone; every local is written first
```

## Equivalent BASIC

```basic
SUB Work
  LOCAL a%, b%, c%          ' the zero-initialization guarantee is unobservable here
  a% = 1 : b% = 2 : c% = a% + b%
  PRINT c%
END SUB
```

## Why it is safe

- The proof is a **prefix** proof: it aborts at the first construct it cannot
  reason about, so anything with a jump into the middle of the body keeps the
  fill.
- An `ON ERROR` handler can re-enter the frame and observe unassigned locals, so
  a body with error handling never qualifies.
- String and FLEX handle slots keep their zeroing: a non-zero handle would be
  freed as if it were a live allocation.

## Limits

Skipping an array's allocation zero-fill needs a loop-fill dominance proof over
the value lattice — [O0068](O0068-array-zero-fill-elision.md). Dropping the BP
frame entirely for leaf procedures is [O0070](O0070-leaf-frame-elision.md).
