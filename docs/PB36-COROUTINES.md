# PB 3.6 — coroutines via `YIELD`

Design sketch for a coroutine facility in `--dialect pb36`: a SUB/FUNCTION that
can **suspend** mid-execution, hand a value back to whoever resumed it, and later
**continue** from exactly that point with all of its locals intact.

This is a pure pb36 extension. Genuine PowerBASIC 3.50 — the differential oracle
— has no `YIELD`, so a coroutine program can never be byte-verified against PBC.
Like the other pb36-only surface (shift/rotate operators, lambdas, `FOR EACH`),
it is gated to pb36 and rejected in every older dialect with the standard
`requires PowerBASIC 3.6` message; pb36 execution tests in DOSBox are the only
available verification gate for the eventual code generator.

Status: **front-end prototype only.** Lexer/parser/AST and the dialect gate are
in place and unit-tested; binding type-checks the yielded value. No code is
generated yet — `YieldStmt` falls through to the code generator's `Unsupported`
path, which is intentional until the strategy below is built out.

---

## 1. Surface syntax

### 1.1 Declaring a coroutine

A coroutine is an ordinary SUB or FUNCTION marked with a leading `COROUTINE`
modifier. The marker is required (rather than inferring "is a coroutine" from the
presence of `YIELD`) so the property is visible at the declaration and at every
`DECLARE`, and so a non-coroutine proc that happens to contain the word `YIELD`
is never silently reinterpreted:

```basic
COROUTINE FUNCTION Counter(BYVAL first AS LONG, BYVAL last AS LONG) AS LONG
  DIM i AS LONG
  FOR i = first TO last
    YIELD i            ' suspend, surfacing i; resume continues the FOR
  NEXT
END FUNCTION
```

A coroutine FUNCTION's return type is the **element type** surfaced by `YIELD`
(here `LONG`). A coroutine SUB yields nothing typed and is consumed only for its
side effects between suspensions.

`YIELD <expression>` is the suspension point. Its expression must be assignable to
the coroutine's return type. `YIELD` outside a `COROUTINE` proc is a bind-time
error.

### 1.2 Consuming a coroutine

Two consumption forms, both idiomatic to the existing language:

**(a) `FOR EACH` drive (preferred).** pb36 already has `FOR EACH ... IN ...`
(see `LanguageFeature.ForEach`). Extend its `IN` source to accept a coroutine
call. Each pass resumes the coroutine to its next `YIELD`; the loop ends when the
coroutine runs off its `END FUNCTION`:

```basic
DIM n AS LONG
FOR EACH n IN Counter(1, 5)
  PRINT n                       ' 1 2 3 4 5
NEXT
```

**(b) Explicit handle + `RESUME`-style pull.** For producers consumed
irregularly, a coroutine call in a value context yields a *coroutine handle*
(a fat value: a far pointer to the saved context — see §3). A new intrinsic
advances it:

```basic
DIM g AS COROUTINE              ' opaque handle type (DWORD-sized)
DIM v AS LONG
g = Counter(1, 5)              ' create, do not start
DO WHILE RESUMING(g)          ' advance to next YIELD; FALSE once exhausted
  v = YIELDED(g)              ' the value from the last YIELD
  PRINT v
LOOP
```

`RESUMING(g)` resumes `g` and returns non-zero while a value was produced;
`YIELDED(g)` reads the most recently surfaced value. Form (a) is sugar over (b).

The prototype in this branch parses only the **`YIELD <expression>` statement**;
`COROUTINE`, the handle type, and the consumption intrinsics are specified here
but deferred to the implementation phases.

---

## 2. Semantics

- **First call does not run the body.** Calling a coroutine constructs a context
  and returns a handle in the suspended-at-entry state. The body advances only on
  the first resume. (This matches generator semantics and keeps `FOR EACH`'s
  pre-test loop natural.)
- **Suspend.** `YIELD e` evaluates `e`, stores it where the resumer can read it,
  saves the coroutine's execution state, and transfers control back to the
  resumer.
- **Resume.** Control re-enters immediately after the last executed `YIELD`, with
  every local, the FOR/DO loop state, and the instruction pointer restored.
- **Completion.** Falling off the end (or `EXIT FUNCTION`/`EXIT SUB`) marks the
  handle exhausted: `RESUMING` returns false and `FOR EACH` stops. Resuming an
  exhausted handle is a no-op returning false.
- **Local state preservation** is the whole point: locals live in the
  coroutine's own frame, which persists between suspensions (it is *not* torn
  down on `YIELD` — see §3).
- **Re-entrancy.** Each call to a coroutine creates an independent context, so
  the same coroutine can have many live instances; a coroutine may itself drive
  other coroutines.
- **Lifetime.** A handle's context must outlive the last resume. For the explicit
  form the context is heap-allocated and freed when the handle goes out of scope
  or is exhausted; for the `FOR EACH` form the loop owns it and frees on exit.

---

## 3. Compilation strategy for 16-bit x86

