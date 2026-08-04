# O0249 — Branchless absolute value

| | |
|---|---|
| **Status** | ✅ Done (the `ABS()` intrinsic and the `IF x < 0 THEN x = -x` spelling, on a 16-bit value) |
| **Stage** | Emitter |
| **Related** | [O0108](O0108-branchless-select.md), [O0077](O0077-negation-idioms.md), [O0258](O0258-vector-abs.md) |
| **Split from** | [O0108](O0108-branchless-select.md) |

## The idea

`IF x < 0 THEN x = -x` — and the `ABS()` intrinsic — lower to the classic
three-instruction sequence with no branch at all:

```asm
    cwd                      ; DX = sign mask
    xor     ax, dx
    sub     ax, dx
```

## Applies to

```basic
DIM x%
IF x% < 0 THEN x% = -x%
PRINT ABS(x%)
```

## Now

Both the `ABS()` intrinsic (`EmitIntrinsic`) and the explicit
`IF x < 0 THEN x = -x` diamond — either operand order, no `ELSE`, over a 16-bit
signed variable (`TryEmitBranchlessAbsIf` in `EmitIf`; declines a register-resident
`x`) — emit the branchless `cwd; xor ax,dx; sub ax,dx` under `--optimize` (three bytes, no
branch, faster on average than the taken `JNS`). It is **bit-identical** to the
`test; jns; neg` form for every input — the `-32768` case returns `-32768` in
both, since its absolute value is not representable — verified by a
self-differential run and a differential checksum over `-30000…30000` byte for
byte against the genuine oracle, plus a regression test for the emitted sequence.

Gated on `--optimize` (the faithful path keeps `test/jns/neg`, byte-identical to
genuine) and on **not** `$ERROR OVERFLOW` — there the negation's overflow trap
must survive, which the mask form cannot raise, so the branching path stays.

Native-only. The IR back ends emit an `abs`/`select` the host C compiler and LLVM
lower to their target's branchless idiom.

## Still planned

- The 32-bit `LONG` branchless form (a `SAR`-mask blend across `DX:AX`), and the
  register-resident `x` case (abs in place on the SI/DI resident rather than the
  cell).
