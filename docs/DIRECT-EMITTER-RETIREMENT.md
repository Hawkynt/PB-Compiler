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

**Checked, and the ordering is structural rather than a matter of effort.** The tempting move is to unify each representation NOW so the guards can go before deletion. For DATA that is not available in either direction:

- the routed side cannot adopt the direct emitter's absolute `rt_dataptr` and `rt_readdata`, because the portable runtime has no such routine — `runtime/pbc_rt.h` defines none, and the IR's blob-plus-index form is exactly what lets the C and LLVM back ends read DATA at all;
- the direct side cannot adopt the index, because its emitted bytes are what the golden gate holds byte-identical to the genuine compilers.

So each representation is correct for its own path, and neither can move while both paths exist. That is why the gate says the guards may be deleted only after there is no direct side left to observe the competing representation — the guards are a consequence of gate 5, not a prerequisite for it. The four are `DataReadersRouteTogether`, `SharedDynArrayUsersRouteTogether`, `RewrittenSignaturesRouteTogether` and `CanCallDirectCallee`.

The corollary is that the critical path runs through **gate 4**, not gate 2: the 61 emitted-code fixtures that fail with routing default-on are assertions about the direct emitter's instruction sequences, and they can only be re-pointed at the routed path once the routed path delivers the optimizations they name. That is the 12/55 list above.

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

**Measured.** `OptimizationBatteryTests.Battery_GivenScenarios_WhenTheBackEndIsForced_ThenTheUnmetListDoesNotGrow` runs the battery's expectations with routing forced: **3 of 55 are unmet** (CODEGEN 3, RANGES 0), recorded as a baseline so the list can only shrink. It was 11; section 4c is what the other eight turned out to be.

Reported rather than gating, because almost every assertion names a specific INSTRUCTION and a routed sequence reaching the same result by another shape is not a regression. Telling a missing optimization from a fixture that merely encodes the legacy instruction sequence is done one at a time, by argument.

**An unmet expectation can mean the routed path is BETTER, and the first one examined was exactly that.** `IndexRangeUnknownKeepsCheck` is the control proving the range lattice does not simply drop every bounds check, and under routing it emitted no `rt_raise` call at all. It was not a missing check: measured by call count in the procedure's extent,

| call shape | direct | routed |
|---|---|---|
| `Keep 4` — one constant site | 2 | **0** |
| `Keep 4` and `Keep 7` | 2 | 1 |
| index from `VAL(COMMAND$)` | 2 | 2 |

the routed back end's interprocedural propagation **proves** the index in range from a single constant call site and drops the check correctly — more than the direct emitter manages. A control whose premise an optimizer can discharge is not a control, so the scenario now has two call sites with different values and the range is genuinely unknown. That is one fixture rewritten rather than one pass written, which is the distinction the note below is about.

The forced-backend optimizer fixture is a separate gate from semantic coverage. Its remaining failures are a work list for IR or machine passes, not reasons to preserve syntax-to-machine lowering. Move a transformation according to what it knows:

- language/semantic facts -> IR analysis/pass;
- target-independent algebra/data-flow -> IR pass;
- x86 instruction shape, addressing, stack/register cost -> machine-IR pass;
- ABI prologue/epilogue and runtime calling rules -> x86 backend.

Do not reproduce direct-emitter implementation structure merely to satisfy a byte-pattern fixture. Rewrite fixtures that only encode the legacy instruction sequence when the routed sequence is measurably equivalent or better.

### 4a. What default-on routing costs today

Measured by flipping the default and running the suite **with `DOSBOX_EXE` set**: **63 failures of 6488**, against the 109 this document used to record. The split is what matters:

- **61 are emitted-code assertions** — `Emit_*`, `InlinePolicy_*`, SIMD `Compile_*`. The same class gate 4 describes: fixtures naming an instruction the routed path reaches by another shape.
- **2 are execution failures**, one of which is the pre-existing TIMER corpus case.
- **0 are tail recursion.** This document recorded the deep-recursion pair as the ones that settle it — *"not code quality; it is a behavioural promise"* — and they now run and pass.

The one genuine behavioural regression the measurement found has been fixed, and it was **not** a retirement-only defect: a routed BYTE/SBYTE FUNCTION returned its result in AL alone, leaving AH as whatever ran last, while the direct emitter loads a byte result zero- or sign-extended into AX. A directly-emitted caller therefore read garbage in the high byte. `FileUtil_CanRead` answered -255 where it meant 1; the comparison against 1 simply failed and the program carried on. It is invisible while both sides route, which is why it survived. `BackendByteResultTests` pins it, with a callee that dirties AH on purpose and an assertion that the mixed boundary is real.

