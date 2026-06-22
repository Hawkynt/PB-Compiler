# PB 3.6 object model & `YIELD` coroutines

A minimal, fully compile-time object model for pb36: TYPEs gain methods and
properties, and any `SUB`/`FUNCTION` containing `YIELD` becomes a generator whose
call returns a first-class enumerator built from that same object model. No
inheritance, no virtual dispatch, no late binding — every member call resolves
from the static type of the receiver at compile time.

This supersedes the separate-stack fiber strategy sketched in
`PB36-COROUTINES.md`: coroutines lower to a **state machine** that rides on the
object model below, not to a context-switching fiber.

## 1. TYPE members (the foundation)

Surface (chosen): members are declared **inside** the `TYPE` block; the receiver
keyword is **`THIS`**.

```basic
TYPE Stack
  Count AS INTEGER
  Items(1 TO 100) AS LONG
  SUB Push(BYVAL v AS LONG)
    INCR THIS.Count
    THIS.Items(THIS.Count) = v
  END SUB
  FUNCTION Pop() AS LONG
    Pop = THIS.Items(THIS.Count)
    DECR THIS.Count
  END FUNCTION
  PROPERTY GET Size() AS INTEGER
    Size = THIS.Count
  END PROPERTY
  PROPERTY SET Size(BYVAL n AS INTEGER)
    THIS.Count = n
  END PROPERTY
END TYPE
```

### Lowering

Every member lifts to an ordinary top-level procedure whose **implicit first
parameter is `THIS`, passed BYREF**, of the enclosing TYPE:

| member            | lifted procedure                                  | call site                       |
|-------------------|---------------------------------------------------|---------------------------------|
| `SUB M(p…)`        | `SUB Stack.M(THIS AS Stack, p…)`                  | `o.M(a)` → `Stack.M(o, a)`      |
| `FUNCTION M(p…) AS T` | `FUNCTION Stack.M(THIS AS Stack, p…) AS T`     | `o.M(a)` → `Stack.M(o, a)`      |
| `PROPERTY GET P() AS T` | `FUNCTION Stack.get_P(THIS AS Stack) AS T`   | `o.P` (rvalue) → `Stack.get_P(o)` |
| `PROPERTY SET P(v)` | `SUB Stack.set_P(THIS AS Stack, v)`             | `o.P = x` → `Stack.set_P(o, x)` |

The mangled names use `.` (`Stack.Push`, `Stack.get_Size`), which a user
identifier can never contain, so there is no collision; call resolution just
constructs the name from the receiver's static type + member and looks it up.
Member bodies reuse the existing BYREF-UDT-parameter machinery: `THIS.field`
is an ordinary `MemberExpr` field access through the BYREF param.

### Auto-implemented & anonymous properties

A `PROPERTY GET`/`SET` with **no body** is auto-implemented over a hidden backing
field `$Prop`. Inside an explicit body, **`FIELD`** names that backing field and
**`VALUE`** names the setter's incoming value; `=>` gives an expression body:

```basic
TYPE Rect
  PROPERTY Width  AS LONG                       ' anonymous: auto getter + setter + field
  PROPERTY Height AS LONG
  PROPERTY GET Area AS LONG => THIS.Width * THIS.Height   ' computed, read-only
  PROPERTY SET Size() => FIELD = 2 * VALUE               ' FIELD = backing $Size, VALUE = incoming
END TYPE
```

`PROPERTY Name AS Type` with no `GET`/`SET` (anonymous form) expands to an auto
getter **and** an auto setter over one `$Name` field.

**Trivial methods inline (optimizer).** Accessors are ordinary lifted procedures
(`get_P` = `THIS.$P`, `set_P` = `THIS.$P = VALUE`) with no special-casing. The
general O6 inliner then inlines *any* trivial method body — auto-generated or
hand-written — by treating the `THIS` receiver as what it is: an ordinary BYREF
argument. A trivial method whose every call inlines is reachability-purged, so
`o.Count` (an anonymous property) ends up exactly as cheap as a field access, and a
hand-written `FUNCTION Sum() = THIS.x + THIS.y` inlines the same way. A method whose
body is too large, or a call where the receiver is not a near lvalue, falls back to
a real call.

