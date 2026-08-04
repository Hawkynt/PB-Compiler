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

| Kind  | Spelling                        | Notes                                        |
|-------|---------------------------------|----------------------------------------------|
| Void  | `void`                          | stores, void calls, `ret void`               |
| Int   | `i1 i8 i16 i32 i64` / `u8 u16 u32 u64` | width in bits **and** signedness      |
| Float | `f32 f64 f80` / `mbf32 mbf64`   | IEEE / x87 extended, or Microsoft Binary Format |
| Ptr   | `ptr`                           | opaque; pointee travels on the mem op        |

PB scalar types map directly (`IrTypeMapper`): INTEGER→`i16` and WORD→`u16`,
LONG→`i32` and DWORD→`u32`, QUAD→`i64` and QWORD→`u64`, BYTE→`u8` and SBYTE→`i8`,
SINGLE/DOUBLE/EXT→`f32/f64/f80`. Non-scalars (strings, UDTs, dynamic arrays) are
not yet mapped.

### Two distinctions LLVM does not make

LLVM's integers are signless and its floats are IEEE. Neither holds for the BASIC
family, and a back end reading *only* the IR has to be able to tell:

- **Signedness.** PB has a signed and an unsigned scalar at every width. Which one
  a value is decides how it widens (`CBW` versus `XOR AH,AH`), which divide it
  uses (`IDIV` versus `DIV`), which condition a comparison takes and how it
  prints. It is an *interpretation* of the same bits, so `IrType.SameStorage`
  deliberately ignores it: `u16` and `i16` mix freely in a phi, a store or a
  binary operand pair, and the instruction carries the reading (`sdiv`/`udiv`,
  `slt`/`ult`, `sext`/`zext`). The verifier checks agreement by storage, not by
  exact type.
- **Microsoft Binary Format.** BASICA, GW-BASIC and the BASCOM-heritage
  QuickBASIC releases store SINGLE and DOUBLE in MBF — a different exponent bias
  and layout, with no infinities or NaNs. MBF is *storage only*: the x87 cannot
  compute on it, so a load converts to IEEE and a store converts back, through the
  `MbfToFP`/`FPToMbf` casts. The verifier rejects arithmetic or a comparison on an
  MBF operand, and `SameStorage` treats `mbf32` and `f32` as different encodings —
  moving between them is a conversion, never a reinterpretation.

The `LlvmEmitter` and `CEmitter` render an unsigned type as the same integer
(correct: the signedness is on the op by the time it reaches them) and **refuse**
an MBF type rather than silently emitting it as IEEE. `IrLowering` maps MBF
storage but declines a program that uses it until it emits the load/store
conversions — the DOS emitter's `EmitMbfSingleLoad`/`EmitMbfSingleStore` are the
model.

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
- `SELECT CASE` (numeric **and string** subjects - string arms/ranges compare via
  `rt_str_compare`), **`GOTO`/labels**, **`ON … GOTO`** (lowered to a `switch`),
  **`GOSUB`/`RETURN`** (a fixed-depth return-id stack + a shared dispatch `switch`, so
  nested GOSUBs return LIFO; `RETURN <label>` pops then jumps to the explicit label);
- static arrays (1-D and multi-dimensional, row-major byte GEP); **string arrays**
  (a buffer of pointer handles addressed by a typed, element-indexed GEP so the index
  scales by the target pointer size rather than the DOS 2-byte handle width);
