# Runtime-aware optimization: inline ASM effects and store fusion

## First native milestone: recognized private MOV-immediate stores

The current x86-16 lowering carries each `!` statement as `IrInlineAsm` with names bound to IR pointers, but marks its containing function `HasInlineAsm`. The usual scalar optimizer skips the function because raw instructions may read/write registers, flags, memory, machine stack, ports or labels which an ordinary `IrStore` cannot represent.

`InlineAsmPureStorePromotion` runs **before** native SSA construction, and requires a whole-function proof: every inline assembly block is routable and contains only the exact `MOV <bound private 16-bit scalar>, <decimal integer>` spelling, with optionally assembler comments. Each recognized instruction has precisely one compiler-owned local storage write, and x86 `MOV r/m16,imm16` changes neither general registers nor EFLAGS. All blocks are lowered to ordinary IR stores together and `HasInlineAsm` is cleared. The existing Mem2Reg/SCCP/GVN/DSE can then fold values across BASIC and formerly opaque ASM statements.

If **any** instruction is not covered, the pass changes **none** of the function's ASM. This includes register and flag operations, memory-indirect/segment accesses, port I/O, branches, labels, runtime-owned cells, shared globals, byte/dword operands, unusual immediate forms and source/error-handler boundaries. Unrecognized instructions continue through the original native assembler path; there is no heuristic alias inference.

This narrow implementation intentionally does not fuse video writes. Named local stores are ordinary RAM, not screen memory. `MOV ES:[DI],AL`, `REP STOSx` and any VGA-port sequence must be modeled with explicit CPU-state, segment, memory-provenance and side-effect contracts before participating in the Mode 13h batched graphics writer.

## Next steps

1. Expose individual ASM effects as structured native IR descriptors with explicit **input/output registers**, flag reads/writes, memory provenance (RAM, certified packed VRAM, unknown MMIO), CPU generation and may-trap behavior.
2. Treat any unrecognized ASM operation as a complete barrier, but permit optimizations **within proven pure regions** of a mixed block after checking label targets and fallthrough.
3. Analyze `MOV` byte writes to a proven A000h Mode 13h scanline with no intervening observable read, interrupt-sensitive boundary or VGA-register change. Fuse with the existing PSET/POKE graphics store operation only when the hardware contract permits the changed width and count of writes.
4. Recognize `REP STOSW/STOSD` as specialized span operations only when direction flag, `ES`, `DI`, `CX`, source pattern, register effects and address/row boundaries are proven.
5. Add 8086, 286 and 386 instruction-shape tests, emulator framebuffer equivalence, genuine DOSBox performance profiles, and negative tests for port/MMIO accesses, aliases, self-modifying code, jump-ins, flags and interrupts.

## Sources and licensing

- [LLVM Language Reference: Inline Assembler Expressions](https://llvm.org/docs/LangRef.html#inline-assembler-expressions): operand constraints and memory clobber contracts are authoritative concepts, not copied code
- Intel x86 MOV immediate-to-memory encoding/flag effects (architectural ISA facts), referenced via [MOV reference](https://asmdude.github.io/x86doc/html/MOV.html)
- Existing project `IrInlineAsm`, `InlineAsmLifting`, `IrEffects`, `IrMiddleEndPipeline`, `IrScreenModeAnalysis` and `PixelRuntimeStoreLowering`

Implementation is independent C# against project-owned APIs; no external code copied, no new dependencies.
