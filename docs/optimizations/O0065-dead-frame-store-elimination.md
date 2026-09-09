# O0065 — Dead frame-store elimination

| | |
|---|---|
| **Status** | 🟡 Partial — IR whole-function private-frame stores are eliminated; the late assembler also removes guaranteed straight-line overwrites; post-selection/register-allocation spill death still needs backend-private slot metadata |
| **Stage** | IR mid-end + assembler fallback |
| **IR** | ✅ `Ir/Passes/DeadStoreElim.cs` |
| **Related** | [O0034](O0034-redundant-load-elimination.md), [O0048](O0048-ir-dead-store-elimination.md), [O0060](O0060-memory-ssa.md) |

## The idea

Once the last reader of a compiler temporary disappears, a store into that frame
cell has no observable effect. The important part is proving that "no reader"
really means no reader: ordinary BASIC locals can escape through BYREF/pointers,
and machine-level frame slots may still be referenced by instruction forms a
narrow recorder did not describe.

The IR can make the useful proof directly. Compiler-private frame objects are
explicit `IrAlloca` values, every derived address is visible in the use graph,
and the shared alias analysis knows constant GEP offsets and access widths.

## IR mid-end implementation

`DeadStoreElim` now performs O0065 before its existing [O0048](O0048-ir-dead-store-elimination.md)
intra-block overwrite scan.

An `IrAlloca` qualifies for the whole-function rule only when:

- it is compiler-generated storage (`IsSourceVariable == false`);
- its pointer use graph contains only loads, stores through the pointer, and GEPs;
- no derived pointer is passed to a call, returned, stored as a value, cast, merged,
  or otherwise escapes the explicit memory graph.

For every store into such an object, the pass asks whether **any** load derived
from the same alloca may alias the bytes written by the store. If none can, the
store is dead and is erased.

This is deliberately a whole-function object-lifetime proof rather than a
straight-line scan. A branch, loop back-edge, or unrelated call is not a barrier:
if the private address never escaped and no IR load can alias the stored bytes,
there is no path on which the value can be observed. Conversely, passing the
slot or any derived address to a call immediately declines the proof.

The check is byte-range aware through `IrAliasAnalysis`, so a read of bytes 2..3
in a private four-byte object does not keep an unrelated store to bytes 0..1
alive. Dynamic/unknown offsets conservatively may-alias and keep the store.

`Mem2Reg` already removes the easiest direct scalar stack traffic. O0065 matters
for private frame objects that remain memory — for example GEP-addressed,
partially accessed, or otherwise non-promotable compiler scaffolding.

### Example

```text
entry:
    %tmp = alloca i32
    store i16 %x, %tmp+0       ; no load can observe bytes 0..1
    br %cond, left, right
left:
    call @unrelated()          ; %tmp was never passed to it
    br done
right:
    br done
done:
    %y = load i16, %tmp+2      ; disjoint range
    ret %y
```

becomes:

```text
entry:
    %tmp = alloca i32
    br %cond, left, right
left:
    call @unrelated()
    br done
right:
    br done
done:
    %y = load i16, %tmp+2
    ret %y
```

No MemorySSA construction is required for this slice because the stronger
nonescape property closes the observation set: the pass can enumerate every
possible read of the private object directly.

## Assembler fallback

The direct emitter still has a conservative late form that composes after
[O0034](O0034-redundant-load-elimination.md). It removes an older plain full-word
frame store when all of these hold:

- it is `MOV [BP+disp], r16` or `MOV WORD PTR [BP+disp], imm16`;
- a later plain full-word `MOV` reaches the **same** BP-relative cell by uninterrupted
  straight-line fall-through and completely overwrites it;
- no surviving memory read that may alias the cell occurs first;
- no conditional branch occurs between the stores — its taken path could skip the
  replacement, leaving the older value observable;
- no label appears at the first store or between the stores;
- there is no unrecorded gap such as a call or inline-asm instruction;
- a partial write, read-modify-write operation, or unknown alias declines the proof.

The branch rule is intentionally stricter than O0034 forwarding. A forwarded load
in the fall-through path of a conditional branch is reached only after the older
store and can safely use its register value. Dead-store elimination asks whether
the **later overwrite is guaranteed to execute**. A conditional branch makes that
false, so it terminates the assembler DSE scan.

Thus:

```asm
    mov     [bp-8], ax
    mov     dx, [bp-8]       ; O0034 -> mov dx,ax
    mov     [bp-8], cx       ; complete overwrite
```

becomes:

```asm
    mov     dx, ax
    mov     [bp-8], cx
```

The implementation lives in `Assembler.LoadForward.cs`, sharing the same
`SchedInstr` memory identity and `MemMayAlias` rules as forwarding and scheduling.
All byte cuts use the common `RemoveBytes` path, so labels, fixups, relocations and
instruction-record offsets remain synchronized.

## Remaining backend work

The IR proof does not retroactively cover spill stores introduced **after** the
middle end by register allocation/instruction selection. Likewise, the direct
emitter cannot infer that a final machine-level frame store is dead merely because
no later *recorded* instruction reads it: `LEA`, `PUSH mem`, read-modify-write forms,
indirect operations and other unrecorded instructions may still observe the cell.

Finishing that backend-only form needs either:

- complete memory def/use recording for every instruction shape that can observe a
  frame cell, or
- explicit backend metadata identifying private spill/temp slots whose addresses
  cannot escape.

Until then, the assembler fallback keeps its conservative control-flow and record-gap
barriers; the IR path does not need them for non-escaping private allocas.

## References

The proof shape follows the standard dead-store conditions used by LLVM DSE:
identify memory definitions, reject intervening observable reads, require a valid
kill/death relation, and rely on alias/object-lifetime information rather than
assuming unknown memory is invisible. LLVM's implementation is Apache-2.0 WITH
LLVM-exception; this implementation is independent and uses only those public
algorithmic requirements and PB-Compiler's existing IR/alias abstractions.
