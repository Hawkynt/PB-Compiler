# O0295 — String result-buffer forwarding

| | |
|---|---|
| **Status** | ✅ Implemented (owned-handle SSA forwarding) |
| **Stage** | Whole-program + emitter |
| **IR** | `IrLowering` + `Mem2Reg` |
| **Related** | [O0102](O0102-return-value-forwarding.md), [O0286](O0286-allocation-elimination.md), [O0282](O0282-internal-calling-convention.md) |

## The idea

A string-returning `FUNCTION` builds its result in a fresh allocation, returns
the handle, and the caller assigns it — freeing whatever was there. A conventional
result-slot optimization would instead let the caller provide the destination so
the callee can write directly into it.

```basic
FUNCTION Pad$(s$, BYVAL n%)
  Pad$ = s$ + SPACE$(n% - LEN(s$))
END FUNCTION

DIM dst$
dst$ = Pad$(src$, 40)
```

## IR implementation

The portable IR does not need an sret-style hidden destination to obtain the
important part of this optimization. Dynamic strings are represented by their
**owned runtime handle**, and a string-returning function returns that handle as a
`ptr`. The caller evaluates the right-hand side first, frees the old destination,
and stores the returned owned handle directly. It does **not** call `rt_str_dup`
on the function result, so there is no second heap allocation or string copy at
the call boundary.

The syntactic result variable and ordinary caller string variables initially have
handle cells because that makes lowering and ownership explicit. `Mem2Reg` removes
those cells when they do not escape, leaving the producer value flowing through
SSA into the consumer. This is the target-neutral equivalent of forwarding the
result storage, but without taking the address of the caller variable.

Passing a hidden destination pointer here would be a pessimization: it would make
an otherwise promotable caller variable address-taken merely to replace an owned
handle transfer that is already copy-free. It would also introduce a new aliasing
surface between the callee and the caller's storage for no allocation benefit.

The regression fixture `StringResultBufferForwardingTests` therefore pins the
actual O0295 contract:

- a string function result is an owned `ptr` consumed directly by the caller's
  assignment, with no `rt_str_dup` handoff;
- the function-result and caller-destination handle cells disappear under the
  normal optimized pipeline rather than becoming an sret buffer;
- when the destination is also read as an argument, that read/duplicate happens
  before the function call and the destination's old handle is freed only after
  the call, preserving the required alias-safe evaluation order.

## Why this differs from classic return-slot optimization

GCC documents return-slot optimization for aggregate values as passing the
address of the assignment destination to the callee so the aggregate copy can be
avoided. That is the right model when the returned value itself is a buffer. PB's
IR string value is instead an owning handle to the buffer; moving that handle is
already the zero-copy operation. The implementation follows the same objective
— remove the redundant result handoff — using the representation the IR actually
has rather than importing an ABI convention that would make it worse.

Reference consulted for the classic transformation: GCC Internals, return-slot
optimization. No GCC or LLVM implementation code is copied or translated; the IR
behavior above is derived independently from PB's ownership semantics and the
repository's existing lowering model.
