# An in-house x86-16 back end for the SSA IR

## Why this exists

The instruction scheduler (`Assembler.RunSchedule`, docs/PB36.md) reorders the
*final* byte stream and is correct, on-pipeline and verified — but it is structurally
limited: the scalar codegen is **AX-centric**, so independent statement chains
serialise through `AX` and the scheduler rarely has anything to interleave. The two
walls that block register reassignment at the byte level are:

1. **Free-register proof.** Renaming a value to a free register is sound only if that
   register is dead across the value's lifetime; at the byte level a window is bounded
   by barriers where *every* register is conservatively live-out, so without external
   liveness the only provably-free targets are registers redefined later in the same
   window — which never happens in the exact AX-serial code the rename must fix.
2. **Length-changing re-encoding.** The scalar codegen emits accumulator short forms
   (`MOV AX,imm`=`B8`, `ADD AX,imm`=`05`); renaming `AX`→`BX` turns `ADD AX,imm` into
   the modrm form `81 /0`, one byte longer — so a rename is not length-preserving and
   forces the window and every later label/fixup to be re-laid-out.

Both walls **dissolve at a virtual-register asm-IL layer**: SSA gives precise liveness
for free (wall 1 gone), and register choice happens *before* final encoding so the
emitter picks the right form once with no relayout (wall 2 gone). AX-serialisation is
gone because each independent value is its own virtual register. This is the proper
home for register reassignment + interleaving, and it is how every production compiler
is structured.

## Architecture decision (2026-06-24)

**Both targets.** Keep the existing `LlvmEmitter` path (AST → SSA IR → optimise →
`.ll` → real LLVM → x86-64/ARM/…) for modern targets — it already gets register
allocation for free. **Build an in-house x86-16 back end off the same SSA IR** for the
16-bit DOS target, which `llc` does not serve. The SSA IR (`PowerBasic.Compiler/Ir/`)
is the shared source for both.

### Phase ordering — settled: optimise (allocate) *late*

The doubt was "for clever inlining you need register pressure in advance, or optimise
at a later stage." Resolution, as in LLVM/GCC: **inline early at the IR level on
size/frequency heuristics; allocate registers late, after instruction selection, with
spilling to absorb pressure.** Register pressure is at most a second-order tuning input
to the inliner's cost model — never a correctness prerequisite. The two phases cannot
both be first; the universal answer is inline-early / allocate-late.

## Pipeline

```
source → AST
       → typed SSA IR          [mem2reg, inline, SCCP, GVN, instcombine, LICM, DCE]   ← BUILT (Ir/)
       → instruction selection → x86-16 MachineFunction with VIRTUAL registers        ← STAGE 1-2
       → instruction scheduling on virtual-register machine instructions              ← STAGE 6
       → liveness / live intervals                                                     ← STAGE 3
       → linear-scan register allocation (vreg → AX/BX/CX/DX/SI/DI, spill on pressure) ← STAGE 4  ★ reassignment
       → Assembler → machine code                                                      ← EXISTS (Asm/)
```

The existing SSA IR middle-end and the `Assembler` are the fixed bookends. Scheduling runs before
allocation so the scheduler can still see independent virtual chains. Calls and any instruction
with explicit physical clobbers are barriers: allocation has not yet chosen physical registers, so
moving virtual work into a pinned-register sequence could overwrite a prepared argument or result.

## The SSA IR foundation (accurate map — `PowerBasic.Compiler/Ir/`)

- **Instructions are values** (`IrInstruction : IrValue`); use-def via object refs.
  `value.Users` is the intrusive use-list; an operand that `is IrInstruction` is its def.
- **Instruction set** (`IrInstructions.cs`): `IrBinary`(`IrBinaryOp`: Add/Sub/Mul/SDiv/
  UDiv/SRem/URem/And/Or/Xor/Shl/LShr/AShr + FAdd/FSub/FMul/FDiv), `IrCmp`(`IrCmpPred`:
  Eq/Ne/Slt/Sle/Sgt/Sge/Ult/Ule/Ugt/Uge + Fo*), `IrCast`(`IrCastOp`: Trunc/ZExt/SExt/
  FP*/IntToPtr/PtrToInt/BitCast), `IrAlloca`(type, Count), `IrLoad`(ptr), `IrStore`
  (value, ptr), `IrGep`(byte-offset or element-indexed), `IrPhi` (lead the block,
  `IncomingBlocks` aligned with operands), `IrSelect`(cond,t,f), `IrCall`(callee, args),
  `IrBr`, `IrCondBr`(i1 cond), `IrSwitch`(cases), `IrRet`, `IrUnreachable`.
- **Values** (`IrValue.cs`): `IrConstantInt`(long Value, ZeroExtended), `IrConstantFloat`,
  `IrNullPtr`, `IrUndef`, `IrArgument`(Index), `IrGlobalVariable`(ValueType, Bytes?,
  IsZeroInitialized), `IrFunction`.
- **CFG** (`IrFunction.cs`/`IrBasicBlock.cs`): `fn.Blocks` (def order), `fn.Entry`,
  `block.Instructions`, `block.Phis`, `block.Terminator`, `block.Successors`/
  `Predecessors`. `IrDominators.Build(fn)` → `ReversePostorder` (entry first),
  `ImmediateDominatorOf`, `Dominates`, `FrontierOf` — reuse for liveness.
- **Types** (`IrType.cs`): `Kind` ∈ {Void, Int, Float, Ptr} + `Bits`. Canonical I1/I8/
  I16/I32/I64, F32/F64/F80, Ptr (pointer width is a *target* property → 16-bit here).
  `IrTypeMapper.TryMap(PbType)` maps scalars; returns false for string/array/UDT/ptr.
- **Lowering** (`IrLowering.cs`): `TryLowerMainBody` / `TryLowerModule` → null if
  unsupported. Covers scalar/array/record assign, IF/FOR/DO/SELECT, CALL (BYVAL scalar
  / BYREF-as-ptr), GOTO/GOSUB, PRINT/INPUT/file/DATA via runtime calls, MID$, arithmetic/
  comparison/cast/intrinsic expressions, strings as handle pointers. **Unsupported (throws
  `IrLoweringException`):** dynamic arrays, UDT-array fields, non-scalar params, inline
  asm, unlisted intrinsics. A program either fully lowers or the backend declines it.
  Error handling (ON ERROR/RESUME) lowers AND routes for a module body - see "Emitting the
  error handler" below. A PROCEDURE that arms one is still excluded, because the direct
  path saves and restores the caller's handler triple around such a body.

### Emitting the error handler

`ON ERROR` is the one construct that cannot be a runtime call. Arming a handler captures the
**current** frame - the `BP` and `SP` that `rt_raise` restores before it jumps - so a `CALL` would
capture its own. The lowering therefore emits intrinsics (`rt_onerr_arm`, `rt_onerr_disarm`,
`rt_onerr_resume_next`, `rt_err_clear`, `rt_resume_mark`, `rt_resume_same`, `rt_resume_next`) and
`InstructionSelector.SelectErrorHandlerIntrinsic` expands each into the same few `MOV`s the direct
emitter writes inline. Three pieces of machinery make that possible:

- `MOperand.BlockOffset` - the offset of a basic block's own label, the machine form of the IR's
  `blockaddress`. No other operand names a point in this function's own code, because every other
  transfer of control *is* an instruction.
- `MOpcode.JmpIndirect` - `RESUME` and `RESUME NEXT` go back to a statement the *fault* chose, so the
  destination is a value the runtime latched rather than a label anything here can name.
- `IrUnreachable` as a terminator, accepted only when the block already ends in one - which it does
  after the indirect jump that never returns.
- **Wiring** (`pbc/Driver.cs` `--emit-llvm`): `TryLowerModule` → `IrPassManager.Standard()`
  `.RunOnModule` → `Inliner.Run` → re-run → `IrVerifier.Verify` → emit. The new backend
  slots a lowering pass in the same spot, after optimisation, before emission.

## Machine IR (Stage 1)

A target-level IR over virtual registers, distinct from the SSA IR. Sketch:

- `MReg` — a register operand: either a virtual id (`v0, v1, …`, unbounded) or a physical
  `Reg` (`AX..DI`), plus a size (byte/word/dword). Allocation rewrites virtual → physical.
- `MOperand` — `MReg` | immediate | memory (`[base+index*scale+disp]`, base/index are
  `MReg`, plus an optional **segment**) | label/global | stack-slot (for spills and allocas).
  `MReg`) | label/global | stack-slot (for spills and allocas). A memory operand may name a
  **segment cell**: a runtime word holding the segment the address is relative to, for the one
  memory that is not the program's own (the far array heap at `rt_arrseg`, which is what an IR
  pointer in address space 1 points into — docs/IR.md). The emitter reloads `ES` from that cell
  immediately before the access and prefixes the operand with `ES:`, after scheduling and
  allocation, so the load and its use are adjacent bytes and no call can be moved between them.- `MInstr` — an opcode (`Mov, Add, Sub, Imul, Cmp, Test, Lea, Shl, …, Jcc, Jmp, Call,
  Ret, Push, Pop`) + operands + a per-opcode **def/use descriptor** (the same shape the
  scheduler already consumes), so liveness, allocation and scheduling all read one model.
- `MBlock` (label + `MInstr` list + successors), `MFunction` (blocks, virtual-reg count,
  stack-slot table, frame size), mirroring the SSA CFG.

## Instruction selection (Stage 2)

Per-block, walk `block.Instructions` and lower each SSA instruction to `MInstr`s over
fresh virtual registers (the SSA value → its defining vreg). Start with the integer
core that the differential batteries exercise: `IrConstantInt`, `IrBinary` int ops,
`IrCmp`+`IrCondBr` (compare→`Jcc`), `IrBr`, `IrLoad`/`IrStore`/`IrGep`/`IrAlloca`,
`IrCall`/`IrRet`, `IrCast` int width changes, `IrPhi` (lowered to parallel copies on
predecessor edges — out-of-SSA). Defer float (x87), strings and intrinsics to later
stages; until then the backend declines functions containing them and the program falls
back to the direct codegen.

## Liveness + linear-scan allocation (Stages 3-4)

