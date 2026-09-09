# O0307 — Speculative devirtualization

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Whole-program IR |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` |
| **IR** | `PowerBasic.Compiler/Ir/Passes/SpeculativeDevirtualization.cs` |
| **Verified by** | `PowerBasic.Compiler.Tests/Ir/O0307MiddleEndTests.cs` |
| **Related** | [O0271](O0271-indirect-call-promotion.md), [O0279](O0279-whole-program-devirtualization.md), [O0304](O0304-guarded-specialization.md) |

## The idea

Where the target set of an indirect call is **not** provably complete, optimize
for the likely target anyway and keep the indirect call as the fallback. Unlike
[O0271](O0271-indirect-call-promotion.md), the guess need not come from a
profile: a static heuristic is enough.

The IR implementation versions the call site as:

```text
if target = Candidate then
    result.fast = call Candidate(args...)
else
    result.slow = call target(args...)
result = phi(result.fast, result.slow)
```

`VOID` calls need no phi. The original `IrCall` is moved into the fallback block
rather than replaced, so an incorrect heuristic changes performance only, never
which procedure executes.

## Static candidate heuristic

A candidate must have the same return type and fixed parameter types as the
indirect call, and its function address must actually be used as data somewhere
in the module. O0307 then chooses:

1. exactly one such candidate whose address is used in the caller; otherwise
2. exactly one such candidate module-wide.

Ambiguity is a hard decline. This covers the useful unprofiled cases — notably a
procedure address assigned in the same procedure — without pretending that the
set is complete. The pass recognizes its own guard/fallback shape, so running the
module pipeline again does not recursively version the fallback.

The transform is gated to `$OPTIMIZE SPEED`: it deliberately buys a likely direct
call with an extra compare, branch and duplicated call site. The direct path is
created before the SPEED inliner module pass, so a profitable candidate can then
be inlined while the cold indirect path remains available.

## Applies to

```basic
DIM f AS FUNCTION(LONG) AS LONG
f = CODEPTR32(Double&)
' ... f may be reassigned somewhere the compiler cannot see
PRINT f(21)
```

```text
if f = CODEPTR32(Double&) then  <direct Double&>  else  call [f]
```

## Correctness details

- The target comparison is pointer equality against the candidate symbol.
- Calling convention, return type and argument values are copied to the direct path.
- Existing instructions after the call move to a continuation block.
- Successor phi predecessor labels are repaired when the original terminator moves.
- Functions with PB error-handler edges or inline assembly are skipped, matching
  the middle-end's existing opaque-function rule.
- No profile metadata or third-party dependency is introduced.

## References

The implementation is clean-room and uses LLVM only as a behavioral reference:
its indirect-call promotion/devirtualization machinery versions an indirect call
behind a target comparison and preserves an indirect fallback when the check
fails. LLVM is Apache-2.0 WITH LLVM-exception; no LLVM implementation code was
copied or translated.
