# Direct-emitter retirement

The DOS compiler is being migrated to one production path:

```text
source -> parser/binder -> typed SSA IR -> middle-end -> x86-16 machine IR -> assembler/linker
```

`CodeGen/CodeGenerator*.cs` currently contains two different kinds of code which must not be deleted together:

1. the **legacy direct emitter**, which lowers bound syntax straight to x86 while performing target-specific optimizations; and
2. **whole-program DOS infrastructure** shared by the routed back end: image/data layout, runtime selection, OMF/PBU/PBL linking, labels, literal pools and executable construction.

The first is the retirement target. The second remains until equivalent target-facing infrastructure has been separated from the legacy syntax emitter.

## Removal gates

All gates below must be green in both optimized and `--no-optimize` modes before the direct emitter is removed.

### 1. No semantic fallback

Every source body accepted by the DOS compiler must either lower and select through the IR path or produce the same front-end diagnostic it produced before. `BackendDeclines` must be empty for every non-external body in the declarative routing gate and in the corpus.

The remaining pinned blockers are:

- assignment into a **string** array parameter (the element address escapes to near-pointer string routines — see the array-parameter entry below);
- `REDIM` / `ERASE` **through** an array parameter (the callee holds a widened copy of the caller's descriptor);
- **8086 register pressure** in a routed body — not an ABI gap and not a construct, but the one remaining class that turns into a compile failure on deletion rather than a fallback. A body combining two descriptor bound reads with a read-modify-write of an element already exceeds the allocator (`allocation: no register assignment, and nothing left that can move to memory`). Every descriptor field is widened to i32 while the descriptor holds words, which doubles the live values on a four-register machine; narrowing that arithmetic is the obvious first move.

A row leaves this list only when a focused routing test also executes the routed image and proves observable equivalence.

Closed so far:

- CDECL/STDCALL procedure definitions — the selector already emitted right-to-left stack arguments; definition-side routing takes `LayoutFrame`'s matching offsets, and CDECL emits a bare `RET` because its caller owns cleanup while STDCALL keeps `RET n`. Proven by `BackendStackConventionRoutingTests`, which executes routed against direct in both optimizer modes, and by the recursive convention case in `CallingConventionTests`.
- BYREF record parameters — a record crosses the call as one near pointer and its layout never crosses the boundary, so member uses lower to ordinary typed GEP/load/store against the caller's storage. Proven by `BackendRecordParameterRoutingTests`, which covers member offsets and write-back through the pointer.
- EXT procedure ABI — the complete parameter/result ABI is now represented on the routed path. A BYVAL EXT argument is staged at its declared ten-byte width with `FSTP TBYTE` and pushed as five words from high to low; the callee addresses that caller-owned argument through a TBYTE `ParamCell`; and an EXT result leaves the function in ST(0). This is the same representation already used by the direct emitter, so no conversion format or second calling convention is introduced. `BackendExtendedParameterRoutingTests` executes two ten-byte parameters plus an EXT result against the direct path in both optimizer modes; the selector's stack-call pusher now closes the former call-side gap as well.
- BYTE procedure ABI — BYTE/SBYTE retain PowerBASIC's word-sized call slot while the value itself is the low byte. Routed calls materialize that word before `PUSH`, routed definitions read the same slot, and byte FUNCTION results cross in AL. The routing gate uses 200 rather than a tiny value so unsigned BYTE semantics are observable rather than accidentally identical to INTEGER.
- QUAD procedure ABI — a BYVAL QUAD remains eight signed-integer bytes on the stack and is copied into the routed backend's existing qword SSA cell on first use. Calls push the four words high-to-low. PowerBASIC's QUAD result channel is x87 ST(0), so routed returns use `FILD qword` and routed callers immediately recover the integer with `FISTP qword`; every signed 64-bit integer is exact in the x87 extended significand.
- BYVAL FIX procedure parameters — FIX crosses the stack as the raw scaled signed i64 cell already used by routed FIX storage, not as an IEEE value. The callee performs the ordinary `rt_fix_down` read conversion, so the runtime-owned scale remains authoritative. `BackendFixBcdTests.Route_GivenByValFixParameter_ThenTheScaledQwordCrossesTheProcedureBoundary` sets `pbvFixDigits = 4`, observes `2.4692`, and compares routed execution with the direct emitter. A FIX FUNCTION result crosses at the other representation entirely; see its own entry below.
- FASTCALL/WATCALL procedure definitions — the call side already placed leading arguments in AX,DX,BX(,CX) via `X86CallAbi`. The definition side needed only the other half of that contract: `LayoutFrame` had always assigned those parameters negative frame cells (`[BP-2]`, `[BP-4]`, …) and left them to a prologue spill that only the direct emitter performed. The routed prologue now pushes the same registers in parameter order, and `MachineEmitter` starts its own stack slots below that reservation so an alloca can never be handed parameter 0's address. From there they are ordinary frame parameters, so nothing after the prologue knows the convention was a register one, and `RET n` is already correct because `paramBytes` counts only the stack parameters. A multiword BYVAL argument stays rejected on both paths — splitting one value across a register pair needs per-compiler rules that neither emitter models — which makes it a front-end diagnostic rather than a routing class. Proven by `BackendRegisterConventionRoutingTests`, which executes routed against direct in both optimizer modes over five arguments (overflowing both register files), BYREF write-back, and a body with its own array storage sharing the frame; and by the recursive case in `CallingConventionTests`, where the callee reuses the very registers its own arguments arrived in.
- FIX FUNCTION results — a FIX result crosses at the opposite representation from a FIX argument, and the asymmetry is the direct emitter's rather than a choice. An argument travels as the raw scaled i64 cell; a result is converted by the callee's epilogue (`FILD qword` then `rt_fixdn`) and returned as the NUMERIC value in ST(0), which the caller then re-quantizes with `rt_fixup` where it stores it. The routed path previously returned the raw cell, a second and incompatible convention that would have made a routed callee and a direct caller disagree by ten to the `pbvFixDigits` power. It now declares the F80 result, converts in `ReturnFromFunction`, and scales straight back at the call site so the surrounding FIX-typed expressions still see the cell — the same down/up pair the direct emitter emits, neither half foldable because the exponent is a runtime cell. `BackendFixBcdTests.Route_GivenFixFunctionResult_ThenTheNumericValueCrossesInSt0` moves `pbvFixDigits` to four before the calls and uses an argument not representable at two places, in both optimizer modes.
- Array parameters — an array crosses as one near pointer to a DESCRIPTOR, never as element storage, and the descriptor's layout is the direct emitter's (`+0` segment, `+2` data offset, `+4` element size, `+6` rank, then a word lower bound and extent per dimension). That choice is what makes a mixed image work: the block belongs to the caller, so its shape is settled by the ABI rather than by whichever emitter compiled the callee. A routed caller fills a fresh block at the call site — which is also what the direct emitter does for a static array's shadow descriptor — and a routed callee widens the fields into an ordinary frame descriptor on entry, after which element addressing and `LBOUND`/`UBOUND` work unchanged. The block address stays two separate words rather than becoming a pointer, because the routed backend's far pointers all live in the single `rt_arrseg` heap segment while a static array's storage is in `DS`; a composed far pointer is an address former with no register to hold it, so the pair is combined at each use. `BackendArrayParameterRoutingTests` executes static, dynamic, two-arrays-through-one-parameter, forwarding a parameter onward, and a string-element read against the direct build in both optimizer modes. The paged and `ABSOLUTE` classes decline as arguments: their element addresses are not one segment plus one offset, so there is no data pointer the block could carry.
- `CHAIN` in the module body — the IR already lowered both halves of the handoff (`LowerChain` streams the COMMON block out, `LowerChainCommonLoad` absorbs it at the head of the body), and the filter that refused it only ever matched a **top-level** `ChainStmt`. A `CHAIN` inside an `IF` was therefore already routing and already passing `BackendChainTests`, handoff bytes included, so the filter was removing the one shape that differed by nesting alone. `Run_GivenATopLevelChain_ThenTheModuleBodyStillRoutesAndAgreesWithTheDirectEmitter` runs both passes and compares the routed handoff file with the direct one byte for byte.
- Procedure-local error handling — `ON ERROR` / `RESUME` / `TRY` already lower to inline handler intrinsics. `ProcedureErrorHandlerPreservation` adds the procedure-boundary ABI rule the module body does not need: save the caller's `rt_onerr`/`rt_onerr_bp`/`rt_onerr_sp` triple in this invocation's frame and restore it before every `RET`. `BackendProcedureErrorHandlerRoutingTests` executes both normal return and an inner handled fault while proving the outer caller trap is restored, with optimization on and off.

BYVAL records are deliberately absent from both lists: the direct emitter refuses them as well ("not yet generated: load of UdtType"), so they are a front-end gap rather than a routing class, and they do not block retirement.

### 2. One owner for program state

The temporary mixed routed/direct architecture has duplicate representations for some state (for example DATA cursors and shared dynamic-array descriptors). Full ownership must make those single-source again. Split-routing guards may be deleted only after there is no direct side left that can observe the competing representation.

String ownership, error-handler state, DATA/RESTORE state, dynamic-array descriptors, COMMON/CHAIN state and file/runtime state all need explicit IR/runtime contracts rather than implicit direct-emitter lifetime.

### 3. Behavioral equivalence

The routed backend corpus differential is the correctness gate. Image byte identity with the AX-serial direct emitter is not required; observable behavior is. The differential battery must remain at zero routed/direct disagreements while each new class starts routing.

Before final deletion, run the genuine-compiler differential/golden gates with routing mandatory so the comparison is no longer accidentally exercising the fallback.

### 4. Optimizer replacement

The forced-backend optimizer fixture is a separate gate from semantic coverage. Its remaining failures are a work list for IR or machine passes, not reasons to preserve syntax-to-machine lowering. Move a transformation according to what it knows:

- language/semantic facts -> IR analysis/pass;
- target-independent algebra/data-flow -> IR pass;
- x86 instruction shape, addressing, stack/register cost -> machine-IR pass;
- ABI prologue/epilogue and runtime calling rules -> x86 backend.

Do not reproduce direct-emitter implementation structure merely to satisfy a byte-pattern fixture. Rewrite fixtures that only encode the legacy instruction sequence when the routed sequence is measurably equivalent or better.

### 5. Production routing becomes mandatory

Once gates 1-4 are green:

- remove `UseExperimentalBackend`, `PBC_X_BACKEND`, `--x-backend` and `--no-x-backend`;
- make IR lowering/selection failures compiler errors rather than fallback decisions;
- delete direct-callee compatibility and split-ownership routing logic that only exists for mixed images;
- remove legacy statement/expression/procedure emission;
- keep/extract the DOS image/runtime/linking services still used by the x86-16 backend;
- rename routed-backend tests so the IR path is simply the production backend.

## Reference architecture

This split follows the same layering used by LLVM's code-generation pipeline: target-independent IR optimization is followed by target machine lowering, scheduling, target-specific machine optimizations and register allocation. x87 stack handling and ABI mechanics therefore belong in the x86 backend rather than in a target-neutral source emitter.