Compute live intervals over the machine function (backward dataflow; SSA + dominators
make this exact). Linear-scan assigns virtual registers to `{AX,BX,CX,DX,SI,DI}` (BP/SP
reserved for the frame), spilling the furthest-next-use interval to a stack slot on
pressure. This is where **register reassignment happens for real**: independent values
land in distinct physical registers, so `x=x*2+7 : y=y*3+15` keeps `x` and `y` apart.
Constraints handled here: `Imul`/`Mul`/`Div` implicit `AX`/`DX`, shift-count in `CL`,
call-clobbered registers across `Call`.

## Scheduling (Stage 6) + emission (Stage 5)

Run the dependency scheduler over virtual-register machine IR, preserving calls, physical-clobber
sequences, x87 stack order, flags and memory dependencies. Linear scan then allocates that final
order. Emit each `MInstr` through the existing `Assembler` methods; encoding, length and fixups are
already handled there, so wall 2 never arises. The byte-level scheduler stays as the ceiling for the
direct codegen path.

The scheduler also **weighs its own reordering against the register file**, and keeps the written order
when a proposed one would hold more values alive at once than six. Independence and register pressure
are the same quantity read two ways, so a list scheduler maximizing the first is maximizing the second
— see "Scheduling is not free on six registers" below.

## Gating + verification

The backend is an **optimiser** feature, so it is gated on the optimiser flags, not the
dialect (the optimiser is dialect-agnostic — docs/PB36.md): an optimised standalone
program under `$OPTIMIZE SPEED` whose body fully lowers to IR is compiled through the
backend; everything else (units, error handling, unsupported constructs) falls back to
the direct codegen. Each stage is verified by the **differential oracle** — output
equivalence to the genuine compilers across all batteries, the same basis the scheduler
uses (the backend changes the emitted bytes, so it is verified by *output*, not
byte-identity). The backend stays behind an explicit opt-in until it reaches output
parity on the full battery, then becomes the default for the programs it can handle.

## Status

All six stages are implemented and unit-tested (the pipeline runs end to end:
`SSA IR → InstructionSelector → MachineScheduler → LivenessAnalysis →
LinearScanAllocator → MachineEmitter → Assembler → machine code`):

- **Stage 1 — MachineIR** (`Backend/MachineIr.cs`). ✅
- **Stage 2 — instruction selection** (`Backend/InstructionSelector.cs`), the corpus-covered integer,
  x87 and runtime-call forms; declines unsupported shapes. ✅
- **Stage 3 — liveness / live intervals** (`Backend/LivenessAnalysis.cs`). ✅
- **Stage 4 — linear-scan allocation** (`Backend/LinearScanAllocator.cs`), with BX/SI/DI address
  constraints, memory spilling, rematerialization and live-range splitting — *this is the register
  reassignment*. ✅
- **Stage 5 — emission** (`Backend/MachineEmitter.cs`), virtual→physical rewrite + frame-slot resolution through the `Assembler`. ✅
- **Stage 6 — machine scheduling** (`Backend/MachineScheduler.cs`), pre-allocation interleaving with
  physical-clobber barriers. ✅

The backend is wired into production codegen behind the experimental backend switch. It can own
individual procedures and the module body when every participating function lowers, selects,
schedules and allocates. Unsupported constructs still decline to the direct emitter; removing that
fallback is a goal, not the current state. The sections below retain the implementation findings in
the order they were discovered.

### Activation finding (empirical — a routing was prototyped, harness-tested, and reverted)

A statement-level routing was prototyped: eligible INTEGER `+`/`-`/`*` assignments compiled by the
backend, reading and writing the variables' **existing memory cells** (frame-reuse, so the calling
convention and result return stay with the unchanged codegen). It produced **correct output in
isolation** — `c% = a%*2 + b%*3 + a%*5` ran in DOSBox and matched the oracle — but running the whole
battery with it forced on showed it is **unsafe to activate this way**: in optimized code a variable's
current value is frequently **not in its memory cell**, so the backend's cell reads go stale. Two
distinct causes were confirmed by minimal repros:

1. **Register residency (O5).** A loop counter/accumulator kept in `SI`/`DI` (`s% = s% + i%` inside a
   `FOR`) — the memory cell is stale. Excluding `ResidentRegOf(symbol) != null` fixed those two batteries.
2. **Constant propagation + dead-store elimination.** `x% = 7` is propagated into later uses and its
   store elided, so `x%`'s cell stays `0`; the backend read `0` and computed `y%*320 + 0` instead of
   `+ 7`. No cheap local test exists for this — it needs the optimizer's propagation/DSE state.

**Conclusion:** memory-cell sharing cannot safely activate the backend inside the optimized pipeline.
Safe activation needs either (a) **frame ownership of a whole region/function** so the backend holds
values in its own registers and never reads a stale cell — but eligible small functions are
*inlined/const-folded away* before emission, so this needs the backend to handle the post-inlining
shapes (control flow, calls, larger bodies); or (b) a real integration with the optimizer's
residency/propagation/liveness state. Both are substantial. The prototype routing was **reverted**
(unsafe to ship even gated); the verified machinery above is retained as the foundation.

### Activation finding #2 — PB integer arithmetic is FLOAT in the IR (the frame-ownership blocker)

The frame-ownership path (compile a whole pure-INTEGER function via its SSA IR — no shared cells, so
no staleness) was then prototyped: `TryLowerModule` → IR passes → `TrySelect` → `Allocate` →
`EmitFunction`, with the function excluded from inlining and the register-parameter convention so its
stack ABI matches the call sites. It wired in cleanly (gated, default off) and the build/tests stay
green — but **0 functions route**, because `TrySelect` declines them. The reason is fundamental:
`IrLowering` lowers PowerBASIC's integral `+`/`-`/`*` to **floating point** (PB's display semantics —
`PRINT A%*B%` shows `9E+8`). So `FUNCTION F%(a%,b%) : F% = a%*2 + b%*3` lowers to:

```
%0 = sitofp i16 %a to float ; %1 = fmul float %0, 2.0
%2 = sitofp i16 %b to float ; %3 = fmul float %2, 3.0
%4 = fadd float %1, %3       ; %5 = fptosi float %4 to i16 ; ret i16 %5
```

The selector handles integers, not `sitofp`/`fmul`/`fadd`/`fptosi`, so it declines. A "pure integer
function" therefore has **no integer IR**. So activation needs one of: **(a)** float/x87 instruction
selection (handle the FP ops + conversions — substantial), or **(b)** an **integer-recovery IR pass**
that rewrites `fptosi(fadd(fmul(sitofp x, C1), fmul(sitofp y, C2)))` back to integer
`add(mul(x, C1), mul(y, C2))` whenever the result is stored as an integer — sound for `+`/`-`/`*`
because the wrapped (mod 2¹⁶) result is identical, and it would let the existing integer selector fire.
Option (b) is the more tractable next step. The prototype routing (gated, non-firing) was reverted at
the time; the verified machinery was retained.

### Activation — DONE (option b: the integer-recovery pass + frame-ownership routing)

`Ir/Passes/IntegerRecovery.cs` implements option (b): it rewrites `fptosi(float-tree)` back to integer
`add`/`sub`/`mul` over the original `iN` values (sound — the stored result is mod-2ᴺ either way, exactly
what the direct codegen already does). With it in the back end's IR pipeline, eligible integer
functions get genuine integer IR and the selector fires. The routing (`CodeGenerator.Backend.cs`):

- `BackendProcs` — `TryLowerModule` → standard IR passes → `IntegerRecovery` → standard passes →
  per-eligible-function `TrySelect` + `MachineScheduler.Schedule` + `LinearScanAllocator.Allocate`.
- Eligible = a procedure with supported scalar BYVAL parameters/results, no procedure-local error
  handling, and IR that fully selects + allocates. Unsupported constructs decline automatically. The back end owns the
  **whole function via SSA** — no shared memory cells, so it never reads an optimizer-stale cell (the
  blocker the cell-sharing prototype hit).
- The function is excluded from inlining (`CodeGenerator.cs:601` `isInlinable` predicate) and from the
  register-parameter convention (an `OptRegParm.Apply` skip predicate), so its emitted stack ABI matches
  the call sites; `EmitBackendFunction` emits the standard prologue / argument loads / body / `RET n`.
- Selection fixes for real IR: a register is materialized for an immediate `IMUL` multiplier (`a%*2`);
  argument vregs are numbered before phi vregs so argument `i` is vreg `i`.

### The routed frame (`BackendFrameTests`)

A procedure with a **local array** used to be kept off the back end outright, after `CODEGEN.BAS`
printed `accumulate-32283` where the direct emitter prints `accumulate 3`. That was blamed on the
frame layout — the routed function getting its slots from `MachineEmitter` while the direct path lays
locals out through `LayoutFrame` — and it was the wrong diagnosis. Two smaller, more specific defects
were, and both show *only* on an array, because a scalar is one slot and is written before it is read:

1. **A multi-slot alloca pointed at the top of its block.** Stack slots are laid out *downward* from
   `BP` (slot 0 at `[BP-2]`, slot 1 at `[BP-4]`, …) while a GEP walks *upward* from its base, so
   pointing at slot 0 put element 0 at the block's high end and sent every later element climbing over
   the saved `BP`, the return address and the caller's arguments. The base is the block's **last**
   slot. A `DIM a%(0 TO 49)` that summed its fifty elements was reading the parameter list back and
   reporting plausible numbers for it.
2. **The prologue never zeroed the frame.** PB gives every local a zero start; the direct path spells
   this `REP STOSW` over the whole frame and the routed one now does the same, before the argument
   loads (it clobbers `AX`, `CX`, `DI` and `ES`, and at that point no allocated register holds
   anything). Spill slots get zeroed along with the allocas — they are written before they are read,
   so it costs only the instruction.

With both fixed the exclusion is gone, and the corpus differential agrees on every routed program in
both optimization modes.

### String ownership - who is allowed to free a handle

`DosRuntime.Strings.cs` states the rule its routines are written to: **every string value in generated
code is an owned temporary**, and a routine documented as "consumes" frees what it is handed.
`rt_strcat` consumes both operands; `rt_str_print` consumes what it prints.

