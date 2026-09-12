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

The remaining pinned blocker is:

- `ERASE` of an **ABSOLUTE** array — unmapping memory the program does not own has no routed meaning yet, and the lowering refuses rather than inventing one; the direct emitter simply clears the descriptor word;

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
- 8086 register pressure in a routed body — this was a blocker in its own right, and the only one that was not a construct: a body combining two descriptor bound reads with a read-modify-write of an element declined with `allocation: no register assignment, and nothing left that can move to memory`. The cause was not the arithmetic width and not the x87. A GEP at a constant displacement was selected as an `LEA` into a register of its own, so each of a descriptor's six fields took a BASE register — and a value used as a memory base is the single thing the spiller cannot relocate, which is why the failure is a hard decline rather than a slowdown. It was isolated by comparing against the IDENTICAL body over a shared dynamic array, whose fields are absolute data cells needing no base: 6 distinct bases and 117 instructions against 3 and 68. Folding the displacement into the access brings the parameter form to exactly the shared form's 3 and 68, and every previously-declining body routes and executes identically to the direct build in both optimizer modes. `BackendGepAddressingTests` pins both the base count and the allocation, over a record's members as well as an array descriptor's fields, so the fold is not special-cased to arrays.
- `REDIM` through an array parameter — and the DIRECT emitter turned out to be the wrong one. `SlotOf` mints a private data cell for an array parameter, so a callee's `REDIM` recorded the new block where nothing reads it: genuine PBC 3.50 answers `UBOUND` 9 and a cleared array after a callee redimensions to `1 TO 9`, while this compiler answered the OLD bound with the old contents intact, and element writes landed in the new block regardless. It printed plausible numbers rather than faulting, which is why it survived. Both emitters now reach the caller's own descriptor — the direct one through `DescriptorAccessorOf` (which clobbers `SI`, so the `PRESERVE` copy's descriptor read has to precede its `SI` load), the routed one by writing the caller's block back and re-reading it after the call. `tests/diff/DIFF124.BAS` compares against the real compiler.
- Split array ownership across the two paths — passing an array to a procedure is invisible to a name-based analysis, because the callee refers to its own parameter. Two guards were blind in the same way: the escape set left such an array in the caller's frame while the callee rewrote the direct emitter's packed block, and `CanCallDirectCallee` had only ever refused array parameters incidentally, as part of a shape check that stopped applying once they began routing. An array handed to a procedure now escapes, and a routed caller will not hand one to a callee the back end did not take.
- Assignment into a string array parameter — the guard that refused it exists because a RECORD element is copied by address, and a `memcpy` would take the far pointer for a near one. A string element is not in that position: its cell holds a HANDLE, one word, and both consumers of the address move exactly that word — the read loads it and the assignment stores the new one over it. Neither hands the ADDRESS to a string routine, which is what could not survive losing a segment. Proven by `BackendArrayParameterRoutingTests`, which writes through the parameter to an element the caller can still see, in both optimizer modes.
- BCD parameters and results — a BCD cell IS ten bytes of x87 extended, which is the value channel EXT already crosses on, so admitting it removed a restriction rather than adding a representation. Both directions execute against the direct build in both optimizer modes.
- `REDIM PRESERVE` through an array parameter — the semantics were always right on both paths; what kept it out of the corpus was 8086 register pressure in the routed body, and the constant-offset GEP fold removed that. It is back in `tests/diff/DIFF124.BAS`, so the oracle covers both halves of the reallocation.
- Procedure-local error handling — `ON ERROR` / `RESUME` / `TRY` already lower to inline handler intrinsics. `ProcedureErrorHandlerPreservation` adds the procedure-boundary ABI rule the module body does not need: save the caller's `rt_onerr`/`rt_onerr_bp`/`rt_onerr_sp` triple in this invocation's frame and restore it before every `RET`. `BackendProcedureErrorHandlerRoutingTests` executes both normal return and an inner handled fault while proving the outer caller trap is restored, with optimization on and off.

BYVAL records are deliberately absent from both lists: the direct emitter refuses them as well ("not yet generated: load of UdtType"), so they are a front-end gap rather than a routing class, and they do not block retirement.

### 2. One owner for program state

The temporary mixed routed/direct architecture has duplicate representations for some state (for example DATA cursors and shared dynamic-array descriptors). Full ownership must make those single-source again. Split-routing guards may be deleted only after there is no direct side left that can observe the competing representation.

String ownership, error-handler state, DATA/RESTORE state, dynamic-array descriptors, COMMON/CHAIN state and file/runtime state all need explicit IR/runtime contracts rather than implicit direct-emitter lifetime.

### 3. Behavioral equivalence

**Routing can now be made mandatory, and that is the only way these gates ask a real question.** `RequireBackend` (`PBC_X_BACKEND_STRICT` / `--x-backend-strict`) turns a decline into a compile error instead of a fall back. With a fallback present every gate below is satisfied by construction: a decline is invisible, the program still compiles, and the differential still agrees — because for that body *both sides ran the same emitter*. A bodiless EXTERNAL declaration is exempt; it is a link import with no code to emit on either path.

Where that leaves the two gates today:

- the pb36 corpus compiles **completely** with routing mandatory, in both optimizer modes — `MandatoryRoutingTests` pins it, and pins that the mode really does reject the constructs that decline, so it cannot pass vacuously;
- the genuine-compiler battery run with routing mandatory scores **548 pass / 0 fail / 0 skip** — the same as with the fallback. Every program in the battery, in every dialect, in both the direct and the round-trip lane, compiles with the direct emitter forbidden from taking any body. Those 11 are the whole remaining distance for this gate, and they are all historic dialects, which is why the pb36 corpus does not see them:

**This gate is now met for the battery.** It was 526/11 when the measurement first existed. What closed, in the order it was found:

- **BASCOM half-away-from-zero rounding** (6 programs) — not a missing ABI row; no such routine existed. `rt_rndaway` is `rt_round`'s body without the decimal-places scaling, in a runtime section of its own, because the trimmer emits per section and sharing `rounding` would have added its bytes to every program that rounds at all.
- **A whole integer constant on the float path** (2 programs) — a constant the front end typed as a float and the folder left whole (`32767 + 1` promoted past INTEGER). It goes in the same qword pool as any float literal, guarded on round-tripping so a value the pool cannot hold still declines rather than being quietly rounded.
- **BASICA/GW source control cannot reach** (2 programs) — `DEADTEXT`'s line 40 is arbitrary text a `GOTO` skips, and the language never parses a line execution does not reach. The direct emitter's own reachability set is handed in rather than recomputed: two analyses would be two answers to a question that must have one. A deferred line that IS reachable still declines.
- **Microsoft Binary Format** (1 program) — BASICA/GW floats are stored in MBF, which the x87 cannot compute on, so every load converts to IEEE and every store converts back. `rt_mbfld`/`rt_mbfst` are those conversions as routines, because the conversion branches while the selector emits within one block; they take the cell's near OFFSET, so an MBF number is converted where it lies and never becomes a register-resident value. What actually blocked it was a level up: `mem2reg` had promoted the MBF cell, and a phi has no address for the conversions to work through — so an MBF cell is now refused promotion, the cell being the thing that is foreign rather than any particular use of it.

**Every figure here was taken with the strict flag verified live** — against a construct that must decline, and against the same construct compiling cleanly without it. An earlier measurement reported a clean strict sweep that was really the ordinary gate with the flags silently ignored, because the branch predated the flag.

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
