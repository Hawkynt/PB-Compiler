
### Float precision: the declared type picks the formatter, it does not round the value

**Done.** PowerBASIC computes a float expression at the x87's own width and lets the static type
choose only the formatter - a SINGLE prints 7 significant digits, a DOUBLE 15. `LowerArithmetic` now
models that: a floating expression is computed in f80 and narrows where PB actually narrows, on the
store into a declared variable, which the `Coerce` at that use site emits because Coerce measures the
value's own width rather than trusting the PB type it is told. `LowerPrintItem` hands the formatter an
f80 and lets the routine's NAME pick the digit count - which is the runtime's own model, where
`rt_print_f32` and `rt_print_f64` share a body and differ only in the digits they set.

It took three attempts and two wrong diagnoses. `PRINT H?/3` with `H? = 200` came out `66.66666`
against PBC 3.50's `66.66667`; the cause was blamed first on the runtime ABI table, then on the
missing 80-bit frame cell. The cell was a genuine bug and is fixed (`MRegSize.Tbyte`, floats sized by
their own width instead of everything wider than a word being called a `Dword` - a DOUBLE was being
addressed through a dword reference - and float temporaries parked at x87 width), but it was not this
one: with all of it fixed the disagreement was unchanged, because the rounding was already in the IR
before any back end saw it, visible in the emitted LLVM as `float 0x4050AAAAA0000000`.

With it modelled, `rt_print_ext` / `rt_fprint_ext` are in the runtime ABI table and DIFF24.BAS agrees.
Differential: 102 compilations, 100 agreeing, 0 disagreeing.

One consequence is recorded rather than fixed. The IR-to-BASIC writer materializes every SSA value
into a declared BASIC variable, and BASIC source cannot separate "format as SINGLE" from "the value is
SINGLE" - giving the temporary a SINGLE type rounds it, giving it an EXT type changes the digit count.
Only rendering the arithmetic inline, so PB types the expression from its operands as the original
does, would be faithful. `pb36/DIFF35.BAS` is a known writer gap for that reason.

### The direct emitter holds an integral operand on the x87 stack across a CALL — eight deep and it is silently wrong

**Open, oracle-confirmed, not fixed here.** PB computes integral `+`/`-`/`*` in floating point, and
`CodeGenerator` evaluates the LEFT operand onto the x87 stack before evaluating the right. When the
right operand contains a CALL, the left operand stays on that stack across it — and the 8087 has eight
registers. The eighth pending value overflows the stack and the answer is quietly wrong; there is no
trap, no diagnostic, and nothing in the corpus nests that deep.

Two reproductions, both diffed against genuine PBC 3.50 with `scripts/diff-one.sh … pb35`:

| program | PBC 3.50 | `pbc` direct | routed |
|---|---|---|---|
| eight distinct nested `Ax% = n% + A(x+1)%(n% + 1)`, no recursion | **45** | 52 | 45 |
| `SumI% = n% + SumI%(n% - 1)` at `n% = 20` | **210** | 245 | 210 |

The threshold is exactly the register file: `n% = 6` is right, `n% = 7` is wrong. Two spellings that
avoid holding anything across the call are right at any depth — `SumI% = SumI%(n% - 1) + n%` (the call
first) and `inner = SumI%(n% - 1) : SumI% = n% + inner`. `A1` disassembles to

```
    fild  [scratch]        ; ST(0) = n%    <-- pushed here
    ...
    call  A2               ; <-- and still here
    fild  [scratch]        ; ST(0) = the result
    faddp st, st(1)
```

so the fix is to spill `ST(0)` to a frame temp around a call in the right operand and reload after.
That is faithful — PBC 3.50 evidently does it, which is why it answers 45 — but it changes the bytes of
every `a + f(x)` in the corpus, so it needs the full 504-case differential battery to land rather than a
unit test. It is recorded here rather than attempted because it is a task of its own size.

The routed path is correct on both programs: it computes in integers after `IntegerRecovery` and spills
to the frame, so it has no eight-value ceiling.

## A. Foreign-object interop / ABI (the active frontier)

Extends the OMF reader/linker + calling-convention work already landed.

### Must
- **C++ name mangling / demangling.** Today only case-sensitive resolution +
  `ALIAS`. To link C++ objects we must resolve (and ideally demangle for
  diagnostics) the mangled publics of MSVC, Borland and Watcom — each scheme
  differs. *Touch points:* `Linker.Resolve`, a new `Demangle` helper, `ALIAS`
  surface. This is the original question that opened the interop thread.
- **Far pointers & non-tiny memory models.** *Single-segment subset done.*
  `OmfToPbu` now lowers far (`Base16`/`Pointer32`) fixups and data-segment fixup
  sites instead of rejecting them: because the whole program lives in one combined
  segment loaded at a single paragraph, every far reference's segment is just the
  load segment (an MZ relocation) and its offset is the target's place in the
  image, so compact/large-model objects that still fit 64 KiB link and run (a far
  `int far g` read; a far-pointer initializer in data — `OmfTests`, plus the
  `Link_GivenObjectUsingFarData_…` interop case). *Still TODO:* genuinely
  multi-segment images **larger than 64 KiB** need real per-segment paragraph
  layout (each foreign far segment its own paragraph) and far FIXUPP frames that
  are not the load segment. The linker's existing 64 KiB size check fires the clear
  diagnostic until then. *Touch points:* `OmfToPbu`, `Linker`, `MzExeWriter`,
  `LinkedImage`.

### Should
- **Full register arg-size rules** for `WATCALL`/`FASTCALL`: LONG/float/pointer
  arguments in register *pairs* (deferred from the common-word-case scope).
  *Touch points:* `CodeGenerator.Procs.cs` (`ConventionRegisters`, `EmitCall`,
  `LayoutFrame`, `BeginFrame`).
