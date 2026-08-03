# R0004 — Inline-assembly intrinsics

| | |
|---|---|
| **Status** | ✅ Implemented (`BSWAP`, `CMOVcc`, MMX/SSE2/AVX/AVX-512 integer SIMD); the named `! MEMCPY`/`! MEMSET`/`! ZERO` sugar is ⬜ planned |
| **Stage** | Assembler + inline-asm parser |
| **Source** | `Asm/Assembler.Simd.cs`, `Asm/Assembler.Instructions.cs`, `CodeGen/CodeGenerator.InlineAsm.cs` |
| **Gate** | `$CPU` feature flags (`80386`, `80486`, `80586 [MMX] [SSE] [AVX] [AVX512]`) |
| **Related** | [O0026](O0026-auto-vectorization.md), [O0039](O0039-inline-asm-scheduling.md), [C0002](C0002-486-codegen.md) |

## What it is

The instruction sets a hand-tuning author can reach from `!` statements:

- **`BSWAP`** (486) — endian flips for `MKx$`/`CVx` helpers and graphics masks;
- **`CMOVcc`** (686) — all 14 condition forms, `0F 40+cc /r`;
- **MMX** (64-bit `MM0..MM7`), **SSE2** (128-bit `XMM0..XMM7`), **AVX**
  (256-bit `YMM0..YMM7`, VEX-encoded) and **AVX-512** (512-bit `ZMM0..ZMM7`,
  EVEX-encoded) integer SIMD: `MOVD`/`MOVQ`, `MOVDQA`/`MOVDQU` and the VEX/EVEX
  `VMOVDQA`/`VMOVDQU`, packed `PADDB/W/D(/Q)` and `PSUBB/W/D(/Q)` with the
  saturating forms, `PMULLW`/`PMULHW`, `PAND`/`PANDN`/`POR`/`PXOR`, the packed
  compares `PCMPEQB/W/D` and `PCMPGTB/W/D`, the shifts `PSLLW/D/Q` ·
  `PSRLW/D/Q` · `PSRAW/D`, the pack/unpack family, and `EMMS`.

The **same mnemonic** picks the encoding by register class: `! PADDW MM0, MM1`
assembles the MMX form, `! PADDW XMM0, XMM1` the 66-prefixed SSE2 form. The
three-operand `V`-mnemonics take the 2-byte VEX (`C5`) prefix for XMM/YMM and
the 4-byte EVEX (`62`) prefix for ZMM — so the inline assembler scales 4 → 8 →
16 → 32 lanes by register choice.

## Sample

```basic
$CPU 80586 MMX
DIM a AS DWORD, b AS DWORD, c AS DWORD   ' two packed 16-bit lanes each
a = 1 + 2 * 65536 : b = 10 + 20 * 65536
! MOVD MM0, a
! MOVD MM1, b
! PADDW MM0, MM1                          ' lanes: 1+10, 2+20
! MOVD c, MM0
! EMMS                                    ' release the MMX/x87 state
' c now holds 11 and 22 in its two words
```

## Equivalent BASIC

```basic
loA% = 1  : hiA% = 2
loB% = 10 : hiB% = 20
loC% = loA% + loB% : hiC% = hiA% + hiB%     ' but in one instruction
```

## Why it is safe

A variable named in an inline-asm body is treated as **live** by the optimizer
(its data slot and its stores survive), so hand-written SIMD works with
`--optimize` on. MMX executes under DOSBox and is verified by execution; the
XMM/YMM/ZMM and `CMOVcc` encodings are verified by assembler unit tests against
hand-computed opcodes, since DOSBox runs neither SSE/AVX nor CMOV. Genuine PBC
3.50 has no MMX, so these are not oracle-differential features.

## What is still planned

The named sugar — `! MEMCPY dst, src, n`, `! MEMSET`, `! ZERO var` — plus named
access to the runtime's string-manager ABI (`GetStrLen` and friends, see
`docs/DIALECTS.md`). All three are expressible today as inline `REP MOVS`/
`STOS`/`STOSW` sequences; what is missing is the convenience form.
