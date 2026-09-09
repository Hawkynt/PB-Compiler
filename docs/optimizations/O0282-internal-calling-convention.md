# O0282 — Internal calling-convention specialization

| | |
|---|---|
| **Status** | 🟡 Partial — private ABI selection is now per procedure and includes one-word BYREF near pointers; wider argument/result shapes remain planned |
| **Stage** | Whole-program + emitter |
| **Source** | `CodeGen/OptRegParm.cs`, `CodeGen/CodeGenerator.Procs.cs` |
| **Verified by** | `PowerBasic.Compiler.Tests/CodeGen/InternalCallingConventionSpecializationTests.cs`, `CallingConventionTests.cs` |
| **Gate** | Optimizer + `$OPTIMIZE SPEED`; self-contained program only |
| **Related** | [O0021](O0021-register-parameters.md), [O0069](O0069-dead-parameter-elimination.md), [O0169](O0169-returned-condition-propagation.md), [O0070](O0070-leaf-frame-elision.md), [O0279](O0279-whole-program-devirtualization.md) |

## The idea

When the compiler owns every call site, the calling convention is an
implementation detail it may choose **per procedure**:

- leading word-sized `BYVAL` scalars in registers — the original
  [O0021](O0021-register-parameters.md) case;
- one-word BYREF near pointers in those same private register slots — implemented here;
- LONG, float and far-pointer arguments in register **pairs**;
- unused arguments dropped ([O0069](O0069-dead-parameter-elimination.md));
- **multiple** return values in registers (a natural fit for `pb36` tuples);
- a Boolean result returned **in the flags**
  ([O0169](O0169-returned-condition-propagation.md));
- BYREF collapsing to direct access after inlining.

The current implementation reuses the already interoperable WATCALL machinery:
AX, DX, BX and CX carry the leading one-word ABI values and overflow stays on the
stack. The new O0282 part is the private-ABI **policy**, not a second public
calling convention.

## Implemented ownership policy

`OptRegParm` now decides independently for each BASIC procedure rather than
turning register specialization off for the whole module when one procedure
escapes:

- only in-module BASIC definitions are candidates;
- explicit source conventions are never replaced;
- capturing closures are excluded because BX:CX carries their environment;
- a procedure must have at least one real direct call site, otherwise changing
  its ABI cannot remove call traffic;
- `CODEPTR`/`CODESEG`/`CODEPTR32` fences the **referenced procedure**, while an
  unrelated fully-owned procedure may still specialize;
- a typed procedure-pointer invocation remains a conservative module-wide fence
  because `SemanticModel` currently records its signature, not a complete target
  set. O0279 can eventually discharge that fence when it proves the target set;
- separately compiled/linkable programs retain the public stack ABI because
  outside callers are not visible.

Caller and callee still change together through the same `ProcedureSymbol`:
`EmitCall` stages the WATCALL arguments and `LayoutFrame`/`BeginFrame` consumes
that convention at the definition. No source-visible declaration is rewritten.

## Applies to the implemented slice

```basic
$OPTIMIZE SPEED

SUB Bump(value AS INTEGER)
  INCR value
END SUB

DIM n AS INTEGER
n = 41
Bump n
PRINT n
```

`value` is BYREF, so the ABI value is its one-word near pointer. For a fully-owned
`Bump`, that pointer can travel in AX instead of being pushed as a stack word;
the define-side WATCALL prologue spills AX into the parameter slot before the
body dereferences it. Reference semantics are unchanged.

The wider intended O0282 shape remains, for example:

```basic
FUNCTION DivMod(BYVAL a&, BYVAL b&) AS (LONG, LONG)
DIM q&, r&
q&, r& = DivMod(x&, y&)
```

— here wider input register pairs and multi-register aggregate returns are still
future work.

## Still planned

- Costed register-pair assignment for LONG, SINGLE/DOUBLE and far-pointer
  arguments instead of the current one-word WATCALL subset.
- Routed x86-backend definitions for register arguments; the routed prologue
  currently consumes stack parameter cells, so register-convention procedures
  deliberately remain on the direct emitter path.
- Multi-register / tuple returns coordinated with O0281.
- Returned-condition / flags conventions coordinated with O0169.
- Call-shape shrinking and dead parameters coordinated with O0069.
- Removing the typed-procedure-pointer global fence once O0279 can prove a
  complete indirect target set.

## References and licensing

The implementation is clean-room; no external implementation code is copied or
translated. Open Watcom's documented 16-bit register convention was used as the
ABI reference for AX/DX/BX/CX argument order and stack overflow behavior. LLVM's
calling-convention model was consulted for the invariant that a private calling
convention is valid only when caller and callee agree. Both are behavioral/design
references; PB-Compiler continues to use its own existing WATCALL implementation.