Two classic implementations were weighed against the *actual* shape of this
compiler (recursive-descent parser → AST → binder → `CodeGenerator` emitting
real x86; frame model in `CodeGenerator.Procs.cs` / `CodeGenerator.cs`).

### 3.1 Option A — state-machine / CPS transform (rejected for now)

Rewrite the coroutine body into a switch over a saved "resume state", hoisting
every local that is live across a `YIELD` into a heap record, and turning each
`YIELD` into "store state, return". This is how C#/Roslyn lowers iterators.

Why it is hard *here*:

- It is an **AST-to-AST control-flow transform**, but the AST mixes structured
  blocks (`FOR`, `DO`, `SELECT`, `IF`, `TRY`) with unstructured `GOTO`/`GOSUB`
  and label statements. A correct transform must reconstitute arbitrary control
  flow as a resumable state machine — essentially a full CFG-relooper. The
  compiler has a CFG only under the optional SSA path (`CodeGen/Ssa`), not on the
  mainline AST→asm route, so this would be sizeable new infrastructure.
- Liveness across suspensions, the closure-style hoisting of locals, and
  interaction with `ON ERROR`/`RESUME` and `GOSUB` return stacks make it
  error-prone.

It is the more portable technique (it would survive a future non-x86 backend via
the IR), but it does not fit the current mainline. Keep it as a long-term option
should coroutines need to ride the typed IR.

### 3.2 Option B — separate-stack fiber / context switch (**recommended**)

Give each coroutine instance its **own stack**, and implement `YIELD`/resume as a
register-level stack switch. The body is emitted as an *ordinary* SUB/FUNCTION —
same prologue/epilogue, same BP-relative locals via the existing `LayoutFrame`
(`CodeGenerator.Procs.cs:52`) and `BeginFrame` (`CodeGenerator.cs:649`) — and its
frame simply *lives on the coroutine's stack* across suspensions instead of being
unwound. No body rewrite, no liveness analysis, no relooper. This reuses almost
all of the existing procedure machinery and is the natural fit for the AST→asm
pipeline.

#### Context record

A coroutine handle is a far pointer to a heap-allocated context:

```
CoroCtx (in the coroutine's own stack block, header at the base):
  +0   resumer_sp    WORD   ; resumer's SP, saved on resume / restored on yield
  +2   resumer_bp    WORD
  +4   coro_sp       WORD   ; coroutine's SP,  saved on yield  / restored on resume
  +6   coro_bp       WORD
  +8   state         WORD   ; 0 = at-entry (not started), 1 = suspended, 2 = done
  +10  value_lo      WORD   ; last YIELDed value (widened to the proc's return slot)
  +12  value_hi      WORD
  +14  stack_seg     WORD   ; segment/handle of the allocated stack block (for free)
  ...  (coroutine's growing stack grows downward from the top of the block)
```

The model is single-threaded cooperative: only one of {resumer, coroutine} runs
at a time, so a single saved (SP,BP) pair per side is sufficient. SS stays the
program's single stack segment in the small/compact memory model the compiler
targets, so the stacks are distinct *regions* of the one stack segment (a block
carved from the runtime heap), and a switch is just an `SP`/`BP` reload — no SS
reload, no far stack. (If a future large/huge model is supported, the switch must
also swap SS; the context record already reserves `stack_seg` for that.)

#### `YIELD e` — emitted inline at each suspension point

```asm
        ; ... evaluate e into AX[:DX] as usual ...
        mov   bx, [bp + ctx]        ; ctx = hidden first parameter (far/near ptr to CoroCtx)
        mov   [bx + 10], ax         ; value_lo
        mov   [bx + 12], dx         ; value_hi (for LONG; omitted for INTEGER)
        mov   word ptr [bx + 8], 1  ; state = suspended
        mov   [bx + 2], sp          ; (resumer_sp/bp were stored by the resumer; we save ours)
        mov   [bx + 4], sp          ; coro_sp  <- our SP
        mov   [bx + 6], bp          ; coro_bp  <- our BP
        mov   sp, [bx + 0]          ; restore resumer SP
        mov   bp, [bx + 2]          ; restore resumer BP
        ret                          ; returns into the resumer right after its RESUMING call
yield_resume_N:                      ; resume re-enters here
        ; locals are exactly as they were — same SS, same BP-frame
```

The trick: the coroutine's frame is never torn down at `YIELD`. We switch the
hardware stack to the resumer's, so the coroutine's frame sits dormant until the
next resume reloads `coro_sp`/`coro_bp` and jumps to `yield_resume_N`.

#### Resume (`RESUMING(g)`) — runtime helper

```asm
        ; bx = ctx
        cmp   word ptr [bx + 8], 2   ; done?
        je    .exhausted
        mov   [bx + 0], sp           ; save resumer SP/BP into ctx
        mov   [bx + 2], bp
        cmp   word ptr [bx + 8], 0   ; at-entry (never started)?
        jne   .resume_suspended
        ; first resume: set SP to top of the coro stack block, BP=0, call the body
        mov   sp, <top of stack block>
        xor   bp, bp
        push  bx                      ; pass ctx as the hidden parameter
        call  Coro_Body               ; runs until its first YIELD (which does the switch back)
.resume_suspended:
        mov   sp, [bx + 4]            ; restore coro SP
        mov   bp, [bx + 6]            ; restore coro BP
        jmp   <saved resume IP>       ; continue after the last YIELD
```