### Constructors

A member `SUB` **named like the TYPE** is its constructor; it has `THIS` access and
runs when an instance is built with `p = TypeName(args)`:

```basic
TYPE Point
  x AS LONG
  y AS LONG
  SUB Point(BYVAL px AS LONG, BYVAL py AS LONG)   ' constructor (same name as the TYPE)
    THIS.x = px
    THIS.y = py
  END SUB
END TYPE

DIM p AS Point
p = Point(3, 4)            ' -> Point.Point(p, 3, 4) with p as the BYREF THIS
```

`p = Type(args)` desugars to a call of the lifted `Type.Type(THIS, args…)` with the
assignment target as the BYREF receiver.

### `READONLY` types

`TYPE Name READONLY … END TYPE` makes every field **write-once**: a field store is
allowed only inside that type's own constructor and rejected everywhere else at
compile time. This composes with anonymous properties — their setter routes through
the same field-store path, so `obj.Count = x` outside the constructor is a compile
error for a readonly type, while the constructor may set it.

```basic
TYPE Vec2 READONLY
  x AS LONG
  y AS LONG
  SUB Vec2(BYVAL ax AS LONG, BYVAL ay AS LONG)
    THIS.x = ax : THIS.y = ay     ' OK: inside the constructor
  END SUB
END TYPE
DIM v AS Vec2 : v = Vec2(1, 2)
v.x = 9                            ' compile error: field 'x' of READONLY TYPE Vec2 …
```

### Bit-field members

A field declared `Name AS BIT * width` (1..16; `AS BIT` alone is width 1) occupies
`width` bits rather than a whole storage cell. Consecutive bit-fields are **packed**
into a hidden 16-bit `$bits` WORD in declaration order; a bit-field that would overflow
the current word starts a new one, and any non-bit field breaks the run.

```basic
TYPE StatusReg
  Mode    AS BIT * 3      ' bits 0..2  of $bits0
  Enabled AS BIT          ' bit  3     of $bits0
  Level   AS BIT * 4      ' bits 4..7  of $bits0
END TYPE
DIM r AS StatusReg
r.Mode = 5 : r.Level = 12 ' each write preserves the neighbouring fields
```

**Lowering** (pure binder desugar, no new codegen — `PackBitFields` + `BitFieldOf`):

- a **read** `r.Mode` → `(r.$bits0 >>> offset) AND ((1 << width) - 1)`
- a **write** `r.Mode = v` → `r.$bits0 = (r.$bits0 AND clearMask) OR ((v AND mask) << offset)`,
  a read-modify-write so the other fields in the word are untouched.

Because both desugar to ordinary WORD arithmetic, bit-fields ride the existing
shift/and/or codegen and the optimizer folds constant masks for free.

### Layout control

By default a `TYPE` is **byte-packed** (no padding) — matching genuine PBC. pb36 adds
explicit control for hardware registers, file/wire formats and overlays:

```basic
TYPE Header ALIGN 4      ' each field on its natural boundary (capped at 4); total padded to 4
  tag    AS BYTE         ' offset 0
  length AS LONG         ' offset 4 (3 padding bytes after tag)
  flags  AS INTEGER      ' offset 8        -> LEN(Header) = 12
END TYPE

TYPE Sector SIZE 512     ' pad the whole record to exactly 512 bytes
  used AS INTEGER
END TYPE

TYPE RegView            ' explicit offsets: an overlay / union-style view
  whole AS LONG          ' offset 0
  lo    AS INTEGER AT 0  ' overlaps the low word
  hi    AS INTEGER AT 2  ' overlaps the high word -> LEN(RegView) = 4
END TYPE
```