**Run it with the emulator.** The first measurement skipped 279 tests without `DOSBOX_EXE`, including both tail-recursion cases and the whole corpus run — and would have reported a smaller number that had never executed the program which found the bug.

#### Re-measured: 62 of 6493, and none of them behavioural

| | count |
|---|---|
| emitted-code assertions | 57 |
| the interpreter missing an opcode | 3 |
| pre-existing TIMER corpus case | 2 |
| **behavioural failures caused by routing** | **0** |

The three that looked behavioural were not. `Rotate32_*` asserts CF/OF against the 386 definition and died with *"unimplemented opcode 66 D1"* — the direct emitter reaches for the imm8 form of the dword shift group even when the count is one, the routed emitter uses the shorter `D1` encoding, and `Cpu8086` only decoded the former. Same instruction; `Shift32` already carried the flag definition for all eight operations. That is the strict oracle needing an opcode, not the routed path needing a fix.

**The 57 are not uniformly re-pointable, and two worked examples say why.** Each has to be read before it is rewritten, because a fixture failing under routing can be failing for a reason that has nothing to do with routing:

- `DialectMetaClaims` drove three claims — `cpu.tier`, `optimize.speed`, `error.overflow` — from a body assigning both operands as literals. A strong enough optimizer computes the result at compile time, and then there is no arithmetic for `$ERROR OVERFLOW` to wrap, no multiply for a CPU tier to widen and nothing for `$OPTIMIZE` to choose between. Reading the operands instead takes pb36 from **3/7 to 5/7 on the ordinary build**: those claims were never failing, they were unmeasured, and the direct emitter only scored better because its propagation is weaker.
- `FloatResultForwardingTests` looks for `FLD [BP+disp8]` immediately before `MOV SP,BP`, across the WHOLE image — so it answers "does any procedure here reload a float before tearing down", which is true of the caller whatever the function under test does. Scoped to the function, routed and direct emit **byte-identical** code, and both contain the marker: O0102 keeps an `FSTP`/`FLD` pair for a float on purpose, to a scratch cell, because that round trip is what rounds the 80-bit x87 value down to SINGLE. The detector cannot tell the eliminated frame-slot reload from the deliberately-kept narrowing one. There is no routing difference here to re-point — the fixture needs the result slot's offset to say what it means, which is a fixture-design change rather than a retirement one.

So the 57 are a real work list, but the unit of work is "read the fixture, find what it actually measures", not "swap an instruction name". Two of the first three examined turned out to be measuring nothing on either path.

### 4b. What the gate-4 work actually turns up

Working the first item on the list produced a chain worth recording, because each step found the next and none of them was the missing optimization the list appeared to name.

1. `IndexRangeUnknownKeepsCheck` is a CONTROL. Under routing it emitted no `rt_raise` call, and the reason was that the routed interprocedural propagation **proves** the index in range from the scenario's single constant call site and drops the check — correctly, and more than the direct emitter manages. A control whose premise an optimizer can discharge is not a control, so it now has two call sites with different values.
2. That made the two paths DISAGREE on the second call: direct answered `unknown 7`, routed `unknown 0`. Genuine PBC 3.50 answers **0** — a local `DIM` array is re-zeroed on every entry.
3. So the direct emitter was wrong: `StackLocalsOf` excludes arrays, so a local array gets a data-segment slot that the frame's `REP STOSW` never reaches, and it kept whatever the previous call left. It is now cleared on entry, excluding `STATIC` (persisting is its meaning), parameters (the storage is the caller's), `STACK` arrays (already in the cleared frame) and dynamic ones (no storage until `DIM`/`REDIM`, which zeroes what it returns).
4. Fixing that invalidated the battery golden — the value recorded there **was** the bug — and broke `Emit_GivenIncrWithAmount_WhenPb36_ThenMemoryAddImmediate`, which scans the WHOLE IMAGE for a byte pattern: the extra prologue in its array variant lifted that variant's count to equal the other's without either `INCR` changing. It now scans the procedure's own extent, which is what its claim is about.

The lesson for the rest of the list: an unmet expectation is a question, not a defect report. Two of the four steps above were the routed path being right.

### 4c. Working the rest of the list: 11 unmet down to 3

Asking that question of the remaining ten produced three answers, and only one of them was a missing optimization.

**Five were the scenario folding away.** Each is a `NOINLINE` SUB driven from the main body by a single literal argument, and the routed path's interprocedural propagation substitutes it and evaluates the whole body at compile time — so the construct the scenario exists to measure is no longer in the image to assert about:

| scenario | direct | routed | what survives routing |
|---|---|---|---|
| `DivideByConstantIsReciprocal` | 55 B | 19 B | `MOV AX,0019` — the division done at compile time |
| `ConstantStoredAsImmediate` | 53 B | 25 B | `MOV AX,8001` — the whole conditional resolved |
| `IntegerMaxFoldsWithoutFpu` | 123 B | 56 B | `MOV AX,0008` |
| `IntegerSignIsBranchless` | 181 B | 72 B | `MOV AX,1` / `MOV AX,FFFF` / `XOR AX,AX` |
| `MinMaxDiamondFolds` | 281 B | 109 B | `MOV AX,0008` |

This is `IndexRangeUnknownKeepsCheck` again, and the fix is the same: a second call site with a different value, so the argument is genuinely unknown on both paths. Each second site was also chosen to take the *other* arm of its scenario's branches, so it earns its place on the direct path too. Varying one argument is not always enough — `IntegerMaxFoldsWithoutFpu` needed both, because leaving `b%` a literal turns the compare into `CMP AX,imm`.

**Three were the same operation in a different operand form.** Not a different result, not a worse one — the assertion simply named one encoding:

| scenario | direct | routed |
|---|---|---|
| `DivideByConstantIsReciprocal` | `MOV BX,6667` / `IMUL BX` | `MOV CX,6667` / **`IMUL CX`** |
| `IntegerMaxFoldsWithoutFpu`, `MinMaxDiamondFolds` | stage into BX, `CMP AX,BX` | **`CMP AX,[BP+4]`** |
| `LongCompareNarrowedToWord` | stage both sides, `CMP AX,BX` | **`CMP WORD PTR [BP-6],50`** |
| `HotAccumulatorWinsTheRegister` | `ADD DI,[BP-4]` — scratch from memory | **`ADD SI,AX`** — neither operand in the frame |

A `present` assertion may now name alternatives as `a|b`, holding when any occurs, so a fixture states which encodings of an operation it accepts rather than which one emitter happened to pick. The last row is the sharpest: that scenario is *titled* `HotAccumulatorWinsTheRegister`, and the routed path keeps both operands in registers with no frame at all (30 bytes against 85) — naming only `add-di-mem-bp` would have failed the emitter that does the thing better.

**One was not a code difference at all.** `LongCompareNarrowedToWord` measured 16 bytes under routing — a prologue cut mid-`REP STOSW`. `ListingInfo.RuntimeLabels` reports every bound `rt_*` label as an offset, but a few are bound `IsConstant` and are *values*: `rt_bss_words` is a word count. On the routed layout that count (1156) happened to fall inside the procedure, and four separate fixtures were using those offsets as code boundaries. `ListingSymbol` now carries `IsConstant`, the fixtures skip them, and `--list` prints a constant as `=XXXX` so it cannot be misread as a place. The procedure is 221 bytes and had narrowed correctly all along.

**The three that remain are one missing pass.** `AccumulateOverArrayIsHandQuality` (2) and `MaxScanReadsEachElementOnce` (1) both walk an array by index, and the routed path recomputes the address every iteration where the direct emitter steps a pointer:

```
routed:  89CA 01D2 19D2   sign-extend CX into DX — a 16-bit index, widened to 32
         89CE D1E6 D1D2   SI = CX*2
         8D38             LEA DI,[BX+SI]
         8B15 01D0 41     MOV DX,[DI] / ADD AX,DX / INC CX
direct:  033F 83C302      ADD DI,[BX] / ADD BX,2
```

That is induction-variable strength reduction, and the 32-bit widening in it is the same habit that drives the register pressure recorded above. It is the last thing between the routed path and this battery.

### 4d. Why the last three are not a small change

`InductionVariableSimplification` already exists and already runs. It does not fire here for a reason it states itself: *"Values from other phis, casts, division/right shifts, calls and memory are rejected."* The index reaches the address through a cast —

```llvm
%i = phi i16 [ 0, %entry ], [ %5, %for.body1 ]
%1 = sext i16 %i to i32
%2 = shl i32 %1, 1
%3 = getelementptr i8, ptr %v, i32 %2
```

— so the affine matcher stops at `%1`. Accepting a **widening of the counter itself** is sound whenever the extension is exact over the loop's own trip count (`sext(i + step) = sext(i) + step` while the narrow add does not wrap; a zero-extension additionally needs the value non-negative), and `CountedLoop` already carries the exact `Trips` needed to decide it.

That was tried. It produces precisely the intended IR — the `sext` and `shl` disappear, an offset phi advances by 2 — and the loop body drops from 24 bytes to 21, losing two shifts and a sign-extension per iteration. **It is still not shippable, for two reasons found only by running the whole suite:**

