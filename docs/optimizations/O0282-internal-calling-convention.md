# O0282 — Internal calling-convention specialization

| | |
|---|---|
| **Status** | ⬜ Planned (the word-sized `BYVAL` case is implemented — [O0021](O0021-register-parameters.md)) |
| **Stage** | Whole-program + emitter |
| **Related** | [O0021](O0021-register-parameters.md), [O0069](O0069-dead-parameter-elimination.md), [O0169](O0169-returned-condition-propagation.md), [O0070](O0070-leaf-frame-elision.md) |

## The idea

When the compiler owns every call site, the calling convention is an
implementation detail it may choose **per procedure**:

- leading word-sized `BYVAL` scalars in registers — done
  ([O0021](O0021-register-parameters.md));
- LONG, float and pointer arguments in register **pairs**;
- unused arguments dropped ([O0069](O0069-dead-parameter-elimination.md));
- **multiple** return values in registers (a natural fit for `pb36` tuples);
- a Boolean result returned **in the flags**
  ([O0169](O0169-returned-condition-propagation.md));
- BYREF collapsing to direct access after inlining.

## Applies to

```basic
FUNCTION DivMod(BYVAL a&, BYVAL b&) AS (LONG, LONG)     ' pb36 tuple return
DIM q&, r&
q&, r& = DivMod(x&, y&)
```

— the two results want EAX and EDX, not a struct write through a hidden pointer.

## What it needs

- The same ownership proof [O0021](O0021-register-parameters.md) uses: every
  call site visible, no address taken, nothing linked that could call by name.
- A per-procedure convention record threaded through both the call site and the
  frame layout — the mechanism exists (`ConventionRegisters`), the policy does
  not.
