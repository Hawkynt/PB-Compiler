# O0283 — Context-sensitive cloning

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Whole-program |
| **Related** | [O0160](O0160-call-site-cloning.md), [O0158](O0158-interprocedural-range-propagation.md), [O0269](O0269-profile-guided-inlining.md) |

## The idea

Interprocedural facts are **joined** over all callers, so one imprecise caller
destroys the precision for everybody. Cloning a procedure for an important
caller keeps that caller's facts intact.

Where [O0160](O0160-call-site-cloning.md) clones by a *property* (range,
alignment, aliasing), this clones by *caller identity* — the distinction that
matters when a hot caller and a cold one disagree about everything.

## Applies to

```basic
SUB Draw(BYVAL x%, BYVAL y%)
  ...
END SUB

' hot caller, always in-bounds coordinates
FOR i% = 0 TO 319 : CALL Draw(i%, 100) : NEXT
' cold caller, arbitrary coordinates
CALL Draw(userX%, userY%)
```

## What it needs

- A **budget** — every clone is a copy of the body, so this is the
  code-size/speed trade in its purest form, and it wants profile weights to
  spend it well ([O0269](O0269-profile-guided-inlining.md)).
- Call-site rebinding, which the compiler already does for inlining and for
  register-parameter conversion.
