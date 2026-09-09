# O0284 — Semantic function merging

| | |
|---|---|
| **Status** | 🟢 Implemented — whole-module IR plus ABI-preserving native x86 entry thunks under `$OPTIMIZE SIZE`, including compatible varying call targets |
| **Stage** | Whole-module IR under SIZE; native x86 routing after the normal IR cleanup pipeline |
| **IR** | ✅ `PowerBasic.Compiler/Ir/Passes/SemanticFunctionMerging.cs` and `SemanticFunctionMerging.Thunks.cs` |
| **Native x86** | ✅ `PowerBasic.Compiler/CodeGen/CodeGenerator.SemanticMerge.cs` — private merged helpers behind source-ABI entry thunks |
| **Verified by** | `SemanticFunctionMergingTests`, `SemanticFunctionMergingThunkTests`, `BackendSemanticFunctionMergingTests` |
| **Related** | [O0040](O0040-identical-code-folding.md), [O0391](O0391-cold-code-deduplication.md), [P0006](P0006-header-squeeze.md) |

## The idea

O0040 merges procedures whose emitted bytes are identical. Semantic merging goes one step further: two
internal procedures whose IR is structurally congruent except for one constant, one field offset, or one
direct call target become one procedure body with one extra context parameter.

A pair such as

```basic
FUNCTION A(BYVAL x AS LONG) AS LONG
  FUNCTION = x + 3
END FUNCTION

FUNCTION B(BYVAL x AS LONG) AS LONG
  FUNCTION = x + 7
END FUNCTION
```

can therefore become the IR equivalent of

```text
merge(x, context) = x + context
A-call(x) -> merge(x, 3)
B-call(x) -> merge(x, 7)
```

The same mechanism covers a GEP field/byte offset because that offset is an operand, and a differing
call target because the IR models the callee as operand zero of `IrCall`.

## Whole-module IR implementation

`SemanticFunctionMerging.Run` performs a conservative structural comparison over candidate functions:

- return type, parameter types, block count, instruction count, control-flow edges, fast-math flags and
  instruction-specific metadata must match exactly;
- parameters, blocks and SSA instructions are mapped positionally, so ordinary local values and
  recursive self-references compare as corresponding values rather than by object identity;
- exactly one operand may differ, and it must be materializable at a caller: an integer/float/null
  constant or a global address. A varying callee must be two direct functions with the same signature;
- functions with error handlers, inline asm, varargs, `NOINLINE`, block-address operands or escaped
  procedure addresses are rejected in the signature-changing form;
- every function use must be a direct call owned by the module. Changing an internal signature without
  that proof would leave an unseen caller using the old ABI;
- self-recursive calls in the surviving body forward the new context parameter. They must not reload
  the representative's original constant, or a call redirected from another variant would change
  specialization after the first recursion.

Candidates that differ at the same operand location are merged as one group rather than pair-by-pair,
so three or more monomorphized variants acquire one parameter once. The pass returns the number of
eliminated bodies; exact-identical native bodies are still independently eligible for O0040.

## ABI-preserving entry-thunk form

The native x86 backend is hybrid: some procedures may be routed through SSA/machine IR while neighbouring
procedures are still emitted by the legacy generator. Changing the public signature of a routed function
would therefore be unsound because a direct-emitted caller could keep using the old stack layout.

`SemanticFunctionMerging.RunWithEntryThunks` solves that without requiring the whole program to route:

1. the source-visible procedures keep their original names and parameter lists;
2. one private `__o0284_*_merge` helper receives the original arguments plus the varying context value;
3. each original body becomes a tiny entry thunk that supplies its former specialization value and calls
   the helper;
4. recursive calls in the cloned helper forward the current context, so recursion cannot switch back to
   the representative specialization;
5. taking the address of an original procedure remains valid because that address still names an entry
   with the source ABI.

