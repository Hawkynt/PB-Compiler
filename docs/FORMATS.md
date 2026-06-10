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
| 4 | u16 format version (currently 1) |
| 6 | u16 cpu flags (bit0: needs 80186, bit1: 80286, bit2: 80386, bit3: x87 used) |
| 8 | unit name (string) |
| | u16 export count, then per export: name (string), u8 kind (0=SUB, 1=FUNCTION), u32 signature hash, u32 code offset |
| | u16 import count, then per import: name (string), u32 signature hash |
| | u16 common count, then per block: name (string), u32 size |
| | u32 code length, code bytes |
| | u32 data length, data bytes |
| | u32 bss size |
| | u16 relocation count, then per fixup: u32 site offset, u8 type (0=near target offset, 1=data offset, 2=segment base, 3=import near call, 4=import absolute offset), u16 target (import index for types 3/4, else reserved 0) |

Fixup semantics (all sites are 16-bit words inside the code image):

- **0 NearCode** — site holds an offset relative to the unit's code base; the
  linker adds the final code base.
- **1 DataOffset** — site holds an offset relative to the unit's data base;
  the linker adds the final code size plus the unit's data base.
- **2 Segment** — site holds a paragraph value; becomes an MZ relocation.
- **3 ImportCall** — site is the displacement of a near CALL/JMP/Jcc; the
  linker writes `target - (site + 2)`.
- **4 ImportOffset** — site holds an addend; the linker adds the import's
  final absolute offset (used for runtime data cells and CODEPTR of imports).

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
4. Pulled-in unit code is appended behind the main image, unit data behind
   all code; every block is word-aligned. Fixups are applied (near offsets
   relative to final layout, segment fixups become MZ relocation entries).
5. Unresolved symbols after the sweep abort the compile, as does a combined
   image beyond the single-segment 64 KiB.
