# C0001 — `$CPU 80386` code generation

| | |
|---|---|
| **Status** | ✅ Implemented for everything that pays without register residency; the EAX-representation change is [O0058](O0058-386-register-allocation.md) |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.*` (`EmitQuad386Bitwise`, `EmitShiftRotate`, the widened block moves) |
| **Gate** | `$CPU 80386` / `-G386` |
| **Verified by** | `tests/diff/DIFF70/71/72/73/74/75.BAS` (byte-identical to genuine PBC 3.50, which accepts `$CPU 80386`) |
| **Related** | [C0002](C0002-486-codegen.md), [O0015](O0015-udt-zero-cost.md), [R0003](R0003-string-engine.md), [O0058](O0058-386-register-allocation.md) |
| **Split into** | [O0233](O0233-hardware-constant-divide.md), [O0234](O0234-quad-bitwise-inline.md), [O0235](O0235-shld-shrd-shifts.md), [O0236](O0236-long-shift-rotate-collapse.md), [O0237](O0237-movzx-movsx-loads.md), [O0238](O0238-setcc-relationals.md), [O0239](O0239-stosd-array-zero.md), [O0240](O0240-stosd-loop-fill.md) |

## What it is

Real mode on a 386+ executes 32-bit operations through operand/address-size
prefixes — no protected mode required. `$CPU 80386` is the **gate**: it is the
programmer's declaration that the target has those instructions, and it is what
each of the individual 386 lowerings tests before it fires.

Those lowerings each have their own entry (see *Split into* above): hardware
constant divide, inline 64-bit bitwise, `SHLD`/`SHRD`, the 32-bit shift/rotate
collapse, `MOVZX`/`MOVSX` loads, `SETcc` relationals, and the `REP STOSD`/
`MOVSD` widenings.

## Sample

```basic
$CPU 80386
DIM n&, q&
q& = n& \ 10
ERASE big%()
```

## Without the gate

```asm
    mov     ax, [n]
    mov     dx, [n+2]
    mov     bx, 000Ah
    xor     cx, cx
    call    rt_ldiv          ; a runtime long-division routine
```

## With the gate

```asm
    mov     eax, [n]
    cdq
    mov     ecx, 0000000Ah
    idiv    ecx              ; the hardware does it
    mov     [q], eax
```

## Equivalent BASIC

Unchanged — same values, fewer instructions.

## Why it is safe

- The `|divisor| ≥ 2` gate rules out the divide-by-zero and `MININT \ -1` traps,
  so the runtime path is dropped only where it cannot trap; the hardware's
  truncate-toward-zero quotient and dividend-signed remainder are exactly PB's
  `\` and `MOD`.
- Bitwise 64-bit operations cannot trap, so the inline form is unconditional.
- Shift counts ≥ 32 and runtime counts stay on the loop, because the 386 masks
  the count to 5 bits.
- QUAD *arithmetic* (add/sub/mul) deliberately stays on the x87 — it matches
  PBC's lossy behavior beyond 2⁵³, and fidelity outranks speed.

## What is still planned

LONG add/sub stay the 2-op `ADD`/`ADC` pair (an EAX form needs four scratch
moves without residency — slower), and scaled-`LEA` addressing needs the 32-bit
ModRM/SIB encoder. Both belong to the 386 register substrate:
[O0058](O0058-386-register-allocation.md) and
[O0064](O0064-lea-fusion.md).
