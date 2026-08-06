# O0100 — Perfect-hash dispatch

| | |
|---|---|
| **Status** | 🟡 Partial (the low-bit `AND`-mask perfect hash is emitted, for `INTEGER` and `LONG`/`DWORD` subjects; the multiply/shift and modulus families in the hash search are not) |
| **Stage** | Emitter |
| **Related** | [O0029](O0029-select-jump-table.md), [O0098](O0098-balanced-decision-tree.md), [O0099](O0099-bit-test-dispatch.md) |

## The idea

For a sparse but fixed set of case values, a small collision-free arithmetic
mapping — `(k * a) >> s AND m`, or `k MOD p` for a suitable prime — indexes a
compact table directly, giving constant-time dispatch where the value span is
far too wide for [O0029](O0029-select-jump-table.md)'s dense table and a
decision tree ([O0098](O0098-balanced-decision-tree.md)) would cost log n
compares.

## Applies to

```basic
SELECT CASE scancode%
  CASE 72  : ...      ' up
  CASE 80  : ...      ' down
  CASE 75  : ...      ' left
  CASE 77  : ...      ' right
  CASE 71  : ...      ' home
  CASE 79  : ...      ' end
  CASE ELSE : ...
END SELECT
```

Six values spread over 71..80 — dense enough today, but the same shape with
extended keys (`&H4700`, `&H4B00`, …) is not.

## Planned

```asm
    mov     ax, [scancode]
    mov     bx, ax
    and     ax, 000Fh        ; the chosen perfect hash for this value set
    shl     ax, 1
    mov     si, ax
    cmp     bx, [KeyTable+si]   ; verify: the hash is not injective on all inputs
    jne     Default
    jmp     word ptr [JumpTable+si]
```

## Now

`TryEmitSelectPerfectHash` (`CodeGenerator.cs`) fires when the dense jump table
declined, the subject is `INTEGER`, every arm is a single-constant point case, and
there are ≥ 8 distinct values. It searches for the smallest table width `k ≤ 8`
whose low `k` bits (`value AND (2^k − 1)`) are **distinct across all values** — a
collision-free hash into a `2^k`-entry table. The subject is masked (`AND AX, m`),
scaled, and used to index a **key table** and a parallel **jump table**; the key is
verified (`CMP CX, [KeyTable+BX]` — the hash is perfect only on the case values, so
every other input must be rejected) and the indexed jump taken. Empty slots point
their jump entry at the default, so a non-member that collides into one is routed
correctly regardless of the verify. It is tried **before** the balanced tree
([O0098](O0098-balanced-decision-tree.md)) because it is O(1) where the tree is
O(log n); if no mask within 8 bits separates the values it declines and the tree
takes over. The dispatch keeps to `AX`/`BX`/`CX` so a resident `SI`/`DI` loop
counter or accumulator survives. Gated on `$OPTIMIZE SPEED`; the same arm runs as
the compare chain — verified by a self-differential DOSBox run over the whole
subject range (all 8 members, low-bit-**colliding** non-members that exercise the
verify, and plain non-members) identical to `$OPTIMIZE OFF`, plus a regression test
pinning the `AND AX, 7` masked-table shape. Golden gate 250/250.

## Still planned

- The rest of the **hash search**: multiply-shift (`(k * a) >> s`) and modulus
  (`k MOD p`) families for value sets whose low bits collide at every width — the
  current search is the `AND`-mask family only.
- A cost-model decision against the tree and the table where more than one applies.

A `LONG`/`DWORD` subject now hashes through the same 16-bit table — every key
must fit an int16 to survive the fold, so the table serves both. The subject is
first proven to BE its own int16 low half (`CWD` against the real high word,
parked in `BX`, which is free until the slot index is computed).

Here that guard buys **correctness**, not merely a skipped table read, and it is
worth being explicit about why. The slot verify compares the subject against the
key stored at its slot — but it compares the TRUNCATED low word. So 0001_03E8h
hashes to 1000's slot, matches the key 1000 sitting there, and takes that arm.
The verify cannot reject what it never sees; only checking the high word first
does. The tree ([O0098](O0098-balanced-decision-tree.md)) and the per-arm mask
([O0099](O0099-bit-test-dispatch.md)) use the same guard for the same reason.