Reading a string *variable* is therefore the case that needs care, because the handle in the cell
belongs to the cell. The lowering used to hand it straight on, and nothing noticed while no
string-printing function was routed. The moment `rt_print_strvar` got an ABI entry, `PRINT a$` twice
printed `hello` and then nothing, and `a$ + b$` emptied both operands - a use-after-free that does not
fault, because freeing a handle only marks its descriptor free and the next read finds a zero-length
string and prints it happily. `IrLowering.BorrowString` now copies (`rt_strdup`) on every read of a
string variable or array element, which is what the direct emitter does and what makes the consuming
entries safe to list at all.

### Coverage, measured (`BackendCoverageTests`)

Widening the selector is only worth doing in the order the corpus demands, so the census runs the
back end's own pipeline over all 162 battery programs and prints a histogram of **why** each
function declines. That measurement immediately paid for itself twice:

- **a crash, not a decline.** `PointerMemory`/`Operand` looked a value up in the vreg map with the
  indexer, so a function loading a module-level global threw `KeyNotFoundException` *out of the
  compiler*. Every operand now goes through a guarded `TryOperand`, which declines - the whole
  point of the back end being an opt-in path is that it falls back, never fails.
- **a pointer is a word.** `RegSize` mapped `ptr` (which carries no bit width - it is a target
  property) to a *byte*, disagreeing with `SizeOf`'s 2 bytes, so a pointer-typed load would have
  been sized wrongly.

The ranking it produced was unambiguous: **`IrCall` was 87 % of all declines** (52 of 60), and of
those, 47 were calls to `rt_*` runtime declarations from the un-routed `main` body while 5 were
calls to defined procedures from exactly the functions the routing does take.

### Calls (the widening that ranking bought)

`SelectCall` handles a direct call to a **defined** procedure in the convention the direct codegen
emits — arguments pushed **left to right**, `CALL`, callee cleans with `RET n` — so a back-end
function and a directly-emitted one call each other unchanged. The result arrives in `AX` and is
copied into the call's own virtual register, which costs nothing when the allocator puts it back in
`AX`.

Two soundness rules make that safe:

- **The call clobbers the whole register file.** This ABI preserves nothing: a callee owns `AX`-`DX`
  as scratch and may use `SI`/`DI` for loop residency without saving them. The `MInstr` declares all
  six, so the allocator refuses to keep any value in a register across the call and — having no
  spilling yet — declines the function instead of letting a value be destroyed.
- **A routed function may only call routed functions.** The two sides must agree on the ABI, and
  `OptRegParm` may convert a directly-emitted procedure to the register convention — a decision it
  makes *after* this set is known, since it skips exactly the routed procedures. Requiring callees to
  be routed makes both sides stack-convention by construction. Dropping one invalidates its callers,
  so the routing iterates to a fixpoint.

A `CALL` also needs the callee's real `Label`: procedure labels are minted with `DefineLabel`, a
different registry from `Assembler.Lbl`, so looking the name up there would create a fresh,
never-bound label and the image would not assemble. `MachineEmitter` takes a resolver from the code
generator (`CalleeLabel`) and throws if the routing ever admits a call it cannot bind.

Self- and mutual recursion route as a result (`Down%(n%-1)` selects); the remaining defined-procedure
declines are now precise and each names its own next increment — a `BYREF` parameter arrives as a
`ptr` the eligibility gate does not admit.

### 32-bit values are register pairs — and were silently truncated before

x86-16 has no 32-bit register, so a `LONG`/`DWORD` lives in a **pair**. The selector mints two
ordinary virtual registers per value (low in `_vregs`, high in `_hiVregs`), which keeps the allocator
free of any pairing concept: it places and spills the halves like anything else, and only the
ABI-pinned spots name physical registers — a `LONG` result goes back in `DX:AX`, as the direct
codegen's convention says (*"Results: AX / DX:AX / ST0 / string handle in AX"*).

What the pair lowering replaced was a **latent miscompile**. A 32-bit load used to mint a single
`Dword`-sized virtual register and emit one `MOV` — and because the emitter resolves every memory
operand as `Mem.Word` and every register by identity regardless of size, that read the **low 16 bits
only** and carried them as the whole value. Such functions selected. They now either lower correctly
or decline honestly, which is why the measured coverage moved *down* from 15 to 13: a coverage number
is only worth defending when every function under it is actually right.

Selected today: `add`/`sub` with the carry threaded through `ADC`/`SBB`, the bitwise ops half by half,
`sext`/`zext` from a word (the sign smeared with `SAR 15`, or the high word cleared), `trunc` to a
word (free — the low half is already its own register), loads and stores as two word accesses at
`+0`/`+2`, and the `DX:AX` return. Declined: multiply, divide and the shifts, which need a runtime
helper; and a 32-bit **parameter**, because the prologue loads one word per argument into
`allocation[i]` and a pair breaks that correspondence.

The `ADC` must not be separated from the `ADD` whose carry it reads. It is safe by the scheduler's
own model rather than by luck: flags are ordered RAW, WAR *and* WAW, so every flag-touching
instruction is totally ordered and none can be placed between the two halves — while a `MOV`, which
touches no flags, may freely move through.

### Truth values, and why selection splits blocks

BASIC's comparison result is `-1`/`0`, not `1`/`0`, and the 8086 has no `SETcc`. So a comparison
whose **result is used** — assigned, combined, passed — rather than folded into a branch is
materialized by branching around it, and the `select` that the IR's if-conversion pass leaves behind
goes back to a branch for the same reason (no `CMOV` before the Pentium Pro):

```
    CMP  lhs, rhs            CMP  cond, 0
    MOV  dest, -1            MOV  dest, ifTrue
    Jcc  done                Jne  done
    MOV  dest, 0             MOV  dest, ifFalse
done:                     done:
```

`MOV` does not disturb flags, which is what lets it sit between the compare and the branch.

Structurally this is the larger change: selection can no longer assume **one machine block per IR
block**. Appends go through a block cursor, and the out-of-SSA phi copies for an IR block must be
inserted in whichever machine block control finally *leaves* from — not the one it entered. Getting
that wrong would put a loop-carried copy on an unreachable path, so it has its own regression test.

A later `sext` of such a comparison costs nothing: the value is already a full word of `-1`/`0`,
which is exactly what the widening would have produced.

**Verified.** Gated behind `UseExperimentalBackend` (`PBC_X_BACKEND` / `--x-backend`), default off (a new
path alongside the battle-tested direct codegen). With it **forced on, all 241 differential batteries
are byte-identical to the genuine compilers** — every eligible integer function across the corpus is
compiled by the back end (register-allocated and scheduled), output-identical to the oracle — and the
2135 unit tests pass with it off (no-op). So register reassignment + instruction scheduling now reach
real programs through the in-house back end, end to end, oracle-verified. Widening eligibility (float/
x87 results, string/array params, the main body) is future work; the integer-function path is live and
verified.

### The data-layout bridge: globals, shared arrays, and STATIC locals

The back end lays out no data of its own — the whole-program codegen does — so a scalar global access
becomes a named `MOperand.DataCell` that resolves at emission to exactly the `Mem` the direct emitter
uses for that symbol (`TryDirectCell`). A shared-array GEP starts from `MOperand.DataOffset(g.name)` and
adds its constant or runtime byte offset. Both paths therefore address the same storage.

The question that had to be settled first was whether that cell can be **stale**, since the
cell-sharing prototype was reverted for exactly that reason. It cannot, for two independent reasons,
and both are properties of the existing code rather than assumptions:

- a global a *procedure* can see is `SHARED`, and `SsaForm.IsTrackableShape` excludes `IsShared`
  variables from SSA tracking — so no store to one is ever elided by dead-store elimination and no
  read of one is ever folded to a constant;
- register residency, which could otherwise hold the value in `SI`/`DI` while the cell went stale,
  requires an `SI`/`DI`-clean region — and a call is not clean, so a loop containing a call to the
  routed function cannot keep the global in a register.

A `STATIC` local also borrows its direct-emitter cell. Its IR name is
`static.<procedure>[.<overload-index>].<local>`, so same-named locals in different procedures cannot
alias and the emission bridge can recover the exact `VariableSymbol`. A synthesized IR global such as
`.data_cursor` still has no source symbol to borrow; it declines, and the emitter throws rather than
guessing if routing ever admits one it cannot address.

### Integer switches on a 16-bit target

`IrSwitch` is selected without relying on an 80186 instruction or a target-owned jump-table format.
An 8/16-bit selector becomes repeated `CMP`/`JE` pairs followed by a jump to the default. A 32-bit
selector is grouped by high word: the dispatch block chooses a high-word group, and that block compares
the low-word cases. Equality therefore covers all 32 bits using ordinary 8086 word operations.

The IR defines switch equality by fixed-width bits, so signed and unsigned spellings of the same case
pattern match throughout SimplifyCFG, SCCP, and selection (`i16 -1` equals `i16 65535`). Verification
rejects non-integer conditions, out-of-width cases, and duplicate bit patterns before those stages run.

The introduced machine-only group blocks carry exact successor metadata. Phi copies for every IR edge
remain in the original dispatch block before its first conditional jump, so later scheduling and
liveness see the values on every path. Language-level `ON GOTO` first coerces its selector to the
historical 16-bit `INTEGER`, so `65537&` selects arm 1 exactly as it does in the direct emitter. Focused
execution covers negative, zero, all in-range arms, the above-range default, and that LONG truncation;
target phis and general raw-I32 switches are separately selected, scheduled, and allocated. This
removes the last named-procedure decline in the current corpus: selection/routing moves **224 → 225 of
240** with allocation declines still at zero.

### Binary-record strings and DX:AX runtime results

The `MKI$`, `MKL$`, `MKDWD$`, `MKS$`, and `MKD$` runtime declarations and their
`MKBYT$`/`MKWRD$`/`MKE$` aliases now map to separately trimmable wrappers. Integer values are staged
little-endian in `rt_scratch`; IEEE `SINGLE` and `DOUBLE` values are staged with declared-width x87
stores. Each wrapper then allocates and returns an owned BASIC string through the existing
string-memory kernel. Their inverse `CVI`, `CVL`, `CVDWD`, `CVS`, and `CVD` calls, plus
`CVBYT`/`CVWRD`/`CVE`, share the runtime's padding/copy kernel and load the exact scratch bytes at the
declared integer or IEEE width. Both one-argument and start-offset CV forms use this path.

