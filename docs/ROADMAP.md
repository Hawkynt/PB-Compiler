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

---
*Optimizations are owned by the separate optimizer instance; ABI/interop items
above are front-end/linker work for this instance unless noted.*
