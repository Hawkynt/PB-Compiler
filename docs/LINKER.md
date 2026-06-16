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
- Producing OMF output. We emit DOS MZ images directly; OMF is an *input* format.

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
  relocating `_TEXT`/`_DATA`/`CONST`/`BSS` into it. Compact/large/huge objects use
  far pointers and multiple segments — supported only if we grow the writer to emit
  extra segments, or rejected with a clear diagnostic. **M1 targets small/tiny.**

- **cdecl (C).** Caller pushes args right-to-left and cleans the stack; result in
  `AX`/`DX:AX`; publics are decorated with a leading underscore (`_Foo`). We already
  emit the CDECL convention and support `ALIAS`, so a `DECLARE ... CDECL ALIAS "_Foo"`
  call needs no codegen change — just symbol resolution. **Caveat:** a C library
  routine that calls `malloc`/`printf`/… drags in the **C runtime**, so its CRT
  `.LIB` (or a no-CRT/freestanding build) must be linked too; the CRT also wants its
  startup/`DGROUP` init. M1 scope = self-contained, no-CRT objects (hand-asm or
  freestanding C); CRT support is M3.

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

## Language surface (mostly already present)

- `$LINK "name.OBJ" | "name.LIB"` — extend the existing `$LINK` (today PBU/PBL) to
  sniff the OMF signature and route to `OmfReader`.
- `DECLARE SUB|FUNCTION Name [CDECL] [ALIAS "public"] (params...) [AS type]` — already
  parsed. `CDECL` selects the C convention; absence selects the BASIC (BYREF) one;
  `ALIAS` gives the exact (decorated) public name; `BYVAL`/`BYREF`/`SEG` per param.
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

- **M1 — OMF object reader + tiny/small cdecl.** Parse `.OBJ` (incl. `LIDATA`
  expansion and the core `FIXUPP` flavors), relocate one self-contained object into
  our segment, resolve a `DECLARE ... CDECL ALIAS` call. Oracle: link the same
  object with genuine `LINK.EXE` (already in the qb45/PDS containers) and diff.
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
- Tiny-model assumption: far calls/data in a foreign object need either multi-segment
  output or a hard diagnostic — decide per object via its `SEGDEF`/`GRPDEF`.
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