Runtime calls returning a dword in `DX:AX` now copy both physical words to a fresh virtual-register
pair immediately after the call. The same explicit result convention covers integer-range `RND`,
`LOF`, and `LOC`; scheduler dependencies and allocation therefore preserve both halves instead of
silently treating the answer as a word.

This routes `DIFF08` and `DIFF58` end to end. At that milestone the census was **142/164 programs lowered**,
**227/240 functions selected and routed**, and **129/142 lowered module bodies owned**, with zero
allocation declines. The differential is **260 participating, 251 agreeing, 9 emulator-limited, and
0 disagreeing**; `DIFF08`'s two executions reach the executor's unimplemented DOS device-information
call, while `DIFF58` agrees in both modes.

### Segmented raw-memory comparison

Whole-value `TYPE`/`UNION` equality lowers to `rt_mem_compare(ptr, ptr, i32)`. On a segmented target,
an opaque near offset is insufficient: a module object lives in DS while a procedure-local object
lives in SS. The runtime ABI therefore derives each pointer's segment from its IR base and passes
`DX:SI` for the left address, `BX:DI` for the right, and the byte count in CX.

The separately trimmable `rt_memcmp` kernel installs those segments in DS/ES, compares unsigned bytes
with `REPE CMPSB`, restores both segment registers, and returns -1/0/1 in AX. Selection sign-extends
that word into the IR declaration's i32 result. Tests cover both DS globals and SS frame objects, plus
optimized and unoptimized execution against the direct emitter.

`DIFF10` now routes and agrees in both modes. The current census is **142/164 programs lowered**,
**228/240 functions selected and routed**, and **130/142 lowered module bodies owned**, with zero
allocation declines. The differential is **262 participating, 253 agreeing, 9 emulator-limited, and
0 disagreeing**.

### Segmented raw-memory copy and fill

Whole-record assignment and static-array `ERASE` lower to LLVM's `memcpy` and `memset` intrinsics.
The x86-16 ABI preserves their segmented addresses: each pointer carries a near offset plus a segment
derived from its IR base, using DS for module/static storage and SS for frame storage. The copy kernel
takes the source in `DX:SI`, the destination in `BX:DI`, and the exact byte count in CX; the fill
kernel takes `BX:DI`, the fill byte in AL, and the exact byte count in CX. The intrinsic volatility
operand must remain a constant i1 marker and has no runtime register slot.

The separately trimmable `rt_memcpy` and `rt_memset` entries install the destination segment in ES,
use `REP MOVSB`/`REP STOSB`, and restore every segment register they change. Byte operations preserve
odd-sized record tails instead of rounding them away. Focused tests compare optimized and unoptimized
images with the direct emitter for a seven-byte record copy and a static-array zero fill.

`DIFF23` and `DIFF74` now route as whole module bodies. Selection/routing moves **228 → 230 of 240**,
ownership moves **130 → 132 of 142**, and allocation declines remain zero. The differential moves to
**266 participating, 256 agreeing, 10 emulator-limited, and 0 disagreeing**; one of the four newly
participating executions reaches an existing direct-emitter test-CPU opcode limitation.

### Signed division: physical pins and the long-runtime bridge

`IDIV` is the first selected instruction that is **fixed to physical registers**: it divides `DX:AX`
and answers with the quotient in `AX` and the remainder in `DX`. Everything else in the machine IR
names its registers in its operands, so this is where two mechanisms have to carry the pin.

- **Allocation.** The `MOV AX,<dividend>`, `CWD` and `IDIV` all declare `Clobbers = [AX, DX]`, which
  is what keeps a live value (or the dividend itself) out of the pair. The result-reading
  `MOV <dest>, AX|DX` deliberately declares none, so the quotient may be allocated to `AX` again and
  the copy costs nothing.
- **Scheduling.** `MachineScheduler` used to build its dependency keys from the operands alone, so a
  clobberer and the `MOV` that reads its result out of `AX` had *no edge between them* and the list
  scheduler was free to hoist the read above the divide. A clobber now counts as a write, which also
  closes the same latent hole for `CALL`. Explicit physical clobbers are also scheduling barriers:
  before allocation, virtual work moved into a pinned sequence could later receive the pinned
  register and overwrite its argument or result.

The inline 16-bit form selects only a **non-zero compile-time constant** divisor. A constant that is
not zero cannot trap, exactly where the direct emitter drops the Error 11 guard (`O0220`); `-1`
declines because `MININT \ -1` would overflow the hardware `IDIV`.

Signed 32-bit `SDiv`/`SRem` instead use the runtime convention the direct emitter already relies on:
dividend in `DX:AX`, divisor in `CX:BX`, result in `DX:AX`, through `rt_ldiv`/`rt_lmod`. The shared
pair-call selector pins all four argument registers, declares the caller-saved clobbers, and copies
both result words back into virtual registers. Runtime divisors are safe here because the helper owns
the language path: zero calls `rt_raise` with Error 11, signed division truncates toward zero, the
remainder takes the dividend's sign, and `MINLONG \ -1` retains the established wrapped result.

### Selection is not routing

The census reports two numbers, because the first overstates the second: `BackendProcs` also
**schedules and allocates**, and not every selected value can yet be preserved across a `CALL`.
`functions routed` counts the functions that survive both, and is the honest coverage
figure. It also splits the decline histogram into all functions versus *named procedures only* -
routing a module body (`main`) additionally needs the whole startup/exit sequence, so what blocks a
procedure is always the cheaper next increment. That split corrected a wrong reading of the earlier
histogram: the `rt_*` declines are **not** all `main`. 38 of the 47 procedure declines are calls to
runtime declarations, which makes the runtime-label bridge the ranking's next target.

### The runtime-label bridge

The IR declares the runtime C-style — `rt_print_str(ptr, i32)` — because the same IR also feeds the C
and LLVM back ends, where a runtime call really is a C call. `DosRuntime`, which the direct emitter
calls, is register-based and vintage-shaped: the string entry wants its address in `SI` and its length
in `CX`, and **nothing is pushed at all**. `Backend/RuntimeAbi.cs` is the mapping — one entry per
routine giving the label to call, where each IR argument goes (`Word`, `Pair` for `DX:AX`, or `Offset`
for the address of a literal), and what the routine destroys.

It is deliberately a short explicit table rather than a convention: each entry is a claim about a
specific hand-written assembly routine, and a wrong claim miscompiles silently. Everything unlisted
declines by name, so the census keeps ranking what to add next. Two supporting pieces came with it:

- **`MOperand.DataOffset`** — the *address* of a data object rather than its contents. It resolves
  through the same codegen-owned data resolver as `DataCell`, so a routed `PRINT "HI"` takes the
  offset of the identical pooled literal a directly-emitted one would.
- **the trimmer needs no change.** `RuntimeTrimmer` seeds from the named labels emitted user code
  references and no user code bound — and a back-end `CALL` references that very label, so a section
  only a routed function needs survives trimming by construction.

The clobber set is the full caller-saved file. The print routines do in fact save and restore
everything they touch, but "in fact" is not "provably", and a claim one register too small
miscompiles a value that is never recomputed; narrowing it needs a mechanical check of each routine's
push/pop discipline standing behind it.

Constant signed QUAD printing is now on the bridge as well. Genuine PBC routes it through the
15-digit DOUBLE formatter even though the value remains an integer on x87. The selector writes all
four words of the `i64` constant into an eight-byte frame cell, `FILD`s that qword, and maps
`rt_print_i64` / `rt_fprint_i64` to `rt_print_f64`. A non-constant `i64` still declines: admitting one
before machine IR has a general 64-bit representation would silently truncate it.

At that point the conservatism was the binding constraint, and the census said so plainly: selection went
**15 → 38** functions, while routing went only **14 → 18**. The other 20 select and then lose their
allocation, because a parameter is live from the prologue and a value live across a `CALL` has no
register while there is no spilling. Spilling to the frame — not more selection — is what the ranking
points at next.

### A parameter BLOCK is a set of named cells, not an address to index

`ARRAY SORT` and `ARRAY SCAN` take every parameter from memory: the shared `rt_arpb` block and the
`rt_num_*` cells. The IR can fill those itself — the selector addresses any global whose name starts
with `rt_`, which is exactly what a runtime cell is — so the whole statement is stores plus a call,
with one exception. The descriptor those routines dereference opens with the SEGMENT its elements live
in, and a segment register is not a value the IR can name; it must also live where DS reaches it,
which a frame object of a routed function does not promise. `rt_arr_desc` (DosRuntime.ArrayDesc)
takes the near address, the bounds and the element size and supplies the rest — the same reason
`CSRLIN`, `VARSEG` and the bare `DEF SEG` became one-instruction routines.

The lesson worth keeping is how the *fields* are addressed. The first version spelled the block the
way the runtime does, as displacements off `rt_arpb`, which in the IR is a GEP: an address in a
register, and one register per field. A register holding a memory BASE is the one thing the spiller
cannot move, the scheduler hoists all the address materializations to the top of the block, and
filling six fields therefore wants six such registers live at once — one more than this machine has
to give. The function selected cleanly and then declined at allocation with **nothing in the machine
IR looking wrong**: every instruction reads correctly, and the only symptom is
`no register assignment, and nothing left that can move to memory`.

Naming each field in the runtime data (`rt_arpb_start`, `rt_arpb_count`, … — the same twenty bytes,
with labels) removes the registers entirely: each store becomes `MOV word [rt_arpb_start], imm`,
which is what the direct emitter writes anyway. A block the IR has to INDEX costs a register per
field; a block whose fields have names costs none.

### Spilling, rematerialization and live-range splitting

The allocation failure that matters on this target is not "six registers ran out". It is a `CALL`: it
destroys the whole caller-saved file, so a value live across one may sit in *none* of the six. Before
spilling existed, those functions were selected and then silently dropped — which is exactly what the
`selected` / `routed` split in the census was added to expose.

