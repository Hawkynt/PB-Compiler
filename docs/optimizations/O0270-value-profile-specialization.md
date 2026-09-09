# O0270 — Value-profile specialization

| | |
|---|---|
| **Status** | ✅ Implemented in the IR middle-end for supplied value profiles; collection remains O0268 |
| **Stage** | Whole-program / IR middle-end |
| **IR** | `PowerBasic.Compiler/Ir/Passes/ValueProfileSpecialization.cs` |
| **Related** | [O0268](O0268-profile-collection.md), [O0160](O0160-call-site-cloning.md), [O0164](O0164-partial-evaluation.md), [O0304](O0304-guarded-specialization.md) |

## The idea

Record the **common runtime values** of selected arguments, then clone the
procedure for them. Where [O0018](O0018-interprocedural-constant-propagation.md)
requires *every* call site to agree at compile time, this needs only that one
value dominates at run time — and guards the specialized version with a test.

```basic
CALL Transform(mode%, data%)       ' mode% = 1 in 97% of executions
```

becomes, conceptually:

```basic
IF mode% = 1 THEN
    CALL Transform_mode1(data%)    ' mode is a constant inside this clone
ELSE
    CALL Transform(mode%, data%)
END IF
```

The clone is re-optimized with the argument known constant. Entire `IF mode%`
branches disappear, constant tables become visible, and the inliner gets a much
smaller body to work with.

## IR implementation

`ValueProfileSpecialization` consumes an argument histogram directly. O0268 still
owns instrumentation, profile files and stable profile identity; inventing a
temporary profile format in O0270 would only create a second representation that
has to be deleted when O0268 lands.

For each observed value above the default 90% share threshold the pass:

1. checks the callee against a 64-instruction clone budget and the ordinary
   opaque-body barriers (`ON ERROR` and inline assembly);
2. clones the function through `IrCloner`, keeping the original signature but
   binding the profiled formal parameter to the constant in the cloned body;
3. splits each visible direct call into an exact value guard, a specialized call
   and the original fallback call;
4. joins non-void results with a phi in the continuation block.

Keeping the original ABI is intentional. It makes the specialization a pure
middle-end transform: the call back ends need no special parameter convention,
and any indirect or external caller that the module cannot enumerate simply
continues to call the original function.

Integer and null-pointer profiles use ordinary exact equality. IEEE `f32`/`f64`
profiles compare the raw bit pattern rather than floating equality. That matters
for `+0.0` versus `-0.0` and for NaN payloads: the guard is true only for the
exact value substituted into the clone.

The histogram is allowed to contain only the hottest tracked values; their
counts do **not** have to sum to the total execution count. This matches LLVM's
`VP` metadata model. Code growth is bounded independently by both callee size
and a per-source-function specialization count.

## What it still needs

- [O0268](O0268-profile-collection.md) to feed these histograms automatically
  from `--profile-use`; until then the pass is an explicit module-level API and
  is deliberately not installed in `IrPassManager.Standard`.
- A target/profile policy may tune the default 90% profitability threshold and
  clone limits. The API exposes those as `Options`; the defaults stay
  conservative.
- Stale-profile validation belongs to [O0404](O0404-stale-profile-matching.md),
  before an `ArgumentProfile` is handed to this pass.

## References

- LLVM Language Reference, `VP` profile metadata: total execution count plus the
  hottest observed value/count pairs; tracked counts need not sum to the total.
- GCC `-fipa-cp-clone`: function cloning strengthens interprocedural constant
  propagation, with code growth explicitly called out as the main trade-off.

The implementation is independent C# built on PB-Compiler's existing
`IrCloner`; no external implementation code or dependency is used.
