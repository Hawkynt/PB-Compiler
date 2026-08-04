# O0079 — Quotient and remainder share one divide

| | |
|---|---|
| **Status** | ✅ Done (adjacent `q = n\d` / `m = n MOD d` over a runtime divisor) |
| **Stage** | Emitter |
| **Related** | [O0003](O0003-common-subexpression-elimination.md), [O0004](O0004-strength-reduction.md), [O0056](O0056-reciprocal-division.md) |

## The idea

`IDIV` leaves the quotient in AX **and** the remainder in DX. When a program
needs both `n \ d` and `n MOD d` with the same operands, one divide suffices —
and on an 8086 a 16-bit `IDIV` costs ~100–180 cycles, so the second one is by
far the most expensive redundancy in the statement.

## Applies to

```basic
DIM n%, d%, q%, r%
q% = n% \ d%
r% = n% MOD d%
```

## Today

Two full divides, each with its own divide-by-zero guard:

```asm
    mov     ax, [n]
    mov     bx, [d]
    or      bx, bx
    jz      rt_err_div
    cwd
    idiv    bx
    mov     [q], ax
    mov     ax, [n]          ; and again
    mov     bx, [d]
    or      bx, bx
    jz      rt_err_div
    cwd
    idiv    bx
    mov     [r], dx
```

## Now

```asm
    mov     ax, [n]
    mov     bx, [d]
    or      bx, bx
    jz      rt_err_div
    cwd
    idiv    bx
    mov     [q], ax
    mov     [r], dx          ; the remainder IDIV already left in DX - no second divide
```

`PrepareDivMod` (run per body, after CSE/SCCP) marks the `MOD` statement of a
strictly-adjacent `q = n\d : m = n MOD d` pair. The divide is emitted untouched;
`EmitAssign` sees the marked `MOD` and emits a single `mov [m], dx`, reusing the
remainder the `IDIV` left live in DX. One `IDIV` instead of two — a runtime
`IDIV` on the 8086 is ~100–180 cycles, so this halves the most expensive part of
the statement. Verified byte-identical against the genuine oracle over the full
sign matrix (`n = -7…7`, a negative divisor), and a regression test confirms the
optimized image runs one `IDIV` where the unoptimized build runs two.

### Why it is sound

- **DX is live at the `MOD`.** The two statements are strictly consecutive and
  `LabelStmt` is its own statement, so adjacency proves nothing can branch
  between them; the divide's `IDIV` leaves DX = remainder and the quotient store
  (`mov [q], ax`, a plain scalar) never touches DX.
- **The remainder is exactly this pair's.** The operands must be the *same*
  side-effect-free `INTEGER` values (a plain variable or constant), so the divide
  computed precisely this remainder and nothing — e.g. a `FUNCTION` call — is
  dropped by not re-evaluating them for the `MOD`.
- **A real `IDIV` runs.** The divisor must be a genuine runtime value; a constant
  could strength-reduce or fact-fold the divide away (leaving no remainder in
  DX), so those pairs are left alone.
- **No aliasing.** The quotient target may not be an operand — `n = n\d` then
  `m = n MOD d` must re-read the *updated* `n`, so it is deliberately not paired
  (confirmed against the oracle). Checked arithmetic, CSE-shared or SCCP-dead
  operands, and non-scalar targets all decline.

## Equivalent BASIC

```basic
q% = n% \ d%
r% = n% - q% * d%        ' the same value, without the second divide
```

Native-only, in `CodeGenerator`. The IR back ends emit both a `/` and a `%` over
the same operands, which LLVM's GVN and the host C compiler already CSE into a
single `divmod`, so the C/LLVM output shares the divide without a dedicated pass.

## Either order — done

One `IDIV` answers both questions, so it does not matter which the program asks for first:

```basic
r% = n% MOD d%            ' this IDIV also produced the quotient
FOR i% = 1 TO 3 : PRINT i% : NEXT i%
q% = n% \ d%              ' which is reused here
```

The two directions stash different registers, and the reversed one needs a different moment. A MOD
emits `IDIV` and then `MOV AX,DX` to bring the remainder into the result register - so the quotient
has to be taken out of `AX` *between those two instructions*, not after the statement the way the
remainder is. The emitter is told through `_stashQuotientSlot` before the statement is emitted, and
the `Modulo` case writes the slot at exactly that point.

## Separated by other statements — done

The pair above reuses `DX` itself, which only works while the two statements are **adjacent**. The
remainder is the same value however far apart they sit, so when they are separated it is stashed in a
frame slot at the divide and loaded at the MOD — statements, loops and calls in between:

```basic
q% = n% \ d%
FOR i% = 1 TO 10          ' anything at all in between
  PRINT i%
NEXT i%
r% = n% MOD d%            ' still the remainder that IDIV already computed
```

**Where it is kept.** Not `AllocTemp` — that is a bump allocator with `ReleaseTemp`, scoped to one
expression's evaluation, so a slot taken at the divide can be handed to something else before the MOD
reads it. The right vehicle is the **CSE slot area** (`_cseBytes`), which already exists to hold a
value computed once and reloaded at a later statement. Framed that way this is not a new mechanism at
all: it is CSE with one extra rule — *`n MOD d` is available wherever `n \ d` has been computed, and
vice versa* — with the divide defining both slots.

**What is proved before it fires**, beyond the conditions the adjacent form already checks:

- the divide **dominates** the MOD. Same statement list and earlier in it is sufficient; a divide
  inside an `IF` with the MOD after it is not, because the divide may not have run;
- nothing between them changes `n` or `d` — including inside nested blocks, which have to be scanned
  recursively;
- a call in between is only harmless when `n` and `d` are out of its reach: local, not `SHARED`, not
  `STATIC`, never passed `BYREF`, address never taken. Otherwise the call may have rewritten them and
  the stored remainder is stale;
- `ON ERROR`/`RESUME` in the body disqualifies it, since a resume can re-enter between the two points.

A `PRINT` between the two is **not** a call for this purpose: it enters the DOS runtime, and the
runtime does not write the program's variables. A call to a user `SUB`/`FUNCTION` is, and so is any
statement kind the analysis does not model - guessing the other way is how a stale value gets reused.

The payoff is worth the analysis: a 16-bit `IDIV` is ~100-180 cycles on an 8086, so the second divide
is by far the most expensive redundancy in the statement - and the separated form is the one real
programs actually write.