x86 is a memory-operand machine, so the cheapest spill needs no reload code: the value simply **is**
its frame cell, and every instruction that can legally name memory uses that cell.
`Backend/Spiller.cs` tries these forms in order:

- an incoming **parameter** is already in the frame where the caller pushed it, and an IR argument is
  an SSA value nothing writes — so the spill is free: the prologue copy disappears and the uses
  address `[BP+6]` directly (`MOperand.ParamCell`);
- any other value gets a fresh stack slot, and its defining instruction writes there.
- a read-only pointer parameter used as a base/index reloads from its incoming cell immediately before
  each dereference, because x86 cannot use memory as a memory-address base;
- a stable `LEA` address is rematerialized with a **fresh virtual register per use**. Reusing the old
  virtual id would leave one live interval spanning every call. Chained `LEA`/GEP addresses use the
  same rule recursively;
- a value that would require an illegal memory-to-memory operand is split through one shared spill
  cell. Every definition receives a fresh virtual id and stores to the cell; every use receives its
  own reload. This includes out-of-SSA phi destinations with definitions in several predecessors and
  read-modify-write definitions, which reload before updating and store afterwards;
- an immutable argument whose use cannot name its caller-owned cell directly reloads immediately
  before each use.

Direct memory rewriting remains conservative: only instruction forms the emitter really has, and
never two memory operands in one instruction. Every spill cell is allocated at the value's widest
view, while each reference retains its own byte/word width. Machine booleans remain BASIC truth words
(`-1`/`0`); only genuine 8-bit values use `AL`/`CL`/`DL`/`BL`.

**The scheduler was manufacturing the pressure it then failed on.** Scheduling runs before allocation,
and a list scheduler with no reason to care about live ranges will happily hoist a value's definition
above a `CALL` — stretching it across the caller-saved file. A `CALL` is now a scheduling barrier:
nothing is gained by moving work across one on a target with no register renaming to hide a latency
behind, and this is what is lost. A call is a barrier, as is every explicit physical clobber: the
latter prevents fixed ABI setup such as `MOV SI,literal` from moving above virtual work that allocation
may later assign to `SI`. With the call barrier, routing went 22 → 32.

Together the two took routing from **14 to 32 of 139** corpus functions this round (38 select).

The current census is **142/164 programs lowered**, **230/240 functions selected**, **230/240
functions routed**, and **132/142 lowered module bodies owned end to end**. Allocation declines are
zero: all selected functions now survive scheduling and allocation. The last 19 required
multi-definition phi splitting, read-modify-write splitting, per-use parameter reloads, and
width-correct byte/word spill references.

### Strings and files on the bridge — and what the census was really measuring

Two more families joined `RuntimeAbi`, and between them they moved selection 38 → 66 and
allocation 32 → 51 of the corpus's 139 functions:

- **string constants.** The IR's `rt_str_const(ptr, i32) -> ptr` is the runtime's `rt_strmem`, which
  takes the bytes as `DS:SI` with the length in `CX` and answers with a handle in `AX`. That needed
  two additions to the table: a **result register**, and **presets** — the register-to-register moves
  a convention requires beyond the arguments, here the `MOV DX, DS` naming the segment the literal
  lives in.
- **files.** `rt_fopen` (`AX` = filename handle, `BX` = file number, `CX` = mode, `SI` = record
  length), `rt_fclose`, `rt_fcloseall`. `PRINT #n` has no per-file entries at all: `rt_fselect` routes
  the *console* routines at a file, and the caller resets `rt_curout`/`rt_colptr` afterwards. The IR
  models one call per printed item, so the select/restore pair wraps each item rather than the whole
  statement — the same observable column accounting, since nothing else runs between items. Runtime
  data cells (`rt_curout`, `rt_col`, `rt_colptr`) resolve through the same data resolver as module
  variables, so both paths write the identical words.

File I/O turned out to stand in front of almost every module body: 115 of the 136 battery programs
`OPEN` a file, because that is how the differential harness records its results. It was the single
blocker hiding behind the earlier `rt_str_const` count.

One caveat the numbers deserve: the census measures the back end's **capability** — what selects and
allocates. `CodeGenerator.BackendProcs` routes only entries of `model.ProcedureList`, so a `main` body
that now selects and allocates is still emitted by the direct path. Routing it needs the module
frame and the startup/exit sequence, and is the next structural step rather than another table entry.

### String concatenation and the 32-bit multiply

Two more, both taken from the register conventions the runtime documents at the head of its partials:
`rt_str_concat(ptr, ptr) -> ptr` is `rt_strcat` (`AX` = left, `DX` = right, result in `AX`, consuming
both), and a 32-bit `Mul` — which x86-16 simply does not have — is `rt_lmul` (`left DX:AX`,
`right CX:BX`, result in `DX:AX`). The multiply is not a table entry, because it is not an IR call at
all: `SelectWideBinary` emits the call itself when the pair form has no instruction. Each of the four
pinned loads declares the whole pinned set as its clobbers, so nothing live is parked in a register
the sequence is about to overwrite; the call then spills whatever else was live, like any other.

### Floating point declines up front — a latent half-load, closed

Selection sizes a scalar value from its bit width, and nothing in the scalar path asked whether the
value was a float. A `SINGLE` load therefore minted **one Dword virtual register and emitted a single
word-sized `MOV`** — half the value, carried on as if it were the whole one. Nothing reached it,
because float *arithmetic* declined earlier, but a plain `a! = b!` copy would have.

That is exactly the silent truncation 32-bit integers were found to have, and the lesson from that
one was that a coverage number is only worth having when every function under it is right. So the
guard is up front and blunt: any function mentioning a floating-point type anywhere declines with the
type named. It costs nothing today (the selected count did not move) and it is the single place to
relax when x87 lands.

With it in place, the census ranks honestly: **38 of the 70 remaining declines are floating point**
(25 × `f32`, 13 × `f64`) — the x87 stack, which is not a register file the linear-scan allocator
models, is now the largest single thing between this back end and the corpus.

### x87 — the values that do not live in registers

Floating point was the largest single blocker (38 of 70 declines), and it does not fit the machine
model at all: x87 computes on a **stack**, not in a register file the linear-scan allocator can hand
out. The answer is not to make it fit. Every float SSA value lives in a **frame cell**, and each
operation is bracketed `FLD ... FSTP`, so the x87 stack is empty again at every instruction boundary
and nothing the allocator models is involved. That is also where the direct emitter keeps a float
between operations, so the two paths agree on the representation.

- `FLD lhs; FLD rhs; F<op>P; FSTP result`. Pushing the **left** operand first leaves it in `ST(1)`,
  and the popping arithmetic computes `ST(1) op ST(0)` — so `FSUBP`/`FDIVP` come out the right way
  round. Getting that backwards is silent and wrong, which is why it has its own test.
- **Literals** resolve through the code generator's own float pool, which stores every constant as a
  qword double whatever its source precision. An unsuffixed literal is quantized to `SINGLE` before
  widening into that qword, so it has the identical bits the direct emitter loads. Constant FOR steps
  go through the same expression-lowering and coercion path instead of bypassing this source boundary.
- **Comparisons used as values** emit `FLD lhs; FLD rhs; FXCH; FCOMPP; FSTSW AX; SAHF`, then the same
  branch diamond integer comparisons use to materialize BASIC's `-1`/`0`. The x87 condition bits map
  to unsigned integer conditions. `FSTSW AX` exposes its physical clobber and `SAHF` its AX/flags use,
  so scheduling and allocation preserve the hidden hardware dependency.
- **`SIToFP`** parks the integer in a cell first (x87 reads integers from memory only), a word for an
  `INTEGER` and both halves of the pair for a `LONG`, then `FILD`s it at that width.
- **A float result** is left on `ST(0)` and deliberately *not* popped — "Results: AX / DX:AX / ST0 /
  string handle in AX".
- **Printing** goes to `rt_print_f32` or `rt_print_f64` by the **source type**. They share a body but
  set different significant-digit counts (7 against 15/16, and the dialect moves it), which is
  precisely the rendering the fidelity tests compare — by the time the value is on the stack the
  format is gone, so the entry has to be chosen from the IR type.

`MRegSize` grew a `Qword` that never names a register: it names a memory width, which is what an x87
load or store needs and what a word-sized reference would have silently halved.

Selection 69 → 81 of 139, routing 52 → 57.

### IEEE real procedure calls - declared width on the stack, x87 width between operations

The procedure bridge now admits BYVAL `SINGLE`/`DOUBLE` parameters and functions returning those
types. It preserves the direct emitter's BASIC/PASCAL stack ABI without pretending x87 values belong
in the integer register allocator:

- an incoming real remains in its caller-owned parameter cell, and `FLD` reads that cell at its
  declared dword/qword width;
- a call argument is first `FSTP`ed from its ten-byte SSA cell into a declared-width staging slot,
  both producing the IEEE stack representation and applying the language's parameter rounding;
- the staging words are pushed high to low, so the low word lands at the callee's parameter offset;
- a function returns its real on `ST(0)`, and the caller immediately `FSTP`s it into its own ten-byte
  result cell, leaving the x87 stack empty at the next machine-IR boundary.

The staging store and pushes also exposed a scheduler fact that register-only arguments had hidden:
`PUSH [memory]` reads its source as well as writing the hardware stack. The machine effect now says so,
preventing a push from crossing the store that produces its bytes.

This removes four census declines and moves selection/routing **205 → 209 of 233**, with whole-module
ownership **113 → 116 of 135** and allocation declines still at zero. The execution differential stays
at 234 participating, 228 agreeing, 6 emulator-limited, and 0 disagreeing because each newly owned
program already had another routed procedure and therefore already counted as participating.

### The remaining existing DOS string kernels

Three IR declarations were still declining even though the DOS runtime already contained exact
kernels for them. Their mappings are now explicit in `RuntimeAbi`, preserving the rule that no runtime
convention is guessed:

- `rt_str_compare(left,right)` is consuming `rt_strcmp`, with handles in `AX`/`DX`. Its word-sized
  `-1`/`0`/`1` result in `AX` is sign-extended with `CWD` to the IR's `i32` pair.