1. **Four corpus main bodies stop routing** (`DIFF53`, `DIFF91`, `DIFF92`, `DIFF93`), against a `BackendCoverageTests` baseline of zero. A change made to advance retirement moved four programs the wrong way.
2. **It breaks the IR→BASIC round trip.** `IrBasicWriter.Undo` reverses a byte offset syntactically — `mul`, `shl`, or a constant — and a strength-reduced offset is an opaque phi, so it raises *"a subscript whose byte offset is not a multiple of 2"*. That writer is how `IrPassObservableEquivalenceTests` proves a pass observable-equivalent, so every IR pass has to keep the module writable back to BASIC. Reversing the recurrence means recognising the offset phi and re-deriving the index from the counter phi beside it — the exact inverse of the transform, and a bounded pattern, but it has to be written.

And the IR is still one layer short even then: the offset recurrence is **i32**, so the emitted loop carries `ADC DX,0` for a high word that cannot be nonzero over a 50-element array. The root is `IrLowering.cs`, where every subscript is `Coerce(..., PbType.Long)` before the index arithmetic. Narrowing it is its own piece of work — the bounds check needs the wide value so an out-of-range LONG subscript traps rather than wraps, so only the arithmetic *after* a passing check can be narrowed, and only where a check ran.

So the remaining three are: one pass change that is written and understood, plus a writer inverse, plus an index-width narrowing — in that order, each measured against the corpus census rather than against the battery alone.

### 5. Production routing becomes mandatory

**The default is flipped: `UseExperimentalBackend` is ON.** Every program `pbc` compiles now goes
through the IR path. `PBC_X_BACKEND=0` / `--no-x-backend` still selects the direct emitter, which is
retained only for the fixtures below.

What that cost, measured rather than expected:

| | |
|---|---|
| golden gate, routing default-on | **552 pass / 0 fail / 0 skip** |
| behavioural failures caused by routing | **0** |
| fixtures asserting the direct emitter's byte shapes | 57, now explicitly pinned |

The golden gate is the one that matters: with the optimizer off, output is byte-identical to all 19
genuine vintage compilers for every historic dialect, with the IR path doing the compiling.

**About the pinning, because it is the part that can be done dishonestly.** The 57 fixtures were
written when the direct emitter was the only one, and every one of them asserts an INSTRUCTION -
`CMP AX,BX`, `IMUL BX`, `ADD DI,[BP-4]`. Routing reaches the same results by other shapes, so under
the new default they were measuring which emitter ran rather than whether the optimization happened.
They are not disabled and not relaxed: each now says `UseExperimentalBackend = false`, which is what
it always meant. `OptimizerTests` already carried a `CompileWithBackend` sibling for exactly this
distinction, so the file's own design had anticipated it.

That leaves the work list, and it is the honest cost of the flip: **those 57 assertions no longer
cover production.** The compensating coverage is `tests/optimize`, which measures the same
optimizations on the routed path and stands at 3 of 55 unmet. Moving a fixture from `Compile` to
`CompileWithBackend` is the unit of work, done by reading what the test means - and two of the first
three read turned out to be measuring nothing on either path.

Remaining, in order:

- move the 57 pinned fixtures onto the routed path one at a time, or delete the ones that only
  encode the legacy sequence;
- close the last three battery expectations (section 4d);
- then make declines errors rather than fallbacks, and delete legacy statement/expression/procedure
  emission, keeping the DOS image/runtime/linking services the x86-16 backend still uses;
- finally remove `UseExperimentalBackend`, `PBC_X_BACKEND`, `--x-backend`/`--no-x-backend` and the
  split-ownership routing logic that only exists for mixed images.

One construct still declines and is not in the corpus: `ERASE` of an ABSOLUTE array. The routed
lowering keeps an absolute array's segment as a compile-time constant, so there is no runtime cell
to clear. The oracle says less rides on this than it looks: genuine PBC 3.50 **refuses**
`DIM v%(0 TO 3) AT &HB800` outright - *"Error 489: Array is already static"* - the declaration has to
be `DIM DYNAMIC ... AT`, and after an `ERASE` genuine terminates the program rather than answering
anything, which neither emitter reproduces. `tests/diff/DIFF125.BAS` pins the part that does have a
defined answer, and passes.

## Reference architecture

This split follows the same layering used by LLVM's code-generation pipeline: target-independent IR optimization is followed by target machine lowering, scheduling, target-specific machine optimizations and register allocation. x87 stack handling and ABI mechanics therefore belong in the x86 backend rather than in a target-neutral source emitter.