- **C-runtime linking (M3).** *No-crt0 subset done.* Beyond leaf `strlen`, a genuine
  `printf`-family routine now links and runs: Borland/Turbo C small-model **`sprintf`**
  formats an integer into a buffer through the real `CS.LIB` formatting engine
  (`_VPRINTER`/`_LTOA`/`_memcpy`/`_REALCVT`, ~5 members trimmed from 200+). DGROUP is laid
  into the single segment (every `_DATA`/`CONST`/`_BSS` relocated behind the code, `_BSS`
  folded as zero fill so its cells get a fixed offset and start zeroed without `crt0`); the
  one startup-provided symbol the integer path references but never executes
  (`_REALCVT`'s `__RealCvtVector`) is satisfied by a lazy, last-resort `CrtSupport` stub.
  *Still TODO:* the routines whose graph genuinely needs the C startup — MS C 6.0
  `sprintf` (buffered `FILE` + heap) and the `malloc` family — require hosting `crt0`'s
  DGROUP markers (`_edata`/`_end`/`_main`) and near-heap init (`__nheap_desc`/`__psp`);
  those fail cleanly with an unresolved-startup diagnostic today.
  *Touch points (done):* `OmfToPbu` DGROUP/BSS layout, `Emit/Omf/CrtSupport.cs`, `Linker`
  last-resort provider. *Remaining:* `crt0` hosting / DGROUP-marker synthesis.
- **Watcom CRT via `WATCALL` declarations** — now feasible since `WATCALL` exists
  (its CRT is register-convention; `_strlen`-style cdecl calls mismatch).

### Could
- **Emit OMF `.OBJ` / `.LIB`.** *Done — genuine MS-LINK compatible.* `OmfWriter`
  (`Emit/Omf/OmfWriter.cs`) emits a `PbuFile` as a 16-bit OMF object —
  THEADR/LNAMES/SEGDEF/PUBDEF/EXTDEF, chunked LEDATA (≤1024 B) and FIXUPP for every
  `PbuFixup` kind, MODEND. `OmfLibraryWriter` archives several such objects into a
  `.LIB` whose **hashed dictionary uses the genuine OMF library hash** (the real
  MS/Intel/Watcom `omflib_hash` — case-folding both-ends rotate, 37 buckets) so foreign
  linkers (LINK/Watcom/Borland, and via OMF any C/asm toolchain) resolve PB symbols
  through it. Verified to be the *same* hash all four staged toolchains' librarians use:
  it locates every public in the genuine Borland (`CS`/`MATHS`), Turbo C (`C[CLMS]`),
  MS C 6.0 (`SLIBCR`) and Watcom (`CLIB*`) runtime libraries (`CInteropTests`
  `LibHashMatches_*`). Both
  round-trip through our `OmfReader`/`OmfToPbu`/`OmfLibrary`; an independent in-test
  port of the genuine hash+search locates every emitted symbol; and genuine MS
  `LINK.EXE` consumes both an emitted object and an emitted `.LIB` (`OmfTests`,
  `OmfLibraryWriterTests`, `LinkOracleTests`). The CLI exposes both: `pbc --emit-obj`
  writes a linkable `.OBJ`, `pbc lib build out.LIB ...` writes an OMF archive
  (`EmitObjTests`, `LibBuildTests`).
- **Per-convention auto name-decoration** (stdcall `_name@N`, fastcall `@name`,
  watcall `name_`, pascal upper) instead of requiring `ALIAS`.

## B. BASIC language features codegen still rejects

### Oracle-confirmed fidelity gap (found once the pb35 differential battery could run locally)
- **LONG multiply overflow should trap Error 6, not store the sentinel.** Genuine PBC 3.50 raises
  Error 6 when an overflowing signed-LONG product is narrowed into a LONG store (goes to the
  `ON ERROR` handler; halts the program without one) - a *wide use* like `PRINT a& * b&` shows the
  full product with no trap, and a DWORD multiply wraps silently. PB-Compiler currently stores the
  x87 integer-indefinite sentinel (`8000_0000h`) instead of trapping: the float->LONG store needs
  an overflow check that raises Error 6 under checked arithmetic (and by default). Marker: the
  advisory differential test `DIFF105`. The sibling LONG `+`/`-` overflow bug (should wrap, was
  storing the sentinel) is already fixed and byte-identical (`DIFF113`). See docs/QUIRKS.md.


Straight from the `Unsupported(...)` survey.

### Should
- ~~`ON ERROR RESUME NEXT`~~ - done (inline mode; byte-identical vs PBC 3.50, battery `DIFF84`).
- ~~`ARRAY SORT`/`ARRAY SCAN` on non-string arrays, and `TAGARRAY`~~ - done: numeric arrays of every element kind (signed/unsigned BYTE/WORD/DWORD/INTEGER/LONG/QUAD, SINGLE/DOUBLE/EXT) sort and scan byte-identical vs PBC 3.50 (battery `DIFF86`), including ASCEND/DESCEND, all six SCAN relops, and `ARRAY SORT ... TAGARRAY`. The comparison runs on the x87 (elements widened into a staging cell), so unsigned values past their signed range still order by true value. FROM/TO ranges and COLLATE stay string-only (rejected for numeric arrays, as in genuine PBC).
- ~~Finish `QUAD` (64-bit integer) operators~~ - done: every `QUAD` operator (`+ - * \ MOD AND OR XOR EQV IMP`, comparisons, unary `- NOT`, and the float-typed `/` and `^`) is byte-identical vs PBC 3.50 (battery `DIFF85`). The remaining `Unsupported` arm in `EmitInt64Op` is an unreachable guard: the binder types `QUAD /` as DOUBLE and `QUAD ^` as EXT, so both run on the float path, never reaching the integral op switch.

### Could
- **Graphics statements (`LINE`/`PSET`/`PRESET`/`CIRCLE`/`PAINT`/`DRAW`)** - parsed and bound,
  but codegen answers `not yet generated: LineStmt`. Two oracle-verified notes for whoever
  implements them: the parser also rejects the relative-coordinate form
  `LINE (x,y)-STEP(dx,dy), c, BF` (`expected '(', found 'STEP'`), which PBC 3.50 accepts - it
  compiles PB-VGAEditor's SPRITE.SUB, which uses it; and `STEP`'s base differs by position (the
  cursor for a first/only point, the first point for a LINE's endpoint), so it belongs in
  `ParsePoint` as a flag on the point, not as a statement-level switch.
- **Constant-propagate the counter into unrolled loop bodies.** O7 fully unrolls a tiny
  constant-trip FOR (`TryEmitUnrolledFor`), but each copy still reads the counter cell and
  recomputes: `FOR i%=0 TO 3: a%(i%) = i%*i%` emits an `IMUL` and an address computation per
  iteration where the counter is a known constant (0,1,2,3), so `i%*i%` and the `a%(i%)` index
  should fold to `a%(0)=0, a%(1)=1, …`. The blocker is architectural: the emitter is keyed by
  original-AST-node identity (`VariableBindings`, `TypeOf`, `ResolvedConstants`), so substituting
  the counter with a literal per iteration would need every semantic side-table repopulated for
  the cloned nodes. A per-iteration constant-override consulted by the folder and
  `IndexRangeOf`/`TryFoldSubscripts` is the smaller path.
- Multi-dimensional arrays (rank > 1) for the memory array classes (EMS/XMS/...)
  and multi-dimensional UDT field arrays.
- `REDIM PRESERVE` on more array classes.
- Non-literal / multi-value `PRINT USING` formats.
- UDT compare/copy edge cases; `ABSOLUTE` arrays without an `AT` segment.

## C. Dialect front-end completeness (`docs/BASIC-FAMILY.md`)

The GW-BASIC, BASICA and QBasic front-ends **already exist and are oracle-validated**
(`--dialect gw|basica|qbasic`, byte-identical `DIFF01` batteries) — earlier roadmap
text here was stale. Remaining dialect work is *deeper per-dialect coverage*, not new
front-ends:

### Could
- Broaden each dialect's diff battery beyond `DIFF01` (arrays, string functions,
  DATA/READ, error model under the interpreter oracles).
- Per-family intrinsic-catalog gaps (e.g. GW vs QB `INSTR` start arg) verified
  against the oracle.

### Known issue (oracle environment, not codegen)
- `pds70`/`pds71` diff batteries currently FAIL with "real PBC produced no RESULT.TXT":
  the genuine BASIC PDS 7.x `BC.EXE` oracle does not run in the current DOSBox setup
  (empty BCLOG, no `T.OBJ`). Pre-existing, reproducible, environment-side; our codegen
  is unaffected. Needs the pds7x oracle invocation re-validated under DOSBox.

## Won't (for now)
- Win16 / protected-mode targets; 32-bit OMF.

## The IR path towards output parity with the direct emitter

The retargetable path (`Ir/` -> `Backend/`, gated behind `--x-backend`) is meant to eventually
produce what the direct emitter produces. Measured by `BackendCoverageTests` over the 165-program
battery, it currently reaches:

| | |
|---|---|
| programs reaching the IR at all | **161 / 165** — every one the FRONT end accepts; the other 4 it rejects |
| **functions ROUTED in production, `--optimize`** | **263 / 263** |
| **functions ROUTED in production, `--no-optimize`** | **259 / 263** |
| **whole module bodies the back end owns** | **161 / 161** |
| functions the SELECTOR would take, if offered one | 263 / 263 |
| module bodies the selector would take | 161 / 161 |

The last two rows are what this table used to report as coverage, and they are not it.
`CodeGenerator.BackendProcs` refuses a procedure on its SHAPE — QUAD, BYTE, FIX, EXT or
record values, an unsupported BYREF pointee, a non-default calling convention, or error handling in
the body — before the selector is asked, so a procedure it skips lands in neither half of the ratio
and 263/263 means "of the functions we attempted, how many succeeded". The routed rows come from the
production code generator's own record of its own decision (`CodeGenerator.BackendDeclines`). The
optimized corpus gap is now empty: `LINKDEMO` routes when the census supplies the `MATHUNIT.PBU` named
by its `$LINK`, while incompatible external conventions still decline individually. With optimization
off, four selector gaps remain: two phi edge-copy cycles, one `FPToSI f80 -> i64`, and one `f32`
`select`. Near BYREF
INTEGER/WORD/LONG/DWORD/SINGLE/DOUBLE
parameters now route through one-word pointers and are pinned by mutation, aliasing, recursion,
optimizer-mode execution, and the SPEED caller/callee fixpoint. Whole classes the corpus never
exercises — QUAD, BYTE, FIX and EXT parameters and results, the four non-default conventions, error
handling in a procedure body — remain pinned one program each by `BackendRoutingGateTests`.

Dynamic STRING procedure values use the same one-word stack/AX representation as the direct emitter.
The IR now makes the non-representational part of that ABI explicit: BYVAL transfers ownership to the
callee, BYREF passes the owner cell, results transfer back to the caller, and locals, copy-in
temporaries, discarded results and BYVAL parameters are each released at their ownership boundary.
That routes the two string-returning corpus functions and the one string-parameter procedure in both
optimizer modes.

Dynamic-string `SWAP` now transfers the two raw runtime handles between their owner cells, including
far string-array elements, without duplicating or freeing either handle. That removes the last
procedure-body lowering decline and returns `CODEGEN.BAS` to production whole-module routing.

**Every function the selector takes is now allocated.** The last one that was not was `DIFF56`'s module
body — a 32-bit accumulation over a static array — and it took two independent repairs, one on each
side of the pressure:

- **the scheduler no longer manufactures pressure it cannot pay for.** With constant bounds the loop is
  unrolled, so all ten element loads are ready at the top of the block, and a list scheduler maximizing
  independence issues them there: ten live values on a six-register machine, where the selector's own
  serial order needed four. `MachineScheduler` now measures the peak simultaneous live count of a
  proposed order and keeps the written one when the reordering would cross the register file. Below six
  it changes nothing, so this only ever refuses a schedule that could not have been allocated.
- **the spiller has an answer for pressure it did not create.** Live-range splitting only considered
  values live across a full-register clobber, which is the pressure a `CALL` makes; four `LONG`
  accumulators wanted at once are eight words with no call in sight, and none of them can move to
  memory as it stands (a value loaded out of an array already has a memory operand in its defining
  instruction, so making it one too would be a memory-to-memory `MOV`). `Spiller.SplitOne` gained a
  second pass over plain pressure, with `MFunction.SplitValues` recording what it has already taken
  apart so the transformation terminates.

Each is load-bearing on its own: the accumulation routes without the scheduler gate, and the
four-accumulator procedure declines without the splitting pass whether it is scheduled or not.

### Inline assembly

Carried, not understood. `IrInlineAsm` holds the text of a `!` statement as an opaque barrier, and the
function containing one is flagged `HasInlineAsm` so `IrPassManager` skips it whole - the same trade
made for `HasErrorHandler`, and the one the direct emitter already makes.

It is a barrier rather than a modelled instruction deliberately. A modelled one needs every operand,
result and clobber the text implies, and a list that is one entry short miscompiles silently: the same
failure as an under-declared machine effect, which is how an `FSQRT` ended up scheduled past the store
that captured its answer. Guessing is worse than declining.

What it buys today is that inline asm stops being a **wall**. A program with one `!` line used to keep
every one of its procedures off the IR path; now only the procedure containing it is unoptimized, and
its siblings promote, fold and route normally. The IR-to-BASIC writer renders it back verbatim, which
is exact rather than approximate - the writer's target is PowerBASIC, so the faithful rendering of `!`
text is that text.

**The frame resolver is done.** Names are bound at LOWERING, against the semantic model, and travel on
the instruction: `IrInlineAsm.Names[i]` is the identifier that operand `i` addresses. The selector
turns each bound pointer into the machine cell it denotes, and `MachineEmitter.FrameResolver` answers
the assembler from those cells - so the emitter never has to know what a BASIC variable is, only where
this back end put it. That is the question the direct emitter's resolver could not answer for a frame
it did not lay out.

Collecting the names is done by ASSEMBLING the text against a throwaway target with a recording
resolver, not by scanning it: the real parser knows which tokens are registers, which are mnemonics
and which are operands. (The stand-in it answers with has to be MEMORY - a constant makes `MOV n, AX`
not an instruction, and every write-to-a-variable block reported itself unbindable - except for a
BASIC LABEL, which has to be answered as a label for the same reason: `JNZ [BP+0]` is not an
instruction either. So the probe asks the lowering which kind each name is rather than guessing from
the spelling, and reaches the conclusion the real resolver will.)

**A BASIC label binds too**, to the block's own address rather than to storage: `!JNZ AddLoop` is a
jump the CFG does not draw, and the address it needs is the `IrBlockAddress` an `ON ERROR` handler and
`CODEPTR32` are already named by. Which makes the keep-alive rule automatic - the block is
address-taken, so `SimplifyCfg` and `Sccp` may not merge or drop it.

**Closed:** a register an `!` statement loads now survives an intervening BASIC statement on the routed
path. The allocator used to be free to put a temporary in `CX` with no way to know the text cared, and
the direct emitter left `CX` alone by luck rather than by contract; the statement now DECLARES what it
defines and reads - the assembler reads it out of the text - and the allocator reserves the register
over the stretch between the two statements. `LOWLEVEL.BAS` relies on exactly this and routes whole,
printing 5 where it printed 1. A register something in between destroys (a call owns the whole
caller-saved file) still declines, because no allocation can answer that one.

**Inline asm routes.** Getting there needed one more thing, and it was not in the asm path at all: a
single-slot alloca is now addressed AS its slot rather than through the register its `LEA` put the
address in. Nothing indexes a scalar - only a multi-slot block needs a base to walk from - and that
register was costing real allocations, because a value used as a memory BASE is the one thing the
spiller cannot move, so any instruction clobbering the whole register file in between left it nowhere
to go. Inline asm is exactly such an instruction; the register file it clobbers is why the function
selected and never allocated.

The `LEA` is still emitted and is now dead for scalars, which is worth cleaning up but costs only size.

### Float comparison as a value - implemented, and the precision gap it exposed is closed

`FLD lhs; FLD rhs; FXCH; FCOMPP; FSTSW AX; SAHF` then the usual -1/0 diamond, with the UNSIGNED
conditions because SAHF puts C3 in the ZF and C0 in the CF and the x87 never sets the sign flag. It is
now in the machine IR. `FSTSW AX` explicitly clobbers AX, `SAHF` explicitly reads it and writes the
flags, and both `FCOMPP` and the status transfer are x87 operations for scheduling purposes. All six
ordered predicates materialize BASIC truth as `-1` or `0` and survive scheduling and allocation.
This is the direct emitter's status-bit mapping. Comparisons involving a raw NaN reconstructed from
binary data still need a vintage-compiler oracle before either path can claim dialect-exact behavior.

The first implementation exposed fourteen differential disagreements. They were not an x87 temporary-
width problem: `DIFF02` printed `1.20000000447035` where the direct emitter printed
`1.20000004023314` because constant-step lowering built a DOUBLE IR constant directly from the host
`double` returned by `ConstantFolder`. That bypassed the source rule that an unsuffixed `0.3` is first
a SINGLE. Float literals now quantize at that source boundary, and a constant FOR step goes through
the same `LowerExpr` plus coercion path as a runtime step. Ten more programs participated in both
optimization modes at that milestone: 228 participating, 222 agreeing, 6 outside the executor's
opcode set, and 0 disagreeing.

### Signed 32-bit division and remainder - implemented through the DOS runtime

The IR's `SDiv` and `SRem` pair values now use the same ABI as the direct emitter: dividend in
`DX:AX`, divisor in `CX:BX`, and the selected result in `DX:AX`, calling `rt_ldiv` or `rt_lmod`.
The helpers handle runtime divisors, sign the remainder like the dividend, preserve the established
`MINLONG \ -1` result, and raise PowerBASIC Error 11 through `rt_raise` for zero. The four pinned
argument moves and the call declare their physical clobbers, so scheduling cannot split the sequence
and the allocator spills around it.

This removed all five `SDiv on i32` and the one `SRem on i32` census entries. Selection/routing moved
from 200 to 205 functions and whole-module ownership from 110 to 113 programs. The broader corpus
differential now reports 234 participating, 228 agreeing, 6 outside the executor's opcode set, and 0
disagreeing. `DIFF32` reaches the next honest blocker, an `i64`-to-`u32` truncation, instead of being
counted as complete merely because division selects.

### SINGLE/DOUBLE procedure ABI - routed through declared-width stack cells

IEEE `SINGLE` and `DOUBLE` BYVAL parameters and function results now use the direct emitter's
BASIC/PASCAL ABI end to end. A callee loads each incoming parameter straight from its caller-owned
four- or eight-byte stack cell. A caller first stores the x87-width intermediate into a temporary of
the parameter's declared width - the required IEEE rounding and encoding boundary - then pushes its
words high to low. A real function returns on `ST(0)`; its caller immediately parks the result in the
ten-byte cell used for float SSA temporaries, restoring the selector's empty-x87-stack invariant.

The scheduler now knows that a `PUSH` from a stack/data/parameter cell reads memory, so it cannot move
one of those pushes above the `FSTP` that creates the staged argument. Focused selection tests pin the
widths and word order; an executed two-argument `DOUBLE`/`SINGLE` function plus a `SINGLE` precision
boundary agrees with the direct emitter.

This removes both `FNDouble returns f32` call declines, the `Half returns f32` decline, and the
remaining `f32 parameter has no frame cell` decline. Selection/routing is now 209 of 233 functions and
whole-module ownership is 116 of 135 lowered programs, with zero allocation declines. The corpus
differential remains 234 participating, 228 agreeing, 6 outside the executor's opcode set, and 0
disagreeing: those programs already counted as participating because another procedure routed; the
new result is that their complete module bodies are now owned. `EXT`, MBF, BYREF reals, and foreign
register conventions remain separate ABI work.

### Remaining DOS string kernels - explicit runtime ABI mappings

The IR's last three string calls with existing DOS implementations now cross the same explicit bridge
as the rest of the string runtime. `rt_str_compare` calls consuming `rt_strcmp` with handles in
`AX`/`DX`, then sign-extends its word-sized `-1`/`0`/`1` answer to the IR's `i32`. Two-argument
`rt_str_mid2` calls `rt_strmid` with the direct emitter's `DX=7FFFh` maximum-length preset.
`rt_str_mid_assign` calls `rt_midset` with target/start/limit/replacement in `AX`/`CX`/`BX`/`DX`; the
kernel consumes the replacement and returns the duplicated, mutated target handle in `AX` for the IR
store back to the lvalue.

The selector tests pin those registers, the preset, and the result extension. Executed tests use
runtime INTEGER start/length values and compare optimized and unoptimized routed images with the direct
emitter. Arbitrary true 32-bit indices still decline unless the existing word extraction proves that
narrowing is safe; their historical overflow behavior needs an oracle rather than an implicit truncation.

All four affected module bodies (`DIFF02`, `DIFF40`, `DIFF54`, and `STRINGS`) route at this milestone.
Selection and routing move 209 → 213 of 233 functions, whole-module ownership moves 116 → 120 of 135
lowered programs, and allocation declines remain zero. The differential moves to 242 participating,
235 agreeing, 7 outside the executor's opcode set, and 0 disagreeing.

### Shared arrays and STATIC locals - one data layout, stable identities

The x86-16 back end now addresses shared static arrays through the cells the direct emitter already
owns. A global-array GEP materializes `OFFSET g.name` plus its constant or runtime byte offset; loads
and stores then use the resulting address normally. No second data segment or competing layout exists.

`STATIC` locals use the same bridge, but their IR names now include their owning procedure:
`static.<procedure>.<local>` (and an overload index where PB 3.6 needs one). This makes two legal
`STATIC count` declarations distinct and lets emission resolve each name to the exact `VariableSymbol`
whose `SlotOf` cell the direct path uses. Synthesized globals such as `.data_cursor` no longer decline
either - see below.

This removes all four shared/static named-procedure declines in `SHAREDG` and `SUBFN`. Against the
post-pull census, selection and routing move **220 → 224 of 240**, whole-module ownership moves
**126 → 127 of 142**, and allocation declines remain zero. `SHAREDG` joins differential execution in
both modes, moving it to **256 participating, 249 agreeing, 7 emulator-limited, and 0 disagreeing**.

### Integer switch selection - every lowered named procedure now routes

`IrSwitch` now selects to an explicit 8086 compare chain. Byte and word selectors compare each case
directly. A dword selector first branches on its high word, then compares low words inside the matching
group, preserving all 32 bits without inventing a 32-bit machine instruction. Default and case edges
remain explicit in the machine CFG, and successor phi values are copied before dispatch.

Switch equality is defined once as fixed-width bit equality across SimplifyCFG, SCCP, and instruction
selection. Signed and unsigned spellings of the same pattern therefore agree (`i16 -1` is `i16 65535`),
while the verifier rejects non-integer conditions, out-of-width cases, and duplicate patterns.

Language-level `ON GOTO` first coerces its selector to the historical 16-bit `INTEGER`, matching the
direct emitter: `65537&` therefore selects arm 1. Focused execution covers negative, zero, every
in-range arm, the first value above the range, and that LONG-to-INTEGER truncation in both optimization
modes. The general raw-I32 switch path is separately verified through selection, scheduling, and
allocation.

Selection and routing move **224 → 225 of 240**, with zero allocation declines. The census now has
**no declined named procedures** among the programs that reach IR. Module ownership and the
differential counts remain 127/142 and 256/249/7/0: `CODEGEN.BAS` already participated through other
routed functions, while its `main` still honestly declines at an external call.

### Binary-record strings and paired runtime results - explicit and trimmable

The complete binary-record family now crosses the runtime ABI. `MKI$`, `MKL$`, `MKDWD$`, `MKS$`, and
`MKD$`, together with `MKBYT$`, `MKWRD$`, and `MKE$`, stage exact little-endian integer or
declared-width IEEE bytes in `rt_scratch`, allocate an owned BASIC string, and remain a separately
trimmable runtime section. `CVI`, `CVL`, `CVDWD`, `CVS`, and `CVD`, plus the
`CVBYT`/`CVWRD`/`CVE` aliases, use the existing copy/pad kernel. Selection then loads the exact byte,
word, dword, binary32, or binary64 scratch representation for both the ordinary and start-offset CV
forms.

The ABI also represents a 32-bit answer in `DX:AX`. Integer-range `RND`, `LOF`, and `LOC` copy both
halves immediately after their runtime call, giving scheduling and allocation explicit dependencies
on the physical result pair.

`DIFF08` and `DIFF58` now route as whole module bodies. Selection/routing moves **225 → 227 of 240**,
module ownership moves **127 → 129 of 142**, and allocation declines remain zero. The differential
moves to **260 participating, 251 agreeing, 9 emulator-limited, and 0 disagreeing**. `DIFF58` agrees
in both modes; both `DIFF08` executions stop only because the test executor does not yet implement DOS
device-information call `AX=4400h`.

### Segmented raw-memory comparison - DS globals and SS locals

Whole `TYPE`/`UNION` equality now crosses the x86-16 runtime ABI without flattening a segmented
address to an offset. Selection derives DS for module/static objects and SS for frame objects, then
passes the left address in `DX:SI`, the right in `BX:DI`, and the byte count in CX. The separately
trimmable `rt_memcmp` kernel installs DS/ES, scans unsigned bytes with `REPE CMPSB`, restores both
segment registers, and returns -1/0/1 in AX; the bridge sign-extends that word to the IR's i32 result.

Focused tests cover equal and unequal records, module globals, stack locals, and both optimization
modes against the direct emitter. `DIFF10` becomes a complete module body: selection/routing moves
**227 → 228 of 240**, ownership moves **129 → 130 of 142**, and allocation declines remain zero. The
differential moves to **262 participating, 253 agreeing, 9 emulator-limited, and 0 disagreeing**.

### Segmented raw-memory copy/fill - exact tails and static storage

Whole-record assignment and static-array `ERASE` now cross the same segmented-pointer ABI through
LLVM's `memcpy` and `memset` intrinsics. Selection derives DS for module/static objects and SS for
frame objects. The separately trimmable `rt_memcpy` kernel consumes source `DX:SI`, destination
`BX:DI`, and byte count CX; `rt_memset` consumes destination `BX:DI`, fill byte AL, and byte count CX.
Both use byte-string instructions so a seven-byte UDT tail is copied exactly, and both restore every
segment register they change. A nonconstant LLVM volatility flag remains an honest selection decline.

Focused optimized/unoptimized tests cover odd-sized UDT copies and static-array zero fill. `DIFF23`
and `DIFF74` become complete module bodies: selection/routing moves **228 → 230 of 240**, ownership
moves **130 → 132 of 142**, and allocation declines remain zero. The differential moves to
**266 participating, 256 agreeing, 10 emulator-limited, and 0 disagreeing**; one new execution reaches
an existing direct-emitter test-CPU opcode limitation rather than being credited as agreement.

### The IR's own DATA pool and read cursor - two pools, never both live

`DATA`/`READ`/`RESTORE` lower to a length-prefixed blob (`.data`) and a read cursor
(`.data_cursor`), and the back end now lays both down itself as `ir_datapool` / `ir_dataptr`. They sit
*beside* the direct emitter's `rt_datapool` / `rt_dataptr` rather than replacing them, because the two
cursors do not mean the same thing: the IR's is a blob-relative **index**, the direct emitter's an
**absolute pointer**, and one cell cannot be both. `CodeGenerator.BackendOwnsData` is what makes two
pools safe - it refuses the whole arrangement when any procedure the direct emitter still compiles
also reads `DATA`, so no program can ever advance one cursor and consult the other. A `READ` of a
string item calls `rt_str_from_fixed` rather than `rt_str_const`: same routine underneath, but a
`DATA` item is *n* bytes at an offset into the pool where a constant is a whole pooled literal named
by its global, and only the second can be reached by naming one.

`DATAREAD` becomes a complete module body, and the differential still reports zero disagreements.

Getting there needed a load-forwarding fix that had nothing to do with `DATA` and everything to do
with when the assembler's bytes become true. `MOV WORD PTR [BP-88],OFFSET ir_datapool+21` is emitted
with a **zero placeholder** and the address written in when the label resolves, so
`FrameCellImmediate` - which reads the immediate straight out of the buffer - answered 0 for a cell
that was going to hold an address, and the reload was rewritten to `MOV SI,0`. It cost `DATAREAD` a
garbage string at `-O1` and nothing at all at `-O0`, the pass being off there; the pool bytes, the IR,
and every individual instruction all looked correct throughout. Any `MOV WORD PTR [BP+d],<label>`
followed by a reload had the same defect waiting - `DATA` is only the first construct to emit that
shape - and the pass now declines any store whose bytes a pending fixup will overwrite.

### QUAD storage, FIX/BCD, inline-asm exports and fixed-string LSET/RSET

Four independent declines, each holding one program, and two shared cells they uncovered. The census
moves to **156/164 programs lowered**, **254/256 functions selected and routed**, **154/156 module
bodies owned**, and the corpus differential to **302 participating, 289 agreeing, 13
emulator-limited, 0 disagreeing**.

- **`LSET`/`RSET` into a fixed-length string.** Both were lowered for a FIELD variable, where they
  justify a value inside the length the target already has. A `STRING * n` has no handle to justify
  inside: the bytes are the variable and the width is declared, so `LSET` is exactly the padded store
  an assignment already makes and `RSET` the same store against the far edge (`rt_storefixed_r`,
  which the DOS runtime already had and the ABI table did not).
- **A non-constant QUAD reaching the 64-bit printer.** A 64-bit integer has no register form here, so
  the selector now gives one a frame cell of its own and copies it with `FILD qword` / `FISTP qword`.
  The matching STORE had to land in the same step: without it a QUAD literal store went out through a
  386 operand-size prefix carrying half the value.
- **FIX (`@`) and BCD (`@@`).** Two types, not one. BCD is an `f80` cell whose bits are the value;
  FIX is a scaled `i64` whose scale lives in `pbvFixDigits`, a runtime cell - so the conversions are
  calls to `rt_fixdn` / `rt_fixup` and never a folded divide. No new `IrCastOp` was needed: the type
  map plus `Coerce` plus two ABI rows carry it.
- **Inline asm naming a runtime export.** `CALL GetStrLoc` was the one unbound name in `DIFF20`'s
  documented-ABI block - the string handle beside it already bound. An export is code, so it carries
  no cell and the machine emitter resolves it to the runtime's label.

The two cells the differential caught on the way, both pre-existing and both invisible until the
programs that read them routed: `ERL` was never recorded at a numeric line label, and every PB
internal variable (`pbvFixDigits`, `pbvScrnCols`, …) was given a private frame slot instead of the
runtime cell it names.

### Wider integers and SIMD as IR operations - not started

The IR has no integer tier above the dialects' own widths and no vector type; `MRegSize` reads
`Byte, Word, Dword, Qword, Tbyte`, which is a 16-bit machine's operand set. Making 64/128/256/512-bit
integers and MMX/SSE/AVX-shaped operations IR nodes would let each back end choose how to realise
them - a loop on an 8086, register pairs on a 386, one instruction on an MMX or SSE target - without
the front end knowing which. That is the difference between one emitter per target and one emitter
with a target parameter.

Ordering note: the existing back end is x86-**16** and now implements signed 32-bit arithmetic with
word pairs and runtime helpers, but unsigned divide/remainder and general 64-bit values still lack a
machine representation. Widening that integer tier and parameterizing the selector comes before
vectors.

### Porting the optimization catalogue to the IR — the real denominator

"Port the 421 optimizations to the IR" needs a denominator before it means anything, and 421 is the
wrong one. Every document in `docs/optimizations/` carries a **Stage**, and most of them say
*Emitter*, *Assembler*, *Register allocation*, *Layout*, *Linker* or *Scheduler*. Those are not IR
work by any reading — they are decisions about which instruction, which register, which encoding,
which address. The retargetable path needs its **own** versions in its own back end; it cannot
inherit them.

Measured by `OptimizationPortingLedgerTests`, which reads the Stage of every document:

| | |
|---|---|
| documented optimizations with a Stage | 420 |
| machine-level (not IR work at all) | **290** |
| portable to the IR | **126** |
| …already expressed on the IR | 20 |
| …still to port | **110**, of which **20** now carry an `IR` row |

So the target is 110, not 420 — and the bulk of it is one category, *Mid-end* (52). The ledger is a
test with floors, so the portable share cannot shrink by reclassification instead of movement.

A ported optimization records an **IR** row in its own document, which is what the ledger counts.
`Stage` says where an optimization was *first* written and never changes; the `IR` row says where it
*also* lives now, and only grows. Ported so far:

- **O0001 constant folding** — `Sccp` + `InstCombine` + `IrConstFold`.
- **O0002 dead-code elimination** — `Dce` + `DeadStoreElim`.
- **O0003 common subexpression elimination** — `Gvn`, and *global* where the emitter's is
  block-local: a subexpression shared across two blocks is still computed once.
- **O0006 inlining** — `Ir/Passes/Inliner.cs`, run by `CodeGenerator.BackendProcs` and followed by
  another full pass sweep: the point of inlining is not the call overhead but that the callee body
  becomes visible to the caller's optimizer, and nothing sees it until the passes run again.
- **O0007 loop unrolling** — `Ir/Passes/LoopUnroll.cs`, in the standard pipeline.
- **O0132 whole-loop compile-time evaluation** — nobody wrote a pass for it; it falls out of
  unrolling composing with the constant propagation and dead-code elimination already there.
  `FOR i = 1 TO 5 / s = s + i / NEXT / PRINT s` becomes `PRINT 15`.

- **O0018 interprocedural constant propagation** and **O0159 return-value propagation** —
  `Ir/Passes/IpConstantProp.cs`, the two directions of the same fixpoint. In: a parameter every
  visible call passes the same literal for *is* that literal. Out: a function whose every `ret`
  returns the same constant hands that constant to its call sites. Both stand on `IsFullyVisible`,
  which declines `main` and any function whose address appears anywhere but a callee operand —
  because "every call site passes 1" is a statement about the *visible* calls, and is worthless if
  the module cannot enumerate them all. Registered with the new `AddModulePass`, so `RunOnModule`
  runs the function pipeline, then the interprocedural pass, then the function pipeline again.
- **O0027 copy propagation** — falls out of SSA: mem2reg leaves no copies to propagate.
- **O0028 loop-invariant code motion** — `Ir/Passes/Licm.cs`.
- **O0061 reassociation** — `Ir/Passes/Reassociate.cs`. The direct tier already merged adjacent
  constants; what it could not do, and this does, is order the *variable* leaves by a stable id, so
  `x+y+1` and `1+y+x` become the same tree and GVN numbers them as one value. Integer `+ * AND OR
  XOR` only: reassociating floating point changes the answer, which is why that is a separate,
  opt-in optimization and not this one.
- **O0097 repeated comparison elimination** — `Gvn` keys `IrCmp` by predicate and operands under
  dominator scoping, so a comparison recomputed where the first one dominates is reused.
- **O0132 whole-loop compile-time evaluation** — nobody wrote a pass for it; it falls out of
  unrolling composing with the constant propagation and dead-code elimination already there.
  `FOR i = 1 TO 5 / s = s + i / NEXT / PRINT s` becomes `PRINT 15`.
- **O0182 small local array scalar replacement** — `Ir/Passes/ScalarReplaceArrays.cs`. A tiny
  non-escaping array indexed only by constants is N variables wearing one name; split, mem2reg
  promotes each element into SSA and the rest of the pipeline can see through it. It sits *after*
  SCCP, because a subscript is not constant in the raw lowering — it is `index * sizeof(element)`
  with the index still an expression.
- **O0165 read-only global propagation** — `Ir/Passes/ReadOnlyGlobals.cs`. A module-level variable
  nothing ever writes reads as ZERO, which is what PB guarantees an uninitialized variable holds. It
  sounds like it would never fire, and it fires because DOS-era BASIC uses `DIM SHARED` where a
  modern program would use `CONST`.
- **O0022 dead procedure elimination** and **O0023 dead global elimination** — `Ir/Passes/GlobalDce.cs`,
  run from the driver on the `--emit-c` / `--emit-llvm` path. Deliberately NOT in the hybrid x86
  pipeline: there the IR module is not the whole program, so removing a function only stops it being
  routed. Measured, that cost six corpus comparisons and saved nothing.
- **O0278 global variable localization** — `Ir/Passes/LocalizeGlobals.cs`. A scalar global whose only
  user is one function becomes an alloca there. "Only one user" is NOT the whole condition: a global
  keeps its value between calls and a local does not, so it also requires a store in the entry block
  with no load before it, which makes the incoming value unobservable.
- **O0111 redundant induction-variable elimination** — `Ir/Passes/PhiCongruence.cs`. Two loop-carried
  values advancing in lockstep are one value written twice. It has to start OPTIMISTICALLY, because a
  loop phi's latch value is derived from the phi itself and a pessimistic proof of that cycle is
  circular - which is also why GVN skips phis entirely and leaves these untouched.
- **O0161 function summaries (mod/ref)** — `Ir/Passes/FunctionSummaries.cs`. Two bits per procedure,
  computed as a fixpoint over the call graph that starts from PURE and only ever adds impurity - which
  is what makes a recursive pure function come out pure. Its first consumer, `RemoveDeadPureCalls`, is
  NOT in the standard pipeline: `DIFF113` declares `SUB Opaque(v&)` with an empty body precisely to be
  an optimization barrier, and dropping the call hands the DIRECT emitter's optimizer code it could not
  previously see through. What it then does with it differs from the original - a finding about that
  optimizer rather than about this pass, and one that needs chasing before the consumer is turned on.
  Its SECOND consumer is on: `IsPureExternal` / `IsSpeculatableExternal` name the externals `Gvn` may
  number and `Licm` may hoist. Eight rows, all float math intrinsics, and the interesting part is what
  is kept OFF - `rt_str_len` looks like a pure read and is not one, because the DOS entry frees the
  handle it is given, which is why the lowering copies every read of a string variable in the first
  place.
- **O0225 SSA construction** — `Ir/IrDominators.cs` + `Ir/Passes/Mem2Reg.cs`, the same Cytron
  construction the direct tier has.

O0132 is the argument for the whole exercise: a ported optimization that *enables* another without
further work is the compounding the retargetable path was supposed to get.

The porting also pays back the other way, which was not expected. Interprocedural propagation made
three corpus functions **stop** being selectable — it turned a parameter into a literal, and a
`select` with a constant condition reached the back end with an immediate where a register was
required. `InstCombine` had no `select` rule at all. Adding one (`select true, a, b → a`, and a
select whose arms match) recovered those three and 16 more: functions routed went 93 → 109. A gap
that had been sitting in the peephole tier since it was written was only visible once something
upstream started producing the shape that hits it.

The runtime traps and the error handler are **done**. `$ERROR BOUNDS / OVERFLOW / NUMERIC ON` now emit
their checks rather than merely accepting the metastatement, over dynamic arrays as well as static
ones; `ON ERROR` / `RESUME` / `ERROR n` / `ERRCLEAR` lower, along with the `ERR` / `ERL` / `ERADR`
cells a handler reads. See [IR.md](IR.md) for how a construct whose control flow no CFG can express is
kept sound - `IrBlockAddress` names the handler and `IrFunction.HasErrorHandler` takes the whole
function out of the optimizer, the same trade the direct emitter makes with `_trackResume`.

Ranked by the census, what stands between that and full coverage:

1. **A PROCEDURE that arms a handler is still not routed** (the module body now is). The direct path
   saves the caller's handler triple on entry and restores it on every exit; the routed prologue has
   no equivalent, and routing without it would lose the caller's handler silently.
2. A tail of statements: `LSET` / `RSET`, `DIM AT`, `HEX$` with a digit count, `PRINT USING` /
   `LPRINT`, `FIELD`, `CHAIN`, and the `$COMPILE` / `$IF` / `$LINK` / `$STRING`
2. A tail of statements: `LSET` / `RSET` (`DIM … AT` and the memory-model classes `HUGE` /
   `VIRTUAL` / `EMS` / `XMS` came off this list - see the far-pointer notes in
   [X86-BACKEND.md](X86-BACKEND.md) and [BACKENDS.md](BACKENDS.md)), `HEX$` with a digit count, `PRINT USING` /
   `LPRINT`, `CODEPTR32`, `FIELD`, `CHAIN`, and the `$COMPILE` / `$IF` / `$LINK` / `$STRING`
   metastatements. `ARRAY SORT` / `ARRAY SCAN` came off this list: the parameter block is a set of
   stores to NAMED runtime cells, which the IR addresses directly, and only the array DESCRIPTOR
   needed a routine - it opens with a segment, and a segment register is not a value the IR can name
   (`rt_arr_desc`, DosRuntime.ArrayDesc). A string array routes too: reading or writing one of its
   elements is an element-indexed GEP, which the selector declined wherever it appeared until it
   learned to scale the index by the target's own pointer width (X86-BACKEND.md).
2. A tail of statements: `ArraySortStmt`, `PUT$`, `DIM AT`, `ERASE` of a static array, `HEX$` with a
   digit count, and the `$COMPILE` / `$IF` / `$LINK` / `$STRING`
2. A tail of statements: `ArraySortStmt`, `PUT$`, `IrFarPtr` (a `DIM … AT` element), `ERASE` of a static array, `HEX$` with a
   digit count, `CODEPTR32`, and the `$COMPILE` / `$IF` / `$LINK` / `$STRING`
   metastatements. `PRINT USING` and `LPRINT` lower; a NON-LITERAL USING format still declines,
   since the format is read at compile time on both paths. `VARPTR` and `CODEPTR32` came off this
   list: an address is `ptrtoint` of the address `VARPTR32` already forms, and a LABEL's address is
   the `IrBlockAddress` an `ON ERROR` handler is already named by, jumped through by a new
   `IrIndirectBr` whose target list keeps the CFG honest about where a computed jump can land
   (X86-BACKEND.md). `CODEPTR32` of a PROCEDURE still declines: the direct emitter answers it with a
   far entry THUNK it synthesizes, and the IR has nothing of the kind to point at.
3. **Register allocation no longer loses selected functions:** all 192 selected functions route.
   Direct memory spills, address rematerialization, multi-definition/RMW live-range splitting and
   per-use argument reloads cover every allocation shape currently present in the corpus.
4. The largest remaining selection declines are runtime routines with no entry in the ABI table, a 32-bit
   compare used as a value, and a float phi with no frame cell. The table grows one routine at a time
   and only after its emitter has been read - see [X86-BACKEND.md](X86-BACKEND.md). Several of the
   remaining ones need the bridge to grow first, not just an entry: `rt_str_val` answers on `ST(0)`
   and the table can only name a register result; `rt_str_len` answers in a word where the IR declares
   a LONG; `rt_str_hex` needs immediate presets (`CL` = bits per digit) rather than register moves.

### Differential execution without the vintage oracle - DONE

The parity question the IR path has to answer is narrower than the one the golden battery answers.
Byte-identity with PBC 3.50 is the *direct* emitter's job and always will be - the IR path is a
different code generator and will never match those bytes. What it must match is the direct emitter's
**observable behaviour**: the same program, compiled both ways, printing the same thing. And the
direct emitter is a sound reference for exactly that, because the golden battery holds *it* to the
genuine compiler.

`PowerBasic.Compiler.Tests/Exec/Cpu8086.cs` is that executor: a real-mode 8086 interpreter over the
emitted MZ image (loader, relocations, the single-segment model the runtime documents, and the INT
21h/10h subset the runtime calls). `BackendDifferentialTests` compiles a program both ways, runs both
images, and compares the captured output. It needs no DOSBox and no vintage toolchain.

The rule that makes it worth trusting is that it **fails loudly**: an unimplemented opcode, an
unhandled DOS call or a runaway program throws, and the test is skipped rather than passed. Its x87
model keeps integral values exact through `FILD`, integral arithmetic, comparison and `FISTP`; this is
required because the real extended format has a 64-bit significand and represents every signed QUAD.
Non-integral values still use a host `double`, so the executor does not claim fidelity for differences
that depend on the extra eleven fraction bits of a real 8087 temporary.

What it already proves, on programs it can run end to end: routed and directly-emitted code agree on
integer arithmetic, a constant divide, control flow through a loop and a merge, a value spilled across
a call, a SHARED global written by one path and read by the other, and a whole module body the back
end owns.

`BackendCorpusDifferentialTests` runs the same comparison over the **whole battery**, optimized and
unoptimized. The current run has 266 compilations in which the back end participates: 256 run both
ways and agree, 10 cannot be compared because the 8086 executor lacks one required instruction or DOS
service, and 0 disagree. No participating compilation throws. The outcomes stay separate deliberately: "not
compared" is never counted as agreement, because collapsing them is how a coverage number starts
lying. Any disagreement fails the build.

Getting the last one took two fixes, both real. `L& = A2% * B2%` with both operands 32767 answered
1073676288 against the exact 1073676289: PowerBASIC's integral arithmetic is float-shaped in the IR,
and (a) `IntegerRecovery` refused a leaf narrower than the target, so a 16x16 multiply widening into a
LONG was never recovered, and (b) it ran only AFTER the optimizer, so constant folding had already
collapsed the product in an `f32` whose 24-bit mantissa cannot hold 2^30. Recovery now widens narrow
leaves and runs before the optimizer as well as after - which is what lets the folding happen in
integers, exactly as the direct emitter's 64-bit x87 temporary computes it.

Getting here needed the interpreter to be checked against the one path already known to be right
(`InterpreterSanityTests`, against the direct emitter). It failed that check at first - a subtraction
reaching the FPU came out negated - and the cause was the Intel encoding trap the manuals are famous
for: the `DC`/`DE` register forms **swap** `FSUB`/`FSUBR` and `FDIV`/`FDIVR` relative to `D8`, so
`DE /5` is `FSUBP` (ST(i) - ST(0)) while `D8 /4` is `FSUB` (ST(0) - ST(i)). Reading /5 as "reverse" in
both directions inverted the sign of every subtraction PowerBASIC's float-shaped integer arithmetic
put through the FPU. Three of the four "disagreements" the harness first reported were that bug, in
the harness rather than in either compiler - which is why the reference is checked before its verdicts
are believed.

**Superseded note.** `InterpreterSanityTests` checks the interpreter against the one path already
known to be right - the direct emitter, whose bytes the golden battery holds to PBC 3.50 - and two of
those checks fail: a subtraction of two VARIABLES comes out negated, while `PRINT x%` and every
constant-folded case are right. So the fault is in the interpreter's execution of the direct emitter's
two-operand subtract, and the three corpus disagreements it currently reports are the interpreter's,
not the back end's. The corpus fixture is therefore `[Explicit]` and the failing sanity cases carry the
diagnosis: a harness whose reference is wrong is worse than no harness. Finding that subtract is the
next step, and it is a bounded one.

It still earned its place on the first run by finding a real miscompilation that every static check had
missed: `sext i1 1 to i16` folded to **1** where BASIC's TRUE is **-1**, so every comparison the
optimizer could decide at compile time went out as 1 - on the native, C and LLVM back ends alike.

---
*Optimizations are owned by the separate optimizer instance; ABI/interop items
above are front-end/linker work for this instance unless noted.*