- `rt_str_mid2(value,start)` is `rt_strmid` in `AX`/`CX`, with the direct emitter's `DX=7FFFh`
  maximum-length preset.
- `rt_str_mid_assign(target,start,limit,replacement)` is `rt_midset` in `AX`/`CX`/`BX`/`DX`. It
  consumes the replacement, mutates the duplicated target, and restores that target handle in `AX`.

The tests pin every register, the preset, and the result extension, then execute dynamic INTEGER
positions through both optimized and unoptimized routed builds against the direct emitter. A true
32-bit position that cannot be proven to originate in a word still declines instead of being silently
truncated; its dialect-specific overflow behavior needs oracle evidence.

The four former decline sites become complete module bodies at this milestone: `DIFF02`, `DIFF40`,
`DIFF54`, and `STRINGS`. Selection/routing moves **209 → 213 of 233**, module ownership
**116 → 120 of 135**, and the corpus differential moves to **242 participating, 235 agreeing,
7 emulator-limited, 0 disagreeing**.

### Routing the module body

`BackendProcs` only ever looked at `model.ProcedureList`, so a `main` that selected and allocated was
still emitted by the direct path — the back end compiled *functions*, never a program. It now routes
the module body too. Three things follow from `main` not being a procedure:

- it takes no arguments, so the prologue loads none;
- it has no caller to `RET` to. `MachineEmitter.EmitFunction` takes an `onReturn` hook, and for the
  module body that emits the implicit `END` (`MOV AL,0` / `JMP rt_exit`) the direct path emits — the
  frame teardown and `RET n` would be both wrong and unreachable;
- it is not in `ProcedureList`, so the routing looks it up by name in the IR module.

Everything it calls must itself be routed, for the ABI reason the procedure fixpoint already covers.
`ON ERROR` and `CHAIN` disqualify it outright: both are emitted *around* the body by the direct path,
not inside it, so a routed body would silently lose them.

The census reports this as its own figure, because it is the one that matters for the goal: **25 of
the 106 lowered programs** are module bodies the back end can own end to end. That is the first time
the number has been anything but zero.

### A memory operand can name its own segment (`DIM … AT`)

`MOperand.Memory` was a base, an index, a scale and a displacement, all implicitly `DS` or `SS`. That
is every address a PowerBASIC program forms except one: `DIM a(…) AT segment` declares an array that
is a **view** of memory the program does not own — the text screen at `&HB800`, a BIOS area — and its
elements are reached through that segment and no other.

The SSA IR says it with `IrFarPtr(segment, offset)`, and the machine operand carries a `Segment`
register beside its base. Two decisions are load-bearing:

- the segment is part of the **operand**, not a `MOV ES, reg` instruction of its own. x86-16 reaches a
  non-default segment through a segment register, so the value has to be moved into `ES` before the
  access, and every gap between the two is a window for a later pass — scheduling, a spill reload, a
  rematerialized address — to put something in. `MachineEmitter` writes the move immediately in front
  of the instruction it belongs to; nothing upstream ever saw two things to separate. An x86
  instruction has at most one memory operand, so at most one segment is ever in play;
- a far pointer is **not** interchangeable with a near one. Only a load or a store directly through
  one selects; `TryOperand` finds no register for it anywhere else, so a `GEP` on top of one, a BYREF
  argument or a runtime-routine argument declines the whole function. That is not belt-and-braces:
  passing `a%(0)` of an `AT` array to a BYREF `SUB` lowered cleanly and the two back ends then
  disagreed about what the `SUB` had incremented, which is how the lowering came to refuse every use
  of an element address that is not the element's own value.

A constant offset folds into the displacement (`ES:[0020h]`), so a fixed-subscript sequence spends no
address register at all. The segment does need one — there is no `MOV ES, imm` — and sixteen far
stores in a row therefore supply sixteen independent `MOV reg, imm`, all of which the list scheduler
happily hoists to the head of the block. `Spiller.RematerializeOne` now recomputes a
`MOV reg, immediate` at each use for the same reason it already recomputed an `LEA`: it depends on
nothing, so a copy beside the use is free, and every live range collapses to one instruction.

`HUGE`, `VIRTUAL`, `EMS` and `XMS` still decline. One segment named at the declaration is not enough
for them: `HUGE` steps the segment by `byteOffset >> 4` so a single array spans many, and `VIRTUAL`
maps a 16 KiB EMS page pair into a window before each access.
### A QUAD in storage, and the two instructions that move eight bytes

A 64-bit integer has no register representation here - it would want four - so the selector used to
take a QUAD load down the *scalar* path, which sizes a value from its bit width and minted one
dword-sized register for it. That is half the value, silently, and the matching store wrote the low
half through a 386 operand-size prefix on a target that has no such instruction. Neither showed up
until `DIFF86`'s module body routed, because until then the call that consumed the value declined
first and took the whole function with it.

`SelectQwordLoad`/`SelectQwordStore` give a QUAD a frame cell of its own and move it with the only
unit that handles eight bytes at once: `FILD qword` then `FISTP qword`. Both directions are exact -
the x87 mantissa is sixty-four bits wide, which is precisely what an int64 needs - so this is a copy
and not a conversion. The copy happens at the LOAD, not at the use: an SSA load names the bytes at
that point, and re-reading the source later would see whatever a store in between had put there.
`ArgKind.SignedQwordSt0` then takes the cell as it already takes a literal's staging cell, and
printing a non-constant QUAD stops declining.

### FIX and BCD: one of them is a float and the other is not

`@` and `@@` look like one feature and are two. A BCD cell is ten bytes of x87 extended, so its bits
ARE the value and it maps to `f80` with no conversion in either direction. A FIX cell is a scaled
INT64 - the number times ten to the power of `pbvFixDigits` - so it maps to `i64`, a read divides
and a write multiplies and rounds to the nearest integer.

That exponent lives in a RUNTIME cell, which is what decides the shape: the conversions are calls to
`rt_fixdn` / `rt_fixup` (both ST(0) in, ST(0) out, one new ABI row each), not arithmetic the lowering
folds. A compile-time divide by 100 agrees with every FIX program in the corpus and stops agreeing
the moment one assigns `pbvFixDigits` - which the direct emitter's literal-store fold demonstrates,
writing at two digits and reading back at four.

No new `IrCastOp` was needed for either. The storage-versus-value distinction MBF carries in the type
system is carried here by the type map plus `Coerce`, and the scaling by a runtime call the ABI table
already knows how to bridge - which also means the qword load and store above are what make it
selectable at all.

### Two cells the IR was inventing rather than sharing

Routing `DIFF20` and `DIFF16` each exposed a cell the IR had quietly given itself a private copy of,
and the corpus differential caught both by running the images rather than reading them:

- **`rt_erl`.** The direct emitter writes the line number into it at every NUMERIC line label when
  error handling is in scope; the lowering wrote it nowhere, so every handler asking `ERL` read a
  zero. Alphabetic labels do not count, which is PB's rule and not an omission.
- **`rt_pbv_fixdigits`, and every other PB internal variable.** `SlotFor` gave `pbvFixDigits` a frame
  slot of its own, so `PRINT pbvFixDigits` answered 0 while `rt_fixdn` - reading the runtime's cell,
  three instructions away - scaled by 2. Internal variables now resolve to their runtime label, which
  the data-layout bridge already addresses because it begins with `rt_`.

### Inline asm can name CODE as well as storage

The lowering binds each identifier in a `!` block to the storage it denotes, and refused the whole
block when a name bound to nothing. One class of name binds to nothing on purpose: the string-manager
routines PB publishes an inline-asm ABI for (`GetStrLoc`, `GetStrLen`, `GetStrAlloc`, `RlsStrAlloc`).
They are code, so there is no cell to pair with them and nothing about them depends on which back end
laid out the frame - the machine emitter resolves them to the runtime's own labels. `DIFF20` pushes a
string handle, calls `GetStrLoc` and reads the far pointer it answers with; every variable in that
block, the handle included, already bound.

## The x87 stack is not in the machine IR

`MInstrEffect` names registers, flags and memory. It has no name for the x87 stack, so nothing an x87
instruction does to it appears in its descriptor: `FADDP` and `FSQRT` take no operands and touch no
memory, and to a scheduler reading effects they depend on nothing at all.

That produced two miscompiles. An `FSQRT` was moved past the `FSTP` that captured its answer, so
`SQR(16)` printed 16 - visible only once a call landed between the two, because until then there was
nothing to reorder against. Later a `FADDP` was moved out from between the `FLD`s that set up its
operands, so a DOUBLE accumulated round a loop printed the addend instead of the sum; that one was
latent in the float binary path from the start and surfaced when float phis put enough x87 in one
block to give the scheduler a choice.

Both were first patched by declaring the instruction to read and write memory. That worked, was untrue,
and over-ordered: it pinned every unrelated integer load and store against every x87 operation. The fix
in place now names the real resource - `MOpcodes.UsesX87`, and `MachineScheduler` orders any two x87
instructions against each other and against nothing else. The effects are truthful again.

Anything added to the x87 opcode set must be added to `UsesX87` at the same time; `MachineSchedulerX87Tests`
covers the current set.

## `ptrtoint`: an address read as a number

`VARPTR(x)` is the offset of a variable, and on this target a near pointer **is** that offset — the
direct emitter answers it with one `LEA AX, cell`. So the IR needs no new operation for it: the
lowering forms the same address `VARPTR32` forms (`AddressOfStorage`, which addresses a variable, a
static-array element, a record field or the place another pointer points at) and casts it with the
`IrCastOp.PtrToInt` the verifier and the C and LLVM emitters already understood. The operand is
addressed, never loaded, which is what makes `VARPTR` of a string name the handle's cell rather than
its characters.

Selecting the cast costs no instruction. A frame object already has its address in a virtual register
— its own `alloca` emitted the `LEA` — so the cast is a **rename**, the same change of view the
`Trunc i16 -> u8` form performs when a byte is the low half of the word already holding it. A
module-level or `STATIC` variable is a data *label* rather than a register, so there is nothing to
rename and its offset is materialized as `MOV dest, OFFSET cell`, exactly as an indexed access into
the same object already does. Anything else declines rather than guessing an address.

