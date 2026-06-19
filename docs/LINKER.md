# External object/library linking (OMF) — design scope

## Goal

Let a compiled program call into **third-party object code** distributed as the
DOS-standard **Intel OMF** `.OBJ` files and `.LIB` archives — the format produced
by every period C compiler (Microsoft C, Borland C, Watcom), by MASM/TASM, and by
the BASIC compilers (QuickBASIC, PowerBASIC, BASIC PDS). Concretely:

- `$LINK "GRAPHX.LIB"` / `$LINK "MOUSE.OBJ"` pulls external modules into the image;
- `DECLARE FUNCTION Foo CDECL ALIAS "_Foo" (BYVAL x AS LONG) AS LONG` binds a BASIC
  call site to an external public symbol, with the calling convention spelled out.

This is the bridge to **3rd-party libraries**: C SDKs, hand-written asm, and
routines compiled against the QB/PB runtimes.

### Non-goals
- Optimizing *through* linked binary code. A linked module is opaque machine code,
  not IR — the optimizer cannot run SSA across it. The realistic link-time win is
  **dead-module elimination** (only pull library modules whose publics are actually
  referenced), which is selective extraction, not optimization of foreign code.
- Emitting a full program as OMF for re-linking by a foreign toolchain end to end.
  A finished *program* is emitted as a DOS MZ image directly. (OMF *objects* can now
  be produced — `OmfWriter` lowers a `PbuFile` to a `.OBJ` that genuine `LINK.EXE`
  consumes — but wiring a "compile to object, not executable" CLI mode and the C
  startup/DGROUP contract for a foreign linker to build the final image is future
  work.)

## Where we are today

`Emit/Linker.cs` already is a small linker, but only for **our own** unit format
(`PbuFile`/`PblFile`): it resolves `PbuImport`↔`PbuExport` by name+signature across
units and applies `PbuFixup`s (`ImportCall` = 16-bit near call, `ImportOffset` =
absolute offset) while concatenating each unit's code/data into the single real-mode
segment that `MzExeWriter` emits. There is **no OMF reader** — so no C/QB/PB `.LIB`
can be linked. The DECLARE surface we need mostly exists already: `CDECL`, `ALIAS`,
`BYVAL`/`BYREF`, and `SEG`/`ANY` are all parsed and honored for *our* calls.

## OMF in one screen

An OMF object module is a record stream; the records the linker must understand:

| Record | Purpose |
|--------|---------|
| `THEADR`/`LHEADR` | module name |
| `COMENT` | comments — **incl. the default-library directive** (link a runtime automatically) and the WKEXT/IMPDEF/LNAMES-style extensions |
| `LNAMES` | name pool (segment/class/group names) |
| `SEGDEF` | a segment: name, class, size, alignment, combine attribute |
| `GRPDEF` | a group of segments addressed off one base (e.g. `DGROUP`) |
| `PUBDEF` | **public** symbols this module exports (name → segment:offset) |
| `EXTDEF` | **external** symbols this module imports (resolved from other modules/libs) |
| `LEDATA`/`LIDATA` | the actual bytes (`LIDATA` = run-length *iterated* data — must be expanded) |
| `FIXUPP` | relocations: patch a location using a `FRAME` (seg/group/target) and a `TARGET` (segment/extdef + displacement), in `SEG`/`OFFSET`/`SEG:OFFSET`/`SELF-relative` flavors |
| `MODEND` | end of module, optional program entry point |

A `.LIB` is a concatenation of OMF modules plus a **dictionary** (hashed
`symbol → module offset` blocks) so the linker can pull *only* the modules that
satisfy unresolved externals.

## Architecture

```
.OBJ / .LIB  ──▶  OmfReader ──▶ OmfModule {segments, groups, publics, externals,
                                            data blocks, fixups}
                                      │
$LINK targets ─────────────────────▶ Linker (extended)
PBU/PBL units ─────────────────────▶  • build the public-symbol table (ours + OMF)
                                      • selective .LIB extraction (dictionary)
                                      • lay OMF segments into our segment/groups
                                      • resolve EXTDEF ↔ PUBDEF (+ DECLARE aliases)
                                      • apply OMF FIXUPP and our PbuFixup
                                      │
                                      ▼
                               MzExeWriter (unchanged)
```

