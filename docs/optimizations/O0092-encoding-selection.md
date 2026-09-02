# O0092 — Encoding selection: size, micro-ops and decode width

| | |
|---|---|
| **Status** | 🟡 Partial (flag-safe zero idioms and early-target INC/DEC selection are wired; general per-target encoding competition remains) |
| **Stage** | Emitter / assembler |
| **Related** | [O0035](O0035-jump-relaxation.md), [O0038](O0038-instruction-scheduling.md), [O0174](O0174-target-cost-models.md) |
| **Split into** | [O0244](O0244-microop-selection.md), [O0245](O0245-decode-width-scheduling.md), [O0246](O0246-move-elimination-aware.md) |

## The idea

x86 offers several encodings for the same operation, and which one is best is a
*target* question, not a universal one:

- the accumulator short forms (`ADD AX,imm` = `05 iw`) against the general modrm
  forms (`81 /0`) — fewer bytes, but they pin the value to AX
  ([O0072](O0072-register-reassignment.md));
- `INC` versus `ADD r,1`; `LOOP` versus `DEC CX`/`JNZ` (the 486 prefers the
  latter, [C0002](C0002-486-codegen.md));
- **micro-op count** rather than instruction count on decoded cores: a single
  microcoded instruction may cost more than three simple ones;
- **decode width and boundaries** on superscalar cores, where an awkward
  instruction mix throttles the front end;
- **move elimination** on cores that resolve register-to-register moves in
  rename, which changes the cost of coalescing
  ([O0085](O0085-copy-coalescing.md)).

On an 8086 exactly one of these matters, and it matters a lot: **fewer
instruction bytes** keep the 4-byte prefetch queue full while the bus interface
unit is the bottleneck.

## Applies to

Every emitted instruction — this is a selection policy, not a pattern.

## Now

`Assembler.EncodingSelect.cs` performs two shrink-only choices immediately before
the `$OPTIMIZE SPEED` instruction scheduler consumes its def/use records.

### Zero materialization

A numeric word-register zero load:

```asm
    mov     ax, 0            ; 3 bytes, preserves flags
    ...                      ; no flag reader
    cmp     dx, si           ; independently replaces arithmetic flags
```

becomes:

```asm
    xor     ax, ax           ; 2 bytes
    ...
    cmp     dx, si
```

This is legal only when the pass can prove the changed flags are dead. It walks
the byte-adjacent recorded stream until a complete independent arithmetic flag
definition. A conditional branch, `ADC`/`SBB`, any other recorded flag read, an
unrecorded gap (`CALL`, inline asm, etc.), or the end of the run before such a
kill makes it decline.

An unresolved `MOV AX, OFFSET label` is explicitly excluded. Its immediate bytes
are zero placeholders before fixup resolution and are **not** the numeric value
zero; treating those bytes as a zero idiom would replace an address with zero.

### `ADD/SUB r,1` versus `INC/DEC`

On the default pre-386 target, when CF is dead by the same forward proof:

```asm
    add     ax, 1            ; 83 C0 01
    sub     dx, 1            ; 83 EA 01
```

become the one-byte encodings:

```asm
    inc     ax               ; 40
    dec     dx               ; 4A
```

`INC`/`DEC` preserve CF while `ADD`/`SUB` define it, so a carry consumer before a
full flag kill blocks the rewrite. A later `INC`/`DEC` is not accepted as a full
kill either, because it preserves exactly the flag whose difference matters.

The optimization is deliberately **not** applied for a selectable 386-or-later
CPU floor under SPEED. On early byte/prefetch-bound machines the two-byte saving
is the dominant win; later cores make flag dependencies and execution behavior
part of the cost. The compiler already exposes the 8086-vs-386+ boundary to the
assembler, so this pass uses that existing target fact rather than inventing a
second microarchitecture model.

Every rewrite repairs the scheduler record (length, register/flag effects) and
all shrinking goes through the common `RemoveBytes` machinery, so labels, fixups,
relocations and later instruction positions remain synchronized.

## Still planned

The general O0092 policy is larger than these two choices:

- use [O0174](O0174-target-cost-models.md) directly for competing accumulator,
  ModRM, microcoded and decomposed forms;
- account for decode width and micro-op count on Pentium/P6-class targets;
- make move-elimination and register reassignment costs part of encoding choice;
- perform choices that require changing register assignment/layout, not only
  shrink-only substitutions over an already emitted stream.

Length-changing selection still belongs before final layout/fixup resolution;
this implementation establishes that late-assembler path for the cases whose
legality can be proven from the existing def/use stream.
