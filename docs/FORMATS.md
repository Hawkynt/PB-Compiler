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
| | u16 relocation count, then per fixup: u32 site offset, u8 type (0=near target offset, 1=data offset, 2=segment base, 3=import near call), u16 target (import index for type 3, else reserved 0) |

The *signature hash* is a FNV-1a-32 over the canonical signature string
(`name(byval:type,byref:type,...)->type`), letting the linker reject
unit/caller mismatches that PB 3.5 only caught at run time.

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
2. Resolve each from explicitly `$LINK`ed PBUs, then PBLs, in source order.
3. Signature hashes must match; mismatch is a compile-time error.
4. Pulled-in unit code/data is appended to the image; fixups are applied
   (near offsets relative to final layout, segment fixups become MZ
   relocation entries).
5. Unresolved symbols after the sweep abort the compile.
