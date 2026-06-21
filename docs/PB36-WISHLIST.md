# PB 3.6 feature wishlist (beyond what's built)

Candidate features for the `pb36` dialect, **excluding objects/classes** (inheritance,
virtual dispatch, late binding — the compile-time object model in
[PB36-TYPES.md](PB36-TYPES.md) is deliberately the ceiling there). Everything below is
value-semantics, compile-time, or zero-/low-overhead, in keeping with pb36's "blazingly
fast and lean" mission. Each item notes a rough size (S/M/L) and what it builds on.

Already shipped (for reference): the optimizer pipeline, TYPE methods/properties/auto- &
anonymous properties/constructors/`READONLY`, **generics** (types + procedures), `YIELD`
generators, lambdas/closures, nested procedures, typed delegates, `FOR EACH`,
`TRY/CATCH/FINALLY` incl. filtered `CATCH`, **`DEFER`**, **FUNCTION returning a TYPE by
value (struct return)**, compound assignment, ternary `IF()`, short-circuit
`ANDALSO/ORELSE`, shift/rotate, `ENUM`, `WITH`, default/named parameters, overloading,
string interpolation, array-initializer literals.

> **Done from this list:** `DEFER` (scope guards) and FUNCTION-returns-TYPE-by-value
> (the keystone that unblocks **tuples** and UDT-returning **`OPERATOR`** overloads).

## Type system (non-OOP)

- **Tuples & multiple return values** (M) — `FUNCTION DivMod(...) AS (LONG, LONG)`,
  `q, r = DivMod(a, b)`. A small anonymous value-aggregate + destructuring assignment.
  High ergonomic payoff; rides the existing UDT/value machinery.
- **Discriminated unions / variant records** (L) — a tagged sum type
  (`TYPE Shape = Circle(r) | Rect(w, h)`) with exhaustive `SELECT CASE` matching. The
  natural complement to generics for modelling data without classes.
- **Nullable / option types** (M) — `DIM x AS LONG?` (a value + presence flag) with a
  null-coalescing operator. Safer than sentinel values; pure value semantics.
- **`OPERATOR` overloading for TYPEs** (M) — `OPERATOR + (a AS Vec, b AS Vec) AS Vec`,
  resolved at compile time like a TYPE method. Value-type feature (no dispatch), makes
  numeric-ish UDTs (Vec2, Fixed, BigInt) ergonomic.
- **Type aliases** (S) — `TYPE Celsius = LONG` / `ALIAS`, distinct-or-transparent, for
  readable signatures (pairs well with generics, e.g. `Stack OF Celsius`).
- **Bit-field members in TYPE** (M) — `Flags AS BIT * 3`, packed sub-byte fields for
  registers/hardware structs.

## Functional / iteration

- **Range expressions & lazy sequence operators** (M) — first-class `1..n` ranges and
  `MAP`/`FILTER`/`REDUCE`/`TAKE` over generators (`FOR EACH x IN Range(1,10) FILTER ...`),
  building straight on the `YIELD` enumerator model.
- **Pipeline / `|>` operator** (S) — `x |> f |> g` = `g(f(x))`; pure parser sugar over
  calls, great with the above.
- **Partial application / function composition** (M) — bind some arguments of a delegate;
  compose two delegates. Extends the existing typed-delegate support.
- **Array slicing / spans** (M) — `a(2..5)` a borrowed view; `a(^3..)` from-end slices
  (we already have `arr(^n)` indices).

## Control flow & error handling

- **Pattern matching `SELECT CASE`** (M) — match on type/shape/destructure, exhaustiveness
  checking; the consumer side of discriminated unions.
- **`DEFER` / scope guards** (M) — `DEFER CloseFile(h)` runs on scope exit (normal or via
  the error path), lowered onto the `FINALLY` machinery already in place.
- **Result/error-return types & propagation** (L) — an `Outcome OF T` plus a `?`
  propagation operator as a typed alternative to `TRY/CATCH`; rides generics + tuples.
- **`ITERATE`/`EXIT` with labels** (S) — break/continue an outer named loop.

## Compile-time / metaprogramming

- **Compile-time function evaluation (`CONSTEXPR`)** (L) — run a pure user `FUNCTION` at
  compile time to fold constants / size arrays; supercharges the existing constant folder.
- **Static assertions** (S) — `$ASSERT cond, "msg"` evaluated at compile time
  (sizes, ranges, feature gates).
- **Compile-time reflection over TYPEs** (M) — iterate a TYPE's fields at compile time
  (auto-serialize, auto-print), monomorphized like generics — no runtime RTTI.
- **`$RESOURCE` / embed-binary** (S) — bake a file into the image as a byte array
  (sprites, tables), with `$INCLUDE`-style ergonomics.

## Memory & performance (pb36's core mission)

- **Arena / region allocators** (M) — `WITH ARENA ... END` bulk-frees temporaries; cheap
  scratch memory without per-object frees.
- **`ALIGN` / `PACKED` TYPE layout control** (S) — explicit field alignment/packing for
  speed or hardware/file layout.
- **SIMD/MMX (586/MMX gate) intrinsics** (L) — wide integer/byte ops behind `$CPU`, the
  next tier after the 386/486 codegen already done.
- **`FORCEINLINE` / `NOINLINE` hints** (S) — override the trivial-method inliner's
  heuristic when the programmer knows better.
- **Stack arrays / `ALLOCA`** (S) — fixed-size scratch arrays on the frame, no heap.

## Tooling / dialect

- **The source-writer / decompiler** (M) — already on the list: re-emit BASIC from the
  fully woven, optimized AST/IR to see the program "without the magic" (see memory
  `pb36-source-writer`).
- **`$IF` with full constant expressions** (S) — richer conditional compilation than the
  bare-equate condition genuine PBC allows.
- **Contracts** (M) — `REQUIRE` / `ENSURE` pre/postconditions, compiled out in release.

## Suggested near-term order

1. **Tuples + multiple return values** — high payoff, moderate size, broadly useful.
2. **`OPERATOR` overloading** — completes the value-type story alongside generics.
3. **`DEFER`** — small, reuses `FINALLY`, immediately useful for resource handling.
4. **Range + lazy sequence operators** — leverages the generator model already built.
5. **Discriminated unions + pattern matching** — the bigger, higher-value pair once the
   above land.
