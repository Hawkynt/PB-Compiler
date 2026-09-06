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

- QUAD parameters/results;
- BYTE parameters/results;
- FIX parameters;
- EXT routed call arguments (the selector still needs ten-byte stack staging for f80); 
- FASTCALL/WATCALL procedure definitions and register-argument calls;
- procedure-local error handling (`ON ERROR` / `RESUME` / `TRY`) including caller-handler preservation;
- array parameters;
- module-body wrappers still emitted outside IR, notably `CHAIN`.

A row leaves this list only when a focused routing test also executes the routed image and proves observable equivalence.

Closed so far:

- CDECL/STDCALL procedure definitions — the selector already emitted right-to-left stack arguments; definition-side routing takes `LayoutFrame`'s matching offsets, and CDECL emits a bare `RET` because its caller owns cleanup while STDCALL keeps `RET n`. Proven by `BackendStackConventionRoutingTests`, which executes routed against direct in both optimizer modes, and by the recursive convention case in `CallingConventionTests`.
- BYREF record parameters — a record crosses the call as one near pointer and its layout never crosses the boundary, so member uses lower to ordinary typed GEP/load/store against the caller's storage. Proven by `BackendRecordParameterRoutingTests`, which covers member offsets and write-back through the pointer.
- EXT procedure definitions — f80 parameters already address their caller-owned TBYTE cells directly and real results already leave the routed function in ST(0). `BackendExtendedParameterRoutingTests` exercises two ten-byte parameters plus an EXT result across a mixed direct-caller/routed-callee boundary in both optimizer modes. The remaining EXT work is only routed call-side ten-byte argument staging.

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
