# O0340 — Fused multiply-add contraction

| | |
|---|---|
| **Status** | ✅ Implemented as pattern-sensitive IR/LLVM contraction permission; target lowering decides whether an FMA exists |
| **Stage** | IR middle end + LLVM back end |
| **Gate** | Optimizer + `$OPTIMIZE SPEED` / `-OZF` |
| **IR** | `IrFastMathFlags.AllowContract`, applied by `FpFastMath`; emitted as LLVM `contract` |
| **Related** | [O0344](O0344-fp-reassociation.md), [O0347](O0347-mixed-precision.md), [docs/BACKENDS.md](../BACKENDS.md) |

## What is implemented

`FpFastMath` recognizes direct floating multiply-add and multiply-subtract dataflow:
an `FMul` consumed directly by an `FAdd` or `FSub`. When the SPEED objective is
active it marks only those participating operations with the precise
**contraction** permission. Standalone multiply/add/sub operations and divisions
do not acquire `AllowContract` merely because fast floating-point arithmetic is
enabled.

`LlvmEmitter` spells the permission as LLVM's `contract` fast-math flag, so the
LLVM target optimizer may form an FMA where its target and cost model support
one. LLVM explicitly keeps contraction separate from reassociation; O0344 owns
the latter freedom.

The IR does **not** invent an FMA instruction on the 16-bit x87 route. The x87
has no fused multiply-add operation, so there is nothing profitable to select
there; carrying the legality as an IR property keeps the middle end target
neutral.

```basic
DIM a!, b!, c!, r!
r! = a! * b! + c!
```

Under ordinary optimization the multiply and add carry no fast-math flags and
the two-rounding computation remains required. Under SPEED, the recognized pair
carries `AllowContract`, making the one-rounding contraction an explicit
permitted numerical change.

## Why the gate is semantic

An FMA rounds once while separate multiply/add rounds twice. The result can
therefore differ even when both answers are finite and close. This is not a
peephole that is safe to enable merely because a target has FMA hardware.

The permission follows the optimization objective rather than the source
dialect: strict optimization stays strict; SPEED grants the relaxed floating
contract. No external dependency is required.