- **dynamic (`REDIM`'d) arrays** (1-D and multi-dimensional): a runtime-allocated buffer
  behind a descriptor (data pointer + per-dimension lower bound and size) - `REDIM`
  allocates via `rt_arr_alloc`/`rt_arr_alloc_ptr` (count = product of dimension sizes),
  `REDIM PRESERVE` grows in place via `rt_arr_realloc`/`rt_arr_realloc_ptr`, element
  access is row-major flattened relative to the bounds, `ERASE` frees via `rt_arr_free`;
  `LBOUND`/`UBOUND` fold to constants for static arrays and read the descriptor for dynamic;
- **strings** via a runtime-handle ABI (`rt_str_*`): assignment, `&` concat, all
  comparisons, `LEN`, `LEFT$`/`RIGHT$`/`MID$`, `CHR$`/`ASC`, `STR$`/`VAL`,
  `SPACE$`/`STRING$`, `HEX$`/`OCT$`, `UCASE$`/`LCASE$`/`LTRIM$`/`RTRIM$`, `INSTR`
  (2- and 3-arg), the binary-record encoders/decoders `MKI$`/`MKL$`/`MKS$`/`MKD$`/`MKDWD$`
  and `CVI`/`CVL`/`CVS`/`CVD`/`CVDWD`, and the `MID$()=` in-place-replacement statement;
  identical literals are interned to one global;
- **fixed-length strings** (`STRING * n`): an inline n-byte buffer; assignment pads/truncates
  into it (`rt_str_to_fixed`) and any string use reads it back as a handle (`rt_str_from_fixed`);
- **console and file I/O** (`PRINT` incl. `TAB`/`SPC` and `,` print-zone advance,
  `INPUT`/`LINE INPUT`, `OPEN`/`CLOSE`/`PRINT #`/`INPUT #`)
  via `rt_print_*` / `rt_input_*` / `rt_file_*` declarations; **random/binary record I/O**
  (`OPEN … FOR RANDOM/BINARY … LEN=`, `GET`/`PUT #n, rec, var`) of a fixed-size scalar
  variable via `rt_file_get`/`rt_file_put` (the FIELD-buffer form is declined);
- **`DATA`/`READ`/`RESTORE`**: all DATA items pack into one length-prefixed module blob
  (`@.data`) walked by a module-global i32 cursor (`@.data_cursor`); numeric reads parse
  via `rt_str_val`, string reads store the `rt_str_const` handle, `RESTORE [<label>]`
  rewinds the cursor to 0 or to the label's blob offset;
- **user-defined `TYPE` records**: a UDT variable is a packed i8 buffer; member access
  (`v.field`) reads/writes the field's scalar type at its byte offset via a byte GEP
  (QB-style flat dotted variables resolve to a plain scalar); whole-record assignment
  (`rt_mem_copy`), `=`/`<>` comparison (`rt_mem_compare`), and whole-record `GET`/`PUT`
  all operate on the buffer; **arrays of records** (`a(i).field`, static or dynamic)
  index the element then offset the field; **fixed-string record fields** (`name AS STRING * n`)
  convert at the field boundary (`rt_str_to_fixed`/`rt_str_from_fixed`); composes with SWAP
  and field-level GET/PUT;
- intrinsics: `ABS`/`SGN`/`FIX`/`INT`/`CDBL`/`CSNG` (branchless/bitcast, no runtime) and
  the math functions `SQR`/`SIN`/`COS`/`EXP`/`LOG`/`TAN`/`ATN` and the `^` power operator
  lowered to the matching **LLVM intrinsics** (`llvm.sqrt.fN`, `llvm.pow.fN`, …) so `llc`
  optimizes them natively;
- whole modules: user `SUB`/`FUNCTION` with scalar **BYVAL and BYREF** parameters,
  **`TYPE` record** parameters (passed as a pointer; BYREF accesses the caller's storage,
  BYVAL `llvm.memcpy`s a private copy on entry) and **string** parameters and results
  (a string is its runtime handle - BYVAL passes the handle, BYREF a pointer to the
  caller's handle slot); direct calls, and a parameterless FUNCTION called by naming it
  (`PRINT Counter%`). A procedure with an unsupported body is kept as a declaration.
- **storage that outlives a frame**: a `STATIC` local and any module-level variable a
  procedure touches become module globals, so every function reaches the same cell. A
  module variable only the main body uses deliberately stays an alloca, which mem2reg
  promotes to an SSA register - precision here is what keeps correctness from costing
  the optimizer its best case.

The computation in a program is fully optimized; runtime-ABI calls (I/O, strings) stay
opaque but their inputs are optimized. Hello world, numeric/string compute-and-report,
and sequential file-processing programs lower, optimize and compile to a native x86-64
object via `pbc --emit-llvm | llc` (linked against a runtime providing the `rt_*`
functions; the `llvm.*` math intrinsics need no runtime).

Not yet: the FIELD-buffer form of random I/O, and `GET`/`PUT` of UDT/string records
(only fixed-size scalar records are modeled).

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
(`float`/`double`/`x86_fp80`, `getelementptr i8` byte GEPs for scalar arrays and
`getelementptr <elem>` typed GEPs for pointer-element arrays, exact hex float
literals, optional target triple). The test suite feeds it to the real toolchain:
`llvm-as` accepts it and `llc` lowers it to native x86-64. That is the working
path off 16-bit DOS.

## Rendering the IR back to BASIC

`Emit/IrBasicWriter.cs` renders an `IrModule` as PowerBASIC source. The older `Emit/BasicWriter.cs`
renders the bound **AST**, so it can only ever show a program as it was *written*; this one renders
the **IR**, so it shows a program as it will be *compiled* — after lowering, and after whatever the
optimizer did to it.

Control flow is emitted as labels and `GOTO`s rather than recovered into `IF`/`FOR` blocks: a basic
block **is** a label and a branch **is** a `GOTO`, so the translation is exact and needs no
structuring analysis that could be subtly wrong. SSA is destroyed the standard way, with each phi
copied on its incoming edges. An alloca whose address never escapes is recognised as the variable it
is; one holding several elements is an array, its subscript recovered by undoing the byte-offset
multiply the lowering built (which the optimizer may have turned into a shift). Anything it cannot
render **exactly** throws by name — approximate BASIC would make a round-trip failure ambiguous
between a bad pass and a bad rendering.

### What it is for: the observable contract

Byte-identity is the direct emitter's contract and always will be. Once the IR optimizer rewrites a
program, the only statement left worth making is that the rewritten program still *does* the same
thing — and rendering the IR back to BASIC is what makes that checkable, because it gives a pass a
before and an after that are both runnable programs.

`IrPassObservableEquivalenceTests` is that check: every pass in the standard pipeline is run **on its
own** (on top of mem2reg, without which most see nothing to do) over a set of ordinary programs, each
rendered and executed, and its output compared against the program compiled directly. A failure names
the pass rather than the pipeline. The whole pipeline and its idempotence are checked as well.

### Checking the OTHER four hundred optimizations

`IrPassObservableEquivalenceTests` covers the IR passes. `DirectOptimizerOnRenderedBasicTests` covers
the ones in `CodeGen/CodeGenerator.Optimize*.cs` — the optimizations that rewrite the program on its
way to machine code, and the ones the user's phrase "weave the BASIC code" names.

The lever is that the writer produces BASIC no person would write: every value in its own variable,
control flow as a mesh of labels and `GOTO`s, loops unrolled into straight lines, subscripts rebuilt
from byte offsets. Feeding that back through the front end and out through the direct emitter — once
with the optimizer **off**, once **on** — exercises those optimizations on shapes the hand-written
corpus never produces.

Three outputs must agree per program: the original compiled, the rendered BASIC unoptimized, and the
rendered BASIC optimized. The first pair catches a bad *rendering*; the second a bad *optimization*.
Keeping them apart is what makes a failure diagnosable. Currently **73 programs compared, 0
optimization disagreements**; the nine rendering disagreements are the writer's own gaps and are
listed by name with a diagnosis, so the list cannot grow unnoticed.

This found a real miscompile the first time it ran — not in a pass, but in the *direct* emitter: an
`ELSEIF` condition was being folded against the value lattice of the `THEN` arm it followed. See
`CodeGen/CodeGenerator.cs` `EmitIf` and `Tests/CodeGen/ElseIfProgramPointTests.cs`.

### The bar for retiring `BasicWriter`

`BasicWriter` is not a pretty-printer — it is a **pb36 → pb35 down-translator**, and its contract is
that the rendered text is a program *the pb35 front end accepts*. That is the bar the IR writer has
to clear, and it is now measured: `Write_GivenTheCorpus_ThenWhatItRendersRebindsUnderPb35` renders
every corpus module, re-parses and re-binds the output under pb35, and requires zero errors.
Currently **80 modules re-bind, 0 rejected**.

Rendering and re-binding are counted apart on purpose: a module the writer *refuses* is a known gap,
while one it renders into text pb35 *rejects* is a bug, and one number would let the second hide
behind the first.

What `BasicWriter` still does that the IR writer cannot: it renders `TYPE` declarations and procedure
signatures from the original `CompilationUnit`, which the lowering has flattened into offsets and
GEPs. Those are additions, not a swap.

`IrBasicWriterCensusTests` reports how much of the corpus renders (currently **173 of 218**
functions) and ranks what does not, which is the distance still to go before `BasicWriter` can be
retired. It cannot be retired yet: it also renders declarations, `TYPE`s and procedure signatures
from the original `CompilationUnit`, and carries the binder's pb36→pb35 desugaring, none of which the
IR writer has.

## Roadmap

- the constructs that still decline: `PRINT USING`/`LPRINT`, `ArraySortStmt`, `PUT$`,
  `DIM AT`, `HEX$` with a digit count, parts of the `CommandStmt` family, and inline
  assembly (target-specific by definition - it will never lower).
  `TryLowerModule(model, out var reason)` reports which one a program hit, and
  `pbc --emit-c` / `--emit-llvm` print it;
- a native IR → x86-16 back end that reproduces byte-identical output for a
  subset (the fidelity proof that would let the IR augment the direct emitter);
- wire the IR into the production pipeline behind the byte-identical harness.

## Metastatements

A metastatement is a compile-time directive, and most carry no runtime semantics for a
target-independent IR — they steer the *direct* emitter's policy or its target, which each IR back
end decides for itself. `$OPTIMIZE` and `$CPU` are therefore ignored by the lowering. Declining on
them was costing the IR path most of the corpus for no reason: they are the majority of every
metastatement in the battery, and accepting the two of them took the corpus from **40 to 78 of 162
programs** lowering.

`$ERROR <kind> ON` is the one that is not policy: it arms real traps, and the lowering emits them.
Anything unrecognized still declines, so a directive that gains semantics later is refused by default
rather than ignored by accident — silently dropping a trap is a miscompile, not a missing
optimization.

| arm | what it raises | where |
|---|---|---|
| `BOUNDS` | Error 9 on a subscript outside its dimension | every array index, static or dynamic |
| `OVERFLOW` | Error 6 when integer `+`, `-` or `*` wraps | every checked arithmetic node |
| `NUMERIC` | Error 6 when a `FOR` counter wraps past its own range | the counter increment, and nowhere else |

The direct emitter reads the overflow flag straight off the `ADD`/`SUB`/`IMUL` it has just written —
a `JNO` over a call to `rt_raise`. A target-independent IR has no flags register, so the same question
is asked in arithmetic every back end already has:

- `+` and `-` use the textbook sign rule, which is **exact in the operand's own width**: an addition
  overflows exactly when both operands share a sign the sum does not (`~(l^r) & (s^l) < 0`), and a
  subtraction exactly when they differ in sign and the difference takes the subtrahend's
  (`(l^r) & (s^l) < 0`);
- an unsigned type has no overflow flag either — its wrap is a *carry*, which is one unsigned compare
  (`s < l` for `+`, `l < r` for `-`);
- a multiply has no such rule, so it is computed one width up, where the product is exact, and
  range-checked before being truncated back.

A dynamic array's bounds are not in its type — a `REDIM` decides them at run time — so the check reads
the same descriptor slots the address arithmetic reads: the lower bound directly, the upper one
reconstructed as `lo + size - 1`.

## Error handling: the edge the CFG cannot show

`ON ERROR GOTO` writes a code address into a runtime cell, and a fault **anywhere** afterwards — deep
inside a runtime routine, where this compiler emitted no instruction at all — restores the armed frame
and lands on that address. The edge is real at run time and has no representation as a CFG edge,
because its source is "any point in the armed region".

Two things follow, and they are the whole design.

**The handler is named by `IrBlockAddress`** — LLVM's `blockaddress`. Arming is a call to an `rt_`
intrinsic the back end expands *in place* rather than a real call, because it captures the current
`BP` and `SP`; a call would capture its own.

**The function is marked `IrFunction.HasErrorHandler`, and the optimizer does not touch it.** Every
pass in the pipeline reasons from the CFG, and on such a function the CFG is missing its most
important edge: the handler looks unreachable and gets deleted, or a variable the handler reads looks
like it can only hold the value arriving on the fall-through. Both conclusions are wrong and both are
silent. `IrPassManager.Run` and `IntegerRecovery.Run` carry the guard themselves, so no individual
pass has to remember it — one place to be right instead of a dozen. It is the identical trade the
direct emitter makes, where `_trackResume` switches its optimizations off wholesale.

`RESUME <label>` names its destination and is an ordinary branch. `RESUME` and `RESUME NEXT` go back
to a statement the *fault* chose, so each statement publishes its own start and successor addresses
(`rt_resume_mark`, a pair of block addresses) exactly as `_trackResume` does — which is also why they
cannot be IR branches: the destination is a value in a runtime cell, so the runtime performs the jump
and the call never returns.

`ERR`, `ERL` and `ERADR` bind to no variable, so a handler naming one arrives at the lowering as an
unbound name; they read `rt_err` / `rt_erl` / `rt_eresume`, named exactly as the runtime labels them
so the back end's data-cell bridge resolves them to the very storage the direct emitter uses.

## Float to integer: two conversions, not one

BASIC **rounds** a real on its way into an integer variable — `n% = 2.7` is 3 — while a C cast and
LLVM's `fptosi` both truncate. The IR therefore has to say which it means, and
`IrCastOp.FPToSIRound` is a separate operation from `FPToSI`:

| operation | meaning | emitted by | native | C | LLVM |
|---|---|---|---|---|---|
| `FPToSI` | truncate toward zero | `FIX`, `INT` | (declines) | `(int)x` | `fptosi` |
| `FPToSIRound` | round to nearest, ties to even | assignment to an integer variable, `CINT`/`CLNG`/`CWRD`/`CBYT`/`CDWD` | `FISTP` through a dword cell | `(int)llrint(x)` | `llvm.rint` then `fptosi` |

The two disagree on every value with a fraction, which is the kind of difference that shows up as a
wrong number in program output rather than as a crash — the IR path used to emit the truncating one
for both. Nothing names a rounding *mode*: nearest-ties-to-even is where the runtime leaves the x87
control word, and it is what `llvm.rint` follows under the default mode, so the paths agree without
having to be told.

`IntegerRecovery` accepts both spellings as the closing conversion of a float-shaped integer tree.
Whether it rounds or truncates cannot matter there — the recovered tree is integer-valued either way.

## FOR over a float counter

`FOR x! = a TO b STEP c` lowers to the integer loop's block structure with float operations in place
of the integer ones: an ordered compare (`Fole` ascending, `Foge` descending, both when the step's
sign is only known at run time) and `FAdd` for the step.

Two things are deliberately not done. The loop is **not** rewritten into an integer one when the
bounds look whole — a float counter *accumulates* its step, which is why `FOR x! = 0 TO 1 STEP .1`
runs nine times rather than ten, and reproducing that is the point of a fidelity compiler. And the
predicates are the *ordered* ones, so a NaN bound exits the loop instead of spinning — which is what
comparing on the x87 does.
