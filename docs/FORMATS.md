# Artifact formats

PB-Compiler emits three artifact kinds. The `.EXE` is the standard DOS MZ
format and runs anywhere; `.PBU`/`.PBL` are **this compiler's own documented
container formats** — they serve the same role as PowerBASIC 3.5 units and
libraries ( `$COMPILE UNIT`, `$LINK` ) but are *not* binary-compatible with the
proprietary originals (see REQUIREMENTS.md W2). All multi-byte integers are
little-endian. Strings are length-prefixed (u8) ASCII unless noted.

## .EXE — DOS MZ executable

Standard MZ image: header (incl. relocation table for far segment fixups),
code segment(s), data segment, BSS via MINALLOC, stack via SS:SP. Entry code
initializes DS, the string heap and the runtime, then falls into compiled
main code. `END`/`SYSTEM` terminate via int 21h AH=4Ch.

## .PBU — compiled unit  (`$COMPILE UNIT`)

| Offset | Field |
|--------|-------|
| 0 | magic `PBU1` |
| 4 | u16 format version (currently **2**; the reader also accepts version 1) |
| 6 | u16 cpu flags (bit0: needs 80186, bit1: 80286, bit2: 80386, bit3: x87 used) |
| 8 | unit name (string) |
| | u16 export count, then per export: name (string), u8 kind (0=SUB, 1=FUNCTION), u32 signature hash, u32 code offset |
| | u16 import count, then per import: name (string), u32 signature hash |
| | u16 common count, then per block: name (string), u32 size |
| | u32 code length, code bytes |
| | u32 data length, data bytes |
| | u32 bss size |
| | u16 relocation count, then per fixup: u32 site offset, u8 type (0=near target offset, 1=data offset, 2=segment base, 3=import near call, 4=import absolute offset), u16 target (import index for types 3/4, else reserved 0) |
| | **v2:** u16 fragment count, then per fragment: function (string), i32 stable block id, u32 original code offset, u32 encoded length, u32 body length, u8 control kind, i32 primary target block id, i32 secondary target block id, u8 x86 condition nibble, u16 successor count, then i32 successor ids |
| | **v2:** u32 internal-relative count, then per record: u32 instruction offset, u8 encoded length, u8 kind (0=CALL, 1=JMP, 2=Jcc), u8 x86 condition nibble, u32 target code offset |

Version 1 ends after the ordinary relocation table. A v1 unit therefore reads
with empty fragment/internal-relative collections and links exactly as before.
The magic remains `PBU1`; compatibility is carried by the explicit version
field rather than by multiplying magic strings.

Fixup semantics (all persisted sites are 16-bit words inside the code image):

- **0 NearCode** — site holds an offset relative to the unit's code base; the
  linker adds the final code base.
- **1 DataOffset** — site holds an offset relative to the unit's data base;
  the linker adds the final code size plus the unit's data base.
- **2 Segment** — site holds a paragraph value; becomes an MZ relocation.
- **3 ImportCall** — site is the displacement of a near CALL/JMP/Jcc; the
  linker writes `target - (site + 2)`.
- **4 ImportOffset** — site holds an addend; the linker adds the import's
  final absolute offset (used for runtime data cells and CODEPTR of imports).

### PBU2 fragment metadata

PBU2 keeps the machine CFG that the compiler already knows instead of making a
post-link optimizer rediscover it by disassembling its own output. Fragment
metadata is emitted only for x86-backend-routed functions whose machine-block
boundaries are known exactly; direct-emitter, runtime and foreign regions stay
opaque and immovable.

A fragment's `body length` excludes only the layout-dependent terminal
`JMP`/`Jcc` sequence. Its control record says how to reconstruct that transfer:

- **0 Preserve** — copy the complete fragment byte-for-byte; used for terminal,
  indirect or otherwise non-reconstructable control.
- **1 Unconditional** — one successor; materialize a jump only when the target
  is not the next physical fragment.
- **2 Conditional** — two successors; `primary` is the target when the stored
  x86 condition is true and `secondary` is the other edge. The linker may
  invert the condition when that makes the primary edge the fall-through.

Stable block IDs are function-local and independent of physical address/order.
The internal-relative table retains already-resolved internal `CALL`, `JMP` and
`Jcc` sites that ordinary relocations historically discarded after assembly.
That is required when a block move changes a displacement: retained
non-terminal transfers are repatched, while reconstructed terminal transfers
are regenerated and relaxed after the new layout is known.

The *signature hash* is a FNV-1a-32 over the upper-cased canonical signature
string, letting the linker reject unit/caller mismatches that PB 3.5 only
caught at run time. The canonical format is

```
NAME(byval:type,byref:type,seg:type,...)->returntype
```

one entry per parameter in order (`byval`/`seg`/`byref` as declared), `->type`
only for FUNCTIONs. Type names are the lower-case PB scalar names (`byte`,
`word`, `dword`, `integer`, `long`, `single`, `double`, `ext`), `string`,
`string*N` for fixed strings, `flex`, `any`, the TYPE name for UDTs, and the
element type plus `()` for array parameters - e.g.
`ADDINTS(byval:integer,byval:integer)->integer`. Runtime symbols (`rt_*`)
imported by units are unchecked and hash as 0.

## .PBL — unit library

| Offset | Field |
|--------|-------|
| 0 | magic `PBL1` |
| 4 | u16 format version |
| 6 | u16 unit count, then per unit: name (string), u32 offset, u32 length |
| | concatenated `.PBU` blobs at the recorded offsets |

`$LINK "X.PBU"` links one unit; `$LINK "Y.PBL"` makes all units of the
library *available* — only units that satisfy unresolved imports are pulled
into the EXE (library semantics, like `.LIB`).

## Linking model

1. Compile main source; collect unresolved calls (DECLAREd but undefined).
   The main image is itself unit-shaped: its code blob (runtime + main +
   procedures + data, all internal references final because it always lands
   at offset 0) exports every defined SUB/FUNCTION with its signature hash
   *plus* every bound runtime label (`rt_*`, hash 0) as the runtime export
   table units resolve against.
2. Resolve each import from explicitly `$LINK`ed PBUs, then PBLs, in source
   order; library units are pulled only while they satisfy unresolved imports
   (transitively).
3. Signature hashes must match; mismatch is a compile-time error.
4. When a post-link profile is supplied, fragment-aware routed functions are
   reordered **after** the participating unit set is fixed and **before** unit
   bases/final fixups are assigned. Terminal branches are reconstructed for the
   new fall-throughs, then short/near encodings are relaxed monotonically to a
   fixpoint. Opaque code remains in place inside its unit.
5. Unit code is appended behind the main image, unit data behind all code;
   every unit base is word-aligned. Ordinary fixups are then applied against the
   rewritten offsets, segment fixups becoming MZ relocation entries.
6. The linked image exposes the final fragment address map used to attribute
   sampled instruction pointers back to stable function/block IDs.
7. Unresolved symbols after the sweep abort the compile, as does a combined
   image beyond the single-segment 64 KiB.