This is the same ABI shape used by LLVM GlobalMergeFunctions: the parameterized merged instance is private
and the original functions remain thunks supplying the differing contexts. The PB-Compiler implementation
is clean-room C# built against its own IR and backend abstractions.

## Native x86 routing

`CodeGenerator.SemanticMerge` activates the thunk form only when all of these are true:

- the experimental native x86 backend is enabled;
- optimization is enabled;
- `$OPTIMIZE SIZE` is active;
- the source procedure already passes the ordinary native-backend ABI/filter checks.

The synthetic helper is then independently checked for parameter layout, external/runtime callees, global
addressability, instruction selection and register allocation. It participates in the same routed-callee
closure as source procedures. If the helper or one of its required callees cannot remain routed, its entry
thunks are stranded and fall back rather than leaving a half-converted ABI in the image.

A varying direct callee is allowed when both alternatives are defined source procedures using the ordinary
near stack ABI. The thunk passes the selected procedure's 16-bit code offset as its context; the helper stages
that pointer into `BX` and the selector emits an 8086 near-indirect `CALL r/m16` (`FF /2`). Runtime `rt_*`
entries are excluded because their argument placement is selected by name from `RuntimeAbi`, and PB36
`ProcPtrType` fat closures remain on their separate far-call/environment ABI rather than being mistaken for
a near pointer.

The helper uses the private BASIC near-stack convention. `CodeGenerator` computes its parameter offsets
from IR types, emits it once, and resolves calls to its synthetic label through the normal machine emitter.
No fake `ProcedureSymbol` is introduced and no source ABI metadata is rewritten.

## Profitability and gate

This is intentionally a `$OPTIMIZE SIZE` transformation. The whole-module standard pipeline exposes the
signature-changing form through `IrPassManager.Standard(optimizeForSize: true)` and runs it after local
simplification and interprocedural constant propagation have maximized structural congruence.

The target-neutral whole-module cost model requires at least eight instructions in the representative and
estimates

```text
saved = body-instructions * eliminated-bodies
        - visible-calls
        - one context access in the survivor
```

The entry-thunk form additionally charges for the retained public thunks and context materialization, so a
marginal pair is declined instead of growing native code merely to demonstrate that merging was possible.

## Native varying call targets

Whole-module IR can parameterize operand zero of an `IrCall`, so two otherwise-congruent functions that
call different compatible direct targets are merged exactly like constant/offset variants. Native x86 now
supports the corresponding near-indirect call as well:

1. a direct `IrFunction` used as the merge context materializes as a 16-bit `CodeOffset`;
2. the public thunk passes that offset through the ordinary BASIC stack ABI without relying on 80186
   `PUSH imm16` — it uses `MOV reg, OFFSET target` plus `PUSH reg`, which remains valid on the 8086;
3. the helper's pointer parameter is staged into `BX` immediately before the call;
4. `MachineEmitter` emits `CALL BX`, the `FF /2` near-indirect form defined by the x86 ISA;
5. the call still declares the full caller-saved register set, so allocation/spilling obeys the same
   liveness rule as a direct call.

This is intentionally a **near IR code-pointer** facility, not an implementation of PB36's source-level
`ProcPtrType`. That source type is an eight-byte fat closure containing a far code pointer and environment;
its existing legacy lowering remains the correct ABI for lambda/delegate calls.

## Validation

`BackendSemanticFunctionMergingTests` contains end-to-end native differential gates for both literal-context
and varying-call-target merges. Each compiles the same `$OPTIMIZE SIZE` program twice, once through the
direct emitter and once with the experimental x86 backend, and asserts that:

- both source entry thunks remain routed;
- exactly one private O0284 helper is present;
- 8086 execution produces the same output and exit code as the direct emitter.

The IR suites separately cover signature-changing merges, ABI-preserving escaped addresses, recursive
context forwarding, backend candidate filtering, and explicit call-target filtering. Backend call-routing
coverage additionally checks that the selected indirect call reaches the concrete `FF /2` encoding.
