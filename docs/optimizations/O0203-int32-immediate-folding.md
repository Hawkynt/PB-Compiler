# O0203 — 32-bit immediate operand folding

| | |
|---|---|
| **Status** | 🟡 Partial — immediate pair operations and compares; no `OR`-of-halves zero test |
| **Stage** | x86 back end (instruction selection) |
| **Source** | `Backend/InstructionSelector.cs` — `SelectWideBinary`, `SelectWideCmpValue`, `TryOperandPair` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF47.BAS` |
| **Split from** | [O0008](O0008-peephole-zero-idiom.md) |

## What it is

The LONG/DWORD path folds a constant the same way as the 16-bit one, into
immediate **pair** operations:

- `AND`/`OR`/`XOR` on the low word in AX and the high word in DX;
- `ADD AX,imm` + `ADC DX,imm` (and `SUB`/`SBB`);
- comparisons as a high-word `CMP` against the constant's high half, then a
  low-word `CMP` against its low half.

`TryOperandPair` splits the constant into its two immediate halves. The older
zero test that ORs the two halves (`ptr& = 0`, shown below) is not produced on
the IR path; a compare against `0` goes through the same two-`CMP` sequence.

## Sample

```basic
DIM p&, t&
t& = p& + 100
IF p& = 0 THEN PRINT "null"
```

## Without / with

```asm
    ; without: load the constant into CX:BX, push/pop the pair, then add
    mov     ax, [p]
    mov     dx, [p+2]
    add     ax, 0064h        ; with
    adc     dx, 0000h
    ...
    mov     ax, [p]          ; the zero test
    or      ax, [p+2]
    jnz     NotNull
```

## Why it is safe

Each half is combined with the corresponding half of the constant, and the carry
chain (`ADC`/`SBB`) reproduces the 32-bit arithmetic exactly. The
`$ERROR OVERFLOW` trap is an explicit sign test added by the IR lowering, so it
does not depend on the flags of the high-half operation.
