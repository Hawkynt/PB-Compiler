# O0266 — Zero-length string intrinsic folding

| | |
|---|---|
| **Status** | ✅ Done |
| **Stage** | Emitter |
| **IR** | ✅ `Ir/Passes/StringConstantFold.cs` — registered as `strfold` in `IrPassManager.Standard()`. A PB string is a runtime HANDLE, so a zero-length substring is spelled as a call and neither `Sccp` nor `InstCombine` can reach it: they reason about values, not about what a particular runtime routine means. The fold has to account for the handles the call was going to eat - it cancels the borrow an argument came from, or frees it where the call stood. Getting that wrong reads as OUT OF STRING SPACE much later, not as a leak |
| **Related** | [O0178](O0178-empty-string-simplification.md), [O0001](O0001-constant-folding.md), [O0016](O0016-value-fact-analysis.md), [O0180](O0180-string-length-caching.md), [O0181](O0181-empty-string-comparison.md) |
| **Split from** | [O0178](O0178-empty-string-simplification.md) |

## The idea

String intrinsics with a provably zero length produce the empty string and need
no runtime call at all:

| Expression | Result |
|---|---|
| `LEFT$(s$, 0)`, `RIGHT$(s$, 0)`, `MID$(s$, i, 0)` | `""` |
| `SPACE$(0)`, `STRING$(0, c)` | `""` |
| `MID$(s$, i)` where `i > LEN(s$)` is provable | `""` |

## Applies to

```basic
DIM s$, t$, n%
n% = 0
t$ = LEFT$(s$, n%)           ' n% is provably 0
```

## Now

```asm
    ; t$ = LEFT$(s$, 0)   ->   t$ = ""
    xor     ax, ax           ; handle 0 - no rt_strleft call
```

`IsZeroLengthStringIntrinsic` (in `EmitIntrinsic`) folds `LEFT$`/`RIGHT$`/`MID$`
with a zero length, and `SPACE$`/`STRING$` with a zero count, to `xor ax, ax` —
the very instruction an `""` literal emits, so the result is handle 0 and
composes with every empty-handle path ([O0181](O0181-empty-string-comparison.md)
comparison, assignment, concat).

### How it stays correct

- **Zero is proven, not guessed.** The length feeds `FactsOf(...).Range`, so
  both a literal `0` and a provably-zero *variable* (`n% = 0 : LEFT$(s$, n%)`,
  `STRING$(z%, "x")`) fold — the useful case, via the
  [O0016](O0016-value-fact-analysis.md) lattice.
- **Nothing observable is skipped.** The source string and any index must be
  side-effect-free (a literal, named constant, or plain variable) — a function
  call or array element there declines the fold, so its evaluation and any trap
  still happen through the normal call.
- **`MID$(s$, i, 0)` is `""` for any start `i`**, out-of-range included — matched
  against `rt_strmid` by a self-differential run over `i = -1 … 7` (optimized ==
  the golden-faithful unoptimized build).

Native-only, in `CodeGenerator.EmitIntrinsic`. On the IR back ends these
intrinsics lower to `rt_*` calls with a constant `0` length, which the host C
compiler's own constant-folding collapses, so no dedicated IR pass is needed.