One consequence is worth stating because it is load-bearing rather than incidental: `Mem2Reg` promotes
an `alloca` only when every use is a direct load or store of it, and a `ptrtoint` is neither. Taking a
variable's address therefore pins it in memory for the whole function, which is precisely what makes
`POKE VARPTR(v), n` reach `v` at all.

`LOWLEVEL.BAS` joins the IR path with this — **156/164 programs lowered, 251/256 functions selected
and routed, 151/156 module bodies owned**, on 283 corpus comparisons with no disagreement. Its own
module body still declines at *selection*, and for an unrelated reason: `!JNZ AddLoop` names a BASIC
label from inside an inline-assembly block, and the binding pass binds variables.

## `indirectbr`: a branch the CFG can still describe

`CODEPTR32(SomeLabel)` and the `GOTO DWORD` / `GOSUB DWORD` that consume it are the one construct in
this family that needed a new IR **instruction**. A label is an `IrBasicBlock`, not an addressable
value, and `IrBr` / `IrCondBr` / `IrSwitch` all take block targets — none of them can branch to a
number. Half the machinery was already there, because `ON ERROR` needed the same two things:
`IrBlockAddress` (a constant naming a block) and `MOperand.BlockOffset` (the offset of a block's own
label, which only the assembler knows). What was missing was the branch.

`IrIndirectBr(address, targets)` supplies it. The address decides where control goes; the target list
is how the **CFG stays true** — every block whose address the function takes is listed as a successor,
so reachability, liveness and phi placement all see edges to the blocks a computed jump can land on.
That is the difference between this and the `ON ERROR` trade next door: a handler is entered through
an edge no graph can draw, so `IrFunction.HasErrorHandler` takes the whole function out of the
optimizer. A computed jump *can* be drawn, so it is, and the function stays optimized. The lowering
lists every label of the function — a superset, since `CODEPTR32` of a label can only name a label of
the function it is written in, and a superset is sound where a missing target is not. A function with
no labels declines rather than being given an empty list.

Selection is one instruction on each side: `MOV dest, OFFSET lbl` for the address (`ptrtoint` of a
block address), `JMP reg` for the branch, and the `inttoptr` in between is a rename for the same
reason `ptrtoint` is. `CODEPTR32` pairs the offset with `CS` in the high word, which is what the
direct emitter writes; joining them needs a 32-bit shift by sixteen, which on a register pair is not
a shift at all but the two halves changing places — two `MOV`s where the bit-at-a-time loop would
have needed thirty-two steps.

One rule falls out of taking a block's address and is not optional: **a block whose address is taken
cannot be merged away or dropped**. It is the one property of a block carried in a *value* rather than
in an edge, so no CFG rewrite can see it. `IrFunction.AddressTakenBlocks` names them, `SimplifyCfg`
refuses to merge one into its predecessor and treats them as reachability roots, and `Sccp` does the
same. Without it, `CODEPTR(Here)` of a label with nothing in it emitted a `MOV AX, OFFSET lbl.Here`
for a label the emitter had already deleted. `IrCloner` now rewrites a block address into the copy's
own block for the same reason.

`DIFF11` joins the IR path with this — **157/164 programs lowered**, still 283 corpus comparisons with
no disagreement. Its module body declined at *selection* for a while longer, on a limitation that
predates all of this and has nothing to do with code pointers: a plain two-`GOSUB` body did not route
either, because the shared `RETURN` dispatch block is created after the continuations that use its
`GEP` and the selector walked blocks in list order. See the next section for what that cost.

An address printed as a NUMBER usually will not route, and that is nothing to do with `VARPTR`: PB
computes integral `+`/`-`/`*` in floating point, `VARPTR` answers a `WORD`, and `IntegerRecovery`
only closes a float-shaped tree whose leaves are `sitofp` — an unsigned leaf (`uitofp u16 -> f80`)
stops it, and the selector has no form for that conversion. Widening the addresses to `LONG` first
(`p1 = VARPTR(a%(1))`) keeps the arithmetic on the integer path.
## Dynamic array storage: an address space, not a pointer that happens to be elsewhere

`rt_arr_alloc` was the last selection decline that read as a pure signature question. The IR declared
`rt_arr_alloc(i32 count, i32 elementSize)` because the same declaration also feeds the C back end,
where such a function exists; the DOS runtime's routine takes ONE dword — a byte count — in `DX:AX`.
`RuntimeAbi` maps arguments to registers and cannot multiply, so no row could express it.

The reconciliation is that the byte count is formed in the LOWERING (`IrLowering.ArrayBytes`), and both
runtimes take bytes. The element size is a compile-time property of the source type, so the multiply
belongs where the type is known, where it folds to a literal for constant bounds, and where it can be
done at 32 bits. That last part is not incidental: 20000 LONGs is 80000 bytes, a 16-bit product says
14464, and the difference between the two is a program that writes 65 KB past the end of its array
versus one that gets the runtime's own Error 7. The `_ptr` entries stay count-based for the opposite
reason — their element is a target pointer, whose width only the runtime knows.

### The part that was not a signature question

Closing the signature alone routes both programs and **miscompiles**. The block `rt_arr_alloc` answers
with is an offset into `rt_arrseg`, a segment 128 KB above the program; the back end had no way to say
so, so every element access came out `MOV [BX], AX` against `DS`. It printed the right numbers — an
element written and read through the same wrong address round-trips perfectly — while writing over the
program's own code. A 2000-element array put 8000 bytes across `DS:0000`–`DS:1F40`, which is the PSP
and the runtime's already-executed entry stub, and the test still passed.

What was missing was not a table row but a distinction: a pointer into the far array heap is a
different KIND of pointer. `IrType` gained an `AddressSpace` (docs/IR.md), `rt_arr_alloc` answers in
space 1, the descriptor cell is allocated as one, and a GEP inherits it from its base — so the fact
survives a phi, a load and an index without the back end re-deriving it. `MOperand.Memory` gained a
`SegmentCell`, and `MachineEmitter` reloads `ES` from it immediately before the access. Emission, not
selection: the load and its use are then adjacent bytes, so neither the scheduler nor the spiller can
put a call between them.

`ARRAY.BAS` becomes the 152nd owned module body, selection moves **251 → 253 of 255** and the corpus
differential **283 → 285 agreeing, 0 disagreeing**.

### What routing ARRAY.BAS uncovered: a reserved register nobody spoke for

`pts(2) = pts(1)` between two elements of a static UDT array came out as zeros. The defect predates
this work — it reproduces at the branch point with no dynamic array in the program — and was invisible
only because `ARRAY.BAS` declined at selection before reaching it.

The staging for `rt_memcpy` is `MOV DI,dest / MOV BX,SS / MOV SI,src / MOV DX,SS`, and each move
carries the registers filled so far as its clobber list, so a value live ACROSS the sequence avoids
them. But a value defined and consumed entirely INSIDE the sequence is live across none of those moves,
and nothing spoke for it. The spiller puts values exactly there: rematerializing the source address
placed `LEA v,[BP-30]` between the second and third move, its one-instruction interval saw no clobber,
and BX — holding the destination SEGMENT — was the first register free. `rt_memcpy` then wrote ten
bytes into a segment made out of a frame offset.

Every instruction was defensible on its own, which is the signature of a reservation with no owner.
The fix is in `Spiller`: anything it inserts claims the staging pending where it lands
(`WithPendingStaging`), for rematerialized addresses, argument reloads and split reloads alike.

## `USING$`: PRINT USING captured into a string

The runtime's print routines all consult one cell, `rt_capmode`: zero writes to the current output
handle, anything else appends to `rt_capbuf` and counts in `rt_caplen`. That is what already makes
`STR$` the printed form of a number rather than a second formatter, and it is the whole of `USING$` —
the same field emission `PRINT USING` performs, run with the output pointed at a buffer.

So the lowering is composition: `rt_capon`, the shared `EmitUsingBody`, `rt_capoff`, which hands the
captured bytes back as a string handle. The two halves are new **routines** (`DosRuntime.Capture.cs`)
for the reason `rt_lpon`/`rt_lpoff` are: the direct emitter writes the four instructions inline, and
a store of one runtime cell's *offset* into another is not something the IR can say. Neither emitter's
output moves; there is now somewhere to `CALL`. They are declined by the C back end alongside
`rt_using_field`, because a stub cannot have the property that matters — that `rt_print_*` stop
reaching stdout while it is on.

`DIFF14.BAS` does **not** join the IR path on the back of this. Its decline moves to the next
construct in the same program, `EXIT FAR`, so the program count is unchanged and only the reason is.
(It joins the path when `EXIT FAR` lowers, below — and stops one level further on, still short of
routing.)

## A BASIC label inside an inline-assembly block

`!JNZ AddLoop` names a **label**, not a variable, and the binding pass bound variables. Everything the
answer needs was already here: a label is an `IrBasicBlock`, its address is the `IrBlockAddress` that
`ON ERROR` and `CODEPTR32` are named by, and the machine operand for it is the `MOperand.BlockOffset`
those two already select. So the lowering binds the name to a block address, the selector answers with
the block's offset instead of a frame cell, and `MachineEmitter` hands the assembler an
`AsmSymbol.OfLabel` rather than an `AsmSymbol.OfMemory`. The keep-alive rule comes free: an operand
that is a block address makes the block address-taken, which is exactly what `SimplifyCfg` and `Sccp`
already consult before merging or dropping one.

The part that was not free is the **probe**. Names are collected by assembling the text against a
throwaway target with a recording resolver, and that resolver answered every question with a memory
stand-in — which is right for `MOV n, AX` and fatal for `JNZ AddLoop`, because `JNZ [BP+0]` is not an
instruction. The parse failed, and a failed parse is reported as "a name in it is not a variable this
pass could bind" — so the block declined for a name it could in fact bind. The probe now asks the
lowering which kind each name is, and reaches the same conclusion the real resolver will.