- `PACKED` — the default byte layout, stated explicitly.
- `ALIGN n` (n = 1/2/4/8/16) — pad each field up to an n-byte boundary, but never past its
  own natural alignment (a `BYTE` stays contiguous even under `ALIGN 4`), and round the whole
  type up to a multiple of n.
- `SIZE n` — pad the whole type to exactly n bytes (must be ≥ its natural size).
- `field AS T AT offset` — place a field at an explicit byte offset; gaps and overlapping
  fields are allowed, and the type's size spans to the highest field end.

This is pure binder layout (`DefineUdt` computes each `UdtField.Offset` and the `UdtType`
size), so member access, array-of-TYPE strides and whole-TYPE block copies all use the
controlled offsets with no codegen change. pb36-only — genuine PBC has no layout keywords,
so it is verified by `LEN`/`VARPTR` execution tests, not the differential oracle.

### Implementation status

**Implemented** (`PowerBasic.Compiler/Semantics/Binder.cs`): methods, properties
(explicit, auto, anonymous, `=>` / `FIELD` / `VALUE`), trivial-accessor inlining,
constructors, `READONLY` enforcement, **bit-field members** (`AS BIT * n`), and
**layout control** (`PACKED` / `ALIGN n` / `SIZE n` / field `AT offset`).
Members lift in `DefineTypeMembers`; calls
resolve in the `MemberExpr` / `IndexExpr` / `MemberCallStmt` binding paths via
`Desugared` / `DesugaredStatements`; the FUNCTION/PROPERTY-GET result alias is the
member's simple name (`proc.ResultName`); `THIS` BYREF reuses the existing
BYREF-UDT-parameter backend (no new codegen).

Verification: pb36-only surface with **no differential oracle** (PBC 3.50 has no
TYPE methods), so each feature is verified by *execution* (compile + run in DOSBox,
compare to a hand-computed expected) plus unit tests
(`TypeMemberBinderTests`, `CoroutineBinderTests`); the full differential harness
stays byte-identical (the feature is inert for every existing battery).

## 2. `YIELD` → state machine → enumerator

Any `SUB`/`FUNCTION` whose body contains `YIELD` is **automatically** a generator
(no `ITERATOR` keyword). Calling it returns a first-class **enumerator** value
(a synthesized UDT) that can be stored in a variable and driven manually with
`.Reset` / `.MoveNext` / `.Current`, or consumed by `FOR EACH`.

```basic
FUNCTION Squares(BYVAL n AS INTEGER) AS LONG
  FOR i AS INTEGER = 1 TO n
    YIELD i * i
  NEXT
END FUNCTION

DIM e AS Squares                 ' the synthesized enumerator type
e = Squares(5)
WHILE e.MoveNext : PRINT e.Current : WEND
' or:
FOR EACH v AS LONG IN Squares(5) : PRINT v : NEXT
```

### Lowering

For a generator `G` returning element type `T`, synthesize a UDT
`G` (the enumerator) with fields:
- `__state AS INTEGER` — resume point (0 = not started, -1 = done, k = after yield k),
- the generator's **parameters and locals** (captured as fields so they persist across resumes),
- `__current AS T` — the last yielded value.

and members (lifted exactly as in §1):
- `FUNCTION MoveNext() AS INTEGER` — `SELECT CASE THIS.__state` dispatches to the resume
  label; runs the body until the next `YIELD x` (set `THIS.__current = x`, `THIS.__state = k`,
  return true) or the end (`THIS.__state = -1`, return false). Built as an AST→AST transform
  to `SELECT` + `GOTO` + labels, reusing the existing control-flow backend.
- `PROPERTY GET Current() AS T` — returns `THIS.__current`.
- `SUB Reset()` — `THIS.__state = 0` and re-seed the captured parameters.

The generator call `G(args)` lowers to: allocate a `G` instance, store `args` into its
parameter fields, set `__state = 0`, return it.

