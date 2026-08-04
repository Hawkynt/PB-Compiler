# Roadmap — missing features from the oracle compilers

Prioritised backlog (IREPB MoSCoW) of capabilities the genuine oracle compilers
(the BASIC family **and** the staged C compilers — see `docs/LINKER.md`,
`docs/BASIC-FAMILY.md`) demonstrate that `pbc` does not yet implement. Grounded
in the codegen `Unsupported(...)` surface and the `OmfException` rejections.

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
produce what the direct emitter produces. Measured by `BackendCoverageTests` over the 162-program
battery, it currently reaches:

| | |
|---|---|
| programs reaching the IR at all | 132 / 162 |
| functions selected | 120 / 218 |
| functions routed (selected **and** allocated) | 79 / 218 |
| whole module bodies the back end can own | 28 / 132 |

The runtime traps and the error handler are **done**. `$ERROR BOUNDS / OVERFLOW / NUMERIC ON` now emit
their checks rather than merely accepting the metastatement, over dynamic arrays as well as static
ones; `ON ERROR` / `RESUME` / `ERROR n` / `ERRCLEAR` lower, along with the `ERR` / `ERL` / `ERADR`
cells a handler reads. See [IR.md](IR.md) for how a construct whose control flow no CFG can express is
kept sound - `IrBlockAddress` names the handler and `IrFunction.HasErrorHandler` takes the whole
function out of the optimizer, the same trade the direct emitter makes with `_trackResume`.

Ranked by the census, what stands between that and full coverage:

1. **The routed path cannot yet EMIT the traps or the handler.** The lowering builds them, but the
   selector declines `rt_onerr_arm` / `rt_resume_mark` and the rest, because arming captures the
   current `BP`/`SP` and so has to be expanded inline rather than called - and a block address has to
   reach the emitter as a label offset. Until then these programs reach the IR but stay on the direct
   path, which is safe (an unknown `rt_` call declines) but is not yet parity.
2. A tail of statements: `ArraySortStmt`, `PUT$`, `DIM AT`, `ERASE` of a static array, `HEX$` with a
   digit count, `PRINT USING` / `LPRINT`, `CODEPTR32`, and the `$COMPILE` / `$IF` / `$LINK` / `$STRING`
   metastatements.
3. **41 functions that select but fail allocation** - each needs a memory operand in a position the
   emitter has no form for; `Spiller` names the position it could not move.
4. The largest selection declines are runtime routines with no entry in the ABI table
   (`rt_fprint_strvar`, `rt_str_val`, `rt_fprint_i64`, ...), a 32-bit compare used as a value, and a
   float phi with no frame cell.

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
unhandled DOS call or a runaway program throws, and the test is skipped rather than passed. x87
arithmetic is deliberately not interpreted - only the control instructions the entry stub runs - since
an approximate 80-bit stack would let a float test pass while disagreeing with the hardware, which is
the one outcome an execution oracle must never produce.

What it already proves, on programs it can run end to end: routed and directly-emitted code agree on
integer arithmetic, a constant divide, control flow through a loop and a merge, a value spilled across
a call, a SHARED global written by one path and read by the other, and a whole module body the back
end owns.

`BackendCorpusDifferentialTests` runs the same comparison over the **whole battery**: of the 24
programs the back end compiles part of, 12 run both ways and agree, 12 are not compared (the
interpreter has no x87 arithmetic), and none disagree. The three outcomes are kept apart deliberately -
"not compared" is never counted as agreement, because collapsing them is how a coverage number starts
lying.

**It is a gate, and it is clean.** Of the 24 programs the back end compiles part of, **all 24 run
both ways and behave identically** - none uncompared, none disagreeing. The known-defect list is
empty; an entry in it would be a diagnosed bug, and a new disagreement fails the build outright.

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

### Still not covered by execution

None of the routed output has ever been **executed**. The differential oracle needs DOSBox
(`tools/dosbox`, or `DOSBOX_EXE`) and the vintage toolchains; without them every correctness claim
about the back end rests on matching the direct emitter's documented register conventions and on
static invariants (selection declines, allocation, images that assemble and link). Coverage added
without that is a larger body of unexecuted code, not parity - so an execution oracle should come
before, or alongside, the items above rather than after them.

---
*Optimizations are owned by the separate optimizer instance; ABI/interop items
above are front-end/linker work for this instance unless noted.*
