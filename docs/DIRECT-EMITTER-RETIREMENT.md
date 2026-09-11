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

- FIX results;
- array parameters;
- module-body wrappers still emitted outside IR, notably `CHAIN`.

A row leaves this list only when a focused routing test also executes the routed image and proves observable equivalence.

Closed so far:

- CDECL/STDCALL procedure definitions — the selector already emitted right-to-left stack arguments; definition-side routing takes `LayoutFrame`'s matching offsets, and CDECL emits a bare `RET` because its caller owns cleanup while STDCALL keeps `RET n`. Proven by `BackendStackConventionRoutingTests`, which executes routed against direct in both optimizer modes, and by the recursive convention case in `CallingConventionTests`.
- BYREF record parameters — a record crosses the call as one near pointer and its layout never crosses the boundary, so member uses lower to ordinary typed GEP/load/store against the caller's storage. Proven by `BackendRecordParameterRoutingTests`, which covers member offsets and write-back through the pointer.
- EXT procedure ABI — the complete parameter/result ABI is now represented on the routed path. A BYVAL EXT argument is staged at its declared ten-byte width with `FSTP TBYTE` and pushed as five words from high to low; the callee addresses that caller-owned argument through a TBYTE `ParamCell`; and an EXT result leaves the function in ST(0). This is the same representation already used by the direct emitter, so no conversion format or second calling convention is introduced. `BackendExtendedParameterRoutingTests` executes two ten-byte parameters plus an EXT result against the direct path in both optimizer modes; the selector's stack-call pusher now closes the former call-side gap as well.
- BYTE procedure ABI — BYTE/SBYTE retain PowerBASIC's word-sized call slot while the value itself is the low byte. Routed calls materialize that word before `PUSH`, routed definitions read the same slot, and byte FUNCTION results cross in AL. The routing gate uses 200 rather than a tiny value so unsigned BYTE semantics are observable rather than accidentally identical to INTEGER.
- QUAD procedure ABI — a BYVAL QUAD remains eight signed-integer bytes on the stack and is copied into the routed backend's existing qword SSA cell on first use. Calls push the four words high-to-low. PowerBASIC's QUAD result channel is x87 ST(0), so routed returns use `FILD qword` and routed callers immediately recover the integer with `FISTP qword`; every signed 64-bit integer is exact in the x87 extended significand.
- BYVAL FIX procedure parameters — FIX crosses the stack as the raw scaled signed i64 cell already used by routed FIX storage, not as an IEEE value. The callee performs the ordinary `rt_fix_down` read conversion, so the runtime-owned scale remains authoritative. `BackendFixBcdTests.Route_GivenByValFixParameter_ThenTheScaledQwordCrossesTheProcedureBoundary` sets `pbvFixDigits = 4`, observes `2.4692`, and compares routed execution with the direct emitter. FIX FUNCTION results remain deliberately fenced until their numeric return conversion has a routed ABI.
- FASTCALL/WATCALL procedure definitions — the call side already placed leading arguments in AX,DX,BX(,CX) via `X86CallAbi`. The definition side needed only the other half of that contract: `LayoutFrame` had always assigned those parameters negative frame cells (`[BP-2]`, `[BP-4]`, …) and left them to a prologue spill that only the direct emitter performed. The routed prologue now pushes the same registers in parameter order, and `MachineEmitter` starts its own stack slots below that reservation so an alloca can never be handed parameter 0's address. From there they are ordinary frame parameters, so nothing after the prologue knows the convention was a register one, and `RET n` is already correct because `paramBytes` counts only the stack parameters. A multiword BYVAL argument stays rejected on both paths — splitting one value across a register pair needs per-compiler rules that neither emitter models — which makes it a front-end diagnostic rather than a routing class. Proven by `BackendRegisterConventionRoutingTests`, which executes routed against direct in both optimizer modes over five arguments (overflowing both register files), BYREF write-back, and a body with its own array storage sharing the frame; and by the recursive case in `CallingConventionTests`, where the callee reuses the very registers its own arguments arrived in.
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