`LOWLEVEL.BAS` does **not** route on the back of this, and the reason is worth writing down because it
is a defect rather than a missing feature. Its module body now declines one construct later, at
`BIT(s, 2)`: a function with inline asm is skipped by the optimizer whole, so the shift count is still
an unfolded `IrCast` of a constant where `SelectWideShift` wants a constant. Behind *that* sits a real
disagreement, pinned by `InlineAsm_GivenARegisterHeldAcrossABasicStatement_ThenTheTwoPathsDisagree`:

```basic
! MOV CX, 5
n = n + 1        ' the allocator puts n+1 in CX
! MOV r, CX      ' ...and the asm's 5 is gone
```

The direct emitter computes through `AX` and so leaves `CX` alone by luck rather than by contract; the
back end's allocator has no way to know the text cared about a register, and `MInstrEffect`'s clobber
list says only that nothing of *ours* survives the block — not that something of *theirs* must. No
label is involved, and a function shaped like this routes today. LOWLEVEL is shaped like this, so
whoever removes its `LShr` decline turns a decline into a corpus **disagreement** unless this is fixed
first.

### Three gaps behind one decline: `pointer: IrGep has no register`

One row of the coverage census, `DIFF11.BAS::main`, was three independent gaps stacked on top of each
other. Each was invisible until the one above it was closed, which is what a census row looks like
from the outside: it names the *first* thing the selector tripped over, not the only one.

**The index of a typed GEP was never scaled.** Most array elements reach the selector as a
byte-offset `GEP`: the lowering already multiplied the flattened subscript by the element's size,
because that size is a property of the source type and the same on every target. An array of dynamic
`STRING`s cannot be lowered that way — its elements are *handles*, and a handle is two bytes here and
eight on a 64-bit host — so the lowering emits an element-indexed `GEP` and leaves the multiplication
to whoever knows the answer. The selector was not one of them and declined outright, which took every
module body that so much as read `a$(i)` with it. It scales the index itself now: a constant folds
into the displacement and costs nothing, a runtime index is shifted into a temporary of its own (a
copy, because the index is an SSA value whose other uses still want it unscaled), and a stride that is
not a power of two is a multiply. `[base+index]` on this target has no scale factor, so the shift is
not an optimization — it is the only form there is.

**The selector walked blocks in list order, which is not dominance order.** SSA promises a definition
*dominates* its uses. It promises nothing about the order the blocks happen to sit in `fn.Blocks`,
and `GOSUB` is where the two part company: the dispatch block is built at the first `RETURN`, so it is
listed *after* the continuations its switch reaches. Once CSE commons the address the dispatch pops
from with the one a continuation pushes to, the definition sits in a block listed after its use, the
selector reaches the use first, finds no register, and declines a function that was never ill-formed.
Selection now runs in **reverse postorder** (`IrDominators.ReversePostorder`, with anything
unreachable left where the function lists it), which places every block after every block that
dominates it. It is free elsewhere: terminators here are always explicit jumps — nothing falls through
to the next listed block — and liveness is a control-flow dataflow, not a scan of the list.

**An `unreachable` that is not already closed still needs an instruction.** The default arm of the
`GOSUB` dispatch is `unreachable`, and an empty block falls into whichever block is laid out next. It
emits a `RET`: the one instruction that is always available, always terminates, and is never taken.

Then the corpus differential earned its keep. Reordering the blocks changed nothing about the
instructions and everything about the *pressure*, and `DIFF03` — `Fact&(7)` — began answering
`330306480` where it had answered `5040`. That number is `5040 * 65537`: the high word of the result
had become the low word. The epilogue is

```
MOV AX, lo
MOV DX, hi
```

and the allocator had put `lo` in `DX`. **Liveness only tracks virtual registers, so a write to a
physical one is invisible to it** — nothing stopped a live value from sitting in the very register a
pinned move was about to overwrite. Under the old layout those two values were spilled and the
sequence read them back from the frame; under the new one they stayed in registers and the hole
opened. It was not new, and it was not confined to returns: every runtime-ABI call sets its arguments
up the same way, and only the allocation it happened to get had been keeping them apart.

`LinearScanAllocator.PinnedByIndex` closes it. Every instruction that writes a *named* physical
register contributes that register — its whole word, since writing `AL` destroys half of `AX` — at its
own index, and a value whose live interval spans that index cannot be given it. Read over
`[start, end)` rather than the call clobbers' `[start, end]`, because a value whose last live point
*is* the pinned move is the value being moved, and `MOV AX, AX` is not a loss.

Together: **256/259 functions selected and routed, 156/159 module bodies, 159/165 programs lowered,
293 corpus comparisons with no disagreement.** One census row, three gaps, and one latent miscompile
that had been waiting for a change in register pressure to surface.
## Scheduling is not free on six registers

`DIFF56`'s module body was the last function in the corpus that selected and did not allocate. It sums
a static array into a `LONG`, and the census reported it as an allocation decline by name.

The selector's own order is fine. It writes the accumulation serially — load an element, sign-extend it
into a register pair, add the pair, move on — which wants four registers at a time however long the
array is, and it allocates unchanged. What does not survive is the SCHEDULED order. With constant
bounds the loop is unrolled, so all ten loads read only frame addresses and are ready at the top of the
block, and the list scheduler prefers memory work: it issues every one of them there. Ten live values,
six registers.

Nothing downstream could undo that, which is why it declined rather than merely spilling. A loaded
element's defining instruction already carries a memory operand, so direct spilling would make the
instruction memory-to-memory; and with the loop unrolled there is no `CALL` between the load and its
use, so live-range splitting — which only looked at values live across a clobber — did not offer it a
candidate either.

**`MachineScheduler.CostsRegisters`** computes the peak number of simultaneously live virtual values for
the proposed order and for the written one, and discards the proposal when it exceeds the register file
having started below it. The gate is the file rather than "no increase at all", so a block whose peak
stays within six keeps every schedule it used to get: this can only refuse an order that was not going
to be allocatable. Physical registers are excluded because they are already pinned — an ABI staging
window is a scheduling barrier through its clobbers.

This is the general form of a rule the `Conflicts` predicate already stated for one case. A `CALL` is a
scheduling barrier there precisely because hoisting a definition above one stretches its live range
across the whole caller-saved file; the pressure a `CALL` makes was simply the only kind that had been
noticed.

### The other half: pressure the scheduler did not create

Splitting a live range was reachable only for a value live across a full-register clobber. That is the
pressure this target creates by itself, and splitting one is self-limiting because the fresh ranges no
longer cross the call. Plain pressure has no such shape: four `LONG` accumulators are eight words with
no call anywhere near them, and each is defined by an instruction that already reads memory.

`Spiller.SplitOne` now runs a second pass over ordinary live ranges once the clobber-crossing pass finds
nothing. The order matters — a function that routes today takes exactly the transformation it took
before, because the new pass is only reached where the old one gave up. Termination comes from
`MFunction.SplitValues`, the set of virtual ids the splitter minted: re-splitting one of those could
only add another store and another reload without shortening anything, so the second pass never offers
one, and the set of ids it can offer therefore shrinks strictly with each split.

Both repairs are load-bearing and neither subsumes the other. The unrolled accumulation routes with the
splitting pass alone, but at the cost of a store and a reload per element that the scheduler gate makes
unnecessary; a procedure holding four `LONG` accumulators at once declines with the gate alone, because
that pressure is in the selector's output and no scheduling decision put it there.

Selection stays at **257 of 259**, routing moves **256 → 257**, module ownership **156 → 157 of 159**,
and allocation declines reach **zero for the whole corpus**. The execution differential moves to **295
agreeing, 0 disagreeing**.
## `EXIT FAR`: PB's other non-local jump

The keyword argues for the wrong reading. `EXIT FAR` is not a far **return** and pops nothing: `EXIT
FAR AT label` records an unwind point — a target offset plus the `SP` and `BP` of the frame it belongs
to — and a bare `EXIT FAR` at any call depth afterwards puts that frame back and jumps there,
abandoning every procedure frame and `GOSUB` in between without unwinding them. The procedure that
fires it never returns to its caller, and neither does anything between it and the landing point.

So it is `ON ERROR` in a different suit, and it took the same treatment rather than a new one. Arming
is an **intrinsic the selector expands in place** (`rt_efar_arm`) for the reason arming a handler is:
it captures the *current* `SP` and `BP`, and a `CALL` would capture its own. The bare form
(`rt_efar_go`) is a call that never comes back, followed by `unreachable` — two `MOV`s out of the cells
and a `JMP` through the third, which is exactly what `CodeGenerator.Vendor.EmitExitFar` writes and into
the same three cells, so the two emitters can be mixed in one image. Both loads carry an explicit
clobber of what they write, which is what pins the sequence: the scheduler treats a clobber as a
barrier, so nothing addressed off `BP` can be moved after the `MOV` that replaces `BP`.

The function that **arms** is marked `HasErrorHandler`, taking it out of the optimizer whole. The
justification is unchanged from `ON ERROR`: the landing block is entered by an edge the CFG does not
have, so a reachability pass would delete it and `mem2reg` would promote variables the landing then
reads out of registers holding somebody else's value. The function that *fires* needs no mark — it
only leaves.

`EXIT FAR AT` can only name a label of its own procedure, and the front end already enforces that (a
reference elsewhere is `undefined label`), so the lowering's guard for it is defence rather than a
path. Nothing renders it back to BASIC and nothing emits it in C: the arm is a frame capture, and that
has no spelling in either.

`DIFF14.BAS` reaches the IR with this — **160/165 programs lowered, 258/261 functions selected, 257
routed** — and then stops again, twice over, which is worth recording rather than rounding off. Its
module body declines at `UIToFP u32 -> f80` (`USING$` of a `DWORD`), and its `SUB Noisy(n%)` takes a
**BYREF** parameter, which `BackendProcs` excludes from whole-program routing regardless; a routed
`main` may only call routed procedures, so closing either one alone would change nothing. The corpus
differential is therefore unmoved at **293 agreeing, 0 disagreeing** — the back end still takes nothing
of that program. What `EXIT FAR` does *through* the back end is verified by `BackendExitFarTests`,
which runs both emitters over programs shaped to route: an unwind out of a loop, out of three nested
frames, out of a `FUNCTION` that had already assigned its result (the caller never receives it — an
unwind is not a return), and the same procedure returning normally on a call that does not fire one.