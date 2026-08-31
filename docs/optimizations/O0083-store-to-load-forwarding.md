# O0083 — Statement-level store-to-load forwarding

| | |
|---|---|
| **Status** | ✅ Done — frame slots and ordinary unprefixed direct-variable cells forward through the assembler's recorded stream |
| **Stage** | Assembler |
| **Related** | [O0034](O0034-redundant-load-elimination.md), [O0084](O0084-cross-statement-register-caching.md), [O0027](O0027-copy-propagation.md) |

## The idea

The exact pattern:

```asm
    mov     [n], ax          ; store
    mov     ax, [n]          ; reload — AX already holds it
```

After `x = <expression>`, the value is still in the accumulator. An immediately
following read of `x` should use the register instead of reloading the cell.
The same shape appears whenever consecutive statements touch the same variable,
which in generated BASIC code is most of them — `n% = n% + 1 : IF n% = 0 …`
stores and reloads `n%` for no reason at all.

[O0034](O0034-redundant-load-elimination.md) originally did this only for `[BP+d]`
frame cells. The same recorded instruction chain is sufficient for ordinary direct
variables too: a segment-register load, call, inline-asm block or other opaque operation
is not recorded and therefore breaks the chain before a DS-relative access can be
forwarded across it.

## Applies to

```basic
DIM x%, y%
x% = a% + b%
y% = x% * 2
```

## Before

```asm
    mov     ax, [a]
    add     ax, [b]
    mov     [x], ax
    mov     ax, [x]          ; AX already holds it
    shl     ax, 1
    mov     [y], ax
```

## Now

```asm
    mov     ax, [a]
    add     ax, [b]
    mov     [x], ax
    shl     ax, 1
    mov     [y], ax
```

## How it is proven

`RunLoadForwarding` recognizes a plain word `MOV` store into either:

- a BP-relative frame cell, whose segment is inherently SS; or
- an **unprefixed direct label** — the ordinary representation of a BASIC scalar
  variable in the program data segment.

It then scans forward only through an unbroken, byte-adjacent sequence of recorded
instructions. The load can become:

- nothing, when it reloads into the register that still holds the value;
- a register move, when another register needs the value; or
- for frame slots whose last store was an immediate, a direct immediate load.

The proof stops on a label, a write to the held register, a possibly-aliasing store, or
any unrecorded instruction. That last rule is what makes direct variables safe: `MOV DS,…`,
a call, inline asm, string machinery and other opaque operations all create a gap, so the
optimizer never assumes the same segment or memory contents survived them.

Explicit segment overrides (`ES:[label]`, `CS:[label]`, and so on) deliberately do not
qualify. Immediate forwarding also remains frame-only; a direct-label immediate store
contains an address fixup as well as the value, and O0083 does not need to distinguish the
two to remove the overwhelmingly common register store/reload pair.

The pass composes directly with [O0081](O0081-flag-reuse.md): once a redundant variable
reload disappears, an earlier `ADD`/`SUB`/`DEC` can remain the live producer of ZF/SF/PF
for the following branch, allowing the subsequent zero test to disappear too.
