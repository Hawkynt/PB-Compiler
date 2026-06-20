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

### Implementation status / plan

- **Phase 1a — DONE** (commit a2ac826): front-end. `TypeMember` AST + `TypeDecl.Members`,
  `LanguageFeature.TypeMethods` (pb36-gated), parser support inside the TYPE block.
  Binder/codegen ignore `Members`, so it is inert (no battery declares members).
- **Phase 1b — next**: bind & lower.
  - In `ScanModule`'s `TypeDecl` case, after `DefineUdt`, synthesize each member via
    `DefineProcedure(MemberProcName(...), isFunction, suffix, returnType, [THIS]+params, …, body)`.
    `THIS` = `Parameter("THIS", TypeName(None, UserTypeName: t.Name), ByVal:false)`.
  - **Resolve member calls**: `o.M(args)` parses as `IndexExpr(MemberExpr(o,"M"), args)`
    (rvalue) and, at statement level, as a dotted bare-call. When `o`'s static type is a
    UDT and a proc `Type.M` exists (vs. a field `M`), rebind to a call with `o` passed
    BYREF as arg 0. `o.P` rvalue → `Type.get_P(o)`; `o.P = x` → `Type.set_P(o, x)`.
  - **Subtlety A — FUNCTION/PROPERTY GET result name**: the body assigns `Pop = …` /
    `Size = …` (the member's simple name) but the proc is `Stack.Pop` / `Stack.get_Size`.
    The function-result binding (which matches the assignment target to the proc name)
    must accept the member's *simple* name as the result alias. SUB / PROPERTY SET have
    no result and are unaffected — build those first.
  - **Subtlety B — statement member-call**: `ParseBareCall`/`CallStmt(Name,…)` carry a
    string name, not a target expression, so a dotted `o.Push(v)` statement needs either a
    new statement shape (a member-call statement) or binder handling of the dotted name.
  - **Codegen**: passing `THIS` BYREF is the address of `o` — reuse `EmitArgumentPush`'s
    BYREF path (VARPTR of the lvalue). No new backend.
- **Phase 1c**: properties as rvalue/lvalue, indexed members, `THIS` inside expressions.

Verification: pb36-only feature with **no differential oracle** (PBC 3.50 has no
TYPE methods), so verify by *execution* (compile + run in DOSBox, compare to a hand
-computed expected) plus unit tests; the full differential harness must stay green
(the feature is inert for every existing battery).

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

**Scope of the first MVP**: yields at the top level and inside a single `FOR`/`WHILE`/`DO`
loop (the state machine can resume into one loop level). Nested/complex control flow around
a `YIELD`, or `YIELD` after `GOTO`, are rejected with a clear diagnostic and widened later.

## 3. `FOR EACH` over a generator

`FOR EACH` is currently parse-time sugar to a counted `ForStmt`, which only fits
arrays/ranges. Introduce a real `ForEachStmt` AST node lowered by the **binder**
per the collection's static type:
- array / `[lo..hi]` range → today's counted loop (unchanged),
- generator/enumerator type → `e = coll : WHILE e.MoveNext : v = e.Current : <body> : WEND`.

Because the enumerator is an ordinary UDT with lifted members (§1), `FOR EACH` and
manual `.MoveNext`/`.Current` use the identical machinery.
