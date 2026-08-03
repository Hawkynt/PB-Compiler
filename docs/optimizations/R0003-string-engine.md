# R0003 — String engine

| | |
|---|---|
| **Status** | ✅ Implemented (DWORD-wide copy under `$CPU 80386`, in-place append paths); free-lists and the two-digit formatter are ⬜ planned |
| **Stage** | Runtime + emitter |
| **Source** | `EmitRepMovsbWidened`, `Runtime/DosRuntime` — `rt_strcatlit`, `rt_strcatvar`, `rt_strcatn` |
| **Gate** | `--optimize`; DWORD widening needs `$CPU 80386` |
| **Verified by** | `tests/diff/DIFF40.BAS` (a 386 string storm), `DIFF23.BAS` (block moves) |
| **Related** | [O0009](O0009-string-temp-economy.md), [O0024](O0024-multi-concat.md), [C0001](C0001-386-codegen.md) |
| **Split into** | [O0241](O0241-dword-string-copy.md), [O0242](O0242-movsd-block-copy.md) |

## What it is

**This page is the string subsystem's roadmap entry** — the engine as a whole,
and what it still lacks. The individual implemented widenings and in-place paths
each have their own entry:

| Implemented | Entry |
|---|---|
| DWORD-wide string copy | [O0241](O0241-dword-string-copy.md) |
| DWORD block copy for TYPE/`LSET` | [O0242](O0242-movsd-block-copy.md) |
| in-place append (literal / variable / chain) | [O0208](O0208-inplace-literal-append.md), [O0209](O0209-inplace-variable-append.md), [O0210](O0210-concat-chain-temp-reuse.md) |
| single-allocation multi-concat | [O0024](O0024-multi-concat.md) |

## Sample

```basic
$CPU 80386
DIM a$, b$
a$ = STRING$(4000, "x")
b$ = a$ + a$
```

## Without the optimizer

```asm
    mov     cx, 0FA0h
    rep     movsb            ; 4 000 byte moves
```

## With the optimizer

```asm
    mov     cx, 03E8h
    rep     movsd            ; 1 000 dword moves + a <=3-byte tail
```

The copied bytes are identical — only the transfer width changes.

## Equivalent BASIC

Unchanged.

## What is still planned

- **Heap free-lists** to avoid compaction storms: today a freed block is
  reclaimed by compaction, which is O(heap) at exactly the moment a program is
  allocating hardest.
- **Two-digit-table number formatting**, which halves the divide-by-ten loop for
  PRINT-heavy code and pairs with
  [O0056](O0056-reciprocal-division.md)'s reciprocal division.
- Named access to the string-manager ABI from inline asm — see
  [R0004](R0004-asm-intrinsics.md).
