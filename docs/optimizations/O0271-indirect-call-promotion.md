# O0271 — Indirect call promotion

| | |
|---|---|
| **Status** | ✅ Done — the IR module pass `icp` consumes indirect-target profiles attached to `IrCall`, promotes one uniquely hottest profitable target to a guarded direct call, and retains the original indirect call as the fallback |
| **Stage** | IR whole-program |
| **IR** | ✅ `Ir/Passes/IndirectCallPromotion.cs`, registered before the module inliner in `IrPassManager.Standard()`; profile metadata lives in `IrIndirectCallProfile` / `IrCallProfileExtensions` |
| **Related** | [O0279](O0279-whole-program-devirtualization.md), [O0307](O0307-speculative-devirtualization.md), [O0268](O0268-profile-collection.md) |

## The idea

A `CALL DWORD` through a procedure pointer blocks direct-call optimizations. When a value profile shows
one target is the uniquely hottest profitable candidate, split the call site into a guarded direct arm
and the original indirect fallback:

```basic
IF target = CommonHandler THEN
    r& = CommonHandler(x&)
ELSE
    r& = target(x&)
END IF
```

The direct arm is now visible to the existing inliner and subsequent SSA passes. The miss arm is the
original `IrCall`, with the same callee value, arguments and calling convention, so correctness does not
depend on the profile being right.

For non-void calls the two results meet in a continuation phi. Splitting the block also repairs phi
predecessor labels on the old successors, exactly as the ordinary inliner does when it moves a
terminator into a continuation block.

## Profitability and safety

The first-target threshold follows LLVM's default indirect-call-promotion profitability rule: the
candidate must account for at least 30% of executions at the site. O0271 deliberately emits only one
guard, so it additionally requires that the hottest count is unique instead of using profile-list order
to break a tie.

Promotion is declined when:

- the call is already direct or has no target profile;
- the hottest target is below the threshold or tied with another target;
- the profiled function does not belong to the current module;
- return type, fixed argument count or fixed argument storage types do not match the call site;
- the caller has an error handler or inline assembly, whose hidden control/frame effects make CFG
  rewriting unsafe.

The fallback's profile metadata is cleared after promotion, making repeated module sweeps idempotent.

## Applies to

```basic
DIM handler AS FUNCTION(LONG) AS LONG
r& = handler(x&)
```

The current pass is the **consumer** half. Profile collection, serialization/loading and stable call-site
identity are still [O0268](O0268-profile-collection.md); a producer can attach resolved target/count data
to the lowered `IrCall` through `IrIndirectCallProfile` without changing this transform.

## Validation

`IndirectCallPromotionTests` covers the guarded direct/fallback shape, the 30% boundary, tied targets,
stale module/signature data, successor-phi repair, opaque callers, repeated-sweep idempotence, standard
pipeline registration and composition with `Inliner`.

## Reference model

The behavior and profitability policy were derived from LLVM's documented value-profile metadata and
its `IndirectCallPromotion` / `promoteIndirectCall` interfaces. LLVM is Apache-2.0 WITH LLVM-exception;
this implementation is independently written against PB-Compiler's IR and does not copy LLVM source.
