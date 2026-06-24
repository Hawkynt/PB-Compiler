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

- Architecture + phase ordering decided; IR foundation mapped; design fixed (this doc).
- Stages 1-6 unimplemented. Build order: MachineIR → integer isel → liveness →
  linear-scan → emit (one trivial function end-to-end, oracle-verified) → widen
  coverage (control flow, calls, casts) → float/x87 → move the scheduler onto it.
