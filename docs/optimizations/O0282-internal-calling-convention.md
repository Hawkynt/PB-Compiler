# O0282 — Internal calling-convention specialization

| | |
|---|---|
| **Status** | 🟡 Partial — private ABI selection is per procedure and covers word integers and one-word BYREF near pointers; wider argument/result shapes remain planned |
| **Stage** | Whole-module IR + x86 back end (emission) |
| **Source** | `Ir/Passes/PrivateCallingConvention.cs` (run last in `IrMiddleEndPipeline.RunNativeModule`); `Backend/MachineEmitter.cs` — `EmitFunction` (register-argument moves) |
| **Verified by** | `PowerBasic.Compiler.Tests/Ir/PrivateCallingConventionTests.cs`, `CallingConventionTests.cs` |
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

`PrivateCallingConvention` runs on the IR once the call graph is final (after
whole-program dead-code removal, under `$OPTIMIZE SPEED`) and decides independently
for each procedure:

- only in-module definitions with the BASIC convention are candidates; explicit
  source conventions are never replaced, and the module entry keeps its ABI;
- every parameter must be one word — a 16-bit integer or a near pointer (BYREF);
- a procedure must have at least one direct call site, otherwise changing its
  ABI cannot remove call traffic;
- a procedure whose address is taken (a far entry for a delegate, or any use that
  is not the callee of a direct call) keeps the stack ABI, while an unrelated
  fully-owned procedure may still specialize;
- any indirect call in the module is a module-wide fence, because its target set
  is not known. O0279 can eventually discharge that fence when it proves the
  target set;
- inline assembly anywhere is also a module-wide fence, since a text `CALL` is a
  caller the IR cannot see;
- units and separately linked programs retain the public stack ABI because
  outside callers are not visible (`IrModule.OwnsProcedureAbi`).

The definition and every call site are respecified to `IrCallConvention.Watcall`
together, and the back end reads both from the IR. At the definition,
`MachineEmitter.EmitFunction` moves the register arguments straight into their
allocated registers when nothing needs them in the frame, and otherwise spills
them below BP and reads them back like stack parameters. No source-visible
declaration is rewritten.

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
the definition either keeps it in its allocated register or spills AX into a
frame slot before the body dereferences it. Reference semantics are unchanged.

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
- Multi-register / tuple returns coordinated with O0281.
- Returned-condition / flags conventions coordinated with O0169.
- Call-shape shrinking and dead parameters coordinated with O0069.
- Removing the indirect-call global fence once O0279 can prove a complete
  indirect target set.

## References and licensing

The implementation is clean-room; no external implementation code is copied or
translated. Open Watcom's documented 16-bit register convention was used as the
ABI reference for AX/DX/BX/CX argument order and stack overflow behavior. LLVM's
calling-convention model was consulted for the invariant that a private calling
convention is valid only when caller and callee agree. Both are behavioral/design
references; PB-Compiler continues to use its own existing WATCALL implementation.