`OmfReader` and an `OmfModule` model are new (`Emit/Omf/`). `Linker` gains a second
front end (OMF modules alongside PBU units) but keeps one back end: everything is
relocated into the image `MzExeWriter` already produces. A BASIC call site that the
binder resolved to an external OMF public emits the same call shape we already use
for `DECLARE`d externals; only the *resolution* and *relocation* are new.

## The hard part: memory model & ABI

Our target image is effectively **tiny**: one segment with `CS = DS = SS`. That
constrains what foreign code can be linked and is the crux of the whole feature.

- **Memory model.** Only objects built for a compatible model link cleanly. Tiny/
  small C (near code, near data in `DGROUP`) maps onto our single segment by
  relocating `_TEXT`/`_DATA`/`CONST`/`BSS` into it. Compact/large objects that use
  **far pointers** now link too **as long as the whole image fits 64 KiB**: since
  everything lives in one combined segment loaded at a single paragraph, a far
  reference's segment is simply that load segment, so `OmfToPbu` lowers a `Base16`
  (segment word) to an MZ relocation and a `Pointer32` (seg:off) to an offset half
  plus that relocation — fixup sites in a data segment included (`PbuFixup.InData`).
  An image that genuinely needs **more than 64 KiB / multiple paragraphs** is still
  rejected by the linker's size check (real per-segment layout is future work).

- **cdecl (C).** Caller pushes args right-to-left and cleans the stack; result in
  `AX`/`DX:AX`; publics are decorated with a leading underscore (`_Foo`). We already
  emit the CDECL convention and support `ALIAS`, so a `DECLARE ... CDECL ALIAS "_Foo"`
  call needs no codegen change — just symbol resolution. **Caveat:** a C library
  routine that calls `malloc`/`printf`/… drags in the **C runtime**, so its CRT
  `.LIB` (or a no-CRT/freestanding build) must be linked too; the CRT also wants its
  startup/`DGROUP` init. M1 scope = self-contained, no-CRT objects (hand-asm or
  freestanding C); CRT support is M3.

- **stdcall / pascal.** Two callee-cleans conventions used by Win16/OS2 and Pascal
  toolchains. **stdcall** pushes args right-to-left like cdecl but the *callee*
  cleans the stack (its `RET n`), so the caller emits no `add sp`. **pascal** pushes
  left-to-right and the callee cleans. Both are emitted: `DECLARE ... STDCALL|PASCAL
  ALIAS "..."` gets the argument order and stack discipline right. Name decoration
  differs (stdcall `_name@N`, pascal often uppercase) but is left to `ALIAS` plus the
  linker's underscore fallback for now. (stdcall is a Win32 convention — no 16-bit DOS
  C compiler we ship emits it, so it is supported but not validated against a real object.)