(The "saved resume IP" is the simplest variant: each `YIELD` site stores its own
`yield_resume_N` label address into the context, so resume is an indirect jump.
Equivalently, the body can keep its own resume dispatch — but the IP-in-context
approach keeps the body a straight-line emission with no added switch.)

When the body falls off the end, its epilogue sets `state = 2` and switches back
to the resumer one last time; `RESUMING` then returns false.

#### Why this fits

- **Reuses the frame model unchanged.** Prologue/epilogue, BP-relative locals,
  parameter passing, string cleanup — all emitted exactly as today. A coroutine
  is "a normal proc whose stack we keep around".
- **No control-flow rewrite.** `FOR`, `DO`, `GOSUB`, nested calls inside the
  coroutine all just work, because they run on the coroutine's own stack.
- **Small, local codegen.** Each `YIELD` is a fixed instruction sequence; resume
  is one runtime helper. The only new allocation is the per-instance stack block.

#### Hard parts (must be handled before this ships)

- **Stack sizing.** Each coroutine instance needs a stack big enough for its
  deepest call chain. Honour `$STACK`; expose a per-coroutine size (e.g.
  `COROUTINE(2048) FUNCTION ...`); detect overflow with the same
  `CMP SP,[rt_stackmin]` guard the existing prologue uses
  (`CodeGenerator.Procs.cs:112`), but against the *coroutine's* limit.
- **`ON ERROR` / `RESUME` and the GOSUB return stack.** The error-handler
  context (`rt_onerr`, `rt_onerr_bp`, `rt_onerr_sp`) is already saved/restored
  around procedures (`CodeGenerator.Procs.cs:188`). The stack switch must
  save/restore these as part of the context so a fault inside a suspended
  coroutine resolves against *its* handler, not the resumer's. `RESUME` /
  `RESUME NEXT` semantics across a `YIELD` boundary must be defined (initial
  rule: an unhandled error in a coroutine propagates to the resumer with the
  handle marked done).
- **Lifetime / leaks.** The heap stack block must be freed on exhaustion and on
  early `FOR EACH` exit (`EXIT FOR`, error unwind). The `FOR EACH` lowering owns
  this; the explicit-handle form frees at end of scope (tie into the existing
  local string/closure cleanup pass in the epilogue).
- **Re-entrancy and nesting** are naturally supported (independent contexts) but
  need tests: coroutine driving a coroutine, recursive coroutine.
- **Interaction with `$CPU 80386` 32-bit codegen.** When the coroutine body uses
  32-bit registers/`ESP`, the saved SP/BP must be the 32-bit forms; reserve
  DWORD slots in the context under that mode.

---

## 4. Phased implementation plan

**Phase 0 — front-end prototype (this branch).**
`YIELD` token path, `YieldStmt` AST node, pb36 gate, binder type-check of the
value, unit tests. No codegen. *Done.*

**Phase 1 — declaration surface + binder model.**
Parse the `COROUTINE` modifier on SUB/FUNCTION/DECLARE; mark `ProcedureSymbol`
as a coroutine; bind-time errors for `YIELD` outside a coroutine and for a
coroutine whose return type does not match its yields. Add the `COROUTINE`
handle type to the type system. Front-end + binder unit tests.

**Phase 2 — runtime context + stack switch helpers.**
Implement `CoroCtx`, the stack-block allocator/free (off the runtime heap), and
the `RESUMING`/`YIELDED` runtime helpers (or inline sequences). Pb36 DOSBox
execution tests: a hand-written single-`YIELD` coroutine driven by the explicit
handle form, checked against a golden transcript.

**Phase 3 — `YIELD` codegen + body emission.**
Emit the suspension sequence and the resume re-entry label per `YIELD`; emit the
coroutine body as a normal proc that takes the hidden `ctx` parameter; wire the
completion epilogue. Execution tests: multi-`YIELD`, FOR/DO across suspensions,
nested calls inside the coroutine.

**Phase 4 — consumption.**
`FOR EACH ... IN <coroutine call>` lowering (resume/test/read/free); the explicit
`g = Coro(...)` handle form. Execution tests for both, plus early-exit and
exhaustion cleanup.

**Phase 5 — robustness.**
`ON ERROR`/`RESUME` across suspensions, stack-overflow detection per instance,
re-entrancy/recursion, `$STACK`/`COROUTINE(size)` sizing, 32-bit (`$CPU 80386`)
context slots. Stress execution tests.

Each phase keeps the pb35 differential harness green (coroutine surface is
pb36-only, so it never reaches the oracle path) and adds pb36 execution tests as
its verification gate.