**Supported `YIELD` positions** (all flattened to the resumable state machine, nesting
freely): top level, inside `FOR` / `WHILE`-`DO`-`LOOP` / `IF` / `SELECT CASE`, inside a
`FOR EACH` over *another* generator (the inner enumerator persists across the outer yields
as a `$fe<n>` UDT field, and the loop variable persists as its own captured field), and
inside `TRY` / `CATCH` / `FINALLY`. Parameters and locals (suffix- or `AS`-typed) are
captured as enumerator fields and survive suspension; `INCR`/`DECR` of a captured variable
persists too. A `SELECT CASE` that yields requires a side-effect-free subject (it is
compared once per arm).

### `YIELD` inside `TRY` / `CATCH` / `FINALLY`

The normal `TRY` arms the ON ERROR handler on the **stack** (it pushes the previous
`rt_onerr` / `_bp` / `_sp` triple and pops it on exit). A `YIELD` does `EXIT FUNCTION`,
which unwinds that frame — so the stack save can't survive a suspension. The generator
state machine instead:

- snapshots the **caller's** handler triple into three enumerator fields
  (`$gonerr` / `$gbp` / `$gsp`) at the top of every `MoveNext` invocation;
- **arms** its own catch dispatcher (`rt_onerr = OFFSET catch`, `_bp = BP`, `_sp = SP`) on
  entry to the protected body and again at each resume into it (each `MoveNext` runs in a
  fresh frame, so the armed SP/BP must be this frame's);
- **disarms** (restores the caller's triple from the fields) before each in-`TRY` `YIELD`'s
  `EXIT`, so consumer code between `MoveNext` calls runs under the caller's handler, and on
  normal completion / at the catch-dispatch label.

A fault in the protected body during a `MoveNext` call lands on the armed dispatcher (SP/BP
restored to that invocation's frame), runs `CATCH` then `FINALLY`; a `TRY … FINALLY` with no
`CATCH` re-raises the still-set `ERR` to the now-restored outer handler. Yields in `CATCH`
and `FINALLY` are ordinary (the handler is already restored there). These are synthesized
`HandlerSave` / `HandlerArm` / `HandlerRestore` / `HandlerReraise` statements with bespoke
codegen. **Not yet supported**: a `TRY` that yields while itself nested inside another
yielding `TRY` (rejected with a clear diagnostic).

## Generated-name safety

Every compiler-synthesized name that shares the user's namespace is prefixed so it can
never collide with source identifiers. A user identifier must START with an ASCII letter
(`Lexer.IsIdentifierStart`), so:
- variable/field-namespace names (property backing fields `$Prop`, coroutine state fields
  `$state`/`$current`, ...) use the leading `$` prefix (`Binder.GeneratedPrefix`) — untypeable;
- procedure-namespace names (`Type.Method`, `Type.get_P`) embed a `.` — also untypeable as one
  identifier.

User-facing keywords inside member/property bodies (`THIS`, `VALUE`, `FIELD`) are deliberately
ordinary letter identifiers — they are written by the programmer, not hidden sugar.

## Future work: decompiling weaved code

A pretty-printer that re-emits BASIC source from the fully-bound, desugared, optimized AST/IR
(after member lifting, property/coroutine weaving, and the optimizer passes) would let us read
what the compiler actually produced "without the magic" — invaluable for debugging the lowerings
and the optimizer. Tracked as a later task (see memory: pb36-source-writer).

## 3. `FOR EACH` over a generator

`FOR EACH` is currently parse-time sugar to a counted `ForStmt`, which only fits
arrays/ranges. Introduce a real `ForEachStmt` AST node lowered by the **binder**
per the collection's static type:
- array / `[lo..hi]` range → today's counted loop (unchanged),
- generator/enumerator type → `e = coll : WHILE e.MoveNext : v = e.Current : <body> : WEND`.

Because the enumerator is an ordinary UDT with lifted members (§1), `FOR EACH` and
manual `.MoveNext`/`.Current` use the identical machinery.