- **fastcall / watcall (register conventions).** Args go in registers, not (only) on
  the stack — matched empirically to the genuine compilers:
  - **fastcall** (Microsoft `_fastcall` / Borland `__fastcall`): first three word args
    in **AX, DX, BX**, overflow on the stack left-to-right, callee cleans (`RET n` over
    the overflow only), public `@name`.
  - **watcall** (Watcom's default register convention): first four word args in
    **AX, DX, BX, CX**, overflow right-to-left, callee cleans the overflow, public
    `name_` (trailing underscore).

  Both are emitted for **calling and defining**: the call site evaluates the leading
  args and loads the registers (pushing then popping so they survive arg evaluation);
  a defined `SUB/FUNCTION WATCALL|FASTCALL` spills the incoming AX,DX,BX(,CX) into its
  parameter slots in the prologue and `RET n`s the overflow. Scope is the common
  16-bit case: every register-passed parameter must be a single word (BYVAL ≤ 2 bytes
  or a BYREF near pointer); LONG/float/UDT/string in a register slot needs the full
  per-compiler size rules and is rejected with a diagnostic rather than miscompiled.
  `CInteropTests` calls genuine watcall/fastcall/pascal objects; `CallingConventionTests`
  round-trips define+call (incl. stack overflow) for watcall and fastcall.

- **PowerBASIC.** A 3rd-party PB `.LIB` (genuine PBC output) uses PB's BYREF
  convention and PB's string/runtime entry points. We already *emulate* PB's runtime
  (string manager, error model), so this is the **most compatible** foreign ABI —
  the work is matching PBC's runtime symbol names/entry contract so its objects find
  our equivalents. M4.

- **QuickBASIC / PDS.** QB/PDS objects assume their own runtime (BRUNxx/BCOMxx: far
  string descriptors, the BASIC heap, error/event handlers). Calling one generally
  means **hosting that runtime**, which is a large dependency. Scope to a *subset*
  first — routines that take/return only numerics and don't touch the BASIC runtime
  — and treat full QB-runtime hosting as out of M1–M4. M5+.

### C++ mangled symbols

A C **`extern "C"`** function (or a C file compiled by a C compiler) exports an
*undecorated* public — just the cdecl `_name`. A function compiled **as C++**,
however, has its public **name-mangled** to encode the argument signature (so the
linker can tell overloads apart). The mangling is per-compiler:

| Compiler | `int square(int)` mangles to | scheme |
|----------|------------------------------|--------|
| Borland / Turbo C++ | `@square$qi` | `@`name`$q`‹arg type codes› |
| Watcom C++ | `square_$n(i)i` | name`$n(`‹args›`)`ret |
| MSVC | `?square@@YAHH@Z` | `?`name`@@`‹conv›‹ret›‹args›`@Z` |

The Borland codes above are **real** — harvested from genuine BCC 3.1 output
(`BCC -c -ms -P`): `i`=int, `l`=long, `d`=double, `v`=void(empty list), `c`=char
(`zc`=signed char), prefix `u`=unsigned / `z`=signed / `p`=pointer. So
`@many$qzciuil` is `many(signed char, int, unsigned int, long)`.

**The key fact for linking:** a C++ *free* function still uses the **cdecl argument
convention** — only the **name** is mangled. So no new codegen is needed to call one;
you just `ALIAS` the exact mangled public:

```basic
DECLARE FUNCTION square CDECL ALIAS "@square$qi" (BYVAL x AS INTEGER) AS INTEGER
PRINT square(5)   '-> 25, resolved against the C++ object's @square$qi public
```

Because the linker resolves foreign publics **case-sensitively** (and the mangled
string is just an ordinary symbol), `ALIAS "@square$qi"` resolves with no special
handling. `CInteropTests.Link_GivenCppFunctionCompiledAsCpp_…` proves this
end-to-end: it compiles `int square(int)` with `BCC -P` (forcing C++), confirms the
object exports `@square$qi`, links it behind a BASIC `square(5)`, and the program
prints `25` under DOSBox.

**Demangler (`Emit/Omf/Demangle.cs`).** A `static Demangle.Parse(symbol)` turns a
mangled public back into a legible `name(types)` and reports the scheme; `extern "C"`
/ plain publics come back as `MangleScheme.None`. It decodes Borland (verified),
MSVC, and Watcom free-function signatures. Its purpose is **diagnostic**: when the
linker hits an unresolved external that *looks* mangled, the `LinkException` appends
the demangled form, e.g.

```
unresolved symbol @square$qi (C++ Borland symbol for square(int)) (imported by MAIN)
```

so the user can see exactly what to put in the `ALIAS`. (Watcom C++ `wpp` is not
staged in the toolchains — only the 16-bit C compiler `wcc` — so the Watcom scheme is
decoded structurally but not validated against a genuine Watcom C++ object.)

## Language surface (mostly already present)

- `$LINK "name.OBJ" | "name.LIB"` — extend the existing `$LINK` (today PBU/PBL) to
  sniff the OMF signature and route to `OmfReader`.
- `DECLARE SUB|FUNCTION Name [CDECL|STDCALL|PASCAL] [ALIAS "public"] (params...) [AS type]` —
  already parsed. `CDECL`/`STDCALL`/`PASCAL` select the foreign convention; absence
  selects the BASIC (BYREF) one; `ALIAS` gives the exact (decorated) public name;
  `BYVAL`/`BYREF`/`SEG` per param.
- New diagnostics: unresolved external, duplicate public, unsupported memory
  model/fixup, ABI mismatch.

## Optimizer interaction

The SSA/SCCP/DSE/inlining chain runs on the bound model of **our** source and stays
fully active regardless of what is linked. A call into a linked module is an
**opaque external call** — exactly how `DECLARE`d externals are already treated
(arguments escape, no inlining or cross-call propagation into it). The only
link-time transform on foreign code is **dead-module stripping** via the `.LIB`
dictionary (don't emit modules nothing references), mirroring the P1 runtime trimmer
we already apply to our own sections.

## Milestones

- **M1 — OMF object reader + tiny/small cdecl. ✅ Implemented.** `Emit/Omf/OmfReader`
  parses `.OBJ` (THEADR/LNAMES/SEGDEF/PUBDEF/EXTDEF/LEDATA/LIDATA/FIXUPP/MODEND) and
  `.LIB` (page-walked members); `OmfToPbu` lowers a module to a synthetic unit so the
  existing `Linker` lays it out and resolves it. `$LINK "x.OBJ"`/`"x.LIB"` is wired in
  the driver; `DECLARE ... CDECL ALIAS "_sym"` names the external public (the codegen
  external label now uses the alias). End-to-end test: a BASIC program links a leaf
  cdecl object and calls it under DOSBox (`addone(41)` → `42`). Far (`Base16`/
  `Pointer32`) and data-segment fixups are now lowered into the single 64 KiB segment
  (a far reference's segment becomes the load segment); only genuinely >64 KiB
  multi-segment objects remain unsupported.
- **M2 — `.LIB` archives.** Dictionary parse + selective extraction + dead-module
  stripping; multi-module resolution.
- **M3 — C runtime.** Link a CRT `.LIB`, startup/`DGROUP` init, far-data handling as
  needed; enable real C SDK routines.
- **M4 — PowerBASIC objects.** Map PBC's runtime contract onto ours (most compatible
  ABI); BYREF/string-handle bridging.
- **M5 — QuickBASIC/PDS subset.** Numeric, runtime-free routines; assess the cost of
  hosting the BASIC runtime for the general case.

## Risks

- `FIXUPP` has many frame/target/location combinations; getting `SELF`-relative and
  group-relative fixups right is fiddly. Start from the subset MS C/MASM emit for
  small model.
- `LIDATA` (iterated/repeated data) must be expanded correctly.
- Tiny-model assumption: far data/pointers in a foreign object are hosted in the single
  combined segment **while it fits 64 KiB** (a far reference's segment is the load
  segment); a larger image still needs real multi-segment output and hits the size
  diagnostic instead.
- CRT and BASIC-runtime dependencies are transitive; a single `printf` can pull in a
  large dependency graph.
- Name decoration differs by convention (cdecl `_name`, pascal/stdcall uppercase) —
  `ALIAS` lets the program state the exact public when inference is ambiguous.

## Testing

We hold genuine `LINK.EXE` (in the qb45 and PDS containers) and `MASM`/`BC`/C
toolchains can produce known objects. The differential harness pattern applies: for
a fixture object, link it with both genuine `LINK.EXE` and our linker, run both EXEs
under DOSBox, and byte-compare `RESULT.TXT` — the same oracle discipline used for the
compiler dialects.

### Differential link oracle (`LinkOracleTests`)

`PowerBasic.Compiler.Tests/CodeGen/LinkOracleTests.cs` implements that oracle for the
leaf `_addone` object. A raw image byte-diff is meaningless (our linker links a BASIC
main; MS LINK links an asm main), so equivalence is proven **behaviourally** — both
sides link the *same* hand-built OMF bytes and must write the identical `RESULT.TXT`:

- **reference:** a hand-built asm `MAIN.OBJ` (calls `_addone(41)`, formats the LONG
  exactly as PB's `PRINT #` would — `" 42 \r\n"` — and writes `RESULT.TXT` via INT 21h)
  plus `ADDONE.OBJ`, linked by genuine `tools/qb45/LINK.EXE` under DOSBox into `REF.EXE`;
- **ours:** a BASIC main that `DECLARE`s `addone CDECL ALIAS "_addone"` and
  `PRINT #1, addone(41)`, compiled with the same `ADDONE` object as a linked unit.

The test decrypts `LINK.EXE` from `tools/qb45-toolchain.tar.enc` on demand (key via
`PB_TOOLCHAIN_KEY` or a local `pbkey`) and `Assume`-skips when the key, `LINK.EXE`,
`openssl`/`tar` or DOSBox is unavailable, so key-less CI still passes.

### Cross-compiler OMF interop (`CInteropTests`)

`PowerBasic.Compiler.Tests/CodeGen/CInteropTests.cs` proves our object reader + linker
consume **real foreign objects** from four different vintage DOS C compilers, each
emitting a subtly different flavour of Intel OMF:

| Slot | Compiler | OMF traits | leaf compile |
|------|----------|------------|--------------|
| `bcc31` | Borland C++ 3.1 | THEADR + COMENT(`TC86`) + DGROUP, cdecl `_name` | `BCC -c -ms` |
| `tc20`  | Turbo C 2.0     | THEADR + COMENT(`TC86`), cdecl `_name` | `TCC -c -ms` |
| `wc10`  | Watcom C/C++ 10.0a | THEADR + COMENT(`WAT` 0x9B) + phantom `_small_code_` EXTDEF | `wcc -ms -0 -s` (source spells `__cdecl`) |
| `msc6`  | Microsoft C 6.0 | THEADR + COMENT(`MS C`) + `SLIBCE` default-lib COMENT | `CL /c /AS /Gs` |

Each genuine compiler (decrypted on demand from `tools/<slot>-toolchain.tar.enc`)
compiles the same leaf `int addone(int x){ return x+1; }` to an `.OBJ` under DOSBox; we
read it with `OmfReader`, lower it with `OmfToPbu`, and link it behind a BASIC main that
`PRINT #`s `addone(41)`, requiring the run to write `42`. Notes:

- **cdecl is forced per compiler** so the public matches the BASIC `ALIAS "_addone"`:
  Borland/Turbo C/MS C are cdecl by default; Watcom 10.0a predates OpenWatcom's `-ecc`,
  so its source spells `__cdecl` explicitly (its default is the register `watcall`).
- **stack-probe externals are suppressed** (`-s` for Watcom's `__STK`, `/Gs` for MS C's
  `__aNchkstk`) — those are real referenced externals we have no runtime for.
- **`OmfToPbu` only imports externals a FIXUPP references.** Compilers emit phantom
  memory-model marker EXTDEFs (e.g. Watcom's `_small_code_`) that no relocation targets;
  importing them would manufacture an unsatisfiable dependency.
- MS C **6.0** is staged, not 7.0: 7.0's `CL.EXE` is a DOSX32 image needing a 32-bit DPMI
  host DOSBox does not provide; 6.0 is pure real mode and emits the same MS OMF dialect.

Like the link oracle, these `Assume`-skip without the toolchain key / `openssl` / DOSBox.

### Linking + trimming a real C runtime (`OmfLibrary`)

A real `.LIB` holds hundreds of members, most using OMF features the tiny single-segment
model cannot host — eagerly lowering all of them (the `$LINK` `PblFile` path) would choke
on the first unused incompatible member. `PowerBasic.Compiler/Emit/Omf/OmfLibrary.cs`
instead presents the library for **lazy, dictionary-driven selective extraction**: the
`Linker` (`AddOmfLibrary`) asks it for a symbol only when an import is unresolved, and it
lowers *just* the one member that defines it (cached, at most once), driven by the library
hash dictionary. So a 200-member runtime contributes only the handful of objects actually
referenced — the rest are never even converted. That is both the "trim" and the robustness
fix. `EmitExecutable(units, libraries, omfLibraries)` threads them through; a needed member
that still can't be lowered surfaces as a `link:` diagnostic rather than crashing.

`CInteropTests` exercises this two ways: end-to-end (Borland/Turbo C/MS C compile a
`strlen`-calling object, we link their genuine `CS.LIB`/`SLIBCR.LIB` pulling **≤3 of 20+**
members and the program prints `5`), and at the library level across **all four** runtimes
incl. Watcom — extracting one symbol lowers exactly one member. Watcom's CRT is its
register `watcall` convention, so we parse + trim its lib but do not *call* it via cdecl.
