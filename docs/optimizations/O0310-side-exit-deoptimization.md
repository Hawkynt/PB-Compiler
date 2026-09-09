# O0310 — Side exits and deoptimization

| | |
|---|---|
| **Status** | 🟨 Implemented infrastructure — one exact side exit per canonical loop version |
| **Stage** | IR middle-end |
| **IR** | ✅ `Ir/Passes/SideExitDeoptimization.cs` — clones a speculative fast loop plus only the generic remainder of the failed iteration; SSA values are the state map |
| **Verified by** | `SideExitDeoptimizationTests` |
| **Related** | [O0304](O0304-guarded-specialization.md), [O0306](O0306-loop-versioning.md), [O0308](O0308-speculative-overflow-elimination.md) |

## The idea

Enter optimized code under an assumption and leave it the moment that assumption
fails — **mid-loop**, not only at the loop entry. The important part is not the
branch. It is reconstructing the generic program state at the exact point where
execution resumes.

For PB-Compiler that does not need a backend-specific deoptimization opcode.
The IR already has explicit control flow and SSA. `SideExitDeoptimization`
therefore keeps the original loop as the generic implementation, clones one fast
version, and makes a failed fast guard enter a cloned **suffix of the current
generic iteration**. That suffix is seeded with the fast clone's SSA values.

```text
preheader -> fast.header -> ... -> fast.guard
                                  | assumption holds
                                  v
                              fast suffix -> fast.header

                                  | assumption fails
                                  v
                            deopt resume suffix
                                  |
                                  v
                           generic.header -> ...
```

After the resume suffix reaches the generic latch, the original header phis take
their next values from that suffix. All later iterations stay in the generic loop.

## Why the suffix matters

Consider:

```basic
DIM i%, s&
FOR i% = 0 TO 999
  s& = s& + 1                 ' already happened
  IF a&(i%) > 32767 THEN      ' speculative assumption fails here
    ' generic-width work
  ELSE
    ' narrow fast work
  END IF
NEXT
```

Restarting the generic loop at its header would execute `s& = s& + 1` twice for
the failing iteration. O0310 instead resumes **after the guard**. Values produced
before the guard are mapped to their fast-clone equivalents; memory side effects
already performed are simply left performed.

The same mechanism also repairs the normal loop exit: values that can escape
from either the generic or fast header are joined with exit phis, so both natural
termination and deoptimization remain valid SSA.

## Safety envelope

The helper deliberately declines rather than guesses. Today it requires:

- a single-entry, single-latch loop whose only normal exit is the header test;
- an acyclic iteration body once the latch-to-header back edge is ignored;
- a guard that dominates the latch and both of its successors;
- cloneable target-neutral IR, with no `ON ERROR`, inline assembly, address-taken
  loop blocks, body side exits, or hidden entries;
- a bounded clone cost (fast loop plus resume suffix, currently 192 instructions).

Those restrictions are about exact reconstruction, not about what assumption is
profitable. O0310 is a construction primitive: O0304/O0308/O0309 and later
profile-guided passes decide *which* fact deserves a speculative version.

## Remaining generalization

The implemented slice handles one side exit per canonical loop version. Multiple
independent guards in one fast version, arbitrary irreducible CFG regions, and
runtime/interpreter frame reconstruction are still broader deoptimization work.
They are not required for the explicit-CFG model used by the current PB-Compiler
back ends.
