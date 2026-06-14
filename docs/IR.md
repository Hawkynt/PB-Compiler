# The IR middle-end — a retargetable optimizer

`PowerBasic.Compiler.Ir` is an LLVM-style typed SSA intermediate representation
and optimization middle-end. It exists to take the compiler **beyond 16-bit
DOS**: bound PowerBASIC is lowered into a target-independent IR, optimized hard,
and then either emitted as textual LLVM (handed to LLVM's back end for
x86-64/ARM/…) or — in future — lowered by a native IR back end.

It is a **parallel path**. The direct bound-AST → x86-16 emitter
(`CodeGen/CodeGenerator*.cs`) and its byte-for-byte fidelity to the genuine
PBC/QB/TB/PDS compilers is untouched; the differential harness stays green by
construction. Nothing in the production pipeline depends on this IR yet.

## Type system

A small, target-independent first-class type lattice (`IrType`):

| Kind  | Spelling          | Notes                                   |
|-------|-------------------|-----------------------------------------|
| Void  | `void`            | stores, void calls, `ret void`          |
| Int   | `i1 i8 i16 i32 i64` | width in bits; signedness lives on ops |
| Float | `f32 f64 f80`     | IEEE / x87 extended                     |
| Ptr   | `ptr`             | opaque; pointee travels on the mem op   |

PB scalar types map directly (`IrTypeMapper`): BYTE/WORD/INTEGER→`i16`-class by
byte size, LONG/DWORD→`i32`, QUAD→`i64`, SINGLE/DOUBLE/EXT→`f32/f64/f80`.
Non-scalars (strings, UDTs, dynamic arrays) are not yet mapped.

## Core data model

- `IrValue` — base of constants, arguments, globals and instructions, with an
  intrusive **use-list** and `ReplaceAllUsesWith`.
- `IrInstruction` — operands are values; mutation goes through `SetOperand`, which
  keeps use-lists exact. Instruction set: binary arithmetic/bitwise, `icmp`/`fcmp`,
  the LLVM cast set, `alloca`/`load`/`store`/byte-`gep`, `phi`, `call`, and the
  terminators `ret`/`br`/`condbr`/`switch`/`unreachable`.
- `IrBasicBlock` — ends in exactly one terminator; predecessors are derived from
  sibling terminators so CFG edges never drift.
- `IrFunction` / `IrModule` — signature + blocks; globals + functions.
- `IrDominators` — immediate dominators (Cooper-Harvey-Kennedy) + dominance
  frontiers (Cytron).
- `IrVerifier` — one terminator per block, phis lead and match the predecessor
  set, operands dominate uses, full type/cast legality, `ret` matches the return
  type. Every pass should leave the IR verifiable.
- `IrPrinter` — deterministic, LLVM-like text for snapshots and debugging.

## Lowering (bound AST → IR)

`IrLowering` produces clang-style alloca/load/store form (a later mem2reg pass
promotes to SSA). `TryLowerModule` lowers the whole program; `TryLowerMainBody`
lowers just `@main`. Anything outside the supported subset makes the lowering
**decline** (return `null`) rather than miscompile.

Supported today:

- scalar integer/float arithmetic, bitwise, comparisons (sign-extended to BASIC's
  `-1`/`0`), `Eqv`/`Imp`, unary negate/not — **faithful to PB's `INTEGER+INTEGER → SINGLE`
  promotion**;
- `IF`/`ELSEIF`/`ELSE`, `FOR` (constant **or runtime** step), `DO` (pre/post
  `WHILE`/`UNTIL`, infinite), `EXIT`/`ITERATE`, `END`, `SWAP`;
- `SELECT CASE` (value / list / `x TO y` range / `IS <rel>` arms, `CASE ELSE`) as a
  short-circuit comparison chain;
- static arrays (1-D and multi-dimensional, row-major byte GEP);
- the pure numeric intrinsics `ABS`, `SGN`, `FIX`, `INT` (branchless / bitcast, no runtime);
- whole modules: user `SUB`/`FUNCTION` with scalar **BYVAL and BYREF** parameters
  and direct calls; a procedure with an unsupported body is kept as a declaration.

Not yet: strings, dynamic arrays, `GOTO`/`GOSUB`, file/console I/O, other intrinsics.

## Optimization passes (`Ir/Passes/`)

Per-function (`IrPassManager.Standard()`, run to a verified fixpoint):

1. **mem2reg** — promote allocas to SSA registers + phis (iterated dominance
   frontier; PB zero-init seeds reads, never `undef`).
2. **instcombine** — constant folding (incl. bitcast); algebraic identities (x+0,
   x*1, x*0, x^x, absorption, x+x→shl, x*-1→-x, double negate, add/sub cancellation);
   strength reduction (`x*2^k→shl`, unsigned `x/2^k→lshr`, `x MOD 2^k→and`);
   canonicalization (double-complement, constant reassociation through op chains,
   sub→add-of-negation, constant-to-RHS comparison swap, widened-bool collapse,
   `gep p,0→p`).
3. **sccp** — Wegman-Zadeck conditional constant propagation; deletes dead arms
   and unreachable blocks.
4. **correlate** — correlated value propagation (facts from `if (x==C)` into the
   guarded region).
5. **gvn** — dominator-scoped global value numbering (commutative-aware).
6. **memopt** — intra-block load/store forwarding (sound alias test).
7. **dse** — intra-block dead-store elimination.
8. **licm** — hoist loop-invariant, non-trapping computations into the preheader.
9. **dce** — remove unused side-effect-free instructions.
10. **ifconv** — if-conversion: collapse a simple diamond into `select`.
11. **simplifycfg** — trivial-phi elimination, single-predecessor merging, constant/
    identical-target branch folding, unreachable-block removal.

Module-level: **inliner** — inline direct calls to non-recursive single-block
callees (run between per-function rounds).

`IrConstFold` underpins folding: two's-complement wrap to the result width,
opcode signedness, and it **declines** anything undefined (division by zero,
`INT_MIN/-1`, out-of-range shifts, `x/0.0`, out-of-range float→int) so runtime
semantics and traps are never silently changed.

## LLVM emission (targeting beyond DOS)

`LlvmEmitter` renders the optimized IR as strictly-valid textual LLVM
(`float`/`double`/`x86_fp80`, `getelementptr i8` byte GEPs, exact hex float
literals, optional target triple). The test suite feeds it to the real toolchain:
`llvm-as` accepts it and `llc` lowers it to native x86-64. That is the working
path off 16-bit DOS.

## Roadmap

- lower strings (a representation decision: the DOS string-handle model vs. an
  LLVM `ptr`+`len` model), dynamic arrays, `GOTO`/`GOSUB`, I/O and intrinsics;
- a native IR → x86-16 back end that reproduces byte-identical output for a
  subset (the fidelity proof that would let the IR augment the direct emitter);
- wire the IR into the production pipeline behind the byte-identical harness.
