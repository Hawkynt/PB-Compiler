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
- **C-runtime linking (M3).** Leaf `strlen` works; full CRT (objects calling
  `printf`/`malloc`) needs the C startup object (`c0*.obj`) and `_DATA`/`DGROUP`
  initialisation. *Touch points:* `Linker` startup handling, DGROUP layout.
- **Watcom CRT via `WATCALL` declarations** — now feasible since `WATCALL` exists
  (its CRT is register-convention; `_strlen`-style cdecl calls mismatch).

### Could
- **Emit OMF `.OBJ` / `.LIB`.** *Done.* `OmfWriter` (`Emit/Omf/OmfWriter.cs`) emits a
  `PbuFile` as a 16-bit OMF object — THEADR/LNAMES/SEGDEF/PUBDEF/EXTDEF, chunked
  LEDATA (≤1024 B) and FIXUPP for every `PbuFixup` kind, MODEND. `OmfLibraryWriter`
  archives several such objects into a `.LIB` (0xF0 header, page-aligned members, 0xF1
  trailer, hash-dictionary blocks). Both round-trip through our own
  `OmfReader`/`OmfToPbu`/`OmfLibrary` (incl. multi-LEDATA segments and selective
  extraction) and genuine MS `LINK.EXE` consumes an emitted object (`OmfTests`,
  `OmfLibraryWriterTests`, `LinkOracleTests`). The CLI exposes both: `pbc --emit-obj`
  writes a linkable `.OBJ` instead of an `.EXE`, and `pbc lib build out.LIB ...`
  writes an OMF archive (`EmitObjTests`, `LibBuildTests`). *Still Could:* make the
  emitted `.LIB` dictionary hash genuine-MS-LINK-compatible (today the archive is
  consumed by our own linker; the object form is LINK-validated).
- **Per-convention auto name-decoration** (stdcall `_name@N`, fastcall `@name`,
  watcall `name_`, pascal upper) instead of requiring `ALIAS`.

## B. BASIC language features codegen still rejects

Straight from the `Unsupported(...)` survey.

### Should
- ~~`ON ERROR RESUME NEXT`~~ - done (inline mode; byte-identical vs PBC 3.50, battery `DIFF84`).
- ~~`ARRAY SORT`/`ARRAY SCAN` on non-string arrays, and `TAGARRAY`~~ - done: numeric arrays of every element kind (signed/unsigned BYTE/WORD/DWORD/INTEGER/LONG/QUAD, SINGLE/DOUBLE/EXT) sort and scan byte-identical vs PBC 3.50 (battery `DIFF86`), including ASCEND/DESCEND, all six SCAN relops, and `ARRAY SORT ... TAGARRAY`. The comparison runs on the x87 (elements widened into a staging cell), so unsigned values past their signed range still order by true value. FROM/TO ranges and COLLATE stay string-only (rejected for numeric arrays, as in genuine PBC).
- ~~Finish `QUAD` (64-bit integer) operators~~ - done: every `QUAD` operator (`+ - * \ MOD AND OR XOR EQV IMP`, comparisons, unary `- NOT`, and the float-typed `/` and `^`) is byte-identical vs PBC 3.50 (battery `DIFF85`). The remaining `Unsupported` arm in `EmitInt64Op` is an unreachable guard: the binder types `QUAD /` as DOUBLE and `QUAD ^` as EXT, so both run on the float path, never reaching the integral op switch.

### Could
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
