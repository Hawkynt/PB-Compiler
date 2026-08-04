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
       → liveness / live intervals                                                     ← STAGE 3
       → linear-scan register allocation (vreg → AX/BX/CX/DX/SI/DI, spill on pressure) ← STAGE 4  ★ reassignment
       → instruction scheduling on the allocated machine instrs (interleave freely)    ← STAGE 6
       → Assembler → machine code                                                      ← EXISTS (Asm/)
```

The existing SSA IR middle-end and the `Assembler` are the fixed bookends; the new work
is the middle three boxes (isel, regalloc, and moving the scheduler onto the allocated
machine IR).

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
  asm, error handling (ON ERROR/RESUME), unlisted intrinsics. A program either fully
  lowers or the backend declines it.
- **Wiring** (`pbc/Driver.cs` `--emit-llvm`): `TryLowerModule` → `IrPassManager.Standard()`
  `.RunOnModule` → `Inliner.Run` → re-run → `IrVerifier.Verify` → emit. The new backend
  slots a lowering pass in the same spot, after optimisation, before emission.

## Machine IR (Stage 1)

A target-level IR over virtual registers, distinct from the SSA IR. Sketch:

- `MReg` — a register operand: either a virtual id (`v0, v1, …`, unbounded) or a physical
  `Reg` (`AX..DI`), plus a size (byte/word/dword). Allocation rewrites virtual → physical.
- `MOperand` — `MReg` | immediate | memory (`[base+index*scale+disp]`, base/index are
  `MReg`) | label/global | stack-slot (for spills and allocas).
- `MInstr` — an opcode (`Mov, Add, Sub, Imul, Cmp, Test, Lea, Shl, …, Jcc, Jmp, Call,
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

## Emission (Stage 5) + scheduling (Stage 6)

Emit each `MInstr` through the existing `Assembler` methods (encoding/length/fixups are
already handled there — no byte-patching, so wall 2 never arises). Then run the
**existing dependency scheduler on the allocated machine IR**, where it finally has
independent chains in distinct registers to interleave. The byte-level scheduler stays
as the ceiling for the direct codegen path.

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
`SSA IR → InstructionSelector → LivenessAnalysis → LinearScanAllocator →
MachineScheduler → MachineEmitter → Assembler → machine code`):

- **Stage 1 — MachineIR** (`Backend/MachineIr.cs`). ✅
- **Stage 2 — instruction selection** (`Backend/InstructionSelector.cs`), straight-line integer core; declines the rest. ✅
- **Stage 3 — liveness / live intervals** (`Backend/LivenessAnalysis.cs`). ✅
- **Stage 4 — linear-scan allocation** (`Backend/LinearScanAllocator.cs`), with the BX/SI/DI addressing register class — *this is the register reassignment*. ✅
- **Stage 5 — emission** (`Backend/MachineEmitter.cs`), virtual→physical rewrite + frame-slot resolution through the `Assembler`. ✅
- **Stage 6 — machine scheduling** (`Backend/MachineScheduler.cs`), post-allocation interleaving of independent chains. ✅

The backend is still **standalone** — not yet wired into the production codegen — so the
241-battery harness is untouched. Remaining **productionization**: wire it as the codegen for
eligible pb36 + `$OPTIMIZE SPEED` programs (whole-program ABI: prologue/epilogue, how arguments
arrive and the result returns, the entry call), widen instruction-selection coverage beyond the
straight-line integer core (branches, phis, calls, casts, division, float/x87), add stack spilling to
the allocator (it currently declines when the live set exceeds six registers), and end-to-end
oracle-verify against the differential battery.

(Since this was written, instruction selection AND emission also cover **branches** — `IrBr`→`JMP`,
`IrCondBr`+folded compare→`CMP`/`Jcc`/`JMP` — and the emitter produces a complete function with the
standard stack ABI. Phis, calls, casts, division and float remain.)

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
- Eligible = pure INTEGER (signed-16) function, INTEGER BYVAL params, no error handling, and the IR
  fully selects + allocates (so calls/division/float are declined automatically). The back end owns the
  **whole function via SSA** — no shared memory cells, so it never reads an optimizer-stale cell (the
  blocker the cell-sharing prototype hit).
- The function is excluded from inlining (`CodeGenerator.cs:601` `isInlinable` predicate) and from the
  register-parameter convention (an `OptRegParm.Apply` skip predicate), so its emitted stack ABI matches
  the call sites; `EmitBackendFunction` emits the standard prologue / argument loads / body / `RET n`.
- Selection fixes for real IR: a register is materialized for an immediate `IMUL` multiplier (`a%*2`);
  argument vregs are numbered before phi vregs so argument `i` is vreg `i`.

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

### The data-layout bridge: a routed function reading a module variable

The back end lays out no data of its own — the whole-program codegen does — so a global access
becomes a named `MOperand.DataCell` that resolves at emission to exactly the `Mem` the direct emitter
uses for that symbol (`TryDirectCell`). Both paths then address the same storage, which is what lets
routed and directly-emitted code share state at all.

The question that had to be settled first was whether that cell can be **stale**, since the
cell-sharing prototype was reverted for exactly that reason. It cannot, for two independent reasons,
and both are properties of the existing code rather than assumptions:

- a global a *procedure* can see is `SHARED`, and `SsaForm.IsTrackableShape` excludes `IsShared`
  variables from SSA tracking — so no store to one is ever elided by dead-store elimination and no
  read of one is ever folded to a constant;
- register residency, which could otherwise hold the value in `SI`/`DI` while the cell went stale,
  requires an `SI`/`DI`-clean region — and a call is not clean, so a loop containing a call to the
  routed function cannot keep the global in a register.

Only a *module* variable is bridged. A `STATIC` local and a synthesized IR global (`.data_cursor`, a
string literal) have no `ModuleVariables` symbol to map back to, so they decline — the emitter throws
rather than guesses if the routing ever admits one it cannot address.

### Signed division, and the first physically-pinned instruction

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
  closes the same latent hole for `CALL`.

Only a **non-zero compile-time constant** divisor is selected. PowerBASIC raises Error 11 on a zero
divisor, and that guard belongs to the language rather than to an `$ERROR` option — but a constant
that is not zero cannot trap, which is exactly the case where the direct emitter also drops the guard
(`O0220`). A `-1` divisor declines as well, because `MININT \ -1` overflows `IDIV` into a hardware
divide fault where PowerBASIC reports Error 6. A runtime divisor waits for the runtime-label bridge,
since the guard it needs is a jump to the runtime's error entry.

### Selection is not routing

The census reports two numbers, because the first overstates the second: `BackendProcs` also
**schedules and allocates**, and a value live across a `CALL` has no register while there is no
spilling. `functions routed` counts the functions that survive both, and is the honest coverage
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

That conservatism is now the binding constraint, and the census says so plainly: selection went
**15 → 38** functions, while routing went only **14 → 18**. The other 20 select and then lose their
allocation, because a parameter is live from the prologue and a value live across a `CALL` has no
register while there is no spilling. Spilling to the frame — not more selection — is what the ranking
points at next.
