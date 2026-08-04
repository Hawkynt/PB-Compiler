
## Not yet: separated by other statements

The pair above must be **strictly adjacent**, because the reuse is `DX` itself — the register the
`IDIV` left the remainder in — and anything between could clobber it.

That is narrower than the optimization really is. The remainder is still the same value however far
apart the two statements sit, across intervening statements, loops and calls; what is needed is
somewhere to *keep* it:

```basic
q% = n% \ d%
FOR i% = 1 TO 10          ' anything at all in between
  PRINT i%
NEXT i%
r% = n% MOD d%            ' still the remainder that IDIV already computed
```

**Where to keep it.** Not `AllocTemp` — that is a bump allocator with `ReleaseTemp`, scoped to one
expression's evaluation, so a slot taken at the divide can be handed to something else before the MOD
reads it. The right vehicle is the **CSE slot area** (`_cseBytes`), which already exists to hold a
value computed once and reloaded at a later statement. Framed that way this is not a new mechanism at
all: it is CSE with one extra rule — *`n MOD d` is available wherever `n \ d` has been computed, and
vice versa* — with the divide defining both slots.

**What has to be proved before it fires**, beyond the conditions the adjacent form already checks:

- the divide **dominates** the MOD. Same statement list and earlier in it is sufficient; a divide
  inside an `IF` with the MOD after it is not, because the divide may not have run;
- nothing between them changes `n` or `d` — including inside nested blocks, which have to be scanned
  recursively;
- a call in between is only harmless when `n` and `d` are out of its reach: local, not `SHARED`, not
  `STATIC`, never passed `BYREF`, address never taken. Otherwise the call may have rewritten them and
  the stored remainder is stale;
- `ON ERROR`/`RESUME` in the body disqualifies it, since a resume can re-enter between the two points.

The payoff is worth the analysis: a 16-bit `IDIV` is ~100-180 cycles on an 8086, so the second divide
is by far the most expensive redundancy in the statement - and the separated form is the one real
programs actually write.
