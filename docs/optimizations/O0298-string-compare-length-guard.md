# O0298 — String comparison length guard

| | |
|---|---|
| **Status** | 🟡 Partial (equality `=` / `<>` short-circuit on length **and** widened content compare; the ordering forms `<` / `>` still compare byte-wise) |
| **Stage** | Runtime + emitter |
| **IR** | ✅ `Ir/Passes/StringCompareEquality.cs` — registered as `strcmpeq` in `IrPassManager.Standard()`, covering the same equality half as the emitter. `rt_str_compare` walks bytes to the first difference so it can say which string sorts first; `=` and `<>` never need that ordering, and unequal lengths settle it without reading a byte. The rewrite is a callee swap - same handles, same registers, same consumption - so the whole soundness condition is that the answer is only ever tested against zero |
| **Related** | [O0181](O0181-empty-string-comparison.md), [O0180](O0180-string-length-caching.md), [R0003](R0003-string-engine.md) |

## Now

For `=` and `<>`, two strings of different lengths are unequal without examining a
byte. A dedicated runtime routine `rt_strcmpeq` (`EmitStrCmpEq`, `DosRuntime.Strings.cs`)
loads both descriptors and, when the lengths differ, returns "unequal" immediately —
turning the common negative case into two loads and a compare, where the full
`rt_strcmp` still `REPE CMPSB`s the common prefix before comparing lengths. The
emitter routes a `=` / `<>` string comparison to it under `--optimize`
(`CodeGenerator.Expressions.cs`), and likewise an equality `SELECT CASE` arm over a
string subject (`CASE "quit"`, in `EmitSelectorString`); it returns 0 (equal) /
1 (unequal), which the same `je`/`jne` test reads, and consumes (frees) both operands
exactly like `rt_strcmp`. Ordering arms (`CASE IS < …`) keep the full compare.

Once the lengths are known equal the content scan runs a **word at a time**: `SHR CX,1`
words through `REPE CMPSW`, then the single trailing byte when the length is odd. That is
half the REPE iterations of the byte scan, and it touches exactly `length` bytes — the
`length >> 1` words plus the odd byte — so a string ending at the last byte of the heap is
never read past. Widening is sound **only for equality**: `CMPSW` compares little-endian
16-bit values, so on a mismatch its sign says which word is larger as a number rather than
which string sorts first (`"ba"` is 0x6162 and `"ab"` is 0x6261, ordering them backwards).
The ordering forms therefore keep `CMPSB`.

**How much the widening actually saves depends on the bus, and neither half of that
is controlled here.** `REPE CMPSB` reads two bytes per iteration; `REPE CMPSW` reads
two words over half as many iterations. On an **8088** a word is two bus cycles, so
the traffic is identical (2n either way) and only the REP loop overhead is saved. On
a true 16-bit **8086** with word-aligned operands the traffic halves — but a string
at an odd address costs four extra clocks per access, which spends the gain. PB's
string heap does not align its allocations, so both cases occur.

The widening is never worse in instruction count and is a real win on aligned 16-bit
accesses; "half the REPE iterations" is the honest description, "twice as fast" is
not. Measured claims about it want a specific machine and a known alignment.

`rt_strcmpeq` is referenced only by the optimized emitter, so the faithful build keeps the
full three-way compare for every comparison it makes (golden gate 250/250). Note it is not
*absent* from that image, though: dead-code trimming is a Tier 3 pass that runs under
`--optimize` only, so a `--dialect pb35` build carries the routine's bytes as unreferenced
dead code — measured, after this page previously claimed a "trimmed section". What the
faithful build keeps is the **call**, not the absence of the callee. Verified by a self-differential DOSBox run over equal, unequal
same-length, unequal different-length (the guard path), prefix (`"hello"` vs
`"hello world"`), empty and literal comparisons — all identical to `$OPTIMIZE OFF` —
plus a regression test that the `=` routine begins with the length guard while an
ordering `<` keeps the min computation.

## Still planned

- The ordering forms `<` / `>` comparing the common prefix wide. Worked out but not
  built; the shape is below so it need not be re-derived.

  It cannot widen `rt_strcmp` in place. That routine is what the FAITHFUL build
  calls, so touching its bytes moves non-optimized output — the one thing the
  golden gate forbids. It needs a second routine referenced only by the optimized
  emitter, exactly as `rt_strcmpeq` is, which the trimmed-section arrangement
  already in place carries.

  The loop, after the existing `CX = min(len)` and `JCXZ`:

  ```
      push ds
      mov  bx, es
      mov  ds, bx
      mov  bx, cx          ; keep the min: its low bit is the odd-tail test
      shr  cx, 1
      jz   tail            ; min is 1 - no whole word to compare
      repe cmpsw
      jne  mismatch
  tail:
      test bl, 1
      jz   prefixPop       ; even min, the common prefix is equal
      cmpsb                ; the odd trailing byte decides
      jne  diffPop
  prefixPop:
      pop  ds
      jmp  prefix          ; on to the length comparison
  mismatch:
      sub  si, 2           ; REPE left SI/DI past the differing word
      sub  di, 2
      cmpsb                ; its first byte - if equal, the second must differ
      jne  diffPop
      cmpsb
  diffPop:
      pop  ds
      jmp  diff            ; CMPSB's flags are the lexicographic answer
  ```

  Three things make it work and each is easy to lose. The mismatch must be
  re-compared BYTE-wise, because `CMPSW`'s own sign orders the little-endian 16-bit
  values and would sort `"ba"` before `"ab"` (0x6162 against 0x6261). `POP DS` does
  not disturb flags, which is what lets the answer survive to `diff` — the existing
  `rt_strcmp` leans on the same property. And every path leaving the `PUSH DS`
  region must pop exactly once: the three exits are `prefixPop`, `diffPop`, and the
  `JCXZ` that never entered.

  Worth measuring before building, and here is the number to measure against: the
  mismatch disambiguation costs about 52 cycles on an 8086, which buys back roughly
  22 per byte-PAIR saved, so it **breaks even at about six bytes of common prefix**
  and is a loss below that.

  | common prefix | `CMPSB` | widened | |
  |---|---|---|---|
  | 2 | 44 | 78 | loss |
  | 4 | 88 | 104 | loss |
  | 6 | 132 | 130 | break-even |
  | 10 | 220 | 182 | win |
  | 20 | 440 | 312 | win |
  | 40 | 880 | 572 | win |

  Equal strings never pay the penalty (there is no mismatch), so they always win;
  ordering comparisons that differ early do not. Whether that is a net gain depends
  on how far real comparisons agree before diverging, which is a profiling question
  about actual programs, not one this page can settle.
- `CMPSD` for the equal-length case on a 386 target, halving the iterations again
  behind the `$CPU` gate.

## The idea

For `=` and `<>`, two strings of **different lengths** are unequal — no byte
needs to be examined. Testing lengths first turns the common negative case into
two loads and a compare, and the positive case can then run a widened content
comparison (`REPE CMPSW`/`CMPSD`) since the lengths are known equal.

Ordering comparisons (`<`, `>`) still need the content, but can compare the
common prefix wide and only then consider the length difference.

## Applies to

```basic
DIM a$, b$
IF a$ = b$ THEN ...
```

## What it needs

- The length guard in `StrCmp` itself — one implementation benefiting every
  program, rather than a codegen pattern.
- Widened content comparison with a tail
  ([O0241](O0241-dword-string-copy.md) does the same for copying).
- PB's exact comparison semantics for the ordering forms, including how a
  shorter string that is a prefix of a longer one orders.
