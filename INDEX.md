# Codebase Index

Generated 2026-09-05 12:52 UTC by index_codebase.py.

Every symbol is one line ending in `path:line` — grep this file to
locate anything: `grep -n "symbolName" INDEX.md`. Regenerate after
adding, renaming, moving, or deleting symbols; line numbers drift
with unrelated edits, so treat them as anchors, not gospel.

727 files, 6321 symbols.

## PowerBasic.Compiler.Tests/Asm/

### AsmRegisterEffectTests.cs  `C#, 158 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/AsmRegisterEffectTests.cs:2
- class `AsmRegisterEffectTests` — Reading one inline-assembly statement's register effect out of its text — PowerBasic.Compiler.Tests/Asm/AsmRegisterEffectTests.cs:14
- class `Cells` — Answers a named identifier as code and every other as storage - the two kinds a PB inline-asm — PowerBasic.Compiler.Tests/Asm/AsmRegisterEffectTests.cs:21
- method `TryResolve` — PowerBasic.Compiler.Tests/Asm/AsmRegisterEffectTests.cs:24

### AssemblerAluTests.cs  `C#, 277 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/AssemblerAluTests.cs:2
- class `AssemblerAluTests` — PowerBasic.Compiler.Tests/Asm/AssemblerAluTests.cs:4

### AssemblerBitManipulationTests.cs  `C#, 28 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/AssemblerBitManipulationTests.cs:2
- class `AssemblerBitManipulationTests` — PowerBasic.Compiler.Tests/Asm/AssemblerBitManipulationTests.cs:4

### AssemblerBmiTests.cs  `C#, 69 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/AssemblerBmiTests.cs:2
- class `AssemblerBmiTests` — PowerBasic.Compiler.Tests/Asm/AssemblerBmiTests.cs:4

### AssemblerCryptoExtensionsTests.cs  `C#, 33 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/AssemblerCryptoExtensionsTests.cs:2
- class `AssemblerCryptoExtensionsTests` — PowerBasic.Compiler.Tests/Asm/AssemblerCryptoExtensionsTests.cs:4

### AssemblerEncodingSelectionTargetTests.cs  `C#, 60 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/AssemblerEncodingSelectionTargetTests.cs:2
- class `AssemblerEncodingSelectionTargetTests` — PowerBasic.Compiler.Tests/Asm/AssemblerEncodingSelectionTargetTests.cs:4
- method `if(haystack[i + k] != needle[k])` — PowerBasic.Compiler.Tests/Asm/AssemblerEncodingSelectionTargetTests.cs:53

### AssemblerFpuTests.cs  `C#, 346 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/AssemblerFpuTests.cs:2
- class `AssemblerFpuTests` — PowerBasic.Compiler.Tests/Asm/AssemblerFpuTests.cs:4

### AssemblerLabelAndDataTests.cs  `C#, 226 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/AssemblerLabelAndDataTests.cs:2
- class `AssemblerLabelAndDataTests` — PowerBasic.Compiler.Tests/Asm/AssemblerLabelAndDataTests.cs:4

### AssemblerModRmTests.cs  `C#, 116 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/AssemblerModRmTests.cs:2
- class `AssemblerModRmTests` — Golden-byte tests for every 16-bit ModRM addressing form (using MOV AX, mem = 8B /r). — PowerBasic.Compiler.Tests/Asm/AssemblerModRmTests.cs:6

### AssemblerMovTests.cs  `C#, 197 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/AssemblerMovTests.cs:2
- class `AssemblerMovTests` — PowerBasic.Compiler.Tests/Asm/AssemblerMovTests.cs:4

### AssemblerPeepholeTests.cs  `C#, 128 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/AssemblerPeepholeTests.cs:2
- class `AssemblerPeepholeTests` — PowerBasic.Compiler.Tests/Asm/AssemblerPeepholeTests.cs:4
- method `Emit(Assembler a)` — a branch targets the 'mov bx, ax' - folding it away would strand the jump, so it must stay — PowerBasic.Compiler.Tests/Asm/AssemblerPeepholeTests.cs:67
- method `if(haystack[i + k] != needle[k])` — PowerBasic.Compiler.Tests/Asm/AssemblerPeepholeTests.cs:121

### AssemblerPopcntTests.cs  `C#, 31 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/AssemblerPopcntTests.cs:2
- class `AssemblerPopcntTests` — PowerBasic.Compiler.Tests/Asm/AssemblerPopcntTests.cs:4

### AssemblerRelocatableTests.cs  `C#, 150 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/AssemblerRelocatableTests.cs:2
- class `AssemblerRelocatableTests` — PowerBasic.Compiler.Tests/Asm/AssemblerRelocatableTests.cs:4
- method `finish(asm)` — PowerBasic.Compiler.Tests/Asm/AssemblerRelocatableTests.cs:46

### AssemblerScheduleTests.cs  `C#, 269 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/AssemblerScheduleTests.cs:2
- class `AssemblerScheduleTests` — The assembler-level instruction scheduler (): reorders contiguous — PowerBasic.Compiler.Tests/Asm/AssemblerScheduleTests.cs:11
- method `if(haystack[i + k] != needle[k])` — PowerBasic.Compiler.Tests/Asm/AssemblerScheduleTests.cs:262

### AssemblerShiftTests.cs  `C#, 185 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/AssemblerShiftTests.cs:2
- class `AssemblerShiftTests` — PowerBasic.Compiler.Tests/Asm/AssemblerShiftTests.cs:4

### AssemblerSimdTests.cs  `C#, 149 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/AssemblerSimdTests.cs:2
- class `AssemblerSimdTests` — MMX (Pentium) integer SIMD encodings: the two-byte 0F xx escape with a ModRM whose — PowerBasic.Compiler.Tests/Asm/AssemblerSimdTests.cs:10

### AssemblerSsse3Sse4Tests.cs  `C#, 85 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/AssemblerSsse3Sse4Tests.cs:2
- class `AssemblerSsse3Sse4Tests` — PowerBasic.Compiler.Tests/Asm/AssemblerSsse3Sse4Tests.cs:4

### AssemblerStackAndFlowTests.cs  `C#, 495 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/AssemblerStackAndFlowTests.cs:2
- class `AssemblerStackAndFlowTests` — PowerBasic.Compiler.Tests/Asm/AssemblerStackAndFlowTests.cs:4

### DeadFrameStoreControlFlowTests.cs  `C#, 38 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/DeadFrameStoreControlFlowTests.cs:2
- class `DeadFrameStoreControlFlowTests` — PowerBasic.Compiler.Tests/Asm/DeadFrameStoreControlFlowTests.cs:4
- method `if(haystack[i + j] != needle[j])` — PowerBasic.Compiler.Tests/Asm/DeadFrameStoreControlFlowTests.cs:31

### EightySixOnlyInstructionTests.cs  `C#, 176 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/EightySixOnlyInstructionTests.cs:4
- class `EightySixOnlyInstructionTests` — That an image built for an 8086 contains only instructions an 8086 has. — PowerBasic.Compiler.Tests/Asm/EightySixOnlyInstructionTests.cs:26
- method `if(image[i + j] != bytes[j])` — PowerBasic.Compiler.Tests/Asm/EightySixOnlyInstructionTests.cs:85

### FlagReuseTests.cs  `C#, 133 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/FlagReuseTests.cs:2
- class `FlagReuseTests` — O0081 result-flag reuse: a zero test is redundant when the closest flag writer already left — PowerBasic.Compiler.Tests/Asm/FlagReuseTests.cs:10
- method `if(haystack[i + k] != needle[k])` — PowerBasic.Compiler.Tests/Asm/FlagReuseTests.cs:126

### JumpRelaxationTests.cs  `C#, 147 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/JumpRelaxationTests.cs:2
- class `JumpRelaxationTests` — S1 $OPTIMIZE SIZE: short-jump relaxation. A near JMP (E9 rel16, 3 bytes) or near Jcc — PowerBasic.Compiler.Tests/Asm/JumpRelaxationTests.cs:11

### JumpThreadingTests.cs  `C#, 103 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/JumpThreadingTests.cs:2
- class `JumpThreadingTests` — Assembler-level jump threading: a JMP/Jcc whose target label sits on an unconditional JMP is — PowerBasic.Compiler.Tests/Asm/JumpThreadingTests.cs:11

### LoadForwardingTests.cs  `C#, 302 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/LoadForwardingTests.cs:2
- class `LoadForwardingTests` — Redundant-load and dead-store elimination over BP-relative frame cells: — PowerBasic.Compiler.Tests/Asm/LoadForwardingTests.cs:11
- method `if(haystack[i + k] != needle[k])` — PowerBasic.Compiler.Tests/Asm/LoadForwardingTests.cs:295

### OrphanedJumpHopTests.cs  `C#, 132 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/OrphanedJumpHopTests.cs:2
- class `OrphanedJumpHopTests` — O0093's second half: once threading has bypassed an A: JMP B hop, the hop is dead code — PowerBasic.Compiler.Tests/Asm/OrphanedJumpHopTests.cs:28

### TailMergeTests.cs  `C#, 114 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/TailMergeTests.cs:2
- class `TailMergeTests` — S3 identical-code folding: procedure regions with byte- and fixup-identical content fold — PowerBasic.Compiler.Tests/Asm/TailMergeTests.cs:9

### TextAssemblerImmediateRangeTests.cs  `C#, 35 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/TextAssemblerImmediateRangeTests.cs:2
- class `TextAssemblerImmediateRangeTests` — PowerBasic.Compiler.Tests/Asm/TextAssemblerImmediateRangeTests.cs:4

### TextAssemblerTests.cs  `C#, 481 lines`
- namespace `PowerBasic.Compiler.Tests.Asm` — PowerBasic.Compiler.Tests/Asm/TextAssemblerTests.cs:2
- class `TextAssemblerTests` — PowerBasic.Compiler.Tests/Asm/TextAssemblerTests.cs:4
- class `TestResolver` — PowerBasic.Compiler.Tests/Asm/TextAssemblerTests.cs:7
- method `With` — PowerBasic.Compiler.Tests/Asm/TextAssemblerTests.cs:11
- method `TryResolve` — PowerBasic.Compiler.Tests/Asm/TextAssemblerTests.cs:16

## PowerBasic.Compiler.Tests/Backend/

### BackendAbsoluteArrayTests.cs  `C#, 158 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendAbsoluteArrayTests.cs:7
- class `BackendAbsoluteArrayTests` — DIM a(...) AT segment through the IR and the x86-16 back end: an ABSOLUTE array is a VIEW of — PowerBasic.Compiler.Tests/Backend/BackendAbsoluteArrayTests.cs:23
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendAbsoluteArrayTests.cs:95

### BackendArrayElementTests.cs  `C#, 344 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendArrayElementTests.cs:8
- class `BackendArrayElementTests` — Addressing ONE element of an array through the x86-16 back end, at an index the machine does not — PowerBasic.Compiler.Tests/Backend/BackendArrayElementTests.cs:37
- method `Execute` — PowerBasic.Compiler.Tests/Backend/BackendArrayElementTests.cs:56
- method `s(1 TO 4)` — PowerBasic.Compiler.Tests/Backend/BackendArrayElementTests.cs:94
- method `s(0 TO 3)` — PowerBasic.Compiler.Tests/Backend/BackendArrayElementTests.cs:115
- method `s(0)` — PowerBasic.Compiler.Tests/Backend/BackendArrayElementTests.cs:117
- method `LEN(s(2))` — PowerBasic.Compiler.Tests/Backend/BackendArrayElementTests.cs:118
- method `t(-2 TO 2)` — PowerBasic.Compiler.Tests/Backend/BackendArrayElementTests.cs:132
- method `b(1 TO 4)` — PowerBasic.Compiler.Tests/Backend/BackendArrayElementTests.cs:151
- method `p` — PowerBasic.Compiler.Tests/Backend/BackendArrayElementTests.cs:177
- method `g(1 TO 2, 1 TO 3)` — PowerBasic.Compiler.Tests/Backend/BackendArrayElementTests.cs:204
- method `g(2, 3)` — PowerBasic.Compiler.Tests/Backend/BackendArrayElementTests.cs:210

### BackendArrayLayoutTests.cs  `C#, 137 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendArrayLayoutTests.cs:5
- class `BackendArrayLayoutTests` — Physical multidimensional-array layout. PowerBASIC stores the first subscript contiguously, so — PowerBasic.Compiler.Tests/Backend/BackendArrayLayoutTests.cs:14
- method `Execute` — PowerBasic.Compiler.Tests/Backend/BackendArrayLayoutTests.cs:30
- method `CLNG(VARPTR(a%(11, 20)))` — PowerBasic.Compiler.Tests/Backend/BackendArrayLayoutTests.cs:53
- method `CLNG(VARPTR(a%(11, 20)))` — PowerBasic.Compiler.Tests/Backend/BackendArrayLayoutTests.cs:71

### BackendArraySortTests.cs  `C#, 352 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendArraySortTests.cs:5
- class `BackendArraySortTests` — ARRAY SORT and ARRAY SCAN through the IR path, run and read. — PowerBasic.Compiler.Tests/Backend/BackendArraySortTests.cs:21
- method `Execute` — PowerBasic.Compiler.Tests/Backend/BackendArraySortTests.cs:40
- method `Ascending()` — PowerBasic.Compiler.Tests/Backend/BackendArraySortTests.cs:252
- method `Descending()` — PowerBasic.Compiler.Tests/Backend/BackendArraySortTests.cs:253
- method `Ascending()` — PowerBasic.Compiler.Tests/Backend/BackendArraySortTests.cs:282
- method `Windowed()` — PowerBasic.Compiler.Tests/Backend/BackendArraySortTests.cs:284
- method `Repeatedly()` — PowerBasic.Compiler.Tests/Backend/BackendArraySortTests.cs:331

### BackendArrayUdtDifferentialTests.cs  `C#, 223 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendArrayUdtDifferentialTests.cs:5
- class `BackendArrayUdtDifferentialTests` — Arrays and user-defined types through both back ends, run and compared - the shapes a sweep of the — PowerBasic.Compiler.Tests/Backend/BackendArrayUdtDifferentialTests.cs:21
- method `Execute` — PowerBasic.Compiler.Tests/Backend/BackendArrayUdtDifferentialTests.cs:37
- method `q` — PowerBasic.Compiler.Tests/Backend/BackendArrayUdtDifferentialTests.cs:68
- method `Neg` — PowerBasic.Compiler.Tests/Backend/BackendArrayUdtDifferentialTests.cs:80
- method `f` — PowerBasic.Compiler.Tests/Backend/BackendArrayUdtDifferentialTests.cs:105
- method `b(0 TO 999)` — PowerBasic.Compiler.Tests/Backend/BackendArrayUdtDifferentialTests.cs:141
- method `src(0 TO 50)` — PowerBasic.Compiler.Tests/Backend/BackendArrayUdtDifferentialTests.cs:164
- method `g(r, c)` — PowerBasic.Compiler.Tests/Backend/BackendArrayUdtDifferentialTests.cs:177
- method `a()` — PowerBasic.Compiler.Tests/Backend/BackendArrayUdtDifferentialTests.cs:206
- method `a(1)` — PowerBasic.Compiler.Tests/Backend/BackendArrayUdtDifferentialTests.cs:210
- method `Grow(Op%(8))` — PowerBasic.Compiler.Tests/Backend/BackendArrayUdtDifferentialTests.cs:211
- method `a(1)` — PowerBasic.Compiler.Tests/Backend/BackendArrayUdtDifferentialTests.cs:212
- method `Grow` — PowerBasic.Compiler.Tests/Backend/BackendArrayUdtDifferentialTests.cs:213

### BackendByRefRoutingTests.cs  `C#, 222 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendByRefRoutingTests.cs:5
- class `BackendByRefRoutingTests` — End-to-end coverage for near numeric BYREF parameters on the routed x86-16 stack ABI. The IR — PowerBasic.Compiler.Tests/Backend/BackendByRefRoutingTests.cs:14
- method `Bump(n AS INTEGER)` — PowerBasic.Compiler.Tests/Backend/BackendByRefRoutingTests.cs:19
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendByRefRoutingTests.cs:121
- method `Mutate(a AS INTEGER, b AS INTEGER)` — PowerBasic.Compiler.Tests/Backend/BackendByRefRoutingTests.cs:122
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendByRefRoutingTests.cs:143
- method `Bump(value AS LONG)` — PowerBasic.Compiler.Tests/Backend/BackendByRefRoutingTests.cs:144
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendByRefRoutingTests.cs:168
- method `CountDown(n AS INTEGER, total AS LONG)` — PowerBasic.Compiler.Tests/Backend/BackendByRefRoutingTests.cs:169
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendByRefRoutingTests.cs:195
- method `Bump(value AS INTEGER)` — PowerBasic.Compiler.Tests/Backend/BackendByRefRoutingTests.cs:200

### BackendCallRoutingTests.cs  `C#, 360 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendCallRoutingTests.cs:9
- class `BackendCallRoutingTests` — A back-end-compiled function that calls another one. Until calls were selectable the — PowerBasic.Compiler.Tests/Backend/BackendCallRoutingTests.cs:25
- method `IrConstantInt(IrType.I32, 0x11112222)` — PowerBasic.Compiler.Tests/Backend/BackendCallRoutingTests.cs:112
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendCallRoutingTests.cs:226
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendCallRoutingTests.cs:257
- method `Touch(v%)` — PowerBasic.Compiler.Tests/Backend/BackendCallRoutingTests.cs:258
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendCallRoutingTests.cs:286
- method `CountDown(v%)` — PowerBasic.Compiler.Tests/Backend/BackendCallRoutingTests.cs:287
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendCallRoutingTests.cs:337
- method `Sum(BYVAL n%)` — PowerBasic.Compiler.Tests/Backend/BackendCallRoutingTests.cs:338

### BackendChainTests.cs  `C#, 250 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendChainTests.cs:9
- class `BackendChainTests` — CHAIN through the retargetable path: the COMMON values written into the handoff file, and the — PowerBasic.Compiler.Tests/Backend/BackendChainTests.cs:28

### BackendCodePointerTests.cs  `C#, 196 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendCodePointerTests.cs:6
- class `BackendCodePointerTests` — PB 3.2 CODE pointers on the retargetable path: CODEPTR32 of a label, and the — PowerBasic.Compiler.Tests/Backend/BackendCodePointerTests.cs:20
- method `Execute` — PowerBasic.Compiler.Tests/Backend/BackendCodePointerTests.cs:36
- method `CODEPTR(Here)` — PowerBasic.Compiler.Tests/Backend/BackendCodePointerTests.cs:143
- method `Work()` — PowerBasic.Compiler.Tests/Backend/BackendCodePointerTests.cs:170

### BackendConsoleTests.cs  `C#, 274 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendConsoleTests.cs:5
- class `BackendConsoleTests` — The console domain, compiled both ways and compared on the SCREEN rather than on stdout - which is — PowerBasic.Compiler.Tests/Backend/BackendConsoleTests.cs:23
- record `Both` — PowerBasic.Compiler.Tests/Backend/BackendConsoleTests.cs:31
- method `Execute` — PowerBasic.Compiler.Tests/Backend/BackendConsoleTests.cs:45

### BackendCorpusDifferentialTests.cs  `C#, 269 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendCorpusDifferentialTests.cs:6
- class `BackendCorpusDifferentialTests` — The corpus-wide version of : every battery program compiled — PowerBasic.Compiler.Tests/Backend/BackendCorpusDifferentialTests.cs:29
- record `Behaviour` — What a run can be observed to have done. Screen is the 80x25 text page, and it is a — PowerBasic.Compiler.Tests/Backend/BackendCorpusDifferentialTests.cs:41
- record `Disagreement` — PowerBasic.Compiler.Tests/Backend/BackendCorpusDifferentialTests.cs:42
- method `if(cpu.FileContent(name) is { } content)` — PowerBasic.Compiler.Tests/Backend/BackendCorpusDifferentialTests.cs:52
- method `Bind()` — Preprocessor.Expand, not Lexer.Tokenize: it is its own entry point, and it is what resolves — PowerBasic.Compiler.Tests/Backend/BackendCorpusDifferentialTests.cs:109
- method `foreach` — PowerBasic.Compiler.Tests/Backend/BackendCorpusDifferentialTests.cs:112
- method `Compare(optimize)` — PowerBasic.Compiler.Tests/Backend/BackendCorpusDifferentialTests.cs:114
- method `Compare(bool optimize)` — Both optimization settings, because they are different emitters: with the optimizer off there — PowerBasic.Compiler.Tests/Backend/BackendCorpusDifferentialTests.cs:120
- method `if(bound.Errors.Count > 0)` — PowerBasic.Compiler.Tests/Backend/BackendCorpusDifferentialTests.cs:125
- method `if(direct.Errors.Count > 0 || routed.Errors.Count > 0)` — PowerBasic.Compiler.Tests/Backend/BackendCorpusDifferentialTests.cs:132
- method `if` — PowerBasic.Compiler.Tests/Backend/BackendCorpusDifferentialTests.cs:161
- method `Window(string a, string b, string what)` — PowerBasic.Compiler.Tests/Backend/BackendCorpusDifferentialTests.cs:243
- method `Show(string text, int from, int at)` — PowerBasic.Compiler.Tests/Backend/BackendCorpusDifferentialTests.cs:250
- method `OneLine(string text, int limit)` — PowerBasic.Compiler.Tests/Backend/BackendCorpusDifferentialTests.cs:262

### BackendCoverageTests.cs  `C#, 947 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendCoverageTests.cs:9
- class `BackendCoverageTests` — How much of the real corpus the in-house x86-16 back end can actually compile, and - for — PowerBasic.Compiler.Tests/Backend/BackendCoverageTests.cs:44
- record `Census` — PowerBasic.Compiler.Tests/Backend/BackendCoverageTests.cs:84
- method `new(0, 0, 0, mainBodies, declines, selectionCases, lowered, 0, 0, loweri…` — PowerBasic.Compiler.Tests/Backend/BackendCoverageTests.cs:136
- method `if(model.Errors.Count > 0)` — PowerBasic.Compiler.Tests/Backend/BackendCoverageTests.cs:155
- method `if(routedNames.Contains(declinedName, StringComparer.OrdinalIgnoreCase))` — PowerBasic.Compiler.Tests/Backend/BackendCoverageTests.cs:210
- method `foreach(var (declinedName, declinedBecause) in unoptimized.BackendDeclines)` — PowerBasic.Compiler.Tests/Backend/BackendCoverageTests.cs:232
- method `foreach(var f in module.Functions)` — PowerBasic.Compiler.Tests/Backend/BackendCoverageTests.cs:246
- method `foreach` — PowerBasic.Compiler.Tests/Backend/BackendCoverageTests.cs:253
- method `if(InstructionSelector.TrySelect(fn, out var reason) is { } machine)` — PowerBasic.Compiler.Tests/Backend/BackendCoverageTests.cs:258
- method `if(LinearScanAllocator.Allocate(machine, out var noRegisters) is not nu…` — PowerBasic.Compiler.Tests/Backend/BackendCoverageTests.cs:264
- method `if(fn.Name.Equals("main", StringComparison.OrdinalIgnoreCase))` — a module body that selects AND allocates is a whole program the back end can own — PowerBasic.Compiler.Tests/Backend/BackendCoverageTests.cs:267
- method `if(!fn.Name.Equals("main", StringComparison.OrdinalIgnoreCase))` — PowerBasic.Compiler.Tests/Backend/BackendCoverageTests.cs:278
- method `new` — PowerBasic.Compiler.Tests/Backend/BackendCoverageTests.cs:283

### BackendCpuTargetTests.cs  `C#, 343 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendCpuTargetTests.cs:8
- class `BackendCpuTargetTests` — The declared $CPU target decides how a transcendental is computed, and the x86-16 back end — PowerBasic.Compiler.Tests/Backend/BackendCpuTargetTests.cs:23
- method `LOG(x)` — PowerBasic.Compiler.Tests/Backend/BackendCpuTargetTests.cs:129
- field `source` — every shape that used to produce one: a literal narrow shift, an array of a 4-byte element — PowerBasic.Compiler.Tests/Backend/BackendCpuTargetTests.cs:162
- method `if(instr.Opcode is not (MOpcode.Shl or MOpcode.Shr or MOpcode.Sar))` — PowerBasic.Compiler.Tests/Backend/BackendCpuTargetTests.cs:199
- method `if(instr.Operands[1] is MOperand.Immediate { Value: not 1 } count)` — PowerBasic.Compiler.Tests/Backend/BackendCpuTargetTests.cs:202
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendCpuTargetTests.cs:218
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendCpuTargetTests.cs:245
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendCpuTargetTests.cs:277
- method `Report(BYVAL n&)` — PowerBasic.Compiler.Tests/Backend/BackendCpuTargetTests.cs:280
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendCpuTargetTests.cs:301
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendCpuTargetTests.cs:324

### BackendDataReadTests.cs  `C#, 233 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendDataReadTests.cs:5
- class `BackendDataReadTests` — DATA/READ/RESTORE over both back ends. The corpus reads DATA in two programs — PowerBasic.Compiler.Tests/Backend/BackendDataReadTests.cs:22
- method `Execute` — PowerBasic.Compiler.Tests/Backend/BackendDataReadTests.cs:38
- method `Take` — PowerBasic.Compiler.Tests/Backend/BackendDataReadTests.cs:203

### BackendDialectDifferentialTests.cs  `C#, 63 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendDialectDifferentialTests.cs:5
- class `BackendDialectDifferentialTests` — Executed dialect gate for the IR middle end and x86-16 back end. Parser acceptance is insufficient: — PowerBasic.Compiler.Tests/Backend/BackendDialectDifferentialTests.cs:13

### BackendDifferentialTests.cs  `C#, 523 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendDifferentialTests.cs:5
- class `BackendDifferentialTests` — The measurement the retargetable path has been missing: the same program compiled BOTH ways, both — PowerBasic.Compiler.Tests/Backend/BackendDifferentialTests.cs:22
- method `Execute` — PowerBasic.Compiler.Tests/Backend/BackendDifferentialTests.cs:38
- method `a(1 TO 32760)` — PowerBasic.Compiler.Tests/Backend/BackendDifferentialTests.cs:311
- method `a(1)` — PowerBasic.Compiler.Tests/Backend/BackendDifferentialTests.cs:314
- method `a(1 TO 20000)` — PowerBasic.Compiler.Tests/Backend/BackendDifferentialTests.cs:329
- method `a` — PowerBasic.Compiler.Tests/Backend/BackendDifferentialTests.cs:343
- method `a(1)` — PowerBasic.Compiler.Tests/Backend/BackendDifferentialTests.cs:350
- method `a` — PowerBasic.Compiler.Tests/Backend/BackendDifferentialTests.cs:364
- method `a(1)` — PowerBasic.Compiler.Tests/Backend/BackendDifferentialTests.cs:370
- method `a(1 TO 3)` — PowerBasic.Compiler.Tests/Backend/BackendDifferentialTests.cs:387
- method `a(1)` — PowerBasic.Compiler.Tests/Backend/BackendDifferentialTests.cs:389
- method `a` — PowerBasic.Compiler.Tests/Backend/BackendDifferentialTests.cs:400
- method `a(1)` — PowerBasic.Compiler.Tests/Backend/BackendDifferentialTests.cs:407
- method `s` — PowerBasic.Compiler.Tests/Backend/BackendDifferentialTests.cs:424
- method `s(i)` — PowerBasic.Compiler.Tests/Backend/BackendDifferentialTests.cs:430
- method `s(1 TO 2)` — PowerBasic.Compiler.Tests/Backend/BackendDifferentialTests.cs:443
- method `s(1)` — PowerBasic.Compiler.Tests/Backend/BackendDifferentialTests.cs:448
- method `s` — PowerBasic.Compiler.Tests/Backend/BackendDifferentialTests.cs:449
- method `g` — PowerBasic.Compiler.Tests/Backend/BackendDifferentialTests.cs:463
- method `g` — PowerBasic.Compiler.Tests/Backend/BackendDifferentialTests.cs:468

### BackendDivRemTests.cs  `C#, 78 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendDivRemTests.cs:5
- class `BackendDivRemTests` — One signed IDIV supplies both adjacent quotient and remainder results. — PowerBasic.Compiler.Tests/Backend/BackendDivRemTests.cs:9
- method `DivideBoth` — PowerBasic.Compiler.Tests/Backend/BackendDivRemTests.cs:21

### BackendDivisionTests.cs  `C#, 240 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendDivisionTests.cs:9
- class `BackendDivisionTests` — Signed 16/32-bit division and remainder on the x86-16 back end. IDIV is the first selected — PowerBasic.Compiler.Tests/Backend/BackendDivisionTests.cs:24
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendDivisionTests.cs:160
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendDivisionTests.cs:196
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendDivisionTests.cs:222

### BackendDynamicArrayAliasTests.cs  `C#, 149 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendDynamicArrayAliasTests.cs:7
- class `BackendDynamicArrayAliasTests` — Two dynamic arrays allocated in one procedure, through the x86-16 back end - the case where they — PowerBasic.Compiler.Tests/Backend/BackendDynamicArrayAliasTests.cs:31
- method `Execute` — PowerBasic.Compiler.Tests/Backend/BackendDynamicArrayAliasTests.cs:78

### BackendDynamicDimTests.cs  `C#, 139 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendDynamicDimTests.cs:7
- class `BackendDynamicDimTests` — DIM a(1 TO n) with a bound that is not a compile-time constant, and no REDIM anywhere — PowerBasic.Compiler.Tests/Backend/BackendDynamicDimTests.cs:30

### BackendErrorHandlerTests.cs  `C#, 206 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendErrorHandlerTests.cs:5
- class `BackendErrorHandlerTests` — ON ERROR compiled by the x86-16 back end, and executed. — PowerBasic.Compiler.Tests/Backend/BackendErrorHandlerTests.cs:26
- method `Boom(v%)` — PowerBasic.Compiler.Tests/Backend/BackendErrorHandlerTests.cs:121
- method `Recurse(d%)` — PowerBasic.Compiler.Tests/Backend/BackendErrorHandlerTests.cs:144

### BackendErrorTrapTests.cs  `C#, 230 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendErrorTrapTests.cs:5
- class `BackendErrorTrapTests` — The $ERROR traps a program arms, inside a PROCEDURE, through the IR path - the case where — PowerBasic.Compiler.Tests/Backend/BackendErrorTrapTests.cs:35
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendErrorTrapTests.cs:208

### BackendExitFarTests.cs  `C#, 288 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendExitFarTests.cs:5
- class `BackendExitFarTests` — EXIT FAR compiled by the x86-16 back end, and executed. — PowerBasic.Compiler.Tests/Backend/BackendExitFarTests.cs:24
- method `Leave()` — PowerBasic.Compiler.Tests/Backend/BackendExitFarTests.cs:57
- method `Noisy(BYVAL n%)` — PowerBasic.Compiler.Tests/Backend/BackendExitFarTests.cs:79
- method `Counter()` — PowerBasic.Compiler.Tests/Backend/BackendExitFarTests.cs:103
- method `Maybe(BYVAL n%)` — PowerBasic.Compiler.Tests/Backend/BackendExitFarTests.cs:151
- method `Leave()` — PowerBasic.Compiler.Tests/Backend/BackendExitFarTests.cs:175
- method `Quiet()` — PowerBasic.Compiler.Tests/Backend/BackendExitFarTests.cs:201
- method `Quiet` — PowerBasic.Compiler.Tests/Backend/BackendExitFarTests.cs:208
- method `Outer()` — PowerBasic.Compiler.Tests/Backend/BackendExitFarTests.cs:240
- method `Churn(BYVAL n%)` — PowerBasic.Compiler.Tests/Backend/BackendExitFarTests.cs:268
- method `Churn` — PowerBasic.Compiler.Tests/Backend/BackendExitFarTests.cs:277

### BackendFieldTests.cs  `C#, 246 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendFieldTests.cs:9
- class `BackendFieldTests` — FIELD through the retargetable path: a record buffer read and written through named windows on — PowerBasic.Compiler.Tests/Backend/BackendFieldTests.cs:21

### BackendFixBcdTests.cs  `C#, 219 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendFixBcdTests.cs:6
- class `BackendFixBcdTests` — PowerBASIC's two decimal types through the retargetable path, and the reason they are not one — PowerBasic.Compiler.Tests/Backend/BackendFixBcdTests.cs:20
- method `SIZEOF(b@@)` — PowerBasic.Compiler.Tests/Backend/BackendFixBcdTests.cs:86
- method `SIZEOF(f@)` — PowerBasic.Compiler.Tests/Backend/BackendFixBcdTests.cs:123
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendFixBcdTests.cs:191

### BackendFloatPhiTests.cs  `C#, 67 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendFloatPhiTests.cs:8
- class `BackendFloatPhiTests` — A float that flows out of a loop or a branch is a PHI, and a phi on this target cannot be a — PowerBasic.Compiler.Tests/Backend/BackendFloatPhiTests.cs:19

### BackendFloatTests.cs  `C#, 355 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendFloatTests.cs:9
- class `BackendFloatTests` — Floating point on the x86-16 back end. x87 computes on a stack, not in a register file, so — PowerBasic.Compiler.Tests/Backend/BackendFloatTests.cs:23
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendFloatTests.cs:103
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendFloatTests.cs:235
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendFloatTests.cs:288
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendFloatTests.cs:324
- method `MAX` — PowerBasic.Compiler.Tests/Backend/BackendFloatTests.cs:325

### BackendFloatWidthTests.cs  `C#, 312 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendFloatWidthTests.cs:5
- class `BackendFloatWidthTests` — Float WIDTH through the x86-16 back end, which is the thing the differential battery caught it on. — PowerBasic.Compiler.Tests/Backend/BackendFloatWidthTests.cs:23

### BackendFrameTests.cs  `C#, 189 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendFrameTests.cs:7
- class `BackendFrameTests` — The routed frame: where a local lives and what is in it before the body writes anything. — PowerBasic.Compiler.Tests/Backend/BackendFrameTests.cs:28
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendFrameTests.cs:48
- method `Acc(3)` — PowerBasic.Compiler.Tests/Backend/BackendFrameTests.cs:49
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendFrameTests.cs:75
- method `Edges(9)` — PowerBasic.Compiler.Tests/Backend/BackendFrameTests.cs:76
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendFrameTests.cs:103
- method `Dirty(1234)` — PowerBasic.Compiler.Tests/Backend/BackendFrameTests.cs:104

### BackendGlobalAccessTests.cs  `C#, 357 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendGlobalAccessTests.cs:8
- class `BackendGlobalAccessTests` — A back-end-compiled function reading a module-level variable. The back end lays out no data of its — PowerBasic.Compiler.Tests/Backend/BackendGlobalAccessTests.cs:24
- method `Store` — PowerBasic.Compiler.Tests/Backend/BackendGlobalAccessTests.cs:69
- method `Show` — PowerBasic.Compiler.Tests/Backend/BackendGlobalAccessTests.cs:82
- method `DataCells` — PowerBasic.Compiler.Tests/Backend/BackendGlobalAccessTests.cs:155
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendGlobalAccessTests.cs:225
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendGlobalAccessTests.cs:263

### BackendIdiomTests.cs  `C#, 214 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendIdiomTests.cs:4
- class `BackendIdiomTests` — The multi-instruction selection patterns (InstructionSelector.Idioms): shapes the optimizer — PowerBasic.Compiler.Tests/Backend/BackendIdiomTests.cs:14
- method `IrArgument(IrType.I16, 0)` — PowerBasic.Compiler.Tests/Backend/BackendIdiomTests.cs:84

### BackendInlineAsmTests.cs  `C#, 410 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendInlineAsmTests.cs:8
- class `BackendInlineAsmTests` — Inline assembly through the x86-16 back end. — PowerBasic.Compiler.Tests/Backend/BackendInlineAsmTests.cs:19
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendInlineAsmTests.cs:72
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendInlineAsmTests.cs:86
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendInlineAsmTests.cs:105
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendInlineAsmTests.cs:126
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendInlineAsmTests.cs:159
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendInlineAsmTests.cs:190

### BackendInputRoutingTests.cs  `C#, 243 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendInputRoutingTests.cs:5
- class `BackendInputRoutingTests` — Numeric and string INPUT, and narrowing to a BYTE, on the x86-16 back end. — PowerBasic.Compiler.Tests/Backend/BackendInputRoutingTests.cs:23
- method `if(expected is not null)` — PowerBasic.Compiler.Tests/Backend/BackendInputRoutingTests.cs:216

### BackendLoopStepTests.cs  `C#, 147 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendLoopStepTests.cs:5
- class `BackendLoopStepTests` — FOR i = a TO b STEP s where s is a runtime value, compiled both ways and executed. — PowerBasic.Compiler.Tests/Backend/BackendLoopStepTests.cs:31
- method `Walk(BYVAL a&, BYVAL b&, BYVAL s&)` — PowerBasic.Compiler.Tests/Backend/BackendLoopStepTests.cs:106

### BackendMainRoutingTests.cs  `C#, 101 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendMainRoutingTests.cs:4
- class `BackendMainRoutingTests` — The module body compiled by the x86-16 back end - the step from "the back end compiles some — PowerBasic.Compiler.Tests/Backend/BackendMainRoutingTests.cs:14

### BackendMathIntrinsicTests.cs  `C#, 67 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendMathIntrinsicTests.cs:5
- class `BackendMathIntrinsicTests` — The transcendental intrinsics are INSTRUCTIONS on this target, not runtime routines: the x87 has — PowerBasic.Compiler.Tests/Backend/BackendMathIntrinsicTests.cs:17
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendMathIntrinsicTests.cs:57
- method `TAN(i / 4)` — PowerBasic.Compiler.Tests/Backend/BackendMathIntrinsicTests.cs:60

### BackendMemoryCompareTests.cs  `C#, 97 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendMemoryCompareTests.cs:5
- class `BackendMemoryCompareTests` — A comparison where NEITHER side is in a register. — PowerBasic.Compiler.Tests/Backend/BackendMemoryCompareTests.cs:22

### BackendMemoryOrderingTests.cs  `C#, 135 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendMemoryOrderingTests.cs:8
- class `BackendMemoryOrderingTests` — The one aliasing rule has - order any pair where at least one side — PowerBasic.Compiler.Tests/Backend/BackendMemoryOrderingTests.cs:31
- method `Bump` — PowerBasic.Compiler.Tests/Backend/BackendMemoryOrderingTests.cs:45

### BackendMetaStatementTests.cs  `C#, 121 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendMetaStatementTests.cs:5
- class `BackendMetaStatementTests` — The metastatements that describe the COMPILATION rather than the run - $DYNAMIC, — PowerBasic.Compiler.Tests/Backend/BackendMetaStatementTests.cs:27
- record `Program` — how the case reads in the test list. — PowerBasic.Compiler.Tests/Backend/BackendMetaStatementTests.cs:33
- method `ToString()` — PowerBasic.Compiler.Tests/Backend/BackendMetaStatementTests.cs:34

### BackendMirroredCompareTests.cs  `C#, 100 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendMirroredCompareTests.cs:5
- class `BackendMirroredCompareTests` — A comparison written with the CONSTANT on the left, through the x86-16 back end. — PowerBasic.Compiler.Tests/Backend/BackendMirroredCompareTests.cs:21

### BackendNeverThrowsTests.cs  `C#, 342 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendNeverThrowsTests.cs:5
- class `BackendNeverThrowsTests` — The routed back end must DECLINE what it cannot compile, never THROW. — PowerBasic.Compiler.Tests/Backend/BackendNeverThrowsTests.cs:37
- method `if(RoutedCompileFailure(text, name, optimize) is { } e)` — PowerBasic.Compiler.Tests/Backend/BackendNeverThrowsTests.cs:80
- method `foreach(var optimize in new[] { true, false })` — PowerBasic.Compiler.Tests/Backend/BackendNeverThrowsTests.cs:186
- method `if(RoutedCompileFailure(source, name + ".BAS", optimize) is { } e)` — PowerBasic.Compiler.Tests/Backend/BackendNeverThrowsTests.cs:188
- method `if(!takesOperand)` — PowerBasic.Compiler.Tests/Backend/BackendNeverThrowsTests.cs:191
- method `if(RoutedCompileFailure(source, name + ".BAS", optimize) is { } e)` — PowerBasic.Compiler.Tests/Backend/BackendNeverThrowsTests.cs:286
- method `Bind()` — and the decline has to be a real fallback, not merely a non-crash: the direct emitter takes — PowerBasic.Compiler.Tests/Backend/BackendNeverThrowsTests.cs:292
- method `if(!directImage.SequenceEqual(routedImage))` — PowerBasic.Compiler.Tests/Backend/BackendNeverThrowsTests.cs:298
- method `if(direct.Errors.Count != routed.Errors.Count)` — PowerBasic.Compiler.Tests/Backend/BackendNeverThrowsTests.cs:300

### BackendOnGosubTests.cs  `C#, 264 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendOnGosubTests.cs:7
- class `BackendOnGosubTests` — ON n GOSUB on the routed path. It is ON n GOTO's dispatch over the GOSUB — PowerBasic.Compiler.Tests/Backend/BackendOnGosubTests.cs:24

### BackendOptimizeGatingTests.cs  `C#, 134 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendOptimizeGatingTests.cs:5
- class `BackendOptimizeGatingTests` — The routed path honours --no-optimize: a function the x86-16 back end takes is compiled — PowerBasic.Compiler.Tests/Backend/BackendOptimizeGatingTests.cs:29
- method `Execute` — PowerBasic.Compiler.Tests/Backend/BackendOptimizeGatingTests.cs:118

### BackendOverflowTests.cs  `C#, 54 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendOverflowTests.cs:5
- class `BackendOverflowTests` — Checked arithmetic must retain its PowerBASIC Error 6 path after IR loop transforms. — PowerBasic.Compiler.Tests/Backend/BackendOverflowTests.cs:9

### BackendPagedArrayTests.cs  `C#, 176 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendPagedArrayTests.cs:6
- class `BackendPagedArrayTests` — The memory-model array classes through the IR and the x86-16 back end: DIM HUGE, which takes — PowerBasic.Compiler.Tests/Backend/BackendPagedArrayTests.cs:21
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendPagedArrayTests.cs:126
- method `v(1 TO 50000)` — PowerBasic.Compiler.Tests/Backend/BackendPagedArrayTests.cs:127
- method `v(1)` — PowerBasic.Compiler.Tests/Backend/BackendPagedArrayTests.cs:130

### BackendPhiSwapTests.cs  `C#, 159 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendPhiSwapTests.cs:7
- class `BackendPhiSwapTests` — Taking SSA apart on one CFG edge is a PARALLEL copy: every phi on the edge reads the values the — PowerBasic.Compiler.Tests/Backend/BackendPhiSwapTests.cs:24
- field `source` — the values come out of DATA, so nothing folds the loop away and prints the answer — PowerBasic.Compiler.Tests/Backend/BackendPhiSwapTests.cs:123

### BackendPointerTests.cs  `C#, 510 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendPointerTests.cs:6
- class `BackendPointerTests` — PB 3.2 data pointers on the retargetable path: VARPTR32 forms one, @p reads and — PowerBasic.Compiler.Tests/Backend/BackendPointerTests.cs:19
- method `Execute` — PowerBasic.Compiler.Tests/Backend/BackendPointerTests.cs:35
- method `Bump(v AS INTEGER)` — PowerBasic.Compiler.Tests/Backend/BackendPointerTests.cs:127
- method `CLNG(VARPTR(a%(4)))` — PowerBasic.Compiler.Tests/Backend/BackendPointerTests.cs:180
- method `VARPTR(v)` — PowerBasic.Compiler.Tests/Backend/BackendPointerTests.cs:205
- method `VARPTR(g)` — PowerBasic.Compiler.Tests/Backend/BackendPointerTests.cs:239
- method `CLNG(VARPTR(v))` — PowerBasic.Compiler.Tests/Backend/BackendPointerTests.cs:264
- method `VARSEG(vid%(0))` — PowerBasic.Compiler.Tests/Backend/BackendPointerTests.cs:334
- method `VARSEG(a%(Given%(9)))` — PowerBasic.Compiler.Tests/Backend/BackendPointerTests.cs:428
- method `VARSEG(h%(1))` — PowerBasic.Compiler.Tests/Backend/BackendPointerTests.cs:452
- method `Poke` — PowerBasic.Compiler.Tests/Backend/BackendPointerTests.cs:496

### BackendPrintUsingTests.cs  `C#, 407 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendPrintUsingTests.cs:6
- class `BackendPrintUsingTests` — PRINT USING and LPRINT through the retargetable path, executed and read. — PowerBasic.Compiler.Tests/Backend/BackendPrintUsingTests.cs:31
- record `Behaviour` — What one run was observed to do: the screen, the printer, and any file it wrote. — PowerBasic.Compiler.Tests/Backend/BackendPrintUsingTests.cs:41
- method `Execute` — PowerBasic.Compiler.Tests/Backend/BackendPrintUsingTests.cs:53
- method `new("", "", null)` — PowerBasic.Compiler.Tests/Backend/BackendPrintUsingTests.cs:60
- method `POS(0)` — PowerBasic.Compiler.Tests/Backend/BackendPrintUsingTests.cs:250
- method `POS(0)` — PowerBasic.Compiler.Tests/Backend/BackendPrintUsingTests.cs:339

### BackendQuadPrintTests.cs  `C#, 423 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendQuadPrintTests.cs:9
- class `BackendQuadPrintTests` — PRINT of a signed QUAD. Genuine PB 3.5 keeps the integer exact on the x87 stack and sends it — PowerBasic.Compiler.Tests/Backend/BackendQuadPrintTests.cs:17
- field `value` — PowerBasic.Compiler.Tests/Backend/BackendQuadPrintTests.cs:208
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendQuadPrintTests.cs:271
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendQuadPrintTests.cs:300
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendQuadPrintTests.cs:327
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendQuadPrintTests.cs:349
- method `Bits(BYVAL a&, BYVAL b&)` — PowerBasic.Compiler.Tests/Backend/BackendQuadPrintTests.cs:352
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendQuadPrintTests.cs:387
- method `Shifted(BYVAL a&)` — PowerBasic.Compiler.Tests/Backend/BackendQuadPrintTests.cs:390

### BackendRadixTests.cs  `C#, 77 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendRadixTests.cs:5
- class `BackendRadixTests` — HEX$, OCT$ and BIN$ through the x86-16 back end, including the two-argument form. — PowerBasic.Compiler.Tests/Backend/BackendRadixTests.cs:25

### BackendRandomTests.cs  `C#, 170 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendRandomTests.cs:5
- class `BackendRandomTests` — RANDOMIZE, RND and TIMER over both back ends. — PowerBasic.Compiler.Tests/Backend/BackendRandomTests.cs:36
- method `RND(1, 1000)` — PowerBasic.Compiler.Tests/Backend/BackendRandomTests.cs:74

### BackendRecordSwapTests.cs  `C#, 123 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendRecordSwapTests.cs:5
- class `BackendRecordSwapTests` — SWAP of two RECORDS on the x86-16 back end. — PowerBasic.Compiler.Tests/Backend/BackendRecordSwapTests.cs:24
- method `t(1 TO 3)` — PowerBasic.Compiler.Tests/Backend/BackendRecordSwapTests.cs:108

### BackendRegisterPressureTests.cs  `C#, 167 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendRegisterPressureTests.cs:8
- class `BackendRegisterPressureTests` — Register PRESSURE on the x86-16 back end, as distinct from the CALL-driven spilling next door in — PowerBasic.Compiler.Tests/Backend/BackendRegisterPressureTests.cs:26

### BackendResidencyTests.cs  `C#, 337 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendResidencyTests.cs:9
- class `BackendResidencyTests` — Register residency in the routed path (docs/X86-BACKEND.md, docs/PB36.md O5): a loop's counter and — PowerBasic.Compiler.Tests/Backend/BackendResidencyTests.cs:31
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendResidencyTests.cs:128
- method `Walk(BYVAL i%)` — PowerBasic.Compiler.Tests/Backend/BackendResidencyTests.cs:130
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendResidencyTests.cs:181
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendResidencyTests.cs:205
- method `if(model.Errors.Count > 0)` — PowerBasic.Compiler.Tests/Backend/BackendResidencyTests.cs:305
- method `if(module is null)` — PowerBasic.Compiler.Tests/Backend/BackendResidencyTests.cs:308
- method `foreach(var f in module.Functions)` — PowerBasic.Compiler.Tests/Backend/BackendResidencyTests.cs:311
- method `foreach` — PowerBasic.Compiler.Tests/Backend/BackendResidencyTests.cs:318
- method `if(plain is not null && speed is null)` — PowerBasic.Compiler.Tests/Backend/BackendResidencyTests.cs:328

### BackendRoundingTests.cs  `C#, 133 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendRoundingTests.cs:7
- class `BackendRoundingTests` — The three roundings PowerBASIC keeps apart on purpose, measured on the x86-16 back end by — PowerBasic.Compiler.Tests/Backend/BackendRoundingTests.cs:29

### BackendRoutingGateTests.cs  `C#, 471 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendRoutingGateTests.cs:5
- class `BackendRoutingGateTests` — One tiny program per construct, compiled twice - with routing on and with routing off - so that a — PowerBasic.Compiler.Tests/Backend/BackendRoutingGateTests.cs:41
- record `Construct` — how the case reads in the test list. — PowerBasic.Compiler.Tests/Backend/BackendRoutingGateTests.cs:48
- method `ToString()` — PowerBasic.Compiler.Tests/Backend/BackendRoutingGateTests.cs:49
- method `F(BYVAL a%)` — PowerBasic.Compiler.Tests/Backend/BackendRoutingGateTests.cs:55
- method `F(v%)` — PowerBasic.Compiler.Tests/Backend/BackendRoutingGateTests.cs:85
- method `F(BYVAL a$)` — PowerBasic.Compiler.Tests/Backend/BackendRoutingGateTests.cs:88
- method `Grow(BYVAL n%)` — PowerBasic.Compiler.Tests/Backend/BackendRoutingGateTests.cs:198
- method `LBOUND` — PowerBasic.Compiler.Tests/Backend/BackendRoutingGateTests.cs:202
- method `Grow` — PowerBasic.Compiler.Tests/Backend/BackendRoutingGateTests.cs:204
- method `ABS(v)` — PowerBasic.Compiler.Tests/Backend/BackendRoutingGateTests.cs:268
- method `F(BYVAL a&&)` — PowerBasic.Compiler.Tests/Backend/BackendRoutingGateTests.cs:305

### BackendRuntimeCallTests.cs  `C#, 988 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendRuntimeCallTests.cs:9
- class `BackendRuntimeCallTests` — The runtime-label bridge: a back-end-compiled function calling the DOS runtime. — PowerBasic.Compiler.Tests/Backend/BackendRuntimeCallTests.cs:21
- method `KeepPair` — PowerBasic.Compiler.Tests/Backend/BackendRuntimeCallTests.cs:99
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendRuntimeCallTests.cs:320
- method `IsPhysicalMove` — PowerBasic.Compiler.Tests/Backend/BackendRuntimeCallTests.cs:474
- method `Destination(MInstr instruction)` — PowerBasic.Compiler.Tests/Backend/BackendRuntimeCallTests.cs:477
- method `LOF(1)` — PowerBasic.Compiler.Tests/Backend/BackendRuntimeCallTests.cs:633
- method `IrArgument(IrType.Ptr, 0)` — PowerBasic.Compiler.Tests/Backend/BackendRuntimeCallTests.cs:825
- method `IrConstantInt` — PowerBasic.Compiler.Tests/Backend/BackendRuntimeCallTests.cs:834
- method `a(1 TO 5)` — PowerBasic.Compiler.Tests/Backend/BackendRuntimeCallTests.cs:947
- method `a(1 TO 5)` — PowerBasic.Compiler.Tests/Backend/BackendRuntimeCallTests.cs:973

### BackendSpillTerminationTests.cs  `C#, 238 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendSpillTerminationTests.cs:7
- class `BackendSpillTerminationTests` — That the x86-16 allocator's spill loop STOPS - and stops because each round gets measurably closer — PowerBasic.Compiler.Tests/Backend/BackendSpillTerminationTests.cs:25
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: fals…` — PowerBasic.Compiler.Tests/Backend/BackendSpillTerminationTests.cs:67
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: fals…` — PowerBasic.Compiler.Tests/Backend/BackendSpillTerminationTests.cs:71
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: fals…` — PowerBasic.Compiler.Tests/Backend/BackendSpillTerminationTests.cs:75
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [1], ReadsFlags: false, WritesFlags: fals…` — PowerBasic.Compiler.Tests/Backend/BackendSpillTerminationTests.cs:81
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [1], ReadsFlags: false, WritesFlags: fals…` — PowerBasic.Compiler.Tests/Backend/BackendSpillTerminationTests.cs:85
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendSpillTerminationTests.cs:126
- method `Fill(BYVAL seed%)` — PowerBasic.Compiler.Tests/Backend/BackendSpillTerminationTests.cs:127
- method `if(allocation is not null)` — PowerBasic.Compiler.Tests/Backend/BackendSpillTerminationTests.cs:179
- method `if(rounds > worst.Rounds)` — PowerBasic.Compiler.Tests/Backend/BackendSpillTerminationTests.cs:183
- method `if(rounds > budget)` — PowerBasic.Compiler.Tests/Backend/BackendSpillTerminationTests.cs:185
- method `return(function.Name, machine)` — PowerBasic.Compiler.Tests/Backend/BackendSpillTerminationTests.cs:234

### BackendSpillTests.cs  `C#, 479 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendSpillTests.cs:9
- class `BackendSpillTests` — Spilling on the x86-16 back end. The allocation failure that matters on this target is not "six — PowerBasic.Compiler.Tests/Backend/BackendSpillTests.cs:23
- method `Callee(BYVAL fixed%, BYVAL varying%)` — PowerBasic.Compiler.Tests/Backend/BackendSpillTests.cs:197
- method `Work()` — PowerBasic.Compiler.Tests/Backend/BackendSpillTests.cs:216
- method `Size(MOperand operand)` — PowerBasic.Compiler.Tests/Backend/BackendSpillTests.cs:347
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendSpillTests.cs:368
- method `Work()` — PowerBasic.Compiler.Tests/Backend/BackendSpillTests.cs:369

### BackendStringLifetimeTests.cs  `C#, 372 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendStringLifetimeTests.cs:5
- class `BackendStringLifetimeTests` — Who owns a string handle, on the retargetable path - the rule IrLowering states and the two — PowerBasic.Compiler.Tests/Backend/BackendStringLifetimeTests.cs:25
- method `Execute` — PowerBasic.Compiler.Tests/Backend/BackendStringLifetimeTests.cs:41
- method `LEN(s$)` — PowerBasic.Compiler.Tests/Backend/BackendStringLifetimeTests.cs:152
- method `ASC(s$, 5)` — PowerBasic.Compiler.Tests/Backend/BackendStringLifetimeTests.cs:181
- method `LEN(s$)` — PowerBasic.Compiler.Tests/Backend/BackendStringLifetimeTests.cs:184
- method `Exchange()` — PowerBasic.Compiler.Tests/Backend/BackendStringLifetimeTests.cs:199
- method `Mutate(value$)` — PowerBasic.Compiler.Tests/Backend/BackendStringLifetimeTests.cs:234
- method `Mutate(text)` — PowerBasic.Compiler.Tests/Backend/BackendStringLifetimeTests.cs:239
- method `Mutate(value$)` — PowerBasic.Compiler.Tests/Backend/BackendStringLifetimeTests.cs:261
- method `Mutate` — PowerBasic.Compiler.Tests/Backend/BackendStringLifetimeTests.cs:270
- method `Work(BYVAL seed$)` — PowerBasic.Compiler.Tests/Backend/BackendStringLifetimeTests.cs:286
- method `Observe(value$)` — PowerBasic.Compiler.Tests/Backend/BackendStringLifetimeTests.cs:316

### BackendStringOffsetTests.cs  `C#, 92 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendStringOffsetTests.cs:5
- class `BackendStringOffsetTests` — The string forms that take a POSITION, through the x86-16 back end: ASC(s$, i), the — PowerBasic.Compiler.Tests/Backend/BackendStringOffsetTests.cs:22
- method `ASC(s, i%)` — PowerBasic.Compiler.Tests/Backend/BackendStringOffsetTests.cs:44

### BackendStringSetTests.cs  `C#, 172 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendStringSetTests.cs:5
- class `BackendStringSetTests` — The character-set string surface on the retargetable path: INSTR … ANY, VERIFY, — PowerBasic.Compiler.Tests/Backend/BackendStringSetTests.cs:16
- method `Execute` — PowerBasic.Compiler.Tests/Backend/BackendStringSetTests.cs:32
- method `INSTR(a$, ANY "-/")` — PowerBasic.Compiler.Tests/Backend/BackendStringSetTests.cs:52
- method `VERIFY("123A45", "0123456789")` — PowerBasic.Compiler.Tests/Backend/BackendStringSetTests.cs:68
- method `LEN(CHR$(65, 66, 67))` — PowerBasic.Compiler.Tests/Backend/BackendStringSetTests.cs:104
- method `BIT(b%, 3)` — PowerBasic.Compiler.Tests/Backend/BackendStringSetTests.cs:147
- method `TALLY("the cat and the hat", "the")` — PowerBasic.Compiler.Tests/Backend/BackendStringSetTests.cs:160

### BackendSwitchDispatchTests.cs  `C#, 387 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendSwitchDispatchTests.cs:5
- class `BackendSwitchDispatchTests` — SELECT CASE dispatch through the x86-16 back end: the same five shapes the direct emitter — PowerBasic.Compiler.Tests/Backend/BackendSwitchDispatchTests.cs:31
- method `if(image[at + i] != needle[i])` — PowerBasic.Compiler.Tests/Backend/BackendSwitchDispatchTests.cs:51
- field `wide` — PowerBasic.Compiler.Tests/Backend/BackendSwitchDispatchTests.cs:136
- field `wide` — PowerBasic.Compiler.Tests/Backend/BackendSwitchDispatchTests.cs:163

### BackendSwitchTests.cs  `C#, 251 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendSwitchTests.cs:8
- class `BackendSwitchTests` — Integer switch selection for ON ... GOTO and the IR's GOSUB return dispatch. — PowerBasic.Compiler.Tests/Backend/BackendSwitchTests.cs:12
- method `DispatchLong` — PowerBasic.Compiler.Tests/Backend/BackendSwitchTests.cs:24
- method `HasOnePinnedDecision` — PowerBasic.Compiler.Tests/Backend/BackendSwitchTests.cs:94

### BackendTailRecursionTests.cs  `C#, 132 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendTailRecursionTests.cs:7
- class `BackendTailRecursionTests` — Tail recursion through the x86-16 back end - the first of the DIRECT emitter's optimizations the — PowerBasic.Compiler.Tests/Backend/BackendTailRecursionTests.cs:29
- method `CountDown(BYVAL n&)` — PowerBasic.Compiler.Tests/Backend/BackendTailRecursionTests.cs:86

### BackendTruthValueTests.cs  `C#, 165 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendTruthValueTests.cs:4
- class `BackendTruthValueTests` — BASIC's truth value is -1/0, and the 8086 has no SETcc - so a comparison — PowerBasic.Compiler.Tests/Backend/BackendTruthValueTests.cs:18

### BackendUnsignedConversionTests.cs  `C#, 85 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendUnsignedConversionTests.cs:5
- class `BackendUnsignedConversionTests` — A float converted to an UNSIGNED integer through the x86-16 back end. — PowerBasic.Compiler.Tests/Backend/BackendUnsignedConversionTests.cs:21

### BackendUnsignedPrintTests.cs  `C#, 86 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendUnsignedPrintTests.cs:8
- class `BackendUnsignedPrintTests` — PRINT of an unsigned DWORD. There is no unsigned 32-bit printer in the runtime - rt_print_i32 would — PowerBasic.Compiler.Tests/Backend/BackendUnsignedPrintTests.cs:17
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendUnsignedPrintTests.cs:74

### BackendWideCompareTests.cs  `C#, 203 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendWideCompareTests.cs:8
- class `BackendWideCompareTests` — 32-bit comparison materialized as PowerBASIC's -1/0 truth value. There is no 32-bit CMP on this — PowerBasic.Compiler.Tests/Backend/BackendWideCompareTests.cs:21

### BackendWideIntegerTests.cs  `C#, 322 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendWideIntegerTests.cs:4
- class `BackendWideIntegerTests` — 32-bit values on a 16-bit target. The baseline representation of a LONG/DWORD is a register — PowerBasic.Compiler.Tests/Backend/BackendWideIntegerTests.cs:19

### BackendWordNarrowingTests.cs  `C#, 391 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendWordNarrowingTests.cs:9
- class `BackendWordNarrowingTests` — Selecting a 32-bit value the target can PROVE is word-sized into ONE word register - the — PowerBasic.Compiler.Tests/Backend/BackendWordNarrowingTests.cs:33
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendWordNarrowingTests.cs:228
- method `s(1 TO 40)` — PowerBasic.Compiler.Tests/Backend/BackendWordNarrowingTests.cs:229
- method `Execute` — PowerBasic.Compiler.Tests/Backend/BackendWordNarrowingTests.cs:245
- method `s(1 TO 4)` — """ — PowerBasic.Compiler.Tests/Backend/BackendWordNarrowingTests.cs:286
- method `s(0)` — PowerBasic.Compiler.Tests/Backend/BackendWordNarrowingTests.cs:297
- method `t(-2 TO 2)` — """, — PowerBasic.Compiler.Tests/Backend/BackendWordNarrowingTests.cs:300
- method `g(2, 3)` — PowerBasic.Compiler.Tests/Backend/BackendWordNarrowingTests.cs:311
- method `foreach(var f in module!.Functions)` — PowerBasic.Compiler.Tests/Backend/BackendWordNarrowingTests.cs:319
- method `if(!f.IsDeclaration)` — PowerBasic.Compiler.Tests/Backend/BackendWordNarrowingTests.cs:357

### BackendWriteTests.cs  `C#, 118 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/BackendWriteTests.cs:5
- class `BackendWriteTests` — What WRITE renders a number as, which is not what STR$ renders it as and not what — PowerBasic.Compiler.Tests/Backend/BackendWriteTests.cs:27
- field `source` — PowerBasic.Compiler.Tests/Backend/BackendWriteTests.cs:90
- method `Written` — """; — PowerBasic.Compiler.Tests/Backend/BackendWriteTests.cs:104

### InstructionSelectorTests.cs  `C#, 242 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/InstructionSelectorTests.cs:4
- class `InstructionSelectorTests` — Stage 2 of the x86-16 back end (docs/X86-BACKEND.md): selecting the typed-SSA IR into the — PowerBasic.Compiler.Tests/Backend/InstructionSelectorTests.cs:12

### LinearScanAllocatorTests.cs  `C#, 206 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/LinearScanAllocatorTests.cs:4
- class `LinearScanAllocatorTests` — Stage 4 of the x86-16 back end (docs/X86-BACKEND.md): linear-scan register allocation. Overlapping — PowerBasic.Compiler.Tests/Backend/LinearScanAllocatorTests.cs:12
- method `if(x.VirtualId < y.VirtualId && x.Start <= y.End && y.Start <= x.End)` — PowerBasic.Compiler.Tests/Backend/LinearScanAllocatorTests.cs:35
- method `if(operand is MOperand.Memory { Index: not null, Base: { } b })` — PowerBasic.Compiler.Tests/Backend/LinearScanAllocatorTests.cs:172

### LivenessAnalysisTests.cs  `C#, 83 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/LivenessAnalysisTests.cs:3
- class `LivenessAnalysisTests` — Stage 3 of the x86-16 back end (docs/X86-BACKEND.md): live-interval analysis. Each virtual — PowerBasic.Compiler.Tests/Backend/LivenessAnalysisTests.cs:11

### MachineEmitterTests.cs  `C#, 207 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/MachineEmitterTests.cs:4
- class `MachineEmitterTests` — Stage 5 of the x86-16 back end (docs/X86-BACKEND.md): emission. The selected machine IR, once — PowerBasic.Compiler.Tests/Backend/MachineEmitterTests.cs:13
- method `if(haystack[i + k] != needle[k])` — PowerBasic.Compiler.Tests/Backend/MachineEmitterTests.cs:200

### MachineIrTests.cs  `C#, 57 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/MachineIrTests.cs:3
- class `MachineIrTests` — The machine-IR data model (docs/X86-BACKEND.md) the x86-16 back end selects into: virtual — PowerBasic.Compiler.Tests/Backend/MachineIrTests.cs:11

### MachineLoopRotationTests.cs  `C#, 94 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/MachineLoopRotationTests.cs:3
- class `MachineLoopRotationTests` — The SPEED-only machine rotation that keeps one entry guard and moves later tests to the latch. — PowerBasic.Compiler.Tests/Backend/MachineLoopRotationTests.cs:7

### MachineSchedulerTests.cs  `C#, 142 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/MachineSchedulerTests.cs:3
- class `MachineSchedulerTests` — Stage 6 of the x86-16 back end (docs/X86-BACKEND.md): scheduling the allocated machine IR. With — PowerBasic.Compiler.Tests/Backend/MachineSchedulerTests.cs:12
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: fals…` — PowerBasic.Compiler.Tests/Backend/MachineSchedulerTests.cs:117
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: false…` — PowerBasic.Compiler.Tests/Backend/MachineSchedulerTests.cs:121
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: fals…` — PowerBasic.Compiler.Tests/Backend/MachineSchedulerTests.cs:125
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: fals…` — PowerBasic.Compiler.Tests/Backend/MachineSchedulerTests.cs:129
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: true,…` — PowerBasic.Compiler.Tests/Backend/MachineSchedulerTests.cs:132

### MachineSchedulerX87Tests.cs  `C#, 66 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/MachineSchedulerX87Tests.cs:2
- class `MachineSchedulerX87Tests` — The x87 stack is a resource no can name, so the scheduler orders x87 — PowerBasic.Compiler.Tests/Backend/MachineSchedulerX87Tests.cs:16
- method `MInstr(MOpcode.Ret, [], MInstrEffect.None)` — PowerBasic.Compiler.Tests/Backend/MachineSchedulerX87Tests.cs:41

### O0355O0358MachineOptimizationTests.cs  `C#, 198 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/O0355O0358MachineOptimizationTests.cs:3
- class `O0355O0358MachineOptimizationTests` — Target-machine regression coverage for O0355-O0358. — PowerBasic.Compiler.Tests/Backend/O0355O0358MachineOptimizationTests.cs:7
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: source is MOperand.Register ? [1] : [], …` — PowerBasic.Compiler.Tests/Backend/O0355O0358MachineOptimizationTests.cs:15
- method `MInstrEffect(WrittenRegs: [], ReadRegs: source is MOperand.Register ? [1] : [], R…` — PowerBasic.Compiler.Tests/Backend/O0355O0358MachineOptimizationTests.cs:20
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: tru…` — PowerBasic.Compiler.Tests/Backend/O0355O0358MachineOptimizationTests.cs:25
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [0], ReadsFlags: false, WritesFlags: true…` — PowerBasic.Compiler.Tests/Backend/O0355O0358MachineOptimizationTests.cs:30
- method `MInstrEffect([], [], ReadsFlags: false, WritesFlags: true, ReadsMemory: true, Wri…` — PowerBasic.Compiler.Tests/Backend/O0355O0358MachineOptimizationTests.cs:35
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: tru…` — PowerBasic.Compiler.Tests/Backend/O0355O0358MachineOptimizationTests.cs:68
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: fals…` — PowerBasic.Compiler.Tests/Backend/O0355O0358MachineOptimizationTests.cs:95
- method `MInstr(MOpcode.Mov, [V(1), new MOperand.Register(source)], new MInstrEffect…` — PowerBasic.Compiler.Tests/Backend/O0355O0358MachineOptimizationTests.cs:114

### O0358SpillProvenanceTests.cs  `C#, 50 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/O0358SpillProvenanceTests.cs:3
- class `O0358SpillProvenanceTests` — PowerBasic.Compiler.Tests/Backend/O0358SpillProvenanceTests.cs:5

### OptimizeSpeedCorpusTests.cs  `C#, 191 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/OptimizeSpeedCorpusTests.cs:6
- class `OptimizeSpeedCorpusTests` — $OPTIMIZE SPEED may change the code however it likes and must not change what the program — PowerBasic.Compiler.Tests/Backend/OptimizeSpeedCorpusTests.cs:28
- record `Behaviour` — The 80x25 text page is part of this, because LOCATE and a line wrap move characters without changin… — PowerBasic.Compiler.Tests/Backend/OptimizeSpeedCorpusTests.cs:38
- method `if(cpu.FileContent(name) is { } content)` — PowerBasic.Compiler.Tests/Backend/OptimizeSpeedCorpusTests.cs:45
- method `Bind` — PowerBasic.Compiler.Tests/Backend/OptimizeSpeedCorpusTests.cs:66
- method `if(Bind().Errors.Count > 0)` — PowerBasic.Compiler.Tests/Backend/OptimizeSpeedCorpusTests.cs:73
- method `if(a.Errors.Count > 0 || b.Errors.Count > 0)` — PowerBasic.Compiler.Tests/Backend/OptimizeSpeedCorpusTests.cs:79
- method `if` — PowerBasic.Compiler.Tests/Backend/OptimizeSpeedCorpusTests.cs:84
- method `Bind` — PowerBasic.Compiler.Tests/Backend/OptimizeSpeedCorpusTests.cs:146
- method `if(Bind().Errors.Count > 0)` — PowerBasic.Compiler.Tests/Backend/OptimizeSpeedCorpusTests.cs:152
- method `if(a.Errors.Count > 0 || b.Errors.Count > 0)` — PowerBasic.Compiler.Tests/Backend/OptimizeSpeedCorpusTests.cs:158
- method `if` — PowerBasic.Compiler.Tests/Backend/OptimizeSpeedCorpusTests.cs:163

### PeepholeTests.cs  `C#, 332 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/PeepholeTests.cs:3
- class `PeepholeTests` — The encoding idioms folds over the selected machine IR: an ALU operand read — PowerBasic.Compiler.Tests/Backend/PeepholeTests.cs:15
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [1], ReadsFlags: false, WritesFlags: fal…` — PowerBasic.Compiler.Tests/Backend/PeepholeTests.cs:169
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [1], ReadsFlags: false, WritesFlags: fal…` — PowerBasic.Compiler.Tests/Backend/PeepholeTests.cs:184
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [1], ReadsFlags: false, WritesFlags: fal…` — PowerBasic.Compiler.Tests/Backend/PeepholeTests.cs:198
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [1], ReadsFlags: false, WritesFlags: fal…` — PowerBasic.Compiler.Tests/Backend/PeepholeTests.cs:256
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [1], ReadsFlags: false, WritesFlags: fal…` — PowerBasic.Compiler.Tests/Backend/PeepholeTests.cs:259

### UnoptimizedByteCompatibilityTests.cs  `C#, 91 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/UnoptimizedByteCompatibilityTests.cs:5
- class `UnoptimizedByteCompatibilityTests` — Whether the IR path could produce byte-identical output to the direct emitter with the optimizer — PowerBasic.Compiler.Tests/Backend/UnoptimizedByteCompatibilityTests.cs:25
- method `Bind()` — the preprocessor, not the lexer - see BackendCoverageTests for what tokenizing directly costs — PowerBasic.Compiler.Tests/Backend/UnoptimizedByteCompatibilityTests.cs:45
- method `if(Bind().Errors.Count > 0)` — PowerBasic.Compiler.Tests/Backend/UnoptimizedByteCompatibilityTests.cs:48
- method `if(direct.Errors.Count > 0 || routed.Errors.Count > 0 || !routed.Backen…` — PowerBasic.Compiler.Tests/Backend/UnoptimizedByteCompatibilityTests.cs:54
- method `if(a.SequenceEqual(b))` — PowerBasic.Compiler.Tests/Backend/UnoptimizedByteCompatibilityTests.cs:57
- method `if(delta < 0)` — PowerBasic.Compiler.Tests/Backend/UnoptimizedByteCompatibilityTests.cs:63
- method `if(delta > 0)` — PowerBasic.Compiler.Tests/Backend/UnoptimizedByteCompatibilityTests.cs:65

### Wave3SweepHarness.cs  `C#, 168 lines`
- namespace `PowerBasic.Compiler.Tests.Backend` — PowerBasic.Compiler.Tests/Backend/Wave3SweepHarness.cs:6
- class `Wave3SweepHarness` — Scratch sweep driver: every .BAS under $PBC_PROBE_DIR compiled both ways, both images run, and the — PowerBasic.Compiler.Tests/Backend/Wave3SweepHarness.cs:14
- record `Behaviour` — PowerBasic.Compiler.Tests/Backend/Wave3SweepHarness.cs:16
- method `new(cpu.Output, string.Join("\n", cpu.Screen), Attributes(cpu), cpu.Curs…` — PowerBasic.Compiler.Tests/Backend/Wave3SweepHarness.cs:40
- method `foreach` — PowerBasic.Compiler.Tests/Backend/Wave3SweepHarness.cs:90
- method `Bind()` — PowerBasic.Compiler.Tests/Backend/Wave3SweepHarness.cs:93
- method `if(bound.Errors.Count > 0)` — PowerBasic.Compiler.Tests/Backend/Wave3SweepHarness.cs:102
- method `if(direct.Errors.Count > 0)` — PowerBasic.Compiler.Tests/Backend/Wave3SweepHarness.cs:109
- method `if(routed.Errors.Count > 0)` — PowerBasic.Compiler.Tests/Backend/Wave3SweepHarness.cs:110
- method `if` — PowerBasic.Compiler.Tests/Backend/Wave3SweepHarness.cs:115
- method `if(directRun is null || routedRun is null)` — PowerBasic.Compiler.Tests/Backend/Wave3SweepHarness.cs:121
- method `if` — PowerBasic.Compiler.Tests/Backend/Wave3SweepHarness.cs:127
- method `Show(report, "output", directRun.Output, routedRun.Output)` — PowerBasic.Compiler.Tests/Backend/Wave3SweepHarness.cs:137
- method `Show(report, "screen", directRun.Screen, routedRun.Screen)` — PowerBasic.Compiler.Tests/Backend/Wave3SweepHarness.cs:138
- method `Show(report, "attrib", directRun.Attributes, routedRun.Attributes)` — PowerBasic.Compiler.Tests/Backend/Wave3SweepHarness.cs:139
- method `Show(report, "cursor", directRun.Cursor, routedRun.Cursor)` — PowerBasic.Compiler.Tests/Backend/Wave3SweepHarness.cs:140
- method `Show(report, "printer", directRun.Printer, routedRun.Printer)` — PowerBasic.Compiler.Tests/Backend/Wave3SweepHarness.cs:141
- method `Show(report, "files", directRun.Files, routedRun.Files)` — PowerBasic.Compiler.Tests/Backend/Wave3SweepHarness.cs:142
- method `if(directRun.ExitCode != routedRun.ExitCode)` — PowerBasic.Compiler.Tests/Backend/Wave3SweepHarness.cs:143

## PowerBasic.Compiler.Tests/Cli/

### EmitLlvmTests.cs  `C#, 46 lines`
- namespace `PowerBasic.Compiler.Tests.Cli` — PowerBasic.Compiler.Tests/Cli/EmitLlvmTests.cs:2
- class `EmitLlvmTests` — The pbc --emit-llvm front-end path: lower → optimize → emit textual LLVM. — PowerBasic.Compiler.Tests/Cli/EmitLlvmTests.cs:6

### EmitObjTests.cs  `C#, 65 lines`
- namespace `PowerBasic.Compiler.Tests.Cli` — PowerBasic.Compiler.Tests/Cli/EmitObjTests.cs:3
- class `EmitObjTests` — The pbc --emit-obj front-end path: compile a program's procedures to a linkable Intel OMF .OBJ. — PowerBasic.Compiler.Tests/Cli/EmitObjTests.cs:7

### LibBuildTests.cs  `C#, 69 lines`
- namespace `PowerBasic.Compiler.Tests.Cli` — PowerBasic.Compiler.Tests/Cli/LibBuildTests.cs:4
- class `LibBuildTests` — The pbc lib build path: a .LIB output is a foreign-consumable Intel OMF archive (otherwise our own … — PowerBasic.Compiler.Tests/Cli/LibBuildTests.cs:8

### ListTests.cs  `C#, 103 lines`
- namespace `PowerBasic.Compiler.Tests.Cli` — PowerBasic.Compiler.Tests/Cli/ListTests.cs:2
- class `ListTests` — The pbc --list front-end path: compile a program and write a human-readable .LST map of the emitted… — PowerBasic.Compiler.Tests/Cli/ListTests.cs:6

### XBackendTests.cs  `C#, 50 lines`
- namespace `PowerBasic.Compiler.Tests.Cli` — PowerBasic.Compiler.Tests/Cli/XBackendTests.cs:3
- class `XBackendTests` — PowerBasic.Compiler.Tests/Cli/XBackendTests.cs:5

## PowerBasic.Compiler.Tests/CodeGen/

### ArraySliceTests.cs  `C#, 108 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/ArraySliceTests.cs:4
- class `ArraySliceTests` — pb36 array slices: b() = a(lo TO hi) copies the slice into a dynamic array — PowerBasic.Compiler.Tests/CodeGen/ArraySliceTests.cs:13
- field `source` — PowerBasic.Compiler.Tests/CodeGen/ArraySliceTests.cs:43
- method `b()` — PowerBasic.Compiler.Tests/CodeGen/ArraySliceTests.cs:44
- method `LBOUND(b)` — PowerBasic.Compiler.Tests/CodeGen/ArraySliceTests.cs:46
- field `source` — PowerBasic.Compiler.Tests/CodeGen/ArraySliceTests.cs:53
- method `b()` — PowerBasic.Compiler.Tests/CodeGen/ArraySliceTests.cs:54
- method `b(0)` — PowerBasic.Compiler.Tests/CodeGen/ArraySliceTests.cs:58
- field `source` — PowerBasic.Compiler.Tests/CodeGen/ArraySliceTests.cs:66
- method `a(lo TO 7)` — PowerBasic.Compiler.Tests/CodeGen/ArraySliceTests.cs:69

### AutoVectorizeTests.cs  `C#, 100 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/AutoVectorizeTests.cs:4
- class `AutoVectorizeTests` — pb36 R4 auto-vectorisation: a constant-trip FOR i: c(i) = a(i) OP b(i) over rank-1 — PowerBasic.Compiler.Tests/CodeGen/AutoVectorizeTests.cs:14
- method `if(image[i + k] != pattern[k])` — PowerBasic.Compiler.Tests/CodeGen/AutoVectorizeTests.cs:32

### BSaveBLoadTests.cs  `C#, 92 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/BSaveBLoadTests.cs:5
- class `BSaveBLoadTests` — BSAVE and BLOAD - a block of DEF SEG written to a file and read back. — PowerBasic.Compiler.Tests/CodeGen/BSaveBLoadTests.cs:20
- method `VARPTR(b%(1))` — PowerBasic.Compiler.Tests/CodeGen/BSaveBLoadTests.cs:38
- method `VARPTR(b%(0))` — PowerBasic.Compiler.Tests/CodeGen/BSaveBLoadTests.cs:55
- method `VARPTR(b%(1))` — PowerBasic.Compiler.Tests/CodeGen/BSaveBLoadTests.cs:72
- method `VARPTR(b%(1))` — PowerBasic.Compiler.Tests/CodeGen/BSaveBLoadTests.cs:87

### ByteCounterLimitFoldTests.cs  `C#, 115 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/ByteCounterLimitFoldTests.cs:5
- class `ByteCounterLimitFoldTests` — O0113 on a BYTE counter: a constant FOR limit folds into the compare as an immediate. — PowerBasic.Compiler.Tests/CodeGen/ByteCounterLimitFoldTests.cs:34

### CInteropTests.cs  `C#, 703 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/CInteropTests.cs:7
- class `CInteropTests` — Cross-compiler OMF interop (docs/LINKER.md): prove that our object — PowerBasic.Compiler.Tests/CodeGen/CInteropTests.cs:33
- record `ForeignCc` — A staged foreign C toolchain and how to drive it under DOSBox (slot mounted as C:, scratch as D:, c… — PowerBasic.Compiler.Tests/CodeGen/CInteropTests.cs:41
- method `ToString()` — PowerBasic.Compiler.Tests/CodeGen/CInteropTests.cs:42
- field `source` — PowerBasic.Compiler.Tests/CodeGen/CInteropTests.cs:86
- record `CRuntimeCase` — A foreign object that needs the C runtime, plus the small-model lib that satisfies it. — PowerBasic.Compiler.Tests/CodeGen/CInteropTests.cs:120
- method `ToString()` — PowerBasic.Compiler.Tests/CodeGen/CInteropTests.cs:121
- field `source` — PowerBasic.Compiler.Tests/CodeGen/CInteropTests.cs:152
- record `SprintfCase` — A compiler whose small-model CRT links sprintf without the c0 startup, and that lib. — PowerBasic.Compiler.Tests/CodeGen/CInteropTests.cs:208
- method `ToString()` — PowerBasic.Compiler.Tests/CodeGen/CInteropTests.cs:209
- field `source` — PowerBasic.Compiler.Tests/CodeGen/CInteropTests.cs:270
- method `if(slot == 0)` — PowerBasic.Compiler.Tests/CodeGen/CInteropTests.cs:409
- record `ConvCase` — A foreign object exporting sub2(a,b)=a-b under a convention, and how BASIC declares it. — PowerBasic.Compiler.Tests/CodeGen/CInteropTests.cs:447
- method `ToString()` — PowerBasic.Compiler.Tests/CodeGen/CInteropTests.cs:448
- field `mangled` — the mangled public must be present exactly as Borland decorates a free int square(int) — PowerBasic.Compiler.Tests/CodeGen/CInteropTests.cs:518
- field `source` — PowerBasic.Compiler.Tests/CodeGen/CInteropTests.cs:568

### CallingConventionTests.cs  `C#, 292 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/CallingConventionTests.cs:5
- class `CallingConventionTests` — Register calling conventions (docs/LINKER.md): WATCALL (Watcom: args in AX,DX,BX,CX, — PowerBasic.Compiler.Tests/CodeGen/CallingConventionTests.cs:16
- field `source` — PowerBasic.Compiler.Tests/CodeGen/CallingConventionTests.cs:32
- method `WATCALL(BYVAL a AS INTEGER, BYVAL b AS INTEGER)` — PowerBasic.Compiler.Tests/CodeGen/CallingConventionTests.cs:33
- field `source` — PowerBasic.Compiler.Tests/CodeGen/CallingConventionTests.cs:44
- method `FASTCALL(BYVAL a AS INTEGER, BYVAL b AS INTEGER)` — PowerBasic.Compiler.Tests/CodeGen/CallingConventionTests.cs:45
- field `source` — a,b,c,d -> AX,DX,BX,CX ; e -> stack ; callee cleans the one overflow word (RET 2) — PowerBasic.Compiler.Tests/CodeGen/CallingConventionTests.cs:57
- method `WATCALL(BYVAL a AS INTEGER, BYVAL b AS INTEGER, BYVAL c AS INTEGER, BYVAL d …` — PowerBasic.Compiler.Tests/CodeGen/CallingConventionTests.cs:58
- field `source` — a,b,c -> AX,DX,BX ; d,e -> stack ; callee cleans the two overflow words (RET 4) — PowerBasic.Compiler.Tests/CodeGen/CallingConventionTests.cs:70
- method `FASTCALL(BYVAL a AS INTEGER, BYVAL b AS INTEGER, BYVAL c AS INTEGER, BYVAL d …` — PowerBasic.Compiler.Tests/CodeGen/CallingConventionTests.cs:71
- field `source` — a SUB (no return) with register args, observed via its side effect — PowerBasic.Compiler.Tests/CodeGen/CallingConventionTests.cs:83
- method `WATCALL(BYVAL a AS INTEGER, BYVAL b AS INTEGER)` — PowerBasic.Compiler.Tests/CodeGen/CallingConventionTests.cs:84
- method `addw(BYVAL a AS INTEGER, BYVAL b AS INTEGER)` — PowerBasic.Compiler.Tests/CodeGen/CallingConventionTests.cs:105
- method `f(BYVAL x AS LONG)` — PowerBasic.Compiler.Tests/CodeGen/CallingConventionTests.cs:119
- method `addw(BYVAL a AS INTEGER, BYVAL b AS INTEGER)` — PowerBasic.Compiler.Tests/CodeGen/CallingConventionTests.cs:133
- method `g(2, 3)` — PowerBasic.Compiler.Tests/CodeGen/CallingConventionTests.cs:150
- field `source` — recursion is never inlined, so the register call path + per-frame spill are exercised — PowerBasic.Compiler.Tests/CodeGen/CallingConventionTests.cs:173
- method `fact(BYVAL n AS INTEGER)` — PowerBasic.Compiler.Tests/CodeGen/CallingConventionTests.cs:174
- field `source` — a multi-statement body keeps the proc out of the trivial inliner so the real — PowerBasic.Compiler.Tests/CodeGen/CallingConventionTests.cs:191
- method `calc(BYVAL a AS INTEGER, BYVAL b AS INTEGER, BYVAL c AS INTEGER, BYVAL d …` — PowerBasic.Compiler.Tests/CodeGen/CallingConventionTests.cs:192
- field `source` — a LONG does not fit the common-case word model; reject rather than silently miscompile — PowerBasic.Compiler.Tests/CodeGen/CallingConventionTests.cs:219
- method `WATCALL(BYVAL x AS LONG)` — PowerBasic.Compiler.Tests/CodeGen/CallingConventionTests.cs:220
- method `sub2(20, 7)` — PowerBasic.Compiler.Tests/CodeGen/CallingConventionTests.cs:244
- method `sub2(20, 7)` — PowerBasic.Compiler.Tests/CodeGen/CallingConventionTests.cs:274

### CeilFracTests.cs  `C#, 69 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/CeilFracTests.cs:5
- class `CeilFracTests` — CEIL and FRAC, two of the built-ins the intrinsic census found binding and — PowerBasic.Compiler.Tests/CodeGen/CeilFracTests.cs:21
- method `CEIL(n)` — PowerBasic.Compiler.Tests/CodeGen/CeilFracTests.cs:57
- method `CEIL(x)` — PowerBasic.Compiler.Tests/CodeGen/CeilFracTests.cs:66

### CircleStatementTests.cs  `C#, 146 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/CircleStatementTests.cs:5
- class `CircleStatementTests` — CIRCLE, checked by reading the pixels back with POINT. — PowerBasic.Compiler.Tests/CodeGen/CircleStatementTests.cs:16
- method `POINT(50, 40)` — PowerBasic.Compiler.Tests/CodeGen/CircleStatementTests.cs:34
- method `POINT(45, 40)` — PowerBasic.Compiler.Tests/CodeGen/CircleStatementTests.cs:41
- method `POINT(60, 45)` — PowerBasic.Compiler.Tests/CodeGen/CircleStatementTests.cs:49
- method `POINT(12, 40)` — PowerBasic.Compiler.Tests/CodeGen/CircleStatementTests.cs:60
- method `POINT(20, 20)` — PowerBasic.Compiler.Tests/CodeGen/CircleStatementTests.cs:67
- method `CIRCLE` — PowerBasic.Compiler.Tests/CodeGen/CircleStatementTests.cs:79
- method `POINT(80, 60)` — PowerBasic.Compiler.Tests/CodeGen/CircleStatementTests.cs:100
- method `POINT(40, 60)` — PowerBasic.Compiler.Tests/CodeGen/CircleStatementTests.cs:108
- method `POINT(80, 60)` — PowerBasic.Compiler.Tests/CodeGen/CircleStatementTests.cs:116
- method `POINT(80, 60)` — PowerBasic.Compiler.Tests/CodeGen/CircleStatementTests.cs:127
- method `POINT(80, 60)` — PowerBasic.Compiler.Tests/CodeGen/CircleStatementTests.cs:135
- method `POINT(60, 60)` — PowerBasic.Compiler.Tests/CodeGen/CircleStatementTests.cs:143

### CopyPropTests.cs  `C#, 58 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/CopyPropTests.cs:6
- class `CopyPropTests` — pb36 copy propagation (OptCopyProp): a copy y = x redirects reads of y to x and the — PowerBasic.Compiler.Tests/CodeGen/CopyPropTests.cs:14

### CorpusCompileTests.cs  `C#, 88 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/CorpusCompileTests.cs:4
- class `CorpusCompileTests` — Full-pipeline backend gate: every PB-SvgaLibrary test suite must compile — PowerBasic.Compiler.Tests/CodeGen/CorpusCompileTests.cs:12
- class `SvgaBuildDirProvider` — Mirrors the SVGA harness: SVGAENG.SUB = SVGA.SUB minus its $INCLUDE lines. — PowerBasic.Compiler.Tests/CodeGen/CorpusCompileTests.cs:18
- method `TryReadSource` — PowerBasic.Compiler.Tests/CodeGen/CorpusCompileTests.cs:20
- method `new(suite)` — PowerBasic.Compiler.Tests/CodeGen/CorpusCompileTests.cs:48

### CorpusRunTests.cs  `C#, 132 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/CorpusRunTests.cs:5
- class `CorpusRunTests` — Backend run gate: selected PB-SvgaLibrary suites are compiled (with the — PowerBasic.Compiler.Tests/CodeGen/CorpusRunTests.cs:14
- class `DriverSourceProvider` — SVGA build-dir provider that additionally serves the driver-amended main file. — PowerBasic.Compiler.Tests/CodeGen/CorpusRunTests.cs:48
- method `TryReadSource` — PowerBasic.Compiler.Tests/CodeGen/CorpusRunTests.cs:50
- method `if(!files.TryGetValue("UNITTEST.LOG", out var log))` — PowerBasic.Compiler.Tests/CodeGen/CorpusRunTests.cs:116

### CrossBlockCseTests.cs  `C#, 117 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/CrossBlockCseTests.cs:4
- class `CrossBlockCseTests` — pb36 cross-block common-subexpression elimination: a value computed before an IF is — PowerBasic.Compiler.Tests/CodeGen/CrossBlockCseTests.cs:13

### CseSlotReuseTests.cs  `C#, 101 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/CseSlotReuseTests.cs:4
- class `CseSlotReuseTests` — PowerBasic.Compiler.Tests/CodeGen/CseSlotReuseTests.cs:6

### DeadInterpreterTextTests.cs  `C#, 90 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/DeadInterpreterTextTests.cs:4
- class `DeadInterpreterTextTests` — BASICA and GW-BASIC store a line without validating every statement on it, so unparseable text — PowerBasic.Compiler.Tests/CodeGen/DeadInterpreterTextTests.cs:21

### DirectoryCommandTests.cs  `C#, 84 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/DirectoryCommandTests.cs:5
- class `DirectoryCommandTests` — MKDIR / RMDIR / CHDIR: three DOS calls that had no code generator, so a — PowerBasic.Compiler.Tests/CodeGen/DirectoryCommandTests.cs:17

### DiscriminatedUnionTests.cs  `C#, 205 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/DiscriminatedUnionTests.cs:5
- class `DiscriminatedUnionTests` — pb36 discriminated unions: a UNION whose members are CASEs with per-case payload fields. — PowerBasic.Compiler.Tests/CodeGen/DiscriminatedUnionTests.cs:15
- field `source` — PowerBasic.Compiler.Tests/CodeGen/DiscriminatedUnionTests.cs:92
- method `Show(BYREF s AS Shape)` — PowerBasic.Compiler.Tests/CodeGen/DiscriminatedUnionTests.cs:93
- field `source` — PowerBasic.Compiler.Tests/CodeGen/DiscriminatedUnionTests.cs:113
- field `source` — PowerBasic.Compiler.Tests/CodeGen/DiscriminatedUnionTests.cs:129
- field `source` — PowerBasic.Compiler.Tests/CodeGen/DiscriminatedUnionTests.cs:151
- method `Show(BYREF s AS Shape)` — PowerBasic.Compiler.Tests/CodeGen/DiscriminatedUnionTests.cs:152

### DivideByMinusOneTests.cs  `C#, 152 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/DivideByMinusOneTests.cs:5
- class `DivideByMinusOneTests` — O0080: x \ -1 becomes NEG, but only where MININT is ruled out. — PowerBasic.Compiler.Tests/CodeGen/DivideByMinusOneTests.cs:21
- field `source` — PowerBasic.Compiler.Tests/CodeGen/DivideByMinusOneTests.cs:142

### DoLoopLicmTests.cs  `C#, 89 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/DoLoopLicmTests.cs:5
- class `DoLoopLicmTests` — Loop-invariant code motion for DO/WHILE loops ($OPTIMIZE SPEED). LICM previously — PowerBasic.Compiler.Tests/CodeGen/DoLoopLicmTests.cs:14
- field `source` — PowerBasic.Compiler.Tests/CodeGen/DoLoopLicmTests.cs:64

### DosBoxRunner.cs  `C#, 344 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/DosBoxRunner.cs:3
- class `DosBoxRunner` — Runs a generated DOS executable under DOSBox (headless-ish) and captures the — PowerBasic.Compiler.Tests/CodeGen/DosBoxRunner.cs:11
- method `Collect(object _, DataReceivedEventArgs e)` — PowerBasic.Compiler.Tests/CodeGen/DosBoxRunner.cs:108
- method `if(!minimized)` — PowerBasic.Compiler.Tests/CodeGen/DosBoxRunner.cs:202
- method `if(finished)` — PowerBasic.Compiler.Tests/CodeGen/DosBoxRunner.cs:208
- method `foreach(var (name, content) in extraFiles)` — PowerBasic.Compiler.Tests/CodeGen/DosBoxRunner.cs:244
- method `if(!minimized)` — PowerBasic.Compiler.Tests/CodeGen/DosBoxRunner.cs:277
- method `if(completed)` — PowerBasic.Compiler.Tests/CodeGen/DosBoxRunner.cs:289
- method `if(File.Exists(path))` — PowerBasic.Compiler.Tests/CodeGen/DosBoxRunner.cs:304

### DrawStatementTests.cs  `C#, 130 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/DrawStatementTests.cs:5
- class `DrawStatementTests` — DRAW with a written-down string, checked by reading the pixels back with POINT. — PowerBasic.Compiler.Tests/CodeGen/DrawStatementTests.cs:20
- method `POINT(15, 10)` — PowerBasic.Compiler.Tests/CodeGen/DrawStatementTests.cs:39
- method `POINT({x}, {y})` — PowerBasic.Compiler.Tests/CodeGen/DrawStatementTests.cs:55
- method `POINT(41, 40)` — PowerBasic.Compiler.Tests/CodeGen/DrawStatementTests.cs:64
- method `POINT(15, 20)` — PowerBasic.Compiler.Tests/CodeGen/DrawStatementTests.cs:73
- method `POINT(14, 30)` — PowerBasic.Compiler.Tests/CodeGen/DrawStatementTests.cs:85
- method `POINT(12, 40)` — PowerBasic.Compiler.Tests/CodeGen/DrawStatementTests.cs:94
- method `POINT(13, 50)` — PowerBasic.Compiler.Tests/CodeGen/DrawStatementTests.cs:103

### EightySevenTrigTests.cs  `C#, 119 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/EightySevenTrigTests.cs:5
- class `EightySevenTrigTests` — SIN and COS below a 386, computed with instructions an 8087 actually has. — PowerBasic.Compiler.Tests/CodeGen/EightySevenTrigTests.cs:25

### ElseIfProgramPointTests.cs  `C#, 113 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/ElseIfProgramPointTests.cs:5
- class `ElseIfProgramPointTests` — The program point an ELSEIF condition is judged at. — PowerBasic.Compiler.Tests/CodeGen/ElseIfProgramPointTests.cs:22

### EnvironFunctionTests.cs  `C#, 124 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/EnvironFunctionTests.cs:5
- class `EnvironFunctionTests` — ENVIRON$, which has shipped since it was written and had no test. — PowerBasic.Compiler.Tests/CodeGen/EnvironFunctionTests.cs:22

### ExecutionTests.cs  `C#, 152 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/ExecutionTests.cs:5
- class `ExecutionTests` — True end-to-end tests: AST -> binder -> code generator -> MZ EXE -> DOSBox. — PowerBasic.Compiler.Tests/CodeGen/ExecutionTests.cs:12
- method `AssignStmt(_pos, Name("a"), Int(11))` — PowerBasic.Compiler.Tests/CodeGen/ExecutionTests.cs:77
- method `AssignStmt(_pos, Name("i"), Int(10))` — PowerBasic.Compiler.Tests/CodeGen/ExecutionTests.cs:86
- method `ForStmt(_pos, Name("i"), Int(1), Int(5), null, [ new PrintStmt(_pos, null, f…` — PowerBasic.Compiler.Tests/CodeGen/ExecutionTests.cs:95
- method `AssignStmt(_pos, Name("i"), Int(0))` — PowerBasic.Compiler.Tests/CodeGen/ExecutionTests.cs:104
- method `GosubStmt(_pos, "sr")` — PowerBasic.Compiler.Tests/CodeGen/ExecutionTests.cs:126

### FastVideoTests.cs  `C#, 122 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/FastVideoTests.cs:4
- class `FastVideoTests` — R1 fast video PRINT ($OPTION VIDEO): console PRINT writes glyphs straight into B800 text — PowerBasic.Compiler.Tests/CodeGen/FastVideoTests.cs:13
- field `subject` — O10 console-setter coalescing drops the shadowed LOCATEs/CLS - the observable — PowerBasic.Compiler.Tests/CodeGen/FastVideoTests.cs:69
- field `subject` — R2: PSET/POINT are direct A000 stores/loads (mode 13h linear addressing) - no BIOS — PowerBasic.Compiler.Tests/CodeGen/FastVideoTests.cs:92
- method `PSET` — PowerBasic.Compiler.Tests/CodeGen/FastVideoTests.cs:94
- field `subject` — TAB (control char) and a >80-column line take the DOS fallback inside the fast build; — PowerBasic.Compiler.Tests/CodeGen/FastVideoTests.cs:112

### FileAttrTests.cs  `C#, 104 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/FileAttrTests.cs:5
- class `FileAttrTests` — FILEATTR(n, 1) - the mode a file was opened in. — PowerBasic.Compiler.Tests/CodeGen/FileAttrTests.cs:21
- method `FILEATTR(1, 1)` — PowerBasic.Compiler.Tests/CodeGen/FileAttrTests.cs:37
- method `FILEATTR(1, 1)` — PowerBasic.Compiler.Tests/CodeGen/FileAttrTests.cs:47
- method `FILEATTR(1, 1)` — PowerBasic.Compiler.Tests/CodeGen/FileAttrTests.cs:62
- method `FILEATTR(1, 1)` — PowerBasic.Compiler.Tests/CodeGen/FileAttrTests.cs:70
- method `FILEATTR(1, 1)` — PowerBasic.Compiler.Tests/CodeGen/FileAttrTests.cs:82
- method `FILEATTR(1, 2)` — PowerBasic.Compiler.Tests/CodeGen/FileAttrTests.cs:91

### FloatResultForwardingTests.cs  `C#, 117 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/FloatResultForwardingTests.cs:5
- class `FloatResultForwardingTests` — O0102 for SINGLE and DOUBLE results. — PowerBasic.Compiler.Tests/CodeGen/FloatResultForwardingTests.cs:25

### FpuAssume.cs  `C#, 14 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/FpuAssume.cs:1
- class `FpuAssume` — Some fidelity assertions depend on real 80-bit x87 rounding (verified — PowerBasic.Compiler.Tests/CodeGen/FpuAssume.cs:9

### GetPutGraphicsTests.cs  `C#, 105 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/GetPutGraphicsTests.cs:5
- class `GetPutGraphicsTests` — GET and PUT in their graphics form - sprite capture and blit - checked by reading — PowerBasic.Compiler.Tests/CodeGen/GetPutGraphicsTests.cs:21
- method `POINT(20, 20)` — PowerBasic.Compiler.Tests/CodeGen/GetPutGraphicsTests.cs:47
- method `POINT(20, 20)` — PowerBasic.Compiler.Tests/CodeGen/GetPutGraphicsTests.cs:64
- method `POINT(20, 20)` — PowerBasic.Compiler.Tests/CodeGen/GetPutGraphicsTests.cs:66
- method `POINT(20, 20)` — PowerBasic.Compiler.Tests/CodeGen/GetPutGraphicsTests.cs:78
- method `POINT(20, 20)` — PowerBasic.Compiler.Tests/CodeGen/GetPutGraphicsTests.cs:86

### GoldenTests.cs  `C#, 63 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/GoldenTests.cs:4
- class `GoldenTests` — Source-to-DOSBox golden tests: every tests/NAME.BAS is compiled through the — PowerBasic.Compiler.Tests/CodeGen/GoldenTests.cs:11

### InOperatorTests.cs  `C#, 109 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/InOperatorTests.cs:5
- class `InOperatorTests` — pb36 membership test: x IN lo TO hi is (x &gt;= lo) AND (x &lt;= hi), — PowerBasic.Compiler.Tests/CodeGen/InOperatorTests.cs:14
- field `source` — PowerBasic.Compiler.Tests/CodeGen/InOperatorTests.cs:72
- field `source` — PowerBasic.Compiler.Tests/CodeGen/InOperatorTests.cs:87
- field `source` — PowerBasic.Compiler.Tests/CodeGen/InOperatorTests.cs:99

### InlineAsm8086ShiftTests.cs  `C#, 124 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/InlineAsm8086ShiftTests.cs:6
- class `InlineAsm8086ShiftTests` — PowerBasic.Compiler.Tests/CodeGen/InlineAsm8086ShiftTests.cs:8

### InlineAsmBitManipulationSemanticTests.cs  `C#, 85 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmBitManipulationSemanticTests.cs:5
- class `InlineAsmBitManipulationSemanticTests` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmBitManipulationSemanticTests.cs:7

### InlineAsmBmiSemanticTests.cs  `C#, 240 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmBmiSemanticTests.cs:5
- class `InlineAsmBmiSemanticTests` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmBmiSemanticTests.cs:7

### InlineAsmCanonicalizerTests.cs  `C#, 29 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmCanonicalizerTests.cs:2
- class `InlineAsmCanonicalizerTests` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmCanonicalizerTests.cs:4

### InlineAsmCrc32SemanticTests.cs  `C#, 66 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmCrc32SemanticTests.cs:5
- class `InlineAsmCrc32SemanticTests` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmCrc32SemanticTests.cs:7

### InlineAsmCryptoSemanticTests.cs  `C#, 126 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmCryptoSemanticTests.cs:5
- class `InlineAsmCryptoSemanticTests` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmCryptoSemanticTests.cs:7

### InlineAsmExtendedSimdSemanticTests.cs  `C#, 144 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmExtendedSimdSemanticTests.cs:5
- class `InlineAsmExtendedSimdSemanticTests` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmExtendedSimdSemanticTests.cs:7

### InlineAsmGp32SemanticTests.cs  `C#, 193 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmGp32SemanticTests.cs:5
- class `InlineAsmGp32SemanticTests` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmGp32SemanticTests.cs:7

### InlineAsmHorizontalSimdSemanticTests.cs  `C#, 89 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmHorizontalSimdSemanticTests.cs:5
- class `InlineAsmHorizontalSimdSemanticTests` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmHorizontalSimdSemanticTests.cs:7

### InlineAsmIsaCoverageTests.cs  `C#, 119 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmIsaCoverageTests.cs:6
- class `InlineAsmIsaCoverageTests` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmIsaCoverageTests.cs:8
- record `AdvertisedInstruction` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmIsaCoverageTests.cs:11
- method `foreach(var policy in _policies)` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmIsaCoverageTests.cs:32
- method `AssertResolution(instruction, targetName, policy, failures)` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmIsaCoverageTests.cs:33

### InlineAsmPackedStringBoundaryTests.cs  `C#, 77 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmPackedStringBoundaryTests.cs:5
- class `InlineAsmPackedStringBoundaryTests` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmPackedStringBoundaryTests.cs:7

### InlineAsmPackedStringSemanticTests.cs  `C#, 333 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmPackedStringSemanticTests.cs:5
- class `InlineAsmPackedStringSemanticTests` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmPackedStringSemanticTests.cs:7

### InlineAsmPopcntPolicyTests.cs  `C#, 28 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmPopcntPolicyTests.cs:3
- class `InlineAsmPopcntPolicyTests` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmPopcntPolicyTests.cs:5

### InlineAsmPopcntSemanticTests.cs  `C#, 84 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmPopcntSemanticTests.cs:5
- class `InlineAsmPopcntSemanticTests` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmPopcntSemanticTests.cs:7

### InlineAsmSchedulerTests.cs  `C#, 62 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmSchedulerTests.cs:2
- class `InlineAsmSchedulerTests` — pb36 inline-asm scheduler: reorders a run of single-instruction ! lines to group memory and — PowerBasic.Compiler.Tests/CodeGen/InlineAsmSchedulerTests.cs:11

### InlineAsmShuffleSemanticTests.cs  `C#, 58 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmShuffleSemanticTests.cs:5
- class `InlineAsmShuffleSemanticTests` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmShuffleSemanticTests.cs:7

### InlineAsmSsse3ArithmeticSemanticTests.cs  `C#, 89 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmSsse3ArithmeticSemanticTests.cs:5
- class `InlineAsmSsse3ArithmeticSemanticTests` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmSsse3ArithmeticSemanticTests.cs:7

### InlineAsmZeroOverheadTests.cs  `C#, 77 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmZeroOverheadTests.cs:4
- class `InlineAsmZeroOverheadTests` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmZeroOverheadTests.cs:6
- field `source` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmZeroOverheadTests.cs:32
- field `source` — PowerBasic.Compiler.Tests/CodeGen/InlineAsmZeroOverheadTests.cs:48

### InlineByRefBindingTests.cs  `C#, 115 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/InlineByRefBindingTests.cs:5
- class `InlineByRefBindingTests` — How the inliner binds a BYREF parameter, and the one shape it got wrong. — PowerBasic.Compiler.Tests/CodeGen/InlineByRefBindingTests.cs:34
- field `source` — PowerBasic.Compiler.Tests/CodeGen/InlineByRefBindingTests.cs:87

### IntervalRangeTests.cs  `C#, 429 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/IntervalRangeTests.cs:4
- class `IntervalRangeTests` — O16 value facts: interval arithmetic plus the forward reduced-product analysis over a bound — PowerBasic.Compiler.Tests/CodeGen/IntervalRangeTests.cs:12

### IterateTests.cs  `C#, 94 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/IterateTests.cs:5
- class `IterateTests` — ITERATE - continue with the next loop pass. ITERATE FOR jumps to the FOR — PowerBasic.Compiler.Tests/CodeGen/IterateTests.cs:14
- field `source` — PowerBasic.Compiler.Tests/CodeGen/IterateTests.cs:31
- field `source` — PowerBasic.Compiler.Tests/CodeGen/IterateTests.cs:43
- field `source` — ITERATE DO from inside the inner FOR must resume the enclosing DO's retest, — PowerBasic.Compiler.Tests/CodeGen/IterateTests.cs:59
- field `source` — a bare ITERATE targets the innermost loop of ANY kind; the decompiled spelling — PowerBasic.Compiler.Tests/CodeGen/IterateTests.cs:78

### LPrintStatementTests.cs  `C#, 80 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/LPrintStatementTests.cs:5
- class `LPrintStatementTests` — LPRINT, which is PRINT with the output pointed at DOS handle 4 (PRN). — PowerBasic.Compiler.Tests/CodeGen/LPrintStatementTests.cs:16
- method `LPOS(0)` — PowerBasic.Compiler.Tests/CodeGen/LPrintStatementTests.cs:62
- method `LPOS(0)` — PowerBasic.Compiler.Tests/CodeGen/LPrintStatementTests.cs:64
- method `POS(0)` — PowerBasic.Compiler.Tests/CodeGen/LPrintStatementTests.cs:77

### LineStatementTests.cs  `C#, 146 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/LineStatementTests.cs:5
- class `LineStatementTests` — LINE in every spelling, checked by reading the pixels back with POINT rather than by — PowerBasic.Compiler.Tests/CodeGen/LineStatementTests.cs:18
- method `POINT(9, 5)` — PowerBasic.Compiler.Tests/CodeGen/LineStatementTests.cs:36
- method `POINT(7, 1)` — PowerBasic.Compiler.Tests/CodeGen/LineStatementTests.cs:44
- method `POINT(0, 0)` — PowerBasic.Compiler.Tests/CodeGen/LineStatementTests.cs:52
- method `POINT(10, 5)` — PowerBasic.Compiler.Tests/CodeGen/LineStatementTests.cs:60
- method `POINT(6, 5)` — PowerBasic.Compiler.Tests/CodeGen/LineStatementTests.cs:72
- method `POINT(2, 1)` — PowerBasic.Compiler.Tests/CodeGen/LineStatementTests.cs:79
- method `POINT(2, 2)` — PowerBasic.Compiler.Tests/CodeGen/LineStatementTests.cs:87
- method `POINT(2, 2)` — PowerBasic.Compiler.Tests/CodeGen/LineStatementTests.cs:94
- method `POINT(2, 2)` — PowerBasic.Compiler.Tests/CodeGen/LineStatementTests.cs:101
- method `POINT(0, 9)` — PowerBasic.Compiler.Tests/CodeGen/LineStatementTests.cs:112
- method `POINT(0, 11)` — PowerBasic.Compiler.Tests/CodeGen/LineStatementTests.cs:123
- method `POINT(2, 2)` — PowerBasic.Compiler.Tests/CodeGen/LineStatementTests.cs:130
- method `POINT(42, 40)` — PowerBasic.Compiler.Tests/CodeGen/LineStatementTests.cs:143

### LinkOracleTests.cs  `C#, 370 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/LinkOracleTests.cs:8
- class `LinkOracleTests` — Differential oracle for the OMF object linker (docs/LINKER.md): validate that — PowerBasic.Compiler.Tests/CodeGen/LinkOracleTests.cs:33
- field `fixDat` — PowerBasic.Compiler.Tests/CodeGen/LinkOracleTests.cs:102
- method `Record(0x80, Str("MAIN"))` — PowerBasic.Compiler.Tests/CodeGen/LinkOracleTests.cs:104
- field `source` — PowerBasic.Compiler.Tests/CodeGen/LinkOracleTests.cs:128

### LiteralStringComparisonFoldTests.cs  `C#, 105 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/LiteralStringComparisonFoldTests.cs:5
- class `LiteralStringComparisonFoldTests` — O0299 asked for an identity comparison between interned literals — two pool references being — PowerBasic.Compiler.Tests/CodeGen/LiteralStringComparisonFoldTests.cs:21
- method `if(image[i + j] != needle[j])` — PowerBasic.Compiler.Tests/CodeGen/LiteralStringComparisonFoldTests.cs:38

### LongOverflowTests.cs  `C#, 133 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/LongOverflowTests.cs:5
- class `LongOverflowTests` — Where a LONG +/- overflow wraps, and where it does not. — PowerBasic.Compiler.Tests/CodeGen/LongOverflowTests.cs:28
- method `Opaque(a)` — PowerBasic.Compiler.Tests/CodeGen/LongOverflowTests.cs:54
- method `Opaque(a)` — PowerBasic.Compiler.Tests/CodeGen/LongOverflowTests.cs:64
- method `Opaque(a)` — PowerBasic.Compiler.Tests/CodeGen/LongOverflowTests.cs:75
- method `Opaque(a)` — PowerBasic.Compiler.Tests/CodeGen/LongOverflowTests.cs:90
- method `Opaque(a)` — PowerBasic.Compiler.Tests/CodeGen/LongOverflowTests.cs:117
- method `Opaque(a)` — PowerBasic.Compiler.Tests/CodeGen/LongOverflowTests.cs:128

### LongSubjectBitMaskTests.cs  `C#, 153 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/LongSubjectBitMaskTests.cs:5
- class `LongSubjectBitMaskTests` — O0099 over a LONG SELECT subject. — PowerBasic.Compiler.Tests/CodeGen/LongSubjectBitMaskTests.cs:34

### LongSubjectDecisionTreeTests.cs  `C#, 144 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/LongSubjectDecisionTreeTests.cs:5
- class `LongSubjectDecisionTreeTests` — O0098 over a LONG SELECT subject. — PowerBasic.Compiler.Tests/CodeGen/LongSubjectDecisionTreeTests.cs:28

### LongSubjectPerfectHashTests.cs  `C#, 141 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/LongSubjectPerfectHashTests.cs:5
- class `LongSubjectPerfectHashTests` — O0100 over a LONG SELECT subject — the third and last of the dispatch passes that refused — PowerBasic.Compiler.Tests/CodeGen/LongSubjectPerfectHashTests.cs:27

### LoopAlignmentTests.cs  `C#, 67 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/LoopAlignmentTests.cs:4
- class `LoopAlignmentTests` — pb36 C2 loop-top alignment: under $CPU 80486/80586 + $OPTIMIZE SPEED a hot loop — PowerBasic.Compiler.Tests/CodeGen/LoopAlignmentTests.cs:13

### MbfTests.cs  `C#, 96 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/MbfTests.cs:4
- class `MbfTests` — Microsoft Binary Format single precision for BASICA / GW-BASIC: a SINGLE cell is — PowerBasic.Compiler.Tests/CodeGen/MbfTests.cs:13
- field `source` — 1.0 in MBF32 is 00 00 00 81 (exponent 129 = 0x81, zero mantissa, zero sign) — PowerBasic.Compiler.Tests/CodeGen/MbfTests.cs:37
- method `PEEK(p%)` — PowerBasic.Compiler.Tests/CodeGen/MbfTests.cs:40
- field `source` — -0.5 in MBF32: exponent 128 (0x80), sign bit set in byte 2 -> 00 00 80 80 — PowerBasic.Compiler.Tests/CodeGen/MbfTests.cs:51
- method `PEEK(p% + 2)` — PowerBasic.Compiler.Tests/CodeGen/MbfTests.cs:54
- field `source` — PowerBasic.Compiler.Tests/CodeGen/MbfTests.cs:62
- method `PEEK(p%)` — PowerBasic.Compiler.Tests/CodeGen/MbfTests.cs:65
- field `source` — y! = x! loads x! (MBF -> IEEE) and stores y! (IEEE -> MBF); the copy's MBF — PowerBasic.Compiler.Tests/CodeGen/MbfTests.cs:74
- method `PEEK(p% + 3)` — PowerBasic.Compiler.Tests/CodeGen/MbfTests.cs:78
- field `source` — 2.5 * 4.0 = 10.0; MBF32 of 10.0 has exponent byte 132 (0x84) — PowerBasic.Compiler.Tests/CodeGen/MbfTests.cs:86
- method `PEEK(p% + 3)` — PowerBasic.Compiler.Tests/CodeGen/MbfTests.cs:91

### MiniSuiteTests.cs  `C#, 39 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/MiniSuiteTests.cs:1
- class `MiniSuiteTests` — The TESTLIB battery: tests/MINI.BAS ($INCLUDEs tests/TESTLIB.BI) is compiled — PowerBasic.Compiler.Tests/CodeGen/MiniSuiteTests.cs:8

### MultiConcatTests.cs  `C#, 157 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/MultiConcatTests.cs:4
- class `MultiConcatTests` — O24 multi-concat behavioral tests: a chain/tree of three or more string concatenations builds — PowerBasic.Compiler.Tests/CodeGen/MultiConcatTests.cs:13
- class `MemorySourceProvider` — PowerBasic.Compiler.Tests/CodeGen/MultiConcatTests.cs:16
- method `TryReadSource(string name, string? includedFrom, out string sourceText, out string…` — PowerBasic.Compiler.Tests/CodeGen/MultiConcatTests.cs:18
- method `LEN(r$)` — PowerBasic.Compiler.Tests/CodeGen/MultiConcatTests.cs:82
- field `source` — output-equivalence: the optimized (pb36) and unoptimized (pb35) builds produce identical text. — PowerBasic.Compiler.Tests/CodeGen/MultiConcatTests.cs:146

### OmfLinkTests.cs  `C#, 101 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/OmfLinkTests.cs:7
- class `OmfLinkTests` — End-to-end external OMF object linking (docs/LINKER.md, M1): a BASIC program — PowerBasic.Compiler.Tests/CodeGen/OmfLinkTests.cs:17
- method `Record(0x80, Str(moduleName))` — PowerBasic.Compiler.Tests/CodeGen/OmfLinkTests.cs:38
- field `source` — PowerBasic.Compiler.Tests/CodeGen/OmfLinkTests.cs:54
- method `addone(41)` — PowerBasic.Compiler.Tests/CodeGen/OmfLinkTests.cs:56
- field `source` — PowerBasic.Compiler.Tests/CodeGen/OmfLinkTests.cs:71
- method `sub2(20, 7)` — PowerBasic.Compiler.Tests/CodeGen/OmfLinkTests.cs:73

### OptDeadGlobalsTests.cs  `C#, 267 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/OptDeadGlobalsTests.cs:5
- class `OptDeadGlobalsTests` — pb36 O23 data tree-shaking (): a module scalar global no — PowerBasic.Compiler.Tests/CodeGen/OptDeadGlobalsTests.cs:15
- field `source` — PowerBasic.Compiler.Tests/CodeGen/OptDeadGlobalsTests.cs:251

### OptFloatDemotionTests.cs  `C#, 116 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/OptFloatDemotionTests.cs:4
- class `OptFloatDemotionTests` — pb36 O12 float demotion: the analyzer proves SINGLE/DOUBLE variables — PowerBasic.Compiler.Tests/CodeGen/OptFloatDemotionTests.cs:13
- method `Touch(x)` — PowerBasic.Compiler.Tests/CodeGen/OptFloatDemotionTests.cs:60

### OptReachabilityTests.cs  `C#, 205 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/OptReachabilityTests.cs:5
- class `OptReachabilityTests` — pb36 O22 reachability (): transitive dead-procedure — PowerBasic.Compiler.Tests/CodeGen/OptReachabilityTests.cs:14
- method `LINE` — PowerBasic.Compiler.Tests/CodeGen/OptReachabilityTests.cs:45
- method `Outer()` — PowerBasic.Compiler.Tests/CodeGen/OptReachabilityTests.cs:59
- method `A()` — PowerBasic.Compiler.Tests/CodeGen/OptReachabilityTests.cs:83
- method `Alive()` — PowerBasic.Compiler.Tests/CodeGen/OptReachabilityTests.cs:111
- field `none` — PowerBasic.Compiler.Tests/CodeGen/OptReachabilityTests.cs:149
- field `withDeadChain` — """; — PowerBasic.Compiler.Tests/CodeGen/OptReachabilityTests.cs:154
- method `C` — PowerBasic.Compiler.Tests/CodeGen/OptReachabilityTests.cs:157
- field `none` — PowerBasic.Compiler.Tests/CodeGen/OptReachabilityTests.cs:173
- field `withDead` — """; — PowerBasic.Compiler.Tests/CodeGen/OptReachabilityTests.cs:178
- field `source` — PowerBasic.Compiler.Tests/CodeGen/OptReachabilityTests.cs:192

### OptimizationBatteryTests.cs  `C#, 338 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/OptimizationBatteryTests.cs:5
- class `OptimizationBatteryTests` — The optimization battery: every SUB in tests/optimize/*.BAS is one scenario that — PowerBasic.Compiler.Tests/CodeGen/OptimizationBatteryTests.cs:40
- record `Scenario` — One annotated scenario: a NOINLINE procedure plus what is expected of its code. — PowerBasic.Compiler.Tests/CodeGen/OptimizationBatteryTests.cs:92
- class `Compiled` — A compiled battery file: the raw code image plus the per-procedure byte extents. — PowerBasic.Compiler.Tests/CodeGen/OptimizationBatteryTests.cs:97
- method `CodeOf` — PowerBasic.Compiler.Tests/CodeGen/OptimizationBatteryTests.cs:101
- method `Report(scenario, $"no procedure named '{scenario.Name}' survived to the ima…` — PowerBasic.Compiler.Tests/CodeGen/OptimizationBatteryTests.cs:130
- method `if(!ok)` — PowerBasic.Compiler.Tests/CodeGen/OptimizationBatteryTests.cs:135
- method `Report(scenario, $"{assertion} -> {detail}")` — PowerBasic.Compiler.Tests/CodeGen/OptimizationBatteryTests.cs:136
- method `Report` — PowerBasic.Compiler.Tests/CodeGen/OptimizationBatteryTests.cs:139
- method `if(!body.StartsWith('@'))` — PowerBasic.Compiler.Tests/CodeGen/OptimizationBatteryTests.cs:188
- method `switch(key.ToLowerInvariant())` — PowerBasic.Compiler.Tests/CodeGen/OptimizationBatteryTests.cs:192
- method `if(!_patterns.TryGetValue(argument, out var pattern))` — PowerBasic.Compiler.Tests/CodeGen/OptimizationBatteryTests.cs:259
- method `if(verb.Equals("absent", StringComparison.OrdinalIgnoreCase) && _absenc…` — PowerBasic.Compiler.Tests/CodeGen/OptimizationBatteryTests.cs:261
- method `return(found == want, found ? $"{argument} is present" : $"{argument} is ab…` — PowerBasic.Compiler.Tests/CodeGen/OptimizationBatteryTests.cs:266
- method `return(found == want, found ? $"calls {argument}" : $"does not call {argume…` — PowerBasic.Compiler.Tests/CodeGen/OptimizationBatteryTests.cs:274
- method `if(parts.Length != 2 || !int.TryParse(parts[1], out var want))` — PowerBasic.Compiler.Tests/CodeGen/OptimizationBatteryTests.cs:283
- method `return(false, $"'{argument}' is not '<pattern> <count>'")` — PowerBasic.Compiler.Tests/CodeGen/OptimizationBatteryTests.cs:284
- method `if(!_patterns.TryGetValue(parts[0], out var counted))` — PowerBasic.Compiler.Tests/CodeGen/OptimizationBatteryTests.cs:285
- method `if(code[at..].StartsWith(counted))` — PowerBasic.Compiler.Tests/CodeGen/OptimizationBatteryTests.cs:289
- method `return(seen == want, $"{parts[0]} occurs {seen}x, expected {want}x")` — PowerBasic.Compiler.Tests/CodeGen/OptimizationBatteryTests.cs:291
- method `if(!int.TryParse(argument, out var limit))` — PowerBasic.Compiler.Tests/CodeGen/OptimizationBatteryTests.cs:295
- method `return(false, $"'{argument}' is not a byte count")` — PowerBasic.Compiler.Tests/CodeGen/OptimizationBatteryTests.cs:296
- method `return(code.Length <= limit, $"{code.Length} bytes")` — PowerBasic.Compiler.Tests/CodeGen/OptimizationBatteryTests.cs:297
- method `if(!plain.Extents.ContainsKey(procedure))` — PowerBasic.Compiler.Tests/CodeGen/OptimizationBatteryTests.cs:301
- method `return(false, "the unoptimized build has no such procedure")` — PowerBasic.Compiler.Tests/CodeGen/OptimizationBatteryTests.cs:302
- method `return(code.Length < before, $"{before} -> {code.Length} bytes")` — PowerBasic.Compiler.Tests/CodeGen/OptimizationBatteryTests.cs:304
- method `return(false, $"unknown assertion verb '{verb}'")` — PowerBasic.Compiler.Tests/CodeGen/OptimizationBatteryTests.cs:308

### OptimizeAllDialectsTests.cs  `C#, 49 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/OptimizeAllDialectsTests.cs:4
- class `OptimizeAllDialectsTests` — The optimizer is a dialect-agnostic axis: it is only on by default for pb36, but EVERY — PowerBasic.Compiler.Tests/CodeGen/OptimizeAllDialectsTests.cs:14

### OptimizerTests.cs  `C#, 3447 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:4
- class `OptimizerTests` — pb36 optimizer (docs/PB36.md): runtime trimming, trivial-I/O lowering, — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:14
- method `Resident` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:73
- field `source` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:83
- field `source` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:94
- method `Compile(string source)` — $OPTIMIZE SIZE: no inlining plus S3 procedure tail-merging must shrink a branchy program — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:194
- field `body` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:201
- field `body` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:227
- method `CompileCase` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:228
- field `body` — O6's purge drops a procedure it expects to inline at EVERY call site - but $OPTIMIZE SIZE — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:255
- field `source` — O9 closure: right-nested and mixed concat trees flatten into the O24 single-allocation — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:278
- method `HasMarker(string source, bool optimize)` — O16 completed: the interval lattice (not just FOR-counter ranges) feeds comparison — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:300
- method `if(exe.AsSpan(i, marker.Length).SequenceEqual(marker))` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:307
- field `source` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:311
- field `source` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:423
- method `CountOf` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:431
- method `if(image.AsSpan(i, needle.Length).SequenceEqual(needle))` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:436
- field `source` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:444
- field `body` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:451
- field `source` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:490
- field `source` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:543
- field `source` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:555
- field `source` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:567
- field `source` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:575
- field `body` — a constant-count LONG SHIFT collapses the per-bit loop to one 66 C1 dword shift — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:594
- field `with386` — a constant divisor of magnitude >= 2 drops the LongDiv runtime call for a 66 F7 IDIV; — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:616
- field `no386` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:617
- field `narrowed` — a signed LONG \ by a small constant whose dividend the interval lattice proves — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:637
- field `runtime` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:638
- method `Loops(SemanticModel m)` — O0062 loop fusion: two adjacent FOR loops over the same counter and bounds, whose bodies are — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:658
- method `Bound(string src)` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:659
- method `HasBranchlessAbs(byte[] img)` — O0249: ABS on a 16-bit value is emitted branchless (cwd; xor ax,dx; sub ax,dx = 99 31 D0 29 D0) — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:673
- method `if(img[i] == 0x99 && img[i + 1] == 0x31 && img[i + 2] == 0xD0 && img[i …` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:675
- method `CmpSi(byte[] img)` — O0112: a fixed-trip FOR whose counter is never read counts SI down to zero (DEC/JNZ), so no — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:697
- method `if((img[i] == 0x3B && ((img[i + 1] >> 3) & 7) == 6) || ((img[i] == 0x81…` — cmp si, r/m16 (3B, modrm reg field = 110b) OR cmp si, imm (81/83 FE): O0113 folds a — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:702
- method `CmpSi(byte[] img)` — O0062: a register-resident FOR counter (SI) is rotated - an entry guard plus a bottom test - — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:718
- method `if((img[i] == 0x3B && ((img[i + 1] >> 3) & 7) == 6) || ((img[i] == 0x81…` — cmp si, r/m16 (3B, modrm reg field = 110b) OR cmp si, imm (81/83 FE): O0113 folds a — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:723
- method `CmpBound(byte[] img)` — O0062: under $OPTIMIZE SPEED a pre-tested DO WHILE is rotated to an entry guard plus a bottom — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:738
- method `if((img[i] == 0x3D && img[i + 1] == 0xE8 && img[i + 2] == 0x03) || (img…` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:741
- field `loop` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:747
- method `HasNoZeroAlloc(byte[] img)` — O0068: DIM a(1 TO n) immediately followed by FOR i=1 TO n : a(i)=expr writes every element — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:759
- method `if(img[i] == 0x89 && img[i + 1] == 0xD8 && img[i + 2] == 0x5B && img[i …` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:761
- method `Imuls(byte[] img)` — O0066: a fully-unrolled FOR sees its counter as a constant per copy, so i * i folds to a — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:778
- method `if(img[i] == 0xF7 && img[i + 1] is >= 0xE8 and <= 0xEF)` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:781
- method `Imuls(byte[] img)` — O0078: under $OPTIMIZE SPEED, a three-set-bit multiplier (11 = 8+2+1) decomposes into shifts and ad… — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:792
- method `if(img[i] == 0xF7 && img[i + 1] is >= 0xE8 and <= 0xEF)` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:795
- method `Imuls(byte[] img)` — O0078 + O0174: a four-set-bit multiplier (23 = 16+4+2+1) is ~8 instructions - a win over the 8086's — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:811
- method `if(img[i] == 0xF7 && img[i + 1] is >= 0xE8 and <= 0xEF)` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:814
- field `head` — O0248: `IF a > b THEN m = a ELSE m = b` is a MAX, and folds to exactly the integer CMP/keep the MAX% — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:829
- method `Has(byte[] img, params byte[] seq)` — O0081: IF x AND mask emits `test ax, mask` (A9 iw), not `and ax, mask` (83 E0 ib) + `test ax,ax`. — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:842
- method `Has(byte[] img, params byte[] seq)` — O0081: `(x AND mask) = 0` and `<> 0` are the same bit test as the bare `IF x AND mask` - the compare — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:859
- field `head` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:867
- method `Has(byte[] img, params byte[] seq)` — O0081 backs off when the AND is CSE'd: a second use of the same `x AND mask` needs the value, so the — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:880
- field `head` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:888
- method `Has(byte[] img, params byte[] seq)` — O0029: four+ targets dispatch through a jump table (a `cmp ax, count` bounds check followed by an — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:906
- method `HasIndexedJump(byte[] img)` — FF /4 with a memory mod field = JMP r/m16 through memory - the jump table's dispatch — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:915
- method `if(img[i] == 0xFF && (img[i + 1] & 0x38) == 0x20 && (img[i + 1] & 0xC0)…` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:917
- field `head` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:921
- field `head` — O0181: LEN(s$) = 0 is the emptiness handle test, identical to the s$ = "" spelling. — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:934
- field `head` — O0020: SWAP of two scalars is exchanged inline, so the rt_swap byte-loop routine is never — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:955
- method `Has(byte[] img, params byte[] seq)` — O0249: SGN over an INTEGER folds to cwd/neg/adc dx,dx/mov ax,dx - branchless and off the x87. — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:968
- field `head` — O0248: the one-armed clamp `IF x > hi THEN x = hi` (no ELSE) is a MIN, and `IF x < lo THEN x = lo` … — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:992
- method `JgJl(byte[] img)` — O0248: MAX/MIN over LONG arguments fold with a signed 32-bit compare rather than the x87 — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1033
- method `if(img[i] == 0x7F && img[i + 2] == 0x7C)` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1036
- field `body` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1040
- field `head` — O0248: the LONG min/max diamond folds to exactly the 32-bit MAX(a&, b&) intrinsic code. — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1052
- field `head` — The fold evaluates each operand once; the branch re-evaluates the taken arm. A call operand would r… — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1062
- field `tail` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1063
- field `body` — O7 + O0174: a six-iteration tiny FOR loop is above the fetch-bound 8086's four-copy budget (it keep… — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1074
- field `src` — O0079: q = n\d immediately followed by m = n MOD d over the same runtime operands reuses the — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1086
- method `Idivs(byte[] img)` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1091
- method `if(img[i] == 0xF7 && (img[i + 1] & 0x38) == 0x38)` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1094
- field `body` — O0067: an IF/ELSEIF chain of equality tests on one integer variable against >= 4 dense — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1115
- field `three` — O0180: LEN(s$) + LEN(s$) + LEN(s$) reads the descriptor once and reloads a slot for the rest, — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1145
- field `source` — O0088: f = (a < b) over WORD operands used as a value tests the carry the CMP already set, — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1189
- field `body` — a one-expression FUNCTION is the inliner's bread and butter: without NOINLINE it is — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1213
- field `narrowed` — both operands of the LONG compare are range-known (a FOR counter 1..100 against a — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1250
- field `wide` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1251
- field `source` — the narrowing is gated on Optimize, so the faithful build is untouched (golden gate) — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1260
- field `narrowed` — $ERROR NUMERIC ON keeps an unsigned multiply integral (no float promotion), so it — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1269
- field `wide` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1270
- field `source` — the narrowed compare must decide exactly like the 32-bit one across the sign — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1280
- field `source` — the narrowed MUL must produce the full 32-bit product, including the upper word — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1302
- field `body` — a QUAD OR runs inline as two 66 0B (OR EAX, m32) halves instead of the QuadOr call — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1322
- field `body` — a constant-count QUAD SHIFT LEFT collapses the per-bit loop to a 66 0F A4 SHLD — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1356
- field `with386` — ERASE of a static array zeroes it DWORD-wide (F3 66 AB) instead of REP STOSW — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1389
- field `no386` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1390
- field `with386` — a FOR-loop constant array fill stores two elements per REP STOSD instead of REP STOSW — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1399
- field `no386` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1400
- field `folds` — p% is [5,8] (IF-join), so `p% < 20` is always true - the ELSE arm is unreachable and its — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1410
- field `nofold` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1411
- field `bounded` — k% is [5,10] (an IF-join, not a constant and not a FOR counter) - the interval lattice — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1423
- field `unknown` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1424
- field `counterIdx` — a%(i%) with i% the in-bounds FOR counter drops its bounds check; an index nothing can pin down — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1439
- field `varIdx` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1440
- field `twoRange` — a%(i% + j%) with i% the [2,9] FOR counter and j% = i% - 1 a derived [1,8] var: — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1452
- field `defeated` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1453
- field `andIdx` — a(x AND 7) is always in [0,7] (the mask keeps only the low bits); a(i% MOD 8) over a — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1470
- field `modIdx` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1471
- field `unknownIdx` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1472
- field `idx` — a(i% \ 2) over i% in [0,30] is in [0,15] (truncated divide is monotonic in the dividend), — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1486
- field `unknownIdx` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1487
- field `counterAdd` — i% + 1 over an in-range FOR counter drops its Error-6 check; k% + 1 keeps it — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1497
- field `varAdd` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1498
- field `counterAdd` — a LONG i& + 1& over [1,100] -> [2,101] stays inside 32 bits and drops its Error-6 — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1508
- field `varAdd` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1509
- field `counterSub` — a LONG i& - 1& over [1,100] -> [0,99] stays inside 32 bits and drops its Error-6 check — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1518
- field `varSub` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1519
- field `counterDiv` — 100 \ i% with i% a [1,10] counter (excludes 0) drops the divide-by-zero guard; 100 \ k% keeps it. — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1534
- field `varDiv` — a SUB parameter divisor (differing call args) is non-constant and not range-known — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1536
- field `append` — s$ = s$ + "x" appends the literal in place (rt_strcatlit) - the literal is NOT materialized — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1551
- field `prepend` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1552
- field `withVar` — s$ = s$ + v$ emits a CALL to the in-place rt_strcatvar routine; a literal self-append — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1570
- field `literal` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1571
- method `if(image[i + j] != _strCatVarHead[j])` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1587
- field `funcLeft` — LEFT$/RIGHT$/MID$ construct a fresh, dead, topmost temp - like a concat - so a tail operand — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1604
- field `varLeft` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1605
- field `balanced` — (a$+b$) + (c$+d$): a four-leaf tree of plain string variables. O24 (multi-concat) subsumes the — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1618
- field `impure` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1619
- field `chain` — a$ + b$ + c$ is a three-leaf chain: O24 builds it with one rt_strcatn allocation (it subsumes — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1630
- field `pair` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1631
- field `selfAppend` — s$ = s$ + x$ skips the StrDup of s$ and the StrAssign (StrCat consumes s$ directly), — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1642
- field `nonSelf` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1643
- method `if(image[i + j] != _strCatNHead[j])` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1657
- method `if(image[i + j] != seq[j])` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1691
- field `program` — O0290: ASC(MID$(s$, i, 1)) with a compile-time length of 1 reads the byte directly (rt_charat), — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1733
- method `WindowAfterPrologue(byte[] img, params byte[] marker)` — O0298: `=` / `<>` use rt_strcmpeq under --optimize, which after loading the two string — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1792
- method `for(var k = i; k < i + 64 && k + marker.Length <= img.Length; ++k)` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1798
- method `HasResultReload(byte[] img)` — O0102: a single-exit function whose last statement assigns the integer result leaves that value — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1818
- method `if(img[i] == 0x8B && img[i + 1] == 0x46 && img[i + 3] == 0x89 && img[i …` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1820
- field `invariant` — O0180/LICM: LEN(s$) in a WHILE condition (re-evaluated every iteration) and again in the body is — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1882
- field `variant` — The invariance guard: when the body writes s$ its length changes per iteration, so the condition's — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1892
- field `chain` — r$ = a$ & b$ & c$ & d$ is a 4-leaf chain: it builds with ONE rt_strcatn call (a single heap — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1902
- field `pair` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1903
- field `three` — boundary: three leaves is the smallest chain the multi-concat builder fires on (two go to O9). — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1913
- field `chain` — the optimization is strictly Optimize-gated: pb35 (unoptimized) never calls rt_strcatn, so its — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1922
- field `withCall` — a string-returning function call yields a SHARED/volatile result buffer: a later operand's — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1933
- field `source` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:1978
- field `body` — $ERROR OVERFLOW ON: a shift chain cannot raise error 6 on signed overflow, so the strength reducer — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2002
- field `body` — s% = s% + i% over a SI/DI-clean FOR loop keeps the counter in SI and the — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2017
- field `body` — a FOR loop whose body is a clean IF (SI-clean condition + scalar-assign arm) keeps the — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2028
- field `proven` — c% + b% where b% is an SCCP-proven constant folds the constant into one immediate ALU op — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2041
- field `runtime` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2042
- field `direct` — a store to a direct-cell variable needs no address computation, so the value is no longer — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2061
- field `byref` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2062
- field `numeric` — a PRINT of plain numeric items (and string literals, whose SI load is saved/restored) leaves — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2081
- field `stringVar` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2082
- field `intSel` — an INTEGER SELECT CASE dispatches through AX/BX/DX (jump table or compare chain), never the — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2092
- field `strSel` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2093
- field `body` — a LONG FOR counter over an SI-clean body lives in the 32-bit register ESI under $CPU 80386: — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2103
- field `withAcc` — a hot LONG accumulator joins the ESI counter in EDI under $CPU 80386 - two full LONG locals — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2115
- field `noAcc` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2116
- field `body` — a doubly-nested integer loop with SI/DI-clean bodies keeps the outer counter in SI — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2156
- field `body` — an SI/DI-clean DO/LOOP keeps its hot accumulator in SI (no FOR counter competes): the — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2177
- field `body` — a DO loop has no counter, so both SI and DI are free: two hot accumulators live in — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2188
- field `source` — x% is made opaque (BYREF call) so SCCP cannot fold it - this pins the — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2217
- field `source` — O0078: 13 = 1101b (8+4+1) is a three-set-bit multiplier, so it decomposes into a shift-add — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2226
- field `withRead` — DATA bytes nobody READs are dead - the pool labels stay (the runtime references rt_dataptr) but — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2235
- field `noRead` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2236
- field `source` — x% * z% (variable * variable): the right operand is a direct cell, so the modular path reads it — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2246
- field `source` — the shift chains are a SPEED trade (a few bytes for the cycles); SIZE/default — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2255
- field `mem` — c% + n% with n% a direct-cell operand reads it as an ALU memory operand (ADD AX,[n%]), so it — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2305
- field `staged` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2306
- field `mem` — i% > n% with n% a direct cell compares it as a memory operand (CMP AX,[n%]); an expression — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2324
- field `staged` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2325
- field `rmw` — a% = a% + 1 on a non-resident direct cell becomes INC [a%] (one instruction); the same — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2343
- field `nonrmw` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2344
- field `direct` — INCR a%, 5 on a non-resident direct cell becomes ADD [a%],5 (one immediate, no AX park); — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2362
- field `array` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2363
- field `mem` — r! = a! + b! with b! a direct cell adds it straight from memory (FADD m32); an expression — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2381
- field `staged` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2382
- field `mem` — IF a! < b! with b! a direct cell compares it as an FPU memory operand (FCOMP m32); an — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2400
- field `staged` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2401
- field `mem` — x! = x! + i% with i% a signed-integer direct cell reads it with FIADD m16 (no AX load, — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2421
- field `staged` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2422
- field `mem` — r! = a! * 1.5 multiplies by the data-segment float constant in place (FMUL qword [f_n]); — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2440
- field `staged` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2441
- field `mem` — a LONG op (AND/OR/XOR) against a BYVAL direct-cell right operand loads it into BX:CX — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2460
- field `staged` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2461
- field `source` — the FUNCTION call has side effects - x * 0 must keep the call (assert: the — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2535
- field `head` — a%(i%) = i% over an affine subscript: O6b walks the elements instead of recomputing each address — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2565
- field `tail` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2566
- field `source` — verify the stored values are byte-identical to the unoptimized path — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2576
- field `source` — lbound != 0: the initial pointer must account for the bias — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2592
- field `body` — expr reads a%(0) - O6b must decline (conservative aliasing: any a% reference — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2609
- field `body` — $ERROR BOUNDS ON suppresses O6b so per-element bounds checking keeps working — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2630
- field `head` — The dividend is INPUT-sourced and the control is a NON-power-of-two divisor, not the same program — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2671
- field `source` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2680
- field `head` — x% = a%(i%) over an affine subscript scales i% by the element size every iteration unless IVSR — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2740
- field `tail` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2741
- field `body` — $ERROR BOUNDS ON must suppress the optimization: the bounds check that the — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2752
- field `body` — A body with more than one statement does not qualify - the optimization must not fire. — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2767
- method `CountDown(BYVAL n&)` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2781
- method `Twice(n&)` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2802
- field `source` — GIVEN a SUB whose last action is CALL B with a DIFFERENT argument count — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2828
- method `Forward(BYVAL n%)` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2829
- method `Ping(BYVAL n&)` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2857
- field `source` — GIVEN a call that is NOT in tail position (a PRINT runs after it returns) - — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2883
- method `AfterWork(BYVAL n%)` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2884
- method `Note` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2891
- field `source` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2912
- field `source` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2934
- field `source` — GIVEN a small multi-statement leaf FUNCTION (a temp local, then the result) — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2959
- field `inlinedAll` — GIVEN a multi-statement leaf whose every call inlines — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2987
- field `addressTaken` — """; — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:2997
- field `inlinedAll` — GIVEN a trivial TYPE method (its body reads/writes fields through the BYREF THIS receiver) — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:3021
- method `Sum` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:3022
- field `addressTaken` — """; — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:3034
- method `Sum` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:3036
- field `source` — GIVEN a leaf that mutates its own BYVAL parameter and a body local — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:3063
- field `source` — GIVEN callees that disqualify inlining (a nested call, a loop, an ON ERROR) — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:3085
- field `source` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:3155
- field `source` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:3181
- method `P(BYVAL m%, BYVAL v%)` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:3182
- field `source` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:3202
- method `P(BYVAL m%)` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:3203
- field `source` — 16-byte procedure alignment is output-invariant; the program must run — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:3226
- field `source` — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:3241
- field `source` — an IF in the body previously disabled LICM wholesale; the invariant k%*m% in the — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:3297
- field `source` — a value computed ONLY under the IF must not run unconditionally in the preheader — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:3319
- field `source` — k% is written inside the branch - k%*m% is NOT invariant even though the — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:3337
- field `source` — k%*m% appears twice in the body; both k% and m% are not written in the body. — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:3357
- field `source` — k% IS written in the loop body (k% = k% + 1), so k%*m% is NOT invariant. — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:3378
- field `source` — k%*i% reads the loop counter i%; the counter is always in the written set. — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:3396
- field `source` — under checked arithmetic ($ERROR NUMERIC ON) a multiply could trap; — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:3411
- field `body` — With $OPTIMIZE SPEED, LICM hoists k%*m% to the preheader; without SPEED it — PowerBasic.Compiler.Tests/CodeGen/OptimizerTests.cs:3428

### PCopyTests.cs  `C#, 104 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/PCopyTests.cs:5
- class `PCopyTests` — PCOPY source, destination - one text-mode video page copied over another. — PowerBasic.Compiler.Tests/CodeGen/PCopyTests.cs:19
- method `PEEK` — PowerBasic.Compiler.Tests/CodeGen/PCopyTests.cs:45
- method `PEEK` — PowerBasic.Compiler.Tests/CodeGen/PCopyTests.cs:61
- method `PEEK` — PowerBasic.Compiler.Tests/CodeGen/PCopyTests.cs:70
- method `PEEK` — PowerBasic.Compiler.Tests/CodeGen/PCopyTests.cs:83
- method `PEEK` — PowerBasic.Compiler.Tests/CodeGen/PCopyTests.cs:100

### PaintStatementTests.cs  `C#, 109 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/PaintStatementTests.cs:5
- class `PaintStatementTests` — PAINT, checked by reading the pixels back with POINT. — PowerBasic.Compiler.Tests/CodeGen/PaintStatementTests.cs:20
- method `POINT(10, 10)` — PowerBasic.Compiler.Tests/CodeGen/PaintStatementTests.cs:39
- method `LINE` — PowerBasic.Compiler.Tests/CodeGen/PaintStatementTests.cs:46
- method `POINT(12, 12)` — PowerBasic.Compiler.Tests/CodeGen/PaintStatementTests.cs:67
- method `POINT(0, 0)` — PowerBasic.Compiler.Tests/CodeGen/PaintStatementTests.cs:81
- method `POINT(0, 0)` — PowerBasic.Compiler.Tests/CodeGen/PaintStatementTests.cs:87
- method `POINT(5, 5)` — PowerBasic.Compiler.Tests/CodeGen/PaintStatementTests.cs:97
- method `POINT(10, 10)` — PowerBasic.Compiler.Tests/CodeGen/PaintStatementTests.cs:106

### PartialApplicationTests.cs  `C#, 101 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/PartialApplicationTests.cs:4
- class `PartialApplicationTests` — pb36 partial application and composition over typed delegates: BIND(f, consts...) — PowerBasic.Compiler.Tests/CodeGen/PartialApplicationTests.cs:13
- method `Add` — PowerBasic.Compiler.Tests/CodeGen/PartialApplicationTests.cs:36
- field `source` — PowerBasic.Compiler.Tests/CodeGen/PartialApplicationTests.cs:51
- method `FUNCTION(LONG)` — PowerBasic.Compiler.Tests/CodeGen/PartialApplicationTests.cs:52
- method `add5(10)` — PowerBasic.Compiler.Tests/CodeGen/PartialApplicationTests.cs:54
- field `source` — PowerBasic.Compiler.Tests/CodeGen/PartialApplicationTests.cs:61
- method `FUNCTION(LONG)` — PowerBasic.Compiler.Tests/CodeGen/PartialApplicationTests.cs:62

### Pb35FidelityTests.cs  `C#, 233 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/Pb35FidelityTests.cs:4
- class `Pb35FidelityTests` — Byte-fidelity regressions pinned against the genuine PBC 3.50 oracle — PowerBasic.Compiler.Tests/CodeGen/Pb35FidelityTests.cs:12
- class `MemorySourceProvider` — region helpers — PowerBasic.Compiler.Tests/CodeGen/Pb35FidelityTests.cs:17
- method `TryReadSource(string name, string? includedFrom, out string sourceText, out string…` — PowerBasic.Compiler.Tests/CodeGen/Pb35FidelityTests.cs:19
- method `VAL("1e3")` — PowerBasic.Compiler.Tests/CodeGen/Pb35FidelityTests.cs:106
- method `VAL("&HFF")` — PowerBasic.Compiler.Tests/CodeGen/Pb35FidelityTests.cs:119
- method `PRINT` — PowerBasic.Compiler.Tests/CodeGen/Pb35FidelityTests.cs:203

### Pb36LanguageFeatureTests.cs  `C#, 963 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:4
- class `Pb36LanguageFeatureTests` — End-to-end tests for the PB 3.6 new-syntax surface (docs/PB36.md): source is — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:13
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:30
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:43
- method `Show(BYVAL a AS LONG, BYVAL b AS LONG, BYVAL c AS LONG)` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:44
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:55
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:68
- method `Greet(BYVAL n AS LONG, BYVAL times AS LONG)` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:69
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:81
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:99
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:122
- method `UBOUND(a%)` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:126
- field `source` — SIZEOF reflects as LONG - the folded literal must be LONG-typed too, else the 32-bit — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:135
- method `SIZEOF` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:136
- method `FIELDCOUNT(Point)` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:143
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:150
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:160
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:175
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:187
- field `source` — the lambda is lifted to an anonymous proc; its value is a code pointer called — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:216
- method `BDECL()` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:220
- field `source` — Bump captures the outer local x (stack capture via a hidden BYREF parameter); — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:233
- method `Outer()` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:234
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:252
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:269
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:286
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:303
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:313
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:325
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:336
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:345
- field `source` — 100000 does not fit INTEGER; inference must pick LONG so the value survives. — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:355
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:364
- field `source` — If the false branch (100 \ x%) were evaluated with x% = 0 it would raise the — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:387
- method `IF(x% = 0, 42, 100 \ x%)` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:389
- field `source` — (100 \ x%) would raise division-by-zero error 11 if evaluated; ANDALSO must — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:408
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:417
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:426
- field `source` — the width follows the left operand's type: an INTEGER variable shifts 16-bit, — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:446
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:456
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:468
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:479
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:496
- method `Show(BYVAL n AS LONG)` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:497
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:513
- field `source` — x% is SCCP-proven 5 and read inside the ternary; the SSA/SCCP/DSE chain must — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:542
- field `source` — k is a (non-constant) parameter, so the ternary cannot fold; the store it — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:554
- field `source` — p +* i scales i by the target size (2 for INTEGER), matching @p[i]. — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:569
- field `source` — a LONG PTR scales by 4; p -* brings it back down again. — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:589
- field `source` — Z is not listed, so it must keep its zero-initialized value. — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:608
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:624
- field `source` — a typed FUNCTION-pointer carries the signature, so the indirect call passes — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:641
- method `FUNCTION(LONG)` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:642
- field `source` — the pointer variable is plain storage: reassigning it switches the callee. — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:652
- method `FUNCTION(LONG, LONG)` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:653
- field `source` — CODEPTR32 of a named FUNCTION yields a far thunk pointer; assigned to a typed — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:666
- method `FUNCTION(LONG)` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:668
- field `source` — a DECLAREd FUNCTION prototype doubles as a named delegate type: DIM cmp AS — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:682
- method `Comparator(BYVAL a AS LONG, BYVAL b AS LONG)` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:683
- field `source` — the named delegate is usable as a parameter type, so a procedure can accept a — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:695
- method `IntOp(BYVAL a AS LONG, BYVAL b AS LONG)` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:696
- field `source` — (a, b) => expr omits FUNCTION, parameter types and the result type; all are — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:712
- method `Comparator(BYVAL a AS LONG, BYVAL b AS LONG)` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:713
- field `source` — the user's exact shape: a named delegate declared and initialized in one DIM, — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:725
- method `IntOp(BYVAL a AS LONG, BYVAL b AS LONG)` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:726
- field `source` — a single-parameter lambda may drop the parentheses entirely: x => 2 * x. The — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:737
- method `DoDouble(BYVAL x AS LONG)` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:738
- field `source` — '>=' remains the comparison operator, distinct from the '=>' lambda arrow. — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:748
- field `source` — a stage-1 stack closure: the lambda captures the enclosing local 'bonus' by — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:759
- method `Demo()` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:760
- field `source` — the closure is passed to another procedure and invoked there; its environment — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:778
- method `IntFn(BYVAL x AS LONG)` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:779
- field `source` — capture is by reference (stage-1 stack env): the closure mutates the enclosing — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:801
- method `Demo()` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:802
- field `source` — stage-2 ESCAPING closure: MakeAdder builds a capturing lambda and RETURNS it, — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:824
- method `Adder(BYVAL x AS LONG)` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:825
- field `source` — two escaping closures from the same producer get independent heap snapshots: — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:845
- method `Adder(BYVAL x AS LONG)` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:846
- field `source` — [lo TO hi] is a bracketed collection/range literal, equivalent to {lo TO hi}. — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:869
- method `UBOUND(a%)` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:873
- field `source` — FOR EACH v IN [lo TO hi] desugars to a counted loop over the inclusive range. — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:881
- field `source` — FOR EACH v IN a() iterates each element (LBOUND..UBOUND), copying it into v. — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:895
- field `interpolated` — $"a {x} b" desugars to "a " & STR$(x) & " b" - same observable output — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:913
- field `explicitForm` — """; — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:918
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:929
- field `interpolated` — {x:###.##} reuses the PRINT USING formatter via USING$ — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:940
- field `usingForm` — """; — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:945
- field `source` — PowerBasic.Compiler.Tests/CodeGen/Pb36LanguageFeatureTests.cs:955

### PlayFunctionTests.cs  `C#, 60 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/PlayFunctionTests.cs:5
- class `PlayFunctionTests` — PLAY(n) - how many notes are still queued for background music. — PowerBasic.Compiler.Tests/CodeGen/PlayFunctionTests.cs:22

### PureFoldTests.cs  `C#, 94 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/PureFoldTests.cs:5
- class `PureFoldTests` — pb36 O19 - automatic compile-time evaluation of pure functions. A function whose — PowerBasic.Compiler.Tests/CodeGen/PureFoldTests.cs:16

### Qb45DialectTests.cs  `C#, 122 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/Qb45DialectTests.cs:4
- class `Qb45DialectTests` — QuickBASIC 4.5 semantics, pinned against the genuine BC.EXE 4.50 + LINK — PowerBasic.Compiler.Tests/CodeGen/Qb45DialectTests.cs:12
- class `MemorySourceProvider` — region helpers — PowerBasic.Compiler.Tests/CodeGen/Qb45DialectTests.cs:17
- method `TryReadSource(string name, string? includedFrom, out string sourceText, out string…` — PowerBasic.Compiler.Tests/CodeGen/Qb45DialectTests.cs:19
- method `RunSourceAs` — PowerBasic.Compiler.Tests/CodeGen/Qb45DialectTests.cs:36
- method `SQR(2)` — PowerBasic.Compiler.Tests/CodeGen/Qb45DialectTests.cs:99
- method `LOG(2.718281828459045#)` — PowerBasic.Compiler.Tests/CodeGen/Qb45DialectTests.cs:100

### RedundantLoadTests.cs  `C#, 64 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/RedundantLoadTests.cs:4
- class `RedundantLoadTests` — pb36 redundant-load elimination: a repeated array-element read a%(i%) with no — PowerBasic.Compiler.Tests/CodeGen/RedundantLoadTests.cs:14

### RegisterAllocationTests.cs  `C#, 68 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/RegisterAllocationTests.cs:5
- class `RegisterAllocationTests` — Graph-coloring register allocator over the scalar interference graph — PowerBasic.Compiler.Tests/CodeGen/RegisterAllocationTests.cs:13

### ResourceContractTests.cs  `C#, 143 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/ResourceContractTests.cs:5
- class `ResourceContractTests` — pb36 $RESOURCE (a file baked into the image as a static BYTE array) and contracts — PowerBasic.Compiler.Tests/CodeGen/ResourceContractTests.cs:13
- field `source` — PowerBasic.Compiler.Tests/CodeGen/ResourceContractTests.cs:39
- method `logo(0)` — PowerBasic.Compiler.Tests/CodeGen/ResourceContractTests.cs:41
- method `LBOUND(logo)` — PowerBasic.Compiler.Tests/CodeGen/ResourceContractTests.cs:42
- field `source` — PowerBasic.Compiler.Tests/CodeGen/ResourceContractTests.cs:58
- method `Compile(string source)` — $OPTIMIZE SPEED is the release mode: the check (and its message literal) vanish — PowerBasic.Compiler.Tests/CodeGen/ResourceContractTests.cs:82
- method `HasMarker(byte[] exe)` — PowerBasic.Compiler.Tests/CodeGen/ResourceContractTests.cs:91
- method `if(exe.AsSpan(i, marker.Length).SequenceEqual(marker))` — PowerBasic.Compiler.Tests/CodeGen/ResourceContractTests.cs:94
- field `body` — PowerBasic.Compiler.Tests/CodeGen/ResourceContractTests.cs:98

### RoundIntrinsicTests.cs  `C#, 87 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/RoundIntrinsicTests.cs:5
- class `RoundIntrinsicTests` — ROUND, the last of the census's built-ins that looked like an oversight rather than a — PowerBasic.Compiler.Tests/CodeGen/RoundIntrinsicTests.cs:21
- method `ROUND(n)` — PowerBasic.Compiler.Tests/CodeGen/RoundIntrinsicTests.cs:75
- method `ROUND(2.718281828, p)` — PowerBasic.Compiler.Tests/CodeGen/RoundIntrinsicTests.cs:84

### RoutedUnitTests.cs  `C#, 155 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/RoutedUnitTests.cs:6
- class `RoutedUnitTests` — A $COMPILE UNIT compiled through the x86-16 back end, linked, and run. — PowerBasic.Compiler.Tests/CodeGen/RoutedUnitTests.cs:23
- method `Bump` — PowerBasic.Compiler.Tests/CodeGen/RoutedUnitTests.cs:37
- class `MemorySource` — """; — PowerBasic.Compiler.Tests/CodeGen/RoutedUnitTests.cs:61
- method `TryReadSource(string name, string? includedFrom, out string sourceText, out string…` — PowerBasic.Compiler.Tests/CodeGen/RoutedUnitTests.cs:63

### ScalarLivenessTests.cs  `C#, 91 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/ScalarLivenessTests.cs:5
- class `ScalarLivenessTests` — Scalar live-variable analysis (docs/PB36.md O5 prerequisite): the per-variable — PowerBasic.Compiler.Tests/CodeGen/ScalarLivenessTests.cs:14

### ScreenFunctionTests.cs  `C#, 111 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/ScreenFunctionTests.cs:5
- class `ScreenFunctionTests` — SCREEN(row, col [, colour]) - the text page read back. — PowerBasic.Compiler.Tests/CodeGen/ScreenFunctionTests.cs:20
- method `SCREEN` — PowerBasic.Compiler.Tests/CodeGen/ScreenFunctionTests.cs:37
- method `SCREEN` — PowerBasic.Compiler.Tests/CodeGen/ScreenFunctionTests.cs:46
- method `SCREEN` — PowerBasic.Compiler.Tests/CodeGen/ScreenFunctionTests.cs:55
- method `SCREEN` — PowerBasic.Compiler.Tests/CodeGen/ScreenFunctionTests.cs:65
- method `SCREEN` — PowerBasic.Compiler.Tests/CodeGen/ScreenFunctionTests.cs:74
- method `SCREEN` — PowerBasic.Compiler.Tests/CodeGen/ScreenFunctionTests.cs:84
- method `SCREEN` — PowerBasic.Compiler.Tests/CodeGen/ScreenFunctionTests.cs:94
- method `SCREEN` — PowerBasic.Compiler.Tests/CodeGen/ScreenFunctionTests.cs:107

### SelectJumpTableTests.cs  `C#, 234 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/SelectJumpTableTests.cs:4
- class `SelectJumpTableTests` — pb36 dense SELECT CASE -> jump table. The byte-identical output contract is enforced — PowerBasic.Compiler.Tests/CodeGen/SelectJumpTableTests.cs:21
- method `if(image[i + j] != needle[j])` — PowerBasic.Compiler.Tests/CodeGen/SelectJumpTableTests.cs:38
- field `sel` — O0101: a dense SELECT with a wide span but few distinct arms (12 values -> 3 arms + default) — PowerBasic.Compiler.Tests/CodeGen/SelectJumpTableTests.cs:76
- field `sel` — O0099: a value list whose window is 16..31 wide (0, 5, 11, 17, 20 spans 20) needs a 32-bit mask, — PowerBasic.Compiler.Tests/CodeGen/SelectJumpTableTests.cs:154

### SelfDifferentialTests.cs  `C#, 119 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/SelfDifferentialTests.cs:4
- class `SelfDifferentialTests` — The optimizer's one inviolable rule, stated as a test: the same source compiled with and — PowerBasic.Compiler.Tests/CodeGen/SelfDifferentialTests.cs:17
- method `IF(i% + i%)` — PowerBasic.Compiler.Tests/CodeGen/SelfDifferentialTests.cs:28
- method `PRINT(15 + p%)` — PowerBasic.Compiler.Tests/CodeGen/SelfDifferentialTests.cs:56
- method `Opaque(v&)` — PowerBasic.Compiler.Tests/CodeGen/SelfDifferentialTests.cs:88

### SsaTests.cs  `C#, 368 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/SsaTests.cs:5
- class `SsaTests` — SSA mid-end (docs/PB36.md): CFG construction, dominators/frontiers, SSA form — PowerBasic.Compiler.Tests/CodeGen/SsaTests.cs:14

### StackArrayTests.cs  `C#, 165 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/StackArrayTests.cs:5
- class `StackArrayTests` — pb36 stack arrays: DIM STACK a(1 TO 8) AS INTEGER inside a procedure places the — PowerBasic.Compiler.Tests/CodeGen/StackArrayTests.cs:13
- field `source` — PowerBasic.Compiler.Tests/CodeGen/StackArrayTests.cs:64
- method `a` — PowerBasic.Compiler.Tests/CodeGen/StackArrayTests.cs:65
- method `a(1)` — PowerBasic.Compiler.Tests/CodeGen/StackArrayTests.cs:73
- field `source` — the whole point: a DGROUP-resident local array would be smashed by the recursive call — PowerBasic.Compiler.Tests/CodeGen/StackArrayTests.cs:83
- method `a(1 TO 3)` — PowerBasic.Compiler.Tests/CodeGen/StackArrayTests.cs:87
- field `source` — the frame size reaches the image as a "constant label" - a pseudo-label whose position IS — PowerBasic.Compiler.Tests/CodeGen/StackArrayTests.cs:106
- method `g` — PowerBasic.Compiler.Tests/CodeGen/StackArrayTests.cs:107
- field `source` — PowerBasic.Compiler.Tests/CodeGen/StackArrayTests.cs:136
- method `g` — PowerBasic.Compiler.Tests/CodeGen/StackArrayTests.cs:137
- method `g` — PowerBasic.Compiler.Tests/CodeGen/StackArrayTests.cs:143
- field `source` — PowerBasic.Compiler.Tests/CodeGen/StackArrayTests.cs:154

### StdcallPascalTests.cs  `C#, 142 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/StdcallPascalTests.cs:8
- class `StdcallPascalTests` — STDCALL / PASCAL external calling conventions (docs/LINKER.md): unlike CDECL, — PowerBasic.Compiler.Tests/CodeGen/StdcallPascalTests.cs:19
- method `Record(0x80, Str("ADDONES"))` — PowerBasic.Compiler.Tests/CodeGen/StdcallPascalTests.cs:38
- field `source` — PowerBasic.Compiler.Tests/CodeGen/StdcallPascalTests.cs:61
- method `addone(41)` — PowerBasic.Compiler.Tests/CodeGen/StdcallPascalTests.cs:63
- field `source` — PowerBasic.Compiler.Tests/CodeGen/StdcallPascalTests.cs:78
- method `addone(41)` — PowerBasic.Compiler.Tests/CodeGen/StdcallPascalTests.cs:80
- method `Image(string convention)` — PowerBasic.Compiler.Tests/CodeGen/StdcallPascalTests.cs:108
- method `AddSp4Count(byte[] image)` — count "add sp, 4" (83 C4 04) opcode occurrences in the image — PowerBasic.Compiler.Tests/CodeGen/StdcallPascalTests.cs:123
- method `if(image[i] == 0x83 && image[i + 1] == 0xC4 && image[i + 2] == 0x04)` — PowerBasic.Compiler.Tests/CodeGen/StdcallPascalTests.cs:126

### StringCompareWideningTests.cs  `C#, 125 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/StringCompareWideningTests.cs:5
- class `StringCompareWideningTests` — O0298: the equal-length half of a string = / &lt;&gt; compares a WORD at a time. — PowerBasic.Compiler.Tests/CodeGen/StringCompareWideningTests.cs:26

### StringMinMaxTests.cs  `C#, 85 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/StringMinMaxTests.cs:5
- class `StringMinMaxTests` — MIN$ and MAX$, two more of the built-ins the intrinsic census found binding and — PowerBasic.Compiler.Tests/CodeGen/StringMinMaxTests.cs:18

### StringRemoveTests.cs  `C#, 93 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/StringRemoveTests.cs:5
- class `StringRemoveTests` — REMOVE$(s$, match$) - the source with every occurrence of the match cut out. — PowerBasic.Compiler.Tests/CodeGen/StringRemoveTests.cs:18

### TargetCostTests.cs  `C#, 138 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/TargetCostTests.cs:2
- class `TargetCostTests` — O0174 the per-target cost model (docs/optimizations/O0174). These pin the trade-offs the model exis… — PowerBasic.Compiler.Tests/CodeGen/TargetCostTests.cs:10

### Tb11DialectTests.cs  `C#, 108 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/Tb11DialectTests.cs:4
- class `Tb11DialectTests` — Turbo Basic 1.1 runtime semantics, pinned against the genuine TB.EXE 1.1 — PowerBasic.Compiler.Tests/CodeGen/Tb11DialectTests.cs:12
- class `MemorySourceProvider` — region helpers — PowerBasic.Compiler.Tests/CodeGen/Tb11DialectTests.cs:17
- method `TryReadSource(string name, string? includedFrom, out string sourceText, out string…` — PowerBasic.Compiler.Tests/CodeGen/Tb11DialectTests.cs:19
- method `VAL("&HFFFF")` — PowerBasic.Compiler.Tests/CodeGen/Tb11DialectTests.cs:93

### TryCatchTests.cs  `C#, 404 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/TryCatchTests.cs:5
- class `TryCatchTests` — PB 3.6 structured exception handling - TRY / CATCH / FINALLY / END TRY — PowerBasic.Compiler.Tests/CodeGen/TryCatchTests.cs:16
- field `source` — PowerBasic.Compiler.Tests/CodeGen/TryCatchTests.cs:246
- field `source` — PowerBasic.Compiler.Tests/CodeGen/TryCatchTests.cs:261
- field `source` — PowerBasic.Compiler.Tests/CodeGen/TryCatchTests.cs:277
- field `source` — PowerBasic.Compiler.Tests/CodeGen/TryCatchTests.cs:290
- field `source` — PowerBasic.Compiler.Tests/CodeGen/TryCatchTests.cs:304
- field `source` — the FINALLY body catches an unrelated error (overwriting the runtime error cell); the — PowerBasic.Compiler.Tests/CodeGen/TryCatchTests.cs:329
- method `CountFinallyStore(string source)` — the FINALLY body is shared via jumps between the normal and fault/catch edges, — PowerBasic.Compiler.Tests/CodeGen/TryCatchTests.cs:356
- method `if(exe[i] == 0x1D && exe[i + 1] == 0x4B)` — PowerBasic.Compiler.Tests/CodeGen/TryCatchTests.cs:366
- field `source` — Arms an ON ERROR handler, runs a clean TRY, then faults after END TRY: — PowerBasic.Compiler.Tests/CodeGen/TryCatchTests.cs:384

### UnitLinkTests.cs  `C#, 250 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/UnitLinkTests.cs:5
- class `UnitLinkTests` — $COMPILE UNIT / $LINK end to end: unit emission (exports, imports, fixups, — PowerBasic.Compiler.Tests/CodeGen/UnitLinkTests.cs:13
- method `Bump` — PowerBasic.Compiler.Tests/CodeGen/UnitLinkTests.cs:23
- class `MemorySourceProvider` — region helpers — PowerBasic.Compiler.Tests/CodeGen/UnitLinkTests.cs:48
- method `TryReadSource(string name, string? includedFrom, out string sourceText, out string…` — PowerBasic.Compiler.Tests/CodeGen/UnitLinkTests.cs:50
- field `source` — PowerBasic.Compiler.Tests/CodeGen/UnitLinkTests.cs:119
- field `source` — PowerBasic.Compiler.Tests/CodeGen/UnitLinkTests.cs:136
- method `Dummy` — PowerBasic.Compiler.Tests/CodeGen/UnitLinkTests.cs:139
- field `mismatched` — PowerBasic.Compiler.Tests/CodeGen/UnitLinkTests.cs:173
- field `callsMissing` — PowerBasic.Compiler.Tests/CodeGen/UnitLinkTests.cs:186
- method `Missing(x%)` — PowerBasic.Compiler.Tests/CodeGen/UnitLinkTests.cs:187
- field `source` — PowerBasic.Compiler.Tests/CodeGen/UnitLinkTests.cs:200
- method `Missing(x%)` — PowerBasic.Compiler.Tests/CodeGen/UnitLinkTests.cs:201

### ValueFactRangePropagationTests.cs  `C#, 131 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/ValueFactRangePropagationTests.cs:5
- class `ValueFactRangePropagationTests` — PowerBasic.Compiler.Tests/CodeGen/ValueFactRangePropagationTests.cs:7
- field `source` — PowerBasic.Compiler.Tests/CodeGen/ValueFactRangePropagationTests.cs:55
- field `source` — PowerBasic.Compiler.Tests/CodeGen/ValueFactRangePropagationTests.cs:82
- field `source` — PowerBasic.Compiler.Tests/CodeGen/ValueFactRangePropagationTests.cs:96
- field `source` — PowerBasic.Compiler.Tests/CodeGen/ValueFactRangePropagationTests.cs:107

### ValueFactReductionTests.cs  `C#, 203 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/ValueFactReductionTests.cs:5
- class `ValueFactReductionTests` — PowerBasic.Compiler.Tests/CodeGen/ValueFactReductionTests.cs:7
- method `new` — PowerBasic.Compiler.Tests/CodeGen/ValueFactReductionTests.cs:83
- method `new` — PowerBasic.Compiler.Tests/CodeGen/ValueFactReductionTests.cs:95
- method `new` — PowerBasic.Compiler.Tests/CodeGen/ValueFactReductionTests.cs:108

### XmsEmsArrayTests.cs  `C#, 143 lines`
- namespace `PowerBasic.Compiler.Tests.CodeGen` — PowerBasic.Compiler.Tests/CodeGen/XmsEmsArrayTests.cs:4
- class `XmsEmsArrayTests` — pb36 external-memory arrays: DIM EMS/XMS a(...) stores the data outside conventional — PowerBasic.Compiler.Tests/CodeGen/XmsEmsArrayTests.cs:13
- field `source` — 5000 LONGs = 20000 bytes: spans more than one 16 KiB EMS page, so the — PowerBasic.Compiler.Tests/CodeGen/XmsEmsArrayTests.cs:31
- method `LBOUND(a&)` — PowerBasic.Compiler.Tests/CodeGen/XmsEmsArrayTests.cs:38
- field `source` — PowerBasic.Compiler.Tests/CodeGen/XmsEmsArrayTests.cs:45
- method `p` — PowerBasic.Compiler.Tests/CodeGen/XmsEmsArrayTests.cs:50
- field `source` — both arrays live behind the same EMS page frame: every access must map ITS handle's — PowerBasic.Compiler.Tests/CodeGen/XmsEmsArrayTests.cs:71
- field `source` — 30000 LONGs = 120000 bytes > the 64 KiB page frame: offsets must page-map, not wrap — PowerBasic.Compiler.Tests/CodeGen/XmsEmsArrayTests.cs:87
- field `source` — R3: the BSS entry zero and the EMS zero-fill store DWORDs under $CPU 80386 - — PowerBasic.Compiler.Tests/CodeGen/XmsEmsArrayTests.cs:101
- field `source` — C6: on DOS 5+ the entry stub links UMBs and prefers high memory, so a HUGE — PowerBasic.Compiler.Tests/CodeGen/XmsEmsArrayTests.cs:117
- method `h(1 TO 20000)` — PowerBasic.Compiler.Tests/CodeGen/XmsEmsArrayTests.cs:119
- method `h(1)` — PowerBasic.Compiler.Tests/CodeGen/XmsEmsArrayTests.cs:122
- method `VARSEG(h(1))` — PowerBasic.Compiler.Tests/CodeGen/XmsEmsArrayTests.cs:123
- field `source` — PowerBasic.Compiler.Tests/CodeGen/XmsEmsArrayTests.cs:130
- method `e` — PowerBasic.Compiler.Tests/CodeGen/XmsEmsArrayTests.cs:131

## PowerBasic.Compiler.Tests/Dialects/

### DialectBattery.cs  `C#, 82 lines`
- namespace `PowerBasic.Compiler.Tests.Dialects` — PowerBasic.Compiler.Tests/Dialects/DialectBattery.cs:3
- class `DialectBattery` — The per-dialect conformance battery, as data. — PowerBasic.Compiler.Tests/Dialects/DialectBattery.cs:16
- enum `State` — How far along a dimension is for one dialect. — PowerBasic.Compiler.Tests/Dialects/DialectBattery.cs:19
- record `Measurement` — PowerBasic.Compiler.Tests/Dialects/DialectBattery.cs:32
- record `Dimension` — Stable slug, used as the anchor in the generated README. — PowerBasic.Compiler.Tests/Dialects/DialectBattery.cs:38

### DialectBatteryTests.cs  `C#, 157 lines`
- namespace `PowerBasic.Compiler.Tests.Dialects` — PowerBasic.Compiler.Tests/Dialects/DialectBatteryTests.cs:3
- class `DialectBatteryTests` — Runs the per-dialect battery and writes each dialect's README from what it measured. — PowerBasic.Compiler.Tests/Dialects/DialectBatteryTests.cs:13
- method `return(run.Output, run.ExitCode)` — PowerBasic.Compiler.Tests/Dialects/DialectBatteryTests.cs:36

### DialectBitExactClaims.cs  `C#, 44 lines`
- namespace `PowerBasic.Compiler.Tests.Dialects` — PowerBasic.Compiler.Tests/Dialects/DialectBitExactClaims.cs:1
- class `DialectBitExactClaims` — D11 - bit-exact numeric behaviour, starting where it can be settled without a vintage binary: — PowerBasic.Compiler.Tests/Dialects/DialectBitExactClaims.cs:18
- record `Claim` — The literal as it appears in source. — PowerBasic.Compiler.Tests/Dialects/DialectBitExactClaims.cs:23

### DialectMetaClaims.cs  `C#, 120 lines`
- namespace `PowerBasic.Compiler.Tests.Dialects` — PowerBasic.Compiler.Tests/Dialects/DialectMetaClaims.cs:2
- class `DialectMetaClaims` — D9 - the metastatements, and whether they actually change the executable. — PowerBasic.Compiler.Tests/Dialects/DialectMetaClaims.cs:19
- enum `Kind` — What the claim asserts about the two compilations. — PowerBasic.Compiler.Tests/Dialects/DialectMetaClaims.cs:24
- record `Claim` — Stable name for the claim. — PowerBasic.Compiler.Tests/Dialects/DialectMetaClaims.cs:37

### DialectNumericClaims.cs  `C#, 92 lines`
- namespace `PowerBasic.Compiler.Tests.Dialects` — PowerBasic.Compiler.Tests/Dialects/DialectNumericClaims.cs:3
- class `DialectNumericClaims` — D6 - the numeric typing each dialect actually has, as a table of claims. — PowerBasic.Compiler.Tests/Dialects/DialectNumericClaims.cs:19
- record `Claim` — Stable name, used in the failure message and the README note. — PowerBasic.Compiler.Tests/Dialects/DialectNumericClaims.cs:27

### DialectProbes.cs  `C#, 561 lines`
- namespace `PowerBasic.Compiler.Tests.Dialects` — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:6
- class `DialectProbes` — The measurements behind . Each probe answers one dimension for one — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:17
- record `FrontEnd` — Whether the front end accepts a source, and whether a rejection was a controlled diagnostic. — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:22
- class `MemorySource` — Feeds an in-memory source to the preprocessor, which is a separate entry point from the lexer. — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:25
- method `TryReadSource(string name, string? includedFrom, out string sourceText, out string…` — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:26
- method `new(false, true, e.Message)` — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:47
- method `new(DialectBattery.State.NotApplicable, 0, 0, "this dialect provides eve…` — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:80
- method `if(model.Errors.Count > 0)` — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:99
- method `if(IrLowering.TryLowerModule(model, out var why) is not null)` — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:106
- method `if(string.IsNullOrWhiteSpace(why))` — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:108
- method `new(DialectBattery.State.Partial, lowered, total, $"{crashed.Count} form…` — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:115
- method `new(DialectBattery.State.NotApplicable, 0, 0, why)` — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:184
- method `new(DialectBattery.State.NotApplicable, 0, 0, "the dead-branch dimension…` — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:200
- method `if(model.Errors.Count > 0)` — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:232
- method `if(module is null)` — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:237
- method `new(DialectBattery.State.NotApplicable, 0, 0, "no compiler metastatement…` — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:268
- method `if(second is null)` — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:284
- method `if(first is null)` — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:286
- method `if` — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:292
- method `Unprobed("docs/QUIRKS.md is not present")` — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:323
- method `new(DialectBattery.State.Unprobed, 0, 0, "docs/QUIRKS.md catalogues the …` — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:330
- method `Unprobed("no quirk rows found in docs/QUIRKS.md")` — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:338
- method `new(DialectBattery.State.Held, reproduced, rows.Count, $"all {rows.Count…` — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:347
- method `if(statement is AssignStmt { Value: FloatLiteralExpr f })` — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:395
- method `if(actual == scenario.Expect)` — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:431
- method `new(DialectBattery.State.Held, covered, total, $"all {total} {verb}")` — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:485
- method `if` — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:505
- method `if(statement is AssignStmt assign && model.ExpressionTypes.TryGetValue(…` — PowerBasic.Compiler.Tests/Dialects/DialectProbes.cs:543

### DialectRuntimeClaims.cs  `C#, 53 lines`
- namespace `PowerBasic.Compiler.Tests.Dialects` — PowerBasic.Compiler.Tests/Dialects/DialectRuntimeClaims.cs:2
- class `DialectRuntimeClaims` — D7 - which runtime implementation a dialect selects, where the dialects genuinely disagree. — PowerBasic.Compiler.Tests/Dialects/DialectRuntimeClaims.cs:21
- record `Claim` — Stable name for the claim. — PowerBasic.Compiler.Tests/Dialects/DialectRuntimeClaims.cs:28

### DialectRuntimeScenarios.cs  `C#, 80 lines`
- namespace `PowerBasic.Compiler.Tests.Dialects` — PowerBasic.Compiler.Tests/Dialects/DialectRuntimeScenarios.cs:2
- class `DialectRuntimeScenarios` — D8 - what the runtime functions actually do, checked by running the produced executable. — PowerBasic.Compiler.Tests/Dialects/DialectRuntimeScenarios.cs:19
- record `Scenario` — Stable name for the scenario. — PowerBasic.Compiler.Tests/Dialects/DialectRuntimeScenarios.cs:31

## PowerBasic.Compiler.Tests/Emit/

### DemangleTests.cs  `C#, 130 lines`
- namespace `PowerBasic.Compiler.Tests.Emit` — PowerBasic.Compiler.Tests/Emit/DemangleTests.cs:2
- class `DemangleTests` — The C++ symbol demangler (docs/LINKER.md "C++ mangled symbols"): turns a mangled — PowerBasic.Compiler.Tests/Emit/DemangleTests.cs:12

### DirectOptimizerOnRenderedBasicTests.cs  `C#, 249 lines`
- namespace `PowerBasic.Compiler.Tests.Emit` — PowerBasic.Compiler.Tests/Emit/DirectOptimizerOnRenderedBasicTests.cs:9
- class `DirectOptimizerOnRenderedBasicTests` — The direct emitter's optimizations, checked against BASIC the IR wrote. — PowerBasic.Compiler.Tests/Emit/DirectOptimizerOnRenderedBasicTests.cs:36
- record `Behaviour` — PowerBasic.Compiler.Tests/Emit/DirectOptimizerOnRenderedBasicTests.cs:55
- method `if` — PowerBasic.Compiler.Tests/Emit/DirectOptimizerOnRenderedBasicTests.cs:108
- method `if(optimized != plain)` — PowerBasic.Compiler.Tests/Emit/DirectOptimizerOnRenderedBasicTests.cs:136

### IrBasicWriterCensusTests.cs  `C#, 272 lines`
- namespace `PowerBasic.Compiler.Tests.Emit` — PowerBasic.Compiler.Tests/Emit/IrBasicWriterCensusTests.cs:7
- class `IrBasicWriterCensusTests` — How much of the real corpus can render, and - for everything it — PowerBasic.Compiler.Tests/Emit/IrBasicWriterCensusTests.cs:19
- method `if(model.Errors.Count > 0)` — PowerBasic.Compiler.Tests/Emit/IrBasicWriterCensusTests.cs:39
- method `if(module is null)` — PowerBasic.Compiler.Tests/Emit/IrBasicWriterCensusTests.cs:42
- method `foreach` — PowerBasic.Compiler.Tests/Emit/IrBasicWriterCensusTests.cs:48
- method `if(model.Errors.Count > 0)` — PowerBasic.Compiler.Tests/Emit/IrBasicWriterCensusTests.cs:104
- method `if(module is null)` — PowerBasic.Compiler.Tests/Emit/IrBasicWriterCensusTests.cs:109
- method `if(back.Errors.Count > 0)` — PowerBasic.Compiler.Tests/Emit/IrBasicWriterCensusTests.cs:124

### IrBasicWriterTests.cs  `C#, 168 lines`
- namespace `PowerBasic.Compiler.Tests.Emit` — PowerBasic.Compiler.Tests/Emit/IrBasicWriterTests.cs:8
- class `IrBasicWriterTests` — The IR rendered back to PowerBASIC, checked by round trip: source → IR → source → compile → — PowerBasic.Compiler.Tests/Emit/IrBasicWriterTests.cs:24

### IrBasicWriterUnsignedLiteralTests.cs  `C#, 25 lines`
- namespace `PowerBasic.Compiler.Tests.Emit` — PowerBasic.Compiler.Tests/Emit/IrBasicWriterUnsignedLiteralTests.cs:3
- class `IrBasicWriterUnsignedLiteralTests` — PowerBasic.Compiler.Tests/Emit/IrBasicWriterUnsignedLiteralTests.cs:5

### IrBasicWriterWholeProgramTests.cs  `C#, 251 lines`
- namespace `PowerBasic.Compiler.Tests.Emit` — PowerBasic.Compiler.Tests/Emit/IrBasicWriterWholeProgramTests.cs:8
- class `IrBasicWriterWholeProgramTests` — Whole programs round-tripped through the IR: source → IR → source → compile → run, compared — PowerBasic.Compiler.Tests/Emit/IrBasicWriterWholeProgramTests.cs:20
- method `Announce(3)` — PowerBasic.Compiler.Tests/Emit/IrBasicWriterWholeProgramTests.cs:79
- method `a(0 TO 9)` — PowerBasic.Compiler.Tests/Emit/IrBasicWriterWholeProgramTests.cs:125
- method `a(i)` — PowerBasic.Compiler.Tests/Emit/IrBasicWriterWholeProgramTests.cs:131
- method `a(5 TO 8)` — PowerBasic.Compiler.Tests/Emit/IrBasicWriterWholeProgramTests.cs:140
- method `a(5)` — PowerBasic.Compiler.Tests/Emit/IrBasicWriterWholeProgramTests.cs:143
- method `a(0 TO 4)` — PowerBasic.Compiler.Tests/Emit/IrBasicWriterWholeProgramTests.cs:150
- method `a` — PowerBasic.Compiler.Tests/Emit/IrBasicWriterWholeProgramTests.cs:154
- method `LEN(a$)` — PowerBasic.Compiler.Tests/Emit/IrBasicWriterWholeProgramTests.cs:170
- method `ASC(a$)` — PowerBasic.Compiler.Tests/Emit/IrBasicWriterWholeProgramTests.cs:175
- method `SQR(x)` — PowerBasic.Compiler.Tests/Emit/IrBasicWriterWholeProgramTests.cs:184
- method `VAL("17")` — PowerBasic.Compiler.Tests/Emit/IrBasicWriterWholeProgramTests.cs:197

### IrDialectCarryTests.cs  `C#, 186 lines`
- namespace `PowerBasic.Compiler.Tests.Emit` — PowerBasic.Compiler.Tests/Emit/IrDialectCarryTests.cs:6
- class `IrDialectCarryTests` — Dialect facts the IR carries, and what the pb35 renderer does with the ones pb35 has no spelling — PowerBasic.Compiler.Tests/Emit/IrDialectCarryTests.cs:17

### IrPassObservableEquivalenceTests.cs  `C#, 251 lines`
- namespace `PowerBasic.Compiler.Tests.Emit` — PowerBasic.Compiler.Tests/Emit/IrPassObservableEquivalenceTests.cs:9
- class `IrPassObservableEquivalenceTests` — The observable contract, made checkable: an optimization pass may rewrite a program however it — PowerBasic.Compiler.Tests/Emit/IrPassObservableEquivalenceTests.cs:26
- method `a(0 TO 9)` — PowerBasic.Compiler.Tests/Emit/IrPassObservableEquivalenceTests.cs:95
- method `a(i)` — PowerBasic.Compiler.Tests/Emit/IrPassObservableEquivalenceTests.cs:101
- method `Announce(3)` — PowerBasic.Compiler.Tests/Emit/IrPassObservableEquivalenceTests.cs:108
- method `pass(fn)` — PowerBasic.Compiler.Tests/Emit/IrPassObservableEquivalenceTests.cs:156
- method `RunOnEveryFunction(m, Mem2Reg.Run)` — PowerBasic.Compiler.Tests/Emit/IrPassObservableEquivalenceTests.cs:179
- method `RunOnEveryFunction(m, run)` — PowerBasic.Compiler.Tests/Emit/IrPassObservableEquivalenceTests.cs:180
- method `if(got != expected)` — PowerBasic.Compiler.Tests/Emit/IrPassObservableEquivalenceTests.cs:186
- method `RunOnEveryFunction(m, Mem2Reg.Run)` — PowerBasic.Compiler.Tests/Emit/IrPassObservableEquivalenceTests.cs:207
- method `run(m)` — PowerBasic.Compiler.Tests/Emit/IrPassObservableEquivalenceTests.cs:208
- method `if(got != expected)` — PowerBasic.Compiler.Tests/Emit/IrPassObservableEquivalenceTests.cs:214

### LinkerTests.cs  `C#, 174 lines`
- namespace `PowerBasic.Compiler.Tests.Emit` — PowerBasic.Compiler.Tests/Emit/LinkerTests.cs:2
- class `LinkerTests` — PowerBasic.Compiler.Tests/Emit/LinkerTests.cs:4

### MzExeWriterTests.cs  `C#, 214 lines`
- namespace `PowerBasic.Compiler.Tests.Emit` — PowerBasic.Compiler.Tests/Emit/MzExeWriterTests.cs:3
- class `MzExeWriterTests` — PowerBasic.Compiler.Tests/Emit/MzExeWriterTests.cs:5

### OmfLibraryWriterTests.cs  `C#, 183 lines`
- namespace `PowerBasic.Compiler.Tests.Emit` — PowerBasic.Compiler.Tests/Emit/OmfLibraryWriterTests.cs:3
- class `OmfLibraryWriterTests` — The OMF library writer (docs/LINKER.md): emit several units as one .LIB archive and prove the — PowerBasic.Compiler.Tests/Emit/OmfLibraryWriterTests.cs:15

### OmfTests.cs  `C#, 495 lines`
- namespace `PowerBasic.Compiler.Tests.Emit` — PowerBasic.Compiler.Tests/Emit/OmfTests.cs:4
- class `OmfTests` — The external OMF object linker (docs/LINKER.md, M1): parse a genuine-shaped 16-bit — PowerBasic.Compiler.Tests/Emit/OmfTests.cs:13
- field `fixdat` — FIXDAT: frame method 0 (SEGDEF), P-bit(0x4)=no displacement, target method 0 (SEGDEF). — PowerBasic.Compiler.Tests/Emit/OmfTests.cs:174
- method `Record(0x80, Str("DREF"))` — PowerBasic.Compiler.Tests/Emit/OmfTests.cs:182
- method `Record(0x80, Str("TWOSEG"))` — PowerBasic.Compiler.Tests/Emit/OmfTests.cs:215
- method `Record(0x80, Str("FARSEG"))` — PowerBasic.Compiler.Tests/Emit/OmfTests.cs:253
- method `Record(0x80, Str("FARPTR"))` — PowerBasic.Compiler.Tests/Emit/OmfTests.cs:283

### PbuPblTests.cs  `C#, 125 lines`
- namespace `PowerBasic.Compiler.Tests.Emit` — PowerBasic.Compiler.Tests/Emit/PbuPblTests.cs:2
- class `PbuPblTests` — PowerBasic.Compiler.Tests/Emit/PbuPblTests.cs:4

### PowerBasic35EmitterTests.cs  `C#, 354 lines`
- namespace `PowerBasic.Compiler.Tests.Emit` — PowerBasic.Compiler.Tests/Emit/PowerBasic35EmitterTests.cs:4
- class `PowerBasic35EmitterTests` — The back-emitter (): turns a bound program back into PB 3.5-compatible — PowerBasic.Compiler.Tests/Emit/PowerBasic35EmitterTests.cs:13

## PowerBasic.Compiler.Tests/Exec/

### Cpu8086.cs  `C#, 2167 lines`
- namespace `PowerBasic.Compiler.Tests.Exec` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:2
- class `Cpu8086` — A real-mode 8086 interpreter, enough of one to run the executables this compiler emits. — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:23
- class `MemoryFile` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:52
- class `OpenFile` — One DOS handle onto a file. The POSITION belongs to the handle rather than to the file, which is — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:63
- record `EmsMapping` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:67
- method `Cpu8086Exception($"EXEC nesting exceeded {_MAX_EXEC_DEPTH} images")` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:245
- method `if(mode == 2)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:353
- method `if(wide)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:552
- method `if(op != 7)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:556
- method `if(op != 7)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:563
- method `if((opcode & 1) != 0)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:572
- method `if(op != 7)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:574
- method `if(op != 7)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:578
- method `if(this.Condition(opcode - 0x70))` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:621
- method `if(opcode == 0x80)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:628
- method `if(op != 7)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:630
- method `if(op != 7)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:635
- method `if(mode == 3)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:654
- method `Cpu8086Exception("LEA with a register operand")` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:655
- method `if(opcode == 0xC0)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:696
- method `if((opcode & 1) == 0)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:713
- method `if(taken)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:727
- method `Cpu8086Exception( $"unimplemented opcode {opcode:X2} at {this._cs:X4}:{this._ip - 1:X…` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:754
- method `if(toRegister)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:781
- method `unchecked((uint)(int)(sbyte)this.Fetch())` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:791
- method `Cpu8086Exception($"unimplemented dword C7 operation /{operation}")` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:801
- method `Cpu8086Exception($"unimplemented opcode 66 0F {opcode:X2}")` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:874
- method `Cpu8086Exception("only register dword SHLD/SHRD is supported")` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:878
- method `if(operand == 0)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:931
- method `if(quotient > uint.MaxValue)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:935
- method `if(divisor == 0)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:943
- method `if(dividend == long.MinValue && divisor == -1)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:946
- method `if(quotient is < int.MinValue or > int.MaxValue)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:949
- record `X87Value` — One x87 value. FILD must retain every bit of a signed 64-bit integer: extended precision has a — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:965
- method `Exact` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:967
- method `Floating(double value)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:969
- method `Abs` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:970
- method `Negate` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:974
- method `Cpu8086Exception($"unimplemented x87 {opcode:X2} /{reg} at {this._cs:X4}:{start:X4}")` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1068
- method `if((bits & 1) != 0)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1139
- method `if((bits & 2) != 0)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1141
- method `if((bits & 4) != 0)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1143
- method `if(modrm >= 0xD8)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1174
- method `if(!intoStack0 && op >= 4)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1187
- method `Arithmetic(op, this.St(0), this.St(index))` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1190
- method `if(intoStack0)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1192
- method `if(opcode == 0xDE)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1196
- method `Cpu8086Exception($"unimplemented x87 {opcode:X2} {modrm:X2} at {this._cs:X4}:{start:X…` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1201
- method `if(ai == bi)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1275
- method `if(this.Condition(opcode - 0x80))` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1348
- method `if(count == 0)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1371
- method `Cpu8086Exception($"unimplemented 0F {opcode:X2} at {this._cs:X4}:{this._ip - 2:X4}")` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1385
- method `if(wide)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1544
- method `if(wide)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1558
- method `if(wide)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1571
- method `if(wide)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1584
- method `if(wide)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1599
- method `if(compares && this._zf != (repeat == 2))` — this._r[_CX]; — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1676
- method `if(subfunction != 0)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1802
- method `Cpu8086Exception($"unhandled DOS EXEC AL={subfunction:X2}h")` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1803
- method `if(!this._executables.TryGetValue(name, out var image))` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1805
- method `Cpu8086Exception($"unavailable EXEC target {name}")` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1806
- method `if(handle is 1 or 2)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1818
- method `for(var i = 0; i < count; ++i)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1819
- method `if(handle == 4)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1823
- method `if(this._files.TryGetValue(handle, out var open))` — A write lands AT the file position and advances it - it does not append. Appending is what — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1831
- method `while(bytes.Count < open.Position)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1833
- method `if(count == 0)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1835
- method `for(var i = 0; i < count; ++i, ++open.Position)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1838
- method `if(open.Position < bytes.Count)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1840
- method `if(!this._byName.TryGetValue(name, out var file))` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1853
- method `if(!this._byName.TryGetValue(name, out var file))` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1863
- method `if(!this._byName.Remove(from, out var file) || this._byName.ContainsKey…` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1903
- method `if(subfunction != 0)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1922
- method `Cpu8086Exception($"unhandled DOS IOCTL AL={subfunction:X2}h")` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1923
- method `if(handle <= 4)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1925
- method `if(this._videoMode == 0x13)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:1986
- method `for(var column = 0; column < _SCREEN_COLUMNS; ++column)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:2064
- method `while(this._cursorColumn % 8 != 0)` — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:2096
- class `Cpu8086Exception` — Something the interpreter will not guess at: an unimplemented opcode, an unhandled DOS call, a runa… — PowerBasic.Compiler.Tests/Exec/Cpu8086.cs:2166

### InterpreterSanityTests.cs  `C#, 97 lines`
- namespace `PowerBasic.Compiler.Tests.Exec` — PowerBasic.Compiler.Tests/Exec/InterpreterSanityTests.cs:4
- class `InterpreterSanityTests` — The interpreter checked against the ONE path already known to be right: the direct emitter, whose — PowerBasic.Compiler.Tests/Exec/InterpreterSanityTests.cs:13
- method `ASC(MKI$(REG(1)), 2)` — PowerBasic.Compiler.Tests/Exec/InterpreterSanityTests.cs:82

### SharedDivideTests.cs  `C#, 134 lines`
- namespace `PowerBasic.Compiler.Tests.Exec` — PowerBasic.Compiler.Tests/Exec/SharedDivideTests.cs:4
- class `SharedDivideTests` — O0079 in its separated form: q = n \ d and a LATER m = n MOD d share the one divide. — PowerBasic.Compiler.Tests/Exec/SharedDivideTests.cs:16

## PowerBasic.Compiler.Tests/Ir/

### AggregateBlockScalarizationTests.cs  `C#, 72 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/AggregateBlockScalarizationTests.cs:5
- class `AggregateBlockScalarizationTests` — PowerBasic.Compiler.Tests/Ir/AggregateBlockScalarizationTests.cs:7
- method `Change` — PowerBasic.Compiler.Tests/Ir/AggregateBlockScalarizationTests.cs:31

### AggregateZeroCostTests.cs  `C#, 338 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/AggregateZeroCostTests.cs:5
- class `AggregateZeroCostTests` — Zero-cost aggregate lowering invariants. — PowerBasic.Compiler.Tests/Ir/AggregateZeroCostTests.cs:16
- method `GenericProbe` — PowerBasic.Compiler.Tests/Ir/AggregateZeroCostTests.cs:234
- method `AliasProbe` — PowerBasic.Compiler.Tests/Ir/AggregateZeroCostTests.cs:263
- method `UnionProbe` — PowerBasic.Compiler.Tests/Ir/AggregateZeroCostTests.cs:287

### AliasAnalysisTests.cs  `C#, 101 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/AliasAnalysisTests.cs:3
- class `AliasAnalysisTests` — Basic width-aware alias analysis over IR memory locations. — PowerBasic.Compiler.Tests/Ir/AliasAnalysisTests.cs:7

### ArithmeticIdiomOptimizationTests.cs  `C#, 84 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/ArithmeticIdiomOptimizationTests.cs:3
- class `ArithmeticIdiomOptimizationTests` — O0337/O0338 — exact polynomial and reciprocal sequence rewrites. — PowerBasic.Compiler.Tests/Ir/ArithmeticIdiomOptimizationTests.cs:7

### ArrayLoweringTests.cs  `C#, 81 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/ArrayLoweringTests.cs:6
- class `ArrayLoweringTests` — Static array lowering: DIM allocation plus indexed load/store via byte GEPs. — PowerBasic.Compiler.Tests/Ir/ArrayLoweringTests.cs:10

### AsciizLoweringTests.cs  `C#, 112 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/AsciizLoweringTests.cs:6
- class `AsciizLoweringTests` — ASCIIZ * n on the IR path. — PowerBasic.Compiler.Tests/Ir/AsciizLoweringTests.cs:20
- method `LEN` — PowerBasic.Compiler.Tests/Ir/AsciizLoweringTests.cs:85
- method `LEN(z)` — PowerBasic.Compiler.Tests/Ir/AsciizLoweringTests.cs:104

### BitLoweringTests.cs  `C#, 158 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/BitLoweringTests.cs:6
- class `BitLoweringTests` — BIT(value, n) on the IR path - one shift and a mask where the direct emitter writes a loop. — PowerBasic.Compiler.Tests/Ir/BitLoweringTests.cs:19
- method `BIT(5, 0)` — PowerBasic.Compiler.Tests/Ir/BitLoweringTests.cs:37
- method `BIT(v, i)` — PowerBasic.Compiler.Tests/Ir/BitLoweringTests.cs:45
- method `BIT(v, 0)` — PowerBasic.Compiler.Tests/Ir/BitLoweringTests.cs:54
- method `BIT(v, 31)` — PowerBasic.Compiler.Tests/Ir/BitLoweringTests.cs:60
- method `BIT(v, 15)` — PowerBasic.Compiler.Tests/Ir/BitLoweringTests.cs:66
- field `source` — PowerBasic.Compiler.Tests/Ir/BitLoweringTests.cs:100
- method `BIT(v, n)` — PowerBasic.Compiler.Tests/Ir/BitLoweringTests.cs:105
- field `source` — PowerBasic.Compiler.Tests/Ir/BitLoweringTests.cs:119
- method `BIT(v, -1)` — PowerBasic.Compiler.Tests/Ir/BitLoweringTests.cs:122
- method `BIT(s, 2)` — PowerBasic.Compiler.Tests/Ir/BitLoweringTests.cs:143

### BoolCanonTests.cs  `C#, 49 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/BoolCanonTests.cs:5
- class `BoolCanonTests` — InstCombine boolean canonicalization: collapsing the "widen an i1 then compare to — PowerBasic.Compiler.Tests/Ir/BoolCanonTests.cs:12

### BooleanCompareCanonicalizationTests.cs  `C#, 102 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/BooleanCompareCanonicalizationTests.cs:3
- class `BooleanCompareCanonicalizationTests` — PowerBasic.Compiler.Tests/Ir/BooleanCompareCanonicalizationTests.cs:5

### BooleanConstantFoldingTests.cs  `C#, 52 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/BooleanConstantFoldingTests.cs:2
- class `BooleanConstantFoldingTests` — BASIC's TRUE is -1, and a comparison the optimizer decides at compile time has to be that — PowerBasic.Compiler.Tests/Ir/BooleanConstantFoldingTests.cs:14

### BoundsCheckLoweringTests.cs  `C#, 98 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/BoundsCheckLoweringTests.cs:6
- class `BoundsCheckLoweringTests` — $ERROR BOUNDS ON in the IR lowering: every subscript is compared against its dimension and — PowerBasic.Compiler.Tests/Ir/BoundsCheckLoweringTests.cs:18

### CBackendTests.cs  `C#, 197 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/CBackendTests.cs:6
- class `CBackendTests` — The retargeting proof: a program compiled through the IR to C, built by the host C compiler — PowerBasic.Compiler.Tests/Ir/CBackendTests.cs:23
- method `if(string.IsNullOrWhiteSpace(dir))` — PowerBasic.Compiler.Tests/Ir/CBackendTests.cs:41
- method `foreach(var extension in extensions)` — PowerBasic.Compiler.Tests/Ir/CBackendTests.cs:43
- method `if(File.Exists(candidate))` — PowerBasic.Compiler.Tests/Ir/CBackendTests.cs:45
- field `source` — PowerBasic.Compiler.Tests/Ir/CBackendTests.cs:132
- method `CVBYT(MKBYT$(200))` — PowerBasic.Compiler.Tests/Ir/CBackendTests.cs:134

### CEmitterQualityTests.cs  `C#, 116 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/CEmitterQualityTests.cs:5
- class `CEmitterQualityTests` — The C back end must read like hand-written code, not a literal transcription of the SSA form. — PowerBasic.Compiler.Tests/Ir/CEmitterQualityTests.cs:16

### CanonOperandTests.cs  `C#, 55 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/CanonOperandTests.cs:3
- class `CanonOperandTests` — InstCombine operand canonicalization: sub-to-add and constant-to-RHS for comparisons. — PowerBasic.Compiler.Tests/Ir/CanonOperandTests.cs:7

### CanonicalizeTests.cs  `C#, 84 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/CanonicalizeTests.cs:3
- class `CanonicalizeTests` — InstCombine canonicalization: double-NOT elimination and constant reassociation. — PowerBasic.Compiler.Tests/Ir/CanonicalizeTests.cs:7

### CastChainTests.cs  `C#, 64 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/CastChainTests.cs:3
- class `CastChainTests` — InstCombine cast-chain simplification. — PowerBasic.Compiler.Tests/Ir/CastChainTests.cs:7

### ConsoleCommandLoweringTests.cs  `C#, 61 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/ConsoleCommandLoweringTests.cs:4
- class `ConsoleCommandLoweringTests` — Two of the statements the lowering used to reject wholesale as "CommandStmt": LOCATE and — PowerBasic.Compiler.Tests/Ir/ConsoleCommandLoweringTests.cs:13

### CorrelatedValuePropTests.cs  `C#, 60 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/CorrelatedValuePropTests.cs:3
- class `CorrelatedValuePropTests` — Correlated value propagation: facts from if (x == C) flow into the guarded region. — PowerBasic.Compiler.Tests/Ir/CorrelatedValuePropTests.cs:7

### CseShapeTests.cs  `C#, 89 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/CseShapeTests.cs:5
- class `CseShapeTests` — The three CSE shapes the direct emitter needed separate machinery for (O0185 past a merge, O0186 — PowerBasic.Compiler.Tests/Ir/CseShapeTests.cs:20

### DataLayoutTransformsTests.cs  `C#, 275 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/DataLayoutTransformsTests.cs:3
- class `DataLayoutTransformsTests` — PowerBasic.Compiler.Tests/Ir/DataLayoutTransformsTests.cs:5

### DataReadLoweringTests.cs  `C#, 110 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/DataReadLoweringTests.cs:6
- class `DataReadLoweringTests` — DATA / READ / RESTORE lowering: every DATA item program-wide is packed into one — PowerBasic.Compiler.Tests/Ir/DataReadLoweringTests.cs:14

### DataRepresentationOptimizationTests.cs  `C#, 188 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/DataRepresentationOptimizationTests.cs:3
- class `DataRepresentationOptimizationTests` — O0331/O0332/O0333 — compact Boolean storage and lookup-table tradeoffs. — PowerBasic.Compiler.Tests/Ir/DataRepresentationOptimizationTests.cs:7

### DeadLoopEliminationTests.cs  `C#, 320 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/DeadLoopEliminationTests.cs:8
- class `DeadLoopEliminationTests` — Deleting a counted loop nobody can observe - and, just as much a part of the contract, NOT — PowerBasic.Compiler.Tests/Ir/DeadLoopEliminationTests.cs:22
- method `Recover` — PowerBasic.Compiler.Tests/Ir/DeadLoopEliminationTests.cs:40
- field `source` — PowerBasic.Compiler.Tests/Ir/DeadLoopEliminationTests.cs:207
- method `Walk(BYVAL n%)` — PowerBasic.Compiler.Tests/Ir/DeadLoopEliminationTests.cs:210
- field `source` — PowerBasic.Compiler.Tests/Ir/DeadLoopEliminationTests.cs:243
- method `Walk(BYVAL n%)` — PowerBasic.Compiler.Tests/Ir/DeadLoopEliminationTests.cs:246

### DeadStoreElimTests.cs  `C#, 91 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/DeadStoreElimTests.cs:5
- class `DeadStoreElimTests` — Intra-block dead-store elimination for memory (DeadStoreElim). — PowerBasic.Compiler.Tests/Ir/DeadStoreElimTests.cs:9

### DemandedBitsTests.cs  `C#, 86 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/DemandedBitsTests.cs:3
- class `DemandedBitsTests` — PowerBasic.Compiler.Tests/Ir/DemandedBitsTests.cs:5

### DynamicArrayLoweringTests.cs  `C#, 174 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/DynamicArrayLoweringTests.cs:6
- class `DynamicArrayLoweringTests` — Dynamic (REDIM'd) 1-D arrays: the array is a runtime-allocated buffer addressed — PowerBasic.Compiler.Tests/Ir/DynamicArrayLoweringTests.cs:15

### EmitterNeverThrowsTests.cs  `C#, 338 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/EmitterNeverThrowsTests.cs:2
- class `EmitterNeverThrowsTests` — The C and LLVM emitters must DECLINE what they cannot render, never THROW. — PowerBasic.Compiler.Tests/Ir/EmitterNeverThrowsTests.cs:33
- method `if(EmitFailure(file, stage) is { } e)` — PowerBasic.Compiler.Tests/Ir/EmitterNeverThrowsTests.cs:80
- method `if(Emit(file, stage).Code == 0)` — PowerBasic.Compiler.Tests/Ir/EmitterNeverThrowsTests.cs:102
- method `foreach(var (operandName, word, @double) in _operands)` — PowerBasic.Compiler.Tests/Ir/EmitterNeverThrowsTests.cs:218
- method `foreach(var dialect in _dialects)` — PowerBasic.Compiler.Tests/Ir/EmitterNeverThrowsTests.cs:221
- method `foreach(var stage in _stages)` — PowerBasic.Compiler.Tests/Ir/EmitterNeverThrowsTests.cs:222
- method `if(EmitFailure(path, stage, "--dialect", dialect) is { } e)` — PowerBasic.Compiler.Tests/Ir/EmitterNeverThrowsTests.cs:224
- method `foreach(var stage in _stages)` — PowerBasic.Compiler.Tests/Ir/EmitterNeverThrowsTests.cs:254
- method `if(code == 0)` — PowerBasic.Compiler.Tests/Ir/EmitterNeverThrowsTests.cs:256
- method `if(err.StartsWith("pbc:", StringComparison.Ordinal))` — PowerBasic.Compiler.Tests/Ir/EmitterNeverThrowsTests.cs:258
- method `if(code == 0)` — PowerBasic.Compiler.Tests/Ir/EmitterNeverThrowsTests.cs:321
- method `if(!err.Contains(names, StringComparison.Ordinal))` — PowerBasic.Compiler.Tests/Ir/EmitterNeverThrowsTests.cs:323

### EndStmtLoweringTests.cs  `C#, 35 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/EndStmtLoweringTests.cs:4
- class `EndStmtLoweringTests` — Lowering of the END statement (program termination). — PowerBasic.Compiler.Tests/Ir/EndStmtLoweringTests.cs:8

### ExitFarLoweringTests.cs  `C#, 144 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/ExitFarLoweringTests.cs:5
- class `ExitFarLoweringTests` — EXIT FAR in the IR lowering. — PowerBasic.Compiler.Tests/Ir/ExitFarLoweringTests.cs:24
- method `Leave()` — PowerBasic.Compiler.Tests/Ir/ExitFarLoweringTests.cs:101
- method `Arm()` — PowerBasic.Compiler.Tests/Ir/ExitFarLoweringTests.cs:130

### ExitSelectLoweringTests.cs  `C#, 62 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/ExitSelectLoweringTests.cs:4
- class `ExitSelectLoweringTests` — EXIT SELECT jumps to the end of the SELECT block. The lowering models it with the same exit — PowerBasic.Compiler.Tests/Ir/ExitSelectLoweringTests.cs:12

### FileIoLoweringTests.cs  `C#, 43 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/FileIoLoweringTests.cs:5
- class `FileIoLoweringTests` — Sequential file I/O lowering (OPEN/CLOSE/PRINT#/INPUT#) via the runtime-call ABI. — PowerBasic.Compiler.Tests/Ir/FileIoLoweringTests.cs:9

### FloatDemotionTests.cs  `C#, 116 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/FloatDemotionTests.cs:5
- class `FloatDemotionTests` — O0012 — float demotion. PowerBASIC types a bare variable name SINGLE, so DOS-era counters are — PowerBasic.Compiler.Tests/Ir/FloatDemotionTests.cs:17

### FloatForLoopTests.cs  `C#, 101 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/FloatForLoopTests.cs:4
- class `FloatForLoopTests` — FOR over a SINGLE/DOUBLE counter. The block structure is the integer loop's, with float — PowerBasic.Compiler.Tests/Ir/FloatForLoopTests.cs:17

### FloatToIntegerRoundingTests.cs  `C#, 190 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/FloatToIntegerRoundingTests.cs:4
- class `FloatToIntegerRoundingTests` — BASIC rounds a real on its way into an integer variable - n% = 2.7 is 3 - while a C — PowerBasic.Compiler.Tests/Ir/FloatToIntegerRoundingTests.cs:18
- method `FIX(s)` — PowerBasic.Compiler.Tests/Ir/FloatToIntegerRoundingTests.cs:54

### ForwardingBlockElisionTests.cs  `C#, 126 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/ForwardingBlockElisionTests.cs:3
- class `ForwardingBlockElisionTests` — PowerBasic.Compiler.Tests/Ir/ForwardingBlockElisionTests.cs:5

### FrameElisionTests.cs  `C#, 60 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/FrameElisionTests.cs:3
- class `FrameElisionTests` — PowerBasic.Compiler.Tests/Ir/FrameElisionTests.cs:5

### FunctionSummariesTests.cs  `C#, 155 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/FunctionSummariesTests.cs:3
- class `FunctionSummariesTests` — O0161 — per-procedure mod/ref summaries. Two bits, deliberately: a coarse fact computed correctly — PowerBasic.Compiler.Tests/Ir/FunctionSummariesTests.cs:11

### GepSimplifyTests.cs  `C#, 39 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/GepSimplifyTests.cs:5
- class `GepSimplifyTests` — GEP simplification (zero-offset elimination). — PowerBasic.Compiler.Tests/Ir/GepSimplifyTests.cs:9

### GlobalDceTests.cs  `C#, 62 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/GlobalDceTests.cs:3
- class `GlobalDceTests` — Module-level global dead-code elimination: unreferenced functions and global variables are removed — PowerBasic.Compiler.Tests/Ir/GlobalDceTests.cs:11

### GosubLoweringTests.cs  `C#, 95 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/GosubLoweringTests.cs:6
- class `GosubLoweringTests` — GOSUB / RETURN lowering: a fixed-depth return-id stack records the call site, and a — PowerBasic.Compiler.Tests/Ir/GosubLoweringTests.cs:14

### GotoLoweringTests.cs  `C#, 48 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/GotoLoweringTests.cs:5
- class `GotoLoweringTests` — GOTO / label lowering (arbitrary control flow over the alloca form). — PowerBasic.Compiler.Tests/Ir/GotoLoweringTests.cs:9

### GvnTests.cs  `C#, 126 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/GvnTests.cs:3
- class `GvnTests` — Global value numbering: redundant pure computations are replaced by a dominating equal. — PowerBasic.Compiler.Tests/Ir/GvnTests.cs:7

### IfConversionTests.cs  `C#, 107 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/IfConversionTests.cs:5
- class `IfConversionTests` — If-conversion: a simple diamond becomes a branchless select. — PowerBasic.Compiler.Tests/Ir/IfConversionTests.cs:9

### InlineAsmLoweringTests.cs  `C#, 109 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/InlineAsmLoweringTests.cs:5
- class `InlineAsmLoweringTests` — Inline assembly in the IR. — PowerBasic.Compiler.Tests/Ir/InlineAsmLoweringTests.cs:19

### InlinerErrorHandlerTests.cs  `C#, 63 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/InlinerErrorHandlerTests.cs:7
- class `InlinerErrorHandlerTests` — A function with an armed error handler is not duplicable, and the inliner has to know it. — PowerBasic.Compiler.Tests/Ir/InlinerErrorHandlerTests.cs:22

### InlinerNoInlineTests.cs  `C#, 131 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/InlinerNoInlineTests.cs:7
- class `InlinerNoInlineTests` — NOINLINE is a contract with the programmer - "this stays a real call" - and the IR pipeline — PowerBasic.Compiler.Tests/Ir/InlinerNoInlineTests.cs:20
- field `source` — PowerBasic.Compiler.Tests/Ir/InlinerNoInlineTests.cs:97
- method `Poke8(BYVAL v%)` — PowerBasic.Compiler.Tests/Ir/InlinerNoInlineTests.cs:98
- method `Poke8(BYVAL v%)` — PowerBasic.Compiler.Tests/Ir/InlinerNoInlineTests.cs:118

### InlinerSpeedTests.cs  `C#, 61 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/InlinerSpeedTests.cs:3
- class `InlinerSpeedTests` — PowerBasic.Compiler.Tests/Ir/InlinerSpeedTests.cs:5

### InlinerTests.cs  `C#, 99 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/InlinerTests.cs:5
- class `InlinerTests` — Function inlining of single-block callees. — PowerBasic.Compiler.Tests/Ir/InlinerTests.cs:9
- class `TestExtensions` — PowerBasic.Compiler.Tests/Ir/InlinerTests.cs:91

### InputLoweringTests.cs  `C#, 68 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/InputLoweringTests.cs:5
- class `InputLoweringTests` — Console INPUT lowering via the runtime-call ABI. — PowerBasic.Compiler.Tests/Ir/InputLoweringTests.cs:9

### IntegerRecoveryTests.cs  `C#, 54 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/IntegerRecoveryTests.cs:3
- class `IntegerRecoveryTests` — IntegerRecovery rewrites the floating-point form the front end emits for integral +/-/* back to — PowerBasic.Compiler.Tests/Ir/IntegerRecoveryTests.cs:12

### IntrinsicLoweringTests.cs  `C#, 114 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/IntrinsicLoweringTests.cs:5
- class `IntrinsicLoweringTests` — Lowering of the pure numeric intrinsics ABS and SGN (branchless, no runtime). — PowerBasic.Compiler.Tests/Ir/IntrinsicLoweringTests.cs:9

### IpConstantPropTests.cs  `C#, 181 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/IpConstantPropTests.cs:3
- class `IpConstantPropTests` — O0018 / O0159 — interprocedural constant propagation. The interesting cases are the ones it must — PowerBasic.Compiler.Tests/Ir/IpConstantPropTests.cs:11

### IrClonerTests.cs  `C#, 116 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/IrClonerTests.cs:2
- class `IrClonerTests` — The general block-cloning utility, including SSA back-edges (loop phis). — PowerBasic.Compiler.Tests/Ir/IrClonerTests.cs:6

### IrDominatorsTests.cs  `C#, 74 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/IrDominatorsTests.cs:2
- class `IrDominatorsTests` — Dominator tree and dominance frontiers over the IR CFG. — PowerBasic.Compiler.Tests/Ir/IrDominatorsTests.cs:6

### IrLoweringTests.cs  `C#, 151 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/IrLoweringTests.cs:4
- class `IrLoweringTests` — Bound-AST → IR lowering (alloca/load/store form). Every lowered function must — PowerBasic.Compiler.Tests/Ir/IrLoweringTests.cs:11

### IrModelTests.cs  `C#, 202 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/IrModelTests.cs:2
- class `IrModelTests` — The LLVM-style typed SSA IR data model: types, use-lists, operand rewiring and — PowerBasic.Compiler.Tests/Ir/IrModelTests.cs:10

### IrPassManagerTests.cs  `C#, 81 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/IrPassManagerTests.cs:5
- class `IrPassManagerTests` — The pass manager: the standard pipeline run to a verified fixpoint. — PowerBasic.Compiler.Tests/Ir/IrPassManagerTests.cs:9

### IrPassesTests.cs  `C#, 142 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/IrPassesTests.cs:3
- class `IrPassesTests` — Value-based middle-end passes over the IR: constant folding, instcombine, DCE. — PowerBasic.Compiler.Tests/Ir/IrPassesTests.cs:7

### IrPrinterTests.cs  `C#, 112 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/IrPrinterTests.cs:2
- class `IrPrinterTests` — The textual IR printer: deterministic, LLVM-like rendering used for inspection — PowerBasic.Compiler.Tests/Ir/IrPrinterTests.cs:9

### IrTypeSystemTests.cs  `C#, 213 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/IrTypeSystemTests.cs:4
- class `IrTypeSystemTests` — The two distinctions the BASIC family makes that LLVM's type system does not, and that the IR — PowerBasic.Compiler.Tests/Ir/IrTypeSystemTests.cs:15

### IrVerifierTests.cs  `C#, 211 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/IrVerifierTests.cs:2
- class `IrVerifierTests` — The IR verifier: structural, SSA-dominance and type well-formedness. — PowerBasic.Compiler.Tests/Ir/IrVerifierTests.cs:6

### LibraryAndMemoryIdiomTests.cs  `C#, 135 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/LibraryAndMemoryIdiomTests.cs:3
- class `LibraryAndMemoryIdiomTests` — O0330/O0339 — memory-loop recognition and constant-size specialization. — PowerBasic.Compiler.Tests/Ir/LibraryAndMemoryIdiomTests.cs:7
- method `IrArgument(IrType.Ptr, 0)` — PowerBasic.Compiler.Tests/Ir/LibraryAndMemoryIdiomTests.cs:67
- method `IrArgument(IrType.Ptr, 0)` — PowerBasic.Compiler.Tests/Ir/LibraryAndMemoryIdiomTests.cs:88

### LicmTests.cs  `C#, 153 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/LicmTests.cs:3
- class `LicmTests` — LICM: hoisting loop-invariant computations into the loop preheader. — PowerBasic.Compiler.Tests/Ir/LicmTests.cs:7

### LlvmEmitterTests.cs  `C#, 130 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/LlvmEmitterTests.cs:6
- class `LlvmEmitterTests` — The strict LLVM text emitter. The snapshot tests pin the spelling; the toolchain — PowerBasic.Compiler.Tests/Ir/LlvmEmitterTests.cs:14

### LocalizeGlobalsTests.cs  `C#, 99 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/LocalizeGlobalsTests.cs:3
- class `LocalizeGlobalsTests` — O0278 — global variable localization. The interesting condition is not "only one function uses — PowerBasic.Compiler.Tests/Ir/LocalizeGlobalsTests.cs:11

### LoopDependenceAnalysisTests.cs  `C#, 168 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/LoopDependenceAnalysisTests.cs:3
- class `LoopDependenceAnalysisTests` — PowerBasic.Compiler.Tests/Ir/LoopDependenceAnalysisTests.cs:5

### LoopInterchangeLoweringTests.cs  `C#, 45 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/LoopInterchangeLoweringTests.cs:5
- class `LoopInterchangeLoweringTests` — The hand-built O0122 fixtures keep dependence cases readable, but the pass has to match the CFG the — PowerBasic.Compiler.Tests/Ir/LoopInterchangeLoweringTests.cs:13

### LoopInterchangeTests.cs  `C#, 196 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/LoopInterchangeTests.cs:3
- class `LoopInterchangeTests` — PowerBasic.Compiler.Tests/Ir/LoopInterchangeTests.cs:5
- record `Fixture` — PowerBasic.Compiler.Tests/Ir/LoopInterchangeTests.cs:8
- method `AddTerm` — PowerBasic.Compiler.Tests/Ir/LoopInterchangeTests.cs:87

### LoopUnrollTests.cs  `C#, 211 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/LoopUnrollTests.cs:8
- class `LoopUnrollTests` — Full unrolling of a constant-trip counted loop, on the IR - the first optimization ported from the — PowerBasic.Compiler.Tests/Ir/LoopUnrollTests.cs:21

### LoopUnswitchTests.cs  `C#, 121 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/LoopUnswitchTests.cs:5
- class `LoopUnswitchTests` — O0114 — loop unswitching. A conditional inside a loop whose condition never changes is tested every — PowerBasic.Compiler.Tests/Ir/LoopUnswitchTests.cs:16

### MathIntrinsicTests.cs  `C#, 81 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/MathIntrinsicTests.cs:6
- class `MathIntrinsicTests` — Floating-point math intrinsics lowered to LLVM intrinsics (llc-optimizable, not opaque). — PowerBasic.Compiler.Tests/Ir/MathIntrinsicTests.cs:10

### Mem2RegAggregateSafetyTests.cs  `C#, 48 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/Mem2RegAggregateSafetyTests.cs:5
- class `Mem2RegAggregateSafetyTests` — Opaque-pointer safety requirements at the scalar/aggregate boundary of mem2reg. — PowerBasic.Compiler.Tests/Ir/Mem2RegAggregateSafetyTests.cs:9
- field `source` — Given a UNION whose differently-sized fields both use the backing pointer directly at offset zero. — PowerBasic.Compiler.Tests/Ir/Mem2RegAggregateSafetyTests.cs:15

### Mem2RegTests.cs  `C#, 85 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/Mem2RegTests.cs:5
- class `Mem2RegTests` — mem2reg: promotes alloca/load/store slots to SSA registers + phis. — PowerBasic.Compiler.Tests/Ir/Mem2RegTests.cs:9

### MinMaxLoweringTests.cs  `C#, 94 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/MinMaxLoweringTests.cs:5
- class `MinMaxLoweringTests` — MIN and MAX on the IR path, as a left fold of compare-and-select. — PowerBasic.Compiler.Tests/Ir/MinMaxLoweringTests.cs:20

### ModuleLoweringTests.cs  `C#, 224 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/ModuleLoweringTests.cs:6
- class `ModuleLoweringTests` — Whole-module lowering: main body plus user SUB/FUNCTION (BYVAL scalar params) and calls. — PowerBasic.Compiler.Tests/Ir/ModuleLoweringTests.cs:10

### O0350O0353MiddleEndTests.cs  `C#, 231 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/O0350O0353MiddleEndTests.cs:3
- class `O0350O0353MiddleEndTests` — Regression tests for the O0350-O0353 middle-end ports. — PowerBasic.Compiler.Tests/Ir/O0350O0353MiddleEndTests.cs:7
- method `IrArgument(IrType.Ptr, 0)` — PowerBasic.Compiler.Tests/Ir/O0350O0353MiddleEndTests.cs:164
- method `IrArgument(IrType.Ptr, 0)` — PowerBasic.Compiler.Tests/Ir/O0350O0353MiddleEndTests.cs:202

### O0354O0359MiddleEndTests.cs  `C#, 137 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/O0354O0359MiddleEndTests.cs:3
- class `O0354O0359MiddleEndTests` — Regression coverage for equality saturation and verified arithmetic lowering. — PowerBasic.Compiler.Tests/Ir/O0354O0359MiddleEndTests.cs:7

### OffsetInductionCongruenceTests.cs  `C#, 136 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/OffsetInductionCongruenceTests.cs:3
- class `OffsetInductionCongruenceTests` — PowerBasic.Compiler.Tests/Ir/OffsetInductionCongruenceTests.cs:5
- record `LoopFixture` — PowerBasic.Compiler.Tests/Ir/OffsetInductionCongruenceTests.cs:8
- method `new` — PowerBasic.Compiler.Tests/Ir/OffsetInductionCongruenceTests.cs:44

### OnErrorLoweringTests.cs  `C#, 212 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/OnErrorLoweringTests.cs:5
- class `OnErrorLoweringTests` — ON ERROR / RESUME in the IR lowering. — PowerBasic.Compiler.Tests/Ir/OnErrorLoweringTests.cs:23

### OnGotoLoweringTests.cs  `C#, 108 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/OnGotoLoweringTests.cs:5
- class `OnGotoLoweringTests` — ON ... GOTO lowering (computed jump via a switch) and constant-selector folding. — PowerBasic.Compiler.Tests/Ir/OnGotoLoweringTests.cs:9

### OptimizationPortingLedgerTests.cs  `C#, 100 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/OptimizationPortingLedgerTests.cs:3
- class `OptimizationPortingLedgerTests` — How much of the optimization catalogue can move to the IR, and how much of it already has. — PowerBasic.Compiler.Tests/Ir/OptimizationPortingLedgerTests.cs:20

### OverflowCheckLoweringTests.cs  `C#, 135 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/OverflowCheckLoweringTests.cs:5
- class `OverflowCheckLoweringTests` — $ERROR OVERFLOW ON in the IR lowering. The direct emitter reads the overflow flag straight — PowerBasic.Compiler.Tests/Ir/OverflowCheckLoweringTests.cs:17

### PeekPokeLoweringTests.cs  `C#, 119 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/PeekPokeLoweringTests.cs:6
- class `PeekPokeLoweringTests` — PEEK and POKE on the IR path. — PowerBasic.Compiler.Tests/Ir/PeekPokeLoweringTests.cs:20
- method `PEEK` — PowerBasic.Compiler.Tests/Ir/PeekPokeLoweringTests.cs:44
- field `source` — PowerBasic.Compiler.Tests/Ir/PeekPokeLoweringTests.cs:108
- method `PEEK` — PowerBasic.Compiler.Tests/Ir/PeekPokeLoweringTests.cs:110

### PeepholeTests.cs  `C#, 76 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/PeepholeTests.cs:3
- class `PeepholeTests` — Additional sound InstCombine peephole identities. — PowerBasic.Compiler.Tests/Ir/PeepholeTests.cs:7

### PhiBranchFoldingTests.cs  `C#, 122 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/PhiBranchFoldingTests.cs:3
- class `PhiBranchFoldingTests` — PowerBasic.Compiler.Tests/Ir/PhiBranchFoldingTests.cs:5

### PhiCongruenceTests.cs  `C#, 110 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/PhiCongruenceTests.cs:3
- class `PhiCongruenceTests` — O0111 — two loop-carried values that advance in lockstep are one value written twice. — PowerBasic.Compiler.Tests/Ir/PhiCongruenceTests.cs:14

### PipelineSoundnessTests.cs  `C#, 82 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/PipelineSoundnessTests.cs:5
- class `PipelineSoundnessTests` — A safety net for the whole middle-end: lower a spread of representative programs and — PowerBasic.Compiler.Tests/Ir/PipelineSoundnessTests.cs:14

### PortedMidEndOptimizationsTests.cs  `C#, 157 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/PortedMidEndOptimizationsTests.cs:5
- class `PortedMidEndOptimizationsTests` — Mid-end optimizations the IR pipeline already achieves, each verified rather than assumed. — PowerBasic.Compiler.Tests/Ir/PortedMidEndOptimizationsTests.cs:20

### PrintLoweringTests.cs  `C#, 86 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/PrintLoweringTests.cs:5
- class `PrintLoweringTests` — Numeric PRINT lowering via a runtime-call ABI (the computation is optimized; output is a runtime ca… — PowerBasic.Compiler.Tests/Ir/PrintLoweringTests.cs:9

### RadixIntrinsicLoweringTests.cs  `C#, 96 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/RadixIntrinsicLoweringTests.cs:4
- class `RadixIntrinsicLoweringTests` — HEX$, OCT$ and BIN$ in the IR lowering. — PowerBasic.Compiler.Tests/Ir/RadixIntrinsicLoweringTests.cs:20

### RandomFileIoLoweringTests.cs  `C#, 104 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/RandomFileIoLoweringTests.cs:6
- class `RandomFileIoLoweringTests` — Random / binary record I/O: OPEN ... FOR RANDOM/BINARY carries a record length, and — PowerBasic.Compiler.Tests/Ir/RandomFileIoLoweringTests.cs:14

### RangeCheckElimTests.cs  `C#, 208 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/RangeCheckElimTests.cs:6
- class `RangeCheckElimTests` — The IR range lattice and the trap elision it pays for. — PowerBasic.Compiler.Tests/Ir/RangeCheckElimTests.cs:21
- field `inRange` — the counter is a phi bounded below by its initial value and above by the loop's own test, which — PowerBasic.Compiler.Tests/Ir/RangeCheckElimTests.cs:48
- field `outOfRange` — one element too far, and nothing else changed. The check has to survive, and this is the case — PowerBasic.Compiler.Tests/Ir/RangeCheckElimTests.cs:62
- field `masked` — x AND 7 is in [0, 7] however unknown x is - the one-sided AND rule, and the only fact here that — PowerBasic.Compiler.Tests/Ir/RangeCheckElimTests.cs:76
- field `masked` — PowerBasic.Compiler.Tests/Ir/RangeCheckElimTests.cs:87
- field `joined` — k% is neither a constant nor a counter - it is the join of two arms, which is the case the — PowerBasic.Compiler.Tests/Ir/RangeCheckElimTests.cs:100
- field `bounded` — PowerBasic.Compiler.Tests/Ir/RangeCheckElimTests.cs:117
- field `unknown` — the same statement over a value the lattice knows nothing about. It is the pair that makes the — PowerBasic.Compiler.Tests/Ir/RangeCheckElimTests.cs:131
- field `bounded` — PowerBasic.Compiler.Tests/Ir/RangeCheckElimTests.cs:142
- field `nonZero` — PowerBasic.Compiler.Tests/Ir/RangeCheckElimTests.cs:157
- field `reachesZero` — PowerBasic.Compiler.Tests/Ir/RangeCheckElimTests.cs:168

### ReadOnlyGlobalsTests.cs  `C#, 129 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/ReadOnlyGlobalsTests.cs:3
- class `ReadOnlyGlobalsTests` — O0165 — read-only global propagation. As with the other interprocedural passes, the cases that — PowerBasic.Compiler.Tests/Ir/ReadOnlyGlobalsTests.cs:11

### ReassociateTests.cs  `C#, 105 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/ReassociateTests.cs:3
- class `ReassociateTests` — O0061 — reassociation. The pass is judged on what it EXPOSES, not on the tree it builds: a — PowerBasic.Compiler.Tests/Ir/ReassociateTests.cs:11

### RecurrenceClosedFormTests.cs  `C#, 132 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/RecurrenceClosedFormTests.cs:5
- class `RecurrenceClosedFormTests` — O0134 — closed forms for loop-carried recurrences. An accumulator that only adds a constant is — PowerBasic.Compiler.Tests/Ir/RecurrenceClosedFormTests.cs:17

### RedundantMemoryTests.cs  `C#, 122 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/RedundantMemoryTests.cs:5
- class `RedundantMemoryTests` — Intra-block load/store forwarding (RedundantMemory). — PowerBasic.Compiler.Tests/Ir/RedundantMemoryTests.cs:9

### RegInterruptLoweringTests.cs  `C#, 114 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/RegInterruptLoweringTests.cs:6
- class `RegInterruptLoweringTests` — REG and INTERRUPT on the IR path. — PowerBasic.Compiler.Tests/Ir/RegInterruptLoweringTests.cs:23
- method `REG` — PowerBasic.Compiler.Tests/Ir/RegInterruptLoweringTests.cs:42
- method `REG(i)` — PowerBasic.Compiler.Tests/Ir/RegInterruptLoweringTests.cs:64
- field `source` — PowerBasic.Compiler.Tests/Ir/RegInterruptLoweringTests.cs:103
- method `REG(1)` — PowerBasic.Compiler.Tests/Ir/RegInterruptLoweringTests.cs:106

### RemovedBlockUseListTests.cs  `C#, 147 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/RemovedBlockUseListTests.cs:8
- class `RemovedBlockUseListTests` — A value's use-list must name only readers that can actually run. — PowerBasic.Compiler.Tests/Ir/RemovedBlockUseListTests.cs:28
- method `Recover` — PowerBasic.Compiler.Tests/Ir/RemovedBlockUseListTests.cs:90
- method `if(fn.IsDeclaration)` — PowerBasic.Compiler.Tests/Ir/RemovedBlockUseListTests.cs:110

### ReproTests.cs  `C#, 41 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/ReproTests.cs:5
- class `InlineRegressionTests` — Regression tests for inlining interactions that previously produced invalid IR. — PowerBasic.Compiler.Tests/Ir/ReproTests.cs:9

### RuntimeStepForTests.cs  `C#, 83 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/RuntimeStepForTests.cs:5
- class `RuntimeStepForTests` — FOR loops with a runtime (non-constant) STEP. — PowerBasic.Compiler.Tests/Ir/RuntimeStepForTests.cs:9

### ScalarReplaceArraysTests.cs  `C#, 177 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/ScalarReplaceArraysTests.cs:8
- class `ScalarReplaceArraysTests` — O0182 — small local array scalar replacement, on the IR. — PowerBasic.Compiler.Tests/Ir/ScalarReplaceArraysTests.cs:23
- method `a(0 TO 3)` — PowerBasic.Compiler.Tests/Ir/ScalarReplaceArraysTests.cs:66
- method `a(0 TO 3)` — PowerBasic.Compiler.Tests/Ir/ScalarReplaceArraysTests.cs:131
- method `a(0 TO 49)` — PowerBasic.Compiler.Tests/Ir/ScalarReplaceArraysTests.cs:147

### SccpTests.cs  `C#, 91 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/SccpTests.cs:5
- class `SccpTests` — SCCP: conditional constant propagation with dead-branch elimination over the IR. — PowerBasic.Compiler.Tests/Ir/SccpTests.cs:9

### SegmentAndWidePeekTests.cs  `C#, 104 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/SegmentAndWidePeekTests.cs:6
- class `SegmentAndWidePeekTests` — The rest of the PEEK/POKE family, and the segment queries beside it. — PowerBasic.Compiler.Tests/Ir/SegmentAndWidePeekTests.cs:19
- method `PEEKI(100)` — PowerBasic.Compiler.Tests/Ir/SegmentAndWidePeekTests.cs:41
- method `PEEK(100)` — PowerBasic.Compiler.Tests/Ir/SegmentAndWidePeekTests.cs:42
- method `PEEKL(200)` — PowerBasic.Compiler.Tests/Ir/SegmentAndWidePeekTests.cs:52
- method `VARSEG(v)` — PowerBasic.Compiler.Tests/Ir/SegmentAndWidePeekTests.cs:61
- method `VARSEG(v)` — PowerBasic.Compiler.Tests/Ir/SegmentAndWidePeekTests.cs:68
- method `PEEKI(300)` — PowerBasic.Compiler.Tests/Ir/SegmentAndWidePeekTests.cs:99

### SelectLoweringTests.cs  `C#, 78 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/SelectLoweringTests.cs:5
- class `SelectLoweringTests` — SELECT CASE lowering: value/list/range/IS arms and CASE ELSE as a comparison chain. — PowerBasic.Compiler.Tests/Ir/SelectLoweringTests.cs:9

### ShiftMergeTests.cs  `C#, 63 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/ShiftMergeTests.cs:3
- class `ShiftMergeTests` — InstCombine shift-chain merging. — PowerBasic.Compiler.Tests/Ir/ShiftMergeTests.cs:7

### ShiftStatementLoweringTests.cs  `C#, 77 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/ShiftStatementLoweringTests.cs:4
- class `ShiftStatementLoweringTests` — SHIFT LEFT v, n / SHIFT RIGHT v, n - a shift written as a statement, updating the — PowerBasic.Compiler.Tests/Ir/ShiftStatementLoweringTests.cs:14

### SimplifyCfgFoldTests.cs  `C#, 61 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/SimplifyCfgFoldTests.cs:3
- class `SimplifyCfgFoldTests` — SimplifyCFG branch folding and unreachable-block removal. — PowerBasic.Compiler.Tests/Ir/SimplifyCfgFoldTests.cs:7

### SimplifyCfgTests.cs  `C#, 91 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/SimplifyCfgTests.cs:5
- class `SimplifyCfgTests` — SimplifyCFG: trivial-phi elimination and single-predecessor block merging. — PowerBasic.Compiler.Tests/Ir/SimplifyCfgTests.cs:9

### StackProbeLoweringTests.cs  `C#, 124 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/StackProbeLoweringTests.cs:6
- class `StackProbeLoweringTests` — $ERROR STACK ON on the IR path: every procedure entry probes for headroom and raises — PowerBasic.Compiler.Tests/Ir/StackProbeLoweringTests.cs:21
- method `Deep` — PowerBasic.Compiler.Tests/Ir/StackProbeLoweringTests.cs:53
- method `Down(n - 1)` — PowerBasic.Compiler.Tests/Ir/StackProbeLoweringTests.cs:68

### StaticDispatchOptimizationTests.cs  `C#, 152 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/StaticDispatchOptimizationTests.cs:3
- class `StaticDispatchOptimizationTests` — O0334/O0335/O0336 — constant-set search and byte-classification dispatch recovery. — PowerBasic.Compiler.Tests/Ir/StaticDispatchOptimizationTests.cs:7
- method `Visit` — PowerBasic.Compiler.Tests/Ir/StaticDispatchOptimizationTests.cs:137
- method `if(Visit(successor))` — PowerBasic.Compiler.Tests/Ir/StaticDispatchOptimizationTests.cs:144

### StrengthReductionTests.cs  `C#, 72 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/StrengthReductionTests.cs:3
- class `StrengthReductionTests` — InstCombine strength reduction: power-of-two multiply/divide/remainder become shifts and masks. — PowerBasic.Compiler.Tests/Ir/StrengthReductionTests.cs:7

### StringArrayLoweringTests.cs  `C#, 105 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/StringArrayLoweringTests.cs:6
- class `StringArrayLoweringTests` — String arrays: a DIM of string elements allocates a buffer of target-sized pointer — PowerBasic.Compiler.Tests/Ir/StringArrayLoweringTests.cs:14

### StringLoweringTests.cs  `C#, 194 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/StringLoweringTests.cs:6
- class `StringLoweringTests` — Basic string-variable support: assignment, concatenation, PRINT via the runtime-handle ABI. — PowerBasic.Compiler.Tests/Ir/StringLoweringTests.cs:10

### StringOwnershipTests.cs  `C#, 177 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/StringOwnershipTests.cs:6
- class `StringOwnershipTests` — Who owns a string handle, and therefore who is allowed to free it. — PowerBasic.Compiler.Tests/Ir/StringOwnershipTests.cs:23
- method `Touch(value$)` — PowerBasic.Compiler.Tests/Ir/StringOwnershipTests.cs:109
- method `Work(BYVAL input$)` — PowerBasic.Compiler.Tests/Ir/StringOwnershipTests.cs:135

### SwapLoweringTests.cs  `C#, 57 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/SwapLoweringTests.cs:5
- class `SwapLoweringTests` — SWAP statement lowering (exchange of equally typed lvalues). — PowerBasic.Compiler.Tests/Ir/SwapLoweringTests.cs:9

### SwitchFormationTests.cs  `C#, 243 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/SwitchFormationTests.cs:5
- class `SwitchFormationTests` — : putting a SELECT CASE back together out of the per-arm compare — PowerBasic.Compiler.Tests/Ir/SwitchFormationTests.cs:20

### UdtLoweringTests.cs  `C#, 173 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/UdtLoweringTests.cs:6
- class `UdtLoweringTests` — User-defined TYPE records: a UDT variable is a packed byte buffer, and member access — PowerBasic.Compiler.Tests/Ir/UdtLoweringTests.cs:13

### WriteSetEofLoweringTests.cs  `C#, 124 lines`
- namespace `PowerBasic.Compiler.Tests.Ir` — PowerBasic.Compiler.Tests/Ir/WriteSetEofLoweringTests.cs:6
- class `WriteSetEofLoweringTests` — WRITE and SETEOF on the IR path. — PowerBasic.Compiler.Tests/Ir/WriteSetEofLoweringTests.cs:24
- method `LOF(1)` — PowerBasic.Compiler.Tests/Ir/WriteSetEofLoweringTests.cs:82

## PowerBasic.Compiler.Tests/Numerics/

### Extended80Tests.cs  `C#, 389 lines`
- namespace `PowerBasic.Compiler.Tests.Numerics` — PowerBasic.Compiler.Tests/Numerics/Extended80Tests.cs:4
- class `Extended80Tests` — The 80-bit float, held to the standard it exists to meet. — PowerBasic.Compiler.Tests/Numerics/Extended80Tests.cs:18
- method `if(b != 0 && a % b == 0)` — PowerBasic.Compiler.Tests/Numerics/Extended80Tests.cs:382

## PowerBasic.Compiler.Tests/Runtime/

### RuntimeCpuStateTests.cs  `C#, 41 lines`
- namespace `PowerBasic.Compiler.Tests.Runtime` — PowerBasic.Compiler.Tests/Runtime/RuntimeCpuStateTests.cs:2
- class `RuntimeCpuStateTests` — PowerBasic.Compiler.Tests/Runtime/RuntimeCpuStateTests.cs:4

### RuntimePopcntTargetTests.cs  `C#, 25 lines`
- namespace `PowerBasic.Compiler.Tests.Runtime` — PowerBasic.Compiler.Tests/Runtime/RuntimePopcntTargetTests.cs:2
- class `RuntimePopcntTargetTests` — PowerBasic.Compiler.Tests/Runtime/RuntimePopcntTargetTests.cs:4

### RuntimeTargetFeatureTests.cs  `C#, 114 lines`
- namespace `PowerBasic.Compiler.Tests.Runtime` — PowerBasic.Compiler.Tests/Runtime/RuntimeTargetFeatureTests.cs:3
- class `RuntimeTargetFeatureTests` — PowerBasic.Compiler.Tests/Runtime/RuntimeTargetFeatureTests.cs:5

### RuntimeTargetPolicyTests.cs  `C#, 165 lines`
- namespace `PowerBasic.Compiler.Tests.Runtime` — PowerBasic.Compiler.Tests/Runtime/RuntimeTargetPolicyTests.cs:5
- class `RuntimeTargetPolicyTests` — PowerBasic.Compiler.Tests/Runtime/RuntimeTargetPolicyTests.cs:7

## PowerBasic.Compiler.Tests/Semantics/

### BinderCorpusTests.cs  `C#, 64 lines`
- namespace `PowerBasic.Compiler.Tests.Semantics` — PowerBasic.Compiler.Tests/Semantics/BinderCorpusTests.cs:3
- class `BinderCorpusTests` — Full-pipeline front-end gate: every PB-SvgaLibrary test suite must — PowerBasic.Compiler.Tests/Semantics/BinderCorpusTests.cs:11
- class `SvgaBuildDirProvider` — Mirrors the SVGA harness: SVGAENG.SUB = SVGA.SUB minus its $INCLUDE lines. — PowerBasic.Compiler.Tests/Semantics/BinderCorpusTests.cs:17
- method `TryReadSource` — PowerBasic.Compiler.Tests/Semantics/BinderCorpusTests.cs:19
- method `new(suite)` — PowerBasic.Compiler.Tests/Semantics/BinderCorpusTests.cs:38

### BinderTests.cs  `C#, 331 lines`
- namespace `PowerBasic.Compiler.Tests.Semantics` — PowerBasic.Compiler.Tests/Semantics/BinderTests.cs:4
- class `BinderTests` — PowerBasic.Compiler.Tests/Semantics/BinderTests.cs:6
- method `DefTypeStmt(_pos, BuiltinType.Integer, [('i', 'n')])` — PowerBasic.Compiler.Tests/Semantics/BinderTests.cs:35
- method `LabelStmt(_pos, "again")` — PowerBasic.Compiler.Tests/Semantics/BinderTests.cs:292
- method `EquateStmt(_pos, "A", Int(2))` — PowerBasic.Compiler.Tests/Semantics/BinderTests.cs:306

### BitFieldBinderTests.cs  `C#, 118 lines`
- namespace `PowerBasic.Compiler.Tests.Semantics` — PowerBasic.Compiler.Tests/Semantics/BitFieldBinderTests.cs:4
- class `BitFieldBinderTests` — pb36 bit-field members: Flags AS BIT * 3 packs sub-WORD fields into a hidden $bits — PowerBasic.Compiler.Tests/Semantics/BitFieldBinderTests.cs:12

### BitsIsNotAnIntrinsicTests.cs  `C#, 43 lines`
- namespace `PowerBasic.Compiler.Tests.Semantics` — PowerBasic.Compiler.Tests/Semantics/BitsIsNotAnIntrinsicTests.cs:3
- class `BitsIsNotAnIntrinsicTests` — BITS is not a PowerBASIC function, and this compiler no longer pretends it is. — PowerBasic.Compiler.Tests/Semantics/BitsIsNotAnIntrinsicTests.cs:19
- field `source` — PowerBasic.Compiler.Tests/Semantics/BitsIsNotAnIntrinsicTests.cs:37

### CommandArityTests.cs  `C#, 120 lines`
- namespace `PowerBasic.Compiler.Tests.Semantics` — PowerBasic.Compiler.Tests/Semantics/CommandArityTests.cs:3
- class `CommandArityTests` — How many arguments each command takes, enforced against what the genuine compilers accept. — PowerBasic.Compiler.Tests/Semantics/CommandArityTests.cs:24

### CommandsWithNoEffectTests.cs  `C#, 103 lines`
- namespace `PowerBasic.Compiler.Tests.Semantics` — PowerBasic.Compiler.Tests/Semantics/CommandsWithNoEffectTests.cs:3
- class `CommandsWithNoEffectTests` — The statements this runtime accepts and then does nothing with. — PowerBasic.Compiler.Tests/Semantics/CommandsWithNoEffectTests.cs:23

### ConstantFolderTests.cs  `C#, 96 lines`
- namespace `PowerBasic.Compiler.Tests.Semantics` — PowerBasic.Compiler.Tests/Semantics/ConstantFolderTests.cs:4
- class `ConstantFolderTests` — PowerBasic.Compiler.Tests/Semantics/ConstantFolderTests.cs:6

### CoroutineBinderTests.cs  `C#, 146 lines`
- namespace `PowerBasic.Compiler.Tests.Semantics` — PowerBasic.Compiler.Tests/Semantics/CoroutineBinderTests.cs:4
- class `CoroutineBinderTests` — Binding of PB 3.6 generators: a FUNCTION whose body contains YIELD is lowered to a — PowerBasic.Compiler.Tests/Semantics/CoroutineBinderTests.cs:12

### DialectWaveBinderTests.cs  `C#, 226 lines`
- namespace `PowerBasic.Compiler.Tests.Semantics` — PowerBasic.Compiler.Tests/Semantics/DialectWaveBinderTests.cs:4
- class `DialectWaveBinderTests` — Binder semantics of the dialect wave: new suffix typing, QUAD, pointers, ASCIIZ, BCD deferrals. — PowerBasic.Compiler.Tests/Semantics/DialectWaveBinderTests.cs:8

### GenericsBinderTests.cs  `C#, 120 lines`
- namespace `PowerBasic.Compiler.Tests.Semantics` — PowerBasic.Compiler.Tests/Semantics/GenericsBinderTests.cs:4
- class `GenericsBinderTests` — pb36 compile-time generics (monomorphization): a generic TYPE Name OF T is a template — PowerBasic.Compiler.Tests/Semantics/GenericsBinderTests.cs:12

### IntrinsicCensusTests.cs  `C#, 331 lines`
- namespace `PowerBasic.Compiler.Tests.Semantics` — PowerBasic.Compiler.Tests/Semantics/IntrinsicCensusTests.cs:5
- class `IntrinsicCensusTests` — Every built-in function, asked the same questions. — PowerBasic.Compiler.Tests/Semantics/IntrinsicCensusTests.cs:22
- record `Shape` — PowerBasic.Compiler.Tests/Semantics/IntrinsicCensusTests.cs:78
- method `if(RefusedForItsLengthAtEveryShape(intrinsic, endpoint))` — PowerBasic.Compiler.Tests/Semantics/IntrinsicCensusTests.cs:153
- method `Diagnose(body, Dialect.Pb36)` — PowerBasic.Compiler.Tests/Semantics/IntrinsicCensusTests.cs:201
- method `if(generator.Errors.Count == 0)` — PowerBasic.Compiler.Tests/Semantics/IntrinsicCensusTests.cs:219

### MacroStringValidatorTests.cs  `C#, 104 lines`
- namespace `PowerBasic.Compiler.Tests.Semantics` — PowerBasic.Compiler.Tests/Semantics/MacroStringValidatorTests.cs:3
- class `MacroStringValidatorTests` — The compile-time check on PLAY and DRAW strings. — PowerBasic.Compiler.Tests/Semantics/MacroStringValidatorTests.cs:17

### NullableBinderTests.cs  `C#, 140 lines`
- namespace `PowerBasic.Compiler.Tests.Semantics` — PowerBasic.Compiler.Tests/Semantics/NullableBinderTests.cs:4
- class `NullableBinderTests` — pb36 nullable types T?: a synthesized UDT with a Value field of T and an INTEGER — PowerBasic.Compiler.Tests/Semantics/NullableBinderTests.cs:14

### OptionBaseTests.cs  `C#, 65 lines`
- namespace `PowerBasic.Compiler.Tests.Semantics` — PowerBasic.Compiler.Tests/Semantics/OptionBaseTests.cs:5
- class `OptionBaseTests` — OPTION BASE 0|1 - the implicit lower bound of an array declared without one. — PowerBasic.Compiler.Tests/Semantics/OptionBaseTests.cs:16

### PbTypeTests.cs  `C#, 84 lines`
- namespace `PowerBasic.Compiler.Tests.Semantics` — PowerBasic.Compiler.Tests/Semantics/PbTypeTests.cs:2
- class `PbTypeTests` — PowerBasic.Compiler.Tests/Semantics/PbTypeTests.cs:4

### StaticAssertReflectionTests.cs  `C#, 143 lines`
- namespace `PowerBasic.Compiler.Tests.Semantics` — PowerBasic.Compiler.Tests/Semantics/StaticAssertReflectionTests.cs:4
- class `StaticAssertReflectionTests` — pb36 compile-time checking: $ASSERT cond [, "message"] is evaluated by the binder (emits no — PowerBasic.Compiler.Tests/Semantics/StaticAssertReflectionTests.cs:12

### TupleBinderTests.cs  `C#, 88 lines`
- namespace `PowerBasic.Compiler.Tests.Semantics` — PowerBasic.Compiler.Tests/Semantics/TupleBinderTests.cs:4
- class `TupleBinderTests` — pb36 tuples / multiple return values: a tuple type (T1, T2) is an anonymous UDT with fields — PowerBasic.Compiler.Tests/Semantics/TupleBinderTests.cs:11

### TypeAliasBinderTests.cs  `C#, 82 lines`
- namespace `PowerBasic.Compiler.Tests.Semantics` — PowerBasic.Compiler.Tests/Semantics/TypeAliasBinderTests.cs:3
- class `TypeAliasBinderTests` — pb36 type aliases: TYPE Handle AS DWORD (single line, no END TYPE) names an existing — PowerBasic.Compiler.Tests/Semantics/TypeAliasBinderTests.cs:11

### TypeAliasTests.cs  `C#, 82 lines`
- namespace `PowerBasic.Compiler.Tests.Semantics` — PowerBasic.Compiler.Tests/Semantics/TypeAliasTests.cs:3
- class `TypeAliasTests` — pb36 natural type-name aliases: alternative spellings of the existing types so the language reads — PowerBasic.Compiler.Tests/Semantics/TypeAliasTests.cs:13

### TypeLayoutBinderTests.cs  `C#, 109 lines`
- namespace `PowerBasic.Compiler.Tests.Semantics` — PowerBasic.Compiler.Tests/Semantics/TypeLayoutBinderTests.cs:3
- class `TypeLayoutBinderTests` — pb36 TYPE layout control: PACKED (the byte-packed default), ALIGN n (each field on an — PowerBasic.Compiler.Tests/Semantics/TypeLayoutBinderTests.cs:12

### TypeMemberBinderTests.cs  `C#, 220 lines`
- namespace `PowerBasic.Compiler.Tests.Semantics` — PowerBasic.Compiler.Tests/Semantics/TypeMemberBinderTests.cs:4
- class `TypeMemberBinderTests` — Binding of PB 3.6 TYPE members: each lifts to a procedure mangled with the type — PowerBasic.Compiler.Tests/Semantics/TypeMemberBinderTests.cs:12

### WideIntegerTests.cs  `C#, 88 lines`
- namespace `PowerBasic.Compiler.Tests.Semantics` — PowerBasic.Compiler.Tests/Semantics/WideIntegerTests.cs:4
- class `WideIntegerTests` — pb36 wide integer types INT128/256/512 and the unsigned UINT* forms: fixed-size — PowerBasic.Compiler.Tests/Semantics/WideIntegerTests.cs:14

## PowerBasic.Compiler.Tests/Syntax/

### DialectGateTests.cs  `C#, 614 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/DialectGateTests.cs:3
- class `DialectGateTests` — Dialect gating (--dialect pb20..pb35): every feature from the — PowerBasic.Compiler.Tests/Syntax/DialectGateTests.cs:11
- field `source` — PowerBasic.Compiler.Tests/Syntax/DialectGateTests.cs:111
- field `source` — PowerBasic.Compiler.Tests/Syntax/DialectGateTests.cs:207
- field `source` — PowerBasic.Compiler.Tests/Syntax/DialectGateTests.cs:218
- field `source` — PowerBasic.Compiler.Tests/Syntax/DialectGateTests.cs:234
- field `source` — PowerBasic.Compiler.Tests/Syntax/DialectGateTests.cs:241
- field `source` — PowerBasic.Compiler.Tests/Syntax/DialectGateTests.cs:248
- field `source` — PowerBasic.Compiler.Tests/Syntax/DialectGateTests.cs:262
- field `source` — PowerBasic.Compiler.Tests/Syntax/DialectGateTests.cs:269
- field `source` — PowerBasic.Compiler.Tests/Syntax/DialectGateTests.cs:276
- field `source` — PowerBasic.Compiler.Tests/Syntax/DialectGateTests.cs:283
- field `source` — PowerBasic.Compiler.Tests/Syntax/DialectGateTests.cs:290
- field `source` — PowerBasic.Compiler.Tests/Syntax/DialectGateTests.cs:324
- field `source` — PowerBasic.Compiler.Tests/Syntax/DialectGateTests.cs:331
- method `Cmp(BYVAL a AS LONG)` — PowerBasic.Compiler.Tests/Syntax/DialectGateTests.cs:332
- field `source` — PowerBasic.Compiler.Tests/Syntax/DialectGateTests.cs:341
- field `source` — PowerBasic.Compiler.Tests/Syntax/DialectGateTests.cs:374
- method `F(BYVAL a AS LONG)` — PowerBasic.Compiler.Tests/Syntax/DialectGateTests.cs:375
- field `source` — PowerBasic.Compiler.Tests/Syntax/DialectGateTests.cs:395
- field `source` — PowerBasic.Compiler.Tests/Syntax/DialectGateTests.cs:402
- field `source` — PowerBasic.Compiler.Tests/Syntax/DialectGateTests.cs:413
- class `InMemorySource` — PowerBasic.Compiler.Tests/Syntax/DialectGateTests.cs:418
- method `TryReadSource(string name, string? includedFrom, out string source, out string res…` — PowerBasic.Compiler.Tests/Syntax/DialectGateTests.cs:420
- field `shape` — PowerBasic.Compiler.Tests/Syntax/DialectGateTests.cs:432
- field `source` — Not AssertRejected: that one reads the Borland-side wording ("requires PowerBASIC"), and this — PowerBasic.Compiler.Tests/Syntax/DialectGateTests.cs:501

### InterpolatedStringTests.cs  `C#, 199 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/InterpolatedStringTests.cs:4
- class `InterpolatedStringTests` — Front-end coverage for the PB 3.6 interpolated string $"text {expr} {expr:fmt}": — PowerBasic.Compiler.Tests/Syntax/InterpolatedStringTests.cs:13
- method `Walk(child)` — PowerBasic.Compiler.Tests/Syntax/InterpolatedStringTests.cs:43
- method `Walk(a.Value)` — PowerBasic.Compiler.Tests/Syntax/InterpolatedStringTests.cs:47
- method `Walk(b.Left)` — PowerBasic.Compiler.Tests/Syntax/InterpolatedStringTests.cs:56
- method `Walk(b.Right)` — PowerBasic.Compiler.Tests/Syntax/InterpolatedStringTests.cs:57
- field `source` — PowerBasic.Compiler.Tests/Syntax/InterpolatedStringTests.cs:191

### InterpreterDialectTests.cs  `C#, 164 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/InterpreterDialectTests.cs:4
- class `InterpreterDialectTests` — Wave 1 scaffolding for the classic Microsoft BASIC interpreters - BASICA, — PowerBasic.Compiler.Tests/Syntax/InterpreterDialectTests.cs:14
- field `source` — PowerBasic.Compiler.Tests/Syntax/InterpreterDialectTests.cs:95
- field `source` — PowerBasic.Compiler.Tests/Syntax/InterpreterDialectTests.cs:107
- field `source` — PowerBasic.Compiler.Tests/Syntax/InterpreterDialectTests.cs:125
- field `source` — PowerBasic.Compiler.Tests/Syntax/InterpreterDialectTests.cs:141
- field `source` — PowerBasic.Compiler.Tests/Syntax/InterpreterDialectTests.cs:154

### InvalidSyntaxSurfaceTests.cs  `C#, 81 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/InvalidSyntaxSurfaceTests.cs:3
- class `InvalidSyntaxSurfaceTests` — Syntax that belongs to no dialect. This is deliberately separate from — PowerBasic.Compiler.Tests/Syntax/InvalidSyntaxSurfaceTests.cs:13
- record `InvalidForm` — Invalid in Bob Zale's lineage only. CALL DWORD is the case: DWORD is a TYPE keyword — PowerBasic.Compiler.Tests/Syntax/InvalidSyntaxSurfaceTests.cs:22

### LexerCorpusTests.cs  `C#, 41 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/LexerCorpusTests.cs:2
- class `LexerCorpusTests` — Smoke test against a real-world PowerBASIC 3.5 codebase (PB-SvgaLibrary). — PowerBasic.Compiler.Tests/Syntax/LexerCorpusTests.cs:9

### LexerTests.cs  `C#, 461 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/LexerTests.cs:2
- class `LexerTests` — PowerBasic.Compiler.Tests/Syntax/LexerTests.cs:4

### ParserCommandTests.cs  `C#, 242 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/ParserCommandTests.cs:4
- class `ParserCommandTests` — PowerBasic.Compiler.Tests/Syntax/ParserCommandTests.cs:6

### ParserControlFlowTests.cs  `C#, 489 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/ParserControlFlowTests.cs:4
- class `ParserControlFlowTests` — PowerBasic.Compiler.Tests/Syntax/ParserControlFlowTests.cs:6

### ParserCoroutineTests.cs  `C#, 73 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/ParserCoroutineTests.cs:4
- class `ParserCoroutineTests` — Front-end behavior of the PB 3.6 YIELD coroutine statement: it parses into a — PowerBasic.Compiler.Tests/Syntax/ParserCoroutineTests.cs:12

### ParserCorpusTests.cs  `C#, 94 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/ParserCorpusTests.cs:3
- class `ParserCorpusTests` — Acceptance gate: the parser must fully parse the real-world PB-SvgaLibrary corpus — PowerBasic.Compiler.Tests/Syntax/ParserCorpusTests.cs:10
- class `SvgaBuildDirProvider` — The SVGA harness synthesizes SVGAENG.SUB (= SVGA.SUB with its $INCLUDE lines stripped) — PowerBasic.Compiler.Tests/Syntax/ParserCorpusTests.cs:34
- method `TryReadSource` — PowerBasic.Compiler.Tests/Syntax/ParserCorpusTests.cs:36
- method `if(count == 0)` — PowerBasic.Compiler.Tests/Syntax/ParserCorpusTests.cs:62

### ParserDeclarationTests.cs  `C#, 755 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/ParserDeclarationTests.cs:4
- class `ParserDeclarationTests` — PowerBasic.Compiler.Tests/Syntax/ParserDeclarationTests.cs:6
- method `NamedTimers(8)` — PowerBasic.Compiler.Tests/Syntax/ParserDeclarationTests.cs:207
- method `slots` — PowerBasic.Compiler.Tests/Syntax/ParserDeclarationTests.cs:222
- method `Plot(BYVAL x AS WORD, SEG buffer AS ANY, paletteV() AS BYTE, c)` — PowerBasic.Compiler.Tests/Syntax/ParserDeclarationTests.cs:260
- method `FNMax(a, b)` — PowerBasic.Compiler.Tests/Syntax/ParserDeclarationTests.cs:578

### ParserExpressionTests.cs  `C#, 243 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/ParserExpressionTests.cs:4
- class `ParserExpressionTests` — PowerBasic.Compiler.Tests/Syntax/ParserExpressionTests.cs:6

### ParserIoTests.cs  `C#, 287 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/ParserIoTests.cs:5
- class `ParserIoTests` — PowerBasic.Compiler.Tests/Syntax/ParserIoTests.cs:7

### ParserRuntimeTargetMetaTests.cs  `C#, 38 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/ParserRuntimeTargetMetaTests.cs:2
- class `ParserRuntimeTargetMetaTests` — PowerBasic.Compiler.Tests/Syntax/ParserRuntimeTargetMetaTests.cs:4

### ParserTestHelper.cs  `C#, 36 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/ParserTestHelper.cs:3
- class `ParserTestHelper` — Shared shorthands for the parser test fixtures. — PowerBasic.Compiler.Tests/Syntax/ParserTestHelper.cs:7

### ParserTypeMemberTests.cs  `C#, 138 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/ParserTypeMemberTests.cs:4
- class `ParserTypeMemberTests` — Front-end of PB 3.6 TYPE members: a TYPE block parses SUB / FUNCTION / — PowerBasic.Compiler.Tests/Syntax/ParserTypeMemberTests.cs:11

### Pb35PdsStatementSurfaceTests.cs  `C#, 105 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/Pb35PdsStatementSurfaceTests.cs:4
- class `Pb35PdsStatementSurfaceTests` — The deliberate PB 3.5 versus BASIC PDS 7.1 boundary. The broad dialect census derives an answer — PowerBasic.Compiler.Tests/Syntax/Pb35PdsStatementSurfaceTests.cs:12
- class `Source` — PowerBasic.Compiler.Tests/Syntax/Pb35PdsStatementSurfaceTests.cs:17
- method `TryReadSource(string name, string? includedFrom, out string source, out string res…` — PowerBasic.Compiler.Tests/Syntax/Pb35PdsStatementSurfaceTests.cs:19
- field `source` — PowerBasic.Compiler.Tests/Syntax/Pb35PdsStatementSurfaceTests.cs:87
- field `source` — PowerBasic.Compiler.Tests/Syntax/Pb35PdsStatementSurfaceTests.cs:97

### PreprocessorCorpusTests.cs  `C#, 65 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/PreprocessorCorpusTests.cs:2
- class `PreprocessorCorpusTests` — Expands real-world entry points from PB-SvgaLibrary (umbrella include of the — PowerBasic.Compiler.Tests/Syntax/PreprocessorCorpusTests.cs:9
- class `SvgaBuildDirProvider` — The SVGA harness synthesizes SVGAENG.SUB (= SVGA.SUB with its $INCLUDE lines stripped) — PowerBasic.Compiler.Tests/Syntax/PreprocessorCorpusTests.cs:32
- method `TryReadSource` — PowerBasic.Compiler.Tests/Syntax/PreprocessorCorpusTests.cs:34

### PreprocessorTests.cs  `C#, 203 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/PreprocessorTests.cs:2
- class `PreprocessorTests` — PowerBasic.Compiler.Tests/Syntax/PreprocessorTests.cs:4
- class `FakeSources` — PowerBasic.Compiler.Tests/Syntax/PreprocessorTests.cs:7
- method `TryReadSource(string name, string? includedFrom, out string text, out string resol…` — PowerBasic.Compiler.Tests/Syntax/PreprocessorTests.cs:9

### QuirkEmulationTests.cs  `C#, 125 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/QuirkEmulationTests.cs:3
- class `QuirkEmulationTests` — Dialect-conditional bug emulation (docs/QUIRKS.md): compiling under an old — PowerBasic.Compiler.Tests/Syntax/QuirkEmulationTests.cs:11
- class `OneFile` — PowerBasic.Compiler.Tests/Syntax/QuirkEmulationTests.cs:90
- method `TryReadSource(string name, string? includedFrom, out string source, out string res…` — PowerBasic.Compiler.Tests/Syntax/QuirkEmulationTests.cs:92

### StatementNodeCoverageTests.cs  `C#, 178 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/StatementNodeCoverageTests.cs:6
- class `StatementNodeCoverageTests` — Whether the statement surface covers every KIND of statement, not merely every keyword. — PowerBasic.Compiler.Tests/Syntax/StatementNodeCoverageTests.cs:25
- method `if(node is Statement nested)` — PowerBasic.Compiler.Tests/Syntax/StatementNodeCoverageTests.cs:58
- method `foreach(var type in Walk(nestedBody))` — PowerBasic.Compiler.Tests/Syntax/StatementNodeCoverageTests.cs:67

### StatementSurface.cs  `C#, 921 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/StatementSurface.cs:2
- class `StatementSurface` — The statement surface, as data: one entry per spelling of every statement the parser dispatches, — PowerBasic.Compiler.Tests/Syntax/StatementSurface.cs:27
- record `Form` — A stable name for the form, used in failure messages and the census. — PowerBasic.Compiler.Tests/Syntax/StatementSurface.cs:47
- enum `PairAvailability` — The four possible answers in the explicit PB 3.5/PDS 7.1 statement audit. — PowerBasic.Compiler.Tests/Syntax/StatementSurface.cs:506
- method `Add` — PowerBasic.Compiler.Tests/Syntax/StatementSurface.cs:846
- method `NumberPhysicalLines` — PowerBasic.Compiler.Tests/Syntax/StatementSurface.cs:885

### StatementSurfaceCensusTests.cs  `C#, 195 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/StatementSurfaceCensusTests.cs:5
- class `StatementSurfaceCensusTests` — The statement surface against both code generators, measured rather than assumed. — PowerBasic.Compiler.Tests/Syntax/StatementSurfaceCensusTests.cs:20
- enum `Stage` — PowerBasic.Compiler.Tests/Syntax/StatementSurfaceCensusTests.cs:22
- record `Result` — PowerBasic.Compiler.Tests/Syntax/StatementSurfaceCensusTests.cs:24
- method `new(form, dialect, Stage.Parse, e.Message)` — PowerBasic.Compiler.Tests/Syntax/StatementSurfaceCensusTests.cs:35
- method `new(form, dialect, Stage.Bind, model.Errors[0].Message)` — PowerBasic.Compiler.Tests/Syntax/StatementSurfaceCensusTests.cs:38
- method `new(form, dialect, Stage.Direct, direct.Errors[0].Message)` — PowerBasic.Compiler.Tests/Syntax/StatementSurfaceCensusTests.cs:44
- method `new(form, dialect, Stage.Routed, routed.Errors[0].Message)` — PowerBasic.Compiler.Tests/Syntax/StatementSurfaceCensusTests.cs:53
- record `FrontEndResult` — Whether the FRONT END accepts a form under a dialect - which is the whole of the question "does — PowerBasic.Compiler.Tests/Syntax/StatementSurfaceCensusTests.cs:66
- method `new(true, false, null)` — PowerBasic.Compiler.Tests/Syntax/StatementSurfaceCensusTests.cs:73
- method `new(false, true, e.Message)` — PowerBasic.Compiler.Tests/Syntax/StatementSurfaceCensusTests.cs:76
- method `if(should && !accepted)` — PowerBasic.Compiler.Tests/Syntax/StatementSurfaceCensusTests.cs:159
- method `if(wrongfullyRejected <= 400)` — PowerBasic.Compiler.Tests/Syntax/StatementSurfaceCensusTests.cs:161
- method `if(wrongfullyAccepted <= 400)` — PowerBasic.Compiler.Tests/Syntax/StatementSurfaceCensusTests.cs:165
- method `if(rejectionCrashes <= 400)` — PowerBasic.Compiler.Tests/Syntax/StatementSurfaceCensusTests.cs:169

### StatementSurfaceCoverageTests.cs  `C#, 74 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/StatementSurfaceCoverageTests.cs:3
- class `StatementSurfaceCoverageTests` — Whether the statement surface really is the whole statement surface. — PowerBasic.Compiler.Tests/Syntax/StatementSurfaceCoverageTests.cs:15
- method `if(Words(line).FirstOrDefault() is { } opener)` — PowerBasic.Compiler.Tests/Syntax/StatementSurfaceCoverageTests.cs:40

### StatementSurfaceOracleMaterialTests.cs  `C#, 88 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/StatementSurfaceOracleMaterialTests.cs:3
- class `StatementSurfaceOracleMaterialTests` — Exports the same exhaustive statement-form matrix used by the in-process tests as isolated DOS — PowerBasic.Compiler.Tests/Syntax/StatementSurfaceOracleMaterialTests.cs:14
- method `foreach` — PowerBasic.Compiler.Tests/Syntax/StatementSurfaceOracleMaterialTests.cs:36
- method `foreach` — PowerBasic.Compiler.Tests/Syntax/StatementSurfaceOracleMaterialTests.cs:51
- method `DirectoryNotFoundException("could not locate PB-Compiler.slnx")` — PowerBasic.Compiler.Tests/Syntax/StatementSurfaceOracleMaterialTests.cs:85

### SuffixAndRadixTests.cs  `C#, 290 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/SuffixAndRadixTests.cs:3
- class `SuffixAndRadixTests` — Lexer behavior of the PB 3.x suffix system, radix rules and the '&amp;' concat token. — PowerBasic.Compiler.Tests/Syntax/SuffixAndRadixTests.cs:7

### VendorWaveParserTests.cs  `C#, 240 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/VendorWaveParserTests.cs:5
- class `VendorWaveParserTests` — Parser/binder coverage for the vendor-corpus wave: BIT statements, — PowerBasic.Compiler.Tests/Syntax/VendorWaveParserTests.cs:14

### ViewPrintRangeTests.cs  `C#, 52 lines`
- namespace `PowerBasic.Compiler.Tests.Syntax` — PowerBasic.Compiler.Tests/Syntax/ViewPrintRangeTests.cs:4
- class `ViewPrintRangeTests` — VIEW PRINT topline TO bottomline, whose TO the generic command parser used to read — PowerBasic.Compiler.Tests/Syntax/ViewPrintRangeTests.cs:20

## PowerBasic.Compiler/Asm/

### AsmRegisterEffect.cs  `C#, 68 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/AsmRegisterEffect.cs:1
- record `AsmRegisterEffect` — What one inline-assembly statement does to the integer register file, read out of the text by the — PowerBasic.Compiler/Asm/AsmRegisterEffect.cs:48

### AsmSymbol.cs  `C#, 42 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/AsmSymbol.cs:1
- enum `AsmSymbolKind` — What a non-register identifier in inline assembly resolved to. — PowerBasic.Compiler/Asm/AsmSymbol.cs:4
- struct `AsmSymbol` — The resolution result for an identifier inside an inline-assembly statement. — PowerBasic.Compiler/Asm/AsmSymbol.cs:14
- interface `IAsmSymbolResolver` — Maps identifiers found in inline-assembly statements (variables, named — PowerBasic.Compiler/Asm/AsmSymbol.cs:37

### Assembler.BitManipulation.cs  `C#, 34 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/Assembler.BitManipulation.cs:1
- class `Assembler` — Scalar bit-manipulation encoders that post-date the historical inline-assembler table. — PowerBasic.Compiler/Asm/Assembler.BitManipulation.cs:4

### Assembler.Bmi.cs  `C#, 131 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/Assembler.Bmi.cs:1
- class `Assembler` — 32-bit BMI1/BMI2 encoders used by target-aware inline assembly. — PowerBasic.Compiler/Asm/Assembler.Bmi.cs:4
- method `ArgumentException($"{register} must be a 32-bit general-purpose register.", parameterN…` — PowerBasic.Compiler/Asm/Assembler.Bmi.cs:14
- method `ArgumentException($"{memory} must be an unsized or dword memory operand.", parameterNa…` — PowerBasic.Compiler/Asm/Assembler.Bmi.cs:19

### Assembler.CpuState.cs  `C#, 41 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/Assembler.CpuState.cs:1
- class `Assembler` — PowerBasic.Compiler/Asm/Assembler.CpuState.cs:2

### Assembler.CryptoExtensions.cs  `C#, 71 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/Assembler.CryptoExtensions.cs:1
- class `Assembler` — Legacy XMM AES-NI and PCLMULQDQ encoders used by target-aware inline assembly. — PowerBasic.Compiler/Asm/Assembler.CryptoExtensions.cs:4
- method `ArgumentException($"{register} must be an XMM register.", parameterName)` — PowerBasic.Compiler/Asm/Assembler.CryptoExtensions.cs:49

### Assembler.EncodingSelect.cs  `C#, 205 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/Assembler.EncodingSelect.cs:1
- class `Assembler` — PowerBasic.Compiler/Asm/Assembler.EncodingSelect.cs:2
- method `if(!excludeIncDecCandidateAsKiller || !this.TryAddSubOne(later, out _, …` — PowerBasic.Compiler/Asm/Assembler.EncodingSelect.cs:147

### Assembler.Fpu.cs  `C#, 302 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/Assembler.Fpu.cs:1
- class `Assembler` — PowerBasic.Compiler/Asm/Assembler.Fpu.cs:2

### Assembler.ImmediateOverloads.cs  `C#, 33 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/Assembler.ImmediateOverloads.cs:1
- class `Assembler` — Explicit integer overloads keep generated code unambiguous when the literal zero could otherwise — PowerBasic.Compiler/Asm/Assembler.ImmediateOverloads.cs:8

### Assembler.Instructions.cs  `C#, 1122 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/Assembler.Instructions.cs:1
- class `Assembler` — PowerBasic.Compiler/Asm/Assembler.Instructions.cs:2

### Assembler.LoadForward.cs  `C#, 408 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/Assembler.LoadForward.cs:1
- class `Assembler` — PowerBasic.Compiler/Asm/Assembler.LoadForward.cs:2
- method `for` — PowerBasic.Compiler/Asm/Assembler.LoadForward.cs:85
- method `if(recs[j].Start != recs[j - 1].Start + recs[j - 1].Length)` — PowerBasic.Compiler/Asm/Assembler.LoadForward.cs:87
- method `if(labels.Contains(recs[j].Start))` — PowerBasic.Compiler/Asm/Assembler.LoadForward.cs:89
- method `if` — PowerBasic.Compiler/Asm/Assembler.LoadForward.cs:92
- method `if(replacement.Length > later.Length)` — PowerBasic.Compiler/Asm/Assembler.LoadForward.cs:102
- method `if(replacement.Length > 0)` — PowerBasic.Compiler/Asm/Assembler.LoadForward.cs:104
- method `if` — PowerBasic.Compiler/Asm/Assembler.LoadForward.cs:113
- method `if(later.MemWrite && MemMayAlias(recs[i], later))` — PowerBasic.Compiler/Asm/Assembler.LoadForward.cs:116
- method `if(length > 0)` — PowerBasic.Compiler/Asm/Assembler.LoadForward.cs:133
- method `for` — PowerBasic.Compiler/Asm/Assembler.LoadForward.cs:164
- method `if(later.Start != recs[j - 1].Start + recs[j - 1].Length)` — PowerBasic.Compiler/Asm/Assembler.LoadForward.cs:167
- method `if(labels.Contains(later.Start))` — PowerBasic.Compiler/Asm/Assembler.LoadForward.cs:169
- method `if(this.IsConditionalJump(later))` — PowerBasic.Compiler/Asm/Assembler.LoadForward.cs:171
- method `if` — PowerBasic.Compiler/Asm/Assembler.LoadForward.cs:173
- method `if(!later.MemWrite || !MemMayAlias(store, later))` — PowerBasic.Compiler/Asm/Assembler.LoadForward.cs:176
- method `if(this.IsPlainWordFrameStore(later) && SameFrameCell(store, later))` — Only an exact full-word plain MOV kills the old store. A byte store or an RMW operation — PowerBasic.Compiler/Asm/Assembler.LoadForward.cs:181
- method `if(prior.Start + prior.Length != cursor || labels.Contains(cursor))` — PowerBasic.Compiler/Asm/Assembler.LoadForward.cs:248
- method `if((prior.Writes & bit) != 0 && !prior.WritesFlags)` — PowerBasic.Compiler/Asm/Assembler.LoadForward.cs:250
- method `if(prior.WritesFlags)` — PowerBasic.Compiler/Asm/Assembler.LoadForward.cs:252
- method `if` — PowerBasic.Compiler/Asm/Assembler.LoadForward.cs:258

### Assembler.MemoryHelpers.cs  `C#, 28 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/Assembler.MemoryHelpers.cs:1
- class `Assembler` — PowerBasic.Compiler/Asm/Assembler.MemoryHelpers.cs:2

### Assembler.Peephole.cs  `C#, 232 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/Assembler.Peephole.cs:1
- class `Assembler` — PowerBasic.Compiler/Asm/Assembler.Peephole.cs:2
- enum `PeepKind` — PowerBasic.Compiler/Asm/Assembler.Peephole.cs:19
- record `PeepInstr` — A recorded instruction: its byte range, what it is, and (for register/memory MOVs) the modrm byte t… — PowerBasic.Compiler/Asm/Assembler.Peephole.cs:23
- method `if(a.Length > 2)` — PowerBasic.Compiler/Asm/Assembler.Peephole.cs:82
- method `if` — PowerBasic.Compiler/Asm/Assembler.Peephole.cs:86
- method `if` — PowerBasic.Compiler/Asm/Assembler.Peephole.cs:103
- method `if` — PowerBasic.Compiler/Asm/Assembler.Peephole.cs:114
- method `switch` — PowerBasic.Compiler/Asm/Assembler.Peephole.cs:117
- method `if(sched[i].Start >= end)` — PowerBasic.Compiler/Asm/Assembler.Peephole.cs:221
- method `if(peep[i].Start >= end)` — PowerBasic.Compiler/Asm/Assembler.Peephole.cs:227

### Assembler.Schedule.cs  `C#, 188 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/Assembler.Schedule.cs:2
- class `Assembler` — PowerBasic.Compiler/Asm/Assembler.Schedule.cs:4
- record `SchedInstr` — A recorded instruction's data dependencies: which word registers, flags, and memory it reads — PowerBasic.Compiler/Asm/Assembler.Schedule.cs:21
- method `if(order != null)` — PowerBasic.Compiler/Asm/Assembler.Schedule.cs:128
- method `MemMayAlias(a, b)` — PowerBasic.Compiler/Asm/Assembler.Schedule.cs:158

### Assembler.Simd.Ssse3Sse4.cs  `C#, 129 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/Assembler.Simd.Ssse3Sse4.cs:1
- class `Assembler` — SSSE3, SSE4.1 and SSE4.2 packed-integer/string encoders used by target-aware inline asm. — PowerBasic.Compiler/Asm/Assembler.Simd.Ssse3Sse4.cs:4

### Assembler.Simd.Targeting.cs  `C#, 95 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/Assembler.Simd.Targeting.cs:1
- class `Assembler` — PowerBasic.Compiler/Asm/Assembler.Simd.Targeting.cs:2

### Assembler.Simd.cs  `C#, 419 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/Assembler.Simd.cs:1
- class `Assembler` — MMX integer SIMD instructions (Pentium MMX, 1997 - contemporary with PB 3.5). — PowerBasic.Compiler/Asm/Assembler.Simd.cs:11

### Assembler.TailMerge.cs  `C#, 126 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/Assembler.TailMerge.cs:1
- class `Assembler` — PowerBasic.Compiler/Asm/Assembler.TailMerge.cs:2
- method `if(isInternal)` — PowerBasic.Compiler/Asm/Assembler.TailMerge.cs:64
- method `OnlyEntryReferencedFromOutside` — PowerBasic.Compiler/Asm/Assembler.TailMerge.cs:72
- method `if(f.Position >= start && f.Position < end)` — PowerBasic.Compiler/Asm/Assembler.TailMerge.cs:77
- method `if(f.Target.IsBound && f.Target.Position >= start && f.Target.Position …` — PowerBasic.Compiler/Asm/Assembler.TailMerge.cs:79
- method `if(OnlyEntryReferencedFromOutside(region))` — PowerBasic.Compiler/Asm/Assembler.TailMerge.cs:90
- method `ShortJumpsStillReach` — PowerBasic.Compiler/Asm/Assembler.TailMerge.cs:108
- method `foreach(var f in this._fixups)` — PowerBasic.Compiler/Asm/Assembler.TailMerge.cs:112
- method `if(f.Position >= start && f.Position < end)` — PowerBasic.Compiler/Asm/Assembler.TailMerge.cs:115
- method `if(target + f.Addend - (position + 1) is < sbyte.MinValue or > sbyte.Ma…` — PowerBasic.Compiler/Asm/Assembler.TailMerge.cs:118

### Assembler.cs  `C#, 757 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/Assembler.cs:2
- class `Assembler` — In-memory assembler for 16-bit real-mode x86 (8086..80386 subset, x87). — PowerBasic.Compiler/Asm/Assembler.cs:12
- enum `FixupKind` — Rel16Pair is the rel16 of the near JMP inside the 8086 spelling of a long conditional — PowerBasic.Compiler/Asm/Assembler.cs:21
- record `Fixup` — PowerBasic.Compiler/Asm/Assembler.cs:22
- method `InvalidOperationException($"Label {label} is already bound to offset {label.Position}.")` — PowerBasic.Compiler/Asm/Assembler.cs:68
- method `InvalidOperationException($"Label {label} is external and cannot be bound.")` — PowerBasic.Compiler/Asm/Assembler.cs:70
- method `InvalidOperationException($"Label {label} is already bound and cannot become external.")` — PowerBasic.Compiler/Asm/Assembler.cs:87
- method `InvalidOperationException($"Label {fixup.Target} was referenced but never bound.")` — PowerBasic.Compiler/Asm/Assembler.cs:120
- method `ApplyFixup` — PowerBasic.Compiler/Asm/Assembler.cs:121
- method `switch(fixup.Kind)` — PowerBasic.Compiler/Asm/Assembler.cs:146
- method `InvalidOperationException($"Short jump to external label {fixup.Target} is not linkable.")` — PowerBasic.Compiler/Asm/Assembler.cs:158
- method `if` — PowerBasic.Compiler/Asm/Assembler.cs:162
- method `InvalidOperationException($"Label {fixup.Target} was referenced but never bound.")` — PowerBasic.Compiler/Asm/Assembler.cs:164
- method `ApplyFixup` — PowerBasic.Compiler/Asm/Assembler.cs:165
- method `if(!jmpAt.TryGetValue(target.Position + addend, out var j) || j == i)` — PowerBasic.Compiler/Asm/Assembler.cs:233
- method `if(!next.Target.IsBound || (ReferenceEquals(next.Target, target) && nex…` — PowerBasic.Compiler/Asm/Assembler.cs:236
- method `if(f.Position < 1)` — PowerBasic.Compiler/Asm/Assembler.cs:276
- method `if(f.Kind == FixupKind.Rel16 && op == 0xE9)` — PowerBasic.Compiler/Asm/Assembler.cs:279
- method `if(f.Kind == FixupKind.Rel8 && op == 0xEB)` — PowerBasic.Compiler/Asm/Assembler.cs:281
- method `if(f.Target.IsBound && !f.Target.IsConstant)` — PowerBasic.Compiler/Asm/Assembler.cs:289
- method `if(label.IsBound && !label.IsConstant)` — PowerBasic.Compiler/Asm/Assembler.cs:292
- method `if(!targeted.Contains(start) && afterAJump.Contains(start))` — PowerBasic.Compiler/Asm/Assembler.cs:301
- method `if(f.Kind == FixupKind.Rel8 && f.Target.IsBound && !f.Target.IsExternal…` — a JMP to the very next instruction is a no-op: the arm-closing jump of an IF with no — PowerBasic.Compiler/Asm/Assembler.cs:365
- method `if(f.Kind == FixupKind.Rel16Pair && f.Target.IsBound && !f.Target.IsExt…` — the 8086 long-conditional pair folding back into the one short jump it stands in for — PowerBasic.Compiler/Asm/Assembler.cs:372
- method `if(pairRel is >= sbyte.MinValue and <= sbyte.MaxValue)` — PowerBasic.Compiler/Asm/Assembler.cs:377
- method `if(f.Kind != FixupKind.Rel16 || !f.Target.IsBound || f.Target.IsExterna…` — PowerBasic.Compiler/Asm/Assembler.cs:385
- method `if(!isJmp && !isJcc)` — PowerBasic.Compiler/Asm/Assembler.cs:390
- method `if(rel is < sbyte.MinValue or > sbyte.MaxValue)` — PowerBasic.Compiler/Asm/Assembler.cs:397
- method `if(rel is < sbyte.MinValue or > sbyte.MaxValue)` — PowerBasic.Compiler/Asm/Assembler.cs:412
- method `InvalidOperationException($"Unknown fixup kind {fixup.Kind}.")` — PowerBasic.Compiler/Asm/Assembler.cs:431
- method `ArgumentException($"{register} is not a general-purpose register.", parameterName)` — PowerBasic.Compiler/Asm/Assembler.cs:731
- method `ArgumentException($"{register} must be a 16- or 32-bit register.", parameterName)` — PowerBasic.Compiler/Asm/Assembler.cs:736
- method `ArgumentException($"Operand size mismatch: {first} vs {second}.")` — PowerBasic.Compiler/Asm/Assembler.cs:741
- method `ArgumentException($"Operand size mismatch: {register} vs {memory}.")` — PowerBasic.Compiler/Asm/Assembler.cs:750

### Condition.cs  `C#, 30 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/Condition.cs:1
- enum `Condition` — Condition codes for Jcc; the value is the low nibble of the opcode (0x70+cc / 0F 80+cc). — PowerBasic.Compiler/Asm/Condition.cs:4

### IX87InstructionSink.cs  `C#, 14 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/IX87InstructionSink.cs:1
- interface `IX87InstructionSink` — Optional replacement for native x87 encoding. An assembler configured with a sink delegates every — PowerBasic.Compiler/Asm/IX87InstructionSink.cs:8

### Imm.cs  `C#, 30 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/Imm.cs:1
- struct `Imm` — An immediate operand: a constant, the offset of a — PowerBasic.Compiler/Asm/Imm.cs:8

### Label.cs  `C#, 30 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/Label.cs:1
- class `Label` — A position inside the code buffer that may be referenced before it is — PowerBasic.Compiler/Asm/Label.cs:7

### Mem.cs  `C#, 122 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/Mem.cs:1
- struct `Mem` — A 16-bit real-mode memory operand: any legal combination of base — PowerBasic.Compiler/Asm/Mem.cs:8

### OperandSize.cs  `C#, 12 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/OperandSize.cs:1
- enum `OperandSize` — Size of an operand in bytes; means "not specified". — PowerBasic.Compiler/Asm/OperandSize.cs:4

### Reg.cs  `C#, 50 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/Reg.cs:1
- enum `Reg` — x86 registers usable in 16-bit real mode. The low nibble is the hardware — PowerBasic.Compiler/Asm/Reg.cs:7
- class `RegExtensions` — Classification and encoding helpers for . — PowerBasic.Compiler/Asm/Reg.cs:27

### RelocatableImage.cs  `C#, 28 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/RelocatableImage.cs:1
- enum `AsmRelocationKind` — How a recorded relocation site has to be treated by a linker. — PowerBasic.Compiler/Asm/RelocatableImage.cs:4
- record `AsmRelocation` — One linker-visible site inside a relocatable image; is set for external kinds. — PowerBasic.Compiler/Asm/RelocatableImage.cs:16
- record `RelocatableImage` — Result of : the image with all internal — PowerBasic.Compiler/Asm/RelocatableImage.cs:24

### St.cs  `C#, 26 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/St.cs:1
- record `St` — An x87 FPU stack register ST(0)..ST(7). — PowerBasic.Compiler/Asm/St.cs:4

### TextAssembler.Effects.cs  `C#, 374 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:1
- class `TextAssembler` — Reading one inline-assembly statement's REGISTER EFFECT out of its text (see — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:13
- class `LineParser` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:29
- method `Effect()` — Parses the statement for its effect only - nothing is emitted into the target. — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:33
- method `if(this.Current.Kind != TokenKind.Identifier)` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:40
- method `Describe(string mnemonic, List<Operand> operands, bool repeated, EffectBuilde…` — The per-mnemonic entry. Returns false for anything it does not model, which is the conservative — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:66
- method `switch` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:73
- method `if(operands.Count != 2)` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:145
- method `if(operands.Count != 2)` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:152
- method `if(operands.Count != 1)` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:159
- method `if(operands.Count != 1)` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:165
- method `if(operands.Count != 2)` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:173
- method `if(operands.Count != 2)` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:181
- method `if(operands.Count != 2)` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:189
- method `if(operands.Count != 1)` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:197
- method `if(operands.Count != 1)` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:204
- method `if(operands.Count != 2)` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:210
- method `if(operands.Count != 2)` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:218
- method `if(operands.Count != 1)` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:228
- method `if(operands.Count != 1)` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:241
- method `if(operands.Count != 2)` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:257
- method `if(operands.Count != 2)` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:264
- method `if(!TryGetCondition(mnemonic, out _) || operands.Count != 1)` — a conditional jump, or nothing this table models: INT, CALL, RET, the FPU, the SIMD families — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:273
- class `EffectBuilder` — Accumulates one statement's effect, canonicalizing every register to the word one the allocator — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:286
- method `Read` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:294
- method `Define(Reg register)` — Records a write: a definition always, and a kill only when the whole register goes. — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:301
- method `if(!register.IsByte())` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:306
- method `DefinePartial(Reg register)` — Records a write that may leave the old value in place - the DX of a byte-wide — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:314
- method `Read` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:318
- method `switch(operand)` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:320
- method `Write(Operand operand)` — A written operand: a register is defined, and a memory destination still READS its address. — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:331
- method `switch(operand)` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:332
- method `ReadWrite` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:341
- method `Build` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:346
- method `Address` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:349
- method `if(memory.Memory.Index is { } index)` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:353
- method `Tracked(Reg register)` — The word register a name contends for, or null for a class this back end never allocates. — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:362
- method `if(register.IsByte())` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:365
- method `if(register.IsDword())` — PowerBasic.Compiler/Asm/TextAssembler.Effects.cs:367

### TextAssembler.Virtualization.cs  `C#, 55 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/TextAssembler.Virtualization.cs:1
- class `TextAssembler` — PowerBasic.Compiler/Asm/TextAssembler.Virtualization.cs:2
- record `ParsedAsmOperand` — Typed operand form shared with target-specific inline-asm lowerers. — PowerBasic.Compiler/Asm/TextAssembler.Virtualization.cs:5
- record `ParsedAsmRegister` — PowerBasic.Compiler/Asm/TextAssembler.Virtualization.cs:6
- record `ParsedAsmSt` — PowerBasic.Compiler/Asm/TextAssembler.Virtualization.cs:7
- record `ParsedAsmImmediate` — PowerBasic.Compiler/Asm/TextAssembler.Virtualization.cs:8
- record `ParsedAsmMemory` — PowerBasic.Compiler/Asm/TextAssembler.Virtualization.cs:9
- record `ParsedAsmLabel` — PowerBasic.Compiler/Asm/TextAssembler.Virtualization.cs:10
- class `LineParser` — PowerBasic.Compiler/Asm/TextAssembler.Virtualization.cs:33
- method `ParseOperandsForVirtualization()` — PowerBasic.Compiler/Asm/TextAssembler.Virtualization.cs:35
- method `AsmSyntaxException("Internal operand parser lost its synthetic mnemonic.")` — PowerBasic.Compiler/Asm/TextAssembler.Virtualization.cs:37
- method `Unexpected(this.Current)` — PowerBasic.Compiler/Asm/TextAssembler.Virtualization.cs:41
- method `ConvertOperand` — PowerBasic.Compiler/Asm/TextAssembler.Virtualization.cs:44

### TextAssembler.cs  `C#, 1290 lines`
- namespace `PowerBasic.Compiler.Asm` — PowerBasic.Compiler/Asm/TextAssembler.cs:2
- class `TextAssembler` — Parses a single PowerBASIC inline-assembly statement (the text after — PowerBasic.Compiler/Asm/TextAssembler.cs:12
- class `AsmSyntaxException` — PowerBasic.Compiler/Asm/TextAssembler.cs:41
- record `Operand` — region operand model — PowerBasic.Compiler/Asm/TextAssembler.cs:45
- record `RegisterOperand` — PowerBasic.Compiler/Asm/TextAssembler.cs:47
- record `StOperand` — PowerBasic.Compiler/Asm/TextAssembler.cs:48
- record `ImmediateOperand` — PowerBasic.Compiler/Asm/TextAssembler.cs:49
- record `MemoryOperand` — PowerBasic.Compiler/Asm/TextAssembler.cs:50
- record `LabelOperand` — PowerBasic.Compiler/Asm/TextAssembler.cs:51
- class `LineParser` — endregion — PowerBasic.Compiler/Asm/TextAssembler.cs:54
- enum `TokenKind` — PowerBasic.Compiler/Asm/TextAssembler.cs:56
- record `Token` — PowerBasic.Compiler/Asm/TextAssembler.cs:58
- constructor `LineParser` — PowerBasic.Compiler/Asm/TextAssembler.cs:65
- method `Tokenize(string line)` — Splits one statement into tokens. Static because the register effect analysis — PowerBasic.Compiler/Asm/TextAssembler.cs:80
- method `if(char.IsWhiteSpace(c))` — PowerBasic.Compiler/Asm/TextAssembler.cs:89
- method `switch` — PowerBasic.Compiler/Asm/TextAssembler.cs:93
- method `if` — PowerBasic.Compiler/Asm/TextAssembler.cs:104
- method `if` — PowerBasic.Compiler/Asm/TextAssembler.cs:109
- method `while(i < line.Length && char.IsAsciiDigit(line[i]))` — PowerBasic.Compiler/Asm/TextAssembler.cs:112
- method `if(!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture…` — A 32-bit immediate is a bit pattern, not a signed value: 2147483648 is what the operand — PowerBasic.Compiler/Asm/TextAssembler.cs:119
- method `AsmSyntaxException($"Numeric literal '{text}' is out of range.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:121
- method `if` — PowerBasic.Compiler/Asm/TextAssembler.cs:126
- method `while(i < line.Length && (char.IsAsciiLetterOrDigit(line[i]) || line[i] ==…` — dotted QB-style variable names (BR.Char) are one identifier — PowerBasic.Compiler/Asm/TextAssembler.cs:130
- method `while(i < line.Length && line[i] is '%' or '&' or '!' or '#' or '?' or '$')` — BASIC type suffixes stay part of the operand name (Foff%, x??, d#) — PowerBasic.Compiler/Asm/TextAssembler.cs:135
- method `AsmSyntaxException` — PowerBasic.Compiler/Asm/TextAssembler.cs:141
- method `TokenizeRadixNumber` — PowerBasic.Compiler/Asm/TextAssembler.cs:148
- method `AsmSyntaxException("Dangling '&'.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:151
- method `if` — PowerBasic.Compiler/Asm/TextAssembler.cs:165
- method `AsmSyntaxException($"Number expected after '&{radixChar}'.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:167
- method `AsmSyntaxException($"Numeric literal '&{radixChar}{text}' is out of range.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:174
- method `if` — PowerBasic.Compiler/Asm/TextAssembler.cs:176
- method `AsmSyntaxException($"Numeric literal '&{radixChar}{text}' is out of range.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:178
- method `Peek(int offset = 1)` — PowerBasic.Compiler/Asm/TextAssembler.cs:189
- method `Next()` — PowerBasic.Compiler/Asm/TextAssembler.cs:190
- method `Expect` — PowerBasic.Compiler/Asm/TextAssembler.cs:191
- method `AsmSyntaxException($"Expected {what} but found '{this.Current.Text}'.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:194
- method `IsKeyword` — PowerBasic.Compiler/Asm/TextAssembler.cs:198
- method `Unexpected` — PowerBasic.Compiler/Asm/TextAssembler.cs:200
- method `Assemble` — endregion — PowerBasic.Compiler/Asm/TextAssembler.cs:204
- method `AsmSyntaxException("Empty statement.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:207
- method `AsmSyntaxException($"Mnemonic expected, found '{this.Current.Text}'.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:209
- method `Unexpected(this.Current)` — PowerBasic.Compiler/Asm/TextAssembler.cs:225
- method `RequireStringMnemonic` — PowerBasic.Compiler/Asm/TextAssembler.cs:227
- method `AsmSyntaxException("String instruction expected after REP prefix.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:230
- method `AsmSyntaxException($"'{mnemonic}' cannot take a REP prefix.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:234
- field `_REGISTERS` — region operand parsing — PowerBasic.Compiler/Asm/TextAssembler.cs:240
- field `_IMPLICIT_REGISTERS` — The registers a mnemonic uses without naming them. Everything here is architectural: a — PowerBasic.Compiler/Asm/TextAssembler.cs:249
- method `WordFormOf(Reg register)` — The 16-bit register a general-purpose name denotes (AH and EAX are both AX); null for anything else. — PowerBasic.Compiler/Asm/TextAssembler.cs:270
- field `_SIZE_KEYWORDS` — PowerBasic.Compiler/Asm/TextAssembler.cs:277
- method `ParseOperands` — PowerBasic.Compiler/Asm/TextAssembler.cs:286
- method `ParseOperand` — PowerBasic.Compiler/Asm/TextAssembler.cs:300
- method `ImmediateOperand(token.Value)` — PowerBasic.Compiler/Asm/TextAssembler.cs:306
- method `if(this.Current.Kind != TokenKind.Number)` — PowerBasic.Compiler/Asm/TextAssembler.cs:310
- method `AsmSyntaxException("Number expected after '-'.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:311
- method `Unexpected(token)` — PowerBasic.Compiler/Asm/TextAssembler.cs:322
- method `ParseIdentifierOperand` — PowerBasic.Compiler/Asm/TextAssembler.cs:325
- method `if` — PowerBasic.Compiler/Asm/TextAssembler.cs:328
- method `if(this.IsKeyword("PTR"))` — PowerBasic.Compiler/Asm/TextAssembler.cs:331
- method `if` — PowerBasic.Compiler/Asm/TextAssembler.cs:342
- method `RegisterOperand(register)` — PowerBasic.Compiler/Asm/TextAssembler.cs:350
- method `ImmediateOperand(symbol.Value)` — PowerBasic.Compiler/Asm/TextAssembler.cs:357
- method `LabelOperand(symbol.Label!)` — PowerBasic.Compiler/Asm/TextAssembler.cs:359
- method `if(this.Current.Kind == TokenKind.LBracket)` — PowerBasic.Compiler/Asm/TextAssembler.cs:361
- method `MemoryOperand` — PowerBasic.Compiler/Asm/TextAssembler.cs:363
- method `AsmSyntaxException($"Symbol '{name}' resolved to an unknown kind.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:366
- method `ParseSizedOperand` — PowerBasic.Compiler/Asm/TextAssembler.cs:369
- method `if` — PowerBasic.Compiler/Asm/TextAssembler.cs:374
- method `if(symbol.Kind != AsmSymbolKind.Memory)` — PowerBasic.Compiler/Asm/TextAssembler.cs:378
- method `AsmSyntaxException($"Symbol '{token.Text}' is not a memory operand.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:379
- method `if(this.Current.Kind == TokenKind.LBracket)` — PowerBasic.Compiler/Asm/TextAssembler.cs:382
- method `if(size != OperandSize.None)` — PowerBasic.Compiler/Asm/TextAssembler.cs:386
- method `if(segmentOverride is { } segment)` — PowerBasic.Compiler/Asm/TextAssembler.cs:388
- method `MemoryOperand` — PowerBasic.Compiler/Asm/TextAssembler.cs:390
- method `if` — PowerBasic.Compiler/Asm/TextAssembler.cs:393
- method `AsmSyntaxException("Duplicate operand size keyword.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:396
- method `if(this.IsKeyword("PTR"))` — PowerBasic.Compiler/Asm/TextAssembler.cs:399
- method `AsmSyntaxException` — PowerBasic.Compiler/Asm/TextAssembler.cs:404
- method `ParseSt` — PowerBasic.Compiler/Asm/TextAssembler.cs:407
- method `new(index)` — PowerBasic.Compiler/Asm/TextAssembler.cs:419
- method `ParseMemory` — PowerBasic.Compiler/Asm/TextAssembler.cs:421
- method `switch(token.Kind)` — PowerBasic.Compiler/Asm/TextAssembler.cs:435
- method `if(this.Current.Kind != TokenKind.Number)` — PowerBasic.Compiler/Asm/TextAssembler.cs:441
- method `AsmSyntaxException("Number expected after '-'.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:442
- method `if(_REGISTERS.TryGetValue(token.Text, out var register))` — PowerBasic.Compiler/Asm/TextAssembler.cs:454
- method `AddAddressRegister(register, ref @base, ref index)` — PowerBasic.Compiler/Asm/TextAssembler.cs:455
- method `switch(symbol.Kind)` — PowerBasic.Compiler/Asm/TextAssembler.cs:461
- method `if(symbol.Memory.Base is { } symbolBase)` — PowerBasic.Compiler/Asm/TextAssembler.cs:466
- method `AddAddressRegister(symbolBase, ref @base, ref index)` — PowerBasic.Compiler/Asm/TextAssembler.cs:467
- method `if(symbol.Memory.Index is { } symbolIndex)` — PowerBasic.Compiler/Asm/TextAssembler.cs:468
- method `AddAddressRegister(symbolIndex, ref @base, ref index)` — PowerBasic.Compiler/Asm/TextAssembler.cs:469
- method `if(symbol.Memory.Label is { } symbolLabel)` — PowerBasic.Compiler/Asm/TextAssembler.cs:470
- method `AsmSyntaxException("Only one label per memory operand.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:472
- method `AsmSyntaxException($"Symbol '{token.Text}' cannot be used inside a memory operand.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:481
- method `Unexpected(token)` — PowerBasic.Compiler/Asm/TextAssembler.cs:489
- method `AsmSyntaxException("Term expected after '+' in memory operand.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:495
- method `AsmSyntaxException("Empty memory operand.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:497
- method `if` — PowerBasic.Compiler/Asm/TextAssembler.cs:508
- method `new` — PowerBasic.Compiler/Asm/TextAssembler.cs:513
- method `AddAddressRegister` — PowerBasic.Compiler/Asm/TextAssembler.cs:516
- method `if(index is not null)` — PowerBasic.Compiler/Asm/TextAssembler.cs:526
- method `AsmSyntaxException($"{register} cannot address memory in 16-bit mode.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:532
- method `Resolve` — PowerBasic.Compiler/Asm/TextAssembler.cs:535
- method `AsmSyntaxException($"Unknown symbol '{name}'.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:538
- method `Dispatch` — region dispatch — PowerBasic.Compiler/Asm/TextAssembler.cs:546
- method `if(this.OneOperand() is RegisterOperand bswap)` — PowerBasic.Compiler/Asm/TextAssembler.cs:553
- method `AsmSyntaxException` — PowerBasic.Compiler/Asm/TextAssembler.cs:555
- method `if(TryGetCondition(mnemonic, out var condition))` — PowerBasic.Compiler/Asm/TextAssembler.cs:735
- method `if` — PowerBasic.Compiler/Asm/TextAssembler.cs:739
- method `AsmSyntaxException` — PowerBasic.Compiler/Asm/TextAssembler.cs:742
- method `TryGetCondition` — PowerBasic.Compiler/Asm/TextAssembler.cs:746
- method `NoOperands` — region instruction handlers — PowerBasic.Compiler/Asm/TextAssembler.cs:773
- method `AsmSyntaxException($"{mnemonic} takes no operands.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:776
- method `AsmSyntaxException($"Two operands expected, found {operands.Count}.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:782
- method `return` — PowerBasic.Compiler/Asm/TextAssembler.cs:783
- method `OneOperand` — PowerBasic.Compiler/Asm/TextAssembler.cs:786
- method `AsmSyntaxException($"One operand expected, found {operands.Count}.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:790
- method `BinaryMov` — PowerBasic.Compiler/Asm/TextAssembler.cs:794
- method `BinaryXchg` — PowerBasic.Compiler/Asm/TextAssembler.cs:808
- method `BinaryRegMem` — PowerBasic.Compiler/Asm/TextAssembler.cs:818
- method `AsmSyntaxException("Register, memory operands expected.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:822
- method `emit` — PowerBasic.Compiler/Asm/TextAssembler.cs:823
- method `BinaryCmov(Condition condition)` — CMOVcc dest, src/mem (686+ conditional move): dest = src when the condition holds — PowerBasic.Compiler/Asm/TextAssembler.cs:828
- method `AsmSyntaxException("CMOVcc takes a register destination.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:831
- method `BinaryExtend` — PowerBasic.Compiler/Asm/TextAssembler.cs:838
- method `AsmSyntaxException("Register destination expected.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:842
- method `switch` — PowerBasic.Compiler/Asm/TextAssembler.cs:843
- method `PackedBinary(byte opcode)` — packed binary op (0F op /r): destination MM0..MM7 (MMX) or XMM0..XMM7 (SSE2), source the same — PowerBasic.Compiler/Asm/TextAssembler.cs:853
- method `PackedShift(byte opcode, int subOp, Action<Reg, Reg> mmxByReg)` — packed shift (0F op /subOp): by a same-class register or an immediate count — PowerBasic.Compiler/Asm/TextAssembler.cs:865
- method `AsmSyntaxException("an MMX or XMM destination register is expected.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:868
- method `BinaryMovd` — PowerBasic.Compiler/Asm/TextAssembler.cs:875
- method `BinaryMovq` — PowerBasic.Compiler/Asm/TextAssembler.cs:890
- method `AsmSyntaxException($"Three operands expected, found {operands.Count}.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:904
- method `VexBinary(byte opcode)` — AVX/AVX-512 VEX/EVEX 3-operand packed op: dest = src1 OP src2 (XMM/YMM = VEX, ZMM = EVEX) — PowerBasic.Compiler/Asm/TextAssembler.cs:909
- method `AsmSyntaxException("an XMM/YMM/ZMM destination register is expected.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:912
- method `AsmSyntaxException("an XMM/YMM/ZMM first-source register is expected.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:914
- method `Vec(s2.Register)` — PowerBasic.Compiler/Asm/TextAssembler.cs:917
- method `Vec` — PowerBasic.Compiler/Asm/TextAssembler.cs:928
- method `VexMove` — PowerBasic.Compiler/Asm/TextAssembler.cs:930
- method `BinaryMovdqa` — PowerBasic.Compiler/Asm/TextAssembler.cs:943
- method `BinaryAlu` — PowerBasic.Compiler/Asm/TextAssembler.cs:953
- method `UnaryRegMem` — PowerBasic.Compiler/Asm/TextAssembler.cs:965
- method `Imul` — PowerBasic.Compiler/Asm/TextAssembler.cs:973
- method `switch(operands[0])` — PowerBasic.Compiler/Asm/TextAssembler.cs:978
- method `switch(operands[0], operands[1])` — PowerBasic.Compiler/Asm/TextAssembler.cs:984
- method `if(operands[2] is not ImmediateOperand immediate)` — PowerBasic.Compiler/Asm/TextAssembler.cs:991
- method `AsmSyntaxException("IMUL needs an immediate third operand.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:992
- method `switch` — PowerBasic.Compiler/Asm/TextAssembler.cs:993
- method `AsmSyntaxException("IMUL takes one, two or three operands.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:1000
- method `Shift` — PowerBasic.Compiler/Asm/TextAssembler.cs:1003
- method `Push` — PowerBasic.Compiler/Asm/TextAssembler.cs:1014
- method `Pop` — PowerBasic.Compiler/Asm/TextAssembler.cs:1024
- method `Jump` — PowerBasic.Compiler/Asm/TextAssembler.cs:1032
- method `if` — PowerBasic.Compiler/Asm/TextAssembler.cs:1039
- method `switch` — PowerBasic.Compiler/Asm/TextAssembler.cs:1045
- method `CallTarget` — PowerBasic.Compiler/Asm/TextAssembler.cs:1054
- method `Return` — PowerBasic.Compiler/Asm/TextAssembler.cs:1064
- method `RequireLabel` — PowerBasic.Compiler/Asm/TextAssembler.cs:1073
- method `AsmSyntaxException("Label target expected.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:1076
- method `Interrupt` — PowerBasic.Compiler/Asm/TextAssembler.cs:1080
- method `AsmSyntaxException("INT needs a vector 0..255.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:1083
- method `InPort` — PowerBasic.Compiler/Asm/TextAssembler.cs:1087
- method `AsmSyntaxException("IN needs AL/AX/EAX as destination.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:1091
- method `switch` — PowerBasic.Compiler/Asm/TextAssembler.cs:1092
- method `OutPort` — PowerBasic.Compiler/Asm/TextAssembler.cs:1099
- method `AsmSyntaxException("OUT needs AL/AX/EAX as source.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:1103
- method `switch` — PowerBasic.Compiler/Asm/TextAssembler.cs:1104
- method `SizedLike(Mem memory, Reg? partner, OperandSize fallback = OperandSize.None)` — Gives a memory operand the size of its register partner (or a default). — PowerBasic.Compiler/Asm/TextAssembler.cs:1117
- method `TryDispatchFpu` — region FPU dispatch — PowerBasic.Compiler/Asm/TextAssembler.cs:1131
- method `MemEmitter` — PowerBasic.Compiler/Asm/TextAssembler.cs:1216
- method `FpuLoadStore` — PowerBasic.Compiler/Asm/TextAssembler.cs:1218
- method `FpuMemoryOnly` — PowerBasic.Compiler/Asm/TextAssembler.cs:1226
- method `AsmSyntaxException("Memory operand expected.")` — PowerBasic.Compiler/Asm/TextAssembler.cs:1229
- method `memory` — PowerBasic.Compiler/Asm/TextAssembler.cs:1230
- method `FpuArithmetic` — PowerBasic.Compiler/Asm/TextAssembler.cs:1233
- method `FpuPop` — PowerBasic.Compiler/Asm/TextAssembler.cs:1242
- method `FpuCompare` — PowerBasic.Compiler/Asm/TextAssembler.cs:1252
- method `FpuStOrNothing` — PowerBasic.Compiler/Asm/TextAssembler.cs:1262
- method `FpuStOnly` — PowerBasic.Compiler/Asm/TextAssembler.cs:1271
- method `stack` — PowerBasic.Compiler/Asm/TextAssembler.cs:1275
- method `FpuStatusWord` — PowerBasic.Compiler/Asm/TextAssembler.cs:1278

## PowerBasic.Compiler/Backend/

### BackendInvariantException.cs  `C#, 43 lines`
- namespace `PowerBasic.Compiler.Backend` — PowerBasic.Compiler/Backend/BackendInvariantException.cs:1
- class `BackendInvariantException` — An internal-consistency violation inside the x86-16 back end: something the selector, the — PowerBasic.Compiler/Backend/BackendInvariantException.cs:25

### CopyCoalescer.cs  `C#, 205 lines`
- namespace `PowerBasic.Compiler.Backend` — PowerBasic.Compiler/Backend/CopyCoalescer.cs:1
- class `CopyCoalescer` — Merges a register-to-register MOV's two virtual registers into one, so the move disappears — PowerBasic.Compiler/Backend/CopyCoalescer.cs:50
- method `if(!IsPlainCopy(instr, out var destination, out var source))` — PowerBasic.Compiler/Backend/CopyCoalescer.cs:62
- method `if(destination.VirtualId == source.VirtualId || pinned.Contains(destina…` — PowerBasic.Compiler/Backend/CopyCoalescer.cs:64
- method `if(!CanMerge(function, liveness, destination.VirtualId, source.VirtualI…` — PowerBasic.Compiler/Backend/CopyCoalescer.cs:67
- method `if((written == a && after[index].Contains(b)) || (written == b && after…` — PowerBasic.Compiler/Backend/CopyCoalescer.cs:124
- method `Names(memory.Base, a, b)` — PowerBasic.Compiler/Backend/CopyCoalescer.cs:141
- method `if(IsPlainCopy(rewritten, out var destination, out var source) && desti…` — PowerBasic.Compiler/Backend/CopyCoalescer.cs:156
- method `return(index++, instr)` — PowerBasic.Compiler/Backend/CopyCoalescer.cs:202

### InstructionSelector.Dispatch.cs  `C#, 456 lines`
- namespace `PowerBasic.Compiler.Backend` — PowerBasic.Compiler/Backend/InstructionSelector.Dispatch.cs:3
- class `InstructionSelector` — Selection of an into a dispatch that is not a compare per case: an unsigned — PowerBasic.Compiler/Backend/InstructionSelector.Dispatch.cs:48
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [0, 1], ReadsFlags: false, WritesFlags: …` — PowerBasic.Compiler/Backend/InstructionSelector.Dispatch.cs:185
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [0], ReadsFlags: false, WritesFlags: true…` — PowerBasic.Compiler/Backend/InstructionSelector.Dispatch.cs:188
- method `MInstrEffect([0], [0], ReadsFlags: false, WritesFlags: true, ReadsMemory: false, …` — PowerBasic.Compiler/Backend/InstructionSelector.Dispatch.cs:238
- method `MInstrEffect([0], [0], ReadsFlags: true, WritesFlags: true, ReadsMemory: false, W…` — PowerBasic.Compiler/Backend/InstructionSelector.Dispatch.cs:241
- method `MInstrEffect([], [0, 1], ReadsFlags: false, WritesFlags: true, ReadsMemory: false…` — PowerBasic.Compiler/Backend/InstructionSelector.Dispatch.cs:245
- method `NewNode` — PowerBasic.Compiler/Backend/InstructionSelector.Dispatch.cs:334
- method `Build` — PowerBasic.Compiler/Backend/InstructionSelector.Dispatch.cs:340
- method `AddSuccessor(node, fallback)` — PowerBasic.Compiler/Backend/InstructionSelector.Dispatch.cs:353
- method `AddSuccessor(node, rightNode.Label)` — PowerBasic.Compiler/Backend/InstructionSelector.Dispatch.cs:361
- method `AddSuccessor(node, leftNode.Label)` — PowerBasic.Compiler/Backend/InstructionSelector.Dispatch.cs:363
- method `AddSuccessor(node, fallback)` — PowerBasic.Compiler/Backend/InstructionSelector.Dispatch.cs:366
- method `AddSuccessor(node, onlyLeft.Label)` — PowerBasic.Compiler/Backend/InstructionSelector.Dispatch.cs:368
- method `AddSuccessor(node, fallback)` — PowerBasic.Compiler/Backend/InstructionSelector.Dispatch.cs:371
- method `AddSuccessor(node, right.Label)` — PowerBasic.Compiler/Backend/InstructionSelector.Dispatch.cs:373
- method `if` — PowerBasic.Compiler/Backend/InstructionSelector.Dispatch.cs:375
- method `Build(left, low, middle - 1)` — PowerBasic.Compiler/Backend/InstructionSelector.Dispatch.cs:377
- method `Build(right, middle + 1, high)` — PowerBasic.Compiler/Backend/InstructionSelector.Dispatch.cs:379
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: tru…` — PowerBasic.Compiler/Backend/InstructionSelector.Dispatch.cs:409
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [0], ReadsFlags: false, WritesFlags: true…` — PowerBasic.Compiler/Backend/InstructionSelector.Dispatch.cs:415
- method `MInstrEffect([], [], ReadsFlags: true, WritesFlags: false, ReadsMemory: false, Wr…` — PowerBasic.Compiler/Backend/InstructionSelector.Dispatch.cs:420
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [0], ReadsFlags: false, WritesFlags: fals…` — PowerBasic.Compiler/Backend/InstructionSelector.Dispatch.cs:437

### InstructionSelector.Idioms.cs  `C#, 509 lines`
- namespace `PowerBasic.Compiler.Backend` — PowerBasic.Compiler/Backend/InstructionSelector.Idioms.cs:3
- class `InstructionSelector` — The selection patterns that span more than one IR instruction: shapes the optimizer has already — PowerBasic.Compiler/Backend/InstructionSelector.Idioms.cs:18
- method `switch(instr)` — PowerBasic.Compiler/Backend/InstructionSelector.Idioms.cs:80
- method `if(swap)` — PowerBasic.Compiler/Backend/InstructionSelector.Idioms.cs:83
- method `AbsShape(binary)` — PowerBasic.Compiler/Backend/InstructionSelector.Idioms.cs:86
- method `SgnShape(binary)` — PowerBasic.Compiler/Backend/InstructionSelector.Idioms.cs:90
- method `if(first.Op == second.Op || this._consumed.Contains(second) || first.Pa…` — PowerBasic.Compiler/Backend/InstructionSelector.Idioms.cs:123
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: fals…` — PowerBasic.Compiler/Backend/InstructionSelector.Idioms.cs:358
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [0], ReadsFlags: false, WritesFlags: true…` — PowerBasic.Compiler/Backend/InstructionSelector.Idioms.cs:361
- method `MInstrEffect([], [], ReadsFlags: false, WritesFlags: false, ReadsMemory: false, W…` — PowerBasic.Compiler/Backend/InstructionSelector.Idioms.cs:423
- method `MInstrEffect([], [], ReadsFlags: false, WritesFlags: false, ReadsMemory: false, W…` — PowerBasic.Compiler/Backend/InstructionSelector.Idioms.cs:493
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: tru…` — PowerBasic.Compiler/Backend/InstructionSelector.Idioms.cs:496
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [0, 1], ReadsFlags: true, WritesFlags: t…` — PowerBasic.Compiler/Backend/InstructionSelector.Idioms.cs:499

### InstructionSelector.cs  `C#, 4408 lines`
- namespace `PowerBasic.Compiler.Backend` — PowerBasic.Compiler/Backend/InstructionSelector.cs:3
- class `InstructionSelector` — Stage 2 of the x86-16 back end (docs/X86-BACKEND.md): selects the typed-SSA IR into the — PowerBasic.Compiler/Backend/InstructionSelector.cs:16
- method `if(phi.Type.IsFloat)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:167
- method `if(IsWide(phi.Type))` — edge copies below are FLD/FSTP through it — PowerBasic.Compiler/Backend/InstructionSelector.cs:170
- method `if(instr is IrPhi)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:188
- method `if(ReferenceEquals(instr, block.Terminator))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:190
- method `if(ReferenceEquals(instr, folded))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:195
- method `if(this._consumed.Contains(instr))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:197
- method `if(!this.SelectInstruction(instr, mblock))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:199
- method `if(IsWide(phi.Type) && phi.IncomingBlocks.Any(predecessor => dominators…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:263
- method `IsNativeExpression` — PowerBasic.Compiler/Backend/InstructionSelector.cs:266
- method `if(phi.Operands.Any(value => !IsNativeExpression(value)))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:279
- method `foreach(var phi in block.Phis)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:304
- method `if(phi.Type.IsFloat)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:306
- method `if(IsWide(phi.Type))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:315
- method `if(this._vregs[phi].Size == MRegSize.Dword)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:316
- method `if(!this.TryOperandPair(value, out var lowSource, out var highSource))` — both halves of a 32-bit phi are copied on the edge, low then high — PowerBasic.Compiler/Backend/InstructionSelector.cs:323
- method `if(!this.TryOperand(value, out var source))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:329
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: source is MOperand.Register ? [1] : [], …` — PowerBasic.Compiler/Backend/InstructionSelector.cs:342
- method `if(!stillNeeded)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:398
- method `if(!this.TryOperand(cmp.Lhs, out var lhs) || !this.TryOperand(cmp.Rhs, …` — PowerBasic.Compiler/Backend/InstructionSelector.cs:468
- method `if(lhs is not MOperand.Register)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:470
- method `if(rhs is MOperand.Register)` — CMP wants a register on the left, and a constant there is not a dead end: comparing the — PowerBasic.Compiler/Backend/InstructionSelector.cs:474
- method `MInstrEffect(WrittenRegs: [], ReadRegs: RegReadIndices(lhs, rhs), ReadsFlags: fal…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:489
- method `if(!this.TryOperand(valued.Condition, out var condition))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:515
- method `if(condition is not MOperand.Register)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:517
- method `if(!this.TryOperand(indirect.Address, out var address))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:533
- method `if(address is not MOperand.Register)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:535
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [0], ReadsFlags: false, WritesFlags: fals…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:538
- method `foreach(var target in indirect.Targets)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:540
- method `AddSuccessor(this._current, target.Label)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:541
- method `new(unchecked((sbyte)value))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:675
- method `IsQuad(load.Type)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:731
- method `IsQuad(store.Value.Type)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:733
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: rhs is MOperand.Register ? [0, 1] : [0],…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:815
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [0, 1], ReadsFlags: false, WritesFlags: …` — PowerBasic.Compiler/Backend/InstructionSelector.cs:853
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [0, 1], ReadsFlags: false, WritesFlags: …` — PowerBasic.Compiler/Backend/InstructionSelector.cs:913
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [0, 1], ReadsFlags: true, WritesFlags: t…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:916
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: fals…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:995
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [1], ReadsFlags: false, WritesFlags: fals…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:998
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: true,…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1008
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: fals…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1055
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: tru…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1058
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [1], ReadsFlags: false, WritesFlags: fals…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1061
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: fals…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1089
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: fals…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1093
- method `MInstrEffect` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1101
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: tru…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1105
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [1], ReadsFlags: false, WritesFlags: fals…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1109
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [1], ReadsFlags: false, WritesFlags: fals…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1113
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [0], ReadsFlags: false, WritesFlags: true…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1169
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: source is MOperand.Register ? [0, 1] : […` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1305
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: tru…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1405
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: true, WritesFlags: true…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1408
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: true,…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1432
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: true,…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1490
- method `MInstrEffect([], [], ReadsFlags: false, WritesFlags: false, ReadsMemory: false, W…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1546
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [0], ReadsFlags: false, WritesFlags: true…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1549
- method `Capture` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1559
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: true, WritesFlags: true, …` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1678
- class `AsmNameKinds` — Answers the effect analysis' questions about identifiers the same way MachineEmitter's own — PowerBasic.Compiler/Backend/InstructionSelector.cs:1722
- method `TryResolve` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1725
- method `IndexOf` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1732
- method `if(names[i].Equals(name, StringComparison.OrdinalIgnoreCase))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1735
- method `if(this.PointerMemory(store.Pointer, MRegSize.Dword) is not { } cell)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1746
- method `MInstrEffect([], native is MOperand.Register ? [1] : [], false, false, false, Wri…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1749
- method `MInstrEffect(WrittenRegs: [], ReadRegs: value is MOperand.Register ? [1] : [], Re…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1774
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [0, 1], ReadsFlags: false, WritesFlags: …` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1861
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [0, 1], ReadsFlags: false, WritesFlags: …` — PowerBasic.Compiler/Backend/InstructionSelector.cs:1888
- method `MInstrEffect(WrittenRegs: [], ReadRegs: RegReadIndices(left, right), ReadsFlags: …` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2013
- method `MInstrEffect(WrittenRegs: [], ReadRegs: RegReadIndices(lhs, rhs), ReadsFlags: fal…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2047
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: fals…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2068
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [0], ReadsFlags: false, WritesFlags: true…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2071
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [0], ReadsFlags: false, WritesFlags: true…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2161
- method `MInstrEffect([], [], ReadsFlags: true, WritesFlags: false, ReadsMemory: false, Wr…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2167
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [0], ReadsFlags: false, WritesFlags: true…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2221
- method `MInstrEffect([], [], ReadsFlags: true, WritesFlags: false, ReadsMemory: false, Wr…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2226
- method `if(!this.TryOperand(cast.Value, out var truth))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2261
- method `if(IsWide(to))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2264
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: tru…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2268
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: tru…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2278
- method `IsWide(to)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2283
- method `if(cast.Op == IrCastOp.ZExt)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2288
- method `if(!this.TryFloatOperand(cast.Value, out var source))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2307
- method `if(!this.TryOperandPair(cast.Value, out var low, out var high))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2321
- method `if(!this.TryFloatOperand(cast.Value, out var source))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2335
- method `if(!this.TryQwordSlot(cast.Value, out var source))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2345
- method `IsQuad(to)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2351
- method `IsQuad(to)` — An INTEGER straight into a QUAD, which `q = q * 3 + n%` asks for. There are arms for 16->32 — PowerBasic.Compiler/Backend/InstructionSelector.cs:2357
- method `if(!this.TryOperand(cast.Value, out var source))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2365
- method `if(source is MOperand.Register word)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2367
- method `if(!IsWide(to))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2386
- method `if(cast.Value is IrBlockAddress blockAddress)` — CODEPTR of a label: a point in this function's own code, which is the one address no — PowerBasic.Compiler/Backend/InstructionSelector.cs:2412
- method `if(cast.Value is IrGlobalVariable global)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2420
- method `if(!this.TryOperand(cast.Value, out var pointer))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2430
- method `if(pointer is not MOperand.Register held)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2432
- method `if(!this.TryOperand(cast.Value, out var word))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2440
- method `if(word is not MOperand.Register number)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2442
- method `IsWide(from)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2447
- method `if(lo is not MOperand.Register low)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2450
- method `IsQuad(to)` — ...and when the i64 is a value the program KEEPS - a FIX cell is a scaled int64, so the — PowerBasic.Compiler/Backend/InstructionSelector.cs:2465
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: true,…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2582
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: tru…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2590
- method `MInstrEffect(WrittenRegs: [], ReadRegs: source is MOperand.Register ? [1] : [], R…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2690
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: fals…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2710
- method `if(call.Args.FirstOrDefault() is not IrBlockAddress unwind)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2719
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: false…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2734
- method `if(call.Args.FirstOrDefault() is not IrBlockAddress handler)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2739
- method `if(call.Args.ToList() is not [IrBlockAddress start, IrBlockAddress next…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2760
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: false…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2774
- method `if(!arg.Type.IsIeeeFloat)` — the print routines take a float on ST(0) and pop it themselves — PowerBasic.Compiler/Backend/InstructionSelector.cs:2853
- method `if(!this.TryFloatOperand(arg, out var loaded))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2855
- method `if(arg is not IrGlobalVariable global)` — the address of the data object, not its contents - a string literal the codegen pools — PowerBasic.Compiler/Backend/InstructionSelector.cs:2862
- method `if(!this.TryRuntimePointer(arg, callee.Name, out var source, out var se…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2871
- method `if(arg is not IrConstantInt { Type: { IsInteger: true, Bits: 1 }, Value…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2883
- method `if(!this.TryWordOperand(arg, $"{callee.Name} takes a 32-bit value in a …` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2887
- method `if(!IsWide(arg.Type))` — the row claims the high half does not matter; see ArgKind.LowWord for what backs the claim — PowerBasic.Compiler/Backend/InstructionSelector.cs:2896
- method `if(!this.TryOperandPair(arg, out var low, out _))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2898
- method `if(!IsWide(arg.Type))` — four words into one qword cell - the value's own two, then two zeroes - and FILD it. The — PowerBasic.Compiler/Backend/InstructionSelector.cs:2908
- method `if(!this.TryOperandPair(arg, out var low, out var high))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2910
- method `if(IsQuad(arg.Type) && this._qslots.TryGetValue(arg, out var loaded))` — A QUAD read out of storage is already in a qword cell of its own (SelectQwordLoad), so — PowerBasic.Compiler/Backend/InstructionSelector.cs:2926
- method `if(arg is not IrConstantInt { Type: { IsInteger: true, Bits: 64 }, Valu…` — The machine IR does not yet carry a general four-register i64 value. An optimized QUAD — PowerBasic.Compiler/Backend/InstructionSelector.cs:2933
- method `if(IsWide(arg.Type))` — the word into the low register, the high one cleared - "XOR DX,DX" in the direct emitter — PowerBasic.Compiler/Backend/InstructionSelector.cs:2945
- method `if(!this.TryOperand(arg, out var word))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2947
- method `if(!IsWide(arg.Type))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2959
- method `if(!this.TryOperandPair(arg, out var lo, out var hi))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2961
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: true,…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:2994
- method `MInstrEffect([], [], ReadsFlags: false, WritesFlags: false, ReadsMemory: false, W…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:3162
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: true,…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:3236
- method `MInstrEffect(WrittenRegs: [], ReadRegs: handle is MOperand.Register ? [1] : [], R…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:3291
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: true,…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:3301
- method `return(c.Value, c.Value)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:3436
- method `IsWide(bin.Type)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:3442
- method `if(bin.Op == IrBinaryOp.And)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:3447
- method `MaskedRange(lhs, rhs)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:3448
- method `if(lhs is not { } left || rhs is not { } right)` — PowerBasic.Compiler/Backend/InstructionSelector.cs:3449
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: true,…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:3549
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: true,…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:3765
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: true,…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:3796
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: tru…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:3887
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: tru…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:3912
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: fals…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:3985
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [.. Enumerable.Range(0, registers.Length)…` — PowerBasic.Compiler/Backend/InstructionSelector.cs:4127
- method `IsAddressableGlobal(g)` — A global's VALUE is its ADDRESS - MOV reg, OFFSET name - which is what DataOffset is. The — PowerBasic.Compiler/Backend/InstructionSelector.cs:4245
- method `if(this._vregs.TryGetValue(value, out var reg))` — PowerBasic.Compiler/Backend/InstructionSelector.cs:4252

### LateLoadStoreOptimization.cs  `C#, 188 lines`
- namespace `PowerBasic.Compiler.Backend` — PowerBasic.Compiler/Backend/LateLoadStoreOptimization.cs:2
- class `LateLoadStoreOptimization` — Local stack-slot forwarding after spilling and allocation. This is intentionally conservative: — PowerBasic.Compiler/Backend/LateLoadStoreOptimization.cs:11
- method `if` — PowerBasic.Compiler/Backend/LateLoadStoreOptimization.cs:33
- method `if(known.TryGetValue(key, out var value))` — PowerBasic.Compiler/Backend/LateLoadStoreOptimization.cs:36
- method `if(value.Source is MOperand.Register { Reg: var held } && destination i…` — PowerBasic.Compiler/Backend/LateLoadStoreOptimization.cs:38
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: value.Source is MOperand.Register ? [1] …` — PowerBasic.Compiler/Backend/LateLoadStoreOptimization.cs:43
- method `MarkOverlappingReads(known, key)` — A differently-sized access to the same spill slot is a real memory read. It cannot be — PowerBasic.Compiler/Backend/LateLoadStoreOptimization.cs:51
- method `InvalidateOverlaps(known, key, keepExact: true)` — PowerBasic.Compiler/Backend/LateLoadStoreOptimization.cs:55
- method `if(known.TryGetValue(key, out var previous) && SameValue(previous.Sourc…` — PowerBasic.Compiler/Backend/LateLoadStoreOptimization.cs:56
- method `if(known.TryGetValue(key, out previous) && !previous.ReadSinceStore && …` — PowerBasic.Compiler/Backend/LateLoadStoreOptimization.cs:60
- method `foreach(var slot in original.Operands.OfType<MOperand.StackSlot>())` — PowerBasic.Compiler/Backend/LateLoadStoreOptimization.cs:68
- method `if` — PowerBasic.Compiler/Backend/LateLoadStoreOptimization.cs:70
- method `if(original.Effect.WritesMemory)` — PowerBasic.Compiler/Backend/LateLoadStoreOptimization.cs:73
- method `if` — PowerBasic.Compiler/Backend/LateLoadStoreOptimization.cs:76
- method `if` — PowerBasic.Compiler/Backend/LateLoadStoreOptimization.cs:79
- method `InvalidateWrittenRegisters(emitted, known, allocation)` — PowerBasic.Compiler/Backend/LateLoadStoreOptimization.cs:81
- record `SlotKey` — PowerBasic.Compiler/Backend/LateLoadStoreOptimization.cs:164
- method `Of(MOperand.StackSlot slot)` — PowerBasic.Compiler/Backend/LateLoadStoreOptimization.cs:166
- method `Overlaps` — PowerBasic.Compiler/Backend/LateLoadStoreOptimization.cs:167
- method `Bytes` — PowerBasic.Compiler/Backend/LateLoadStoreOptimization.cs:175
- record `KnownValue` — PowerBasic.Compiler/Backend/LateLoadStoreOptimization.cs:185

### LinearScanAllocator.AsmFlow.cs  `C#, 272 lines`
- namespace `PowerBasic.Compiler.Backend` — PowerBasic.Compiler/Backend/LinearScanAllocator.AsmFlow.cs:2
- class `LinearScanAllocator` — The half of allocation that belongs to somebody else's registers: the ones an inline-assembly — PowerBasic.Compiler/Backend/LinearScanAllocator.AsmFlow.cs:9
- method `foreach(var register in facts[i].Destroys)` — PowerBasic.Compiler/Backend/LinearScanAllocator.AsmFlow.cs:89
- method `foreach(var successor in blocks[b].Successors)` — PowerBasic.Compiler/Backend/LinearScanAllocator.AsmFlow.cs:139
- method `for` — PowerBasic.Compiler/Backend/LinearScanAllocator.AsmFlow.cs:144
- method `foreach(var target in fact.JumpsTo)` — PowerBasic.Compiler/Backend/LinearScanAllocator.AsmFlow.cs:147
- method `if(fact.IsAsm)` — PowerBasic.Compiler/Backend/LinearScanAllocator.AsmFlow.cs:155
- method `if` — PowerBasic.Compiler/Backend/LinearScanAllocator.AsmFlow.cs:166
- method `if(blockOf.TryGetValue(successor, out var s))` — PowerBasic.Compiler/Backend/LinearScanAllocator.AsmFlow.cs:197
- method `foreach(var predecessor in predecessors[b])` — PowerBasic.Compiler/Backend/LinearScanAllocator.AsmFlow.cs:205
- method `for` — PowerBasic.Compiler/Backend/LinearScanAllocator.AsmFlow.cs:207
- method `if(fact.IsAsm)` — PowerBasic.Compiler/Backend/LinearScanAllocator.AsmFlow.cs:211
- method `foreach(var target in fact.JumpsTo)` — PowerBasic.Compiler/Backend/LinearScanAllocator.AsmFlow.cs:215
- method `if` — PowerBasic.Compiler/Backend/LinearScanAllocator.AsmFlow.cs:219
- record `InstructionFacts` — One instruction's part in the flow: what an asm statement reads, defines, certainly overwrites — PowerBasic.Compiler/Backend/LinearScanAllocator.AsmFlow.cs:240
- method `Of` — PowerBasic.Compiler/Backend/LinearScanAllocator.AsmFlow.cs:242
- method `if(effect.ReadsFlags)` — PowerBasic.Compiler/Backend/LinearScanAllocator.AsmFlow.cs:248
- method `if(effect.WritesFlags)` — PowerBasic.Compiler/Backend/LinearScanAllocator.AsmFlow.cs:252
- method `foreach(var operand in instr.Operands)` — PowerBasic.Compiler/Backend/LinearScanAllocator.AsmFlow.cs:257
- method `new` — PowerBasic.Compiler/Backend/LinearScanAllocator.AsmFlow.cs:260
- method `new(false, [], [], [], [], destroys, [])` — PowerBasic.Compiler/Backend/LinearScanAllocator.AsmFlow.cs:268

### LinearScanAllocator.cs  `C#, 664 lines`
- namespace `PowerBasic.Compiler.Backend` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:2
- class `LinearScanAllocator` — Stage 4 of the x86-16 back end (docs/X86-BACKEND.md): linear-scan register allocation. It sweeps — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:17
- method `if(active[a].End < interval.Start)` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:305
- method `if(active[a].End < interval.Start)` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:351
- method `ReturnToPool(free, assignment[active[a].VirtualId])` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:352
- method `Usable(Reg r)` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:366
- method `foreach(var preferred in preferences)` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:372
- method `foreach(var operand in instr.Effect.WrittenRegs)` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:437
- method `if(pinned is not null)` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:441
- method `foreach(var read in PhysicalReads(instr))` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:491
- method `for(var at = from; at < index; ++at)` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:493
- method `if(!map.TryGetValue(at, out var regs))` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:494
- method `if(!regs.Contains(read))` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:496
- method `foreach(var written in PhysicalWrites(instr))` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:500
- method `WholeRegister(read.Physical)` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:513
- method `WholeRegister(baseRegister.Physical)` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:518
- method `WholeRegister(indexRegister.Physical)` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:520
- method `WholeRegister(segmentRegister.Physical)` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:522
- method `WholeRegister(written.Physical)` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:531
- method `WholeRegister(clobbered)` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:533
- method `if(instr.Clobbers.Count > 0)` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:548
- method `if(operand is MOperand.Memory mem)` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:566
- method `if(mem.Base is { IsVirtual: true } b)` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:567
- method `if(mem.Index is not null)` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:569
- method `if(mem.Index is { IsVirtual: true } x)` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:572
- method `switch(operand)` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:582
- method `if(memory.Base is { IsVirtual: true, Size: MRegSize.Byte } baseRegister)` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:587
- method `if(memory.Index is { IsVirtual: true, Size: MRegSize.Byte } indexRegist…` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:589
- method `switch(operand)` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:604
- method `Record(register.Reg)` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:606
- method `Record(memory.Base)` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:609
- method `Record(memory.Index)` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:610
- method `Record(memory.Segment)` — PowerBasic.Compiler/Backend/LinearScanAllocator.cs:611

### LivenessAnalysis.cs  `C#, 241 lines`
- namespace `PowerBasic.Compiler.Backend` — PowerBasic.Compiler/Backend/LivenessAnalysis.cs:1
- class `LivenessAnalysis` — Stage 3 of the x86-16 back end (docs/X86-BACKEND.md): live-interval analysis over a — PowerBasic.Compiler/Backend/LivenessAnalysis.cs:11
- record `LiveInterval` — A virtual register's live range over the linearized instruction indices (inclusive). — PowerBasic.Compiler/Backend/LivenessAnalysis.cs:14
- record `Liveness` — The intervals plus the set of values live AT each instruction index - the same marks the — PowerBasic.Compiler/Backend/LivenessAnalysis.cs:41
- method `if(mem.Base is { IsVirtual: true } b)` — PowerBasic.Compiler/Backend/LivenessAnalysis.cs:59
- method `if(mem.Index is { IsVirtual: true } x)` — PowerBasic.Compiler/Backend/LivenessAnalysis.cs:61
- method `if(mem.Segment is { IsVirtual: true } s)` — a far operand's segment register is read the same way the base is - the emitter moves it — PowerBasic.Compiler/Backend/LivenessAnalysis.cs:65
- method `foreach(var r in reads[i])` — PowerBasic.Compiler/Backend/LivenessAnalysis.cs:111
- method `foreach(var w in writes[i])` — PowerBasic.Compiler/Backend/LivenessAnalysis.cs:114
- method `foreach(var s in blocks[b].SuccessorsWithAsmJumps())` — including the edges an inline-asm jump makes: `!JNZ AddLoop` closes a loop the IR never drew — PowerBasic.Compiler/Backend/LivenessAnalysis.cs:132
- method `if(!outSet.SetEquals(liveOut[b]) || !inSet.SetEquals(liveIn[b]))` — PowerBasic.Compiler/Backend/LivenessAnalysis.cs:138
- method `foreach(var v in live)` — PowerBasic.Compiler/Backend/LivenessAnalysis.cs:167
- method `Mark(v, i)` — PowerBasic.Compiler/Backend/LivenessAnalysis.cs:168
- method `foreach(var w in writes[i])` — PowerBasic.Compiler/Backend/LivenessAnalysis.cs:169
- method `Mark(w, i)` — PowerBasic.Compiler/Backend/LivenessAnalysis.cs:170
- method `foreach(var r in reads[i])` — PowerBasic.Compiler/Backend/LivenessAnalysis.cs:173
- method `Mark(r, i)` — PowerBasic.Compiler/Backend/LivenessAnalysis.cs:175
- method `Mark(v, start[b])` — PowerBasic.Compiler/Backend/LivenessAnalysis.cs:179
- method `if(index.TryGetValue(successor, out var s) && s <= b && end[b] > start[…` — PowerBasic.Compiler/Backend/LivenessAnalysis.cs:219
- method `if(interval.Start <= head && interval.End >= tail)` — PowerBasic.Compiler/Backend/LivenessAnalysis.cs:227

### MachineCombiner.cs  `C#, 106 lines`
- namespace `PowerBasic.Compiler.Backend` — PowerBasic.Compiler/Backend/MachineCombiner.cs:2
- class `MachineCombiner` — Target-level combining after instruction selection. These patterns deliberately depend on x86 — PowerBasic.Compiler/Backend/MachineCombiner.cs:10
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [0, 1], ReadsFlags: false, WritesFlags: t…` — PowerBasic.Compiler/Backend/MachineCombiner.cs:35
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: fals…` — PowerBasic.Compiler/Backend/MachineCombiner.cs:63
- method `Add(memory.Base)` — PowerBasic.Compiler/Backend/MachineCombiner.cs:76
- method `Add(memory.Index)` — PowerBasic.Compiler/Backend/MachineCombiner.cs:77
- method `Add(memory.Segment)` — PowerBasic.Compiler/Backend/MachineCombiner.cs:78
- method `Add` — PowerBasic.Compiler/Backend/MachineCombiner.cs:81

### MachineEmitter.cs  `C#, 665 lines`
- namespace `PowerBasic.Compiler.Backend` — PowerBasic.Compiler/Backend/MachineEmitter.cs:2
- class `MachineEmitter` — Stage 5 of the x86-16 back end (docs/X86-BACKEND.md): emission. Given a selected — PowerBasic.Compiler/Backend/MachineEmitter.cs:14
- method `if(allocation.TryGetValue(virtualId, out var reg))` — PowerBasic.Compiler/Backend/MachineEmitter.cs:132
- method `if(instr.Opcode == MOpcode.Ret)` — PowerBasic.Compiler/Backend/MachineEmitter.cs:144
- method `onReturn(asm)` — PowerBasic.Compiler/Backend/MachineEmitter.cs:146
- method `if(elideFrame)` — PowerBasic.Compiler/Backend/MachineEmitter.cs:147
- method `if(positions.TryGetValue(successor, out var target) && target <= index)` — PowerBasic.Compiler/Backend/MachineEmitter.cs:187
- method `if(cell is not null && cell != name)` — PowerBasic.Compiler/Backend/MachineEmitter.cs:218
- method `if(ops[0] is MOperand.Register exchangeRegister)` — PowerBasic.Compiler/Backend/MachineEmitter.cs:245
- method `if(this.ToSource(ops[0]) is Mem factor)` — PowerBasic.Compiler/Backend/MachineEmitter.cs:268
- method `if(this.ToSource(ops[1]) is Mem im)` — PowerBasic.Compiler/Backend/MachineEmitter.cs:277
- method `if(ops[0] is MOperand.Register incReg)` — PowerBasic.Compiler/Backend/MachineEmitter.cs:286
- method `if(ops[0] is MOperand.Register decReg)` — PowerBasic.Compiler/Backend/MachineEmitter.cs:292
- method `if(ops[0] is MOperand.Register negReg)` — PowerBasic.Compiler/Backend/MachineEmitter.cs:300
- method `if(ops[0] is MOperand.Register notReg)` — PowerBasic.Compiler/Backend/MachineEmitter.cs:306
- method `if(this.ToSource(ops[0]) is Mem divisor)` — PowerBasic.Compiler/Backend/MachineEmitter.cs:313
- method `if(ops[0] is MOperand.Register jumpThrough)` — PowerBasic.Compiler/Backend/MachineEmitter.cs:332
- method `resolve(callee)` — PowerBasic.Compiler/Backend/MachineEmitter.cs:344
- method `switch(this.ToSource(ops[0]))` — PowerBasic.Compiler/Backend/MachineEmitter.cs:351
- method `BackendInvariantException("MachineEmitter.ResolveData", $"no data cell for global '{name}' - C…` — PowerBasic.Compiler/Backend/MachineEmitter.cs:553
- method `BackendInvariantException("MachineEmitter.EmitInlineAsm", "an MOpcode.InlineAsm instruction ha…` — PowerBasic.Compiler/Backend/MachineEmitter.cs:574
- method `BackendInvariantException("MachineEmitter.EmitInlineAsm", $"inline asm '{descriptor.Text.Trim(…` — PowerBasic.Compiler/Backend/MachineEmitter.cs:589
- class `FrameResolver` — Answers inline-asm identifiers from what the selector paired with them - a frame cell for a — PowerBasic.Compiler/Backend/MachineEmitter.cs:600
- method `TryResolve(string name, out AsmSymbol symbol)` — PowerBasic.Compiler/Backend/MachineEmitter.cs:601

### MachineIr.cs  `C#, 463 lines`
- namespace `PowerBasic.Compiler.Backend` — PowerBasic.Compiler/Backend/MachineIr.cs:2
- enum `MRegSize` — The target-level machine IR the x86-16 back end selects the SSA IR into (docs/X86-BACKEND.md). — PowerBasic.Compiler/Backend/MachineIr.cs:18
- record `MReg` — A register operand: a virtual id until allocation binds it to a physical register. — PowerBasic.Compiler/Backend/MachineIr.cs:21
- record `MOperand` — An instruction operand: a register, an immediate, a memory reference, a code/data label or a spill/… — PowerBasic.Compiler/Backend/MachineIr.cs:33
- record `Register` — PowerBasic.Compiler/Backend/MachineIr.cs:34
- record `Immediate` — PowerBasic.Compiler/Backend/MachineIr.cs:36
- record `Memory` — [Base + Index*Scale + Disp]; / are registers, either — PowerBasic.Compiler/Backend/MachineIr.cs:64
- record `LabelRef` — A code label (branch target) or a data/global symbol address. — PowerBasic.Compiler/Backend/MachineIr.cs:68
- record `StackSlot` — A frame stack slot - allocas and register spills resolve to [BP + Offset] at emission. — PowerBasic.Compiler/Backend/MachineIr.cs:75
- record `DataCell` — A source variable's data cell, named as the IR names it (g.total, static.Tick.c). — PowerBasic.Compiler/Backend/MachineIr.cs:83
- record `DataOffset` — The address of a data object rather than its contents - MOV SI, OFFSET .str0, the — PowerBasic.Compiler/Backend/MachineIr.cs:91
- record `InlineAsmText` — An inline-assembly block: the source text plus the BASIC names it refers to. The instruction's — PowerBasic.Compiler/Backend/MachineIr.cs:106
- record `BlockOffset` — The OFFSET of a basic block's own label - the machine form of the IR's blockaddress. — PowerBasic.Compiler/Backend/MachineIr.cs:114
- record `BlockAddressTable` — A table of BLOCK ADDRESSES, assembled as DATA into the code stream immediately behind the — PowerBasic.Compiler/Backend/MachineIr.cs:153
- record `ParamCell` — An incoming argument read straight out of the cell the caller pushed it into - [BP+6]. — PowerBasic.Compiler/Backend/MachineIr.cs:163
- class `MInstr` — A machine instruction: an opcode, its operands, and a conservative def/use descriptor so that one — PowerBasic.Compiler/Backend/MachineIr.cs:197
- record `MInstrEffect` — What an reads and writes, in terms of operand positions (so allocation can rewrite virtuals). — PowerBasic.Compiler/Backend/MachineIr.cs:218
- enum `MOpcode` — The x86-16 opcodes the selector targets; each maps to an method at emission. — PowerBasic.Compiler/Backend/MachineIr.cs:230
- class `MOpcodes` — Facts about opcodes that the scheduler and the selector both need to agree on. — PowerBasic.Compiler/Backend/MachineIr.cs:318
- class `MBlock` — A machine basic block: a label, its instructions in order, and its successor labels. — PowerBasic.Compiler/Backend/MachineIr.cs:348
- method `if(operand is MOperand.BlockOffset target)` — PowerBasic.Compiler/Backend/MachineIr.cs:368
- class `MFunction` — A machine function: its blocks, the number of virtual registers selection minted, and the stack-slo… — PowerBasic.Compiler/Backend/MachineIr.cs:375
- method `foreach(var instr in block.Instructions)` — PowerBasic.Compiler/Backend/MachineIr.cs:458

### MachineLoopRotation.cs  `C#, 81 lines`
- namespace `PowerBasic.Compiler.Backend` — PowerBasic.Compiler/Backend/MachineLoopRotation.cs:1
- class `MachineLoopRotation` — Rotates a canonical pre-tested machine loop under the SPEED objective. The header remains the — PowerBasic.Compiler/Backend/MachineLoopRotation.cs:8

### MachineOptimizationState.cs  `C#, 40 lines`
- namespace `PowerBasic.Compiler.Backend` — PowerBasic.Compiler/Backend/MachineOptimizationState.cs:2
- class `MachineOptimizationState` — Carries the optimizer-on decision from selection into the late machine pipeline without baking a — PowerBasic.Compiler/Backend/MachineOptimizationState.cs:12
- class `Marker` — PowerBasic.Compiler/Backend/MachineOptimizationState.cs:13

### MachineScheduler.cs  `C#, 193 lines`
- namespace `PowerBasic.Compiler.Backend` — PowerBasic.Compiler/Backend/MachineScheduler.cs:2
- class `MachineScheduler` — Stage 6 of the x86-16 back end (docs/X86-BACKEND.md): instruction scheduling on the machine IR. — PowerBasic.Compiler/Backend/MachineScheduler.cs:14
- method `if(value < 0)` — PowerBasic.Compiler/Backend/MachineScheduler.cs:111
- method `if(!first.ContainsKey(value))` — PowerBasic.Compiler/Backend/MachineScheduler.cs:113
- method `if(mem.Base is { } b)` — PowerBasic.Compiler/Backend/MachineScheduler.cs:181
- method `if(mem.Index is { } x)` — PowerBasic.Compiler/Backend/MachineScheduler.cs:183
- method `if(mem.Segment is { } s)` — PowerBasic.Compiler/Backend/MachineScheduler.cs:185

### Peephole.cs  `C#, 543 lines`
- namespace `PowerBasic.Compiler.Backend` — PowerBasic.Compiler/Backend/Peephole.cs:1
- class `Peephole` — The idiom pass over the selected machine IR (docs/X86-BACKEND.md): rewrites that are about — PowerBasic.Compiler/Backend/Peephole.cs:72
- record `Census` — How many times each virtual register is defined and read over the WHOLE function, which is what — PowerBasic.Compiler/Backend/Peephole.cs:120
- method `Of` — PowerBasic.Compiler/Backend/Peephole.cs:121
- method `foreach(var read in reads)` — PowerBasic.Compiler/Backend/Peephole.cs:127
- method `foreach(var write in writes)` — PowerBasic.Compiler/Backend/Peephole.cs:129
- method `new(defs, uses)` — PowerBasic.Compiler/Backend/Peephole.cs:132
- method `Exactly(MReg register, int definitions, int readers)` — Whether the value is virtual and mentioned exactly this many times, and no more. — PowerBasic.Compiler/Backend/Peephole.cs:136
- method `MInstrEffect(WrittenRegs: user.Effect.WrittenRegs, ReadRegs: [0], ReadsFlags: use…` — PowerBasic.Compiler/Backend/Peephole.cs:189
- method `if` — PowerBasic.Compiler/Backend/Peephole.cs:246
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: source is MOperand.Register ? [1] : [], …` — PowerBasic.Compiler/Backend/Peephole.cs:251
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: fal…` — PowerBasic.Compiler/Backend/Peephole.cs:327
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [1], ReadsFlags: false, WritesFlags: fals…` — PowerBasic.Compiler/Backend/Peephole.cs:330
- method `MInstr(modify.Opcode == MOpcode.Add ? MOpcode.Inc : MOpcode.Dec, [cell], ne…` — PowerBasic.Compiler/Backend/Peephole.cs:374
- method `IsMemory(subject)` — PowerBasic.Compiler/Backend/Peephole.cs:419
- method `MInstrEffect(WrittenRegs: [], ReadRegs: subject is MOperand.Register ? [0] : [], …` — PowerBasic.Compiler/Backend/Peephole.cs:438
- method `if` — PowerBasic.Compiler/Backend/Peephole.cs:490
- method `if` — PowerBasic.Compiler/Backend/Peephole.cs:501
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: true…` — PowerBasic.Compiler/Backend/Peephole.cs:536

### PostRegisterAllocationPeepholes.cs  `C#, 110 lines`
- namespace `PowerBasic.Compiler.Backend` — PowerBasic.Compiler/Backend/PostRegisterAllocationPeepholes.cs:2
- class `PostRegisterAllocationPeepholes` — Local machine-IR simplifications that require the final virtual-to-physical allocation. — PowerBasic.Compiler/Backend/PostRegisterAllocationPeepholes.cs:6
- method `if(IsSelfCopy(block.Instructions[i], allocation))` — PowerBasic.Compiler/Backend/PostRegisterAllocationPeepholes.cs:17
- method `if(i + 1 < block.Instructions.Count && TryPair(block.Instructions[i], b…` — PowerBasic.Compiler/Backend/PostRegisterAllocationPeepholes.cs:22
- method `if(removeFirst)` — PowerBasic.Compiler/Backend/PostRegisterAllocationPeepholes.cs:27
- method `if(i > 0)` — PowerBasic.Compiler/Backend/PostRegisterAllocationPeepholes.cs:30

### RuntimeAbi.cs  `C#, 945 lines`
- namespace `PowerBasic.Compiler.Backend` — PowerBasic.Compiler/Backend/RuntimeAbi.cs:2
- class `RuntimeAbi` — The bridge between the IR's runtime declarations and the DOS runtime the direct code generator — PowerBasic.Compiler/Backend/RuntimeAbi.cs:20
- enum `ArgKind` — Where one IR argument goes: registers, the x87 stack, or a target address. — PowerBasic.Compiler/Backend/RuntimeAbi.cs:23
- record `RuntimeArg` — PowerBasic.Compiler/Backend/RuntimeAbi.cs:123
- enum `ResultKind` — How a routine hands its answer back, when the IR's result type is not simply the register. — PowerBasic.Compiler/Backend/RuntimeAbi.cs:127
- record `Routine` — One runtime routine: the label the direct emitter calls, where its arguments go, what it — PowerBasic.Compiler/Backend/RuntimeAbi.cs:184
- method `new(ArgKind.Word, Reg.CX)` — PowerBasic.Compiler/Backend/RuntimeAbi.cs:341
- method `new(ArgKind.Word, Reg.BX)` — PowerBasic.Compiler/Backend/RuntimeAbi.cs:400
- method `new(ArgKind.Pointer, Reg.DI, Reg.SI)` — PowerBasic.Compiler/Backend/RuntimeAbi.cs:545
- method `new(ArgKind.Word, Reg.CX)` — PowerBasic.Compiler/Backend/RuntimeAbi.cs:691

### SelectionTarget.cs  `C#, 41 lines`
- namespace `PowerBasic.Compiler.Backend` — PowerBasic.Compiler/Backend/SelectionTarget.cs:1
- record `SelectionTarget` — What the instruction selector is compiling for: the CPU generation it may assume and the — PowerBasic.Compiler/Backend/SelectionTarget.cs:13

### Spiller.cs  `C#, 880 lines`
- namespace `PowerBasic.Compiler.Backend` — PowerBasic.Compiler/Backend/Spiller.cs:1
- class `Spiller` — The x86-16 back end's spilling (docs/X86-BACKEND.md): moving a value out of the register file and — PowerBasic.Compiler/Backend/Spiller.cs:23
- record `Progress` — How far along the spiller is, as three counts that a move must lower to be worth applying. It is — PowerBasic.Compiler/Backend/Spiller.cs:57
- method `Of` — PowerBasic.Compiler/Backend/Spiller.cs:58
- method `foreach(var instruction in block.Instructions)` — PowerBasic.Compiler/Backend/Spiller.cs:65
- method `if(instruction.Operands is [MOperand.Register { Reg: { IsVirtual: true …` — PowerBasic.Compiler/Backend/Spiller.cs:68
- method `new(present.Count(value => !function.MovedValues.Contains(value)), cross…` — PowerBasic.Compiler/Backend/Spiller.cs:77
- method `IsBelow(Progress other)` — Whether this state is strictly closer to an allocation than . — PowerBasic.Compiler/Backend/Spiller.cs:82
- method `if(instr.Operands is not [MOperand.Register { Reg: { IsVirtual: true } …` — PowerBasic.Compiler/Backend/Spiller.cs:119
- method `if(census.DefinitionsOf(target.VirtualId) != 1 || census.UsesOf(target.…` — PowerBasic.Compiler/Backend/Spiller.cs:122
- method `if(!ReadsTheSameOperandsAtEveryUse(census, instr, target.VirtualId))` — PowerBasic.Compiler/Backend/Spiller.cs:124
- method `if(UnsettledUses(census, instr, target.VirtualId) == 0)` — PowerBasic.Compiler/Backend/Spiller.cs:126
- method `Rematerialize(function, census, instr, target.VirtualId)` — PowerBasic.Compiler/Backend/Spiller.cs:128
- method `Name(operands, memory.Base)` — PowerBasic.Compiler/Backend/Spiller.cs:168
- method `Name(operands, memory.Index)` — PowerBasic.Compiler/Backend/Spiller.cs:169
- method `Name(operands, memory.Segment)` — PowerBasic.Compiler/Backend/Spiller.cs:170
- method `if(LivenessAnalysis.RegistersOf(definedAt.Block.Instructions[at]).Write…` — PowerBasic.Compiler/Backend/Spiller.cs:181
- method `foreach` — PowerBasic.Compiler/Backend/Spiller.cs:204
- method `for(var i = block.Instructions.Count - 1; i >= 0; --i)` — PowerBasic.Compiler/Backend/Spiller.cs:206
- method `if(!UsesAsAddress(instruction, load.VirtualId))` — PowerBasic.Compiler/Backend/Spiller.cs:208
- method `switch(operand)` — PowerBasic.Compiler/Backend/Spiller.cs:241
- method `Is(memory.Base, virtualId)` — PowerBasic.Compiler/Backend/Spiller.cs:244
- class `ValueCensus` — Where every value is defined and used, and where every instruction sits, taken once per spiller — PowerBasic.Compiler/Backend/Spiller.cs:348
- method `Of` — PowerBasic.Compiler/Backend/Spiller.cs:353
- method `for(var i = 0; i < block.Instructions.Count; ++i)` — PowerBasic.Compiler/Backend/Spiller.cs:358
- method `Mentioned(instruction, mentioned)` — PowerBasic.Compiler/Backend/Spiller.cs:362
- method `foreach(var value in mentioned)` — PowerBasic.Compiler/Backend/Spiller.cs:363
- method `if(writes.Contains(value))` — PowerBasic.Compiler/Backend/Spiller.cs:366
- method `UsesOf` — PowerBasic.Compiler/Backend/Spiller.cs:377
- method `DefinitionsOf` — PowerBasic.Compiler/Backend/Spiller.cs:380
- method `PreparesOnly(MInstr instruction, MInstr use)` — Whether exists only to prepare an operand of : — PowerBasic.Compiler/Backend/Spiller.cs:388
- method `Mentioned(MInstr instruction, List<int> into)` — The virtual values the instruction names, each once - as an operand or inside an address. — PowerBasic.Compiler/Backend/Spiller.cs:397
- method `switch(operand)` — PowerBasic.Compiler/Backend/Spiller.cs:400
- method `Add(into, register.VirtualId)` — PowerBasic.Compiler/Backend/Spiller.cs:402
- method `if(memory.Base is { IsVirtual: true } baseRegister)` — PowerBasic.Compiler/Backend/Spiller.cs:405
- method `Add(into, baseRegister.VirtualId)` — PowerBasic.Compiler/Backend/Spiller.cs:406
- method `if(memory.Index is { IsVirtual: true } indexRegister)` — PowerBasic.Compiler/Backend/Spiller.cs:407
- method `Add(into, indexRegister.VirtualId)` — PowerBasic.Compiler/Backend/Spiller.cs:408
- method `if(memory.Segment is { IsVirtual: true } segmentRegister)` — PowerBasic.Compiler/Backend/Spiller.cs:409
- method `Add(into, segmentRegister.VirtualId)` — PowerBasic.Compiler/Backend/Spiller.cs:410
- method `Add` — PowerBasic.Compiler/Backend/Spiller.cs:414
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: fals…` — PowerBasic.Compiler/Backend/Spiller.cs:424
- method `if(!filled.Contains(register))` — PowerBasic.Compiler/Backend/Spiller.cs:456
- method `if(ReferenceEquals(instr, definition) || !Mentions(instr, virtualId))` — PowerBasic.Compiler/Backend/Spiller.cs:497
- method `if(LivenessAnalysis.RegistersOf(instr).Writes.Contains(virtualId))` — PowerBasic.Compiler/Backend/Spiller.cs:499
- method `if(ReferenceEquals(block.Instructions[i], definition))` — PowerBasic.Compiler/Backend/Spiller.cs:513
- method `if(TrySplitArgument(function, census, interval.VirtualId, function.Argu…` — PowerBasic.Compiler/Backend/Spiller.cs:609
- method `foreach` — PowerBasic.Compiler/Backend/Spiller.cs:620
- method `for(var i = block.Instructions.Count - 1; i >= 0; --i)` — PowerBasic.Compiler/Backend/Spiller.cs:622
- method `if(definitionSet.Contains(instruction) || !Mentions(instruction, interv…` — PowerBasic.Compiler/Backend/Spiller.cs:624
- method `if(LivenessAnalysis.RegistersOf(instruction).Writes.Contains(interval.V…` — PowerBasic.Compiler/Backend/Spiller.cs:626
- method `for(var i = block.Instructions.Count - 1; i >= 0; --i)` — PowerBasic.Compiler/Backend/Spiller.cs:638
- method `if(!definitionSet.Contains(instruction))` — PowerBasic.Compiler/Backend/Spiller.cs:640
- method `MInstrEffect(WrittenRegs: [], ReadRegs: [1], ReadsFlags: false, WritesFlags: fals…` — PowerBasic.Compiler/Backend/Spiller.cs:648
- method `if(readsOldValue)` — PowerBasic.Compiler/Backend/Spiller.cs:650
- method `BackendInvariantException("Spiller.SplitLiveRange", $"{definitions.Count} definitions of v{int…` — PowerBasic.Compiler/Backend/Spiller.cs:659
- method `if(!Mentions(instruction, virtualId))` — PowerBasic.Compiler/Backend/Spiller.cs:685
- method `switch(operand)` — PowerBasic.Compiler/Backend/Spiller.cs:705
- method `Is(memory.Base, virtualId)` — PowerBasic.Compiler/Backend/Spiller.cs:710
- method `Is(memory.Index, virtualId)` — PowerBasic.Compiler/Backend/Spiller.cs:713
- method `Is(memory.Segment, virtualId)` — PowerBasic.Compiler/Backend/Spiller.cs:716
- method `if(operand is MOperand.Memory mem && ((mem.Base is { IsVirtual: true } …` — PowerBasic.Compiler/Backend/Spiller.cs:800
- method `if(positions.Count == 0)` — PowerBasic.Compiler/Backend/Spiller.cs:838
- method `foreach(var at in positions)` — PowerBasic.Compiler/Backend/Spiller.cs:842

### SuperoptimizedPeepholes.cs  `C#, 142 lines`
- namespace `PowerBasic.Compiler.Backend` — PowerBasic.Compiler/Backend/SuperoptimizedPeepholes.cs:1
- class `SuperoptimizedPeepholes` — A bounded superoptimizer for small one-register x86-16 peepholes. At startup it searches a tiny — PowerBasic.Compiler/Backend/SuperoptimizedPeepholes.cs:9
- method `if(!FlagsDeadAfter(block, i) || instruction.Condition is not null || in…` — PowerBasic.Compiler/Backend/SuperoptimizedPeepholes.cs:20
- method `if(Match(instruction) is not { } pattern || !_catalog.TryGetValue(patte…` — PowerBasic.Compiler/Backend/SuperoptimizedPeepholes.cs:22
- method `unchecked((ushort)immediate.Value)` — PowerBasic.Compiler/Backend/SuperoptimizedPeepholes.cs:35
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: tru…` — PowerBasic.Compiler/Backend/SuperoptimizedPeepholes.cs:53
- method `MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: wri…` — PowerBasic.Compiler/Backend/SuperoptimizedPeepholes.cs:64
- method `if(cost >= bestCost || !Equivalent(source, candidate))` — PowerBasic.Compiler/Backend/SuperoptimizedPeepholes.cs:85
- enum `SourcePattern` — PowerBasic.Compiler/Backend/SuperoptimizedPeepholes.cs:138
- enum `Candidate` — PowerBasic.Compiler/Backend/SuperoptimizedPeepholes.cs:140

### X86CallAbi.cs  `C#, 57 lines`
- namespace `PowerBasic.Compiler.Backend` — PowerBasic.Compiler/Backend/X86CallAbi.cs:3
- enum `X86StackArgumentOrder` — The order in which argument groups are placed on the 16-bit x86 stack. — PowerBasic.Compiler/Backend/X86CallAbi.cs:7
- enum `X86StackCleanup` — Which side restores SP after a 16-bit x86 stack call. — PowerBasic.Compiler/Backend/X86CallAbi.cs:10
- enum `X86CallDistance` — The return-address width used by a 16-bit x86 call. — PowerBasic.Compiler/Backend/X86CallAbi.cs:13
- record `X86CallAbi` — The concrete x86-16 rules selected from a source-level calling-convention identity. Register — PowerBasic.Compiler/Backend/X86CallAbi.cs:21

## PowerBasic.Compiler/CodeGen/

### CodeGenerator.Arrays.cs  `C#, 891 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.Arrays.cs:5
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.Arrays.cs:7
- method `if(coverWrite != null)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Arrays.cs:105
- method `if(fillBytes % 4 != 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Arrays.cs:521
- method `new(Mem.At(Reg.BP, symbol.Offset + offset), false)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Arrays.cs:685
- method `for` — PowerBasic.Compiler/CodeGen/CodeGenerator.Arrays.cs:688
- method `if(this.CheckBounds && !provablyInRange)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Arrays.cs:697
- method `if(strides[d] != 1)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Arrays.cs:703
- method `if(d > 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Arrays.cs:705
- method `if(d < bounds.Count - 1)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Arrays.cs:709

### CodeGenerator.Backend.cs  `C#, 974 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.Backend.cs:8
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.Backend.cs:10
- method `if(!f.IsDeclaration)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Backend.cs:213
- method `if(!f.IsDeclaration && SwitchFormation.Run(f) > 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Backend.cs:232
- method `if(!f.IsDeclaration)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Backend.cs:245
- method `if(CalleeNames(candidates[i].Fn) .FirstOrDefault(name => !routable.Cont…` — PowerBasic.Compiler/CodeGen/CodeGenerator.Backend.cs:313
- method `if(this._backendProcs.ContainsKey(proc) && CalleeNames(fn).FirstOrDefau…` — PowerBasic.Compiler/CodeGen/CodeGenerator.Backend.cs:340
- method `if(this._backendMain is null)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Backend.cs:611
- method `if(proc.Body is not { } body || !ReferencesVariable(body, symbol.Name))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Backend.cs:617
- method `if(this._backendProcs.ContainsKey(proc))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Backend.cs:619
- method `foreach(var symbol in procedure.Variables.Values)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Backend.cs:779
- method `if(symbol.Storage == VariableStorage.Static && IrLowering.StaticGlobalN…` — PowerBasic.Compiler/CodeGen/CodeGenerator.Backend.cs:787

### CodeGenerator.Data.cs  `C#, 73 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.Data.cs:3
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.Data.cs:5
- method `if(item.Length > 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Data.cs:67

### CodeGenerator.Expressions.cs  `C#, 2028 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:5
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:7
- method `if(wide)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:20
- method `SameConstIndex` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:60
- method `if(model.TypeOf(i) is ScalarType { IsFloat: true })` — TB types integer literals beyond LONG as DOUBLE (no QUAD there) — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:87
- method `if(model.Equates.TryGetValue(c.Name, out var v) && v.Text is { } text)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:106
- method `if(model.IntrinsicBindings.TryGetValue(n, out var bareIntrinsic))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:115
- method `if(model.CallBindings.TryGetValue(n, out var fn))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:119
- method `if(!model.VariableBindings.TryGetValue(n, out var symbol))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:123
- method `if(symbol.Type is ArrayType)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:127
- method `if(this._inlineParamSlots is { } inlined && inlined.TryGetValue(symbol,…` — pb36 O6: inside an inlined body, parameter reads come from the — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:133
- method `if(this.ResidentRegOf(symbol) is { } residentReg)` — pb36 O5: a variable resident in a register this loop (FOR counter in — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:143
- method `if(this._ipcp is { } ipcp && ipcp.TryGetValue(symbol, out var constant)…` — pb36 O18 (IPCP): a parameter that is the same constant at every call — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:155
- method `if(this.EmitPlace(n) is { } place)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:163
- method `if(this._pureFold is { } pf && pf.TryGetValue(call, out var pureResult)…` — pb36 O25: a pure-function call with all-constant arguments was evaluated at — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:171
- method `if(model.ProcPtrCalls.TryGetValue(call, out var ptrSig))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:176
- method `if(model.IntrinsicBindings.TryGetValue(call, out var intrinsic))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:178
- method `if(model.VariableBindings.TryGetValue(call, out var array))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:180
- method `if(call.Arguments.Count == 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:181
- method `if(this.EmitPlace(call) is { } place)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:185
- method `if(this.EmitPlace(m) is { } memberPlace)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:194
- method `if(this.EmitPlace(ix) is { } indexPlace)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:199
- method `if(this.EmitPlace(deref) is { } derefPlace)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:204
- method `if(!this.TryEmitFolded(u))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:217
- method `if(!this.TryEmitFolded(b))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:222
- method `if(!this.TryEmitFolded(ternary))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:232
- method `if(leftUnsigned != rightUnsigned && widest is ScalarType { IsFloat: fal…` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:438
- method `if(promoted.Size > ((ScalarType)widest).Size)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:441
- method `if(this.TryEmitInt16ConstBinary(b, opType, unsignedCompare))` — pb36 O8: fold a constant operand into one immediate ALU op — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:466
- method `if(this.OptimizeSpeed && !this.CheckOverflow && b.Op == BinaryOp.Multip…` — $OPTIMIZE SPEED: x * 2^n inlines as shifts (no overflow checking applies) — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:469
- method `if(isComparison && this.TryInt16MemOperand(b.Right, opType) is { } cmem)` — pb36 O8: a comparison against a direct-memory right operand reads it straight into the — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:479
- method `if(!this.TryEmitCompareAsBranch(b, cmpCond))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:484
- method `if(b.Op is BinaryOp.Add or BinaryOp.Subtract or BinaryOp.And or BinaryO…` — pb36 O8: a same-width direct-memory right operand of a commutative/subtractive ALU op — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:494
- method `switch(b.Op)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:499
- method `if(this.CheckOverflow && !this.ProvablyNoOverflow(b))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:502
- method `if(this.CheckOverflow && !this.ProvablyNoOverflow(b))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:507
- method `if(this.TryEmitInt32ConstBinary(b, opType))` — pb36 O8: fold a constant operand into immediate pair ops — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:528
- method `if(this.Optimize && this.TryInt32MemOperand(b.Right) is { } lo32)` — pb36: a 4-byte direct-cell right operand loads straight into BX:CX (no — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:533
- method `if(this.Optimize && this.TryFloatMemOperand(b.Right) is { } fmem && thi…` — pb36: x87 reads the right float operand from memory (FADD/FSUB/FMUL/FDIV — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:561
- method `if(this.Optimize && this.TryFloatIntMemOperand(b.Right) is { } imem && …` — pb36: a float op against a signed integer cell reads it with an x87 integer — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:565
- method `if(this.Optimize && b.Op is not BinaryOp.Power && this.TryFloatConstMem…` — pb36: a float op against a float literal reads it from its data-segment QWORD — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:570
- method `if(this.Optimize && this.Has32BitCpu)` — pb36 C1 ($CPU 80386): a 64-bit bitwise op runs inline as two 32-bit halves — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:616
- method `Collect` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:742
- method `var(jump, condition)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:878
- method `if(!this.TryEmitCompareAsBranch(b, condition))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:881
- method `if` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:900
- method `if(this.CheckOverflow && !this.ProvablyNoOverflow(b))` — pb36 O16: drop the Error-6 check when the affine FOR-counter range proves no overflow — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1156
- method `if(this.CheckOverflow && !this.ProvablyNoOverflow(b))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1161
- method `if(this.CheckOverflow)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1166
- method `if(!this.DivisorNonZero(b))` — pb36 O16: drop the divide-by-zero guard when the divisor range excludes zero — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1171
- method `if(!this.DivisorNonZero(b))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1177
- method `if(this._stashQuotientSlot is { } quotientSlot)` — O0079 reversed: this IDIV produced a quotient a later q = n \ d wants, and the next — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1183
- method `EmitOperand` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1319
- method `if(this.TryModularFoldConst(b.Right, out c))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1330
- method `if(this.TryModularFoldConst(b.Left, out c))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1332
- method `EmitOperand(variable)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1336
- method `switch(b.Op)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1337
- method `if(this.CheckOverflow && !this.ProvablyNoOverflow(b))` — pb36 O16: drop the Error-6 check when the affine FOR-counter range proves no overflow — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1344
- method `if(!this.TryModularFoldConst(b.Right, out var c))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1352
- method `EmitOperand(b.Left)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1354
- method `if(this.CheckOverflow && !this.ProvablyNoOverflow(b))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1356
- method `if(this.TryModularFoldConst(b.Right, out c))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1366
- method `EmitOperand(variable)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1374
- method `if((c & 0xFFFF) == 0)` — pb36 O8: compare against zero is OR AX,AX (2 bytes, same ZF/SF; OF is — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1378
- method `if(!this.TryEmitCompareAsBranch(b, condition))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1383
- method `if(this.CheckOverflow && !this.ProvablyNoOverflow32(b))` — pb36 O16: drop the Error-6 check when the affine FOR-counter range proves no 32-bit overflow — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1434
- method `if(this.CheckOverflow && !this.ProvablyNoOverflow32(b))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1440
- method `if((c & 0xFFFFFFFFL) != 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1444
- method `if(this.CheckOverflow && !this.ProvablyNoOverflow32(b))` — pb36 O16: drop the Error-6 check when the affine FOR-counter range proves no 32-bit overflow — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1583
- method `if(this.CheckOverflow && !this.ProvablyNoOverflow32(b))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1589
- method `if(this.BothOperandsNarrow16(b, unsignedType))` — pb36 O16 type narrowing: when both operands provably fit one 16-bit word, the 8086's — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1598
- method `if(this.Optimize && this.Has32BitCpu)` — pb36 C1 ($CPU 80386): low-32-bit product via one IMUL EAX, EBX - — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1607
- method `if(this.Optimize && !unsignedType && this.OptFolder.TryFold(b.Right) is…` — pb36 O16: a signed LONG \ / MOD by a compile-time-constant divisor of — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1636
- method `if(b.Op == BinaryOp.Modulo)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1641
- method `if(this.Optimize && this.Has32BitCpu && this.OptFolder.TryFold(b.Right)…` — pb36 C1 ($CPU 80386): divide by a compile-time-constant divisor of — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1652
- method `if(unsignedType)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1661
- method `switch(jump)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1716
- method `case` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1878
- method `case` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1882
- method `case` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1885
- method `case` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1891
- method `case` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1896
- method `case` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1906
- method `case` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1912
- method `case` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1920
- method `case` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1930
- method `if(this.CheckOverflow)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1933
- method `case` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1966
- method `case` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1971
- method `case` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1979
- method `case` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1992
- method `case` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:1998
- method `case` — PowerBasic.Compiler/CodeGen/CodeGenerator.Expressions.cs:2001

### CodeGenerator.Extras.cs  `C#, 190 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.Extras.cs:4
- class `CodeGenerator` — PB 3.x surface added with the dialect wave: code pointers, ASC statement, STDIN/STDOUT, QUAD consta… — PowerBasic.Compiler/CodeGen/CodeGenerator.Extras.cs:8
- method `if(place.Far)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Extras.cs:84

### CodeGenerator.Graphics.cs  `C#, 268 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.Graphics.cs:4
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.Graphics.cs:6
- method `if(step.X != 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Graphics.cs:143
- method `if(step.Y != 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Graphics.cs:147

### CodeGenerator.InlineAsm.cs  `C#, 197 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsm.cs:6
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsm.cs:8
- method `if(InlineAsmCanonicalizer.IsRedundant(statement.Text))` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsm.cs:68
- method `if` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsm.cs:74
- method `if(InlineAsmScheduler.Schedule(lines) is { } order)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsm.cs:77
- class `InlineAsmResolver` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsm.cs:115
- method `TryResolve` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsm.cs:117
- method `if(owner.LookupVariable(bare, explicitSuffix) is { } suffixed)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsm.cs:129
- method `foreach` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsm.cs:135
- method `if` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsm.cs:147
- method `return(name[..^text.Length], suffix)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsm.cs:168

### CodeGenerator.InlineAsmBitManipulation.cs  `C#, 186 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBitManipulation.cs:3
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBitManipulation.cs:5
- method `InvalidOperationException("not a POPCNT word source")` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBitManipulation.cs:163

### CodeGenerator.InlineAsmBmi.cs  `C#, 783 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:3
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:5
- method `if(operands.Count != 3 || !BmiNativeRegister(operands[0], out var desti…` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:36
- method `if(rightRegister is { } right)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:41
- method `if(operands.Count != 3 || !BmiNativeRegister(operands[0], out var desti…` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:49
- method `if(sourceRegister is { } source)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:55
- method `if(operands.Count != 2 || !BmiNativeRegister(operands[0], out var desti…` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:63
- method `if(sourceRegister is { } registerSource)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:68
- method `switch(mnemonic)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:69
- method `switch(mnemonic)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:77
- method `if(operands.Count != 3 || !BmiNativeRegister(operands[0], out var desti…` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:88
- method `if(sourceRegister is { } registerSource)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:94
- method `switch(mnemonic)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:95
- method `switch(mnemonic)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:103
- method `if(operands.Count != 3 || !BmiNativeRegister(operands[0], out var desti…` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:114
- method `if(mnemonic == "PDEP")` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:120
- method `if(operands.Count != 3 || !BmiNativeRegister(operands[0], out var highD…` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:131
- method `if(sourceRegister is { } source)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:137
- method `if(operands.Count != 3 || !BmiNativeRegister(operands[0], out var desti…` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:145
- method `if(sourceRegister is { } source)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:152
- method `if(operands.Count != 3 || !BmiVirtualRegister(operands[1], out var left…` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:236
- method `if(operands.Count != 3 || !BmiVirtualSource(operands[1], out var source…` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:254
- method `if(operands.Count != 2 || !BmiVirtualSource(operands[1], out var source…` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:269
- method `switch(mnemonic)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:275
- method `static(asm, accumulator, right)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:331
- method `if(operands.Count != 3 || !BmiVirtualSource(operands[1], out var source…` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:501
- method `if(operands.Count != 3 || !BmiVirtualRegister(operands[1], out var lowD…` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:516
- method `if(operands.Count != 3 || !BmiVirtualRegister(operands[1], out var sour…` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:532
- method `if(operands.Count != 3 || !BmiVirtualSource(operands[1], out var data) …` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:547
- method `switch(mnemonic)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:555
- method `if(mnemonic == "BZHI")` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmBmi.cs:561

### CodeGenerator.InlineAsmCpuExtensions.cs  `C#, 178 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmCpuExtensions.cs:3
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmCpuExtensions.cs:5
- method `InvalidOperationException("POPCNT word source was not normalized")` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmCpuExtensions.cs:128

### CodeGenerator.InlineAsmCrc32Emulation.cs  `C#, 103 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmCrc32Emulation.cs:3
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmCrc32Emulation.cs:5
- method `if(sourceBytes == 1)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmCrc32Emulation.cs:84
- method `if(sourceBytes == 1)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmCrc32Emulation.cs:91

### CodeGenerator.InlineAsmCryptoExtensions.cs  `C#, 440 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmCryptoExtensions.cs:3
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmCryptoExtensions.cs:5
- record `AesLookupTables` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmCryptoExtensions.cs:7
- method `for(var k = 0; k < 4; ++k)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmCryptoExtensions.cs:261
- method `if(factor == 1)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmCryptoExtensions.cs:264
- method `if(addRoundKey)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmCryptoExtensions.cs:271

### CodeGenerator.InlineAsmGp32Arithmetic.cs  `C#, 506 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmGp32Arithmetic.cs:3
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmGp32Arithmetic.cs:5

### CodeGenerator.InlineAsmGp32Extended.cs  `C#, 186 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmGp32Extended.cs:3
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmGp32Extended.cs:5

### CodeGenerator.InlineAsmGp32Strings.cs  `C#, 133 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmGp32Strings.cs:3
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmGp32Strings.cs:5

### CodeGenerator.InlineAsmGp32Virtualization.cs  `C#, 417 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmGp32Virtualization.cs:3
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmGp32Virtualization.cs:5
- record `DwordPlace` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmGp32Virtualization.cs:7
- method `Of(Reg register)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmGp32Virtualization.cs:8
- method `Of(Mem memory)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmGp32Virtualization.cs:9
- method `InvalidOperationException("not a dword source")` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmGp32Virtualization.cs:129
- method `if(sourceSize == 1)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmGp32Virtualization.cs:238
- method `if(destination.Register is { } rr)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmGp32Virtualization.cs:352

### CodeGenerator.InlineAsmIsaClassifiers.cs  `C#, 6 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmIsaClassifiers.cs:1
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmIsaClassifiers.cs:2

### CodeGenerator.InlineAsmNativeExtendedSimd.cs  `C#, 190 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmNativeExtendedSimd.cs:3
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmNativeExtendedSimd.cs:5
- method `if(!this.EmitNativeExtendedSimdRegReg(mnemonic, destination.Register, s…` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmNativeExtendedSimd.cs:45
- method `if(!this.EmitNativeExtendedSimdRegMem(mnemonic, destination.Register, s…` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmNativeExtendedSimd.cs:49
- method `if(!this.EmitNativeExtendedSimdImmediateReg(mnemonic, destination.Regis…` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmNativeExtendedSimd.cs:138
- method `if(!this.EmitNativeExtendedSimdImmediateMem(mnemonic, destination.Regis…` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmNativeExtendedSimd.cs:142

### CodeGenerator.InlineAsmPackedString.cs  `C#, 405 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmPackedString.cs:3
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmPackedString.cs:5
- method `if` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmPackedString.cs:64
- method `for` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmPackedString.cs:209
- method `if(elementBytes == 1)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmPackedString.cs:215

### CodeGenerator.InlineAsmPolicy.cs  `C#, 231 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmPolicy.cs:3
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmPolicy.cs:5

### CodeGenerator.InlineAsmTargeting.cs  `C#, 416 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmTargeting.cs:3
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmTargeting.cs:5
- record `InlineInstruction` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmTargeting.cs:393
- method `Parse(string line)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmTargeting.cs:395
- method `new(null, string.Empty, string.Empty)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmTargeting.cs:401
- method `new(first, mnemonic, operands)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmTargeting.cs:410

### CodeGenerator.InlineAsmVectorFixups.cs  `C#, 93 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVectorFixups.cs:3
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVectorFixups.cs:5

### CodeGenerator.InlineAsmVirtualization.cs  `C#, 663 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVirtualization.cs:4
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVirtualization.cs:6
- record `VirtualIsaState` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVirtualization.cs:8
- record `VirtualOperand` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVirtualization.cs:11
- method `Of(Reg register)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVirtualization.cs:13
- method `Of(Mem memory)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVirtualization.cs:14
- method `if(target.Has32BitGeneralPurpose)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVirtualization.cs:213
- method `if(target.Has32BitGeneralPurpose)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVirtualization.cs:240
- method `if(word == 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVirtualization.cs:333
- method `if(equality)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVirtualization.cs:448
- method `if(mnemonic == "PACKSSDW")` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVirtualization.cs:499

### CodeGenerator.InlineAsmVirtualizationExtended.cs  `C#, 508 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVirtualizationExtended.cs:3
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVirtualizationExtended.cs:5
- method `if` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVirtualizationExtended.cs:210
- method `if` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVirtualizationExtended.cs:305
- method `if(wantMax)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVirtualizationExtended.cs:325
- method `if(laneBytes == 1)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVirtualizationExtended.cs:341
- method `if(laneBytes == 1)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVirtualizationExtended.cs:353
- method `if` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVirtualizationExtended.cs:370
- method `for(var offset = 0; offset < 8; offset += 2)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVirtualizationExtended.cs:372
- method `for(var offset = 4; offset >= 0; offset -= 2)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVirtualizationExtended.cs:385

### CodeGenerator.InlineAsmVirtualizationHorizontal.cs  `C#, 92 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVirtualizationHorizontal.cs:2
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVirtualizationHorizontal.cs:4
- method `if(laneBytes == 2)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVirtualizationHorizontal.cs:66
- method `if(subtract)` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVirtualizationHorizontal.cs:78

### CodeGenerator.InlineAsmVirtualizationSsse3Arithmetic.cs  `C#, 176 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVirtualizationSsse3Arithmetic.cs:2
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmVirtualizationSsse3Arithmetic.cs:4

### CodeGenerator.InlineAsmZeroOverhead.cs  `C#, 89 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmZeroOverhead.cs:2
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmZeroOverhead.cs:4
- method `SameGp` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmZeroOverhead.cs:19
- method `SameVector` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmZeroOverhead.cs:24
- method `IsImmediateZero` — PowerBasic.Compiler/CodeGen/CodeGenerator.InlineAsmZeroOverhead.cs:30

### CodeGenerator.Intrinsics.cs  `C#, 1307 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:5
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:7
- method `switch(argType)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:173
- method `if(this.EmitPlace(args[0]) is not { } azPlace)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:180
- method `if(args.Count > 2)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:245
- method `if(this.Optimize && intrinsic.Name == "INSTR" && needle is not AnyMatch…` — O0302: INSTR([k,] s$, "c") with a single-character constant needle scans for the byte — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:282
- method `if(hasStart)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:285
- method `if(hasStart)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:291
- method `if(hasStart)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:299
- method `if(hasStart)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:308
- method `if(intrinsic.Name == "VERIFY")` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:312
- method `if(intrinsic.Name == "TALLY")` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:332
- method `if(args[0] is not StringLiteralExpr usingFormat)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:342
- method `if(args.Count != 2)` — runtime format: single numeric field supported via rt_usingdyn — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:344
- method `if(args.Count > 1)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:380
- method `if(args[1] is not IntegerLiteralExpr { Value: 1 or 2 } attribute)` — FILEATTR(n, 1) is the mode the file was opened in and FILEATTR(n, 2) its DOS handle. The — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:395
- method `if(attribute.Value == 2)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:400
- method `foreach(var (internalMode, basicMode) in new[] { (0, 1), (1, 2), (2, 8), (3,…` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:416
- method `if(args.Count > 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:432
- method `if(KindOf(model.TypeOf(args[0])) == ValueKind.Str)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:434
- method `if(args.Count >= 1)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:441
- method `if(args.Count > 1)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:443
- method `for(var i = 1; i < args.Count; ++i)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:462
- method `if(KindOf(model.TypeOf(args[0])) == ValueKind.Float)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:499
- method `if(this.Optimize)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:512
- method `if(args.Count == 2)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:513
- method `if(haveSource)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:519
- method `if(args.Count > 1)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:524
- method `switch(KindOf(model.TypeOf(args[0])))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:538
- method `if(KindOf(model.TypeOf(args[1])) == ValueKind.Str)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:572
- method `if(args.Count > 1)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:602
- method `if(model.Dialect.IsPbAtLeast(Dialect.Pb31))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:611
- method `switch(KindOf(model.TypeOf(args[0])))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:657
- method `if(this.Optimize && !this.CheckOverflow)` — O0249 branchless abs: y = (x XOR mask) - mask where mask = CWD (all-ones iff negative), — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:664
- method `if(this.Optimize && KindOf(type) == ValueKind.Int16)` — O0108/O0249: branchless integer sign. cwd puts the sign mask (0 / -1) in DX; neg sets CF iff x != 0; — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:699
- method `if(onFpu)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:708
- method `if(this.Optimize && KindOf(model.TypeOf(call)) == ValueKind.Int16 && ar…` — O0108/O0248: when every argument and the result are INTEGER, fold with an integer compare instead of — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:752
- method `if(this.Optimize && KindOf(model.TypeOf(call)) == ValueKind.Int32 && ar…` — O0108/O0248: the same fold for all-LONG arguments, over DX:AX with a 32-bit signed compare. — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:758
- method `for(var i = 1; i < args.Count; ++i)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:765
- method `if(wantMax)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:773
- method `if(KindOf(model.TypeOf(args[0])) != ValueKind.Float)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:836
- method `if(args.Count > 1)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:838
- method `if(KindOf(model.TypeOf(args[0])) == ValueKind.Float)` — an integer is already whole, whichever way the rounding would have gone — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:849
- method `if(KindOf(model.TypeOf(args[0])) != ValueKind.Float)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:858
- method `if(this.EmitPlace(args[0]) is { } vp32)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:890
- method `if(model.TypeOf(args[0]) is StringType or FlexType && this.EmitPlace(ar…` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:898
- method `if(args.Count == 2)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:1051
- method `if(args.Count > 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:1065
- method `if(KindOf(model.TypeOf(args[0])) == ValueKind.Float)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:1067
- method `switch(intrinsic.Name)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:1084
- method `if(this._rt.Target.Has32BitGeneralPurpose)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:1091
- method `if(this._rt.Target.Has32BitGeneralPurpose)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:1097
- method `if(this._rt.Target.Has32BitGeneralPurpose)` — FPTAN; FSTP ST(0) is the 387 reading - discard what was pushed, keep the tangent — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:1106
- method `if(args.Count > 0 && TryLiteralValue(args[0]) == -11)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:1149
- method `if(args.Count > 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:1153
- method `if(KindOf(model.TypeOf(args[0])) == ValueKind.Str)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:1155
- method `if(KindOf(model.TypeOf(args[0])) == ValueKind.Float)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:1157
- method `if(args.Count > 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:1165
- method `if(args.Count > 2 && this.OptFolder.TryFold(args[2]) is not { Integer: …` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:1188
- method `if(args.Count > 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Intrinsics.cs:1201

### CodeGenerator.Io.cs  `C#, 422 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.Io.cs:4
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.Io.cs:6
- method `if(item.Separator == PrintSeparator.Comma)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Io.cs:47
- method `if(saveSi)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Io.cs:60
- method `if(saveSi)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Io.cs:67
- method `if(item.Separator == PrintSeparator.Comma)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Io.cs:69
- method `if(lit.Value.Length > 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Io.cs:74
- method `if(saveSi)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Io.cs:78
- method `if(saveSi)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Io.cs:83
- method `if` — PowerBasic.Compiler/CodeGen/CodeGenerator.Io.cs:90
- method `if(this.EmitPlace(target) is not { } strPlace)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Io.cs:390
- method `if(kind == ValueKind.Int32)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Io.cs:402
- method `if(kind != ValueKind.Float)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Io.cs:404
- method `if(this.EmitPlace(target) is not { } place)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Io.cs:406
- method `if(kind == ValueKind.Int32)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Io.cs:409
- method `if(kind != ValueKind.Float)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Io.cs:413
- method `if(kind == ValueKind.Int32)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Io.cs:415

### CodeGenerator.LowLevel.cs  `C#, 693 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.LowLevel.cs:4
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.LowLevel.cs:6
- method `foreach(var (_, body) in i.ElseIfs)` — PowerBasic.Compiler/CodeGen/CodeGenerator.LowLevel.cs:489
- method `if(i.Else != null)` — PowerBasic.Compiler/CodeGen/CodeGenerator.LowLevel.cs:491
- method `foreach(var arm in sel.Arms)` — PowerBasic.Compiler/CodeGen/CodeGenerator.LowLevel.cs:495
- method `if(t.Catch != null)` — PowerBasic.Compiler/CodeGen/CodeGenerator.LowLevel.cs:506
- method `if(t.Finally != null)` — PowerBasic.Compiler/CodeGen/CodeGenerator.LowLevel.cs:508
- method `foreach(var s in t.Finally)` — PowerBasic.Compiler/CodeGen/CodeGenerator.LowLevel.cs:562

### CodeGenerator.OnGoto.cs  `C#, 60 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.OnGoto.cs:4
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.OnGoto.cs:6

### CodeGenerator.Optimize.cs  `C#, 2738 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:6
- class `CodeGenerator` — pb36 optimizations (docs/PB36.md). Every transformation here must preserve — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:16
- method `IntegerLiteralExpr(n.Position, value, TypeSuffix.None)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:249
- method `if(this.Optimize && (value & 0xFFFF) == 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:275
- method `if(this.Optimize && low == 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:288
- method `if(this.Optimize && high == 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:292
- method `if(WritesCounter(a.Target, model, counter))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:476
- method `if(WritesCounter(id.Target, model, counter))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:481
- method `if(WritesCounter(sw.Left, model, counter) || WritesCounter(sw.Right, mo…` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:486
- method `if(input.Targets.Any(t => WritesCounter(t, model, counter)))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:495
- method `if(read.Targets.Any(t => WritesCounter(t, model, counter)))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:500
- method `if(i.Else != null)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:507
- method `foreach(var branch in branches)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:509
- method `foreach(var v in dim.Variables)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:621
- method `if(ReadsPending(model, upper, pending))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:625
- method `if(ReadsPending(model, assign.Value, pending))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:632
- method `if(model.VariableBindings.TryGetValue(target, out var symbol))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:634
- method `if(pending.Count == 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:636
- method `if(ReadsPending(model, loop.From, pending) || ReadsPending(model, loop.…` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:642
- method `if(model.VariableBindings.TryGetValue(counter, out var symbol))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:645
- method `if(model.CallBindings.ContainsKey(name))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:676
- method `if(model.CallBindings.ContainsKey(call))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:681
- method `if(model.VariableBindings.TryGetValue(call, out var array) && pending.C…` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:683
- method `ReadsPending(model, member.Target, pending)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:689
- method `ReadsPending(model, deref.Pointer, pending)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:695
- method `ReadsPending(model, byVal.Value, pending)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:699
- method `ReadsPending(model, unary.Operand, pending)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:702
- method `ReadsPending(model, file.Number, pending)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:708
- method `if(wide)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:822
- field `W` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:895
- method `if(this.TryModularFoldConst(b.Left, out rc))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:1150
- method `if(stepped.Lbound != 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:1303
- method `foreach(var statement in f.Body)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:1344
- method `SiCleanExpression` — a conditional whose test computes through AX/BX/CX/DX (SI-clean) and whose every arm — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:1772
- method `KindOf` — an INTEGER SELECT CASE dispatches through AX/BX/DX (the jump table's MOV BX/SHL/indexed — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:1781
- method `if(model.VariableBindings.TryGetValue(call, out var cs) && ReferenceEqu…` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:2179
- method `ExpressionReferencesArray(u.Operand, array, model)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:2184
- method `ExpressionReferencesArray(b.Left, array, model)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:2187
- method `if(firstElement.Far)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:2373
- method `if(this.Optimize && this.Has32BitCpu && values.Count >= 4)` — pb36 C1 ($CPU 80386): broadcast the 16-bit fill value into both halves of EAX — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:2384
- method `if(values.Count % 2 != 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:2391
- method `if(this.EmitPlace(copyDst) is { } dstElement)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:2424
- method `if(!dstElement.Far)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:2426
- method `foreach(var v in values)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:2461
- method `switch(acc.Type)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs:2465

### CodeGenerator.Places.cs  `C#, 1367 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:5
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:7
- record `Place` — An addressable storage location. is either a direct — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:16
- method `if(this._copyReads is { } copyReads && copyReads.TryGetValue(n, out var…` — copy propagation: a read remapped to the source of a removed copy y = x (the — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:80
- method `new(srcCell, false)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:82
- method `if(!model.VariableBindings.TryGetValue(n, out var symbol))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:83
- method `if(this._inlineParamSlots is { } inlinedSlots && inlinedSlots.TryGetVal…` — pb36 O6: inside an inlined body, a write to a parameter/local/result maps to — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:89
- method `new(inlinedSlot.Cell, Far: false)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:95
- method `if(symbol.Storage == VariableStorage.Captured)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:97
- method `if(this.TryDirectCell(symbol) is { } cell)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:99
- method `new(cell, false)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:100
- method `if(model.VariableBindings.TryGetValue(m, out var flat))` — QB-style dotted variable (binder flattened the chain into one symbol) — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:107
- method `new(flatCell, false)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:109
- method `if(model.TypeOf(m.Target) is not UdtType udt || udt.FindField(m.Member)…` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:113
- method `if(this.EmitPlace(m.Target) is not { } basePlace)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:117
- method `if(this.Optimize && this.Has32BitCpu)` — pb36 C1 ($CPU 80386): one MOVZX/MOVSX load replaces the MOV+extend pair — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:203
- method `if(b1.Signed)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:211
- method `if(place.Far)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:269
- method `if(place.Far)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:279
- method `if(place.Far)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:353
- method `if(place.Far)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:363
- method `IsSelf(Expression e)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:666
- method `IsBarrierFreeStr(Expression e)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:667
- method `if(selfIsLeft && other is StringLiteralExpr { Value: { Length: > 0 } li…` — pb36 O9 in-place: `s$ = s$ + "literal"` appends the literal bytes straight after s$'s — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:680
- method `if(selfIsLeft && other is NameExpr otherName && model.VariableBindings.…` — pb36 O9 in-place: `s$ = s$ + v$` (v$ a bare string variable) appends v$'s bytes — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:693
- method `if(selfIsLeft)` — emit operands left-to-right (genuine order); s$ is read directly, the other is dup'd — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:704
- method `if(!simple16)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:752
- method `if(!simple32)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:775
- method `if(kind == ValueKind.Int32)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:805
- method `if(this.EmitPlace(bl) is not { } lhs || this.EmitPlace(br) is not { } r…` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:848
- method `for(var k = 0; k < wt.Words; ++k)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:853
- method `if(k == 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:855
- method `if(srcW.Signed)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:878
- method `if(narrow.Signed)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:898
- method `if(narrow.ByteSize > 2)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:931
- method `if(this.EmitPlace(ls.Target) is not { } place)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:1042
- method `if(this.EmitPlace(ls.Target) is not { } place)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:1056
- method `if(this.ModularTreeBits(b.Left, maxLeafBytes, depth + 1) is not { } l |…` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:1137
- method `if(this.TryInt32MemOperand(b.Right) is { } rmem)` — a 4-byte direct cell on the right loads straight into BX:CX - no push/pop staging — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:1287
- method `if(b.Op == BinaryOp.Multiply && this.TryEmitModularConstMul(b))` — pb36 O4: v * const lowers to a shift/add chain (SPEED) instead of IMUL — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:1325
- method `if(b.Op is BinaryOp.Add or BinaryOp.Subtract && this.TryEmitModularCons…` — pb36 O8: v +/- const becomes one immediate ALU op (smaller and faster) — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:1328
- method `if(b.Op is BinaryOp.Add or BinaryOp.Subtract && (this.TryInt16MemOperan…` — pb36 O8: a direct-memory right operand reads straight into the ALU op (ADD AX,[mem]) — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:1334
- method `if(b.Op == BinaryOp.Add)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:1338
- method `if(b.Op == BinaryOp.Multiply && this.TryInt16MemOperand(b.Right, PbType…` — pb36 O8: a direct-memory right operand of a multiply reads straight into the one-operand — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:1348
- method `switch(b.Op)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Places.cs:1358

### CodeGenerator.Procs.cs  `C#, 1194 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:5
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:7
- method `if(general.Count > 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:243
- method `if(local.Type is StringType or FlexType)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:260
- method `if(sig.ReturnType is StringType or FlexType)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:373
- method `Visit` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:480
- method `Visit(i.Then)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:490
- method `foreach(var (_, armBody) in i.ElseIfs)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:491
- method `Visit(armBody)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:492
- method `if(i.Else != null)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:493
- method `Visit(i.Else)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:494
- method `foreach(var arm in s.Arms)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:497
- method `Visit(arm.Body)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:498
- method `Visit` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:515
- method `Visit(i.Then)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:529
- method `foreach(var (_, armBody) in i.ElseIfs)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:530
- method `Visit(armBody)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:531
- method `if(i.Else != null)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:532
- method `Visit(i.Else)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:533
- method `foreach(var arm in s.Arms)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:536
- method `Visit(arm.Body)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:537
- record `InlinableLeaf` — Emits a SUB/FUNCTION invocation: arguments pushed left to right (BYREF = — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:585
- field `maxStatements` — every body statement must be a scalar assignment whose target is a parameter, — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:625
- method `if(dim.Storage != StorageClass.Local || dim.SharedFlag || dim.StaticFla…` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:639
- method `foreach(var decl in dim.Variables)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:642
- method `if(local is not { Storage: VariableStorage.Local, Type: ScalarType } ||…` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:646
- method `if(!locals.Contains(local))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:648
- method `InlinableLeaf` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:678
- method `ReserveSlot` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:716
- method `if(this.EmitPlace(args[i]) is not { Far: false } refPlace)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:729
- method `if(resultKind == ValueKind.Int32)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:910
- method `if(resultKind == ValueKind.Int32)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:920
- method `if(parameterType is BcdType { IsFixedPoint: true })` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:1146
- method `switch(size)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:1150
- method `if(type is BcdType { IsFixedPoint: true })` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:1173
- method `switch(type.Size)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Procs.cs:1178

### CodeGenerator.RuntimeTarget.cs  `C#, 97 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.RuntimeTarget.cs:2
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.RuntimeTarget.cs:4
- method `if(args.Length >= 2)` — PowerBasic.Compiler/CodeGen/CodeGenerator.RuntimeTarget.cs:56
- method `if(meta.Arguments.Count >= 1)` — PowerBasic.Compiler/CodeGen/CodeGenerator.RuntimeTarget.cs:65
- method `if(token is null)` — PowerBasic.Compiler/CodeGen/CodeGenerator.RuntimeTarget.cs:75
- method `if` — PowerBasic.Compiler/CodeGen/CodeGenerator.RuntimeTarget.cs:86

### CodeGenerator.SoftwareX87.cs  `C#, 27 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.SoftwareX87.cs:2
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.SoftwareX87.cs:4

### CodeGenerator.Trivial.cs  `C#, 168 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.Trivial.cs:5
- class `CodeGenerator` — pb36 P7 (docs/PB36.md): intrinsic lowering of trivial I/O. A program whose — PowerBasic.Compiler/CodeGen/CodeGenerator.Trivial.cs:17
- method `if(end.ExitCode is { } codeExpr)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Trivial.cs:40
- method `foreach(var item in print.Items)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Trivial.cs:49
- method `switch(item.Separator)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Trivial.cs:52
- method `if(print.Items.Count == 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Trivial.cs:67
- method `foreach(var c in s.Value)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Trivial.cs:86
- method `if(model.TypeOf(value) is not ScalarType { ByteSize: <= 8 } type)` — numeric: PB renders "[ |-]digits[ ]" - only exact integral folds qualify — PowerBasic.Compiler/CodeGen/CodeGenerator.Trivial.cs:96
- method `if(this.OptFolder.TryFold(value) is not { Integer: { } raw })` — PowerBasic.Compiler/CodeGen/CodeGenerator.Trivial.cs:98
- method `if(type.IsFloat)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Trivial.cs:101
- method `if(Math.Abs(raw) >= limit)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Trivial.cs:106
- method `if(type.ByteSize > 4)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Trivial.cs:110

### CodeGenerator.Units.cs  `C#, 313 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.Units.cs:6
- class `CodeGenerator` — PowerBasic.Compiler/CodeGen/CodeGenerator.Units.cs:8
- record `ListingProcedure` — One procedure entry in a . — PowerBasic.Compiler/CodeGen/CodeGenerator.Units.cs:23
- record `ListingSymbol` — One named offset (a bound runtime label or a module data slot) in a . — PowerBasic.Compiler/CodeGen/CodeGenerator.Units.cs:26
- record `ListingInfo` — A read-only, post-emission snapshot of the compiled image for the --list — PowerBasic.Compiler/CodeGen/CodeGenerator.Units.cs:33
- method `SignatureOf` — PowerBasic.Compiler/CodeGen/CodeGenerator.Units.cs:54
- method `if(this.IsBackendRouted(proc))` — PowerBasic.Compiler/CodeGen/CodeGenerator.Units.cs:144
- method `ImportOf` — PowerBasic.Compiler/CodeGen/CodeGenerator.Units.cs:231
- method `switch` — PowerBasic.Compiler/CodeGen/CodeGenerator.Units.cs:247
- method `if(value < codeLength)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Units.cs:251

### CodeGenerator.Vendor.cs  `C#, 409 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.Vendor.cs:4
- class `CodeGenerator` — Vendor-corpus wave: BIT statements, EXIT FAR, ARRAY SORT/SCAN. — PowerBasic.Compiler/CodeGen/CodeGenerator.Vendor.cs:8
- method `if(scalar.ByteSize == 1)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Vendor.cs:45
- method `if(scalar.ByteSize == 4)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Vendor.cs:49
- method `if(scalar.ByteSize == 1)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Vendor.cs:55
- method `if(scalar.ByteSize == 4)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Vendor.cs:59
- method `if(scalar.ByteSize == 1)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Vendor.cs:63
- method `if(scalar.ByteSize == 4)` — PowerBasic.Compiler/CodeGen/CodeGenerator.Vendor.cs:67

### CodeGenerator.cs  `C#, 4402 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:7
- class `CodeGenerator` — Translates a bound program into a 16-bit real-mode DOS executable. — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:19
- class `ForRangeScope` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:136
- method `Dispose()` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:138
- method `IsModifiedIn(iff.Then, v, model)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:162
- method `if(this._forRanges.TryGetValue(v, out var r))` — a FOR-counter range wins (it is the exact loop bound); otherwise the interval lattice — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:207
- method `if(this.IndexRangeOf(b.Left) is { } la && this.IndexRangeOf(b.Right) is…` — both operands range-known (e.g. a(i+j) over two counters/derived vars): the — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:216
- method `if(this.IndexRangeOf(b.Left) is { } lm && this.OptFolder.TryFold(b.Righ…` — scaling by a constant (strided access a(i*2)) - the endpoints flip when k < 0 — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:226
- method `if(this.IndexRangeOf(b.Right) is { } rm2 && this.OptFolder.TryFold(b.Le…` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:228
- method `if(this.OptFolder.TryFold(b.Right) is { Integer: { } am } && am >= 0)` — x AND m (m a non-negative constant): the result keeps only m's bits, so it is in — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:234
- method `return(0, am)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:235
- method `if(this.OptFolder.TryFold(b.Left) is { Integer: { } am2 } && am2 >= 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:236
- method `return(0, am2)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:237
- method `Fits(named)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:324
- method `Fits(bound)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:330
- method `if(b.Op == BinaryOp.Xor)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:527
- method `ValueFacts(new Interval(other.Lo, other.Hi), KnownBits.Unknown, Congruence.Unkn…` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:692
- method `if(WritesCounter(a.Target, counter, model) || !CallFree(a.Value, model)…` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:708
- method `if(WritesCounter(id.Target, counter, model) || (id.Amount != null && !C…` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:713
- method `if((p.FileNumber != null && !CallFree(p.FileNumber, model)) || p.Items.…` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:717
- method `if(!CallFree(iff.Condition, model) || !CounterStableInBody(iff.Then, co…` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:722
- method `if(!CallFree(sel.Subject, model) || sel.Arms.Any(arm => !CounterStableI…` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:728
- method `if(this.IsBackendRouted(proc))` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:1038
- method `if(body[j] is LabelStmt)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:1246
- method `if(body[j] is not AssignStmt { Value: BinaryExpr { Op: { } op } } candi…` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:1248
- method `if(this._remainderReuse?.Contains(candidate) == true)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:1253
- method `if(!this.IsSharedDivModPair(producer, candidate, out var divideIsFirst))` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:1255
- method `if(divideIsFirst)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:1259
- method `if(bytes > resource.Length)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:1595
- enum `ValueKind` — Evaluation-register category. (QUAD) values — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:1637
- method `if(this._remainderStash?.TryGetValue(a, out var stashSlot) == true)` — O0079 separated form: the IDIV just left the remainder in DX and a later MOD wants it. — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:1706
- method `if(this._trackResume && l.Name.All(char.IsAsciiDigit) && int.TryParse(l…` — ERL bookkeeping: numeric line labels only (PB: labels do not count) — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:1733
- method `if(e.ExitCode != null)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:1782
- method `if(this._unreachableDeferred?.Contains(deferred) == true)` — Text on a line control can never arrive at is discarded, which is the whole point of — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:1935
- method `if(ps.Color is { } col)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:1964
- method `if(this.OptimizeSpeed)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:1987
- method `if(rq.Message is { Length: > 0 } msg)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:1993
- method `foreach(var v in dim.Variables)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:2016
- method `if(symbol == null)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:2018
- method `if(symbol.IsArray)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:2020
- method `if(!result.Contains(symbol))` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:2024
- method `if(symbol.Type is StringType or FlexType)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:2042
- method `if(cmd.Arguments.Count == 2 && cmd.Arguments[1] is { } loadOffset)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:2154
- method `if(cmd.Arguments.Count >= 1 && cmd.Arguments[0] is { } row)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:2242
- method `if(cmd.Arguments.Count >= 2 && cmd.Arguments[1] is { } column)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:2247
- method `if(cmd.Arguments.Count == 0 || cmd.Arguments[0] is not { } seed)` — No argument: the BIOS tick counter, which rt_randomize reads and stores. It moved out of — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:2267
- method `if(cmd.Arguments.Count >= 1 && cmd.Arguments[0] is { } sleepArg)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:2303
- method `foreach(var argument in cmd.Arguments)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:2355
- method `if(argument != null)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:2356
- method `if(KindOf(model.TypeOf(argument)) == ValueKind.Str)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:2358
- method `foreach(var s in i.Then)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:2772
- method `foreach(var s in i.Else)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:2782
- method `if(referenced.Contains(label.Name))` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:2871
- method `if(Transfers(statement))` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:2878
- method `IsSubject(Expression e)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:2924
- method `IsConst(Expression e)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:2925
- method `AddArm` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:2930
- method `if(SameOperand(thenValue, right) && SameOperand(elseValue, left))` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3036
- method `if(SameOperand(m, right) && SameOperand(thenValue, left))` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3046
- method `if(constantStep is { } cs16)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3192
- method `if(cs16 >= 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3195
- method `if(stepSign == 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3214
- method `if(stepSign >= 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3218
- method `if(stepSign == 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3228
- method `if(stepSign <= 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3230
- method `if(stepSign == 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3244
- method `if(stepSign >= 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3252
- method `if(stepSign == 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3260
- method `if(stepSign <= 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3262
- method `if(isByte)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3373
- method `if(this.CheckNumeric)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3395
- method `if(this.CheckNumeric)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3401
- method `if` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3568
- method `if(!(kind is ValueKind.Int16 or ValueKind.Int32 && this.Optimize && thi…` — O0099: an arm listing several point values in a <=16-wide window (CASE 1, 3, 5, 9) tests — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3574
- method `foreach(var selector in arm.Selectors)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3576
- method `if(selector.Value == null)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3577
- method `switch(kind)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3581
- method `if(elseArm != null)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3630
- method `if(sel.Value == null || sel.RangeUpper != null || sel.IsComparison != n…` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3636
- method `if(kind == ValueKind.Int16)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3638
- method `if(this.OptFolder.TryFold(sel.Value) is not { Integer: { } v } || v is …` — Int32: values must be compile-time constants in LONG range — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3644
- method `if(elseArm != null)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3763
- method `if(sel.Value == null || sel.RangeUpper != null || sel.IsComparison != n…` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3769
- method `if(this.OptFolder.TryFold(sel.Value) is not { Integer: { } v } || v is …` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3771
- method `if(elseArm != null)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3871
- method `if(sel.Value == null || sel.RangeUpper != null || sel.IsComparison != n…` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3877
- method `if(this.OptFolder.TryFold(sel.Value) is not { Integer: { } v } || v is …` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3879
- method `Tree` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3906
- method `Tree(lo, mid - 1)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3917
- method `Tree(mid + 1, hi)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3919
- method `Tree(lo, mid - 1)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3922
- method `Tree(mid + 1, hi)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:3925
- method `Collect` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:4054
- method `if(name is not NameExpr n || model.IntrinsicBindings.ContainsKey(n) || …` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:4060
- method `if(keyVar == null)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:4064
- method `if(!model.VariableBindings.TryGetValue(keyVar, out var ksym) || !Refere…` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:4066
- method `if(this.OptFolder.TryFold(valueExpr) is not { Integer: { } v } || v is …` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:4068
- method `CompareSubjectWith` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:4143
- method `if(id.Increment)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:4303
- method `if(id.Increment)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:4310
- method `if(net == 1)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:4333
- method `if(net == -1)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:4335
- method `if(net != 0)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:4337
- method `if(id.Increment)` — PowerBasic.Compiler/CodeGen/CodeGenerator.cs:4366

### InlineAsmCanonicalizer.cs  `C#, 46 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/InlineAsmCanonicalizer.cs:2
- class `InlineAsmCanonicalizer` — Removes register-only inline-assembly instructions whose architectural result is provably identical — PowerBasic.Compiler/CodeGen/InlineAsmCanonicalizer.cs:11

### InlineAsmScheduler.cs  `C#, 261 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/InlineAsmScheduler.cs:1
- class `InlineAsmScheduler` — pb36 inline-assembly instruction scheduler: reorders a block of consecutive single-instruction — PowerBasic.Compiler/CodeGen/InlineAsmScheduler.cs:16
- record `Instr` — PowerBasic.Compiler/CodeGen/InlineAsmScheduler.cs:20
- method `ScheduleByDependency` — PowerBasic.Compiler/CodeGen/InlineAsmScheduler.cs:43
- method `if(conflicts(i, j))` — PowerBasic.Compiler/CodeGen/InlineAsmScheduler.cs:66
- method `if(pick < 0 || (touchesMemory(c) == lastTouchedMemory && touchesMemory(…` — PowerBasic.Compiler/CodeGen/InlineAsmScheduler.cs:82
- method `if(--indeg[k] == 0)` — PowerBasic.Compiler/CodeGen/InlineAsmScheduler.cs:90
- method `MemMayAlias(a.MemKey, b.MemKey)` — PowerBasic.Compiler/CodeGen/InlineAsmScheduler.cs:106
- method `ApplyOperand` — PowerBasic.Compiler/CodeGen/InlineAsmScheduler.cs:140
- method `if(isRead || IsByteReg(op))` — PowerBasic.Compiler/CodeGen/InlineAsmScheduler.cs:146
- method `if(isWrite)` — PowerBasic.Compiler/CodeGen/InlineAsmScheduler.cs:148
- method `foreach(var r in ExtractRegisters(op))` — PowerBasic.Compiler/CodeGen/InlineAsmScheduler.cs:157
- method `if(!IsPlainName(op))` — PowerBasic.Compiler/CodeGen/InlineAsmScheduler.cs:164
- method `Instr` — PowerBasic.Compiler/CodeGen/InlineAsmScheduler.cs:181
- record `Shape` — mnemonic -> (operand count, per-operand read/write, flag effects). LEA/MOV* read their source — PowerBasic.Compiler/CodeGen/InlineAsmScheduler.cs:187

### IntervalRange.cs  `C#, 684 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:4
- record `Interval` — O16 interval lattice: a signed-integer value range [Lo, Hi]. is the full — PowerBasic.Compiler/CodeGen/IntervalRange.cs:14
- method `Hull(checked(this.Lo * o.Lo), checked(this.Lo * o.Hi), checked(this.Hi * …` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:47
- method `Hull(checked(this.Lo / o.Lo), checked(this.Lo / o.Hi), checked(this.Hi / …` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:57
- method `Magnitude(long value)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:69
- record `ValueFacts` — What the analysis knows about one integer value: a range, known one/zero bits, and an affine — PowerBasic.Compiler/CodeGen/IntervalRange.cs:91
- class `IntervalRangeAnalysis` — O16 forward value propagation over a bound statement list. Every tracked integer carries a — PowerBasic.Compiler/CodeGen/IntervalRange.cs:123
- record `Scope` — The analysis context: the bound model plus whether this body contains anything that can — PowerBasic.Compiler/CodeGen/IntervalRange.cs:138
- method `IntVar(t, model)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:219
- method `IntVar(t, model)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:223
- method `CallFree(iff.Condition, model)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:233
- method `RefineForCondition(thenEnv, iff.Condition, whenTrue: true, model)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:236
- method `Run(iff.Then, thenEnv, scope, points)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:237
- method `foreach(var (cond, b) in iff.ElseIfs)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:239
- method `RefineForCondition(e, cond, whenTrue: true, model)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:241
- method `Run(b, e, scope, points)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:242
- method `RefineForCondition(elseEnv, iff.Condition, whenTrue: false, model)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:246
- method `if(iff.Else != null)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:247
- method `Run(iff.Else, elseEnv, scope, points)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:248
- method `IntVar(f.Variable, model)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:253
- method `TransferLoop(f, f.Body, ctr, range, env, scope, points)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:256
- method `when(d.PreCondition == null || CallFree(d.PreCondition, model))` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:259
- method `TransferLoop(d, d.Body, null, Interval.Top, env, scope, points)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:261
- method `CallFree(sel.Subject, model)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:263
- method `foreach(var arm in sel.Arms)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:267
- method `if(subject != null && arm.Selectors.Count > 0)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:269
- method `RefineForSelectors(armEnv, subject, arm.Selectors, model)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:270
- method `Run(arm.Body, armEnv, scope, points)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:271
- method `if(!sel.Arms.Any(a => a.Selectors.Count == 0))` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:274
- method `when(p.FileNumber == null || CallFree(p.FileNumber, model))` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:279
- method `if(scope.Jumps)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:284
- method `KillReachableByCall(s, env, model)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:290
- method `IntVar(n, model)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:362
- method `if(width > 0)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:391
- method `if(fitted.IsTop && !IsPowerOfTwo(mod.Modulus))` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:429
- method `RefineForCondition(env, and2.Left, whenTrue: true, model)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:442
- method `RefineForCondition(env, and2.Right, whenTrue: true, model)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:443
- method `RefineForCondition(env, or2.Left, whenTrue: false, model)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:446
- method `RefineForCondition(env, or2.Right, whenTrue: false, model)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:447
- method `RefineForCondition(env, negated, !whenTrue, model)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:450
- method `SetRange(exit, counter, counterRange)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:561
- method `CallFree(a.Value, model)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:616
- method `CallFree(iff.Condition, model)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:620
- method `CallFree(f.From, model)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:625
- method `when(p.FileNumber == null || CallFree(p.FileNumber, model))` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:627
- method `if(width > 0 && kv.Key.Type is ScalarType { Signed: var signed })` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:675
- method `if(!joined.IsUnknown)` — PowerBasic.Compiler/CodeGen/IntervalRange.cs:677

### KnownBits.cs  `C#, 245 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/KnownBits.cs:1
- record `KnownBits` — O16 bit lattice: which bits of a value are provably 1 and which are provably 0. It answers the — PowerBasic.Compiler/CodeGen/KnownBits.cs:19
- method `Of(0, width)` — PowerBasic.Compiler/CodeGen/KnownBits.cs:130
- record `Congruence` — O16 congruence lattice: v = Residue (mod Modulus) - the domain that knows — PowerBasic.Compiler/CodeGen/KnownBits.cs:153
- method `new(0, residue)` — PowerBasic.Compiler/CodeGen/KnownBits.cs:176
- method `Of(this.Residue + other)` — PowerBasic.Compiler/CodeGen/KnownBits.cs:210
- method `Of(this.Residue * o.Residue)` — PowerBasic.Compiler/CodeGen/KnownBits.cs:226
- method `Make(this.Modulus * o.Residue, this.Residue * o.Residue)` — PowerBasic.Compiler/CodeGen/KnownBits.cs:228
- method `Make(o.Modulus * this.Residue, o.Residue * this.Residue)` — PowerBasic.Compiler/CodeGen/KnownBits.cs:230

### OptCommonSubexpr.cs  `C#, 1001 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:4
- class `OptCommonSubexpr` — pb36 O3 - block-local common subexpression elimination (docs/PB36.md). A — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:27
- record `CseMark` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:28
- class `Result` — Analysis result: which AST nodes to define/reload, and how many 4-byte slots the frame must reserve. — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:32
- class `LicmResult` — pb36 LICM analysis result: a hoistable loop-invariant subexpression, the — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:45
- method `if(cond != null)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:118
- method `FindLicmIn(cond, written, firstSlot, slotOfKey, varId, result, model)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:119
- method `IsBodyFlatStraightLine(i.Then)` — an IF/SELECT is analyzable when every nested block is - its writes are — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:145
- method `if(ScalarSymbolOfStatic(a.Target, model) is { } sym)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:162
- method `if(StringVarSymbol(a.Target, model) is { } strSym)` — O0180: reassigning a string changes its LEN, so a retained length cache reading it — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:166
- method `if(a.Target is CallOrIndexExpr && model.VariableBindings.TryGetValue(a.…` — an array-element write touches a cached array read (redundant-load — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:171
- method `if(ScalarSymbolOfStatic(id.Target, model) is { } isym)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:176
- method `if(id.Target is CallOrIndexExpr && model.VariableBindings.TryGetValue(i…` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:178
- method `CollectWrites(i.Then, written, model)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:183
- method `foreach(var (_, elseBody) in i.ElseIfs)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:184
- method `CollectWrites(elseBody, written, model)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:185
- method `if(i.Else != null)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:186
- method `CollectWrites(i.Else, written, model)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:187
- method `foreach(var arm in sel.Arms)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:190
- method `CollectWrites(arm.Body, written, model)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:191
- method `IsBarrierFree(a.Value, model)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:285
- method `FindLicmIn(a.Value, written, firstSlot, slotOfKey, varId, result, model)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:286
- method `if(a.Target is CallOrIndexExpr { Arguments: { } args })` — array index expressions on the target are also emitted and can be hoisted — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:288
- method `FindLicmIn(arg, written, firstSlot, slotOfKey, varId, result, model)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:291
- method `FindLicmIn(id.Amount, written, firstSlot, slotOfKey, varId, result, model)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:294
- method `if(p.FileNumber is { } fn && IsBarrierFree(fn, model))` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:297
- method `FindLicmIn(fn, written, firstSlot, slotOfKey, varId, result, model)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:298
- method `foreach(var item in p.Items)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:299
- method `FindLicmIn(v, written, firstSlot, slotOfKey, varId, result, model)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:301
- method `IsBarrierFree(i.Condition, model)` — an IF's FIRST condition is evaluated on every pass (unconditionally), so its — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:306
- method `FindLicmIn(i.Condition, written, firstSlot, slotOfKey, varId, result, model)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:307
- method `IsBarrierFree(sel.Subject, model)` — likewise a SELECT's subject is evaluated unconditionally — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:310
- method `FindLicmIn(sel.Subject, written, firstSlot, slotOfKey, varId, result, model)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:311
- method `IsModularInt16Tree(b.Left, model, depth + 1)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:395
- method `if(divisor is null or 0)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:414
- method `IsHoistableSafely(b.Left, model)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:416
- method `IsHoistableSafely(u.Operand, model)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:421
- method `if(!varId.TryGetValue(sym, out var id))` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:449
- method `AppendLicmKey(sb, u.Operand, varId, model)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:457
- method `AppendLicmKey(sb, b.Left, varId, model)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:462
- method `AppendLicmKey(sb, b.Right, varId, model)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:463
- method `CacheableLenSymbol(e, model)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:466
- enum `Mode` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:494
- class `State` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:496
- method `Run` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:511
- method `RunInheriting(IReadOnlyList<Statement> statements, Dictionary<string, Expression> …` — Runs a block starting from an inherited live cache (the dominating code's still-valid values). — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:515
- method `foreach(var statement in statements)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:520
- method `Walk` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:527
- method `if(a.Target is CallOrIndexExpr { Arguments: { } targetArgs })` — index expressions on an array target are emitted via the normal path — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:535
- method `if(p.FileNumber is { } fn)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:542
- method `foreach(var item in p.Items)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:544
- method `if(id.Amount is { } amount)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:551
- method `IsBarrierFree` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:555
- method `foreach(var (_, elseIfBody) in iff.ElseIfs)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:571
- method `if(iff.Else != null)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:573
- method `foreach(var (_, elseIfBody) in iff.ElseIfs)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:576
- method `if(iff.Else != null)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:578
- method `IsBarrierFree` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:582
- method `foreach(var arm in sel.Arms)` — a SELECT join behaves like an IF merge: the subject is evaluated once and — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:588
- method `CollectWrites(f.Body, loopWrites, model)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:603
- method `if(model.VariableBindings.TryGetValue(f.Variable, out var counterSym))` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:604
- method `foreach(var symbol in loopWrites)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:606
- method `CollectWrites(d.Body, loopWrites, model)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:616
- method `foreach(var symbol in loopWrites)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:617
- method `foreach(var block in ChildBlocks(statement))` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:626
- method `Register(Expression e, Mode mode)` — Registers every cacheable subtree of bottom-up, marking define/use pairs. — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:635
- method `IsCacheable(Expression e, Mode mode)` — A composite worth a slot: an integer-typed pure tree, or (modular mode) a float-typed +,-,* tree ov… — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:660
- method `FoldsToConstant(Expression e)` — True when every leaf is a compile-time constant, so the emitter folds the whole subtree away. — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:685
- method `IsModularInt16Tree(Expression e, int depth = 0)` — Replicates CodeGenerator.IsModularInt16Tree: a +,-,* (and unary negate) tree over 16-bit-or-narrowe… — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:694
- method `IsModularAssign(AssignStmt a)` — Exactly the EmitAssign condition that routes a store through EmitModularInt16. — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:708
- method `SlotFor` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:713
- method `EndRun()` — Ends the current live CSE run. A top-level run has no saved dominating cache, — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:729
- method `InvalidateAfterWrite` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:736
- method `if(StringVarSymbol(target, model) is { } strSym)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:743
- method `if(target is CallOrIndexExpr && model.VariableBindings.TryGetValue(targ…` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:745
- method `Invalidate` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:749
- method `RetainPastMerge(List<IReadOnlyList<Statement>> branches)` — Broader GVN: flow the inherited cache PAST the IF merge. A value computed — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:767
- method `CollectWrites(branch, written, model)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:778
- method `IsRetainableBranch(IReadOnlyList<Statement> body)` — A branch whose writes are fully captured by : only — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:788
- method `switch(s)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:790
- method `when` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:794
- method `when` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:798
- method `SelectorsBarrierFree(CaseArm arm)` — A CASE arm whose selector expressions are all call-free, so evaluating them (the — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:814
- method `if(sel.RangeUpper != null && !IsBarrierFree(sel.RangeUpper, model))` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:818
- method `IsStraightLineSafe` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:823
- method `IsStraightLinePrint` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:840
- method `ScalarSymbolOf` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:845
- method `Key` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:850
- method `AppendKey` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:856
- method `CacheableArrayReadSymbol(e, model)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:879
- method `foreach(var arg in ((CallOrIndexExpr)e).Arguments)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:881
- method `CacheableLenSymbol(e, model)` — O0180: LEN(strVar) keyed by the string symbol - two reads of the same unmodified string match — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:886
- method `IdOf` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:894
- method `IsBarrierFree(u.Operand, model)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:934
- method `IsBarrierFree(bv.Value, model)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:938
- method `IsBarrierFree(f.Number, model)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:940
- method `CacheableLenSymbol(e, model)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:944
- method `Collect` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:957
- method `Collect(child)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:966
- method `foreach(var (_, body) in i.ElseIfs)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:983
- method `if(i.Else != null)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:985
- method `foreach(var arm in sel.Arms)` — PowerBasic.Compiler/CodeGen/OptCommonSubexpr.cs:989

### OptCopyProp.cs  `C#, 80 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/OptCopyProp.cs:4
- class `OptCopyProp` — pb36 copy propagation over the SSA. A copy y = x (the right-hand side a bare — PowerBasic.Compiler/CodeGen/OptCopyProp.cs:17
- method `foreach(var r in copyReads)` — PowerBasic.Compiler/CodeGen/OptCopyProp.cs:61

### OptDeadGlobals.cs  `C#, 281 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/OptDeadGlobals.cs:5
- class `OptDeadGlobals` — pb36 O23 data tree-shaking: a module scalar global that no reachable code ever — PowerBasic.Compiler/CodeGen/OptDeadGlobals.cs:28
- record `Result` — PowerBasic.Compiler/CodeGen/OptDeadGlobals.cs:29
- method `foreach(var v in dim.Variables)` — PowerBasic.Compiler/CodeGen/OptDeadGlobals.cs:55
- method `foreach` — PowerBasic.Compiler/CodeGen/OptDeadGlobals.cs:73
- method `ScanBody(body, model, dead, candidates, read, disqualified, stores, checkingP…` — PowerBasic.Compiler/CodeGen/OptDeadGlobals.cs:75
- method `if(stores.TryGetValue(v, out var list))` — PowerBasic.Compiler/CodeGen/OptDeadGlobals.cs:85
- method `if` — PowerBasic.Compiler/CodeGen/OptDeadGlobals.cs:88
- method `new` — PowerBasic.Compiler/CodeGen/OptDeadGlobals.cs:97
- method `Reach` — PowerBasic.Compiler/CodeGen/OptDeadGlobals.cs:136
- method `if(model.CallBindings.TryGetValue(node, out var callee))` — PowerBasic.Compiler/CodeGen/OptDeadGlobals.cs:144
- method `Reach(callee)` — PowerBasic.Compiler/CodeGen/OptDeadGlobals.cs:145
- method `if(node is LambdaExpr lambda && model.LambdaProcs.TryGetValue(lambda, o…` — PowerBasic.Compiler/CodeGen/OptDeadGlobals.cs:146
- method `Reach(lifted)` — PowerBasic.Compiler/CodeGen/OptDeadGlobals.cs:147
- method `foreach(var node in OptReachability.DescendantNodes(statement))` — PowerBasic.Compiler/CodeGen/OptDeadGlobals.cs:157
- method `foreach(var v in candidates)` — PowerBasic.Compiler/CodeGen/OptDeadGlobals.cs:181
- method `if` — PowerBasic.Compiler/CodeGen/OptDeadGlobals.cs:191
- method `if(!stores.TryGetValue(sym, out var list))` — PowerBasic.Compiler/CodeGen/OptDeadGlobals.cs:195
- method `if(!dead.Contains(sym))` — the RHS still counts as reads for whatever globals it mentions, EXCEPT a CODEPTR/call — PowerBasic.Compiler/CodeGen/OptDeadGlobals.cs:205
- method `MarkOccurrence(node, model, candidates, read)` — PowerBasic.Compiler/CodeGen/OptDeadGlobals.cs:207
- method `foreach` — PowerBasic.Compiler/CodeGen/OptDeadGlobals.cs:210
- method `MarkOccurrence(node, model, candidates, read)` — PowerBasic.Compiler/CodeGen/OptDeadGlobals.cs:212
- method `IsSideEffectFreeRhs(bv.Value, model, checkingPossible)` — PowerBasic.Compiler/CodeGen/OptDeadGlobals.cs:274

### OptFloatDemotion.cs  `C#, 544 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:4
- class `OptFloatDemotion` — pb36 O12 - float demotion ("de-floating", docs/PB36.md). PB defaults bare — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:26
- class `Candidate` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:30
- method `Observe` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:36
- class `State` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:78
- method `AllBodies` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:83
- method `if(proc.Body is { } body)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:87
- method `Collect` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:90
- method `foreach(var symbol in proc.Variables.Values)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:95
- method `foreach(var parameter in proc.Parameters)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:97
- method `Consider` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:101
- method `BindingOf` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:109
- method `CandidateOf` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:112
- method `BlockAll` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:115
- method `Walk(IReadOnlyList<Statement> statements)` — Walks one statement list; false = unhandled construct seen (caller aborts the pass). — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:122
- method `WalkStatement` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:128
- method `if(this.CandidateOf(id.Target) is { } incrTarget)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:147
- method `if(id.Amount is { } amount)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:150
- method `if(d.PreCondition is { } pre)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:158
- method `if(d.PostCondition is { } post)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:160
- method `if(!this.Walk(i.Then))` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:166
- method `foreach(var (condition, body) in i.ElseIfs)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:168
- method `if(!this.Walk(body))` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:170
- method `if(p.FileNumber is { } fn)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:180
- method `foreach(var item in p.Items)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:183
- method `if(item.Value is { } value)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:184
- method `foreach(var argument in c.Arguments)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:194
- method `foreach(var target in TargetsOf(statement))` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:200
- method `if(w.FileNumber is { } wfn)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:210
- method `foreach(var item in w.Items)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:212
- method `if(m.Length is { } len)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:220
- method `if(aa.Index is { } aaIndex)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:227
- method `if(this.CandidateOf(b.Target) is { } bitTarget)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:238
- method `foreach(var e in ExpressionsOf(statement))` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:251
- method `foreach(var e in new[] { asrt.Count, asrt.FromPos, asrt.ToPos, asrt.Collate …` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:257
- method `foreach(var e in new[] { ascn.Count, ascn.FromPos, ascn.ToPos, ascn.Collate,…` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:264
- method `if(o.RecordLength is { } rl)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:274
- method `if(gp.RecordNumber is { } gpPos)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:280
- method `if(gp.Variable is { } target)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:282
- method `foreach(var (width, target) in fl.Fields)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:294
- method `if(ds.Segment is { } seg)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:312
- method `if(so.Value is { } soValue)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:317
- method `foreach(var argument in cmd.Arguments)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:322
- method `if(ln.From is { } lf)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:329
- method `foreach(var e in new[] { ln.Color, ln.Style })` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:335
- method `foreach(var e in new[] { ci.Color, ci.Start, ci.End, ci.Aspect })` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:345
- method `if(ps.Color is { } psc)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:354
- method `if(gg.To is { } gt)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:362
- method `WalkAssign` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:373
- method `WalkFor` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:392
- method `if(f.Step is { } st)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:397
- method `WalkSelect` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:428
- method `foreach(var selector in arm.Selectors)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:433
- method `if(e != null)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:435
- method `if(!this.Walk(arm.Body))` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:440
- method `Safe(Expression e)` — Value-exactness check for one expression tree: candidate reads inside — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:453
- method `TreeIsValueExact` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:461
- method `ContainsCandidate` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:481
- method `BlockContained` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:495
- method `foreach(var argument in c.Arguments)` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:508
- method `foreach(var child in AstQuery.Subexpressions(e))` — unmodeled node: block every candidate nested inside it (conservative). — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:516
- method `TargetsOf` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:521
- method `ExpressionsOf` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:528
- method `DeclBounds` — PowerBasic.Compiler/CodeGen/OptFloatDemotion.cs:534

### OptInlining.cs  `C#, 100 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/OptInlining.cs:3
- class `OptInlining` — pb36 O6 reachability support: which procedures the emitter will inline at — PowerBasic.Compiler/CodeGen/OptInlining.cs:21

### OptIpcp.cs  `C#, 208 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/OptIpcp.cs:3
- class `OptIpcp` — pb36 O18 - interprocedural constant propagation. A scalar parameter that — PowerBasic.Compiler/CodeGen/OptIpcp.cs:19
- method `for(var i = args.Count; i < proc.Parameters.Count; ++i)` — PowerBasic.Compiler/CodeGen/OptIpcp.cs:50
- method `if(proc.Parameters[i].DefaultValue is not { } d)` — PowerBasic.Compiler/CodeGen/OptIpcp.cs:51
- method `if(!allDefaulted)` — PowerBasic.Compiler/CodeGen/OptIpcp.cs:57
- method `if(poison[i])` — PowerBasic.Compiler/CodeGen/OptIpcp.cs:69
- method `if(folder.TryFold(args[i]) is { } folded && (folded.Integer.HasValue ||…` — PowerBasic.Compiler/CodeGen/OptIpcp.cs:71
- method `if(poison[i] || slots[i] is not { } constant)` — PowerBasic.Compiler/CodeGen/OptIpcp.cs:87
- method `if(parameter.Type is not ScalarType)` — PowerBasic.Compiler/CodeGen/OptIpcp.cs:90
- method `if(WritesParameter(proc.Body!, model, parameter))` — PowerBasic.Compiler/CodeGen/OptIpcp.cs:92
- method `ExprMightWrite` — PowerBasic.Compiler/CodeGen/OptIpcp.cs:120
- method `foreach(var arg in call.Arguments)` — a user FUNCTION call: the parameter passed plainly is a BYREF write hazard — PowerBasic.Compiler/CodeGen/OptIpcp.cs:127
- method `if(ExprMightWrite(arg))` — PowerBasic.Compiler/CodeGen/OptIpcp.cs:130
- method `ExprMightWrite(u.Operand)` — PowerBasic.Compiler/CodeGen/OptIpcp.cs:137
- method `ExprMightWrite(v.Value)` — PowerBasic.Compiler/CodeGen/OptIpcp.cs:141
- method `ExprMightWrite(f.Number)` — PowerBasic.Compiler/CodeGen/OptIpcp.cs:143
- method `StatementWrites` — PowerBasic.Compiler/CodeGen/OptIpcp.cs:150
- method `IsParam(id.Target)` — PowerBasic.Compiler/CodeGen/OptIpcp.cs:156
- method `IsParam(b.Target)` — PowerBasic.Compiler/CodeGen/OptIpcp.cs:160
- method `if(WritesParameter(block, model, parameter))` — PowerBasic.Compiler/CodeGen/OptIpcp.cs:180
- method `foreach(var (_, body) in i.ElseIfs)` — PowerBasic.Compiler/CodeGen/OptIpcp.cs:190
- method `if(i.Else != null)` — PowerBasic.Compiler/CodeGen/OptIpcp.cs:192
- method `foreach(var arm in sel.Arms)` — PowerBasic.Compiler/CodeGen/OptIpcp.cs:196

### OptLoopFusion.cs  `C#, 168 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/OptLoopFusion.cs:3
- class `OptLoopFusion` — O0062 loop fusion: two adjacent FOR loops over the SAME counter with identical bounds, — PowerBasic.Compiler/CodeGen/OptLoopFusion.cs:20
- method `IsCounterIndex` — PowerBasic.Compiler/CodeGen/OptLoopFusion.cs:96
- method `Read` — PowerBasic.Compiler/CodeGen/OptLoopFusion.cs:99
- method `if(s.Type is ArrayType)` — PowerBasic.Compiler/CodeGen/OptLoopFusion.cs:108
- method `Read(u.Operand)` — PowerBasic.Compiler/CodeGen/OptLoopFusion.cs:115
- method `Read(b.Left)` — PowerBasic.Compiler/CodeGen/OptLoopFusion.cs:118
- method `Read(val)` — PowerBasic.Compiler/CodeGen/OptLoopFusion.cs:131
- method `Read(val)` — PowerBasic.Compiler/CodeGen/OptLoopFusion.cs:137

### OptPruner.cs  `C#, 326 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/OptPruner.cs:3
- class `OptPruner` — pb36 statement-level cleanups (docs/PB36.md O2/O10), applied to the bound — PowerBasic.Compiler/CodeGen/OptPruner.cs:27
- method `Resolve` — PowerBasic.Compiler/CodeGen/OptPruner.cs:53
- method `CollectGotoHops(block, hops)` — PowerBasic.Compiler/CodeGen/OptPruner.cs:70
- method `if(s is LabelStmt or DataStmt or EquateStmt or DefTypeStmt or MetaStmt)` — PowerBasic.Compiler/CodeGen/OptPruner.cs:75
- method `if(s is GotoStmt g && !g.Target.Equals(label.Name, StringComparison.Ord…` — PowerBasic.Compiler/CodeGen/OptPruner.cs:77
- method `foreach(var (_, b) in i.ElseIfs)` — PowerBasic.Compiler/CodeGen/OptPruner.cs:106
- method `if(i.Else != null)` — PowerBasic.Compiler/CodeGen/OptPruner.cs:108
- method `foreach(var arm in sel.Arms)` — PowerBasic.Compiler/CodeGen/OptPruner.cs:112
- method `if` — PowerBasic.Compiler/CodeGen/OptPruner.cs:172
- method `if` — PowerBasic.Compiler/CodeGen/OptPruner.cs:177
- method `if(seg.Segment != null && ObservesSegment(seg.Segment, model))` — PowerBasic.Compiler/CodeGen/OptPruner.cs:193
- method `if(pending >= 0)` — PowerBasic.Compiler/CodeGen/OptPruner.cs:198
- method `if` — PowerBasic.Compiler/CodeGen/OptPruner.cs:205
- method `Drop` — PowerBasic.Compiler/CodeGen/OptPruner.cs:253
- method `if(pendingLocate >= 0 && Covers(loc, (CommandStmt)body[pendingLocate]))` — PowerBasic.Compiler/CodeGen/OptPruner.cs:265
- method `Drop(ref pendingLocate, ref pendingCls, i, body, ref i)` — PowerBasic.Compiler/CodeGen/OptPruner.cs:266
- method `if(pendingCls >= 0)` — PowerBasic.Compiler/CodeGen/OptPruner.cs:273
- method `Drop(ref pendingCls, ref pendingLocate, i, body, ref i)` — PowerBasic.Compiler/CodeGen/OptPruner.cs:274
- method `if(pendingLocate >= 0)` — PowerBasic.Compiler/CodeGen/OptPruner.cs:275
- method `Drop(ref pendingLocate, ref pendingCls, i, body, ref i)` — PowerBasic.Compiler/CodeGen/OptPruner.cs:276
- method `if(!IsConsoleTransparent(body[i], model))` — PowerBasic.Compiler/CodeGen/OptPruner.cs:283

### OptPureFold.cs  `C#, 464 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:4
- class `OptPureFold` — pb36 O25 - automatic compile-time evaluation of pure functions. A FUNCTION the — PowerBasic.Compiler/CodeGen/OptPureFold.cs:24
- method `if(calls[proc].Any(callee => !candidates.Contains(callee)))` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:92
- method `if(seen.Add(p))` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:108
- method `if(d.Storage is not (StorageClass.Dim or StorageClass.Local) || d.Stati…` — plain local scalar declarations only - no SHARED/STATIC/PUBLIC/COMMON, no arrays, — PowerBasic.Compiler/CodeGen/OptPureFold.cs:133
- method `ExprIsPure(i.Condition, model, callees)` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:144
- method `ExprIsPure(sel.Subject, model, callees)` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:150
- method `IsLocalScalarTarget(f.Variable, model)` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:156
- method `return(d.PreCondition == null || ExprIsPure(d.PreCondition, model, callees))` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:160
- method `if(model.IntrinsicBindings.ContainsKey(call) || model.ProcPtrCalls.Cont…` — a call to another user FUNCTION is pure if that function is (recorded as a dependency); — PowerBasic.Compiler/CodeGen/OptPureFold.cs:204
- method `if(!model.CallBindings.TryGetValue(call, out var callee))` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:206
- class `Evaluator` — region interpreter — PowerBasic.Compiler/CodeGen/OptPureFold.cs:219
- method `Evaluate` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:222
- class `BailOut` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:232
- method `Call` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:234
- method `BailOut()` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:237
- method `BailOut()` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:245
- enum `Flow` — control-flow signal bubbling up from a block — PowerBasic.Compiler/CodeGen/OptPureFold.cs:253
- method `ExecBlock` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:254
- method `if(flow != Flow.Normal)` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:258
- method `Exec` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:263
- method `BailOut()` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:266
- method `switch` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:267
- method `if(this.Eval(i.Condition, env, depth) != 0)` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:285
- method `foreach(var (cond, body) in i.ElseIfs)` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:287
- method `foreach(var arm in sel.Arms)` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:295
- method `BailOut()` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:312
- method `ExecFor` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:315
- method `BailOut()` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:323
- method `if(flow == Flow.ExitFunction)` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:328
- method `if(flow == Flow.ExitLoop)` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:330
- method `if(++this._steps > StepBudget)` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:333
- method `BailOut()` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:334
- method `ExecDoLoop` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:338
- method `if(d.PreTest != LoopTestKind.None && d.PreCondition != null)` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:341
- method `if(d.PreTest == LoopTestKind.While ? !c : c)` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:343
- method `if(flow == Flow.ExitFunction)` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:347
- method `if(flow == Flow.ExitLoop)` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:349
- method `if(d.PostTest != LoopTestKind.None && d.PostCondition != null)` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:351
- method `if(d.PostTest == LoopTestKind.While ? !c : c)` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:353
- method `if(++this._steps > StepBudget)` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:356
- method `BailOut()` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:357
- method `SelectorMatches` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:361
- method `Store` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:380
- method `SymbolOf` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:385
- method `Eval` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:388
- method `if(args.Count > visible)` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:417
- method `BailOut()` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:418
- method `for(var i = 0; i < visible; ++i)` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:420
- method `BailOut()` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:425
- method `BailOut()` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:429
- method `Wrap` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:432
- method `EvalBinary` — PowerBasic.Compiler/CodeGen/OptPureFold.cs:435

### OptReachability.cs  `C#, 121 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/OptReachability.cs:6
- class `OptReachability` — pb36 O22 reachability - which procedures can actually run. A whole program's entry point — PowerBasic.Compiler/CodeGen/OptReachability.cs:24
- method `Reach` — PowerBasic.Compiler/CodeGen/OptReachability.cs:31
- method `Visit` — PowerBasic.Compiler/CodeGen/OptReachability.cs:36
- method `Reach(callee)` — PowerBasic.Compiler/CodeGen/OptReachability.cs:40
- method `if(node is LambdaExpr lambda && model.LambdaProcs.TryGetValue(lambda, o…` — PowerBasic.Compiler/CodeGen/OptReachability.cs:41
- method `Reach(lifted)` — PowerBasic.Compiler/CodeGen/OptReachability.cs:42
- method `if(node is Expression e && model.Desugared.TryGetValue(e, out var desug…` — a bind-time rewrite (member call/property access, string interpolation) is reached — PowerBasic.Compiler/CodeGen/OptReachability.cs:45
- method `Visit(desugared)` — PowerBasic.Compiler/CodeGen/OptReachability.cs:46
- method `if(node is Statement s && model.DesugaredStatements.TryGetValue(s, out …` — PowerBasic.Compiler/CodeGen/OptReachability.cs:47
- method `Visit(desugaredStmt)` — PowerBasic.Compiler/CodeGen/OptReachability.cs:48
- method `if(e is LambdaExpr)` — PowerBasic.Compiler/CodeGen/OptReachability.cs:67
- method `foreach(var prop in PropertiesOf(e.GetType()))` — PowerBasic.Compiler/CodeGen/OptReachability.cs:69
- method `foreach(var prop in PropertiesOf(s.GetType()))` — PowerBasic.Compiler/CodeGen/OptReachability.cs:78
- method `if(node is not null && node.GetType().Namespace == AstNamespace)` — PowerBasic.Compiler/CodeGen/OptReachability.cs:83
- method `PushFlattened(stack, tuple[i])` — PowerBasic.Compiler/CodeGen/OptReachability.cs:101
- method `foreach(var item in items)` — PowerBasic.Compiler/CodeGen/OptReachability.cs:104
- method `PushFlattened(stack, item)` — PowerBasic.Compiler/CodeGen/OptReachability.cs:105

### OptRegParm.cs  `C#, 50 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/OptRegParm.cs:3
- class `OptRegParm` — pb36 $OPTIMIZE SPEED - internal register parameter passing. For a procedure the — PowerBasic.Compiler/CodeGen/OptRegParm.cs:23

### RuntimeTrimmer.cs  `C#, 100 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/RuntimeTrimmer.cs:3
- class `RuntimeTrimmer` — pb36 runtime trimming (docs/PB36.md P1/P2/P4): a one-time probe emission of — PowerBasic.Compiler/CodeGen/RuntimeTrimmer.cs:14
- record `Section` — One runtime code/data section and the foreign labels its bytes reference. — PowerBasic.Compiler/CodeGen/RuntimeTrimmer.cs:17
- record `Analysis` — PowerBasic.Compiler/CodeGen/RuntimeTrimmer.cs:18
- method `CloseOver(IEnumerable<string> seedLabels)` — Closes (plus the entry stub's needs) over the section graph. — PowerBasic.Compiler/CodeGen/RuntimeTrimmer.cs:25
- method `while` — PowerBasic.Compiler/CodeGen/RuntimeTrimmer.cs:32
- method `if(!seen.Add(label))` — PowerBasic.Compiler/CodeGen/RuntimeTrimmer.cs:35
- method `if(!this.ProviderOf.TryGetValue(label, out var sectionName))` — PowerBasic.Compiler/CodeGen/RuntimeTrimmer.cs:37
- method `if(!needed.Add(sectionName))` — PowerBasic.Compiler/CodeGen/RuntimeTrimmer.cs:39
- method `foreach(var need in byName[sectionName].Needs)` — PowerBasic.Compiler/CodeGen/RuntimeTrimmer.cs:41
- method `if(name != "<entry>" && label.Position >= start && label.Position < end)` — PowerBasic.Compiler/CodeGen/RuntimeTrimmer.cs:74
- method `if(position >= start && position < end && providerOf.TryGetValue(target…` — PowerBasic.Compiler/CodeGen/RuntimeTrimmer.cs:86
- method `if` — PowerBasic.Compiler/CodeGen/RuntimeTrimmer.cs:90
- method `new` — PowerBasic.Compiler/CodeGen/RuntimeTrimmer.cs:96

### TargetCost.cs  `C#, 207 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/TargetCost.cs:1
- enum `CpuTier` — The microarchitecture floor a program is compiled for (the $CPU family). — PowerBasic.Compiler/CodeGen/TargetCost.cs:10
- enum `CostObjective` — What the optimizer is being asked to minimise (the $OPTIMIZE SIZE|SPEED objective). — PowerBasic.Compiler/CodeGen/TargetCost.cs:20
- class `TargetCost` — O0174 - the per-target cost model. An optimization is only an optimization on a particular machine, — PowerBasic.Compiler/CodeGen/TargetCost.cs:42

### ValueFactReduction.cs  `C#, 587 lines`
- namespace `PowerBasic.Compiler.CodeGen` — PowerBasic.Compiler/CodeGen/ValueFactReduction.cs:3
- class `ValueFactReduction` — Cross-domain reduction and transfer for O0016 value facts. Interval, known-bit and congruence — PowerBasic.Compiler/CodeGen/ValueFactReduction.cs:12
- method `if` — PowerBasic.Compiler/CodeGen/ValueFactReduction.cs:49
- method `new` — PowerBasic.Compiler/CodeGen/ValueFactReduction.cs:59
- method `for` — PowerBasic.Compiler/CodeGen/ValueFactReduction.cs:93
- method `for(var b = 0; b <= 1; ++b)` — PowerBasic.Compiler/CodeGen/ValueFactReduction.cs:96
- method `for(var carry = 0; carry <= 1; ++carry)` — PowerBasic.Compiler/CodeGen/ValueFactReduction.cs:98
- method `new` — PowerBasic.Compiler/CodeGen/ValueFactReduction.cs:111
- method `new(result.Ones | top, result.Zeros & ~top)` — PowerBasic.Compiler/CodeGen/ValueFactReduction.cs:141
- method `new(result.Ones & ~top, result.Zeros | top)` — PowerBasic.Compiler/CodeGen/ValueFactReduction.cs:143
- method `Compare(op, left, width, signed, right, width, signed, width)` — PowerBasic.Compiler/CodeGen/ValueFactReduction.cs:151
- method `ExactRotateCount(exactCount, width)` — PowerBasic.Compiler/CodeGen/ValueFactReduction.cs:195
- method `Disjoint(left.Range, right.Range)` — PowerBasic.Compiler/CodeGen/ValueFactReduction.cs:235
- method `Disjoint(left.Range, right.Range)` — PowerBasic.Compiler/CodeGen/ValueFactReduction.cs:236
- method `Bits` — PowerBasic.Compiler/CodeGen/ValueFactReduction.cs:347
- method `new(-sign, sign - 1)` — PowerBasic.Compiler/CodeGen/ValueFactReduction.cs:515

## PowerBasic.Compiler/CodeGen/Ssa/

### ControlFlowGraph.cs  `C#, 327 lines`
- namespace `PowerBasic.Compiler.CodeGen.Ssa` — PowerBasic.Compiler/CodeGen/Ssa/ControlFlowGraph.cs:2
- class `BasicBlock` — A basic block: a maximal straight-line run of statements ending in at most — PowerBasic.Compiler/CodeGen/Ssa/ControlFlowGraph.cs:12
- class `ControlFlowGraph` — A control-flow graph over a structured, acyclic region of a bound AST — PowerBasic.Compiler/CodeGen/Ssa/ControlFlowGraph.cs:55
- class `Builder` — PowerBasic.Compiler/CodeGen/Ssa/ControlFlowGraph.cs:91
- constructor `Builder` — PowerBasic.Compiler/CodeGen/Ssa/ControlFlowGraph.cs:101
- method `NewBlock` — PowerBasic.Compiler/CodeGen/Ssa/ControlFlowGraph.cs:103
- method `LinkUnconditional` — PowerBasic.Compiler/CodeGen/Ssa/ControlFlowGraph.cs:109
- method `LinkBranch` — PowerBasic.Compiler/CodeGen/Ssa/ControlFlowGraph.cs:111
- method `LinkOpaqueBranch(BasicBlock from, BasicBlock onTrue, BasicBlock onFalse)` — A two-way branch whose condition is not analyzable - both edges stay reachable. — PowerBasic.Compiler/CodeGen/Ssa/ControlFlowGraph.cs:119
- method `BuildSequence(IReadOnlyList<Statement> stmts, BasicBlock entry)` — Appends to the graph starting at — PowerBasic.Compiler/CodeGen/Ssa/ControlFlowGraph.cs:131
- method `if(this.Failed)` — PowerBasic.Compiler/CodeGen/Ssa/ControlFlowGraph.cs:134
- method `if(current == null)` — PowerBasic.Compiler/CodeGen/Ssa/ControlFlowGraph.cs:136
- method `BuildStatement` — PowerBasic.Compiler/CodeGen/Ssa/ControlFlowGraph.cs:142
- method `BuildIf` — PowerBasic.Compiler/CodeGen/Ssa/ControlFlowGraph.cs:192
- method `if(armExit != null)` — PowerBasic.Compiler/CodeGen/Ssa/ControlFlowGraph.cs:210
- method `if` — PowerBasic.Compiler/CodeGen/Ssa/ControlFlowGraph.cs:214
- method `if(elseExit != null)` — PowerBasic.Compiler/CodeGen/Ssa/ControlFlowGraph.cs:217
- method `BuildFor` — PowerBasic.Compiler/CodeGen/Ssa/ControlFlowGraph.cs:224
- method `BuildDoLoop` — PowerBasic.Compiler/CodeGen/Ssa/ControlFlowGraph.cs:257
- method `if` — PowerBasic.Compiler/CodeGen/Ssa/ControlFlowGraph.cs:272
- method `if(d.PreTest == LoopTestKind.Until)` — PowerBasic.Compiler/CodeGen/Ssa/ControlFlowGraph.cs:275
- method `BuildSelect` — PowerBasic.Compiler/CodeGen/Ssa/ControlFlowGraph.cs:289
- method `foreach(var selector in arm.Selectors)` — PowerBasic.Compiler/CodeGen/Ssa/ControlFlowGraph.cs:301
- method `if(selector.RangeUpper != null)` — PowerBasic.Compiler/CodeGen/Ssa/ControlFlowGraph.cs:304
- method `if(armExit != null)` — PowerBasic.Compiler/CodeGen/Ssa/ControlFlowGraph.cs:311
- method `ComputePredecessors` — PowerBasic.Compiler/CodeGen/Ssa/ControlFlowGraph.cs:319

### DeadStore.cs  `C#, 95 lines`
- namespace `PowerBasic.Compiler.CodeGen.Ssa` — PowerBasic.Compiler/CodeGen/Ssa/DeadStore.cs:3
- class `DeadStore` — SSA dead-store elimination (docs/PB36.md O2/O17). Removes assignments to — PowerBasic.Compiler/CodeGen/Ssa/DeadStore.cs:20
- method `MarkLive(version)` — PowerBasic.Compiler/CodeGen/Ssa/DeadStore.cs:48
- method `foreach(var (_, input) in v.PhiInputs)` — PowerBasic.Compiler/CodeGen/Ssa/DeadStore.cs:54
- method `MarkLive(input)` — PowerBasic.Compiler/CodeGen/Ssa/DeadStore.cs:55
- method `if(v.IncrBase != null)` — PowerBasic.Compiler/CodeGen/Ssa/DeadStore.cs:58
- method `MarkLive(v.IncrBase)` — PowerBasic.Compiler/CodeGen/Ssa/DeadStore.cs:59
- method `if(candidateRhsReads.TryGetValue(v, out var reads))` — keeping this assignment emits its RHS, so its real reads come alive — PowerBasic.Compiler/CodeGen/Ssa/DeadStore.cs:63
- method `MarkLive(rv)` — PowerBasic.Compiler/CodeGen/Ssa/DeadStore.cs:66

### DominatorTree.cs  `C#, 116 lines`
- namespace `PowerBasic.Compiler.CodeGen.Ssa` — PowerBasic.Compiler/CodeGen/Ssa/DominatorTree.cs:1
- class `DominatorTree` — Immediate dominators and dominance frontiers over a , — PowerBasic.Compiler/CodeGen/Ssa/DominatorTree.cs:10
- method `ReferenceEquals(a, runner)` — PowerBasic.Compiler/CodeGen/Ssa/DominatorTree.cs:32
- method `if(visited.Add(succ.Current))` — PowerBasic.Compiler/CodeGen/Ssa/DominatorTree.cs:46
- method `if(ReferenceEquals(block, cfg.Entry))` — PowerBasic.Compiler/CodeGen/Ssa/DominatorTree.cs:68
- method `foreach(var pred in block.Predecessors)` — PowerBasic.Compiler/CodeGen/Ssa/DominatorTree.cs:71
- method `if(newIdom != null && (!idom.TryGetValue(block, out var current) || !Re…` — PowerBasic.Compiler/CodeGen/Ssa/DominatorTree.cs:76
- method `if(!idom.ContainsKey(pred))` — PowerBasic.Compiler/CodeGen/Ssa/DominatorTree.cs:92
- method `for(var runner = pred; !ReferenceEquals(runner, bIdom); runner = idom[ru…` — PowerBasic.Compiler/CodeGen/Ssa/DominatorTree.cs:94
- method `if(ReferenceEquals(runner, idom[runner]))` — PowerBasic.Compiler/CodeGen/Ssa/DominatorTree.cs:96

### RegisterAllocation.cs  `C#, 84 lines`
- namespace `PowerBasic.Compiler.CodeGen.Ssa` — PowerBasic.Compiler/CodeGen/Ssa/RegisterAllocation.cs:2
- enum `AllocReg` — The callee-stable index registers an 8086 allocation may use (SI/DI are the only GP registers our i… — PowerBasic.Compiler/CodeGen/Ssa/RegisterAllocation.cs:6
- class `RegisterAllocation` — A graph-coloring register allocator over the — PowerBasic.Compiler/CodeGen/Ssa/RegisterAllocation.cs:24
- method `if(live.Interferes(v, other))` — PowerBasic.Compiler/CodeGen/Ssa/RegisterAllocation.cs:62
- method `if(!taken.Contains(r))` — PowerBasic.Compiler/CodeGen/Ssa/RegisterAllocation.cs:67
- method `new` — PowerBasic.Compiler/CodeGen/Ssa/RegisterAllocation.cs:76

### ScalarLiveness.cs  `C#, 238 lines`
- namespace `PowerBasic.Compiler.CodeGen.Ssa` — PowerBasic.Compiler/CodeGen/Ssa/ScalarLiveness.cs:3
- class `ScalarLiveness` — Backward live-variable analysis over a for the — PowerBasic.Compiler/CodeGen/Ssa/ScalarLiveness.cs:26
- method `foreach(var succ in block.Successors)` — PowerBasic.Compiler/CodeGen/Ssa/ScalarLiveness.cs:100
- method `foreach(var v in outSet)` — PowerBasic.Compiler/CodeGen/Ssa/ScalarLiveness.cs:105
- method `foreach(var v in gen[block])` — PowerBasic.Compiler/CodeGen/Ssa/ScalarLiveness.cs:108
- method `new` — PowerBasic.Compiler/CodeGen/Ssa/ScalarLiveness.cs:118
- method `if(tracked.Contains(u) && !kill.Contains(u))` — PowerBasic.Compiler/CodeGen/Ssa/ScalarLiveness.cs:135
- method `if(tracked.Contains(u) && !kill.Contains(u))` — PowerBasic.Compiler/CodeGen/Ssa/ScalarLiveness.cs:144
- method `if(tracked.Contains(u))` — PowerBasic.Compiler/CodeGen/Ssa/ScalarLiveness.cs:165
- method `foreach(var v in live)` — PowerBasic.Compiler/CodeGen/Ssa/ScalarLiveness.cs:173
- method `if` — PowerBasic.Compiler/CodeGen/Ssa/ScalarLiveness.cs:175
- method `if(tracked.Contains(u))` — PowerBasic.Compiler/CodeGen/Ssa/ScalarLiveness.cs:179
- method `VarsOf(a.Value, model)` — PowerBasic.Compiler/CodeGen/Ssa/ScalarLiveness.cs:201
- method `VarsOf(p.FileNumber, model)` — PowerBasic.Compiler/CodeGen/Ssa/ScalarLiveness.cs:209

### Sccp.cs  `C#, 271 lines`
- namespace `PowerBasic.Compiler.CodeGen.Ssa` — PowerBasic.Compiler/CodeGen/Ssa/Sccp.cs:4
- class `Sccp` — Sparse conditional constant propagation over — PowerBasic.Compiler/CodeGen/Ssa/Sccp.cs:18
- enum `State` — PowerBasic.Compiler/CodeGen/Ssa/Sccp.cs:19
- record `Lat` — PowerBasic.Compiler/CodeGen/Ssa/Sccp.cs:20
- field `Top` — PowerBasic.Compiler/CodeGen/Ssa/Sccp.cs:22
- field `Bottom` — PowerBasic.Compiler/CodeGen/Ssa/Sccp.cs:23
- method `Of(long v)` — PowerBasic.Compiler/CodeGen/Ssa/Sccp.cs:24
- method `Meet(Lat other)` — Lattice meet: Top is identity, Bottom absorbs, unequal constants drop to Bottom. — PowerBasic.Compiler/CodeGen/Ssa/Sccp.cs:27
- method `if(!this._reachableBlocks.Contains(block))` — PowerBasic.Compiler/CodeGen/Ssa/Sccp.cs:74
- method `foreach(var value in this._byBlock[block])` — re-evaluate this block's values (phis first by construction order) — PowerBasic.Compiler/CodeGen/Ssa/Sccp.cs:77
- method `if(!updated.Equals(this._values[value]))` — PowerBasic.Compiler/CodeGen/Ssa/Sccp.cs:79
- method `foreach(var (pred, input) in value.PhiInputs)` — PowerBasic.Compiler/CodeGen/Ssa/Sccp.cs:97
- method `if(baseLat.State != State.Const)` — PowerBasic.Compiler/CodeGen/Ssa/Sccp.cs:105
- method `if(amount is not { } step)` — PowerBasic.Compiler/CodeGen/Ssa/Sccp.cs:108
- method `if(this.InputState(value.DefExpr!) is { } pending)` — PowerBasic.Compiler/CodeGen/Ssa/Sccp.cs:116
- method `if(folded is not { } v)` — PowerBasic.Compiler/CodeGen/Ssa/Sccp.cs:119
- method `IntegerLiteralExpr(name.Position, this._values[version].Value, TypeSuffix.None)` — PowerBasic.Compiler/CodeGen/Ssa/Sccp.cs:190
- method `foreach(var r in TrackedReads(u.Operand))` — PowerBasic.Compiler/CodeGen/Ssa/Sccp.cs:208
- method `foreach(var r in TrackedReads(b.Left))` — PowerBasic.Compiler/CodeGen/Ssa/Sccp.cs:212
- method `foreach(var r in TrackedReads(b.Right))` — PowerBasic.Compiler/CodeGen/Ssa/Sccp.cs:214
- method `foreach(var r in TrackedReads(t.Condition))` — PowerBasic.Compiler/CodeGen/Ssa/Sccp.cs:218
- method `foreach(var r in TrackedReads(t.WhenTrue))` — PowerBasic.Compiler/CodeGen/Ssa/Sccp.cs:220
- method `foreach(var r in TrackedReads(t.WhenFalse))` — PowerBasic.Compiler/CodeGen/Ssa/Sccp.cs:222

### SsaForm.cs  `C#, 528 lines`
- namespace `PowerBasic.Compiler.CodeGen.Ssa` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:3
- enum `SsaDefKind` — How an obtains its value. — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:7
- class `SsaValue` — A single static-single-assignment version of a tracked scalar variable. — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:19
- class `SsaForm` — Static single assignment form over an acyclic structured region — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:58
- method `if(DefinesIn(block, v, model))` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:101
- method `foreach(var df in dom.FrontierOf(b))` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:107
- method `if(seenDef.Add(df))` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:110
- method `Read` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:149
- method `if(model.VariableBindings.TryGetValue(name, out var sym))` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:155
- method `Consider(sym, candidates, escaped)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:156
- method `if(dangerous)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:157
- method `Read(u.Operand, dangerous)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:162
- method `Read(b.Left, dangerous)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:165
- method `Read(b.Right, dangerous)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:166
- method `foreach(var a in call.Arguments)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:169
- method `Read(a, true)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:170
- method `Read(index.Target, true)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:173
- method `foreach(var a in index.Arguments)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:174
- method `Read(a, true)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:175
- method `Read(m.Target, true)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:178
- method `Read(p.Pointer, true)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:181
- method `Read(p.Index, true)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:182
- method `Read(v.Value, true)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:185
- method `Read(am.Value, true)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:188
- method `Read(f.Number, true)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:191
- method `Read(t.Condition, dangerous)` — a ternary only reads (one branch executes, but both read the same — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:197
- method `Read(t.WhenTrue, dangerous)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:198
- method `Read(t.WhenFalse, dangerous)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:199
- method `foreach(var child in AstQuery.Subexpressions(e))` — any expression node this pass does not model explicitly (e.g. a new — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:206
- method `Read(child, true)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:207
- method `switch(s)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:214
- method `Consider(model, a.Target, candidates, escaped)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:217
- method `Read` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:218
- method `Read(a.Value, false)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:220
- method `if(id.Target is NameExpr)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:223
- method `Consider(model, id.Target, candidates, escaped)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:224
- method `Read` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:225
- method `Read(id.Amount, false)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:227
- method `Read(p.FileNumber, false)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:230
- method `Read(p.UsingFormat, false)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:231
- method `foreach(var item in p.Items)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:232
- method `Read(item.Value, false)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:233
- method `foreach(var e in StatementExpressions(s))` — any other statement is opaque to scalar tracking: escape every — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:238
- method `Read(e, true)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:239
- method `Read(extra, false)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:244
- method `foreach(var a in c.Arguments)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:278
- method `foreach(var a in cmd.Arguments)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:282
- method `foreach(var v in dim.Variables)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:323
- class `Renamer` — Standard dominator-tree SSA renaming with per-variable version stacks. — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:335
- constructor `Renamer` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:349
- method `Run` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:359
- method `if(!ReferenceEquals(idom, block))` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:369
- method `foreach(var b in blocks)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:377
- method `NewValue` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:388
- method `Top` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:394
- method `Rename` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:396
- method `foreach` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:399
- method `foreach` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:404
- method `foreach` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:407
- method `foreach(var phi in this._blockPhis[succ])` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:414
- method `foreach` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:416
- method `RenameStatement` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:423
- method `foreach(var item in p.Items)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:461
- method `foreach(var e in StatementExpressions(stmt))` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:466
- method `IsTracked` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:471
- method `RecordUses(Expression? e)` — Records the reaching version for every tracked-variable read in an expression. — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:480
- method `foreach(var a in call.Arguments)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:495
- method `foreach(var a in index.Arguments)` — PowerBasic.Compiler/CodeGen/Ssa/SsaForm.cs:500

## PowerBasic.Compiler/Emit/

### IrBasicWriter.cs  `C#, 861 lines`
- namespace `PowerBasic.Compiler.Emit` — PowerBasic.Compiler/Emit/IrBasicWriter.cs:5
- class `IrBasicWriterException` — Raised when the IR contains something this writer cannot render as PowerBASIC. — PowerBasic.Compiler/Emit/IrBasicWriter.cs:9
- class `IrBasicWriter` — Renders an back to PowerBASIC source - a back end that targets BASIC itself. — PowerBasic.Compiler/Emit/IrBasicWriter.cs:41
- method `Walk` — PowerBasic.Compiler/Emit/IrBasicWriter.cs:200
- method `Walk(successor)` — PowerBasic.Compiler/Emit/IrBasicWriter.cs:205
- method `if(alloca.Count <= 1 && !this.IsByteBlob(alloca))` — a multi-element slot is declared by the first subscript that names it (ArrayElement), so — PowerBasic.Compiler/Emit/IrBasicWriter.cs:325
- method `if(store.Value is IrNullPtr && store.Pointer.Type.Kind == IrTypeKind.Pt…` — A string slot is null-initialised at entry so the handle it replaces is readable. In BASIC — PowerBasic.Compiler/Emit/IrBasicWriter.cs:335
- method `IrBasicWriterException($"a truncation to {cast.Type.Bits} bits")` — PowerBasic.Compiler/Emit/IrBasicWriter.cs:413
- method `IrBasicWriterException("a phi with no entry for one of its predecessors")` — PowerBasic.Compiler/Emit/IrBasicWriter.cs:484
- method `IrBasicWriterException("an alloca holding more than one element used without a subscript")` — PowerBasic.Compiler/Emit/IrBasicWriter.cs:539
- method `IrBasicWriterException("an alloca whose address is itself stored")` — PowerBasic.Compiler/Emit/IrBasicWriter.cs:543
- method `IrBasicWriterException( $"a TYPE field read at two widths ({field.Type} and {type}) - overl…` — PowerBasic.Compiler/Emit/IrBasicWriter.cs:761
- method `IrBasicWriterException("an indirect call")` — PowerBasic.Compiler/Emit/IrBasicWriter.cs:819
- method `IrBasicWriterException($"a call to the runtime routine {callee.Name}")` — PowerBasic.Compiler/Emit/IrBasicWriter.cs:853

### Linker.cs  `C#, 189 lines`
- namespace `PowerBasic.Compiler.Emit` — PowerBasic.Compiler/Emit/Linker.cs:1
- record `LinkedImage` — One linked artifact ready for the MZ writer. — PowerBasic.Compiler/Emit/Linker.cs:4
- class `LinkException` — Raised when symbol resolution fails or signatures mismatch. — PowerBasic.Compiler/Emit/Linker.cs:7
- class `Linker` — Resolves a main image's imports against explicitly linked units and — PowerBasic.Compiler/Emit/Linker.cs:15
- method `if(!bySensitive.TryAdd(export.Name, (unit, export)))` — PowerBasic.Compiler/Emit/Linker.cs:47
- method `if(!unit.Foreign && !byInsensitive.TryAdd(export.Name, (unit, export)))` — PowerBasic.Compiler/Emit/Linker.cs:49
- method `if(Resolve(import.Name) != null)` — PowerBasic.Compiler/Emit/Linker.cs:66
- method `if(provider == null)` — foreign OMF .LIBs resolve lazily: convert only the member that defines the symbol — PowerBasic.Compiler/Emit/Linker.cs:71
- method `if(provider == null && !this._crtSupportPulled && this._omfLibraries.Co…` — last resort: a C-startup-provided symbol a linked CRT routine references but never — PowerBasic.Compiler/Emit/Linker.cs:76
- method `if(provider == null)` — PowerBasic.Compiler/Emit/Linker.cs:80
- method `Index(provider)` — PowerBasic.Compiler/Emit/Linker.cs:83
- method `if(Resolve(import.Name) is not { } found)` — PowerBasic.Compiler/Emit/Linker.cs:91
- method `if(found.Export.SignatureHash != import.SignatureHash && found.Export.S…` — hash 0 on either side = unchecked (runtime symbols and asm-level references) — PowerBasic.Compiler/Emit/Linker.cs:100
- method `LinkException($"signature mismatch for {import.Name}: {unit.Name} expects a differ…` — PowerBasic.Compiler/Emit/Linker.cs:101
- method `switch(fixup.Kind)` — PowerBasic.Compiler/Emit/Linker.cs:143
- method `if(fixup.Target >= unit.Imports.Count)` — PowerBasic.Compiler/Emit/Linker.cs:157
- method `LinkException($"fixup in {unit.Name} references import #{fixup.Target} of {unit.Im…` — PowerBasic.Compiler/Emit/Linker.cs:158
- method `if(fixup.Kind == PbuFixupKind.ImportOffset)` — PowerBasic.Compiler/Emit/Linker.cs:162
- method `LinkException($"unknown fixup kind {fixup.Kind} in {unit.Name}")` — PowerBasic.Compiler/Emit/Linker.cs:171

### Listing.cs  `C#, 111 lines`
- namespace `PowerBasic.Compiler.Emit` — PowerBasic.Compiler/Emit/Listing.cs:6
- class `Listing` — Renders a human-readable listing (.LST) of a compiled program: a map — PowerBasic.Compiler/Emit/Listing.cs:16
- method `foreach` — PowerBasic.Compiler/Emit/Listing.cs:76
- method `foreach` — PowerBasic.Compiler/Emit/Listing.cs:84

### MzExeWriter.cs  `C#, 123 lines`
- namespace `PowerBasic.Compiler.Emit` — PowerBasic.Compiler/Emit/MzExeWriter.cs:1
- record `MzRelocation` — A single MZ relocation: the load-time segment:offset of a word the DOS loader adjusts by the start … — PowerBasic.Compiler/Emit/MzExeWriter.cs:4
- class `MzExeWriter` — Writes a standard DOS MZ executable: header, relocation table (padded to a — PowerBasic.Compiler/Emit/MzExeWriter.cs:11

### PblFile.cs  `C#, 78 lines`
- namespace `PowerBasic.Compiler.Emit` — PowerBasic.Compiler/Emit/PblFile.cs:2
- class `PblFile` — A unit library (.PBL): a table-of-contents over concatenated — PowerBasic.Compiler/Emit/PblFile.cs:10
- method `InvalidDataException("not a PBL1 library file")` — PowerBasic.Compiler/Emit/PblFile.cs:56
- method `InvalidDataException($"unsupported PBL version {version}")` — PowerBasic.Compiler/Emit/PblFile.cs:59

### PbuFile.cs  `C#, 152 lines`
- namespace `PowerBasic.Compiler.Emit` — PowerBasic.Compiler/Emit/PbuFile.cs:2
- enum `PbuExportKind` — Export kind inside a compiled unit. — PowerBasic.Compiler/Emit/PbuFile.cs:6
- enum `PbuFixupKind` — Relocation kinds inside a unit's code image (see docs/FORMATS.md). — PowerBasic.Compiler/Emit/PbuFile.cs:9
- record `PbuExport` — PowerBasic.Compiler/Emit/PbuFile.cs:21
- record `PbuImport` — PowerBasic.Compiler/Emit/PbuFile.cs:23
- record `PbuCommonBlock` — PowerBasic.Compiler/Emit/PbuFile.cs:25
- record `PbuFixup` — A relocation in a unit's image. is relative to the unit's — PowerBasic.Compiler/Emit/PbuFile.cs:35
- enum `PbuCpuFlags` — CPU/feature requirement flags of a unit. — PowerBasic.Compiler/Emit/PbuFile.cs:38
- class `PbuFile` — A compiled unit ($COMPILE UNIT) in PB-Compiler's own documented — PowerBasic.Compiler/Emit/PbuFile.cs:46
- method `InvalidDataException("not a PBU1 unit file")` — PowerBasic.Compiler/Emit/PbuFile.cs:117
- method `InvalidDataException($"unsupported PBU version {version}")` — PowerBasic.Compiler/Emit/PbuFile.cs:120
- method `InvalidDataException($"name too long: {value}")` — PowerBasic.Compiler/Emit/PbuFile.cs:145

### PowerBasic35Emitter.cs  `C#, 1275 lines`
- namespace `PowerBasic.Compiler.Emit` — PowerBasic.Compiler/Emit/PowerBasic35Emitter.cs:5
- class `PowerBasic35Emitter` — Renders a bound program back to readable, PB 3.5-compatible PowerBASIC source - a "back-emitter" — PowerBasic.Compiler/Emit/PowerBasic35Emitter.cs:24
- method `for(var i = 0; i < call.Arguments.Count; i++)` — PowerBasic.Compiler/Emit/PowerBasic35Emitter.cs:475
- method `if(!s.ResumeNext)` — PowerBasic.Compiler/Emit/PowerBasic35Emitter.cs:584
- method `Test` — PowerBasic.Compiler/Emit/PowerBasic35Emitter.cs:874
- method `Paren(parentPrec, 7, $"{this.Expr(x.Left, 7)} * {factor}")` — PowerBasic.Compiler/Emit/PowerBasic35Emitter.cs:1069
- method `if(proc.Parameters[i].DefaultValue is { } d)` — PowerBasic.Compiler/Emit/PowerBasic35Emitter.cs:1118

## PowerBasic.Compiler/Emit/Omf/

### CrtSupport.cs  `C#, 54 lines`
- namespace `PowerBasic.Compiler.Emit.Omf` — PowerBasic.Compiler/Emit/Omf/CrtSupport.cs:1
- class `CrtSupport` — Synthetic definitions for the handful of C-startup-provided symbols a CRT — PowerBasic.Compiler/Emit/Omf/CrtSupport.cs:26

### Demangle.cs  `C#, 278 lines`
- namespace `PowerBasic.Compiler.Emit.Omf` — PowerBasic.Compiler/Emit/Omf/Demangle.cs:1
- enum `MangleScheme` — The compiler scheme a mangled C++ public symbol was produced by. — PowerBasic.Compiler/Emit/Omf/Demangle.cs:4
- record `Demangled` — The outcome of demangling: the recognised scheme and a readable signature. — PowerBasic.Compiler/Emit/Omf/Demangle.cs:20
- class `Demangle` — Demangler for C++ public symbols as the period DOS C++ compilers decorate free — PowerBasic.Compiler/Emit/Omf/Demangle.cs:45
- method `new(MangleScheme.None, symbol ?? "", symbol ?? "", false)` — PowerBasic.Compiler/Emit/Omf/Demangle.cs:54

### OmfLibrary.cs  `C#, 59 lines`
- namespace `PowerBasic.Compiler.Emit.Omf` — PowerBasic.Compiler/Emit/Omf/OmfLibrary.cs:1
- class `OmfLibrary` — A foreign OMF library (.LIB) presented to the for lazy, — PowerBasic.Compiler/Emit/Omf/OmfLibrary.cs:12

### OmfLibraryWriter.cs  `C#, 152 lines`
- namespace `PowerBasic.Compiler.Emit.Omf` — PowerBasic.Compiler/Emit/Omf/OmfLibraryWriter.cs:1
- class `OmfLibraryWriter` — Emits an OMF library (.LIB) - the archive counterpart to (docs/LINKER.md). — PowerBasic.Compiler/Emit/Omf/OmfLibraryWriter.cs:24
- method `OmfException("could not lay out the OMF library dictionary")` — PowerBasic.Compiler/Emit/Omf/OmfLibraryWriter.cs:80
- method `if(blocks[block][bucket] == 0)` — PowerBasic.Compiler/Emit/Omf/OmfLibraryWriter.cs:97

### OmfModule.cs  `C#, 55 lines`
- namespace `PowerBasic.Compiler.Emit.Omf` — PowerBasic.Compiler/Emit/Omf/OmfModule.cs:1
- class `OmfSegment` — A segment defined by an OMF module (SEGDEF). — PowerBasic.Compiler/Emit/Omf/OmfModule.cs:4
- record `OmfPublic` — A public symbol the module exports (PUBDEF): segment-relative offset. — PowerBasic.Compiler/Emit/Omf/OmfModule.cs:22
- enum `OmfTargetKind` — How a fixup's target is named. — PowerBasic.Compiler/Emit/Omf/OmfModule.cs:25
- enum `OmfLocation` — FIXUPP location type (the LOC field). is a near 16-bit offset; — PowerBasic.Compiler/Emit/Omf/OmfModule.cs:33
- record `OmfFixup` — A relocation (FIXUPP): patch the location at inside segment — PowerBasic.Compiler/Emit/Omf/OmfModule.cs:43
- class `OmfModule` — A parsed OMF object module (one .OBJ, or one member of a .LIB). — PowerBasic.Compiler/Emit/Omf/OmfModule.cs:46

### OmfReader.cs  `C#, 306 lines`
- namespace `PowerBasic.Compiler.Emit.Omf` — PowerBasic.Compiler/Emit/Omf/OmfReader.cs:1
- class `OmfException` — Raised when an OMF object/library cannot be parsed. — PowerBasic.Compiler/Emit/Omf/OmfReader.cs:4
- class `OmfReader` — Parser for 16-bit Intel OMF object modules (.OBJ) - the format every DOS-era C, — PowerBasic.Compiler/Emit/Omf/OmfReader.cs:12
- method `for(var bucket = 0; bucket < 37; ++bucket)` — PowerBasic.Compiler/Emit/Omf/OmfReader.cs:88
- method `if(pageToMember.TryGetValue(page, out var member))` — PowerBasic.Compiler/Emit/Omf/OmfReader.cs:101
- method `OmfException("truncated OMF record header")` — PowerBasic.Compiler/Emit/Omf/OmfReader.cs:120
- method `OmfException($"OMF record 0x{type:X2} overruns the buffer")` — PowerBasic.Compiler/Emit/Omf/OmfReader.cs:126
- method `switch` — PowerBasic.Compiler/Emit/Omf/OmfReader.cs:129
- method `while(this._pos < bodyEnd)` — PowerBasic.Compiler/Emit/Omf/OmfReader.cs:144
- method `OmfException($"LxDATA references segment {segIdx} of {m.Segments.Count}")` — PowerBasic.Compiler/Emit/Omf/OmfReader.cs:294

### OmfToPbu.cs  `C#, 156 lines`
- namespace `PowerBasic.Compiler.Emit.Omf` — PowerBasic.Compiler/Emit/Omf/OmfToPbu.cs:1
- class `OmfToPbu` — Lowers a parsed to a synthetic so the — PowerBasic.Compiler/Emit/Omf/OmfToPbu.cs:16
- method `EmitOffset(f, blob, site, unitOffset, inData)` — PowerBasic.Compiler/Emit/Omf/OmfToPbu.cs:105
- method `EmitSegment(blob, site, unitOffset, inData)` — PowerBasic.Compiler/Emit/Omf/OmfToPbu.cs:108
- method `EmitOffset(f, blob, site, unitOffset, inData)` — PowerBasic.Compiler/Emit/Omf/OmfToPbu.cs:111
- method `EmitSegment(blob, site + 2, unitOffset + 2, inData)` — PowerBasic.Compiler/Emit/Omf/OmfToPbu.cs:112

### OmfWriter.cs  `C#, 130 lines`
- namespace `PowerBasic.Compiler.Emit.Omf` — PowerBasic.Compiler/Emit/Omf/OmfWriter.cs:1
- class `OmfWriter` — Emits a as a 16-bit Intel OMF object module (.OBJ) - the inverse of — PowerBasic.Compiler/Emit/Omf/OmfWriter.cs:22
- method `if(f.Offset > (uint)pos && f.Offset < (uint)(pos + len) && f.Offset + 2…` — PowerBasic.Compiler/Emit/Omf/OmfWriter.cs:71
- method `foreach(var f in inChunk)` — PowerBasic.Compiler/Emit/Omf/OmfWriter.cs:79
- field `fixdat` — FIXDAT: frame=SEGDEF(method 0), P=1 (no displacement), target=EXTDEF(method 2) — PowerBasic.Compiler/Emit/Omf/OmfWriter.cs:102
- field `segFixdat` — FIXDAT: frame=SEGDEF(method 0), P=1, target=SEGDEF(method 0); frame and target are that segment — PowerBasic.Compiler/Emit/Omf/OmfWriter.cs:106

## PowerBasic.Compiler/Ir/

### CEmitter.cs  `C#, 590 lines`
- namespace `PowerBasic.Compiler.Ir` — PowerBasic.Compiler/Ir/CEmitter.cs:3
- class `CEmitter` — Emits portable C99 from the optimized IR - the second consumer of the middle end, alongside — PowerBasic.Compiler/Ir/CEmitter.cs:28
- method `Emit(module)` — PowerBasic.Compiler/Ir/CEmitter.cs:96
- method `Goto(IrBasicBlock t)` — PowerBasic.Compiler/Ir/CEmitter.cs:245
- method `if(inst is not IrPhi && !(inst is IrCmp ic && this._inlinedCmps.Contain…` — PowerBasic.Compiler/Ir/CEmitter.cs:274
- method `if(inst is IrAlloca a)` — PowerBasic.Compiler/Ir/CEmitter.cs:284
- method `if(inst.Type.Kind != IrTypeKind.Void && !(inst is IrCmp ic && this._inl…` — PowerBasic.Compiler/Ir/CEmitter.cs:287
- method `if(_notInTheCRuntime.Contains(callee))` — PowerBasic.Compiler/Ir/CEmitter.cs:350
- method `EmitDeclinedException( $"C emission: a call to {callee}, which runtime/pbc_rt.c has no ent…` — PowerBasic.Compiler/Ir/CEmitter.cs:351
- method `if(callee is "memcpy" or "memset" && args.Count == 4)` — PowerBasic.Compiler/Ir/CEmitter.cs:354
- method `if(!ReferenceEquals(b.Target, this._nextBlock))` — PowerBasic.Compiler/Ir/CEmitter.cs:367
- method `ReferenceEquals` — PowerBasic.Compiler/Ir/CEmitter.cs:370
- method `ReferenceEquals` — PowerBasic.Compiler/Ir/CEmitter.cs:378
- method `foreach(var (value, target) in s.Cases)` — PowerBasic.Compiler/Ir/CEmitter.cs:397

### EmitDeclinedException.cs  `C#, 43 lines`
- namespace `PowerBasic.Compiler.Ir` — PowerBasic.Compiler/Ir/EmitDeclinedException.cs:1
- class `EmitDeclinedException` — A construct one of the IR back ends (, ) has no — PowerBasic.Compiler/Ir/EmitDeclinedException.cs:25

### IrArgument.cs  `C#, 17 lines`
- namespace `PowerBasic.Compiler.Ir` — PowerBasic.Compiler/Ir/IrArgument.cs:1
- class `IrArgument` — A formal parameter of an , usable as an operand inside its body. — PowerBasic.Compiler/Ir/IrArgument.cs:4

### IrBasicBlock.cs  `C#, 81 lines`
- namespace `PowerBasic.Compiler.Ir` — PowerBasic.Compiler/Ir/IrBasicBlock.cs:1
- class `IrBasicBlock` — A maximal straight-line run of instructions ending in exactly one terminator. — PowerBasic.Compiler/Ir/IrBasicBlock.cs:8

### IrBuilder.cs  `C#, 84 lines`
- namespace `PowerBasic.Compiler.Ir` — PowerBasic.Compiler/Ir/IrBuilder.cs:1
- class `IrBuilder` — A cursor that appends instructions to a basic block. It keeps construction — PowerBasic.Compiler/Ir/IrBuilder.cs:8

### IrCloner.cs  `C#, 149 lines`
- namespace `PowerBasic.Compiler.Ir` — PowerBasic.Compiler/Ir/IrCloner.cs:1
- class `IrCloner` — Deep-clones a connected set of basic blocks into a function, remapping every — PowerBasic.Compiler/Ir/IrCloner.cs:25
- method `if(operand is IrBlockAddress)` — PowerBasic.Compiler/Ir/IrCloner.cs:106

### IrConstFold.cs  `C#, 217 lines`
- namespace `PowerBasic.Compiler.Ir` — PowerBasic.Compiler/Ir/IrConstFold.cs:1
- class `IrConstFold` — Pure evaluation of an instruction whose operands are all constants. Integer ops — PowerBasic.Compiler/Ir/IrConstFold.cs:10
- method `FoldFloat(b, fl.Value, fr.Value)` — PowerBasic.Compiler/Ir/IrConstFold.cs:34
- method `if(r.Value == 0 || (l.Value == long.MinValue && r.Value == -1))` — PowerBasic.Compiler/Ir/IrConstFold.cs:48
- method `if(r.Value == 0 || (l.Value == long.MinValue && r.Value == -1))` — PowerBasic.Compiler/Ir/IrConstFold.cs:53
- method `if(Unsigned(r) == 0)` — PowerBasic.Compiler/Ir/IrConstFold.cs:58
- method `if(Unsigned(r) == 0)` — PowerBasic.Compiler/Ir/IrConstFold.cs:63
- method `if(r.Value < 0 || r.Value >= t.Bits)` — PowerBasic.Compiler/Ir/IrConstFold.cs:68
- method `if(r.Value < 0 || r.Value >= t.Bits)` — PowerBasic.Compiler/Ir/IrConstFold.cs:73
- method `if(r.Value < 0 || r.Value >= t.Bits)` — PowerBasic.Compiler/Ir/IrConstFold.cs:78
- method `if(!double.IsFinite(value))` — PowerBasic.Compiler/Ir/IrConstFold.cs:111
- method `if((l - (value - back)) + (addend - back) != 0.0)` — PowerBasic.Compiler/Ir/IrConstFold.cs:115
- method `if(!double.IsFinite(value) || Math.FusedMultiplyAdd(l, r, -value) != 0.…` — PowerBasic.Compiler/Ir/IrConstFold.cs:121
- method `if(r == 0.0)` — PowerBasic.Compiler/Ir/IrConstFold.cs:125
- method `if(!double.IsFinite(value) || Math.FusedMultiplyAdd(value, r, -l) != 0.…` — PowerBasic.Compiler/Ir/IrConstFold.cs:128

### IrConstant.cs  `C#, 63 lines`
- namespace `PowerBasic.Compiler.Ir` — PowerBasic.Compiler/Ir/IrConstant.cs:1
- class `IrConstant` — A compile-time constant operand. — PowerBasic.Compiler/Ir/IrConstant.cs:4
- class `IrConstantInt` — An integer constant. The is stored as a 64-bit two's-complement — PowerBasic.Compiler/Ir/IrConstant.cs:11
- class `IrConstantFloat` — A floating-point constant. An f32 constant is rounded to single precision on — PowerBasic.Compiler/Ir/IrConstant.cs:29
- class `IrNullPtr` — The null pointer constant. It carries an address space so that seeding an unwritten pointer — PowerBasic.Compiler/Ir/IrConstant.cs:38
- class `IrBlockAddress` — The address of a basic block - LLVM's blockaddress. PB needs one for exactly one reason: — PowerBasic.Compiler/Ir/IrConstant.cs:53
- class `IrUndef` — An undefined value of a given type. Reading it yields an arbitrary bit pattern; — PowerBasic.Compiler/Ir/IrConstant.cs:62

### IrDominators.cs  `C#, 128 lines`
- namespace `PowerBasic.Compiler.Ir` — PowerBasic.Compiler/Ir/IrDominators.cs:1
- class `IrDominators` — Dominator tree and dominance frontiers over an 's CFG, — PowerBasic.Compiler/Ir/IrDominators.cs:9
- method `if(visited.Add(next))` — PowerBasic.Compiler/Ir/IrDominators.cs:64
- method `foreach(var pred in block.Predecessors)` — PowerBasic.Compiler/Ir/IrDominators.cs:86
- method `if(newIdom is not null && (!this._idom.TryGetValue(block, out var cur) …` — PowerBasic.Compiler/Ir/IrDominators.cs:91
- method `while(!ReferenceEquals(runner, idom))` — PowerBasic.Compiler/Ir/IrDominators.cs:120

### IrFunction.cs  `C#, 167 lines`
- namespace `PowerBasic.Compiler.Ir` — PowerBasic.Compiler/Ir/IrFunction.cs:1
- class `IrFunction` — A function: a signature plus a list of basic blocks. The first block is the entry — PowerBasic.Compiler/Ir/IrFunction.cs:9
- method `if(operand is IrBlockAddress address)` — PowerBasic.Compiler/Ir/IrFunction.cs:154

### IrGlobalValue.cs  `C#, 32 lines`
- namespace `PowerBasic.Compiler.Ir` — PowerBasic.Compiler/Ir/IrGlobalValue.cs:1
- class `IrGlobalValue` — A symbol with a fixed address: a global variable or a function. Its IR value is — PowerBasic.Compiler/Ir/IrGlobalValue.cs:8
- class `IrGlobalVariable` — A module-level variable. is the type stored at its address. — PowerBasic.Compiler/Ir/IrGlobalValue.cs:19

### IrInstruction.cs  `C#, 75 lines`
- namespace `PowerBasic.Compiler.Ir` — PowerBasic.Compiler/Ir/IrInstruction.cs:1
- class `IrInstruction` — The base of every IR instruction. An instruction is itself a value (its result), — PowerBasic.Compiler/Ir/IrInstruction.cs:8

### IrInstructions.cs  `C#, 449 lines`
- namespace `PowerBasic.Compiler.Ir` — PowerBasic.Compiler/Ir/IrInstructions.cs:1
- enum `IrBinaryOp` — Binary arithmetic / bitwise opcodes. Signedness is encoded in the opcode (sdiv vs udiv), as in LLVM. — PowerBasic.Compiler/Ir/IrInstructions.cs:4
- enum `IrCmpPred` — Comparison predicates. Integer predicates carry signedness; float predicates are ordered (the commo… — PowerBasic.Compiler/Ir/IrInstructions.cs:11
- enum `IrCastOp` — Type-conversion opcodes (the LLVM cast set, restricted to what the dialects need). — PowerBasic.Compiler/Ir/IrInstructions.cs:19
- class `IrBinary` — A binary arithmetic or bitwise instruction: result = op lhs, rhs. — PowerBasic.Compiler/Ir/IrInstructions.cs:56
- class `IrCmp` — A comparison producing an i1: result = icmp/fcmp pred lhs, rhs. — PowerBasic.Compiler/Ir/IrInstructions.cs:72
- class `IrCast` — A type conversion: result = op value to type. — PowerBasic.Compiler/Ir/IrInstructions.cs:89
- class `IrAlloca` — Stack-allocates space for consecutive values of — PowerBasic.Compiler/Ir/IrInstructions.cs:104
- class `IrLoad` — Loads a value of from a pointer: result = load type, ptr. — PowerBasic.Compiler/Ir/IrInstructions.cs:115
- class `IrStore` — Stores a value through a pointer: store value, ptr (yields void). — PowerBasic.Compiler/Ir/IrInstructions.cs:121
- class `IrInlineAsm` — A block of inline assembly, carried through the IR as an opaque barrier. — PowerBasic.Compiler/Ir/IrInstructions.cs:149
- class `IrGep` — Pointer displacement. In the default (byte) mode it adds a byte count to a pointer — — PowerBasic.Compiler/Ir/IrInstructions.cs:184
- class `IrFarPtr` — A pointer that names its own SEGMENT: segment:offset, where every other pointer in this IR — PowerBasic.Compiler/Ir/IrInstructions.cs:234
- class `IrPhi` — An SSA phi: picks an incoming value according to the predecessor control came from. — PowerBasic.Compiler/Ir/IrInstructions.cs:248
- class `IrSelect` — A branchless choice: result = select cond, ifTrue, ifFalse (cond is i1). — PowerBasic.Compiler/Ir/IrInstructions.cs:290
- enum `IrCallConvention` — Source-level calling-convention identity carried through target-neutral IR. A target maps this — PowerBasic.Compiler/Ir/IrInstructions.cs:306
- class `IrCall` — A call: [result =] call callee(args...). The callee is an operand, so indirect calls are — PowerBasic.Compiler/Ir/IrInstructions.cs:312
- class `IrRet` — A function return: ret value or ret void. — PowerBasic.Compiler/Ir/IrInstructions.cs:328
- class `IrBr` — An unconditional branch: br target. — PowerBasic.Compiler/Ir/IrInstructions.cs:340
- class `IrCondBr` — A conditional branch: br cond, ifTrue, ifFalse. — PowerBasic.Compiler/Ir/IrInstructions.cs:347
- class `IrSwitch` — An integer switch: a default target plus a list of (value, target) cases. — PowerBasic.Compiler/Ir/IrInstructions.cs:362
- class `IrIndirectBr` — A branch through a code ADDRESS rather than to a named block: indirectbr addr, [targets], — PowerBasic.Compiler/Ir/IrInstructions.cs:422
- class `IrUnreachable` — Marks an unreachable point (control must never arrive here). — PowerBasic.Compiler/Ir/IrInstructions.cs:446

### IrLowering.PagedArrays.cs  `C#, 301 lines`
- namespace `PowerBasic.Compiler.Ir` — PowerBasic.Compiler/Ir/IrLowering.PagedArrays.cs:4
- class `IrLowering` — The MEMORY-MODEL array classes: DIM HUGE, DIM VIRTUAL, and the pb36 EMS / — PowerBasic.Compiler/Ir/IrLowering.PagedArrays.cs:62
- record `PagedArr` — The two words a memory-model array is addressed through, beside the bounds every dynamic array — PowerBasic.Compiler/Ir/IrLowering.PagedArrays.cs:70
- method `IrAlloca(IrType.I16)` — PowerBasic.Compiler/Ir/IrLowering.PagedArrays.cs:85
- method `IrAlloca(IrType.I16)` — PowerBasic.Compiler/Ir/IrLowering.PagedArrays.cs:88
- method `IrLoweringException( $"a {symbol.ArrayClass} array of rank {arr.Rank} (the direct emitte…` — PowerBasic.Compiler/Ir/IrLowering.PagedArrays.cs:98
- method `IrLoweringException($"dynamic strings inside a {symbol.ArrayClass} array")` — PowerBasic.Compiler/Ir/IrLowering.PagedArrays.cs:101
- method `IrLoweringException($"a {arr.Element} element of a {symbol.ArrayClass} array")` — PowerBasic.Compiler/Ir/IrLowering.PagedArrays.cs:103
- method `IrLoweringException($"a {symbol.ArrayClass} array a procedure also reaches")` — PowerBasic.Compiler/Ir/IrLowering.PagedArrays.cs:105
- method `IrLoweringException($"DIM {d.Class} {v.Name} without array bounds")` — PowerBasic.Compiler/Ir/IrLowering.PagedArrays.cs:114
- method `IrLoweringException($"DIM {d.Class}: no array symbol for {v.Name}")` — PowerBasic.Compiler/Ir/IrLowering.PagedArrays.cs:116
- method `IrLoweringException($"{symbol.ArrayClass} array rank mismatch")` — PowerBasic.Compiler/Ir/IrLowering.PagedArrays.cs:132
- method `IrConstantInt(IrType.I32, 0)` — PowerBasic.Compiler/Ir/IrLowering.PagedArrays.cs:152
- method `IrLoweringException( $"ERASE of the {symbol.ArrayClass} array {symbol.Name} (the direct …` — EMS/XMS have no arm in the direct emitter's EmitErase and fall through to the conventional — PowerBasic.Compiler/Ir/IrLowering.PagedArrays.cs:198
- method `IrLoweringException($"{symbol.ArrayClass} array rank mismatch")` — PowerBasic.Compiler/Ir/IrLowering.PagedArrays.cs:218
- method `IrLoweringException($"element of {symbol.Name} before its DIM was lowered")` — PowerBasic.Compiler/Ir/IrLowering.PagedArrays.cs:222

### IrLowering.cs  `C#, 5073 lines`
- namespace `PowerBasic.Compiler.Ir` — PowerBasic.Compiler/Ir/IrLowering.cs:4
- class `IrLowering` — Lowers a bound program into the IR in clang-style alloca/load/store form: every — PowerBasic.Compiler/Ir/IrLowering.cs:18
- record `DataLayout` — PowerBasic.Compiler/Ir/IrLowering.cs:64
- record `LoopContext` — PowerBasic.Compiler/Ir/IrLowering.cs:65
- method `if(node is Expression e && model.VariableBindings.TryGetValue(e, out va…` — PowerBasic.Compiler/Ir/IrLowering.cs:168
- method `if(node is RedimStmt redim)` — A REDIM names its array through a VariableDecl rather than an expression, so the walk above — PowerBasic.Compiler/Ir/IrLowering.cs:175
- method `foreach(var v in dim.Variables)` — PowerBasic.Compiler/Ir/IrLowering.cs:200
- method `if(symbol is not null && !result.Contains(symbol))` — PowerBasic.Compiler/Ir/IrLowering.cs:204
- method `foreach(var (_, target) in field.Fields)` — PowerBasic.Compiler/Ir/IrLowering.cs:218
- method `if(proc.ReturnType is null || !IrTypeMapper.TryMap(proc.ReturnType, out…` — PowerBasic.Compiler/Ir/IrLowering.cs:230
- method `if(p.Type is UdtType pudt)` — PowerBasic.Compiler/Ir/IrLowering.cs:278
- method `if(p.ByVal)` — PowerBasic.Compiler/Ir/IrLowering.cs:279
- method `foreach(var (_, body) in i.ElseIfs)` — PowerBasic.Compiler/Ir/IrLowering.cs:361
- method `if(i.Else is { } e)` — PowerBasic.Compiler/Ir/IrLowering.cs:363
- method `foreach(var arm in sel.Arms)` — PowerBasic.Compiler/Ir/IrLowering.cs:373
- method `ContainsResume(i.Then)` — PowerBasic.Compiler/Ir/IrLowering.cs:389
- method `ContainsErrorHandling(i.Then)` — PowerBasic.Compiler/Ir/IrLowering.cs:408
- method `ContainsGosub(i.Then)` — PowerBasic.Compiler/Ir/IrLowering.cs:423
- method `if(this._labels.TryGetValue(name, out var target))` — PowerBasic.Compiler/Ir/IrLowering.cs:518
- method `if(Runtime.InlineAsmExports.Canonical(name) is null)` — PowerBasic.Compiler/Ir/IrLowering.cs:520
- class `AsmNames` — Records every identifier the assembler asks about, answering so that parsing continues. — PowerBasic.Compiler/Ir/IrLowering.cs:545
- method `TryResolve` — PowerBasic.Compiler/Ir/IrLowering.cs:547
- method `IrLoweringException("pointer variable with shared storage")` — PowerBasic.Compiler/Ir/IrLowering.cs:595
- method `IrLoweringException("dynamic array")` — PowerBasic.Compiler/Ir/IrLowering.cs:615
- method `IrLoweringException("non-scalar array element")` — PowerBasic.Compiler/Ir/IrLowering.cs:625
- method `StaticGlobalName(this._proc, symbol)` — PowerBasic.Compiler/Ir/IrLowering.cs:679
- method `IrLoweringException($"a {element} element of an ABSOLUTE array")` — PowerBasic.Compiler/Ir/IrLowering.cs:737
- method `IrLoweringException($"not an array element: {expr.Name}")` — PowerBasic.Compiler/Ir/IrLowering.cs:744
- method `IrLoweringException("rank mismatch")` — PowerBasic.Compiler/Ir/IrLowering.cs:760
- record `DynArr` — A dynamic array is a runtime-allocated buffer plus a bound descriptor: the data — PowerBasic.Compiler/Ir/IrLowering.cs:790
- record `ErrorChecks` — The $ERROR traps a procedure body is compiled with (see ). — PowerBasic.Compiler/Ir/IrLowering.cs:815
- method `StaticGlobalName(this._proc, symbol)` — PowerBasic.Compiler/Ir/IrLowering.cs:920
- method `Cell` — PowerBasic.Compiler/Ir/IrLowering.cs:922
- method `IrLoweringException("dynamic array rank mismatch")` — PowerBasic.Compiler/Ir/IrLowering.cs:941
- method `IrLoweringException($"element of {symbol.Name} before its DIM ... AT was lowered")` — PowerBasic.Compiler/Ir/IrLowering.cs:989
- method `IrLoweringException("ABSOLUTE array rank mismatch")` — PowerBasic.Compiler/Ir/IrLowering.cs:991
- method `if(randomize.Arguments is [{ } seed])` — PowerBasic.Compiler/Ir/IrLowering.cs:1195
- method `if(randomize.Arguments.Count <= 1)` — PowerBasic.Compiler/Ir/IrLowering.cs:1198
- method `IrLoweringException` — PowerBasic.Compiler/Ir/IrLowering.cs:1200
- method `IrLoweringException("assignment through a pointer to a non-scalar")` — PowerBasic.Compiler/Ir/IrLowering.cs:1221
- method `if(field.Type is FixedStringType ffs)` — PowerBasic.Compiler/Ir/IrLowering.cs:1288
- method `if(field.Type is AsciizType faz)` — PowerBasic.Compiler/Ir/IrLowering.cs:1293
- method `IrLoweringException("MID$ statement requires whole-module lowering")` — PowerBasic.Compiler/Ir/IrLowering.cs:1335
- method `IrLoweringException("ASC assignment requires whole-module lowering")` — PowerBasic.Compiler/Ir/IrLowering.cs:1365
- method `IrLoweringException("ASC assignment to a fixed-length or ASCIIZ target")` — PowerBasic.Compiler/Ir/IrLowering.cs:1367
- method `IrLoweringException($"BIT statement on {targetType}")` — PowerBasic.Compiler/Ir/IrLowering.cs:1400
- method `IrLoweringException("REPLACE requires whole-module lowering")` — PowerBasic.Compiler/Ir/IrLowering.cs:1424
- method `IrLoweringException("REPLACE into a fixed-length or ASCIIZ target")` — PowerBasic.Compiler/Ir/IrLowering.cs:1426
- method `IrLoweringException("SWAP of differently-typed operands")` — PowerBasic.Compiler/Ir/IrLowering.cs:1453
- method `IrAlloca(IrType.I8)` — PowerBasic.Compiler/Ir/IrLowering.cs:1457
- method `IrLoweringException("SWAP of differently-typed operands")` — PowerBasic.Compiler/Ir/IrLowering.cs:1466
- method `IrLoweringException("non-scalar dotted variable")` — PowerBasic.Compiler/Ir/IrLowering.cs:1506
- method `IrLoweringException("non-scalar UDT field")` — PowerBasic.Compiler/Ir/IrLowering.cs:1511
- method `IrLoweringException("unsupported member access")` — PowerBasic.Compiler/Ir/IrLowering.cs:1529
- method `IrLoweringException("UDT array field")` — PowerBasic.Compiler/Ir/IrLowering.cs:1533
- method `IrLoweringException` — PowerBasic.Compiler/Ir/IrLowering.cs:1573
- method `IrLoweringException("unsupported pointer value")` — PowerBasic.Compiler/Ir/IrLowering.cs:1578
- method `IrLoweringException($"VARSEG of an element of the {array.ArrayClass} array {array.Name}")` — PowerBasic.Compiler/Ir/IrLowering.cs:1638
- method `IrConstantInt(IrType.I16, segment)` — PowerBasic.Compiler/Ir/IrLowering.cs:1642
- method `IrLoweringException("PRINT requires whole-module lowering")` — PowerBasic.Compiler/Ir/IrLowering.cs:1691
- method `IrLoweringException("LPRINT to a file number")` — PowerBasic.Compiler/Ir/IrLowering.cs:1693
- method `if(item.Value is { } expr)` — PowerBasic.Compiler/Ir/IrLowering.cs:1703
- method `if(item.Separator == PrintSeparator.Comma)` — PowerBasic.Compiler/Ir/IrLowering.cs:1705
- method `if` — PowerBasic.Compiler/Ir/IrLowering.cs:1708
- method `IrLoweringException("non-literal PRINT USING format")` — PowerBasic.Compiler/Ir/IrLowering.cs:1770
- method `IrLoweringException("more PRINT USING values than fields")` — PowerBasic.Compiler/Ir/IrLowering.cs:1790
- method `IrLoweringException("PRINT USING of a non-numeric, non-string value")` — PowerBasic.Compiler/Ir/IrLowering.cs:1803
- method `IrLoweringException("non-literal USING$ format")` — PowerBasic.Compiler/Ir/IrLowering.cs:1844
- method `IrLoweringException("WRITE requires whole-module lowering")` — PowerBasic.Compiler/Ir/IrLowering.cs:1872
- method `IrLoweringException("WRITE of a non-numeric, non-string value")` — PowerBasic.Compiler/Ir/IrLowering.cs:1910
- method `IrLoweringException("PRINT of a non-numeric, non-literal item")` — PowerBasic.Compiler/Ir/IrLowering.cs:1951
- method `IrLoweringException("INPUT requires whole-module lowering")` — PowerBasic.Compiler/Ir/IrLowering.cs:1964
- method `IrLoweringException("INPUT into a non-scalar target")` — PowerBasic.Compiler/Ir/IrLowering.cs:1996
- method `IrLoweringException("OPEN requires whole-module lowering")` — PowerBasic.Compiler/Ir/IrLowering.cs:2004
- method `IrLoweringException("GET/PUT requires whole-module lowering")` — PowerBasic.Compiler/Ir/IrLowering.cs:2015
- method `IrLoweringException("GET/PUT of a non-scalar record")` — PowerBasic.Compiler/Ir/IrLowering.cs:2036
- method `IrLoweringException("CLOSE requires whole-module lowering")` — PowerBasic.Compiler/Ir/IrLowering.cs:2050
- method `IrLoweringException("runtime calls require whole-module lowering")` — PowerBasic.Compiler/Ir/IrLowering.cs:2089
- method `IrLoweringException("strings require whole-module lowering")` — PowerBasic.Compiler/Ir/IrLowering.cs:2099
- method `IrLoweringException($"GOTO to unknown label {g.Target}")` — PowerBasic.Compiler/Ir/IrLowering.cs:2176
- method `IrLoweringException($"ON ERROR GOTO unknown label {oe.Target}")` — PowerBasic.Compiler/Ir/IrLowering.cs:2207
- method `if(!this._labels.TryGetValue(target, out var block))` — PowerBasic.Compiler/Ir/IrLowering.cs:2215
- method `IrLoweringException($"RESUME to unknown label {target}")` — PowerBasic.Compiler/Ir/IrLowering.cs:2216
- method `IrLoweringException($"EXIT FAR AT unknown label {label}")` — PowerBasic.Compiler/Ir/IrLowering.cs:2260
- method `IrLoweringException("GOSUB without return-stack setup")` — PowerBasic.Compiler/Ir/IrLowering.cs:2285
- method `IrLoweringException($"GOSUB to unknown label {g.Target}")` — PowerBasic.Compiler/Ir/IrLowering.cs:2287
- method `IrLoweringException("GOTO/GOSUB DWORD in a function with no labels to reach")` — PowerBasic.Compiler/Ir/IrLowering.cs:2322
- method `IrLoweringException("GOSUB DWORD without return-stack setup")` — PowerBasic.Compiler/Ir/IrLowering.cs:2336
- method `IrLoweringException("RETURN without a matching GOSUB")` — PowerBasic.Compiler/Ir/IrLowering.cs:2351
- method `IrLoweringException($"RETURN to unknown label {label}")` — PowerBasic.Compiler/Ir/IrLowering.cs:2356
- method `IrLoweringException("ON GOTO with a non-integer selector")` — PowerBasic.Compiler/Ir/IrLowering.cs:2365
- method `IrLoweringException("ON ... GOSUB without return-stack setup")` — PowerBasic.Compiler/Ir/IrLowering.cs:2375
- method `IrLoweringException("DATA item exceeds 64KB")` — PowerBasic.Compiler/Ir/IrLowering.cs:2438
- method `IrLoweringException("DATA/READ requires whole-module lowering")` — PowerBasic.Compiler/Ir/IrLowering.cs:2448
- method `IrLoweringException($"RESTORE to unknown DATA label {label}")` — PowerBasic.Compiler/Ir/IrLowering.cs:2507
- method `IrLoweringException("END inside a procedure")` — PowerBasic.Compiler/Ir/IrLowering.cs:2515
- method `IrLoweringException($"REDIM of non-dynamic array {v.Name}")` — PowerBasic.Compiler/Ir/IrLowering.cs:2560
- method `IrLoweringException($"REDIM of the ABSOLUTE array {v.Name}")` — PowerBasic.Compiler/Ir/IrLowering.cs:2564
- method `IrLoweringException("REDIM rank mismatch")` — PowerBasic.Compiler/Ir/IrLowering.cs:2566
- method `if(r.Preserve)` — PowerBasic.Compiler/Ir/IrLowering.cs:2571
- method `IrLoweringException($"REDIM PRESERVE on the {symbol.ArrayClass} array {v.Name}")` — PowerBasic.Compiler/Ir/IrLowering.cs:2572
- method `IrConstantInt(IrType.I32, 0)` — PowerBasic.Compiler/Ir/IrLowering.cs:2606
- method `IrLoweringException("ERASE of a non-array")` — PowerBasic.Compiler/Ir/IrLowering.cs:2634
- method `IrLoweringException($"ERASE of the ABSOLUTE array {symbol.Name}")` — PowerBasic.Compiler/Ir/IrLowering.cs:2638
- record `SortArray` — The three things the sort/scan parameter block needs to know about an array: where its elements — PowerBasic.Compiler/Ir/IrLowering.cs:2675
- method `IrLoweringException("ARRAY SORT/SCAN of a non-array")` — PowerBasic.Compiler/Ir/IrLowering.cs:2685
- method `IrLoweringException("ARRAY SORT/SCAN of an array parameter")` — PowerBasic.Compiler/Ir/IrLowering.cs:2687
- method `IrLoweringException("ARRAY SORT/SCAN of a dynamic array")` — PowerBasic.Compiler/Ir/IrLowering.cs:2689
- method `IrLoweringException("ARRAY SORT/SCAN of a multi-dimensional array")` — PowerBasic.Compiler/Ir/IrLowering.cs:2691
- method `IrConstantInt` — PowerBasic.Compiler/Ir/IrLowering.cs:2717
- method `IrLoweringException($"ARRAY SORT/SCAN over {shape.Type.Element} elements")` — PowerBasic.Compiler/Ir/IrLowering.cs:2763
- method `IrLoweringException("FROM/TO range on a non-string ARRAY SORT/SCAN")` — PowerBasic.Compiler/Ir/IrLowering.cs:2765
- method `IrLoweringException("ARRAY SORT requires whole-module lowering")` — PowerBasic.Compiler/Ir/IrLowering.cs:2807
- method `IrLoweringException("COLLATE on an ARRAY SORT")` — PowerBasic.Compiler/Ir/IrLowering.cs:2809
- method `IrLoweringException("ARRAY SORT TAGARRAY on a string array")` — PowerBasic.Compiler/Ir/IrLowering.cs:2813
- method `IrLoweringException("ARRAY SCAN requires whole-module lowering")` — PowerBasic.Compiler/Ir/IrLowering.cs:2830
- method `IrLoweringException("COLLATE on an ARRAY SCAN")` — PowerBasic.Compiler/Ir/IrLowering.cs:2832
- method `IrLoweringException("FIELD target that is not a dynamic string")` — PowerBasic.Compiler/Ir/IrLowering.cs:2877
- method `IrLoweringException("CHAIN requires whole-module lowering")` — PowerBasic.Compiler/Ir/IrLowering.cs:2956
- method `IrLoweringException("COMMON array across CHAIN")` — PowerBasic.Compiler/Ir/IrLowering.cs:3013
- method `IrLoweringException($"COMMON {symbol.Type} across CHAIN")` — PowerBasic.Compiler/Ir/IrLowering.cs:3024
- method `IrLoweringException("DIM AT without the ABSOLUTE class")` — PowerBasic.Compiler/Ir/IrLowering.cs:3049
- method `IrLoweringException($"DIM {d.Class} array class")` — PowerBasic.Compiler/Ir/IrLowering.cs:3056
- method `IrLoweringException("DIM rank mismatch")` — PowerBasic.Compiler/Ir/IrLowering.cs:3074
- method `IrLoweringException("DIM AT a segment that is not a compile-time constant")` — PowerBasic.Compiler/Ir/IrLowering.cs:3103
- method `IrLoweringException($"DIM {v.Name} AT without array bounds")` — PowerBasic.Compiler/Ir/IrLowering.cs:3107
- method `IrLoweringException($"DIM AT: no array symbol for {v.Name}")` — PowerBasic.Compiler/Ir/IrLowering.cs:3109
- method `IrLoweringException("DIM AT rank mismatch")` — PowerBasic.Compiler/Ir/IrLowering.cs:3111
- method `IrLoweringException("DIM AT over a dynamic-string element type")` — PowerBasic.Compiler/Ir/IrLowering.cs:3115
- method `IrConstantInt(IrType.I32, 0)` — PowerBasic.Compiler/Ir/IrLowering.cs:3121
- method `IrLoweringException("INCR/DECR on float")` — PowerBasic.Compiler/Ir/IrLowering.cs:3152
- method `IrConstantInt(ty, 1)` — PowerBasic.Compiler/Ir/IrLowering.cs:3155
- method `IrLoweringException($"{cmd.Keyword} with {cmd.Arguments.Count} arguments")` — PowerBasic.Compiler/Ir/IrLowering.cs:3169
- method `IrLoweringException($"{cmd.Keyword} of a non-scalar target")` — PowerBasic.Compiler/Ir/IrLowering.cs:3171
- method `IrLoweringException("LOCATE with a cursor-shape argument")` — PowerBasic.Compiler/Ir/IrLowering.cs:3198
- method `IrLoweringException($"{cmd.Keyword} with {cmd.Arguments.Count} arguments")` — PowerBasic.Compiler/Ir/IrLowering.cs:3216
- method `IrLoweringException($"{cmd.Keyword} of a non-scalar target")` — PowerBasic.Compiler/Ir/IrLowering.cs:3218
- method `IrLoweringException($"{cmd.Keyword} by a runtime count")` — PowerBasic.Compiler/Ir/IrLowering.cs:3220
- method `IrLoweringException($"{cmd.Keyword} by {n} over a {width}-bit value")` — PowerBasic.Compiler/Ir/IrLowering.cs:3225
- method `IrLoweringException($"FOR over a {ty} counter")` — PowerBasic.Compiler/Ir/IrLowering.cs:3300
- method `IrLoweringException("FOR with a runtime STEP over an unsigned counter")` — PowerBasic.Compiler/Ir/IrLowering.cs:3315
- method `foreach(var loop in this._loops)` — PowerBasic.Compiler/Ir/IrLowering.cs:3475
- method `IrLoweringException($"EXIT {e.Kind} outside a matching loop")` — PowerBasic.Compiler/Ir/IrLowering.cs:3482
- method `IrLoweringException($"call to unsupported procedure {c.Name}")` — PowerBasic.Compiler/Ir/IrLowering.cs:3497
- method `IrLoweringException("SELECT CASE on a non-scalar subject")` — PowerBasic.Compiler/Ir/IrLowering.cs:3506
- method `if(type is BcdType bcd)` — A FIX literal is not a float value with a float type. Its CELL holds the number scaled by — PowerBasic.Compiler/Ir/IrLowering.cs:3642
- method `IrLoweringException($"unknown equate {nc.Name}")` — PowerBasic.Compiler/Ir/IrLowering.cs:3688
- method `IrLoweringException("non-numeric equate")` — PowerBasic.Compiler/Ir/IrLowering.cs:3690
- method `IrLoweringException($"call to {proc.Name} outside the modelled subset")` — PowerBasic.Compiler/Ir/IrLowering.cs:3699
- method `IrLoweringException("SUB used in expression position")` — PowerBasic.Compiler/Ir/IrLowering.cs:3701
- method `IrLoweringException($"unbound name {name.Name}")` — PowerBasic.Compiler/Ir/IrLowering.cs:3707
- method `IrLoweringException($"{name} requires whole-module lowering")` — PowerBasic.Compiler/Ir/IrLowering.cs:3768
- method `IrLoweringException($"{name} of an unknown label {labelName}")` — PowerBasic.Compiler/Ir/IrLowering.cs:3855
- method `IrLoweringException($"intrinsic {name} with {call.Arguments.Count} arguments")` — PowerBasic.Compiler/Ir/IrLowering.cs:3875
- method `IrConstantInt(IrType.I16, Math.Max(this._model.TypeOf(call.Arguments[0]).Size, 1))` — PowerBasic.Compiler/Ir/IrLowering.cs:3910
- method `IrLoweringException("POS requires whole-module lowering")` — PowerBasic.Compiler/Ir/IrLowering.cs:3956
- method `IrLoweringException("intrinsic PEEK takes one or two arguments")` — PowerBasic.Compiler/Ir/IrLowering.cs:3975
- method `IrConstantInt(IrType.I32, 1)` — PowerBasic.Compiler/Ir/IrLowering.cs:4016
- method `IrLoweringException("LBOUND/UBOUND of a non-array")` — PowerBasic.Compiler/Ir/IrLowering.cs:4051
- method `IrLoweringException("LBOUND/UBOUND dimension out of range")` — PowerBasic.Compiler/Ir/IrLowering.cs:4056
- method `IrLoweringException("static array without bounds")` — PowerBasic.Compiler/Ir/IrLowering.cs:4061
- method `IrLoweringException($"INSTR with {call.Arguments.Count} arguments")` — PowerBasic.Compiler/Ir/IrLowering.cs:4094
- method `IrLoweringException($"EXTRACT$ with {ci.Arguments.Count} arguments")` — PowerBasic.Compiler/Ir/IrLowering.cs:4168
- method `Num(0)` — PowerBasic.Compiler/Ir/IrLowering.cs:4218
- method `Num(0)` — PowerBasic.Compiler/Ir/IrLowering.cs:4229
- method `IrLoweringException("STR$ of a non-numeric value")` — PowerBasic.Compiler/Ir/IrLowering.cs:4256
- method `IrLoweringException($"{fn} on a non-float result")` — PowerBasic.Compiler/Ir/IrLowering.cs:4288
- method `IrLoweringException("LEN of an ASCIIZ expression that is not storage")` — PowerBasic.Compiler/Ir/IrLowering.cs:4334
- method `IrLoweringException("LEN of a non-string")` — PowerBasic.Compiler/Ir/IrLowering.cs:4345
- method `IrLoweringException("UDT comparison of non-UDT")` — PowerBasic.Compiler/Ir/IrLowering.cs:4378
- method `IrLoweringException($"unsupported call/index {call.Name}")` — PowerBasic.Compiler/Ir/IrLowering.cs:4505
- method `IrLoweringException("SUB used in expression position")` — PowerBasic.Compiler/Ir/IrLowering.cs:4507
- method `if(!address.Type.IsFarPointer)` — PowerBasic.Compiler/Ir/IrLowering.cs:4526
- method `if(address.Type.IsFarPointer)` — PowerBasic.Compiler/Ir/IrLowering.cs:4588
- method `IrLoweringException("far pointer passed BYREF to a near parameter")` — PowerBasic.Compiler/Ir/IrLowering.cs:4589
- method `if(!resultTy.IsFloat)` — PowerBasic.Compiler/Ir/IrLowering.cs:4672
- method `IrLoweringException("integer exponentiation")` — PowerBasic.Compiler/Ir/IrLowering.cs:4673
- method `IrLoweringException( "$ERROR OVERFLOW ON over a 64-bit multiply (there is no wider integ…` — PowerBasic.Compiler/Ir/IrLowering.cs:4763
- method `IrLoweringException($"$ERROR {arm} ON arms a runtime trap the IR lowering does not emit")` — PowerBasic.Compiler/Ir/IrLowering.cs:4881
- method `IrLoweringException($"metastatement ${meta.Command}")` — PowerBasic.Compiler/Ir/IrLowering.cs:4883
- method `IrLoweringException("comparison of non-scalar operands")` — PowerBasic.Compiler/Ir/IrLowering.cs:4914
- method `IrLoweringException("coercion between non-scalar types")` — PowerBasic.Compiler/Ir/IrLowering.cs:5010

### IrModule.cs  `C#, 80 lines`
- namespace `PowerBasic.Compiler.Ir` — PowerBasic.Compiler/Ir/IrModule.cs:2
- class `IrModule` — A translation unit: the globals and functions produced from one bound program. — PowerBasic.Compiler/Ir/IrModule.cs:9

### IrPrinter.cs  `C#, 153 lines`
- namespace `PowerBasic.Compiler.Ir` — PowerBasic.Compiler/Ir/IrPrinter.cs:3
- class `IrPrinter` — Renders the IR to an LLVM-like textual form. The output is deterministic — PowerBasic.Compiler/Ir/IrPrinter.cs:11
- method `if(!inst.Type.IsVoid)` — PowerBasic.Compiler/Ir/IrPrinter.cs:65

### IrSwitchQueries.cs  `C#, 49 lines`
- namespace `PowerBasic.Compiler.Ir` — PowerBasic.Compiler/Ir/IrSwitchQueries.cs:1
- class `IrSwitchQueries` — The two questions a back end asks an that are properties of the — PowerBasic.Compiler/Ir/IrSwitchQueries.cs:14

### IrType.cs  `C#, 172 lines`
- namespace `PowerBasic.Compiler.Ir` — PowerBasic.Compiler/Ir/IrType.cs:1
- enum `IrTypeKind` — The kind of an . The IR type system is deliberately — PowerBasic.Compiler/Ir/IrType.cs:8
- enum `IrFloatFormat` — The in-memory encoding of a floating-point value. LLVM has only one (IEEE), but the BASIC family — PowerBasic.Compiler/Ir/IrType.cs:27
- record `IrType` — A value type in the IR. Types are immutable and value-equatable, so a single — PowerBasic.Compiler/Ir/IrType.cs:62

### IrTypeMapper.cs  `C#, 48 lines`
- namespace `PowerBasic.Compiler.Ir` — PowerBasic.Compiler/Ir/IrTypeMapper.cs:2
- class `IrTypeMapper` — Maps resolved PowerBASIC scalar types onto the target-independent IR type lattice. — PowerBasic.Compiler/Ir/IrTypeMapper.cs:11
- class `IrLoweringException` — Raised when the lowering meets a construct outside its supported subset; caught to decline graceful… — PowerBasic.Compiler/Ir/IrTypeMapper.cs:47

### IrValue.cs  `C#, 46 lines`
- namespace `PowerBasic.Compiler.Ir` — PowerBasic.Compiler/Ir/IrValue.cs:1
- class `IrValue` — The base of everything that can be used as an operand: constants, function — PowerBasic.Compiler/Ir/IrValue.cs:10

### IrVerifier.cs  `C#, 269 lines`
- namespace `PowerBasic.Compiler.Ir` — PowerBasic.Compiler/Ir/IrVerifier.cs:1
- class `IrVerifier` — Checks the structural and SSA well-formedness of a function or module: exactly one — PowerBasic.Compiler/Ir/IrVerifier.cs:9
- method `if(seenNonPhi)` — PowerBasic.Compiler/Ir/IrVerifier.cs:72
- method `if(!this.VerifyOperandIsOwned(value))` — PowerBasic.Compiler/Ir/IrVerifier.cs:99
- method `if(value is IrInstruction def && def.Parent is { } defBlock && !Referen…` — PowerBasic.Compiler/Ir/IrVerifier.cs:101
- method `if(this._order[def] >= this._order[inst])` — PowerBasic.Compiler/Ir/IrVerifier.cs:115
- method `if(!b.Lhs.Type.SameStorage(b.Rhs.Type) || !b.Type.SameStorage(b.Lhs.Typ…` — PowerBasic.Compiler/Ir/IrVerifier.cs:164
- method `if(b.IsFloatOp && !b.Type.IsFloat)` — PowerBasic.Compiler/Ir/IrVerifier.cs:166
- method `if(b.IsFloatOp && b.Type.IsMbf)` — PowerBasic.Compiler/Ir/IrVerifier.cs:168
- method `if(!b.IsFloatOp && !b.Type.IsInteger)` — PowerBasic.Compiler/Ir/IrVerifier.cs:170
- method `if(!c.Lhs.Type.SameStorage(c.Rhs.Type))` — PowerBasic.Compiler/Ir/IrVerifier.cs:174
- method `if(IsFloatPred(c.Pred) && !c.Lhs.Type.IsFloat)` — PowerBasic.Compiler/Ir/IrVerifier.cs:176
- method `if(IsFloatPred(c.Pred) && c.Lhs.Type.IsMbf)` — PowerBasic.Compiler/Ir/IrVerifier.cs:178
- method `if(!IsFloatPred(c.Pred) && c.Lhs.Type.IsFloat)` — PowerBasic.Compiler/Ir/IrVerifier.cs:180
- method `if(!actual.SameStorage(expected))` — PowerBasic.Compiler/Ir/IrVerifier.cs:198
- method `if(!ib.Address.Type.IsPointer)` — PowerBasic.Compiler/Ir/IrVerifier.cs:208
- method `if(ib.Targets.Count == 0)` — PowerBasic.Compiler/Ir/IrVerifier.cs:210
- method `if(!sel.Condition.Type.IsBool)` — PowerBasic.Compiler/Ir/IrVerifier.cs:214
- method `if(!sel.IfTrue.Type.SameStorage(sel.IfFalse.Type) || !sel.Type.SameStor…` — PowerBasic.Compiler/Ir/IrVerifier.cs:216

### LlvmEmitter.cs  `C#, 272 lines`
- namespace `PowerBasic.Compiler.Ir` — PowerBasic.Compiler/Ir/LlvmEmitter.cs:3
- class `LlvmEmitter` — Emits strictly-valid textual LLVM IR (a .ll module) that the real LLVM — PowerBasic.Compiler/Ir/LlvmEmitter.cs:14
- method `Emit(module, targetTriple)` — PowerBasic.Compiler/Ir/LlvmEmitter.cs:35
- method `if(!inst.Type.IsVoid)` — PowerBasic.Compiler/Ir/LlvmEmitter.cs:105
- method `Ty(call.Type)` — PowerBasic.Compiler/Ir/LlvmEmitter.cs:163

## PowerBasic.Compiler/Ir/Analysis/

### IrAliasAnalysis.cs  `C#, 169 lines`
- namespace `PowerBasic.Compiler.Ir.Analysis` — PowerBasic.Compiler/Ir/Analysis/IrAliasAnalysis.cs:1
- enum `IrAliasResult` — The relationship between two memory locations. — PowerBasic.Compiler/Ir/Analysis/IrAliasAnalysis.cs:4
- class `IrAliasAnalysis` — Basic, target-independent alias analysis for ordinary IR loads and stores. — PowerBasic.Compiler/Ir/Analysis/IrAliasAnalysis.cs:34
- record `Address` — PowerBasic.Compiler/Ir/Analysis/IrAliasAnalysis.cs:35
- method `IsUniqueObject(first.Root)` — PowerBasic.Compiler/Ir/Analysis/IrAliasAnalysis.cs:52

### IrLoopDependenceAnalysis.cs  `C#, 493 lines`
- namespace `PowerBasic.Compiler.Ir.Analysis` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:2
- enum `IrDependenceKind` — The observable memory-order relationship between two loop accesses. — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:6
- enum `IrDependenceDirection` — One component of a dependence direction vector. O0172 currently analyzes one loop level, but the — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:20
- record `IrLoopMemoryAccess` — A load or store inside the analyzed loop. — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:27
- record `IrLoopDependence` — A proven dependence from to . is the — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:35
- record `IrLoopDependenceInfo` — The dependence facts for one recognized counted loop. — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:43
- class `IrLoopDependenceAnalysis` — O0172 — target-independent loop memory-dependence analysis for counted loops. — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:80
- record `Affine` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:83
- record `AffineAddress` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:85
- method `new(loop.Header, loop.Counter, loop.Trips, accesses, [], false)` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:103
- method `if(!first.Writes && !second.Writes)` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:119
- method `if` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:121
- method `if` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:125
- method `if(distances is null)` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:133
- method `foreach` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:137
- method `AddDependence(found, first, second, distance, ref complete)` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:139
- method `new` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:141
- method `foreach(var instruction in block.Instructions)` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:152
- method `switch(instruction)` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:153
- method `if(instruction is IrCall call && (call.Callee is not IrFunction callee …` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:167
- method `if(IrAliasAnalysis.StorageBytes(elementType) is not { } elementBytes ||…` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:204
- method `Fits(affine, value.Type, loop.Trips)` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:229
- method `Fits(affine, value.Type, loop.Trips)` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:234
- method `Fits(affine, cast.Type, loop.Trips)` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:243
- method `if(!TryAdd(left, right, out affine))` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:259
- method `if(!TrySubtract(left, right, out affine))` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:263
- method `if(!TryScale(right, left.Constant, out affine))` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:267
- method `if(!TryScale(left, right.Constant, out affine))` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:271
- method `if(!TryScale(left, 1L << (int)right.Constant, out affine))` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:275
- method `Fits` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:281
- method `if(!RangesOverlap(first.Constant, first.Bytes, second.Constant, second.…` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:292
- method `if(trips > 1)` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:295
- method `for(var secondByte = 0; secondByte < second.Bytes; ++secondByte)` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:304
- method `if(!TryAdd(first.Constant, firstByte, out var firstAt) || !TryAdd(secon…` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:305
- method `if(delta % first.Stride != 0)` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:309
- method `if(distance > -trips && distance < trips)` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:317
- method `if(!TryAdd(second.Constant, secondByte, out var secondAt) || !TryAdd(fi…` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:328
- method `if(!CouldHaveIntegerSolution(first.Stride, second.Stride, rhs, trips))` — PowerBasic.Compiler/Ir/Analysis/IrLoopDependenceAnalysis.cs:332

### IrRangeAnalysis.cs  `C#, 509 lines`
- namespace `PowerBasic.Compiler.Ir.Analysis` — PowerBasic.Compiler/Ir/Analysis/IrRangeAnalysis.cs:1
- class `IrRangeAnalysis` — What interval an integer SSA value is provably confined to - the IR's answer to the direct — PowerBasic.Compiler/Ir/Analysis/IrRangeAnalysis.cs:39
- method `foreach(var instruction in block.Instructions)` — PowerBasic.Compiler/Ir/Analysis/IrRangeAnalysis.cs:185
- method `if(sweep >= _WIDEN_AFTER && instruction is IrPhi)` — Widening is applied to the phis only: they are the sole place a cycle can grow without — PowerBasic.Compiler/Ir/Analysis/IrRangeAnalysis.cs:192
- method `if(after.Equals(before))` — PowerBasic.Compiler/Ir/Analysis/IrRangeAnalysis.cs:194
- method `foreach(var instruction in block.Instructions)` — PowerBasic.Compiler/Ir/Analysis/IrRangeAnalysis.cs:209
- method `if(after.Equals(this._global.GetValueOrDefault(instruction, ValueRange.…` — PowerBasic.Compiler/Ir/Analysis/IrRangeAnalysis.cs:213
- method `ValueRange(-(1L << (source.Bits - 1)), (1L << (source.Bits - 1)) - 1)` — PowerBasic.Compiler/Ir/Analysis/IrRangeAnalysis.cs:307
- method `ValueRange(0, (1L << source.Bits) - 1)` — PowerBasic.Compiler/Ir/Analysis/IrRangeAnalysis.cs:313
- method `AddConstraints(collected, cmp, outcome)` — PowerBasic.Compiler/Ir/Analysis/IrRangeAnalysis.cs:424
- method `Parent` — PowerBasic.Compiler/Ir/Analysis/IrRangeAnalysis.cs:427

### ValueRange.cs  `C#, 200 lines`
- namespace `PowerBasic.Compiler.Ir.Analysis` — PowerBasic.Compiler/Ir/Analysis/ValueRange.cs:1
- record `ValueRange` — A closed integer interval [Lo, Hi] over the value a program computes - the range half of — PowerBasic.Compiler/Ir/Analysis/ValueRange.cs:21
- method `new(0, 1)` — PowerBasic.Compiler/Ir/Analysis/ValueRange.cs:45
- method `Hull(checked(this.Lo * o.Lo), checked(this.Lo * o.Hi), checked(this.Hi * …` — PowerBasic.Compiler/Ir/Analysis/ValueRange.cs:87
- method `Hull(checked(this.Lo / o.Lo), checked(this.Lo / o.Hi), checked(this.Hi / …` — PowerBasic.Compiler/Ir/Analysis/ValueRange.cs:102

## PowerBasic.Compiler/Ir/Passes/

### AggregateBlockScalarization.cs  `C#, 308 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/AggregateBlockScalarization.cs:1
- class `AggregateBlockScalarization` — Decomposes fixed-size whole-record copies and equality tests into scalar memory operations when — PowerBasic.Compiler/Ir/Passes/AggregateBlockScalarization.cs:31
- record `Region` — PowerBasic.Compiler/Ir/Passes/AggregateBlockScalarization.cs:35
- method `if(!TryAccessRegion(user, alloca, 0, alloca.Count, out var direct))` — PowerBasic.Compiler/Ir/Passes/AggregateBlockScalarization.cs:223
- method `if(gep.Users.Count == 0)` — PowerBasic.Compiler/Ir/Passes/AggregateBlockScalarization.cs:229
- method `foreach(var indexed in gep.Users)` — PowerBasic.Compiler/Ir/Passes/AggregateBlockScalarization.cs:231
- method `IsExactWholeObjectCall` — PowerBasic.Compiler/Ir/Passes/AggregateBlockScalarization.cs:237
- method `if(distinct[i] != distinct[j])` — PowerBasic.Compiler/Ir/Passes/AggregateBlockScalarization.cs:248
- method `ReferenceEquals(load.Pointer, pointer)` — PowerBasic.Compiler/Ir/Passes/AggregateBlockScalarization.cs:276

### BitsetSubstitution.cs  `C#, 151 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/BitsetSubstitution.cs:1
- class `BitsetSubstitution` — O0331 — packs a non-escaping zero-initialized global INTEGER Boolean array into one bit per — PowerBasic.Compiler/Ir/Passes/BitsetSubstitution.cs:8
- record `Access` — PowerBasic.Compiler/Ir/Passes/BitsetSubstitution.cs:11
- method `RewriteLoad(load, replacement, access.Index)` — PowerBasic.Compiler/Ir/Passes/BitsetSubstitution.cs:38
- method `RewriteStore(store, replacement, access.Index)` — PowerBasic.Compiler/Ir/Passes/BitsetSubstitution.cs:41
- method `foreach(var indexed in gep.Users.ToList())` — PowerBasic.Compiler/Ir/Passes/BitsetSubstitution.cs:66
- method `ReferenceEquals(load.Pointer, global)` — PowerBasic.Compiler/Ir/Passes/BitsetSubstitution.cs:71
- method `ReferenceEquals(store.Pointer, global)` — PowerBasic.Compiler/Ir/Passes/BitsetSubstitution.cs:74
- method `ReferenceEquals(load.Pointer, pointer)` — PowerBasic.Compiler/Ir/Passes/BitsetSubstitution.cs:88
- method `ReferenceEquals(store.Pointer, pointer)` — PowerBasic.Compiler/Ir/Passes/BitsetSubstitution.cs:91
- method `IrConstantInt(index.Type, (long)(element >> 3))` — PowerBasic.Compiler/Ir/Passes/BitsetSubstitution.cs:137

### ConversionRangeCheckElim.cs  `C#, 110 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/ConversionRangeCheckElim.cs:2
- class `ConversionRangeCheckElim` — Removes floating-point range guards around narrowing conversions when the floating value is — PowerBasic.Compiler/Ir/Passes/ConversionRangeCheckElim.cs:18
- method `if(cmp.HasNoUsers || cmp.Pred is not (IrCmpPred.Foeq or IrCmpPred.Fone …` — PowerBasic.Compiler/Ir/Passes/ConversionRangeCheckElim.cs:31
- method `if(TryRange(cmp.Lhs, block, integers, _MAX_DEPTH, []) is not { } lhs ||…` — PowerBasic.Compiler/Ir/Passes/ConversionRangeCheckElim.cs:34
- method `if(Decide(cmp.Pred, lhs, rhs) is { } outcome)` — PowerBasic.Compiler/Ir/Passes/ConversionRangeCheckElim.cs:37
- record `FloatRange` — PowerBasic.Compiler/Ir/Passes/ConversionRangeCheckElim.cs:45
- method `Join(FloatRange other)` — PowerBasic.Compiler/Ir/Passes/ConversionRangeCheckElim.cs:47
- method `new(constant.Value, constant.Value)` — PowerBasic.Compiler/Ir/Passes/ConversionRangeCheckElim.cs:57
- method `if(source.IsEmpty || source.IsTop)` — PowerBasic.Compiler/Ir/Passes/ConversionRangeCheckElim.cs:61
- method `Ordered((double)(float)source.Lo, (double)(float)source.Hi)` — PowerBasic.Compiler/Ir/Passes/ConversionRangeCheckElim.cs:64
- method `TryRange(cast.Value, block, integers, depth - 1, active)` — PowerBasic.Compiler/Ir/Passes/ConversionRangeCheckElim.cs:68
- method `foreach(var incoming in phi.Operands)` — PowerBasic.Compiler/Ir/Passes/ConversionRangeCheckElim.cs:77

### CorrelatedValueProp.cs  `C#, 54 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/CorrelatedValueProp.cs:1
- class `CorrelatedValueProp` — Correlated value propagation: when a block ends in condbr (icmp eq x, C), T, F — PowerBasic.Compiler/Ir/Passes/CorrelatedValueProp.cs:10
- method `if(cmp.Lhs is IrConstant lc && cmp.Rhs is not IrConstant)` — PowerBasic.Compiler/Ir/Passes/CorrelatedValueProp.cs:25
- method `foreach` — PowerBasic.Compiler/Ir/Passes/CorrelatedValueProp.cs:32
- method `if(dom.Dominates(t, ub))` — PowerBasic.Compiler/Ir/Passes/CorrelatedValueProp.cs:36

### CountedLoop.cs  `C#, 126 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/CountedLoop.cs:1
- record `CountedLoop` — A loop that runs a known number of times: the blocks it occupies, the counter it turns, and the — PowerBasic.Compiler/Ir/Passes/CountedLoop.cs:15
- method `if(ReferenceEquals(successor, header))` — PowerBasic.Compiler/Ir/Passes/CountedLoop.cs:67

### DataLayoutTransforms.cs  `C#, 1058 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:2
- record `IrDataLayoutTarget` — Target facts required by storage transforms whose profitability/correct representation is target-de… — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:6
- class `ArrayOfStructsToStructOfArrays` — O0320 — converts private arrays of packed scalar records into one scalar array per used field. — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:13
- class `FieldReordering` — O0321 — reorders private packed-record fields by static access frequency. — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:18
- class `HotColdFieldSplitting` — O0322 — splits infrequently accessed fields out of private packed-record arrays. — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:23
- class `StructurePackingByRange` — O0323 — narrows private integer record fields when every stored value fits a smaller integer storag… — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:28
- class `PointerCompression` — O0324 — stores same-region pointers as 16-bit element indices when the target pointer is wider. — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:33
- class `ArrayPaddingAlignment` — O0325 — rounds private scalar-array storage to a complete target vector. — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:38
- class `CacheConflictPadding` — O0326 — pads two-dimensional row strides that are exact cache-size multiples. — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:43
- class `DataTransposition` — O0327 — transposes private two-dimensional arrays when the innermost loop walks the strided dimensi… — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:49
- class `TemporaryArrayFusion` — O0328 — forwards a pure producer expression into a single same-index consumer and removes the tempo… — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:54
- class `ArrayContraction` — O0329 — contracts a one-element sliding-window array recurrence to a loop-carried SSA value. — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:59
- class `DataLayoutTransformCore` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:62
- enum `RecordMode` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:64
- class `Linear` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:66
- constructor `Linear` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:70
- method `Clone` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:72
- method `Add` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:79
- method `foreach(var (value, coefficient) in other.Terms)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:83
- method `if(next == 0)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:85
- method `Scale` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:95
- method `foreach(var key in this.Terms.Keys.ToArray())` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:99
- method `if(coefficient == 0)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:101
- method `DivideExact` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:111
- method `SameAs` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:120
- record `Access` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:125
- record `Field` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:130
- record `RecordShape` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:136
- method `if(!field.Type.IsInteger || field.Type.Bits <= 8 || field.Accesses.All(…` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:165
- method `foreach(var store in stores)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:171
- method `if(store.Parent is null)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:172
- method `foreach(var user in gep.Users)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:204
- method `switch(user)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:205
- method `if(store.Value is IrNullPtr)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:208
- method `if(store.Value is not IrGep target || target.ElementType is null)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:210
- method `if(store.Parent is null)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:214
- method `if(range.IsTop || range.IsEmpty || range.Lo < 0 || range.Hi >= ushort.M…` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:216
- method `ReferenceEquals(load.Pointer, gep)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:219
- method `foreach(var user in gep.Users.ToList())` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:232
- method `switch(user)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:233
- method `if(store.Value is IrNullPtr)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:236
- method `if(gep.HasNoUsers)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:260
- method `if(!logical.DivideExact(elementBytes) || !logical.Terms.TryGetValue(row…` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:316
- method `if(!elements.DivideExact(elementBytes) || !elements.Terms.TryGetValue(r…` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:361
- method `if(other.Key is null)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:366
- method `if(insideStores.Count != 1 || insideLoads.Count != 1 || outsideStores.C…` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:454
- method `if(!TryCounterProgression(loop, out var firstCounter, out var step) || …` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:456
- method `if(current is null || previous is null || current.Value - previous.Valu…` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:460
- method `if(!ConstantElement(outsideStores[0], elementBytes, out var initialInde…` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:462
- method `if(initialIndex != expectedInitial || finalIndex != expectedFinal)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:473
- method `if(seedStore.Parent is null || recurrenceStore.Parent is null || finalL…` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:479
- method `CleanupDeadGeps(root)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:494
- method `if(root.HasNoUsers)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:496
- method `if(!index.DivideExact(shape.Stride) || BuildLinear(access.Instruction.P…` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:524
- method `SetPointer(access.Instruction, pointer)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:527
- method `if(!index.DivideExact(shape.Stride) || TryScaleCopy(index, shape.Stride…` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:552
- method `if(!index.DivideExact(shape.Stride))` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:587
- method `if(cold.Contains(field))` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:589
- method `if(TryScaleCopy(index, hotStride) is not { } bytes)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:592
- method `if(cold.Contains(field))` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:605
- method `SetPointer(access.Instruction, pointer)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:609
- method `if(!index.DivideExact(shape.Stride) || TryScaleCopy(index, stride) is n…` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:635
- method `if(BuildLinear(access.Instruction.Parent!, access.Instruction, rewritte…` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:644
- method `if(access.Instruction is IrLoad load)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:648
- method `if(storedType.SameStorage(store.Value.Type))` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:660
- method `if(coefficient != 0)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:701
- method `if(coefficient % stride != 0)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:711
- method `if(field.Size != access.BytesWide || !field.Type.SameStorage(access.Val…` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:715
- method `if(ordered[j].Offset < ordered[i].Offset + ordered[i].Size)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:722
- method `TryPointerLinear(load.Pointer, root, out var bytes)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:744
- method `TryPointerLinear(store.Pointer, root, out var bytes)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:750
- method `switch(user)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:766
- method `ReferenceEquals(load.Pointer, pointer)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:770
- method `ReferenceEquals(store.Pointer, pointer)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:772
- method `if(IrAliasAnalysis.StorageBytes(element) is not { } elementBytes || !pa…` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:788
- method `if(!TryLinear(binary.Lhs, out var left, depth + 1) || !TryLinear(binary…` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:807
- method `TryLinear(cast.Value, out expression, depth + 1)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:818
- method `if(seen.Add(gep))` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:895
- method `if(candidate != stride || !ReferenceEquals(candidateTerm, strided.Key))` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:940
- method `Clone` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:1009
- method `ClonePointer` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:1043
- method `IrGep(basePtr!, offset!, et)` — PowerBasic.Compiler/Ir/Passes/DataLayoutTransforms.cs:1052

### Dce.cs  `C#, 32 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/Dce.cs:1
- class `Dce` — Dead-code elimination: removes instructions with no users and no side effects, — PowerBasic.Compiler/Ir/Passes/Dce.cs:9
- method `foreach` — PowerBasic.Compiler/Ir/Passes/Dce.cs:19

### DeadLoopElimination.cs  `C#, 153 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/DeadLoopElimination.cs:1
- class `DeadLoopElimination` — Deletes a counted loop that computes nothing anyone reads. — PowerBasic.Compiler/Ir/Passes/DeadLoopElimination.cs:42
- method `foreach(var successor in terminator.Successors)` — PowerBasic.Compiler/Ir/Passes/DeadLoopElimination.cs:81
- method `if(!region.Contains(successor))` — PowerBasic.Compiler/Ir/Passes/DeadLoopElimination.cs:105
- method `if(HasEffect(instruction))` — PowerBasic.Compiler/Ir/Passes/DeadLoopElimination.cs:111
- method `foreach(var user in instruction.Users)` — PowerBasic.Compiler/Ir/Passes/DeadLoopElimination.cs:113
- method `if(ReferenceEquals(conditional.IfTrue, header))` — PowerBasic.Compiler/Ir/Passes/DeadLoopElimination.cs:145
- method `if(ReferenceEquals(conditional.IfFalse, header))` — PowerBasic.Compiler/Ir/Passes/DeadLoopElimination.cs:147

### DeadStoreElim.cs  `C#, 43 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/DeadStoreElim.cs:2
- class `DeadStoreElim` — Intra-block dead-store elimination for memory: a store is dead if a later store in — PowerBasic.Compiler/Ir/Passes/DeadStoreElim.cs:12
- method `foreach` — PowerBasic.Compiler/Ir/Passes/DeadStoreElim.cs:18
- method `switch(inst)` — PowerBasic.Compiler/Ir/Passes/DeadStoreElim.cs:20

### DemandedBits.cs  `C#, 88 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/DemandedBits.cs:1
- class `DemandedBits` — Eliminates work that only affects bits discarded by a later truncation. This is deliberately a — PowerBasic.Compiler/Ir/Passes/DemandedBits.cs:9
- method `if` — PowerBasic.Compiler/Ir/Passes/DemandedBits.cs:17
- method `if` — PowerBasic.Compiler/Ir/Passes/DemandedBits.cs:22

### EqualitySaturation.cs  `C#, 272 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/EqualitySaturation.cs:1
- class `EqualitySaturation` — Bounded equality saturation for pure integer expression trees. Instead of committing to the first — PowerBasic.Compiler/Ir/Passes/EqualitySaturation.cs:9
- method `foreach(var candidate in RewriteEverywhere(expression, context))` — PowerBasic.Compiler/Ir/Passes/EqualitySaturation.cs:44
- method `if(!seen.Add(normalized))` — PowerBasic.Compiler/Ir/Passes/EqualitySaturation.cs:48
- method `if(Better(normalized, best, context))` — PowerBasic.Compiler/Ir/Passes/EqualitySaturation.cs:51
- method `if(seen.Count >= _candidateBudget)` — PowerBasic.Compiler/Ir/Passes/EqualitySaturation.cs:54
- method `Constant(type, folded)` — PowerBasic.Compiler/Ir/Passes/EqualitySaturation.cs:80
- method `if(b.Left is Binary { Op: IrBinaryOp.Add } addLeft)` — PowerBasic.Compiler/Ir/Passes/EqualitySaturation.cs:93
- method `if(b.Left is Binary { Op: IrBinaryOp.Xor } xorLeft)` — PowerBasic.Compiler/Ir/Passes/EqualitySaturation.cs:127
- method `if(b.Right is Binary { Op: IrBinaryOp.Xor } xorRight)` — PowerBasic.Compiler/Ir/Passes/EqualitySaturation.cs:131
- method `if(common.Equals(otherCommon))` — PowerBasic.Compiler/Ir/Passes/EqualitySaturation.cs:156
- method `return(expression.Right, expression.Left)` — PowerBasic.Compiler/Ir/Passes/EqualitySaturation.cs:165
- record `Expr` — PowerBasic.Compiler/Ir/Passes/EqualitySaturation.cs:231
- record `Leaf` — PowerBasic.Compiler/Ir/Passes/EqualitySaturation.cs:233
- record `Constant` — PowerBasic.Compiler/Ir/Passes/EqualitySaturation.cs:234
- record `Binary` — PowerBasic.Compiler/Ir/Passes/EqualitySaturation.cs:235
- class `Context` — PowerBasic.Compiler/Ir/Passes/EqualitySaturation.cs:236
- method `Import` — PowerBasic.Compiler/Ir/Passes/EqualitySaturation.cs:240
- method `Key` — PowerBasic.Compiler/Ir/Passes/EqualitySaturation.cs:250
- method `Materialize` — PowerBasic.Compiler/Ir/Passes/EqualitySaturation.cs:257
- method `Insert` — PowerBasic.Compiler/Ir/Passes/EqualitySaturation.cs:264

### FloatDemotion.cs  `C#, 202 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/FloatDemotion.cs:1
- class `FloatDemotion` — O0012 — float demotion. PowerBASIC types a bare variable name SINGLE, so most DOS-era loop — PowerBasic.Compiler/Ir/Passes/FloatDemotion.cs:24
- method `if(phi.Parent is not null && phi.Type.IsIeeeFloat && Demote(phi))` — PowerBasic.Compiler/Ir/Passes/FloatDemotion.cs:37
- method `ReferenceEquals(phi.IncomingFrom(predecessor), step)` — PowerBasic.Compiler/Ir/Passes/FloatDemotion.cs:133

### FrameElision.cs  `C#, 29 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/FrameElision.cs:1
- class `FrameElision` — O0070 — stack-frame elision eligibility. — PowerBasic.Compiler/Ir/Passes/FrameElision.cs:19

### FunctionSummaries.cs  `C#, 191 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/FunctionSummaries.cs:1
- class `FunctionSummaries` — O0161 — per-procedure mod/ref summaries, computed once over the call graph so every other pass can — PowerBasic.Compiler/Ir/Passes/FunctionSummaries.cs:28
- record `Summary` — What calling a function may do to memory. — PowerBasic.Compiler/Ir/Passes/FunctionSummaries.cs:31
- method `new(true, true)` — PowerBasic.Compiler/Ir/Passes/FunctionSummaries.cs:117
- method `if(function.IsDeclaration)` — PowerBasic.Compiler/Ir/Passes/FunctionSummaries.cs:124
- method `if(current is { ReadsMemory: true, WritesMemory: true })` — PowerBasic.Compiler/Ir/Passes/FunctionSummaries.cs:127
- method `foreach(var instruction in function.AllInstructions)` — PowerBasic.Compiler/Ir/Passes/FunctionSummaries.cs:131
- method `if(merged == current)` — PowerBasic.Compiler/Ir/Passes/FunctionSummaries.cs:133
- method `Union(current, known.For(callee))` — PowerBasic.Compiler/Ir/Passes/FunctionSummaries.cs:150
- method `if(instruction is IrCall { Callee: IrFunction callee } call && call.Has…` — PowerBasic.Compiler/Ir/Passes/FunctionSummaries.cs:180

### GlobalDce.cs  `C#, 42 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/GlobalDce.cs:1
- class `GlobalDce` — Module-level global dead-code elimination (LLVM's globaldce): removes functions and global — PowerBasic.Compiler/Ir/Passes/GlobalDce.cs:12
- method `if(function.HasNoUsers && !IsEntry(function))` — PowerBasic.Compiler/Ir/Passes/GlobalDce.cs:22

### Gvn.cs  `C#, 110 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/Gvn.cs:2
- class `Gvn` — Global value numbering by dominator-tree scoped hashing: two pure instructions — PowerBasic.Compiler/Ir/Passes/Gvn.cs:14
- class `Context` — PowerBasic.Compiler/Ir/Passes/Gvn.cs:37
- method `Visit` — PowerBasic.Compiler/Ir/Passes/Gvn.cs:43
- method `if(key is null)` — PowerBasic.Compiler/Ir/Passes/Gvn.cs:48
- method `if(this._table.TryGetValue(key, out var leader))` — PowerBasic.Compiler/Ir/Passes/Gvn.cs:50
- method `if` — PowerBasic.Compiler/Ir/Passes/Gvn.cs:59
- method `foreach` — PowerBasic.Compiler/Ir/Passes/Gvn.cs:63
- method `KeyOf` — PowerBasic.Compiler/Ir/Passes/Gvn.cs:67
- method `Pair` — PowerBasic.Compiler/Ir/Passes/Gvn.cs:81
- method `Operand` — PowerBasic.Compiler/Ir/Passes/Gvn.cs:87
- method `IdOf` — PowerBasic.Compiler/Ir/Passes/Gvn.cs:95

### IfConversion.cs  `C#, 118 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/IfConversion.cs:1
- class `IfConversion` — If-conversion: turns a simple diamond into branchless selects. When a block — PowerBasic.Compiler/Ir/Passes/IfConversion.cs:11
- method `foreach` — PowerBasic.Compiler/Ir/Passes/IfConversion.cs:32
- method `if(vt is null || ve is null)` — PowerBasic.Compiler/Ir/Passes/IfConversion.cs:36
- method `foreach(var inst in dead.Instructions.ToList())` — PowerBasic.Compiler/Ir/Passes/IfConversion.cs:47

### Inliner.cs  `C#, 110 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/Inliner.cs:1
- class `Inliner` — Function inlining for direct calls to non-recursive defined callees within a size — PowerBasic.Compiler/Ir/Passes/Inliner.cs:11
- method `if(call.Parent is not null && call.Callee is IrFunction callee && !call…` — PowerBasic.Compiler/Ir/Passes/Inliner.cs:38
- method `InlineCall(call, callee, fn, inlined)` — PowerBasic.Compiler/Ir/Passes/Inliner.cs:40
- method `if(ret.HasValue)` — PowerBasic.Compiler/Ir/Passes/Inliner.cs:87
- method `foreach(var (value, from) in returns)` — PowerBasic.Compiler/Ir/Passes/Inliner.cs:100

### InstCombine.cs  `C#, 323 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/InstCombine.cs:2
- class `InstCombine` — Peephole instruction simplification: constant folding plus the standard algebraic — PowerBasic.Compiler/Ir/Passes/InstCombine.cs:11
- method `if` — PowerBasic.Compiler/Ir/Passes/InstCombine.cs:33
- method `if(r is IrConstantInt subC && !IsZero(r))` — canonicalize x - C into x + (-C) so add-chain constant merging applies — PowerBasic.Compiler/Ir/Passes/InstCombine.cs:98
- method `if(l is IrBinary { Op: IrBinaryOp.Add } la)` — (a + b) - a -> b ; (a + b) - b -> a — PowerBasic.Compiler/Ir/Passes/InstCombine.cs:103
- method `if(r is IrConstantInt rb && l is IrBinary shiftInner && shiftInner.Op =…` — (x shift a) shift b -> x shift (a+b) for the same shift op, when the total stays in range — PowerBasic.Compiler/Ir/Passes/InstCombine.cs:149
- method `if(b.Op == IrBinaryOp.UDiv && Pow2Shift(r) is { } sd)` — PowerBasic.Compiler/Ir/Passes/InstCombine.cs:155
- method `if(b.Op == IrBinaryOp.URem && r is IrConstantInt rc && Pow2Shift(rc) is…` — PowerBasic.Compiler/Ir/Passes/InstCombine.cs:160
- method `IrCast(c.Op, wider.Value, c.Type)` — PowerBasic.Compiler/Ir/Passes/InstCombine.cs:173
- method `IrCast(IrCastOp.Trunc, innerTrunc.Value, c.Type)` — PowerBasic.Compiler/Ir/Passes/InstCombine.cs:177
- method `IrCast(ext.Op, ext.Value, c.Type)` — PowerBasic.Compiler/Ir/Passes/InstCombine.cs:185
- method `IrCast(IrCastOp.Trunc, ext.Value, c.Type)` — PowerBasic.Compiler/Ir/Passes/InstCombine.cs:186
- method `IrCmp(inverse, cmp.Lhs, cmp.Rhs)` — PowerBasic.Compiler/Ir/Passes/InstCombine.cs:234

### IntegerRecovery.cs  `C#, 95 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/IntegerRecovery.cs:1
- class `IntegerRecovery` — Recovers integer arithmetic from the floating-point form the front end emits for PowerBASIC's — PowerBasic.Compiler/Ir/Passes/IntegerRecovery.cs:15
- method `if(instr is IrCast { Op: IrCastOp.FPToSI or IrCastOp.FPToSIRound } cast…` — both spellings close a float-shaped integer tree: the rounding one because that is what an — PowerBasic.Compiler/Ir/Passes/IntegerRecovery.cs:29
- method `TryRecover(precision.Value, intType, block, at)` — PowerBasic.Compiler/Ir/Passes/IntegerRecovery.cs:60
- method `IsExactInteger` — PowerBasic.Compiler/Ir/Passes/IntegerRecovery.cs:61
- method `MapOp` — PowerBasic.Compiler/Ir/Passes/IntegerRecovery.cs:64

### IpConstantProp.cs  `C#, 144 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/IpConstantProp.cs:1
- class `IpConstantProp` — O0018 / O0159 — interprocedural constant propagation across the call graph, in both directions. — PowerBasic.Compiler/Ir/Passes/IpConstantProp.cs:30
- method `if(function.IsDeclaration || function.HasErrorHandler || !IsFullyVisibl…` — PowerBasic.Compiler/Ir/Passes/IpConstantProp.cs:38
- method `if(took > 0)` — PowerBasic.Compiler/Ir/Passes/IpConstantProp.cs:41
- method `if(i >= call.ArgCount)` — PowerBasic.Compiler/Ir/Passes/IpConstantProp.cs:97
- method `if(!IsConstant(argument) || (agreed is not null && !Same(agreed, argume…` — PowerBasic.Compiler/Ir/Passes/IpConstantProp.cs:102
- method `if` — PowerBasic.Compiler/Ir/Passes/IpConstantProp.cs:108
- method `if(!IsConstant(value) || (agreed is not null && !Same(agreed, value)))` — PowerBasic.Compiler/Ir/Passes/IpConstantProp.cs:127

### IrPassManager.cs  `C#, 286 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/IrPassManager.cs:1
- class `IrVerificationException` — Raised when is on and a pass leaves the IR malformed. — PowerBasic.Compiler/Ir/Passes/IrPassManager.cs:4
- class `IrPassManager` — Runs an ordered set of function passes, once or to a fixpoint. Each pass reports — PowerBasic.Compiler/Ir/Passes/IrPassManager.cs:17
- method `if(errors.Count > 0)` — PowerBasic.Compiler/Ir/Passes/IrPassManager.cs:55
- method `IrVerificationException(name, errors)` — PowerBasic.Compiler/Ir/Passes/IrPassManager.cs:56
- method `RunFunctions()` — PowerBasic.Compiler/Ir/Passes/IrPassManager.cs:88
- method `RunFunctions` — PowerBasic.Compiler/Ir/Passes/IrPassManager.cs:90

### LibraryCallRecognition.cs  `C#, 130 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/LibraryCallRecognition.cs:1
- class `LibraryCallRecognition` — O0330 — recognizes canonical counted byte fill/copy loops and replaces them with the existing LLVM — PowerBasic.Compiler/Ir/Passes/LibraryCallRecognition.cs:9
- method `if(header.Parent is null || CountedLoop.Match(function, header) is not …` — PowerBasic.Compiler/Ir/Passes/LibraryCallRecognition.cs:22
- method `if(TryReplace(module, function, loop))` — PowerBasic.Compiler/Ir/Passes/LibraryCallRecognition.cs:24
- method `Start(loop, sourceBase!, initial)` — PowerBasic.Compiler/Ir/Passes/LibraryCallRecognition.cs:74
- method `IrArgument(IrType.Ptr, 0)` — PowerBasic.Compiler/Ir/Passes/LibraryCallRecognition.cs:121

### Licm.cs  `C#, 129 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/Licm.cs:1
- class `Licm` — Loop-invariant code motion. For each natural loop (found from CFG back-edges via — PowerBasic.Compiler/Ir/Passes/Licm.cs:12
- record `Loop` — PowerBasic.Compiler/Ir/Passes/Licm.cs:30
- method `if(dom.Dominates(succ, block))` — PowerBasic.Compiler/Ir/Passes/Licm.cs:39
- method `while(stack.Count > 0)` — PowerBasic.Compiler/Ir/Passes/Licm.cs:44
- method `if(body.Add(n))` — PowerBasic.Compiler/Ir/Passes/Licm.cs:46
- method `if(!body.Contains(inst.Parent!))` — PowerBasic.Compiler/Ir/Passes/Licm.cs:74
- method `if(!AllOperandsOutside(inst, body))` — PowerBasic.Compiler/Ir/Passes/Licm.cs:76
- method `foreach(var inst in block.Instructions)` — PowerBasic.Compiler/Ir/Passes/Licm.cs:94

### LocalizeGlobals.cs  `C#, 87 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/LocalizeGlobals.cs:1
- class `LocalizeGlobals` — O0278 — global variable localization. A DIM SHARED that only one procedure ever touches is — PowerBasic.Compiler/Ir/Passes/LocalizeGlobals.cs:22
- method `Localize` — PowerBasic.Compiler/Ir/Passes/LocalizeGlobals.cs:36

### LookupTableElimination.cs  `C#, 115 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/LookupTableElimination.cs:1
- class `LookupTableElimination` — O0333 — removes 256-byte read-only tables when every possible byte index follows a cheaper exact — PowerBasic.Compiler/Ir/Passes/LookupTableElimination.cs:8
- enum `FormulaKind` — PowerBasic.Compiler/Ir/Passes/LookupTableElimination.cs:9
- record `Formula` — PowerBasic.Compiler/Ir/Passes/LookupTableElimination.cs:11
- record `Access` — PowerBasic.Compiler/Ir/Passes/LookupTableElimination.cs:12
- method `if(indexed.Parent?.Parent is { HasErrorHandler: true } or { HasInlineAs…` — PowerBasic.Compiler/Ir/Passes/LookupTableElimination.cs:51
- method `IrConstantInt(load.Type, formula.Constant)` — PowerBasic.Compiler/Ir/Passes/LookupTableElimination.cs:80

### LookupTableGeneration.cs  `C#, 184 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/LookupTableGeneration.cs:1
- class `LookupTableGeneration` — O0332 — evaluates a sufficiently expensive pure one-byte function over all 256 input patterns and — PowerBasic.Compiler/Ir/Passes/LookupTableGeneration.cs:8
- method `IrConstantInt(IrType.I16, (long)constant.ZeroExtended)` — PowerBasic.Compiler/Ir/Passes/LookupTableGeneration.cs:65
- method `TryBinary(binary, values, out var binaryValue)` — PowerBasic.Compiler/Ir/Passes/LookupTableGeneration.cs:83
- method `TryCompare(compare, values, out var comparison)` — PowerBasic.Compiler/Ir/Passes/LookupTableGeneration.cs:86
- method `TryCast(cast, values, out var castValue)` — PowerBasic.Compiler/Ir/Passes/LookupTableGeneration.cs:89
- method `ValueOf(select.Condition, values, out var condition)` — PowerBasic.Compiler/Ir/Passes/LookupTableGeneration.cs:92

### LoopInterchange.cs  `C#, 593 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:2
- class `LoopInterchange` — O0122 — interchange a rectangular two-level counted-loop nest when doing so makes the innermost — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:34
- record `Nest` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:37
- record `CounterInfo` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:49
- record `Affine2` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:57
- record `MemoryAccess` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:59
- record `Address2` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:66
- method `Apply(nest)` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:82
- method `new` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:132
- method `if` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:273
- method `if(IrAliasAnalysis.StorageBytes(elementType) is not { } elementBytes ||…` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:302
- method `checked(total.Outer * outerCounter.Step)` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:316
- method `Fits(affine, value.Type, outerCounter, innerCounter)` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:339
- method `Fits(affine, value.Type, outerCounter, innerCounter)` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:343
- method `Fits(affine, value.Type, outerCounter, innerCounter)` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:347
- method `Fits(affine, cast.Type, outerCounter, innerCounter)` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:355
- method `IsConstant(left)` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:376
- method `IsConstant(right)` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:379
- method `IsConstant(right)` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:382
- method `Fits` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:388
- method `if(!first.Writes && !second.Writes)` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:398
- method `if(IrAliasAnalysis.Alias(first.Pointer, first.AccessType, second.Pointe…` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:400
- method `if(!ReferenceEquals(first.Address.Root, second.Address.Root))` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:403
- method `if(i != j)` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:405
- method `if(!SelfAccessIsDisjoint(first.Address, nest.Outer.Trips, nest.Inner.Tr…` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:407
- method `IrCmp(nest.Inner.Test.Pred, newOuterCounter, nest.Inner.Test.Rhs)` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:451
- method `IrCmp(nest.Outer.Test.Pred, newInnerCounter, nest.Outer.Test.Rhs)` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:455
- method `IrBinary(IrBinaryOp.Add, newInnerCounter, nest.OuterCounter.StepConstant)` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:462
- method `IrBinary(IrBinaryOp.Add, newOuterCounter, nest.InnerCounter.StepConstant)` — PowerBasic.Compiler/Ir/Passes/LoopInterchange.cs:465

### LoopUnroll.cs  `C#, 217 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/LoopUnroll.cs:1
- class `LoopUnroll` — Full unrolling of a counted loop whose trip count is known at compile time - the first of the — PowerBasic.Compiler/Ir/Passes/LoopUnroll.cs:26
- record `Loop` — A recognized counted loop: the blocks that make it and the counter's constant progression. — PowerBasic.Compiler/Ir/Passes/LoopUnroll.cs:46
- method `foreach(var successor in outside.Successors)` — PowerBasic.Compiler/Ir/Passes/LoopUnroll.cs:102
- method `foreach(var user in instruction.Users)` — PowerBasic.Compiler/Ir/Passes/LoopUnroll.cs:119
- method `new` — PowerBasic.Compiler/Ir/Passes/LoopUnroll.cs:127
- method `Retarget(previousLatch, clones[loop.Body[0]])` — PowerBasic.Compiler/Ir/Passes/LoopUnroll.cs:186

### LoopUnswitch.cs  `C#, 188 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/LoopUnswitch.cs:1
- class `LoopUnswitch` — O0114 — loop unswitching. A conditional inside a loop whose condition is loop-INVARIANT is tested — PowerBasic.Compiler/Ir/Passes/LoopUnswitch.cs:23
- record `Loop` — PowerBasic.Compiler/Ir/Passes/LoopUnswitch.cs:41
- method `ReferenceEquals(onward.Target, header)` — PowerBasic.Compiler/Ir/Passes/LoopUnswitch.cs:77
- method `foreach(var successor in outside.Successors)` — PowerBasic.Compiler/Ir/Passes/LoopUnswitch.cs:112
- method `foreach(var successor in leaving.Successors)` — PowerBasic.Compiler/Ir/Passes/LoopUnswitch.cs:130
- method `new` — PowerBasic.Compiler/Ir/Passes/LoopUnswitch.cs:133
- method `foreach` — PowerBasic.Compiler/Ir/Passes/LoopUnswitch.cs:170

### Mem2Reg.cs  `C#, 181 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/Mem2Reg.cs:1
- class `Mem2Reg` — Promotes stack slots to SSA registers: an alloca whose only uses are direct, — PowerBasic.Compiler/Ir/Passes/Mem2Reg.cs:21
- method `ReferenceEquals(load.Pointer, a)` — PowerBasic.Compiler/Ir/Passes/Mem2Reg.cs:71
- method `ReferenceEquals(store.Pointer, a)` — PowerBasic.Compiler/Ir/Passes/Mem2Reg.cs:73
- method `if(user is IrStore store && store.Parent is { } b && dom.IsReachable(b))` — PowerBasic.Compiler/Ir/Passes/Mem2Reg.cs:90
- method `foreach` — PowerBasic.Compiler/Ir/Passes/Mem2Reg.cs:92
- method `if(!perBlock.ContainsKey(alloca))` — PowerBasic.Compiler/Ir/Passes/Mem2Reg.cs:96
- method `if(idf.Add(y))` — PowerBasic.Compiler/Ir/Passes/Mem2Reg.cs:112
- method `foreach(var (alloca, phi) in succPhis)` — PowerBasic.Compiler/Ir/Passes/Mem2Reg.cs:173
- method `Rename(child, dom, children, allocas, phis, reaching, deadMemoryOps)` — PowerBasic.Compiler/Ir/Passes/Mem2Reg.cs:178

### MemoryRoutineSpecialization.cs  `C#, 81 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/MemoryRoutineSpecialization.cs:1
- class `MemoryRoutineSpecialization` — O0339 — specializes tiny constant-size LLVM memory intrinsics into straight-line byte accesses. — PowerBasic.Compiler/Ir/Passes/MemoryRoutineSpecialization.cs:9

### OverflowCheckCoalescing.cs  `C#, 106 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/OverflowCheckCoalescing.cs:1
- class `OverflowCheckCoalescing` — Coalesces consecutive Error 6 branches when the code made speculative by delaying the first — PowerBasic.Compiler/Ir/Passes/OverflowCheckCoalescing.cs:25
- method `if(!TryMatchGuard(block, out var first))` — PowerBasic.Compiler/Ir/Passes/OverflowCheckCoalescing.cs:40
- method `if(!TryMatchGuard(middle, out var second))` — PowerBasic.Compiler/Ir/Passes/OverflowCheckCoalescing.cs:43
- method `if(ErrorCode(first.Trap, middle) != _OVERFLOW_ERROR || ErrorCode(second…` — PowerBasic.Compiler/Ir/Passes/OverflowCheckCoalescing.cs:45
- method `if(!SafeToSpeculate(middle, second.Branch))` — PowerBasic.Compiler/Ir/Passes/OverflowCheckCoalescing.cs:48
- record `Guard` — PowerBasic.Compiler/Ir/Passes/OverflowCheckCoalescing.cs:65
- method `checked((int)code.Value)` — PowerBasic.Compiler/Ir/Passes/OverflowCheckCoalescing.cs:90

### PhiCongruence.cs  `C#, 233 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/PhiCongruence.cs:1
- class `PhiCongruence` — O0111 — redundant induction-variable elimination. Equal loop-carried values are merged outright; — PowerBasic.Compiler/Ir/Passes/PhiCongruence.cs:30
- record `AffineIv` — PowerBasic.Compiler/Ir/Passes/PhiCongruence.cs:31
- method `if(group.Count < 2)` — PowerBasic.Compiler/Ir/Passes/PhiCongruence.cs:70
- method `if(split.Count == 0)` — PowerBasic.Compiler/Ir/Passes/PhiCongruence.cs:74
- method `foreach(var phi in split)` — PowerBasic.Compiler/Ir/Passes/PhiCongruence.cs:78
- method `for` — PowerBasic.Compiler/Ir/Passes/PhiCongruence.cs:111
- method `if(candidate.Parent is null || MatchAffineIv(candidate) is not { } righ…` — PowerBasic.Compiler/Ir/Passes/PhiCongruence.cs:114
- method `if(!Equals(left.Phi.Type, right.Phi.Type) || !ReferenceEquals(left.Entr…` — PowerBasic.Compiler/Ir/Passes/PhiCongruence.cs:116
- method `if(candidate.Users.Any(user => user is IrPhi))` — PowerBasic.Compiler/Ir/Passes/PhiCongruence.cs:121
- method `if(!delta.IsZero)` — PowerBasic.Compiler/Ir/Passes/PhiCongruence.cs:127
- method `if(latch is not null)` — PowerBasic.Compiler/Ir/Passes/PhiCongruence.cs:154
- method `if` — PowerBasic.Compiler/Ir/Passes/PhiCongruence.cs:160
- method `new(phi, entry, latch, start, step)` — PowerBasic.Compiler/Ir/Passes/PhiCongruence.cs:168

### PointerCheckElim.cs  `C#, 80 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/PointerCheckElim.cs:1
- class `PointerCheckElim` — Eliminates a pointer/handle null test whose result is already established by an explicit — PowerBasic.Compiler/Ir/Passes/PointerCheckElim.cs:21
- method `if(cmp.HasNoUsers || !TryNullTest(cmp, out var value, out var trueMeans…` — PowerBasic.Compiler/Ir/Passes/PointerCheckElim.cs:32
- method `if(KnownNullness(value, block, dom) is not { } isNull)` — PowerBasic.Compiler/Ir/Passes/PointerCheckElim.cs:34
- method `if(dom.Dominates(branch.IfTrue, block))` — PowerBasic.Compiler/Ir/Passes/PointerCheckElim.cs:50
- method `if(dom.Dominates(branch.IfFalse, block))` — PowerBasic.Compiler/Ir/Passes/PointerCheckElim.cs:52

### PolynomialEvaluation.cs  `C#, 135 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/PolynomialEvaluation.cs:1
- class `PolynomialEvaluation` — O0337 — rewrites integer polynomials in one value into Horner form when doing so removes — PowerBasic.Compiler/Ir/Passes/PolynomialEvaluation.cs:8
- method `if(!TryRead(binary.Lhs, type, ref variable, out var left) || !TryRead(b…` — PowerBasic.Compiler/Ir/Passes/PolynomialEvaluation.cs:67
- method `if(!value.Type.SameStorage(type))` — PowerBasic.Compiler/Ir/Passes/PolynomialEvaluation.cs:77
- method `if(variable is null)` — PowerBasic.Compiler/Ir/Passes/PolynomialEvaluation.cs:79
- method `if(!ReferenceEquals(variable, value))` — PowerBasic.Compiler/Ir/Passes/PolynomialEvaluation.cs:81
- method `if(IsZero(right[j], type))` — PowerBasic.Compiler/Ir/Passes/PolynomialEvaluation.cs:101
- method `if(i + j > _MAX_DEGREE)` — PowerBasic.Compiler/Ir/Passes/PolynomialEvaluation.cs:103

### RangeCheckElim.cs  `C#, 66 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/RangeCheckElim.cs:2
- class `RangeCheckElim` — Folds an integer comparison the range analysis decides, which is how a runtime trap that cannot — PowerBasic.Compiler/Ir/Passes/RangeCheckElim.cs:45
- method `if(!cmp.HasNoUsers && ranges.Decide(cmp, block) is { } outcome)` — PowerBasic.Compiler/Ir/Passes/RangeCheckElim.cs:57

### ReadOnlyGlobals.cs  `C#, 80 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/ReadOnlyGlobals.cs:1
- class `ReadOnlyGlobals` — O0165 — read-only global propagation. A module-level variable that nothing ever writes is a — PowerBasic.Compiler/Ir/Passes/ReadOnlyGlobals.cs:27
- method `foreach` — PowerBasic.Compiler/Ir/Passes/ReadOnlyGlobals.cs:37

### Reassociate.cs  `C#, 162 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/Reassociate.cs:1
- class `Reassociate` — O0061 — reassociation of associative, commutative integer chains into a canonical shape, so that — PowerBasic.Compiler/Ir/Passes/Reassociate.cs:26
- method `if(instruction is IrBinary root && instruction.Parent is not null && Is…` — PowerBasic.Compiler/Ir/Passes/Reassociate.cs:43
- method `if(!Flatten(inner, leaves))` — PowerBasic.Compiler/Ir/Passes/Reassociate.cs:75

### ReciprocalSequenceReuse.cs  `C#, 77 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/ReciprocalSequenceReuse.cs:1
- class `ReciprocalSequenceReuse` — O0338 — replaces repeated floating divisions by the same exact power-of-two constant with — PowerBasic.Compiler/Ir/Passes/ReciprocalSequenceReuse.cs:8
- method `if(block is null)` — PowerBasic.Compiler/Ir/Passes/ReciprocalSequenceReuse.cs:31

### RecurrenceClosedForm.cs  `C#, 78 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/RecurrenceClosedForm.cs:1
- class `RecurrenceClosedForm` — O0134 — closed forms for loop-carried recurrences. An accumulator whose only work is adding a — PowerBasic.Compiler/Ir/Passes/RecurrenceClosedForm.cs:27
- method `foreach` — PowerBasic.Compiler/Ir/Passes/RecurrenceClosedForm.cs:69

### RedundantMemory.cs  `C#, 62 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/RedundantMemory.cs:2
- class `RedundantMemory` — Intra-block load/store forwarding — the memory analogue of what mem2reg does for — PowerBasic.Compiler/Ir/Passes/RedundantMemory.cs:14
- method `foreach` — PowerBasic.Compiler/Ir/Passes/RedundantMemory.cs:21
- method `switch(inst)` — PowerBasic.Compiler/Ir/Passes/RedundantMemory.cs:23
- method `if(stored.TryGetValue(p, out var sv) && sv.Type.Equals(load.Type))` — PowerBasic.Compiler/Ir/Passes/RedundantMemory.cs:26
- method `Invalidate(stored, p, store.Value.Type)` — PowerBasic.Compiler/Ir/Passes/RedundantMemory.cs:41
- method `Invalidate(loaded, p, store.Value.Type)` — PowerBasic.Compiler/Ir/Passes/RedundantMemory.cs:42

### ScalarReplaceAggregates.cs  `C#, 183 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/ScalarReplaceAggregates.cs:1
- class `ScalarReplaceAggregates` — Scalar-replaces non-escaping byte-backed aggregates whose observed accesses name independent, — PowerBasic.Compiler/Ir/Passes/ScalarReplaceAggregates.cs:29
- record `Region` — PowerBasic.Compiler/Ir/Passes/ScalarReplaceAggregates.cs:30
- method `if(instruction is IrAlloca { Allocated: var allocated, Count: > 1 } all…` — PowerBasic.Compiler/Ir/Passes/ScalarReplaceAggregates.cs:42
- method `if(!TryAccessRegion(user, alloca, 0, alloca.Count, out var direct))` — PowerBasic.Compiler/Ir/Passes/ScalarReplaceAggregates.cs:60
- method `foreach(var indexed in gep.Users)` — A preceding fold can leave the address calculation behind after all of its memory users — PowerBasic.Compiler/Ir/Passes/ScalarReplaceAggregates.cs:70
- method `if(distinct[i] != distinct[j])` — PowerBasic.Compiler/Ir/Passes/ScalarReplaceAggregates.cs:87
- method `ReferenceEquals(load.Pointer, pointer)` — PowerBasic.Compiler/Ir/Passes/ScalarReplaceAggregates.cs:101
- method `foreach(var indexed in gep.Users.ToList())` — PowerBasic.Compiler/Ir/Passes/ScalarReplaceAggregates.cs:155
- method `switch(indexed)` — PowerBasic.Compiler/Ir/Passes/ScalarReplaceAggregates.cs:156

### ScalarReplaceArrays.cs  `C#, 125 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/ScalarReplaceArrays.cs:1
- class `ScalarReplaceArrays` — O0182 — small local array scalar replacement. — PowerBasic.Compiler/Ir/Passes/ScalarReplaceArrays.cs:29
- method `if(instruction is IrAlloca alloca && Splittable(alloca, out var stride)…` — PowerBasic.Compiler/Ir/Passes/ScalarReplaceArrays.cs:40
- method `AccessMatchesElement(user, alloca, alloca.Allocated)` — element zero, reached through the array pointer itself — PowerBasic.Compiler/Ir/Passes/ScalarReplaceArrays.cs:54
- method `when(gep.ElementType is null || gep.ElementType.SameStorage(alloca.Alloca…` — PowerBasic.Compiler/Ir/Passes/ScalarReplaceArrays.cs:56
- method `IrAlloca(alloca.Allocated)` — PowerBasic.Compiler/Ir/Passes/ScalarReplaceArrays.cs:112

### Sccp.cs  `C#, 264 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:1
- class `Sccp` — Sparse Conditional Constant Propagation (Wegman-Zadeck): solves the constant — PowerBasic.Compiler/Ir/Passes/Sccp.cs:12
- enum `State` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:13
- struct `Lat` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:15
- constructor `Lat(State s, IrConstant? c)` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:19
- field `Top` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:20
- field `Bottom` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:21
- method `Constant(IrConstant c)` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:22
- class `Solver` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:27
- method `Solve` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:35
- method `while(this._flow.Count > 0)` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:39
- method `foreach(var phi in to.Phis)` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:41
- method `if(this._execBlocks.Add(to))` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:43
- method `while(this._ssa.Count > 0)` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:46
- method `if(inst.Parent is null || !this._execBlocks.Contains(inst.Parent))` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:48
- method `if(inst.IsTerminator)` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:50
- method `if(inst is IrPhi phi)` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:52
- method `VisitBlock` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:60
- method `MarkExecutable` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:67
- method `AddEdge` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:72
- method `Get` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:77
- method `Set` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:83
- method `VisitPhi` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:91
- method `if(this._execEdges.Contains((phi.IncomingBlocks[i], phi.Parent!)))` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:95
- method `VisitValue` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:99
- method `EvalFoldable` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:112
- method `if(l.State == State.Top)` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:118
- method `if(l.State == State.Bottom)` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:120
- method `ConstOf` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:139
- method `HandleTerminator` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:141
- method `if(c.State == State.Top)` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:149
- method `if(c.State == State.Bottom)` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:151
- method `if(c.State == State.Top)` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:161
- method `if(c.State == State.Bottom)` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:163
- method `Rewrite` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:176
- method `if(inst.Type.IsVoid || inst is IrPhi { Parent: null })` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:182
- method `if(lat.State != State.Const)` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:185
- method `if(!this._execBlocks.Contains(block) || block.Terminator is not IrCondB…` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:193
- method `if(c.State != State.Const)` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:196
- method `RemoveUnreachable` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:207
- method `if(addressed.Parent is not null && reachable.Add(addressed))` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:216
- method `foreach(var s in stack.Pop().Successors)` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:219
- method `foreach(var phi in block.Phis.ToList())` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:225
- method `foreach` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:229
- method `Clone` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:236
- method `IsTrue` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:243
- method `StateEquals` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:245
- method `Meet` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:248
- method `ConstEquals` — PowerBasic.Compiler/Ir/Passes/Sccp.cs:255

### SimplifyCfg.cs  `C#, 326 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/SimplifyCfg.cs:1
- class `SimplifyCfg` — Control-flow graph cleanup. Safe, high-value canonicalizations tighten the many trivial blocks — PowerBasic.Compiler/Ir/Passes/SimplifyCfg.cs:10
- method `ReferenceEquals(cb.IfTrue, cb.IfFalse)` — PowerBasic.Compiler/Ir/Passes/SimplifyCfg.cs:37
- method `foreach` — PowerBasic.Compiler/Ir/Passes/SimplifyCfg.cs:74
- method `if(condition.IncomingFrom(pred) is not IrConstantInt incoming)` — PowerBasic.Compiler/Ir/Passes/SimplifyCfg.cs:78
- method `if(ReferenceEquals(target, block) || ReferenceEquals(target, pred))` — PowerBasic.Compiler/Ir/Passes/SimplifyCfg.cs:82
- method `if(!TryTranslateSuccessorPhis(block, pred, target, out var translated))` — PowerBasic.Compiler/Ir/Passes/SimplifyCfg.cs:84
- method `foreach(var (phi, value) in translated)` — The old path was pred -> block -> target. A target phi therefore saw the value attached to — PowerBasic.Compiler/Ir/Passes/SimplifyCfg.cs:90
- method `foreach(var phi in phis)` — PowerBasic.Compiler/Ir/Passes/SimplifyCfg.cs:92
- method `if` — PowerBasic.Compiler/Ir/Passes/SimplifyCfg.cs:112
- method `if(reachable.Add(s))` — PowerBasic.Compiler/Ir/Passes/SimplifyCfg.cs:136
- method `foreach(var pred in phi.IncomingBlocks.ToList())` — PowerBasic.Compiler/Ir/Passes/SimplifyCfg.cs:145
- method `if(TrivialValue(phi) is { } value)` — PowerBasic.Compiler/Ir/Passes/SimplifyCfg.cs:162
- method `if(!ReferenceEquals(only, op))` — PowerBasic.Compiler/Ir/Passes/SimplifyCfg.cs:179
- method `foreach` — PowerBasic.Compiler/Ir/Passes/SimplifyCfg.cs:221
- method `Retarget(pred.Terminator!, block, succ)` — PowerBasic.Compiler/Ir/Passes/SimplifyCfg.cs:227
- method `foreach` — PowerBasic.Compiler/Ir/Passes/SimplifyCfg.cs:228
- method `foreach` — PowerBasic.Compiler/Ir/Passes/SimplifyCfg.cs:248
- method `if(value is IrInstruction { Parent: { } valueBlock } && ReferenceEquals…` — PowerBasic.Compiler/Ir/Passes/SimplifyCfg.cs:251
- method `if(value is IrInstruction { Parent: { } translatedBlock } && ReferenceE…` — PowerBasic.Compiler/Ir/Passes/SimplifyCfg.cs:256
- method `ReferenceEquals(br.Target, from)` — PowerBasic.Compiler/Ir/Passes/SimplifyCfg.cs:272
- method `if(ReferenceEquals(cb.IfTrue, from))` — PowerBasic.Compiler/Ir/Passes/SimplifyCfg.cs:276
- method `if(ReferenceEquals(cb.IfFalse, from))` — PowerBasic.Compiler/Ir/Passes/SimplifyCfg.cs:278
- method `InvalidOperationException("prechecked forwarding edge cannot be retargeted")` — PowerBasic.Compiler/Ir/Passes/SimplifyCfg.cs:282
- method `foreach(var phi in after.Phis)` — PowerBasic.Compiler/Ir/Passes/SimplifyCfg.cs:316

### StaticSearchRecognition.cs  `C#, 190 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/StaticSearchRecognition.cs:1
- class `StaticSearchRecognition` — O0334/O0335 — recognizes a counted linear search over a read-only constant integer table. A — PowerBasic.Compiler/Ir/Passes/StaticSearchRecognition.cs:9
- record `Search` — PowerBasic.Compiler/Ir/Passes/StaticSearchRecognition.cs:13
- method `if(header.Parent is null || CountedLoop.Match(function, header) is not …` — PowerBasic.Compiler/Ir/Passes/StaticSearchRecognition.cs:24
- method `Rewrite(function, loop, search)` — PowerBasic.Compiler/Ir/Passes/StaticSearchRecognition.cs:27
- method `if(regionBlock.Terminator is { } terminator)` — PowerBasic.Compiler/Ir/Passes/StaticSearchRecognition.cs:63
- method `if(indexed is not IrLoad load || !ReferenceEquals(load.Pointer, gep))` — PowerBasic.Compiler/Ir/Passes/StaticSearchRecognition.cs:116

### StringAppendInPlace.cs  `C#, 171 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/StringAppendInPlace.cs:1
- class `StringAppendInPlace` — Turns a concatenation whose LEFT operand is a fresh, dead string temporary into an APPEND onto — PowerBasic.Compiler/Ir/Passes/StringAppendInPlace.cs:46
- method `if(call.Parent is null || call.Callee is not IrFunction { Name: _CONCAT…` — PowerBasic.Compiler/Ir/Passes/StringAppendInPlace.cs:74
- method `if(touched)` — PowerBasic.Compiler/Ir/Passes/StringAppendInPlace.cs:78

### StringByteRead.cs  `C#, 78 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/StringByteRead.cs:1
- class `StringByteRead` — Reads one character of a string as a BYTE instead of building a one-character string and asking — PowerBasic.Compiler/Ir/Passes/StringByteRead.cs:29
- method `if(call.Parent is null || call.Callee is not IrFunction { Name: _ASC } …` — PowerBasic.Compiler/Ir/Passes/StringByteRead.cs:44
- method `if(SingleCharacterSource(call.GetOperand(1)) is not var (substring, sou…` — PowerBasic.Compiler/Ir/Passes/StringByteRead.cs:46

### StringCapacityHoisting.cs  `C#, 215 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/StringCapacityHoisting.cs:1
- class `StringCapacityHoisting` — Removes the per-iteration growth checks from a side-effect-free counted string-build loop by — PowerBasic.Compiler/Ir/Passes/StringCapacityHoisting.cs:26
- method `if(Match(header) is not { } loop || !TryRewrite(module, loop))` — PowerBasic.Compiler/Ir/Passes/StringCapacityHoisting.cs:43
- record `Loop` — PowerBasic.Compiler/Ir/Passes/StringCapacityHoisting.cs:50
- method `foreach(var successor in block.Successors)` — PowerBasic.Compiler/Ir/Passes/StringCapacityHoisting.cs:136
- method `new` — PowerBasic.Compiler/Ir/Passes/StringCapacityHoisting.cs:139

### StringCompareEquality.cs  `C#, 70 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/StringCompareEquality.cs:1
- class `StringCompareEquality` — Routes a string comparison whose answer is only ever tested against zero to the runtime's — PowerBasic.Compiler/Ir/Passes/StringCompareEquality.cs:31
- method `if(call.Callee is not IrFunction { Name: _GENERAL } || call.ArgCount !=…` — PowerBasic.Compiler/Ir/Passes/StringCompareEquality.cs:48
- method `if(call.Users.Count == 0 || !call.Users.All(IsEqualityTest))` — PowerBasic.Compiler/Ir/Passes/StringCompareEquality.cs:50

### StringConcatChain.cs  `C#, 119 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/StringConcatChain.cs:1
- class `StringConcatChain` — Builds a chain of three or more string concatenations with ONE allocation instead of one per — PowerBasic.Compiler/Ir/Passes/StringConcatChain.cs:39
- method `if(call.Parent is null || !IsConcat(call))` — PowerBasic.Compiler/Ir/Passes/StringConcatChain.cs:57
- method `if(IsInnerNode(call))` — PowerBasic.Compiler/Ir/Passes/StringConcatChain.cs:59
- method `if(Flatten(call) is not { } leaves)` — PowerBasic.Compiler/Ir/Passes/StringConcatChain.cs:61
- method `Collapse(module, call, leaves)` — PowerBasic.Compiler/Ir/Passes/StringConcatChain.cs:63
- method `Collect` — PowerBasic.Compiler/Ir/Passes/StringConcatChain.cs:84
- method `Erase` — PowerBasic.Compiler/Ir/Passes/StringConcatChain.cs:108
- method `if(operand is IrCall child && IsConcat(child) && child.HasNoUsers)` — PowerBasic.Compiler/Ir/Passes/StringConcatChain.cs:114
- method `Erase(child)` — PowerBasic.Compiler/Ir/Passes/StringConcatChain.cs:115

### StringConstantFold.cs  `C#, 194 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/StringConstantFold.cs:1
- class `StringConstantFold` — Answers at compile time the string operations whose operands are literals, and drops the ones — PowerBasic.Compiler/Ir/Passes/StringConstantFold.cs:38
- method `if(changes == 0)` — PowerBasic.Compiler/Ir/Passes/StringConstantFold.cs:58

### StringEmptinessTest.cs  `C#, 111 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/StringEmptinessTest.cs:1
- class `StringEmptinessTest` — Answers "is this string empty?" by looking at the handle instead of calling the runtime. — PowerBasic.Compiler/Ir/Passes/StringEmptinessTest.cs:32
- method `if(compare.Parent is null || compare.Pred is not (IrCmpPred.Eq or IrCmp…` — PowerBasic.Compiler/Ir/Passes/StringEmptinessTest.cs:48
- method `if(AgainstZero(compare) is not IrCall answer)` — PowerBasic.Compiler/Ir/Passes/StringEmptinessTest.cs:50
- method `if(EmptinessSubject(answer) is not { } subject)` — PowerBasic.Compiler/Ir/Passes/StringEmptinessTest.cs:52
- method `foreach(var consumed in subject.Consumed)` — PowerBasic.Compiler/Ir/Passes/StringEmptinessTest.cs:60
- method `if(literalIndex == 0)` — PowerBasic.Compiler/Ir/Passes/StringEmptinessTest.cs:88
- method `if(Borrowed(answer.GetOperand(literalIndex == 2 ? 1 : 2)) is not { } co…` — PowerBasic.Compiler/Ir/Passes/StringEmptinessTest.cs:90

### SwitchFormation.cs  `C#, 357 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:1
- class `SwitchFormation` — Recovers the DISPATCH a chain of comparisons is: a run of blocks that each test one integer value — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:60
- method `if(claimed.Add(value))` — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:124
- method `if(!ReferenceEquals(user.Parent, block))` — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:179
- method `AgainstZero(wrapper)` — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:196
- method `Leaf(compare, ref subject)` — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:199
- method `Evaluate(widened.Value, ref subject)` — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:201
- method `Combine(either, ref subject, union: true)` — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:203
- method `Combine(both, ref subject, union: false)` — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:205
- class `ValueSet` — A set of subject values as sorted, disjoint, non-adjacent closed intervals over the subject's — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:280
- constructor `ValueSet` — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:284
- method `Empty` — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:289
- method `Of` — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:291
- method `Count()` — How many values the set holds, saturated - a whole-domain set must not overflow a count. — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:296
- method `if(total > _MAX_VALUES)` — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:300
- method `Values` — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:305
- method `for(var value = lo; ; ++value)` — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:308
- method `if(value == hi)` — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:310
- method `Union` — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:314
- method `Intersect` — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:316
- method `foreach(var (otherLo, otherHi) in other._intervals)` — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:320
- method `if(low <= high)` — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:323
- method `Complement` — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:328
- method `if(lo > next)` — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:334
- method `if(next > max)` — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:337
- method `new(this._bits, result)` — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:338
- method `new(this._bits, result)` — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:341
- method `if(merged.Count > 0 && lo <= merged[^1].Hi + 1)` — PowerBasic.Compiler/Ir/Passes/SwitchFormation.cs:349

### TailRecursion.cs  `C#, 159 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/TailRecursion.cs:1
- class `TailRecursion` — Turns a function that calls ITSELF in tail position into a loop, so the recursion runs in constant — PowerBasic.Compiler/Ir/Passes/TailRecursion.cs:49
- method `if(next.Instructions.Any(i => !i.IsTerminator))` — PowerBasic.Compiler/Ir/Passes/TailRecursion.cs:140

### VerifiedArithmeticLowering.cs  `C#, 175 lines`
- namespace `PowerBasic.Compiler.Ir.Passes` — PowerBasic.Compiler/Ir/Passes/VerifiedArithmeticLowering.cs:1
- class `VerifiedArithmeticLowering` — Strength-reduces 16-bit constant arithmetic only after the candidate formula has been exhaustively — PowerBasic.Compiler/Ir/Passes/VerifiedArithmeticLowering.cs:8
- method `if(cached is { } candidate && !VerifyMultiply(factor, candidate))` — PowerBasic.Compiler/Ir/Passes/VerifiedArithmeticLowering.cs:90
- method `new(shift, Subtract: false, Negate: factor < 0)` — PowerBasic.Compiler/Ir/Passes/VerifiedArithmeticLowering.cs:109
- method `new(shift, Subtract: true, Negate: factor < 0)` — PowerBasic.Compiler/Ir/Passes/VerifiedArithmeticLowering.cs:111
- method `unchecked((ushort)(shifted - x))` — PowerBasic.Compiler/Ir/Passes/VerifiedArithmeticLowering.cs:121
- record `MultiplyPlan` — PowerBasic.Compiler/Ir/Passes/VerifiedArithmeticLowering.cs:172

## PowerBasic.Compiler/Numerics/

### Extended80.cs  `C#, 498 lines`
- namespace `PowerBasic.Compiler.Numerics` — PowerBasic.Compiler/Numerics/Extended80.cs:2
- struct `Extended80` — An x87 double-extended float, in software: one sign bit, fifteen exponent bits and a — PowerBasic.Compiler/Numerics/Extended80.cs:38
- method `Overflowed(sign, mode)` — PowerBasic.Compiler/Numerics/Extended80.cs:118
- field `_EXTRA` — enough quotient bits that the rounding decision is never made on a guess: the significands are — PowerBasic.Compiler/Numerics/Extended80.cs:277
- field `_EXTRA` — halving the scale needs it even, and the root of a 64-bit significand is only 32 bits, so the — PowerBasic.Compiler/Numerics/Extended80.cs:296
- method `ArgumentException("an extended real is ten bytes", nameof(bytes))` — PowerBasic.Compiler/Numerics/Extended80.cs:488

### FloatRounding.cs  `C#, 20 lines`
- namespace `PowerBasic.Compiler.Numerics` — PowerBasic.Compiler/Numerics/FloatRounding.cs:1
- enum `FloatRounding` — The four rounding directions, numbered as the x87 control word's RC field numbers them, so a — PowerBasic.Compiler/Numerics/FloatRounding.cs:7

## PowerBasic.Compiler/Runtime/

### DataPool.cs  `C#, 99 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/DataPool.cs:2
- class `DataPool` — The order DATA items and their labels appear in a module, read once and shared by both code — PowerBasic.Compiler/Runtime/DataPool.cs:27
- record `Entry` — One pool contribution in source order: a label that names the offset reached so far, or an item. — PowerBasic.Compiler/Runtime/DataPool.cs:30
- method `new(label.Name, null)` — PowerBasic.Compiler/Runtime/DataPool.cs:41
- method `foreach(var item in data.Items)` — PowerBasic.Compiler/Runtime/DataPool.cs:45
- method `new(null, item)` — PowerBasic.Compiler/Runtime/DataPool.cs:46
- method `foreach(var entry in Walk(i.Then))` — PowerBasic.Compiler/Runtime/DataPool.cs:50
- method `foreach(var (_, body) in i.ElseIfs)` — PowerBasic.Compiler/Runtime/DataPool.cs:52
- method `if(i.Else is { } otherwise)` — PowerBasic.Compiler/Runtime/DataPool.cs:55
- method `foreach(var arm in select.Arms)` — PowerBasic.Compiler/Runtime/DataPool.cs:61
- method `foreach(var entry in Walk(f.Body))` — PowerBasic.Compiler/Runtime/DataPool.cs:67
- method `foreach(var entry in Walk(fe.Body))` — PowerBasic.Compiler/Runtime/DataPool.cs:72
- method `foreach(var entry in Walk(dl.Body))` — PowerBasic.Compiler/Runtime/DataPool.cs:77
- method `foreach(var entry in Walk(g.Body))` — PowerBasic.Compiler/Runtime/DataPool.cs:82
- method `foreach(var entry in Walk(t.Body))` — PowerBasic.Compiler/Runtime/DataPool.cs:87
- method `if(t.Catch is { } caught)` — PowerBasic.Compiler/Runtime/DataPool.cs:89
- method `if(t.Finally is { } cleanup)` — PowerBasic.Compiler/Runtime/DataPool.cs:92

### DosRuntime.ArrayDesc.cs  `C#, 72 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/DosRuntime.ArrayDesc.cs:2
- class `DosRuntime` — The array DESCRIPTOR an ARRAY SORT / ARRAY SCAN parameter block points at, built from arguments — PowerBasic.Compiler/Runtime/DosRuntime.ArrayDesc.cs:31

### DosRuntime.ArrayNum.cs  `C#, 482 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/DosRuntime.ArrayNum.cs:2
- class `DosRuntime` — ARRAY SORT / ARRAY SCAN over non-string (numeric) arrays. Every element kind — PowerBasic.Compiler/Runtime/DosRuntime.ArrayNum.cs:16

### DosRuntime.Arrays.cs  `C#, 193 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/DosRuntime.Arrays.cs:2
- class `DosRuntime` — Dynamic array storage: a bump allocator over the far array heap segment — PowerBasic.Compiler/Runtime/DosRuntime.Arrays.cs:13

### DosRuntime.BinaryStrings.cs  `C#, 52 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/DosRuntime.BinaryStrings.cs:2
- class `DosRuntime` — Bit-exact numeric/string record conversions used by the MKx$/CVx family. — PowerBasic.Compiler/Runtime/DosRuntime.BinaryStrings.cs:6

### DosRuntime.Capture.cs  `C#, 63 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/DosRuntime.Capture.cs:2
- class `DosRuntime` — Print CAPTURE, given labels so the IR path can reach it. — PowerBasic.Compiler/Runtime/DosRuntime.Capture.cs:32

### DosRuntime.Chain.cs  `C#, 248 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/DosRuntime.Chain.cs:2
- class `DosRuntime` — CHAIN / RUN support. COMMON variables travel through the temp file — PowerBasic.Compiler/Runtime/DosRuntime.Chain.cs:18

### DosRuntime.Core.cs  `C#, 399 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/DosRuntime.Core.cs:3
- class `DosRuntime` — Emits the DOS runtime kernel into the program image. Register conventions — PowerBasic.Compiler/Runtime/DosRuntime.Core.cs:26
- method `InvalidOperationException($"runtime label {property.Name} was never assigned")` — PowerBasic.Compiler/Runtime/DosRuntime.Core.cs:291
- method `InvalidOperationException($"runtime label {property.Name} was never assigned")` — PowerBasic.Compiler/Runtime/DosRuntime.Core.cs:314

### DosRuntime.CpuState.cs  `C#, 13 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/DosRuntime.CpuState.cs:2
- class `DosRuntime` — PowerBasic.Compiler/Runtime/DosRuntime.CpuState.cs:4

### DosRuntime.Ems.cs  `C#, 308 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/DosRuntime.Ems.cs:2
- class `DosRuntime` — HUGE (DOS 48h conventional memory) and VIRTUAL (EMS, int 67h) array support. — PowerBasic.Compiler/Runtime/DosRuntime.Ems.cs:17

### DosRuntime.Extras.cs  `C#, 305 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/DosRuntime.Extras.cs:2
- class `DosRuntime` — PB 3.x surface helpers added with the dialect/pointer wave. Conventions — PowerBasic.Compiler/Runtime/DosRuntime.Extras.cs:19

### DosRuntime.Fields.cs  `C#, 197 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/DosRuntime.Fields.cs:2
- class `DosRuntime` — FIELD support for RANDOM files. Field strings are ordinary heap strings of — PowerBasic.Compiler/Runtime/DosRuntime.Fields.cs:15

### DosRuntime.Files.cs  `C#, 1127 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/DosRuntime.Files.cs:3
- class `DosRuntime` — DOS handle-based file I/O. PB file numbers 1..15 map through the word table — PowerBasic.Compiler/Runtime/DosRuntime.Files.cs:33

### DosRuntime.Float.cs  `C#, 384 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/DosRuntime.Float.cs:3
- class `DosRuntime` — PowerBasic.Compiler/Runtime/DosRuntime.Float.cs:5

### DosRuntime.Graphics.cs  `C#, 898 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/DosRuntime.Graphics.cs:2
- class `DosRuntime` — Line drawing for SCREEN 13 (320x200x256, linear A000:y*320+x), built on the rt_pset pixel — PowerBasic.Compiler/Runtime/DosRuntime.Graphics.cs:18
- method `Edge` — PowerBasic.Compiler/Runtime/DosRuntime.Graphics.cs:803

### DosRuntime.Internals.cs  `C#, 209 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/DosRuntime.Internals.cs:2
- class `DosRuntime` — PB internal variables (pbvScrnCols, pbvScrnRows, ...) backed by runtime data — PowerBasic.Compiler/Runtime/DosRuntime.Internals.cs:9
- record `InternalVariable` — One internal variable: runtime data label, cell size in bytes, initial value. — PowerBasic.Compiler/Runtime/DosRuntime.Internals.cs:12

### DosRuntime.LowLevel.cs  `C#, 400 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/DosRuntime.LowLevel.cs:2
- class `DosRuntime` — Low-level services. Register conventions (everything not returned is preserved): — PowerBasic.Compiler/Runtime/DosRuntime.LowLevel.cs:20

### DosRuntime.Math.cs  `C#, 290 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/DosRuntime.Math.cs:2
- class `DosRuntime` — PowerBasic.Compiler/Runtime/DosRuntime.Math.cs:4

### DosRuntime.Memory.cs  `C#, 59 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/DosRuntime.Memory.cs:2
- class `DosRuntime` — Raw memory operations shared by UDTs, arrays, and fixed-width storage. — PowerBasic.Compiler/Runtime/DosRuntime.Memory.cs:6

### DosRuntime.Misc.cs  `C#, 711 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/DosRuntime.Misc.cs:2
- class `DosRuntime` — Console, keyboard, timing and conversion helpers. Conventions: — PowerBasic.Compiler/Runtime/DosRuntime.Misc.cs:17
- method `Map(int pb, int bios)` — PowerBasic.Compiler/Runtime/DosRuntime.Misc.cs:414

### DosRuntime.Print.cs  `C#, 243 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/DosRuntime.Print.cs:2
- class `DosRuntime` — PowerBasic.Compiler/Runtime/DosRuntime.Print.cs:4

### DosRuntime.Printer.cs  `C#, 54 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/DosRuntime.Printer.cs:2
- class `DosRuntime` — LPRINT's two halves, given labels so the IR path can reach them. — PowerBasic.Compiler/Runtime/DosRuntime.Printer.cs:30

### DosRuntime.Quad.cs  `C#, 233 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/DosRuntime.Quad.cs:2
- class `DosRuntime` — 64-bit (QUAD) integer helpers. Values normally ride the x87 stack; for — PowerBasic.Compiler/Runtime/DosRuntime.Quad.cs:11
- method `if(notLeftFirst)` — PowerBasic.Compiler/Runtime/DosRuntime.Quad.cs:33
- method `if(notResult)` — PowerBasic.Compiler/Runtime/DosRuntime.Quad.cs:37
- method `EmitNegate` — PowerBasic.Compiler/Runtime/DosRuntime.Quad.cs:64

### DosRuntime.Strings.cs  `C#, 2074 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/DosRuntime.Strings.cs:3
- class `DosRuntime` — Dynamic string runtime. Representation: a string value is a 2-byte handle, — PowerBasic.Compiler/Runtime/DosRuntime.Strings.cs:48

### DosRuntime.Strings2.cs  `C#, 2002 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/DosRuntime.Strings2.cs:2
- class `DosRuntime` — String runtime, part 2: character-set scanning (INSTR ANY / VERIFY), — PowerBasic.Compiler/Runtime/DosRuntime.Strings2.cs:18

### DosRuntime.Targeting.cs  `C#, 149 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/DosRuntime.Targeting.cs:2
- class `DosRuntime` — PowerBasic.Compiler/Runtime/DosRuntime.Targeting.cs:4

### DosRuntime.Trig.cs  `C#, 190 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/DosRuntime.Trig.cs:2
- class `DosRuntime` — PowerBasic.Compiler/Runtime/DosRuntime.Trig.cs:4

### InlineAsmExports.cs  `C#, 33 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/InlineAsmExports.cs:1
- class `InlineAsmExports` — The string-manager routines PowerBASIC documents as callable from inline assembly, and the — PowerBasic.Compiler/Runtime/InlineAsmExports.cs:16

### RuntimeIsaPolicy.cs  `C#, 113 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/RuntimeIsaPolicy.cs:1
- enum `IsaFallbackMode` — What code generation does only when an instruction is not natively supported by the selected — PowerBasic.Compiler/Runtime/RuntimeIsaPolicy.cs:9
- class `RuntimeIsaPolicy` — Per-compilation ISA policy. Rules are case-insensitive and may name an exact mnemonic, an ISA — PowerBasic.Compiler/Runtime/RuntimeIsaPolicy.cs:25

### RuntimeIsaState.cs  `C#, 48 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/RuntimeIsaState.cs:2
- class `RuntimeIsaState` — Named runtime storage used only by architecture emulation. Keeping names here gives the code — PowerBasic.Compiler/Runtime/RuntimeIsaState.cs:12
- class `DosRuntime` — PowerBasic.Compiler/Runtime/RuntimeIsaState.cs:32

### RuntimeTarget.cs  `C#, 251 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/RuntimeTarget.cs:2
- enum `RuntimeCpuFeatures` — Instruction-set extensions/coprocessors the selected DOS target explicitly permits. — PowerBasic.Compiler/Runtime/RuntimeTarget.cs:6
- record `RuntimeTarget` — Normalized compile-time x86 target shared by runtime specialization and inline-assembly validation. — PowerBasic.Compiler/Runtime/RuntimeTarget.cs:42

### SoftwareX87Backend.cs  `C#, 409 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/SoftwareX87Backend.cs:2
- class `SoftwareX87Backend` — 8086 software implementation of the x87 architectural stack. Values are stored in their native — PowerBasic.Compiler/Runtime/SoftwareX87Backend.cs:11
- method `if(st.Register.Index is < 0 or > 7)` — PowerBasic.Compiler/Runtime/SoftwareX87Backend.cs:154

### SoftwareX87Engine.Arithmetic.AddSub.cs  `C#, 164 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Arithmetic.AddSub.cs:2
- class `SoftwareX87Engine` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Arithmetic.AddSub.cs:4

### SoftwareX87Engine.Arithmetic.Dispatch.cs  `C#, 151 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Arithmetic.Dispatch.cs:2
- class `SoftwareX87Engine` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Arithmetic.Dispatch.cs:4
- method `Fail($"unknown software x87 arithmetic mnemonic {mnemonic}", out error)` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Arithmetic.Dispatch.cs:28
- method `Fail($"FI{mnemonic} expects word/dword integer memory", out error)` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Arithmetic.Dispatch.cs:44
- method `if(operands.Count == 2 && operands[0] is TextAssembler.ParsedAsmSt d &&…` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Arithmetic.Dispatch.cs:59
- method `Fail` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Arithmetic.Dispatch.cs:71

### SoftwareX87Engine.Arithmetic.Helpers.cs  `C#, 378 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Arithmetic.Helpers.cs:2
- class `SoftwareX87Engine` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Arithmetic.Helpers.cs:4

### SoftwareX87Engine.Arithmetic.MulDiv.cs  `C#, 215 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Arithmetic.MulDiv.cs:2
- class `SoftwareX87Engine` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Arithmetic.MulDiv.cs:4

### SoftwareX87Engine.Compare.cs  `C#, 184 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Compare.cs:2
- class `SoftwareX87Engine` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Compare.cs:4
- method `Fail($"{mnemonic} expects word/dword integer memory", out error)` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Compare.cs:14

### SoftwareX87Engine.Conversions.cs  `C#, 636 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Conversions.cs:2
- class `SoftwareX87Engine` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Conversions.cs:4

### SoftwareX87Engine.Exceptions.cs  `C#, 97 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Exceptions.cs:2
- class `SoftwareX87Engine` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Exceptions.cs:4

### SoftwareX87Engine.MathHelpers.cs  `C#, 100 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.MathHelpers.cs:3
- class `SoftwareX87Engine` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.MathHelpers.cs:5

### SoftwareX87Engine.Stack.cs  `C#, 372 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Stack.cs:2
- class `SoftwareX87Engine` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Stack.cs:4
- method `Fail("FSTCW/FNSTCW expects word memory", out error)` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Stack.cs:66
- method `Fail("FLDCW expects word memory", out error)` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Stack.cs:73
- method `Fail("FILD expects integer memory", out error)` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Stack.cs:104
- method `Fail("FIST/FISTP expects integer memory", out error)` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Stack.cs:116

### SoftwareX87Engine.Transcendental.Core.cs  `C#, 307 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Transcendental.Core.cs:2
- class `SoftwareX87Engine` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Transcendental.Core.cs:4
- enum `TrigResult` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Transcendental.Core.cs:36

### SoftwareX87Engine.Transcendental.ExpLog.cs  `C#, 137 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Transcendental.ExpLog.cs:2
- class `SoftwareX87Engine` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Transcendental.ExpLog.cs:4

### SoftwareX87Engine.Transcendental.Trig.cs  `C#, 278 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Transcendental.Trig.cs:2
- class `SoftwareX87Engine` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.Transcendental.Trig.cs:4

### SoftwareX87Engine.cs  `C#, 237 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/SoftwareX87Engine.cs:2
- class `SoftwareX87Engine` — Integer-only x87 implementation for 8086-class targets. Each logical stack entry keeps the full — PowerBasic.Compiler/Runtime/SoftwareX87Engine.cs:10

### UsingFormat.cs  `C#, 96 lines`
- namespace `PowerBasic.Compiler.Runtime` — PowerBasic.Compiler/Runtime/UsingFormat.cs:1
- class `UsingFormat` — The PRINT USING / USING$ format string, read once and shared by both code — PowerBasic.Compiler/Runtime/UsingFormat.cs:33
- record `Field` — One numeric field: its total printed width, its fraction digits, and whether the digit run — PowerBasic.Compiler/Runtime/UsingFormat.cs:39
- record `Segment` — One piece of a format: literal text to print verbatim, or a numeric field to fill. — PowerBasic.Compiler/Runtime/UsingFormat.cs:49
- method `if(i < format.Length && format[i] == '#')` — PowerBasic.Compiler/Runtime/UsingFormat.cs:67
- method `if(i + 1 < format.Length && format[i] == ',' && format[i + 1] == '#')` — a comma inside the digit run requests thousands grouping — PowerBasic.Compiler/Runtime/UsingFormat.cs:73
- method `while(i < format.Length && format[i] == '#')` — PowerBasic.Compiler/Runtime/UsingFormat.cs:83

## PowerBasic.Compiler/Semantics/

### Binder.cs  `C#, 4760 lines`
- namespace `PowerBasic.Compiler.Semantics` — PowerBasic.Compiler/Semantics/Binder.cs:3
- class `Binder` — Binds a parsed compilation unit: resolves every name to a symbol, types every — PowerBasic.Compiler/Semantics/Binder.cs:11
- method `foreach(var v in redim.Variables)` — PowerBasic.Compiler/Semantics/Binder.cs:189
- method `foreach(var block in ChildBlocks(statement))` — PowerBasic.Compiler/Semantics/Binder.cs:199
- method `if(t.IsReadonly)` — PowerBasic.Compiler/Semantics/Binder.cs:350
- method `if(bytes.Length == 0)` — PowerBasic.Compiler/Semantics/Binder.cs:372
- method `if(this._model.ModuleVariables.ContainsKey(resKey))` — PowerBasic.Compiler/Semantics/Binder.cs:377
- method `if(this._model.Udts.ContainsKey(a.Name) || this._model.EnumTypes.Contai…` — PowerBasic.Compiler/Semantics/Binder.cs:389
- method `foreach(var (from, to) in d.Ranges)` — PowerBasic.Compiler/Semantics/Binder.cs:400
- method `for(var c = char.ToUpperInvariant(from); c <= char.ToUpperInvariant(to);…` — PowerBasic.Compiler/Semantics/Binder.cs:401
- method `if(ContainsYield(f.Body))` — PowerBasic.Compiler/Semantics/Binder.cs:422
- method `switch(m.Command)` — PowerBasic.Compiler/Semantics/Binder.cs:437
- method `if(this._folder.TryFold(value) is { Integer: { } v })` — PowerBasic.Compiler/Semantics/Binder.cs:502
- method `UnaryExpr(e.Position, UnaryOp.Negate, stripped)` — PowerBasic.Compiler/Semantics/Binder.cs:535
- method `foreach(var (lowerExpr, upperExpr) in field.ArrayBounds)` — PowerBasic.Compiler/Semantics/Binder.cs:571
- method `if(lower != null && upper != null)` — PowerBasic.Compiler/Semantics/Binder.cs:574
- method `if(this._folder.TryFold(offExpr)?.Integer is not { } at || at < 0)` — pb36 layout control: field AS T AT offset - place at an explicit byte offset (gaps/overlap allowed) — PowerBasic.Compiler/Semantics/Binder.cs:586
- method `if(alignment > 1)` — pb36 ALIGN n: round the running offset up to the field's natural alignment (capped at n) — PowerBasic.Compiler/Semantics/Binder.cs:594
- method `if(parameters[i].Type is { } pt && this.ArgTypeName(this._model.TypeOf(…` — PowerBasic.Compiler/Semantics/Binder.cs:704
- method `if(!map.ContainsKey(tp))` — PowerBasic.Compiler/Semantics/Binder.cs:707
- method `TypeName(pos, BuiltinType.None, origin.Template)` — PowerBasic.Compiler/Semantics/Binder.cs:741
- method `if` — PowerBasic.Compiler/Semantics/Binder.cs:778
- method `if(m.Parameters.Count > 0)` — the incoming value: the explicit first parameter, or an injected VALUE of the property type — PowerBasic.Compiler/Semantics/Binder.cs:781
- method `if(m.IsAuto && hasBacking)` — an auto setter just stores the value into its backing field (a trivial body the optimizer inlines) — PowerBasic.Compiler/Semantics/Binder.cs:789
- method `if(m.Kind == TypeMemberKind.PropertyGet && m.IsAuto && hasBacking)` — an auto getter just yields its backing field (a trivial body the optimizer inlines) — PowerBasic.Compiler/Semantics/Binder.cs:794
- method `FlushRun` — PowerBasic.Compiler/Semantics/Binder.cs:826
- method `if(bit + f.BitWidth > 16)` — PowerBasic.Compiler/Semantics/Binder.cs:834
- method `foreach(var f in container)` — PowerBasic.Compiler/Semantics/Binder.cs:848
- method `FlushRun()` — PowerBasic.Compiler/Semantics/Binder.cs:860
- method `if(ContainsOnError(block))` — PowerBasic.Compiler/Semantics/Binder.cs:921
- method `if(ContainsYieldingTry(block))` — PowerBasic.Compiler/Semantics/Binder.cs:935
- class `GenLower` — Mutable state threaded through the generator-body flattening: the linearized output, the SELECT dis… — PowerBasic.Compiler/Semantics/Binder.cs:942
- method `Restore()` — PowerBasic.Compiler/Semantics/Binder.cs:954
- method `if(g.TryCatchLabel is not null)` — PowerBasic.Compiler/Semantics/Binder.cs:977
- method `if(g.TryCatchLabel is { } rearm)` — PowerBasic.Compiler/Semantics/Binder.cs:982
- method `ContainsYield` — PowerBasic.Compiler/Semantics/Binder.cs:986
- method `if(ascending is null)` — PowerBasic.Compiler/Semantics/Binder.cs:989
- method `ContainsYield` — PowerBasic.Compiler/Semantics/Binder.cs:1004
- method `if(d.PreTest != LoopTestKind.None)` — PowerBasic.Compiler/Semantics/Binder.cs:1009
- method `if(d.PostTest != LoopTestKind.None)` — PowerBasic.Compiler/Semantics/Binder.cs:1012
- method `ContainsYield` — PowerBasic.Compiler/Semantics/Binder.cs:1018
- method `if(i.Else != null)` — PowerBasic.Compiler/Semantics/Binder.cs:1026
- method `for(var k = 0; k < arms.Count; ++k)` — PowerBasic.Compiler/Semantics/Binder.cs:1029
- method `ContainsYield` — PowerBasic.Compiler/Semantics/Binder.cs:1037
- method `if(sel.Subject is not (NameExpr or MemberExpr or IntegerLiteralExpr or …` — SELECT CASE with a YIELD: fan out to per-arm labels (first match wins, CASE ELSE last), — PowerBasic.Compiler/Semantics/Binder.cs:1042
- method `if(elseArm != null)` — PowerBasic.Compiler/Semantics/Binder.cs:1053
- method `for(var k = 0; k < valueArms.Count; ++k)` — PowerBasic.Compiler/Semantics/Binder.cs:1056
- method `if(this._generatorParams.TryGetValue(feInfo.GenName, out var pnames))` — PowerBasic.Compiler/Semantics/Binder.cs:1072
- method `ContainsYield` — PowerBasic.Compiler/Semantics/Binder.cs:1085
- method `if(g.TryCatchLabel is not null)` — a YIELD inside a TRY: flatten the protected body but keep the ON ERROR handler correct — PowerBasic.Compiler/Semantics/Binder.cs:1090
- method `if(tr.Finally != null)` — PowerBasic.Compiler/Semantics/Binder.cs:1103
- method `if(tr.Catch != null)` — PowerBasic.Compiler/Semantics/Binder.cs:1109
- method `if(tr.Finally != null)` — PowerBasic.Compiler/Semantics/Binder.cs:1111
- method `if(tr.Catch == null)` — PowerBasic.Compiler/Semantics/Binder.cs:1113
- method `BinaryExpr(pos, BinaryOp.GreaterEqual, subject, sel.Value!)` — PowerBasic.Compiler/Semantics/Binder.cs:1144
- method `BuildMoveNextBody(pos, f.Body, GeneratedPrefix + "Current")` — PowerBasic.Compiler/Semantics/Binder.cs:1218
- method `Bound(Expression? bound, bool isLower)` — PowerBasic.Compiler/Semantics/Binder.cs:1422
- method `BinaryExpr(pos, BinaryOp.Subtract, fe2.Index, new IntegerLiteralExpr(pos, 1, Ty…` — PowerBasic.Compiler/Semantics/Binder.cs:1427
- method `CallOrIndexExpr(pos, "LBOUND", TypeSuffix.None, [arrayRef])` — PowerBasic.Compiler/Semantics/Binder.cs:1450
- method `foreach(var block in ChildBlocks(s))` — PowerBasic.Compiler/Semantics/Binder.cs:1522
- method `foreach(var block in ChildBlocks(s))` — PowerBasic.Compiler/Semantics/Binder.cs:1538
- method `CollectAssignedNames(block, names)` — PowerBasic.Compiler/Semantics/Binder.cs:1539
- method `foreach(var inner in this.YieldingForEachOverGenerator(block))` — PowerBasic.Compiler/Semantics/Binder.cs:1551
- method `if(ContainsYield(block))` — PowerBasic.Compiler/Semantics/Binder.cs:1583
- method `if(Equals(this._model.ExpressionTypes.GetValueOrDefault(args[i]), candi…` — PowerBasic.Compiler/Semantics/Binder.cs:1647
- method `new` — PowerBasic.Compiler/Semantics/Binder.cs:1663
- method `VariableDecl(pos, arr, TypeSuffix.None, [(null, new IntegerLiteralExpr(pos, Event…` — PowerBasic.Compiler/Semantics/Binder.cs:1679
- method `VariableDecl(pos, cnt, TypeSuffix.None, null, new TypeName(pos, BuiltinType.Integ…` — PowerBasic.Compiler/Semantics/Binder.cs:1682
- method `AssignStmt(pos, Elem(Cnt()), handler)` — PowerBasic.Compiler/Semantics/Binder.cs:1740
- method `AssignStmt(pos, Elem(j), Elem(new BinaryExpr(pos, BinaryOp.Add, j, Int(1))))` — PowerBasic.Compiler/Semantics/Binder.cs:1752
- method `AssignStmt` — PowerBasic.Compiler/Semantics/Binder.cs:1755
- method `IfStmt(pos, new BinaryExpr(pos, BinaryOp.Equal, Elem(i), h), found, [], nul…` — PowerBasic.Compiler/Semantics/Binder.cs:1760
- method `AssignStmt(pos, h, handler)` — PowerBasic.Compiler/Semantics/Binder.cs:1763
- method `BinaryExpr(pos, BinaryOp.Subtract, new NameExpr(pos, ev.Count, TypeSuffix.None)…` — PowerBasic.Compiler/Semantics/Binder.cs:1810
- method `if(!compatible)` — PowerBasic.Compiler/Semantics/Binder.cs:1833
- method `new(v.Name, elementType, storage)` — PowerBasic.Compiler/Semantics/Binder.cs:1852
- method `ProcPtrType( [.. (t.ProcParameterTypes ?? []).Select(p => this.ResolveTypeName(p…` — PowerBasic.Compiler/Semantics/Binder.cs:1910
- method `PointerType(PbType.Integer)` — PowerBasic.Compiler/Semantics/Binder.cs:1918
- method `PointerType(target)` — PowerBasic.Compiler/Semantics/Binder.cs:1920
- method `if(!this._aliasResolutionStack.Add(t.UserTypeName!))` — PowerBasic.Compiler/Semantics/Binder.cs:1946
- method `if(aliased == null)` — PowerBasic.Compiler/Semantics/Binder.cs:1952
- method `new(t.Position, "Value", inner, null)` — PowerBasic.Compiler/Semantics/Binder.cs:1975
- method `MemberExpr(nc.Position, value, m, TypeSuffix.None)` — PowerBasic.Compiler/Semantics/Binder.cs:1995
- method `StringLiteralExpr(nc.Position, "")` — PowerBasic.Compiler/Semantics/Binder.cs:1999
- method `MbfType(IsDouble: false)` — PowerBasic.Compiler/Semantics/Binder.cs:2045
- class `Scope` — Per-procedure (or main) binding context. — PowerBasic.Compiler/Semantics/Binder.cs:2100
- method `if(p.DefaultValue is { } d)` — PowerBasic.Compiler/Semantics/Binder.cs:2120
- method `foreach(var block in ChildBlocks(s))` — PowerBasic.Compiler/Semantics/Binder.cs:2187
- method `foreach(var (call, args) in sites)` — PowerBasic.Compiler/Semantics/Binder.cs:2231
- method `foreach(var captured in captures)` — PowerBasic.Compiler/Semantics/Binder.cs:2233
- method `if(s is LabelStmt l && !labels.Add(l.Name))` — PowerBasic.Compiler/Semantics/Binder.cs:2249
- method `foreach(var child in ChildBlocks(s))` — PowerBasic.Compiler/Semantics/Binder.cs:2251
- method `Walk(child)` — PowerBasic.Compiler/Semantics/Binder.cs:2252
- method `foreach(var (_, body) in i.ElseIfs)` — PowerBasic.Compiler/Semantics/Binder.cs:2263
- method `if(i.Else != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2265
- method `foreach(var arm in sel.Arms)` — PowerBasic.Compiler/Semantics/Binder.cs:2269
- method `if(t.Catch != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2283
- method `if(t.Finally != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2285
- method `foreach(var member in group.Body)` — PowerBasic.Compiler/Semantics/Binder.cs:2305
- method `if(a.Target is NameExpr)` — pb36 nullable assignment: x = value sets Value + HasValue=TRUE; x = NOTHING clears the flag; — PowerBasic.Compiler/Semantics/Binder.cs:2345
- method `ValueIsNullableLvalue()` — PowerBasic.Compiler/Semantics/Binder.cs:2347
- method `if(this.IsNullableType(nullTargetType))` — PowerBasic.Compiler/Semantics/Binder.cs:2348
- method `if(a.Value is NothingExpr)` — PowerBasic.Compiler/Semantics/Binder.cs:2349
- method `if(!ValueIsNullableLvalue())` — PowerBasic.Compiler/Semantics/Binder.cs:2355
- method `if(a.Value is CallOrIndexExpr { Arguments: [RangeArgExpr sliceRange] } …` — pb36 array slice copy: b() = a(lo TO hi) -> REDIM b(0 TO hi-lo) + element copy loop. — PowerBasic.Compiler/Semantics/Binder.cs:2377
- method `if(targetName is null || this.LookupArrayVariable(targetName, targetSuf…` — PowerBasic.Compiler/Semantics/Binder.cs:2384
- method `Bound(Expression? bound, bool isLower)` — PowerBasic.Compiler/Semantics/Binder.cs:2392
- method `BinaryExpr(pos, BinaryOp.Subtract, fe.Index, new IntegerLiteralExpr(pos, 1, Typ…` — PowerBasic.Compiler/Semantics/Binder.cs:2397
- method `AssignStmt(pos, lo, Bound(sliceRange.Lo, isLower: true))` — PowerBasic.Compiler/Semantics/Binder.cs:2405
- method `if(a.Target is MemberExpr bfTarget && this.BindExpression(bfTarget.Targ…` — pb36 bit-field write: o.bf = v -> o.$storage = (o.$storage AND clearMask) OR ((v AND mask) << offse… — PowerBasic.Compiler/Semantics/Binder.cs:2423
- method `if(clearMask == 0)` — PowerBasic.Compiler/Semantics/Binder.cs:2430
- method `if(wbf.Offset > 0)` — PowerBasic.Compiler/Semantics/Binder.cs:2440
- method `if(a.Value is TupleExpr tupleLit && a.Target is NameExpr or MemberExpr …` — pb36 tuple literal assigned to a tuple variable: t = (a, b) -> set each Item field (via temps, — PowerBasic.Compiler/Semantics/Binder.cs:2452
- method `foreach(var element in tupleLit.Elements)` — PowerBasic.Compiler/Semantics/Binder.cs:2458
- method `if(a is { Target: NameExpr enumTarget, Value: CallOrIndexExpr gen } && …` — pb36 coroutine: e = Gen(args) constructs the enumerator - reset its resume state and seed — PowerBasic.Compiler/Semantics/Binder.cs:2472
- method `if(this._generatorParams.TryGetValue(gen.Name, out var paramNames))` — PowerBasic.Compiler/Semantics/Binder.cs:2476
- method `if(a.Value is CallOrIndexExpr ctor && this._typeConstructors.Contains(c…` — pb36 constructor: p = Type(args) runs the type's constructor with the target as BYREF THIS — PowerBasic.Compiler/Semantics/Binder.cs:2485
- method `if(a.Value is CallOrIndexExpr sretCall)` — pb36 struct return: q = F(args) where F returns a UDT by value passes q as the hidden result — PowerBasic.Compiler/Semantics/Binder.cs:2496
- method `if(returnsUdt)` — PowerBasic.Compiler/Semantics/Binder.cs:2500
- method `if(a.Value is BinaryExpr opBin && this.UdtOperatorProc(this.BindExpress…` — pb36 operator overloading returning a TYPE: c = a OP b -> CALL Type.op_X(a, b, c) (struct return) — PowerBasic.Compiler/Semantics/Binder.cs:2509
- method `if(a.Target is NameExpr capTarget && scope.Proc?.CoroutineCaptures is {…` — pb36 coroutine: inside MoveNext, a write to a captured generator parameter/local -> THIS.$name — PowerBasic.Compiler/Semantics/Binder.cs:2517
- method `if(a.Target is NameExpr { Suffix: TypeSuffix.None } fieldTarget && scop…` — pb36 property accessor: FIELD = expr writes the backing field (THIS.$Prop = expr) — PowerBasic.Compiler/Semantics/Binder.cs:2524
- method `if(a.Target is MemberExpr propTarget && this.TryBindPropertySet(a, prop…` — PowerBasic.Compiler/Semantics/Binder.cs:2531
- method `if(a.Target is MemberExpr writeTarget)` — PowerBasic.Compiler/Semantics/Binder.cs:2533
- method `if(targetType is ProcPtrType or ScalarType { Kind: ScalarKind.Dword } &…` — first-class procedures: assigning a bare procedure name to a delegate/DWORD-pointer target — PowerBasic.Compiler/Semantics/Binder.cs:2538
- method `if(targetType is ProcPtrType pp && a.Value is LambdaExpr lam && this._m…` — PowerBasic.Compiler/Semantics/Binder.cs:2546
- method `foreach(var argument in cp.Arguments)` — PowerBasic.Compiler/Semantics/Binder.cs:2557
- method `foreach(var v in redim.Variables)` — PowerBasic.Compiler/Semantics/Binder.cs:2566
- method `if(symbol == null)` — PowerBasic.Compiler/Semantics/Binder.cs:2573
- method `if(created != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2576
- method `foreach(var array in erase.Arrays)` — PowerBasic.Compiler/Semantics/Binder.cs:2589
- method `if(arraySymbol == null)` — PowerBasic.Compiler/Semantics/Binder.cs:2591
- method `foreach(var (condition, body) in i.ElseIfs)` — PowerBasic.Compiler/Semantics/Binder.cs:2603
- method `if(i.Else != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2607
- method `foreach(var arm in sel.Arms)` — PowerBasic.Compiler/Semantics/Binder.cs:2613
- method `foreach(var selector in arm.Selectors)` — PowerBasic.Compiler/Semantics/Binder.cs:2614
- method `if(selector.RangeUpper != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2617
- method `if(counter is not ScalarType)` — PowerBasic.Compiler/Semantics/Binder.cs:2626
- method `if(f.Step != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2630
- method `if(d.PreCondition != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2637
- method `if(d.PostCondition != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2639
- method `foreach(var target in og.Targets)` — PowerBasic.Compiler/Semantics/Binder.cs:2666
- method `if(t.Catch != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2678
- method `if(t.Finally != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2680
- method `if(ev.Index != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2693
- method `if(ec.Index != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2700
- method `if(id.Target is NameExpr incrTarget && scope.Proc?.CoroutineCaptures is…` — pb36 coroutine: INCR/DECR of a captured generator parameter/local persists across resumes — PowerBasic.Compiler/Semantics/Binder.cs:2708
- method `if(id.Amount != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2717
- method `if(this.BindAssignTarget(replace.Target, scope) is not (StringType or F…` — PowerBasic.Compiler/Semantics/Binder.cs:2730
- method `if(targetType is not ScalarType { IsFloat: false })` — PowerBasic.Compiler/Semantics/Binder.cs:2737
- method `if(sort.TagArray != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2752
- method `if(mid.Length != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2765
- method `if(targetType is not (StringType or FixedStringType or AsciizType or Fl…` — PowerBasic.Compiler/Semantics/Binder.cs:2772
- method `if(asc.Index != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2774
- method `if(so.Value != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2781
- method `if(si.Count != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2786
- method `if(this.BindAssignTarget(si.Target, scope) is not (StringType or FlexTy…` — PowerBasic.Compiler/Semantics/Binder.cs:2788
- method `if(p.FileNumber != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2798
- method `if(p.UsingFormat != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2800
- method `foreach(var item in p.Items)` — PowerBasic.Compiler/Semantics/Binder.cs:2802
- method `if(write.FileNumber != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2808
- method `foreach(var item in write.Items)` — PowerBasic.Compiler/Semantics/Binder.cs:2810
- method `if(input.FileNumber != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2823
- method `foreach(var target in input.Targets)` — PowerBasic.Compiler/Semantics/Binder.cs:2825
- method `if(open.RecordLength != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2832
- method `foreach(var n in close.FileNumbers)` — PowerBasic.Compiler/Semantics/Binder.cs:2837
- method `if(gp.RecordNumber != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2843
- method `if(gp.Variable != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2845
- method `foreach(var (width, target) in field.Fields)` — PowerBasic.Compiler/Semantics/Binder.cs:2856
- method `foreach(var target in read.Targets)` — PowerBasic.Compiler/Semantics/Binder.cs:2863
- method `if(this.BindExpression(chain.Target, scope) is not (StringType or Fixed…` — PowerBasic.Compiler/Semantics/Binder.cs:2872
- method `if(seg.Segment != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2885
- method `foreach(var argument in cmd.Arguments)` — PowerBasic.Compiler/Semantics/Binder.cs:2890
- method `foreach(var e in new[] { line.From?.X, line.From?.Y, line.To.X, line.To.Y, l…` — PowerBasic.Compiler/Semantics/Binder.cs:2899
- method `foreach(var e in new[] { circle.Center.X, circle.Center.Y, circle.Radius, ci…` — PowerBasic.Compiler/Semantics/Binder.cs:2905
- method `if(pset.Color != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2913
- method `if(gg.To != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2920
- method `if(this._dialect == Dialect.Pb30 && !this._warnedAsm30)` — QUIRK 2.21 (FAQ): 3.0 resolved inline-asm variable operands differently — PowerBasic.Compiler/Semantics/Binder.cs:2931
- method `if(!DialectFacts.IsAvailable(LanguageFeature.NestedProcedures, this._di…` — PowerBasic.Compiler/Semantics/Binder.cs:2950
- method `if(scope.Proc != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2960
- method `if(this._folder.TryFold(sa.Condition)?.Integer is not { } truth)` — PowerBasic.Compiler/Semantics/Binder.cs:2967
- method `if(truth == 0)` — PowerBasic.Compiler/Semantics/Binder.cs:2969
- method `if(scope.Proc != null)` — PowerBasic.Compiler/Semantics/Binder.cs:2975
- method `foreach(var (lower, upper) in v.ArrayBounds ?? [])` — PowerBasic.Compiler/Semantics/Binder.cs:3017
- method `if(lower != null)` — PowerBasic.Compiler/Semantics/Binder.cs:3029
- method `if(!this._model.ModuleVariables.TryGetValue(key, out var moduleVar))` — SHARED inside a proc aliases the module-level variable — PowerBasic.Compiler/Semantics/Binder.cs:3042
- method `if(created == null)` — PowerBasic.Compiler/Semantics/Binder.cs:3044
- method `if(dim.Class == ArrayClass.Stack)` — PowerBasic.Compiler/Semantics/Binder.cs:3055
- method `if(symbol != null)` — PowerBasic.Compiler/Semantics/Binder.cs:3058
- method `if(symbol == null)` — PowerBasic.Compiler/Semantics/Binder.cs:3066
- method `if(dim.Class == ArrayClass.Stack)` — pb36 STACK array: frame-resident, so it needs a real frame (Local, not STATIC) — PowerBasic.Compiler/Semantics/Binder.cs:3070
- method `if(symbol.Type is not ArrayType { StaticBounds: not null })` — PowerBasic.Compiler/Semantics/Binder.cs:3073
- method `if(scope.Proc.Variables.TryGetValue(key, out var existing) && !Equals(e…` — PowerBasic.Compiler/Semantics/Binder.cs:3076
- method `if(this._folder.TryFold(re.Lo)?.Integer is not { } lo || this._folder.T…` — PowerBasic.Compiler/Semantics/Binder.cs:3161
- method `if(se.Source is not NameExpr src || this.LookupArrayVariable(src.Name, …` — PowerBasic.Compiler/Semantics/Binder.cs:3169
- method `ResolveBound(Expression? bound, long fallback)` — slice bounds: constant expressions, a from-end ^n (= UBOUND - n + 1), or omitted — PowerBasic.Compiler/Semantics/Binder.cs:3177
- method `if(bound is FromEndExpr fe)` — PowerBasic.Compiler/Semantics/Binder.cs:3180
- method `if(sliceLo is not { } lo2 || sliceHi is not { } hi2)` — PowerBasic.Compiler/Semantics/Binder.cs:3186
- method `if(lo2 < dimBound.Item1 || hi2 > dimBound.Item2 || lo2 > hi2)` — PowerBasic.Compiler/Semantics/Binder.cs:3190
- method `for(var j = lo2; j <= hi2; ++j)` — PowerBasic.Compiler/Semantics/Binder.cs:3194
- method `if(this.PbTypeToTypeName(valueType, nu.Position) is not { } tn)` — PowerBasic.Compiler/Semantics/Binder.cs:3243
- method `if((c.Arguments.Count < proc.RequiredParameters || c.Arguments.Count > …` — PowerBasic.Compiler/Semantics/Binder.cs:3336
- method `if(sym.Storage == VariableStorage.Captured && ReferenceEquals(lifted.Ca…` — PowerBasic.Compiler/Semantics/Binder.cs:3425
- method `if(assign.Value is NameExpr src && this._model.VariableBindings.TryGetV…` — PowerBasic.Compiler/Semantics/Binder.cs:3464
- method `foreach(var nested in AssignmentsIn(block))` — PowerBasic.Compiler/Semantics/Binder.cs:3493
- method `foreach(var (_, arm) in i.ElseIfs)` — PowerBasic.Compiler/Semantics/Binder.cs:3503
- method `if(i.Else != null)` — PowerBasic.Compiler/Semantics/Binder.cs:3505
- method `foreach(var arm in sel.Arms)` — PowerBasic.Compiler/Semantics/Binder.cs:3509
- method `if(t.Catch != null)` — PowerBasic.Compiler/Semantics/Binder.cs:3523
- method `if(t.Finally != null)` — PowerBasic.Compiler/Semantics/Binder.cs:3525
- method `if(pi < 0)` — PowerBasic.Compiler/Semantics/Binder.cs:3554
- method `if(slots[pi] != null)` — PowerBasic.Compiler/Semantics/Binder.cs:3558
- method `if(proc.Parameters[i].DefaultValue is { } d)` — PowerBasic.Compiler/Semantics/Binder.cs:3578
- method `if(!Equals(lambda.Parameters[i].Type, sig.ParameterTypes[i]))` — PowerBasic.Compiler/Semantics/Binder.cs:3613
- method `if(!this._model.Equates.TryGetValue(c.Name, out var value))` — PowerBasic.Compiler/Semantics/Binder.cs:3696
- method `if(scope.Proc?.CoroutineCaptures is { } captures && captures.TryGetValu…` — pb36 coroutine: inside MoveNext, a captured generator parameter reads as THIS.$param — PowerBasic.Compiler/Semantics/Binder.cs:3709
- method `if(n.Suffix == TypeSuffix.None && scope.Proc?.BackingField is { } backi…` — pb36 property accessor: FIELD reads the compiler-generated backing field (THIS.$Prop) — PowerBasic.Compiler/Semantics/Binder.cs:3716
- method `if(symbol == null && n.Suffix == TypeSuffix.None && this._model.EnumMem…` — a bare name with no matching variable may be a PB 3.6 ENUM member (its own — PowerBasic.Compiler/Semantics/Binder.cs:3727
- method `if(symbol == null && this._model.Procedures.TryGetValue(n.Name, out var…` — a bare name may be a parameterless FUNCTION call (PB allows omitting "()") — PowerBasic.Compiler/Semantics/Binder.cs:3733
- method `if(symbol == null)` — ... or a parameterless intrinsic (FREEFILE, TIMER, ERR, INKEY$, ...) — PowerBasic.Compiler/Semantics/Binder.cs:3739
- method `if((Intrinsics.TryGet(intrinsicName, out var intrinsic) || Intrinsics.T…` — PowerBasic.Compiler/Semantics/Binder.cs:3741
- method `if(DialectFacts.IntrinsicGate(intrinsic.Name) is { } gate)` — PowerBasic.Compiler/Semantics/Binder.cs:3743
- method `if(this.TryBindDottedVariable(m, scope) is { } flatType)` — QB-style dotted variable names: when the chain root is not a UDT-typed — PowerBasic.Compiler/Semantics/Binder.cs:3761
- method `if(targetType is not UdtType udt)` — PowerBasic.Compiler/Semantics/Binder.cs:3765
- method `if(this.BitFieldOf(udt, m.Member) is { } bf)` — pb36 bit-field read: o.bf -> (o.$storage >>> offset) AND ((1 << width) - 1), minimized: no — PowerBasic.Compiler/Semantics/Binder.cs:3772
- method `if(bf.Offset > 0)` — PowerBasic.Compiler/Semantics/Binder.cs:3774
- method `if(bf.Offset + bf.Width < bf.ContainerBits)` — PowerBasic.Compiler/Semantics/Binder.cs:3776
- method `if(field != null)` — PowerBasic.Compiler/Semantics/Binder.cs:3783
- method `if(member != null)` — PowerBasic.Compiler/Semantics/Binder.cs:3789
- method `if(inner is not (StringType or FixedStringType or FlexType or AsciizTyp…` — PowerBasic.Compiler/Semantics/Binder.cs:3801
- method `if(ix.Target is MemberExpr method && this.TryBindMemberCall(ix, method,…` — pb36: o.Method(args) parses as IndexExpr(MemberExpr(o,Method), args); when the — PowerBasic.Compiler/Semantics/Binder.cs:3809
- method `foreach(var index in ix.Arguments)` — PowerBasic.Compiler/Semantics/Binder.cs:3814
- method `if(deref.Index != null)` — PowerBasic.Compiler/Semantics/Binder.cs:3821
- method `if(pointerType is not PointerType ptr)` — PowerBasic.Compiler/Semantics/Binder.cs:3823
- method `if(operand is BcdType)` — PowerBasic.Compiler/Semantics/Binder.cs:3835
- method `if(operand is not ScalarType)` — PowerBasic.Compiler/Semantics/Binder.cs:3837
- method `if(u.Op == UnaryOp.Not)` — PowerBasic.Compiler/Semantics/Binder.cs:3839
- method `IntegralOf(operand)` — PowerBasic.Compiler/Semantics/Binder.cs:3840
- method `if(u.Op == UnaryOp.Negate && this._dialect.IsPbAtLeast(Dialect.Pb20) &&…` — PB computes integral negation in floating point too: with N% = -32768, — PowerBasic.Compiler/Semantics/Binder.cs:3843
- method `foreach(var element in tup.Elements)` — a tuple literal is only meaningful as an assignment / destructuring right-hand side, which the — PowerBasic.Compiler/Semantics/Binder.cs:3854
- method `if(coalesce.Value is NullConditionalExpr ncLeft)` — pb36 null-coalescing: v ?? d -> IF(v.HasValue, v.Value, d). A null-conditional access on the — PowerBasic.Compiler/Semantics/Binder.cs:3871
- method `if(!this.IsNullableType(valueType))` — PowerBasic.Compiler/Semantics/Binder.cs:3878
- method `MemberExpr(coalesce.Position, coalesce.Value, "HasValue", TypeSuffix.None)` — PowerBasic.Compiler/Semantics/Binder.cs:3881
- method `IntegerLiteralExpr(pos, dim + 1, TypeSuffix.None)` — PowerBasic.Compiler/Semantics/Binder.cs:4230
- method `BinaryExpr(pos, BinaryOp.Subtract, ubound, fromEnd.Index)` — PowerBasic.Compiler/Semantics/Binder.cs:4234
- method `Append` — PowerBasic.Compiler/Semantics/Binder.cs:4251
- method `ParamsOf` — PowerBasic.Compiler/Semantics/Binder.cs:4306
- method `if(tn == null)` — PowerBasic.Compiler/Semantics/Binder.cs:4312
- method `BuildThunk` — PowerBasic.Compiler/Semantics/Binder.cs:4318
- method `if(bound.Count >= target.VisibleParameterCount)` — PowerBasic.Compiler/Semantics/Binder.cs:4336
- method `foreach(var b in bound)` — PowerBasic.Compiler/Semantics/Binder.cs:4341
- method `StringLiteralExpr(b.Position, txt)` — PowerBasic.Compiler/Semantics/Binder.cs:4347
- method `if(pars == null)` — PowerBasic.Compiler/Semantics/Binder.cs:4352
- method `if(f.VisibleParameterCount != 1 || g.VisibleParameterCount != 1)` — PowerBasic.Compiler/Semantics/Binder.cs:4364
- method `if(pars == null)` — PowerBasic.Compiler/Semantics/Binder.cs:4369
- method `if(this.ReflectedUdt(call.Arguments[0], scope) is { } udt)` — PowerBasic.Compiler/Semantics/Binder.cs:4412
- method `if(this.ReflectedUdt(call.Arguments[0], scope) is { } udt && this.Refle…` — PowerBasic.Compiler/Semantics/Binder.cs:4422
- method `if(this.ReflectedUdt(call.Arguments[0], scope) is { } udt && this.Refle…` — PowerBasic.Compiler/Semantics/Binder.cs:4432
- method `if(this._folder.TryFold(selector)?.Integer is { } i && i >= 1 && i <= u…` — PowerBasic.Compiler/Semantics/Binder.cs:4471
- method `if(call.Arguments[d] is FromEndExpr fromEnd)` — PowerBasic.Compiler/Semantics/Binder.cs:4520
- method `if` — PowerBasic.Compiler/Semantics/Binder.cs:4564
- method `if(arraySymbol == null)` — PowerBasic.Compiler/Semantics/Binder.cs:4583
- method `if(this._model.Procedures.TryGetValue(procRef.Name, out var target))` — PowerBasic.Compiler/Semantics/Binder.cs:4599
- method `if(this._model.Labels.TryGetValue(scope.LabelKey, out var labels) && la…` — PowerBasic.Compiler/Semantics/Binder.cs:4604
- method `if(call.Arguments.Count < proc.RequiredParameters || call.Arguments.Cou…` — PowerBasic.Compiler/Semantics/Binder.cs:4652
- method `if(!(captured.Storage == VariableStorage.Local || (captured.Storage == …` — PowerBasic.Compiler/Semantics/Binder.cs:4726
- method `VariableSymbol(name, type, VariableStorage.Global)` — PowerBasic.Compiler/Semantics/Binder.cs:4749

### ConstantFolder.cs  `C#, 142 lines`
- namespace `PowerBasic.Compiler.Semantics` — PowerBasic.Compiler/Semantics/ConstantFolder.cs:3
- record `ConstantValue` — A folded compile-time constant: integral, floating or string. — PowerBasic.Compiler/Semantics/ConstantFolder.cs:7
- class `ConstantFolder` — Evaluates constant expressions at compile time: equate definitions, array — PowerBasic.Compiler/Semantics/ConstantFolder.cs:21
- method `if(this.TryFold(u.Operand) is not { } operand)` — PowerBasic.Compiler/Semantics/ConstantFolder.cs:46
- method `if(this.TryFold(t.Condition) is not { Integer: { } cond })` — PB 3.6 ternary with a compile-time-constant condition folds to the taken — PowerBasic.Compiler/Semantics/ConstantFolder.cs:62

### Intrinsics.cs  `C#, 187 lines`
- namespace `PowerBasic.Compiler.Semantics` — PowerBasic.Compiler/Semantics/Intrinsics.cs:1
- enum `IntrinsicReturn` — Return-type rule of an intrinsic function. — PowerBasic.Compiler/Semantics/Intrinsics.cs:4
- record `IntrinsicInfo` — One built-in function signature. — PowerBasic.Compiler/Semantics/Intrinsics.cs:7
- class `Intrinsics` — Catalog of PB 3.5 built-in functions, used by the binder to tell intrinsic — PowerBasic.Compiler/Semantics/Intrinsics.cs:15

### MacroStringValidator.cs  `C#, 261 lines`
- namespace `PowerBasic.Compiler.Semantics` — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:1
- record `DrawStep` — One step of a DRAW string, already reduced to what the code generator has to emit. — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:4
- enum `DrawStepKind` — What a does: move by a delta, move to a point, or set the colour. — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:7
- class `MacroStringValidator` — Compile-time checking of the two macro languages BASIC embeds in string literals: the tune — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:23
- method `switch` — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:59
- method `ReadNumber(picture, ref at)` — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:63
- method `var(dx, dy)` — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:65
- method `SkipSign(picture, ref at)` — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:82
- method `ReadNumber(picture, ref at)` — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:84
- method `SkipSign(picture, ref at)` — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:88
- method `ReadNumber(picture, ref at)` — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:90
- method `ReadNumber(picture, ref at)` — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:98
- method `while(at < tune.Length && tune[at] is '+' or '#' or '-')` — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:128
- method `ReadNumber(tune, ref at)` — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:130
- method `while(at < tune.Length && tune[at] == '.')` — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:131
- method `if(at >= tune.Length || char.ToUpperInvariant(tune[at]) is not ('N' or …` — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:144
- method `if(at >= picture.Length)` — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:177
- method `switch` — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:182
- method `ReadNumber(picture, ref at)` — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:185
- method `SkipSign(picture, ref at)` — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:190
- method `if(!ReadNumber(picture, ref at))` — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:191
- method `if(at >= picture.Length || picture[at] != ',')` — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:193
- method `SkipSign(picture, ref at)` — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:196
- method `if(!ReadNumber(picture, ref at))` — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:197
- method `if(at >= picture.Length || char.ToUpperInvariant(picture[at]) != 'A')` — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:207
- method `SkipSign(picture, ref at)` — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:210
- method `if(!ReadNumber(picture, ref at))` — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:211
- method `if(Range(picture, ref at, c, 0, 255) is { } fill)` — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:216
- method `if(at >= picture.Length || picture[at] != ',')` — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:218
- method `if(Range(picture, ref at, c, 0, 255) is { } border)` — PowerBasic.Compiler/Semantics/MacroStringValidator.cs:221

### Monomorphizer.cs  `C#, 180 lines`
- namespace `PowerBasic.Compiler.Semantics` — PowerBasic.Compiler/Semantics/Monomorphizer.cs:6
- class `Monomorphizer` — pb36 compile-time generics (monomorphization). A generic TYPE Name OF T … END TYPE is a — PowerBasic.Compiler/Semantics/Monomorphizer.cs:19
- method `foreach(var child in ChildObjects(tn))` — PowerBasic.Compiler/Semantics/Monomorphizer.cs:109
- method `CollectTypeNames(child, sink)` — PowerBasic.Compiler/Semantics/Monomorphizer.cs:110
- method `CollectTypeNames(tuple[i], sink)` — PowerBasic.Compiler/Semantics/Monomorphizer.cs:114
- method `foreach(var item in list)` — PowerBasic.Compiler/Semantics/Monomorphizer.cs:117
- method `CollectTypeNames(item, sink)` — PowerBasic.Compiler/Semantics/Monomorphizer.cs:118
- method `if(node.GetType().IsValueType && node.GetType().Namespace?.StartsWith("…` — PowerBasic.Compiler/Semantics/Monomorphizer.cs:121
- method `foreach(var child in ChildObjects(node))` — PowerBasic.Compiler/Semantics/Monomorphizer.cs:123
- method `CollectTypeNames(child, sink)` — PowerBasic.Compiler/Semantics/Monomorphizer.cs:124
- method `CloneList(list, map)` — PowerBasic.Compiler/Semantics/Monomorphizer.cs:143
- method `CloneRecord(node, map)` — PowerBasic.Compiler/Semantics/Monomorphizer.cs:145

### PbType.cs  `C#, 167 lines`
- namespace `PowerBasic.Compiler.Semantics` — PowerBasic.Compiler/Semantics/PbType.cs:1
- enum `ScalarKind` — Discriminates the scalar kinds (the PB 3.5 set plus the pb36 SByte / QWord additions). — PowerBasic.Compiler/Semantics/PbType.cs:4
- record `PbType` — A resolved PowerBASIC type. Sizes are the on-target (16-bit real mode) byte — PowerBasic.Compiler/Semantics/PbType.cs:10
- record `ScalarType` — Numeric scalar. — PowerBasic.Compiler/Semantics/PbType.cs:33
- record `WideIntType` — pb36 wide integer (INT128/256/512 and the unsigned UINT* forms): a fixed-size — PowerBasic.Compiler/Semantics/PbType.cs:44
- record `StringType` — Dynamic string. Stored as a 2-byte handle into the runtime's string handle — PowerBasic.Compiler/Semantics/PbType.cs:54
- record `FixedStringType` — Fixed-length string (STRING * n), stored inline. — PowerBasic.Compiler/Semantics/PbType.cs:59
- record `FlexType` — FLEX string (PB 3.5 flexible structure); stored like a dynamic string handle. — PowerBasic.Compiler/Semantics/PbType.cs:64
- record `AsciizType` — ASCIIZ * n (PB 3.5): NUL-terminated fixed buffer of n bytes; LEN() is the — PowerBasic.Compiler/Semantics/PbType.cs:72
- record `BcdType` — BCD numeric (baseline PB): FIX (@, 8 bytes fixed-point) or BCD — PowerBasic.Compiler/Semantics/PbType.cs:81
- record `PointerType` — Data pointer (PB 3.2): 32-bit seg:off pointer to ; @p dereferences. — PowerBasic.Compiler/Semantics/PbType.cs:86
- record `MbfType` — Microsoft Binary Format float (BASICA / GW-BASIC): the interpreters store — PowerBasic.Compiler/Semantics/PbType.cs:97
- record `ProcPtrType` — PB 3.6 typed procedure pointer / delegate (a "fat" closure value): an 8-byte — PowerBasic.Compiler/Semantics/PbType.cs:111
- record `UdtField` — One UDT/UNION field with its resolved offset. — PowerBasic.Compiler/Semantics/PbType.cs:132
- record `UdtType` — TYPE ... END TYPE (packed by default) or UNION ... END UNION (all fields at offset 0). — PowerBasic.Compiler/Semantics/PbType.cs:142
- record `ArrayType` — Array of ; static arrays have compile-time bounds, dynamic ones a descriptor. — PowerBasic.Compiler/Semantics/PbType.cs:152
- record `AnyType` — Parameter-only wildcard (AS ANY). — PowerBasic.Compiler/Semantics/PbType.cs:164

### SemanticModel.cs  `C#, 143 lines`
- namespace `PowerBasic.Compiler.Semantics` — PowerBasic.Compiler/Semantics/SemanticModel.cs:3
- class `SemanticModel` — Result of binding a compilation unit: every name resolved to a symbol, every — PowerBasic.Compiler/Semantics/SemanticModel.cs:11

### Symbols.cs  `C#, 133 lines`
- namespace `PowerBasic.Compiler.Semantics` — PowerBasic.Compiler/Semantics/Symbols.cs:3
- enum `VariableStorage` — Where a variable lives on the target. — PowerBasic.Compiler/Semantics/Symbols.cs:7
- class `VariableSymbol` — A bound variable (scalar or array). — PowerBasic.Compiler/Semantics/Symbols.cs:21
- class `ProcedureSymbol` — A SUB or FUNCTION (defined here, DECLAREd, or imported from a unit). — PowerBasic.Compiler/Semantics/Symbols.cs:50
- record `Diagnostic` — A compile-time diagnostic. — PowerBasic.Compiler/Semantics/Symbols.cs:125
- class `BindException` — Raised when binding encounters an unrecoverable inconsistency. — PowerBasic.Compiler/Semantics/Symbols.cs:130

## PowerBasic.Compiler/Syntax/

### Dialect.cs  `C#, 531 lines`
- namespace `PowerBasic.Compiler.Syntax` — PowerBasic.Compiler/Syntax/Dialect.cs:1
- enum `Dialect` — Compiler dialect selected with --dialect (default ). — PowerBasic.Compiler/Syntax/Dialect.cs:12
- enum `DialectFamily` — BASIC product family; feature gating and runtime quirks route on it. — PowerBasic.Compiler/Syntax/Dialect.cs:50
- enum `LanguageFeature` — Version-gated language features (see docs/DIALECTS.md for the researched matrix). — PowerBasic.Compiler/Syntax/Dialect.cs:58
- class `DialectFacts` — The single data-driven gating table: which dialect introduced which feature, — PowerBasic.Compiler/Syntax/Dialect.cs:250

### ISourceProvider.cs  `C#, 71 lines`
- namespace `PowerBasic.Compiler.Syntax` — PowerBasic.Compiler/Syntax/ISourceProvider.cs:2
- interface `ISourceProvider` — Resolves source file names (e.g. $INCLUDE targets) to source text. — PowerBasic.Compiler/Syntax/ISourceProvider.cs:6
- class `FileSourceProvider` — Loads sources from the file system, resolving includes relative to the including file. — PowerBasic.Compiler/Syntax/ISourceProvider.cs:16
- class `SearchPathSourceProvider` — Loads sources from the file system, trying the including file's directory first, — PowerBasic.Compiler/Syntax/ISourceProvider.cs:41

### Lexer.cs  `C#, 666 lines`
- namespace `PowerBasic.Compiler.Syntax` — PowerBasic.Compiler/Syntax/Lexer.cs:2
- class `Lexer` — Tokenizer for PowerBASIC 3.5 source. Keywords are not distinguished from — PowerBasic.Compiler/Syntax/Lexer.cs:10
- method `while(first < end && source[first] is ' ' or '\t')` — PowerBasic.Compiler/Syntax/Lexer.cs:59
- method `LexerException( $"{dialect.DisplayName()} requires a numeric line number on every p…` — PowerBasic.Compiler/Syntax/Lexer.cs:63
- method `if` — PowerBasic.Compiler/Syntax/Lexer.cs:66
- method `new(TokenKind.EndOfLine, "", position)` — PowerBasic.Compiler/Syntax/Lexer.cs:109
- method `new(TokenKind.EndOfFile, "", position)` — PowerBasic.Compiler/Syntax/Lexer.cs:111
- method `new(TokenKind.EndOfLine, "", position)` — PowerBasic.Compiler/Syntax/Lexer.cs:122
- method `while(look < this._source.Length && this._source[look] is ' ' or '\t')` — PowerBasic.Compiler/Syntax/Lexer.cs:182
- method `if(look < this._source.Length && this._source[look] == '\'')` — a comment may follow the continuation - `... + _ ' hotX=0, hotY=0` still joins the — PowerBasic.Compiler/Syntax/Lexer.cs:186
- method `if(look >= this._source.Length || this._source[look] is '\r' or '\n')` — PowerBasic.Compiler/Syntax/Lexer.cs:189
- method `while(this.Peek(run) == '?')` — PowerBasic.Compiler/Syntax/Lexer.cs:317
- method `if(allowCoalesce && run == 1 && this.Peek(run) is '.' or '[' && Dialect…` — pb36 null-conditional operator: a single '?' glued before '.' or '[' is the '?.'/'?[' access, — PowerBasic.Compiler/Syntax/Lexer.cs:321
- method `while(this.Peek(after) is ' ' or '\t')` — PowerBasic.Compiler/Syntax/Lexer.cs:325
- method `if(suffixCount > 3)` — PowerBasic.Compiler/Syntax/Lexer.cs:333
- method `if(this.Current != '&')` — PowerBasic.Compiler/Syntax/Lexer.cs:340
- method `if(this.Current != '#')` — PowerBasic.Compiler/Syntax/Lexer.cs:350
- method `if(this.Current != '@')` — PowerBasic.Compiler/Syntax/Lexer.cs:362
- method `if(this.Current != '$')` — PowerBasic.Compiler/Syntax/Lexer.cs:368
- method `while(char.IsAsciiDigit(this.Current))` — PowerBasic.Compiler/Syntax/Lexer.cs:418
- method `new(TokenKind.IntegerLiteral, text, position, suffix, IntegerValue: valu…` — PowerBasic.Compiler/Syntax/Lexer.cs:433
- method `LexerException("invalid suffix on radix literal", position)` — PowerBasic.Compiler/Syntax/Lexer.cs:503
- method `new` — PowerBasic.Compiler/Syntax/Lexer.cs:508
- method `if(this.Peek() == '{')` — '{{' is a literal brace, not a hole opener — PowerBasic.Compiler/Syntax/Lexer.cs:540
- method `if(depth == 0 && this.Peek() == '}')` — PowerBasic.Compiler/Syntax/Lexer.cs:546
- method `if(depth > 0)` — PowerBasic.Compiler/Syntax/Lexer.cs:550
- method `if(depth == 0)` — PowerBasic.Compiler/Syntax/Lexer.cs:553
- method `while(!this.AtEnd && this.Current is not ('"' or '\r' or '\n'))` — PowerBasic.Compiler/Syntax/Lexer.cs:557
- method `new` — PowerBasic.Compiler/Syntax/Lexer.cs:615
- method `return(TokenKind.Less, "<")` — PowerBasic.Compiler/Syntax/Lexer.cs:641
- method `return(TokenKind.Greater, ">")` — PowerBasic.Compiler/Syntax/Lexer.cs:657
- class `LexerException` — Raised when source contains a character sequence the lexer cannot tokenize. — PowerBasic.Compiler/Syntax/Lexer.cs:663

### Parser.Commands.cs  `C#, 458 lines`
- namespace `PowerBasic.Compiler.Syntax` — PowerBasic.Compiler/Syntax/Parser.Commands.cs:2
- class `Parser` — Calls, simple mutators (INCR/SWAP/MID$/LSET), graphics statements and generic commands. — PowerBasic.Compiler/Syntax/Parser.Commands.cs:6
- method `CallPtrStmt(pos, pointer, convention, ptrArgs)` — PowerBasic.Compiler/Syntax/Parser.Commands.cs:23
- method `CommandStmt(pos, upper, serviceArgs)` — PowerBasic.Compiler/Syntax/Parser.Commands.cs:33
- method `CallStmt(name.Position, name.Text, typedArgs, false, typeArgs)` — PowerBasic.Compiler/Syntax/Parser.Commands.cs:76
- method `MemberCallStmt(name.Position, parenthesized.Target, parenthesized.Member, indexed.A…` — PowerBasic.Compiler/Syntax/Parser.Commands.cs:83
- method `if(!this.IsStatementEnd())` — PowerBasic.Compiler/Syntax/Parser.Commands.cs:86
- method `MemberCallStmt(name.Position, bare.Target, bare.Member, bareArgs)` — PowerBasic.Compiler/Syntax/Parser.Commands.cs:90
- method `if(this.TryMatchKeyword("ASCEND"))` — PowerBasic.Compiler/Syntax/Parser.Commands.cs:169
- method `if(this.TryMatchKeyword("DESCEND"))` — PowerBasic.Compiler/Syntax/Parser.Commands.cs:171
- method `if(this.TryMatchKeyword("TAGARRAY"))` — PowerBasic.Compiler/Syntax/Parser.Commands.cs:173
- method `if(this.Match(TokenKind.LParen))` — PowerBasic.Compiler/Syntax/Parser.Commands.cs:176
- method `if(isScan)` — PowerBasic.Compiler/Syntax/Parser.Commands.cs:184
- method `ArraySortStmt(pos, arrayRef, count, fromPos, toPos, collate, descend, tagArray)` — PowerBasic.Compiler/Syntax/Parser.Commands.cs:201
- method `if(this.Current.Kind != TokenKind.Comma && !this.IsStatementEnd())` — PowerBasic.Compiler/Syntax/Parser.Commands.cs:335
- method `if(flag is not ("B" or "BF"))` — PowerBasic.Compiler/Syntax/Parser.Commands.cs:337
- method `if(this.Match(TokenKind.Comma))` — PowerBasic.Compiler/Syntax/Parser.Commands.cs:342
- method `CommandStmt(pos, keyword, arguments)` — PowerBasic.Compiler/Syntax/Parser.Commands.cs:431
- method `if(this.IsPointAhead())` — PowerBasic.Compiler/Syntax/Parser.Commands.cs:436
- method `if` — PowerBasic.Compiler/Syntax/Parser.Commands.cs:442
- method `CommandStmt(pos, keyword, arguments)` — PowerBasic.Compiler/Syntax/Parser.Commands.cs:454

### Parser.ControlFlow.cs  `C#, 556 lines`
- namespace `PowerBasic.Compiler.Syntax` — PowerBasic.Compiler/Syntax/Parser.ControlFlow.cs:2
- class `Parser` — Control flow: IF, SELECT CASE, loops, jumps, error handling and event statements. — PowerBasic.Compiler/Syntax/Parser.ControlFlow.cs:6
- method `if` — PowerBasic.Compiler/Syntax/Parser.ControlFlow.cs:65
- method `if(text.Length > 0)` — PowerBasic.Compiler/Syntax/Parser.ControlFlow.cs:102
- method `if(this.Current.Kind == TokenKind.Identifier && this._duCases.TryGetVal…` — PowerBasic.Compiler/Syntax/Parser.ControlFlow.cs:140
- method `if(this.Current.Kind == TokenKind.Identifier && !this.IsKeyword(0, "TO"…` — PowerBasic.Compiler/Syntax/Parser.ControlFlow.cs:144
- method `new(pos, value, this.ParseExpression(), null)` — PowerBasic.Compiler/Syntax/Parser.ControlFlow.cs:195
- method `ForStmt(pos, variable, collection, rangeHi, rangeStep, body)` — PowerBasic.Compiler/Syntax/Parser.ControlFlow.cs:241
- method `ForStmt(pos, variable, range.Lo, range.Hi, null, body)` — PowerBasic.Compiler/Syntax/Parser.ControlFlow.cs:245
- method `OnErrorStmt(pos, null, true)` — PowerBasic.Compiler/Syntax/Parser.ControlFlow.cs:391
- method `OnErrorStmt(pos, errorTarget == "0" ? null : errorTarget, false)` — PowerBasic.Compiler/Syntax/Parser.ControlFlow.cs:395
- method `ResumeStmt(pos, ResumeKind.Next, null)` — PowerBasic.Compiler/Syntax/Parser.ControlFlow.cs:429
- method `ResumeStmt(pos, ResumeKind.SameStatement, null)` — PowerBasic.Compiler/Syntax/Parser.ControlFlow.cs:431
- method `ResumeStmt(pos, ResumeKind.SameStatement, null)` — PowerBasic.Compiler/Syntax/Parser.ControlFlow.cs:435
- method `Err` — PowerBasic.Compiler/Syntax/Parser.ControlFlow.cs:484

### Parser.Declarations.cs  `C#, 961 lines`
- namespace `PowerBasic.Compiler.Syntax` — PowerBasic.Compiler/Syntax/Parser.Declarations.cs:2
- class `Parser` — Declarations: SUB/FUNCTION/DECLARE, TYPE/UNION, DEF FN/DEFtype/DEF SEG, DIM family, equates. — PowerBasic.Compiler/Syntax/Parser.Declarations.cs:6
- method `FunctionDecl(pos, name.Text, name.Suffix, returnType, parameters ?? [], isStatic,…` — PowerBasic.Compiler/Syntax/Parser.Declarations.cs:69
- method `new(typeToken.Position, $"__param{++this._anonymousParameters}", TypeSuf…` — PowerBasic.Compiler/Syntax/Parser.Declarations.cs:221
- method `TypeName(tuplePos, BuiltinType.None, TupleElements: elements)` — PowerBasic.Compiler/Syntax/Parser.Declarations.cs:264
- method `DimStmt(pos, StorageClass.Dim, false, [new VariableDecl(name.Position, name.…` — PowerBasic.Compiler/Syntax/Parser.Declarations.cs:456
- method `if(this.IsKeyword(0, "PACKED"))` — PowerBasic.Compiler/Syntax/Parser.Declarations.cs:534
- method `if(n is not (1 or 2 or 4 or 8 or 16))` — PowerBasic.Compiler/Syntax/Parser.Declarations.cs:542
- method `TypeMember` — PowerBasic.Compiler/Syntax/Parser.Declarations.cs:622
- method `AssignStmt(pos, new NameExpr(propName.Position, propName.Text, propName.Suffix)…` — PowerBasic.Compiler/Syntax/Parser.Declarations.cs:636
- method `if(width is < 1 or > 16)` — PowerBasic.Compiler/Syntax/Parser.Declarations.cs:679
- method `if(this.IsKeyword(0, "CASE") || this.IsAtTerminator("END UNION") || thi…` — PowerBasic.Compiler/Syntax/Parser.Declarations.cs:722
- method `TypeName(caseName.Position, BuiltinType.None, viewName)` — PowerBasic.Compiler/Syntax/Parser.Declarations.cs:730
- method `ParserException($"expected SEG or FN-name after DEF, found '{name.Text}'", name.Posi…` — PowerBasic.Compiler/Syntax/Parser.Declarations.cs:750
- method `ParserException($"expected single letter, found '{token.Text}'", token.Position)` — PowerBasic.Compiler/Syntax/Parser.Declarations.cs:776
- method `if(this.TryMatchKeyword("SHARED"))` — PowerBasic.Compiler/Syntax/Parser.Declarations.cs:909
- method `if(this.TryMatchKeyword("STATIC"))` — PowerBasic.Compiler/Syntax/Parser.Declarations.cs:914

### Parser.Expressions.cs  `C#, 975 lines`
- namespace `PowerBasic.Compiler.Syntax` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:2
- class `Parser` — Expression parsing: PowerBASIC operator precedence, atoms and postfix chains. — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:6
- method `IfExpr(pos, left, rightTruth, new IntegerLiteralExpr(pos, 0, TypeSuffix.Non…` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:89
- method `MemberExpr(isPos, left, "$tag", TypeSuffix.None)` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:115
- method `NameExpr(bindVar.Position, bindVar.Text, TypeSuffix.None)` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:180
- method `BinaryExpr(pos, BinaryOp.GreaterEqual, value, r.Lo)` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:200
- method `BinaryExpr(pos, BinaryOp.GreaterEqual, value, lo)` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:213
- method `if(this.IsConciseLambdaAhead())` — PB 3.6 concise lambda: (params) => expr. Only an unambiguous parameter — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:379
- method `if(this.Current.Kind == TokenKind.Comma)` — pb36 tuple literal: (e1, e2, ...) - a comma after the first value makes it a tuple — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:384
- method `while(this.Match(TokenKind.Comma))` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:387
- method `TupleExpr(parenPos, elements)` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:390
- method `CallOrIndexExpr(token.Position, token.Text, token.Suffix, this.ParseArgumentList())` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:432
- method `CallOrIndexExpr(token.Position, token.Text, token.Suffix, this.ParseArgumentList())` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:497
- method `if(--depth == 0)` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:578
- method `if(this.Match(TokenKind.DotDot))` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:597
- method `if(this.Current.Kind == TokenKind.LParen)` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:600
- method `ParseSliceBound()` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:602
- method `if(this.Match(TokenKind.Caret))` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:605
- method `if(this.Current.Kind == TokenKind.DotDot)` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:619
- method `RangeElement(first.Position, first, this.ParseExpression())` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:622
- method `FlushLiteral` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:641
- method `if(i + 1 < raw.Length && raw[i + 1] == '{')` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:652
- method `FlushLiteral()` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:657
- method `if(i + 1 < raw.Length && raw[i + 1] == '}')` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:662
- method `for(++i; i < raw.Length && raw[i] != '"'; ++i)` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:688
- method `if(c is ')` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:693
- method `if(c == '}')` — depth; — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:695
- method `ParserException("unterminated '{' in interpolated string", position)` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:703
- method `ParserException("empty '{}' hole in interpolated string", position)` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:708
- method `ParserException($"unexpected '{sub.Current.Text}' in interpolated expression", posit…` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:721
- method `if(this.Match(TokenKind.Period))` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:745
- method `if` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:756
- method `if(k == TokenKind.LParen)` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:886
- method `if(!this.TrySkipBalancedParens(ref i))` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:909
- method `if(kind == TokenKind.RParen)` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:934
- method `if(kind == TokenKind.Comma && depth == 1)` — depth; — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:936
- method `if(kind is TokenKind.EndOfLine or TokenKind.EndOfFile)` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:938
- method `if(kind == TokenKind.RParen)` — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:963
- method `if(kind is TokenKind.EndOfLine or TokenKind.EndOfFile)` — depth; — PowerBasic.Compiler/Syntax/Parser.Expressions.cs:965

### Parser.Io.cs  `C#, 304 lines`
- namespace `PowerBasic.Compiler.Syntax` — PowerBasic.Compiler/Syntax/Parser.Io.cs:3
- class `Parser` — I/O statements: PRINT, INPUT, OPEN/CLOSE, file GET/PUT, SEEK, FIELD, DATA/READ/RESTORE. — PowerBasic.Compiler/Syntax/Parser.Io.cs:7
- method `if` — PowerBasic.Compiler/Syntax/Parser.Io.cs:30
- method `if(this.Match(TokenKind.Comma))` — PowerBasic.Compiler/Syntax/Parser.Io.cs:33
- method `if(!this.IsStatementEnd())` — PowerBasic.Compiler/Syntax/Parser.Io.cs:35
- method `if(this.TryMatchKeyword("ACCESS"))` — PowerBasic.Compiler/Syntax/Parser.Io.cs:104
- method `if(this.TryMatchKeyword("LOCK"))` — PowerBasic.Compiler/Syntax/Parser.Io.cs:108
- method `if(this.TryMatchKeyword("SHARED"))` — PowerBasic.Compiler/Syntax/Parser.Io.cs:112
- method `OpenStmt(pos, first, mode, access, lockSpec, fileNumber, recordLength)` — PowerBasic.Compiler/Syntax/Parser.Io.cs:126
- method `OpenStmt(pos, first, FileMode.Random, null, null, asNumber, asRecLen)` — PowerBasic.Compiler/Syntax/Parser.Io.cs:137
- method `ParserException("legacy OPEN requires a literal mode string", pos)` — PowerBasic.Compiler/Syntax/Parser.Io.cs:148

### Parser.RuntimeTargetMeta.cs  `C#, 115 lines`
- namespace `PowerBasic.Compiler.Syntax` — PowerBasic.Compiler/Syntax/Parser.RuntimeTargetMeta.cs:2
- class `Parser` — PowerBasic.Compiler/Syntax/Parser.RuntimeTargetMeta.cs:4
- method `NormalizeRuntimeTargetNames(arguments)` — PowerBasic.Compiler/Syntax/Parser.RuntimeTargetMeta.cs:18
- method `ValidateIsaPolicyMeta(command, arguments)` — PowerBasic.Compiler/Syntax/Parser.RuntimeTargetMeta.cs:19
- method `ValidateIsaModeMeta(command, arguments)` — PowerBasic.Compiler/Syntax/Parser.RuntimeTargetMeta.cs:23
- method `ValidateFloatPolicyMeta(command, arguments)` — PowerBasic.Compiler/Syntax/Parser.RuntimeTargetMeta.cs:26
- method `ParserException("$CPU requires a supported x86 generation and/or runtime ISA feature…` — PowerBasic.Compiler/Syntax/Parser.RuntimeTargetMeta.cs:47
- method `ParserException($"${command.Text} expects an ISA/mnemonic and NATIVE, EMULATE, ERROR…` — PowerBasic.Compiler/Syntax/Parser.RuntimeTargetMeta.cs:68
- method `if` — PowerBasic.Compiler/Syntax/Parser.RuntimeTargetMeta.cs:93
- method `if` — PowerBasic.Compiler/Syntax/Parser.RuntimeTargetMeta.cs:101

### Parser.cs  `C#, 795 lines`
- namespace `PowerBasic.Compiler.Syntax` — PowerBasic.Compiler/Syntax/Parser.cs:2
- class `Parser` — Recursive-descent parser turning a (preprocessor-expanded) token stream into a — PowerBasic.Compiler/Syntax/Parser.cs:11
- record `StatementGroup` — Parser-only marker: a WITH body whose statements ParseBody splices inline (never reaches the binder… — PowerBasic.Compiler/Syntax/Parser.cs:62
- method `ParserException("NEXT without FOR", parser.Current.Position)` — PowerBasic.Compiler/Syntax/Parser.cs:81
- method `new` — PowerBasic.Compiler/Syntax/Parser.cs:82
- record `DuCase` — PowerBasic.Compiler/Syntax/Parser.cs:124
- method `parse()` — PowerBasic.Compiler/Syntax/Parser.cs:147
- method `if(this.Current.Kind != TokenKind.Colon)` — PowerBasic.Compiler/Syntax/Parser.cs:186
- method `LowerDefers(result)` — PowerBasic.Compiler/Syntax/Parser.cs:209
- method `if(terminators.Length > 0)` — PowerBasic.Compiler/Syntax/Parser.cs:211
- method `LowerDefers(result)` — PowerBasic.Compiler/Syntax/Parser.cs:213
- method `LowerDefers(result)` — PowerBasic.Compiler/Syntax/Parser.cs:216
- method `LabelStmt(token.Position, token.Text)` — PowerBasic.Compiler/Syntax/Parser.cs:308
- method `if(this.Match(TokenKind.Comma))` — PowerBasic.Compiler/Syntax/Parser.cs:401
- method `StaticAssertStmt(token.Position, condition, message)` — PowerBasic.Compiler/Syntax/Parser.cs:510
- method `ResourceStmt(token.Position, name.Text, file.StringValue!)` — PowerBasic.Compiler/Syntax/Parser.cs:518
- method `ParserException($"REM ${command.Text} takes no arguments", command.Position)` — PowerBasic.Compiler/Syntax/Parser.cs:534
- method `MetaStmt(command.Position, command.Text, [])` — PowerBasic.Compiler/Syntax/Parser.cs:535
- method `MetaStmt(command.Position, "INCLUDE", [new Token(TokenKind.StringLiteral, fil…` — PowerBasic.Compiler/Syntax/Parser.cs:538
- method `RequireOneOf(command, arguments, "EXE", "UNIT", "CHAIN")` — PowerBasic.Compiler/Syntax/Parser.cs:565
- method `RequirePair(command, arguments, ["BOUNDS", "NUMERIC", "OVERFLOW", "STACK", "ALL"…` — PowerBasic.Compiler/Syntax/Parser.cs:571
- method `if(arguments is [{ Kind: TokenKind.Identifier } optimize] && optimize.T…` — PowerBasic.Compiler/Syntax/Parser.cs:574
- method `RequireOneOf(command, arguments, "SIZE", "SPEED")` — PowerBasic.Compiler/Syntax/Parser.cs:579
- method `RequireSingleInteger(command, arguments)` — PowerBasic.Compiler/Syntax/Parser.cs:585
- method `RequireIntegerOneOf(command, arguments, 1, 2, 4, 8, 16, 32)` — PowerBasic.Compiler/Syntax/Parser.cs:588
- method `RequireOneOf(command, arguments, "ALL", "ARRAY")` — PowerBasic.Compiler/Syntax/Parser.cs:594
- method `if(arguments is not [{ Kind: TokenKind.Identifier } dialect] || !Dialec…` — PowerBasic.Compiler/Syntax/Parser.cs:605
- method `ParserException("$COMPAT requires one known dialect name", command.Position)` — PowerBasic.Compiler/Syntax/Parser.cs:607
- method `ParserException($"unknown or malformed metastatement '${command.Text}'", command.Pos…` — PowerBasic.Compiler/Syntax/Parser.cs:620
- method `if(args.Count != duCase.Fields.Count)` — PowerBasic.Compiler/Syntax/Parser.cs:750
- method `AssignStmt(target.Position, new MemberExpr(target.Position, target, "$tag", Typ…` — PowerBasic.Compiler/Syntax/Parser.cs:753
- method `MemberExpr(target.Position, new MemberExpr(target.Position, target, "$" + duCas…` — PowerBasic.Compiler/Syntax/Parser.cs:758
- method `StatementGroup(target.Position, group)` — PowerBasic.Compiler/Syntax/Parser.cs:760
- class `ParserException` — Raised when the token stream violates PowerBASIC 3.5 grammar. — PowerBasic.Compiler/Syntax/Parser.cs:792

### Preprocessor.cs  `C#, 370 lines`
- namespace `PowerBasic.Compiler.Syntax` — PowerBasic.Compiler/Syntax/Preprocessor.cs:1
- class `Preprocessor` — Streams lexed tokens with metastatement handling: $INCLUDE splices the — PowerBasic.Compiler/Syntax/Preprocessor.cs:10
- method `while` — PowerBasic.Compiler/Syntax/Preprocessor.cs:51
- method `switch(token.Kind)` — PowerBasic.Compiler/Syntax/Preprocessor.cs:54
- method `foreach(var t in this.FlushLine(pending))` — PowerBasic.Compiler/Syntax/Preprocessor.cs:63
- method `foreach(var t in this.FlushLine(pending))` — PowerBasic.Compiler/Syntax/Preprocessor.cs:68
- method `if(!tokens.MoveNext() || tokens.Current.Kind != TokenKind.StringLiteral)` — PowerBasic.Compiler/Syntax/Preprocessor.cs:70
- method `PreprocessorException("$INCLUDE requires a quoted file name", token.Position)` — PowerBasic.Compiler/Syntax/Preprocessor.cs:71
- method `foreach(var t in this.ExpandInclude(tokens.Current))` — PowerBasic.Compiler/Syntax/Preprocessor.cs:72
- method `foreach(var t in this.FlushLine(pending))` — PowerBasic.Compiler/Syntax/Preprocessor.cs:77
- method `if(!Parser.TryParseMicrosoftInclude(token.StringValue ?? "", out var mi…` — PowerBasic.Compiler/Syntax/Preprocessor.cs:79
- method `PreprocessorException("REM $INCLUDE requires : 'file-name'", token.Position)` — PowerBasic.Compiler/Syntax/Preprocessor.cs:80
- method `foreach(var t in this.ExpandInclude(microsoftName))` — PowerBasic.Compiler/Syntax/Preprocessor.cs:83
- method `foreach(var t in this.FlushLine(pending))` — PowerBasic.Compiler/Syntax/Preprocessor.cs:88
- method `PreprocessorException($"cannot read $INCLUDE file '{name}'", nameToken.Position)` — PowerBasic.Compiler/Syntax/Preprocessor.cs:119
- method `if(!this.TryEvaluate(condition, out var value))` — PowerBasic.Compiler/Syntax/Preprocessor.cs:127
- method `PreprocessorException("$IF condition is not a constant expression of known equates", token…` — PowerBasic.Compiler/Syntax/Preprocessor.cs:128
- method `if(value != 0)` — PowerBasic.Compiler/Syntax/Preprocessor.cs:129
- method `if(this._openConditionals.Count == 0)` — PowerBasic.Compiler/Syntax/Preprocessor.cs:139
- method `PreprocessorException("$ELSEIF without $IF", token.Position)` — PowerBasic.Compiler/Syntax/Preprocessor.cs:140
- method `ReadToEndOfLine(tokens)` — reaching a live $ELSEIF means an earlier branch was taken - skip to $ENDIF — PowerBasic.Compiler/Syntax/Preprocessor.cs:142
- method `SkipRegion(tokens, token, allowElse: false)` — PowerBasic.Compiler/Syntax/Preprocessor.cs:143
- method `if(this._openConditionals.Count == 0)` — PowerBasic.Compiler/Syntax/Preprocessor.cs:148
- method `PreprocessorException("$ELSE without $IF", token.Position)` — PowerBasic.Compiler/Syntax/Preprocessor.cs:149
- method `SkipRegion(tokens, token, allowElse: false)` — reaching a live $ELSE means the $IF branch was taken - skip to $ENDIF — PowerBasic.Compiler/Syntax/Preprocessor.cs:151
- method `if(this._openConditionals.Count == 0)` — PowerBasic.Compiler/Syntax/Preprocessor.cs:156
- method `PreprocessorException("$ENDIF without $IF", token.Position)` — PowerBasic.Compiler/Syntax/Preprocessor.cs:157
- method `if(!this.TryEvaluate(condition, out var value))` — PowerBasic.Compiler/Syntax/Preprocessor.cs:177
- method `PreprocessorException("$ELSEIF condition is not a constant expression of known equates", o…` — PowerBasic.Compiler/Syntax/Preprocessor.cs:178
- method `if(value == 0)` — PowerBasic.Compiler/Syntax/Preprocessor.cs:179
- enum `SkipStop` — PowerBasic.Compiler/Syntax/Preprocessor.cs:201
- method `switch` — PowerBasic.Compiler/Syntax/Preprocessor.cs:213
- method `PreprocessorException` — PowerBasic.Compiler/Syntax/Preprocessor.cs:229
- method `PreprocessorException("division by zero in constant expression", t[pos - 1].Position)` — PowerBasic.Compiler/Syntax/Preprocessor.cs:316
- method `PreprocessorException("unexpected end of constant expression", default)` — PowerBasic.Compiler/Syntax/Preprocessor.cs:324
- method `PreprocessorException($"'{token.Text}' is not usable in a constant expression", token.Posi…` — PowerBasic.Compiler/Syntax/Preprocessor.cs:356
- class `PreprocessorException` — Raised for metastatement errors ($INCLUDE resolution, $IF evaluation, nesting). — PowerBasic.Compiler/Syntax/Preprocessor.cs:367

### SourcePosition.cs  `C#, 7 lines`
- namespace `PowerBasic.Compiler.Syntax` — PowerBasic.Compiler/Syntax/SourcePosition.cs:1
- record `SourcePosition` — A location in PowerBASIC source, 1-based. — PowerBasic.Compiler/Syntax/SourcePosition.cs:4

### Token.cs  `C#, 7 lines`
- namespace `PowerBasic.Compiler.Syntax` — PowerBasic.Compiler/Syntax/Token.cs:1
- record `Token` — A single lexical token. — PowerBasic.Compiler/Syntax/Token.cs:4

### TokenKind.cs  `C#, 88 lines`
- namespace `PowerBasic.Compiler.Syntax` — PowerBasic.Compiler/Syntax/TokenKind.cs:1
- enum `TokenKind` — Lexical token categories of PowerBASIC 3.5 source. — PowerBasic.Compiler/Syntax/TokenKind.cs:4

### TypeSuffix.cs  `C#, 57 lines`
- namespace `PowerBasic.Compiler.Syntax` — PowerBasic.Compiler/Syntax/TypeSuffix.cs:1
- enum `TypeSuffix` — PowerBASIC type-declaration suffix attached to an identifier or literal. — PowerBasic.Compiler/Syntax/TypeSuffix.cs:4
- class `TypeSuffixExtensions` — PowerBasic.Compiler/Syntax/TypeSuffix.cs:33

## PowerBasic.Compiler/Syntax/Ast/

### AstQuery.cs  `C#, 46 lines`
- namespace `PowerBasic.Compiler.Syntax.Ast` — PowerBasic.Compiler/Syntax/Ast/AstQuery.cs:1
- class `AstQuery` — AST query helpers shared across the binder and code generator. — PowerBasic.Compiler/Syntax/Ast/AstQuery.cs:6

### Expressions.cs  `C#, 159 lines`
- namespace `PowerBasic.Compiler.Syntax.Ast` — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:1
- record `Expression` — Base of all expression nodes. — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:4
- record `IntegerLiteralExpr` — Integer literal, e.g. 42, &amp;H4F05; suffix may force LONG. — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:7
- record `FloatLiteralExpr` — Floating-point literal; suffix may force SINGLE/DOUBLE/EXT. — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:10
- record `StringLiteralExpr` — String literal. — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:13
- record `NamedConstantExpr` — Named-constant (equate) reference, e.g. %SVGA_MODEX. — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:16
- record `NameExpr` — Bare identifier reference (variable, parameter, or parameterless function - resolved semantically). — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:19
- record `TupleExpr` — pb36 tuple literal (e1, e2, ...): an anonymous value aggregate, used to build a tuple value or for … — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:22
- record `CallOrIndexExpr` — name(arg, ...) - array element, intrinsic or user FUNCTION call; the — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:28
- record `MemberExpr` — UDT member access, e.g. ctx.CurrentMode; is a name, index or another member access. — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:31
- record `IndexExpr` — Indexing of a non-name target, e.g. the array-field access ctx.NamedTimers(i) — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:37
- record `PtrDerefExpr` — Pointer dereference @p (PB 3.2) or indexed @p[i] (PB 3.5, — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:43
- record `ByValArgExpr` — Argument-position BYVAL override: passes the pointer target / forces by-value. — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:46
- record `NothingExpr` — pb36 NOTHING: the empty value of a nullable type (clears its presence flag on assignment). — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:49
- record `CoalesceExpr` — pb36 null-coalescing value ?? fallback: the nullable's value when present, else the fallback. — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:52
- record `NullConditionalExpr` — pb36 null-conditional access on a nullable target: target?.Member ( set) — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:59
- enum `BinaryOp` — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:60
- record `BinaryExpr` — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:72
- enum `UnaryOp` — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:74
- record `UnaryExpr` — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:76
- record `FileNumberExpr` — File-number expression, e.g. #1 in I/O statements. — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:80
- record `AnyMatchExpr` — Argument-position ANY match-set prefix, e.g. INSTR(s$, ANY "-/") or — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:86
- record `IfExpr` — PB 3.6 short-circuit ternary: IF(condition, whenTrue, whenFalse) - evaluates — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:92
- record `NewExpr` — PB 3.6 object initializer: NEW type { .field = value, ... }. Valid only as a — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:99
- record `NamedArgExpr` — PB 3.6 named call argument: name := value. The binder reorders these to positional order. — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:102
- record `FromEndExpr` — PB 3.6 from-end array index: arr(^n) = the n-th element from the end (^1 = last). Valid only as an … — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:105
- record `RangeArgExpr` — pb36 array slice argument a(lo TO hi): either bound may be null (the source's LBOUND/UBOUND) or a .… — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:108
- record `LambdaExpr` — PB 3.6 inline lambda: FUNCTION(params) [AS type] =&gt; expr, or the statement-bodied SUB form — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:116
- record `CollectionElement` — One element of a PB 3.6 collection literal: a single value, an inclusive integer range, or a spread… — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:122
- record `ValueElement` — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:123
- record `RangeElement` — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:124
- record `SpreadElement` — Spread of another array: ..arr (all elements) or the slice form ..arr(lo TO hi) - — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:130
- record `ArrayLiteralExpr` — PB 3.6 array-initializer literal: { v1, v2, lo..hi, ..arr }, used as a DIM — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:143
- record `InterpolationPart` — One part of a PB 3.6 interpolated string: a literal text run, or a {expr[:fmt]} — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:149
- record `InterpolatedStringExpr` — PB 3.6 interpolated string $"text {expr} {expr:fmt}". The binder desugars it to — PowerBasic.Compiler/Syntax/Ast/Expressions.cs:158

### Statements.cs  `C#, 462 lines`
- namespace `PowerBasic.Compiler.Syntax.Ast` — PowerBasic.Compiler/Syntax/Ast/Statements.cs:1
- record `Statement` — Base of all statement nodes. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:4
- record `CompilationUnit` — A whole compilation unit (main program, unit, or include-expanded module). — PowerBasic.Compiler/Syntax/Ast/Statements.cs:7
- record `DeferredSourceStmt` — BASICA/GW-BASIC text that the interpreter stores but does not parse until execution reaches it. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:14
- enum `BuiltinType` — Built-in scalar type names usable in an AS clause. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:19
- record `TypeName` — An AS-clause type: builtin, fixed string (STRING * n), ASCIIZ — PowerBasic.Compiler/Syntax/Ast/Statements.cs:30
- enum `Visibility` — region declarations — PowerBasic.Compiler/Syntax/Ast/Statements.cs:46
- enum `CallConvention` — Calling convention of a SUB/FUNCTION/DECLARE. is PB's — PowerBasic.Compiler/Syntax/Ast/Statements.cs:56
- record `Parameter` — Formal parameter: BYVAL/SEG modifiers, optional AS type, optional () — PowerBasic.Compiler/Syntax/Ast/Statements.cs:63
- record `SubDecl` — SUB definition. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:66
- record `FunctionDecl` — FUNCTION definition; return type from name suffix or AS clause. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:76
- record `DeclareStmt` — DECLARE SUB/FUNCTION prototype. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:86
- record `TypeField` — One field inside TYPE/UNION; array fields carry bounds (lower TO upper | upper). — PowerBasic.Compiler/Syntax/Ast/Statements.cs:92
- enum `TypeMemberKind` — The four member shapes a PB 3.6 TYPE block can declare alongside its fields. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:95
- record `TypeMember` — A member declared inside a TYPE block (PB 3.6): a SUB/FUNCTION method or a — PowerBasic.Compiler/Syntax/Ast/Statements.cs:104
- record `TypeDecl` — TYPE ... END TYPE. is empty unless the block declares methods/properties (pb36). — PowerBasic.Compiler/Syntax/Ast/Statements.cs:107
- record `UnionDecl` — UNION ... END UNION. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:120
- record `TypeAliasDecl` — pb36 type alias: TYPE Name AS type (single line, no END TYPE) - a bind-time name for an existing ty… — PowerBasic.Compiler/Syntax/Ast/Statements.cs:123
- record `StaticAssertStmt` — pb36 $ASSERT cond [, "message"]: a compile-time assertion checked by the binder; emits no code. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:126
- record `ResourceStmt` — pb36 $RESOURCE name, "file": bakes the file's bytes into the image as a static BYTE array name(0 TO… — PowerBasic.Compiler/Syntax/Ast/Statements.cs:129
- record `RequireStmt` — pb36 contract check: REQUIRE cond [, "msg"] / ENSURE cond [, "msg"] - raises error 5 when violated;… — PowerBasic.Compiler/Syntax/Ast/Statements.cs:132
- record `DefFnDecl` — DEF FN single-line or block form. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:135
- record `DefTypeStmt` — DEFINT/DEFLNG/DEFSNG/DEFDBL/DEFEXT/DEFSTR letter-range default typing. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:138
- record `EquateStmt` — Named-constant (equate) definition: %NAME = const-expr. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:141
- record `EnumDecl` — PB 3.6 ENUM Name [AS type] : A [= v], B, ... : END ENUM: a group of named — PowerBasic.Compiler/Syntax/Ast/Statements.cs:148
- record `EventDeclStmt` — pb36 EVENT name AS delegate: declares a multicast event whose handlers match the delegate — PowerBasic.Compiler/Syntax/Ast/Statements.cs:156
- record `GroupStmt` — Synthesized statement group: a desugar that expands one surface statement into several. The — PowerBasic.Compiler/Syntax/Ast/Statements.cs:163
- record `VariableDecl` — One declared entity inside DIM/LOCAL/STATIC/SHARED/PUBLIC: optional bounds (lower TO upper | upper). — PowerBasic.Compiler/Syntax/Ast/Statements.cs:175
- enum `StorageClass` — PowerBasic.Compiler/Syntax/Ast/Statements.cs:176
- enum `ArrayClass` — Array allocation class selected on DIM (see docs/DIALECTS.md). PB 3.6 adds Ems/Xms (external-memory… — PowerBasic.Compiler/Syntax/Ast/Statements.cs:180
- record `DimStmt` — DIM/LOCAL/STATIC/SHARED/PUBLIC/EXT/COMMON declaration; DIM may carry an extra SHARED flag — PowerBasic.Compiler/Syntax/Ast/Statements.cs:189
- record `RedimStmt` — REDIM (re-dimension a $DYNAMIC array); PRESERVE (3.5) keeps existing contents. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:192
- record `EraseStmt` — ERASE array, ... — PowerBasic.Compiler/Syntax/Ast/Statements.cs:195
- record `AssignStmt` — Assignment, incl. LET form. Target is a name, array element or member access. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:202
- record `IncrDecrStmt` — INCR/DECR x [, amount]. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:205
- record `CallStmt` — SUB invocation: CALL Name(args) or bare Name args. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:208
- record `MemberCallStmt` — PB 3.6 statement-form member call: receiver.Member(args) / receiver.Member args. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:215
- record `CallPtrStmt` — Far call through a 32-bit pointer: CALL DWORD ptr [BDECL|CDECL|SDECL] (args). — PowerBasic.Compiler/Syntax/Ast/Statements.cs:218
- record `MidAssignStmt` — MID$(s$, start [, len]) = value$ statement form. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:221
- record `AscAssignStmt` — ASC(s$ [, position]) = code statement form (PB 3.5). — PowerBasic.Compiler/Syntax/Ast/Statements.cs:224
- record `StdOutStmt` — STDOUT [s$] [;] - writes to DOS handle 1 (redirectable); trailing ';' suppresses the newline (PB 3.… — PowerBasic.Compiler/Syntax/Ast/Statements.cs:227
- record `StdInStmt` — STDIN n, s$ (read n bytes) / STDIN LINE, s$ (read a line) from DOS handle 0 (PB 3.5). — PowerBasic.Compiler/Syntax/Ast/Statements.cs:230
- record `LsetRsetStmt` — LSET/RSET str-or-field = value. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:233
- record `SwapStmt` — SWAP a, b. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:236
- record `ReplaceStmt` — REPLACE find$ WITH with$ IN target$ - replaces every occurrence. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:239
- enum `BitOp` — PowerBasic.Compiler/Syntax/Ast/Statements.cs:240
- record `BitStmt` — BIT SET/RESET/TOGGLE var, bit-number (PB 3.0). — PowerBasic.Compiler/Syntax/Ast/Statements.cs:244
- record `ArraySortStmt` — ARRAY SORT arr([start]) [FOR count] [, FROM x TO y] [, COLLATE c$] [, ASCEND|DESCEND] [, TAGARRAY t… — PowerBasic.Compiler/Syntax/Ast/Statements.cs:250
- record `ArrayScanStmt` — ARRAY SCAN arr([start]) [FOR count] [, FROM x TO y] [, COLLATE c$], relop expr, TO var - — PowerBasic.Compiler/Syntax/Ast/Statements.cs:256
- record `IfStmt` — IF in block or single-line form; ElseIfs are (condition, body) pairs. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:263
- enum `CaseComparison` — PowerBasic.Compiler/Syntax/Ast/Statements.cs:264
- record `CaseSelector` — One CASE selector: a value, a range (x TO y) or a relation (IS &gt; x). — PowerBasic.Compiler/Syntax/Ast/Statements.cs:268
- record `CaseArm` — SELECT CASE arm; empty Selectors = CASE ELSE. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:271
- record `SelectStmt` — PowerBasic.Compiler/Syntax/Ast/Statements.cs:272
- record `ForStmt` — PowerBasic.Compiler/Syntax/Ast/Statements.cs:274
- enum `LoopTestKind` — PowerBasic.Compiler/Syntax/Ast/Statements.cs:276
- record `DoLoopStmt` — DO/LOOP with optional pre- or post-test; also covers WHILE/WEND (pre-test While). — PowerBasic.Compiler/Syntax/Ast/Statements.cs:280
- record `ForEachStmt` — FOR EACH variable IN collection (PB 3.6): the binder lowers it per the collection's static — PowerBasic.Compiler/Syntax/Ast/Statements.cs:286
- enum `ExitKind` — PowerBasic.Compiler/Syntax/Ast/Statements.cs:287
- record `ExitStmt` — PowerBasic.Compiler/Syntax/Ast/Statements.cs:289
- record `ExitFarStmt` — EXIT FAR AT label records the unwind point (stack mark + target); — PowerBasic.Compiler/Syntax/Ast/Statements.cs:296
- record `IterateStmt` — ITERATE [FOR|DO|LOOP|WHILE] - continue with the next loop pass. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:299
- record `LabelStmt` — Label definition (identifier label or numeric line number). — PowerBasic.Compiler/Syntax/Ast/Statements.cs:302
- record `GotoStmt` — PowerBasic.Compiler/Syntax/Ast/Statements.cs:303
- record `GosubStmt` — PowerBasic.Compiler/Syntax/Ast/Statements.cs:305
- record `GotoPtrStmt` — GOTO DWORD ptr32 (PB 3.2): far jump through a 32-bit code pointer. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:309
- record `GosubPtrStmt` — GOSUB DWORD ptr32 (PB 3.2): far call through a 32-bit code pointer. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:312
- record `ReturnStmt` — RETURN [label]. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:315
- record `OnGotoStmt` — ON expr GOTO/GOSUB label-list. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:318
- record `ChainStmt` — CHAIN file$ (COMMON carries over) / RUN file$ (fresh start). — PowerBasic.Compiler/Syntax/Ast/Statements.cs:321
- record `EndStmt` — END / STOP / SYSTEM program termination (END SUB etc. are structural, not this). — PowerBasic.Compiler/Syntax/Ast/Statements.cs:324
- record `YieldStmt` — PB 3.6 YIELD &lt;expression&gt;: suspends the enclosing coroutine SUB/FUNCTION, — PowerBasic.Compiler/Syntax/Ast/Statements.cs:331
- record `OnErrorStmt` — ON ERROR GOTO label|0 / RESUME NEXT-style registration. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:338
- enum `ResumeKind` — PowerBasic.Compiler/Syntax/Ast/Statements.cs:339
- record `ResumeStmt` — PowerBasic.Compiler/Syntax/Ast/Statements.cs:341
- record `ErrorStmt` — ERROR n - raise a runtime error. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:345
- record `TryStmt` — PB 3.6 structured exception handling: TRY / [CATCH] / [FINALLY] / END TRY. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:353
- record `HandlerSaveStmt` — Saves the current ON ERROR handler triple (rt_onerr / _bp / _sp) into the enumerator fields, so it … — PowerBasic.Compiler/Syntax/Ast/Statements.cs:361
- record `HandlerRestoreStmt` — Restores the saved ON ERROR handler triple from the enumerator fields back into rt_onerr / _bp / _s… — PowerBasic.Compiler/Syntax/Ast/Statements.cs:364
- record `HandlerArmStmt` — Arms the generator's catch dispatcher for the current MoveNext frame: rt_onerr = OFFSET CatchLabel,… — PowerBasic.Compiler/Syntax/Ast/Statements.cs:367
- record `HandlerReraiseStmt` — Re-raises the still-set ERR to the (now restored) outer handler - the no-CATCH fault edge of a gene… — PowerBasic.Compiler/Syntax/Ast/Statements.cs:370
- record `DeferStmt` — pb36 DEFER stmt: schedules to run when the enclosing block exits (normally or via a fault). Lowered… — PowerBasic.Compiler/Syntax/Ast/Statements.cs:373
- record `DestructureStmt` — pb36 tuple destructuring: a, b = expr assigns each tuple element of to the corresponding target. Lo… — PowerBasic.Compiler/Syntax/Ast/Statements.cs:376
- record `OnEventStmt` — ON KEY(n)/TIMER(n)/COM(n)... GOSUB label event registration. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:379
- record `EventControlStmt` — KEY(n) ON/OFF/STOP, TIMER ON/... event arming. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:382
- enum `PrintSeparator` — region I/O — PowerBasic.Compiler/Syntax/Ast/Statements.cs:387
- record `PrintItem` — One PRINT list item with its trailing separator; SPC(n)/TAB(n) appear as expressions. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:391
- record `PrintStmt` — PRINT/LPRINT [#n,] [USING fmt;] items. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:394
- record `InputStmt` — INPUT/LINE INPUT [#n,] ["prompt",|;] var-list. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:397
- record `WriteStmt` — WRITE [#n,] expr-list - comma-delimited output, strings quoted. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:400
- enum `FileMode` — PowerBasic.Compiler/Syntax/Ast/Statements.cs:401
- record `OpenStmt` — OPEN file$ FOR mode [ACCESS ...] [LOCK ...] AS [#]n [LEN = reclen]. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:405
- record `CloseStmt` — CLOSE [[#]n, ...]; empty = close all. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:408
- record `GetPutFileStmt` — GET/PUT #n [, record [, var]] - file form. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:411
- record `SeekStmt` — SEEK #n, position. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:414
- record `FieldStmt` — FIELD #n, width AS strvar, ... — PowerBasic.Compiler/Syntax/Ast/Statements.cs:417
- record `DataStmt` — region DATA — PowerBasic.Compiler/Syntax/Ast/Statements.cs:422
- record `ReadStmt` — PowerBasic.Compiler/Syntax/Ast/Statements.cs:424
- record `RestoreStmt` — PowerBasic.Compiler/Syntax/Ast/Statements.cs:426
- record `InlineAsmStmt` — One raw inline-assembly statement (the text after !). — PowerBasic.Compiler/Syntax/Ast/Statements.cs:434
- record `DefSegStmt` — DEF SEG [= expr]. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:437
- record `CommandStmt` — Catch-all for keyword statements taking a plain expression list (BEEP, CLS, POKE, — PowerBasic.Compiler/Syntax/Ast/Statements.cs:444
- record `LineStmt` — Graphics LINE [(x1,y1)]-(x2,y2) [,[color][,B[F][,style]]]. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:447
- record `CircleStmt` — Graphics CIRCLE (x,y), r [,color [,start [,end [,aspect]]]]. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:450
- record `PsetStmt` — Graphics PSET/PRESET (x,y) [,color]. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:453
- record `GetPutGraphicsStmt` — Graphics GET/PUT (x1,y1)-(x2,y2), array / PUT (x,y), array [,verb]. — PowerBasic.Compiler/Syntax/Ast/Statements.cs:456
- record `MetaStmt` — A metastatement kept for the driver ($CPU, $STACK, $COMPILE, $LINK, $ERROR, ...), arguments as raw … — PowerBasic.Compiler/Syntax/Ast/Statements.cs:459

## pbc/

### Driver.cs  `C#, 442 lines`
- namespace `PowerBasic.Compiler.Cli` — pbc/Driver.cs:7
- class `Driver` — Command-line front end for the PowerBASIC 3.5 compiler. — pbc/Driver.cs:11
- method `RunLib(args[1..], stdout, stderr)` — pbc/Driver.cs:20
- method `if(!DialectFacts.TryParse(name, out dialect))` — pbc/Driver.cs:50
- method `if(source != null)` — pbc/Driver.cs:94
- method `if` — pbc/Driver.cs:116
- method `foreach(var error in model.Errors)` — pbc/Driver.cs:133
- method `if` — pbc/Driver.cs:141
- method `if(optimize ?? (dialect == Dialect.Pb36))` — pbc/Driver.cs:150
- method `if(output != null)` — pbc/Driver.cs:155
- method `if` — pbc/Driver.cs:163
- method `if(module is null)` — pbc/Driver.cs:166
- method `foreach(var f in module.Functions)` — PB computes integral +/-/* in floating point (for PRINT precision); where the result is — pbc/Driver.cs:176
- method `foreach(var f in module.Functions)` — pbc/Driver.cs:182
- method `if(verifyErrors.Count > 0)` — pbc/Driver.cs:188
- method `foreach(var e in verifyErrors)` — pbc/Driver.cs:190
- method `if(text is null)` — pbc/Driver.cs:204
- method `if(output != null)` — pbc/Driver.cs:209
- method `if` — pbc/Driver.cs:229
- method `if(generator.Errors.Count > 0)` — pbc/Driver.cs:235
- method `if` — pbc/Driver.cs:246
- method `if(IsUnitCompile(model))` — pbc/Driver.cs:251
- method `if(!TryLoadLinkTargets(model, [.. linkPaths, sourceDir], stderr, out va…` — pbc/Driver.cs:255
- method `if(image.Length == 0 && generator.Errors.Count == 0)` — pbc/Driver.cs:258
- method `if(generator.Errors.Count > 0)` — pbc/Driver.cs:261
- method `if(!TryLoadLinkTargets(model, [.. linkPaths, sourceDir], stderr, out va…` — pbc/Driver.cs:282
- method `if` — pbc/Driver.cs:290
- method `if(path.EndsWith(".OBJ", StringComparison.OrdinalIgnoreCase))` — pbc/Driver.cs:331
- method `if(path.EndsWith(".LIB", StringComparison.OrdinalIgnoreCase))` — pbc/Driver.cs:336
- method `foreach(var module in Emit.Omf.OmfReader.ReadLibrary(File.ReadAllBytes(path)…` — pbc/Driver.cs:340
- method `if(path.EndsWith(".PBL", StringComparison.OrdinalIgnoreCase))` — pbc/Driver.cs:346
- method `foreach(var file in unitFiles)` — pbc/Driver.cs:366
- method `if(output.EndsWith(".LIB", StringComparison.OrdinalIgnoreCase))` — a .LIB output is a foreign-consumable Intel OMF archive; anything else is our own .PBL — pbc/Driver.cs:375
- method `if(file.EndsWith(".PBU", StringComparison.OrdinalIgnoreCase))` — pbc/Driver.cs:389
- method `foreach(var unit in PblFile.Read(stream).Units)` — pbc/Driver.cs:393
- method `DescribeUnit(unit, stdout)` — pbc/Driver.cs:394

### Program.cs  `C#, 4 lines`
- (no top-level symbols found)

## runtime/

### pbc_rt.c  `C, 865 lines`
> ==========================================================================
- function `rt_xalloc(size_t n)` — runtime/pbc_rt.c:24
- function `rt_new(int32_t len)` — runtime/pbc_rt.c:33
- function `rt_make(const char *bytes, int32_t len)` — runtime/pbc_rt.c:41
- function `rt_of(void *h)` — An absent handle reads as the empty string, exactly like an unassigned PB string. — runtime/pbc_rt.c:49
- function `rt_str_const(void *bytes, int32_t len)` — runtime/pbc_rt.c:54
- function `rt_str_concat(void *a, void *b)` — runtime/pbc_rt.c:56
- function `rt_str_concat_n(int32_t count, ...)` — A whole concatenation chain built with one allocation: sum the operand lengths, reserve once, — runtime/pbc_rt.c:67
- function `rt_str_append_var(void *target, void *source)` — Append onto a string the caller is the last owner of. The DOS runtime grows the block in place — runtime/pbc_rt.c:93
- function `rt_str_append_lit(void *target, void *bytes, int32_t len)` — runtime/pbc_rt.c:101
- function `rt_str_len(void *s)` — runtime/pbc_rt.c:109
- function `rt_str_compare(void *a, void *b)` — runtime/pbc_rt.c:111
- function `rt_str_compare_eq(void *a, void *b)` — Equality only: 0 when the two strings are equal and 1 when they are not. Unequal lengths answer — runtime/pbc_rt.c:122
- function `rt_str_dup(void *s)` — An owned copy, so the value handed on is a temporary the consuming routines may free. Every — runtime/pbc_rt.c:132
- function `rt_str_left(void *s, int32_t n)` — runtime/pbc_rt.c:137
- function `rt_str_right(void *s, int32_t n)` — runtime/pbc_rt.c:144
- function `rt_str_mid(void *s, int32_t start, int32_t len)` — runtime/pbc_rt.c:151
- function `rt_str_mid2(void *s, int32_t start)` — runtime/pbc_rt.c:159
- function `rt_str_mid_assign(void *dst, int32_t start, int32_t len, void *src)` — runtime/pbc_rt.c:164
- function `rt_map(void *s, int upper)` — runtime/pbc_rt.c:175
- function `rt_str_ucase(void *s)` — runtime/pbc_rt.c:184
- function `rt_str_lcase(void *s)` — runtime/pbc_rt.c:185
- function `rt_str_ltrim(void *s)` — runtime/pbc_rt.c:187
- function `rt_str_rtrim(void *s)` — runtime/pbc_rt.c:194
- function `rt_str_space(int32_t n)` — runtime/pbc_rt.c:201
- function `rt_str_string(int32_t n, int32_t ch)` — runtime/pbc_rt.c:207
- function `rt_str_string_s(int32_t n, void *src)` — runtime/pbc_rt.c:213
- function `rt_str_repeat(int32_t n, void *src)` — REPEAT$(n, s$) - the WHOLE string n times. Not rt_str_string_s, which is STRING$ and repeats only — runtime/pbc_rt.c:220
- function `rt_str_asc_set(void *s, int16_t pos, int16_t code)` — ASC(s$, n) = code. Out-of-range positions are IGNORED, matching the DOS rt_ascset, which returns — runtime/pbc_rt.c:231
- function `rt_str_free(void *s)` — The C runtime allocates with malloc and never compacts, so freeing is optional for correctness — runtime/pbc_rt.c:241
- function `rt_rnd_next(void)` — runtime/pbc_rt.c:255
- function `rt_rnd(void)` — runtime/pbc_rt.c:263
- function `rt_rnd_range(int32_t lower, int32_t upper)` — RND(a, z): a LONG in [a, z] inclusive - a different answer from the bare RND's fraction. — runtime/pbc_rt.c:266
- function `rt_str_chr(int32_t code)` — runtime/pbc_rt.c:273
- function `rt_str_asc(void *s)` — runtime/pbc_rt.c:278
- function `rt_str_char_at(void *s, int32_t index)` — ASC(MID$(s$, i, 1)) as one read, matching the DOS rt_charat at both ends: the start clamps to 1 — runtime/pbc_rt.c:286
- function `rt_radix_packed(int32_t v, int32_t packed)` — HEX$/OCT$/BIN$, all one routine, matching the DOS rt_radix exactly. — runtime/pbc_rt.c:300
- function `rt_str_radix(int32_t v, int32_t packed)` — runtime/pbc_rt.c:318
- function `rt_str_hex(int32_t v)` — runtime/pbc_rt.c:319
- function `rt_str_oct(int32_t v)` — runtime/pbc_rt.c:320
- function `rt_str_bin(int32_t v)` — runtime/pbc_rt.c:321
- function `rt_str_instr(void *hay, void *needle)` — runtime/pbc_rt.c:323
- function `rt_str_instr_start(int32_t start, void *hay, void *needle)` — runtime/pbc_rt.c:325
- function `rt_str_val(void *s)` — runtime/pbc_rt.c:336
- function `rt_str_to_fixed(void *dst, int32_t n, void *src)` — runtime/pbc_rt.c:345
- function `rt_str_to_fixed_r(void *dst, int32_t n, void *src)` — RSET into a fixed field: blank the whole width, then land the value against its right edge. — runtime/pbc_rt.c:356
- function `rt_str_from_fixed(void *src, int32_t n)` — runtime/pbc_rt.c:364
- function `rt_str_justify(void *target, void *value, int16_t right)` — LSET/RSET into a DYNAMIC string: the target keeps its handle AND its length - that is what makes — runtime/pbc_rt.c:369
- function `rt_strip_leading_zero(char *b)` — PB prints a fraction without its leading zero (".0001", not "0.0001"). — runtime/pbc_rt.c:382
- function `rt_fmt_float(char *buf, size_t cap, long double v, int digits)` — runtime/pbc_rt.c:389
- function `rt_str_num(const char *text)` — runtime/pbc_rt.c:394
- function `rt_str_int(long long v)` — runtime/pbc_rt.c:407
- function `rt_str_from_i8(int8_t v)` — runtime/pbc_rt.c:413
- function `rt_str_from_u8(uint8_t v)` — runtime/pbc_rt.c:414
- function `rt_str_from_i16(int16_t v)` — runtime/pbc_rt.c:415
- function `rt_str_from_u16(uint16_t v)` — runtime/pbc_rt.c:416
- function `rt_str_from_i32(int32_t v)` — runtime/pbc_rt.c:417
- function `rt_str_from_u32(uint32_t v)` — runtime/pbc_rt.c:418
- function `rt_str_from_i64(int64_t v)` — runtime/pbc_rt.c:419
- function `rt_str_from_single(long double v)` — Both take a long double, for the reason rt_print_single does: the value arrives at the x87's own — runtime/pbc_rt.c:423
- function `rt_str_from_double(long double v)` — runtime/pbc_rt.c:424
- function `rt_str_from_ext(long double v)` — runtime/pbc_rt.c:425
- function `rt_str_mkbyt(int16_t v)` — runtime/pbc_rt.c:429
- function `rt_str_mki(int16_t v)` — runtime/pbc_rt.c:430
- function `rt_str_mkl(int32_t v)` — runtime/pbc_rt.c:431
- function `rt_str_mkdwd(int32_t v)` — runtime/pbc_rt.c:432
- function `rt_str_mks(float v)` — runtime/pbc_rt.c:433
- function `rt_str_mkd(double v)` — runtime/pbc_rt.c:434
- function `rt_cv(void *s, void *out, int32_t n)` — runtime/pbc_rt.c:436
- function `rt_str_cvi(void *s)` — runtime/pbc_rt.c:442
- function `rt_str_cvbyt(void *s)` — runtime/pbc_rt.c:443
- function `rt_str_cvwrd(void *s)` — runtime/pbc_rt.c:444
- function `rt_str_cvl(void *s)` — runtime/pbc_rt.c:445
- function `rt_str_cvdwd(void *s)` — runtime/pbc_rt.c:446
- function `rt_str_cvs(void *s)` — runtime/pbc_rt.c:447
- function `rt_str_cvd(void *s)` — runtime/pbc_rt.c:448
- function `rt_str_cve(void *s)` — runtime/pbc_rt.c:449
- function `rt_out(const char *bytes, int32_t len)` — runtime/pbc_rt.c:456
- function `rt_out_num(const char *text)` — PB gives every numeric a sign slot in front and a trailing space behind. — runtime/pbc_rt.c:465
- function `rt_out_int(long long v)` — runtime/pbc_rt.c:477
- function `rt_print_str(void *bytes, int32_t len)` — runtime/pbc_rt.c:483
- function `rt_print_strvar(void *s)` — runtime/pbc_rt.c:484
- function `rt_print_nl(void)` — runtime/pbc_rt.c:485
- function `rt_print_i8(int8_t v)` — runtime/pbc_rt.c:487
- function `rt_print_u8(uint8_t v)` — runtime/pbc_rt.c:488
- function `rt_print_i16(int16_t v)` — runtime/pbc_rt.c:489
- function `rt_print_u16(uint16_t v)` — runtime/pbc_rt.c:490
- function `rt_print_i32(int32_t v)` — runtime/pbc_rt.c:491
- function `rt_print_u32(uint32_t v)` — runtime/pbc_rt.c:492
- function `rt_print_i64(int64_t v)` — runtime/pbc_rt.c:493
- function `rt_print_single(long double v)` — runtime/pbc_rt.c:495
- function `rt_print_double(long double v)` — runtime/pbc_rt.c:496
- function `rt_print_ext(long double v)` — runtime/pbc_rt.c:497
- function `rt_print_comma(void)` — The PRINT comma separator: advance to the next 14-column zone. Sitting exactly on a boundary — runtime/pbc_rt.c:502
- function `rt_csrlin(void)` — CSRLIN, and whether the standard handles are a console. The DOS runtime asks the BIOS and DOS; — runtime/pbc_rt.c:511
- function `rt_consin(void)` — runtime/pbc_rt.c:512
- function `rt_consout(void)` — runtime/pbc_rt.c:513
- function `rt_defseg_reset(void)` — runtime/pbc_rt.c:518
- function `rt_print_tab(int32_t column)` — runtime/pbc_rt.c:520
- function `rt_print_spc(int32_t count)` — runtime/pbc_rt.c:526
- function `rt_print_zone(void)` — PB's comma separator advances to the next 14-column print zone. — runtime/pbc_rt.c:531
- function `rt_getfield(char *buf, size_t cap, int wholeLine)` — PB reads one comma-separated field per INPUT variable; a LINE INPUT takes the — runtime/pbc_rt.c:541
- function `rt_input_num(void)` — runtime/pbc_rt.c:565
- function `rt_input_prompt(void *bytes, int32_t len)` — runtime/pbc_rt.c:571
- function `rt_input_i8(void)` — runtime/pbc_rt.c:573
- function `rt_input_u8(void)` — runtime/pbc_rt.c:574
- function `rt_input_i16(void)` — runtime/pbc_rt.c:575
- function `rt_input_u16(void)` — runtime/pbc_rt.c:576
- function `rt_input_i32(void)` — runtime/pbc_rt.c:577
- function `rt_input_u32(void)` — runtime/pbc_rt.c:578
- function `rt_input_i64(void)` — runtime/pbc_rt.c:579
- function `rt_input_single(void)` — runtime/pbc_rt.c:580
- function `rt_input_double(void)` — runtime/pbc_rt.c:581
- function `rt_input_ext(void)` — runtime/pbc_rt.c:582
- function `rt_input_str(void)` — runtime/pbc_rt.c:584
- function `rt_input_line(void)` — runtime/pbc_rt.c:590
- macro `RT_FILES` — runtime/pbc_rt.c:608
- function `rt_file_of(int32_t n)` — runtime/pbc_rt.c:613
- function `rt_file_open(int32_t n, void *name, int32_t mode, int32_t reclen)` — runtime/pbc_rt.c:619
- function `rt_file_close(int32_t n)` — runtime/pbc_rt.c:648
- function `rt_file_close_all(void)` — runtime/pbc_rt.c:656
- function `rt_freefile(void)` — runtime/pbc_rt.c:662
- function `rt_eof(int16_t n)` — PB's EOF is TRUE only once the last byte has been read, so it peeks rather than trusting feof, — runtime/pbc_rt.c:673
- function `rt_kill(void *name)` — runtime/pbc_rt.c:682
- function `rt_fout(int32_t n, const char *bytes, int32_t len)` — runtime/pbc_rt.c:694
- function `rt_fout_int(int32_t n, long long v)` — runtime/pbc_rt.c:703
- function `rt_fprint_str(int32_t n, void *bytes, int32_t len)` — runtime/pbc_rt.c:712
- function `rt_fprint_strvar(int32_t n, void *s)` — runtime/pbc_rt.c:713
- function `rt_fprint_nl(int32_t n)` — runtime/pbc_rt.c:714
- function `rt_fprint_comma(int32_t n)` — runtime/pbc_rt.c:715
- function `rt_fprint_i8(int32_t n, int8_t v)` — runtime/pbc_rt.c:721
- function `rt_fprint_u8(int32_t n, uint8_t v)` — runtime/pbc_rt.c:722
- function `rt_fprint_i16(int32_t n, int16_t v)` — runtime/pbc_rt.c:723
- function `rt_fprint_u16(int32_t n, uint16_t v)` — runtime/pbc_rt.c:724
- function `rt_fprint_i32(int32_t n, int32_t v)` — runtime/pbc_rt.c:725
- function `rt_fprint_u32(int32_t n, uint32_t v)` — runtime/pbc_rt.c:726
- function `rt_fprint_i64(int32_t n, int64_t v)` — runtime/pbc_rt.c:727
- function `rt_fprint_single(int32_t n, long double v)` — runtime/pbc_rt.c:728
- function `rt_fprint_double(int32_t n, long double v)` — runtime/pbc_rt.c:729
- function `rt_file_seek_record(int32_t n, int32_t record, int32_t size)` — GET #n, rec, var / PUT #n, rec, var - one fixed-size value at a record position. The record — runtime/pbc_rt.c:734
- function `rt_file_put(int32_t n, int32_t record, void *value, int32_t size)` — runtime/pbc_rt.c:740
- function `rt_file_get(int32_t n, int32_t record, void *value, int32_t size)` — runtime/pbc_rt.c:745
- function `rt_file_length(int32_t n)` — runtime/pbc_rt.c:754
- function `rt_file_pos(int32_t n)` — runtime/pbc_rt.c:763
- function `rt_file_seek(int32_t n, int32_t position)` — SEEK #n, p. Only the sequential modes are open here, so this is the BINARY reading: a 0-based — runtime/pbc_rt.c:768
- function `rt_fput_str(int32_t n, void *s)` — PUT$ / GET$ - raw bytes, no terminator and no record structure. A GET$ that reaches end of file — runtime/pbc_rt.c:775
- function `rt_fget_str(int32_t n, int32_t count)` — runtime/pbc_rt.c:780
- function `rt_finput_line(int32_t n)` — LINE INPUT #n: the rest of the line, without its terminator. — runtime/pbc_rt.c:789
- function `rt_arr_alloc(int32_t bytes)` — The allocation family speaks BYTES, because the element size of an array is a compile-time — runtime/pbc_rt.c:806
- function `rt_arr_alloc_ptr(int32_t count)` — runtime/pbc_rt.c:813
- function `rt_arr_realloc(void *p, int32_t oldBytes, int32_t newBytes)` — REDIM PRESERVE. Allocate-copy-free rather than realloc(): PB's grown tail reads as ZERO, and — runtime/pbc_rt.c:820
- function `rt_arr_realloc_ptr(void *p, int32_t oldCount, int32_t newCount)` — runtime/pbc_rt.c:830
- function `rt_arr_free(void *p, int32_t bytes)` — The byte count is what a bump allocator needs to give a block back; a malloc/free runtime has no — runtime/pbc_rt.c:837
- function `rt_arr_free_ptr(void *p, int32_t count)` — runtime/pbc_rt.c:838
- function `rt_mem_copy(void *dst, void *src, int32_t n)` — runtime/pbc_rt.c:840
- function `rt_mem_compare(void *a, void *b, int32_t n)` — runtime/pbc_rt.c:842
- function `rt_error(int32_t code)` — A BASIC run-time error. ON ERROR is not modelled by the C emitter (docs/BACKENDS.md), so there is — runtime/pbc_rt.c:850
- function `rt_unreachable(void)` — runtime/pbc_rt.c:855
- function `main(void)` — runtime/pbc_rt.c:860

### pbc_rt.h  `C, 200 lines`
> ==========================================================================
- macro `PBC_RT_H` — runtime/pbc_rt.h:15
- type `len` — A string handle. The IR treats it as an opaque pointer, so its shape is — runtime/pbc_rt.h:25

## scripts/

### diff-one.sh  `Shell, 95 lines`
> diff-one.sh [dialect] - compile one battery with the genuine PB
- function `winpath` — scripts/diff-one.sh:34
- function `run_dosbox()` — scripts/diff-one.sh:36

### expand-szdd.ps1  `PowerShell, 84 lines`
> expand-szdd.ps1 - decompress MS "SZ " (old SZDD / install-media) LZSS files.
- function `Expand-One` — scripts/expand-szdd.ps1:22
- function `Map-Name([string]$name)` — map a compressed name's trailing '$' back to the conventional last character — scripts/expand-szdd.ps1:53

### expand-szdd.py  `Python, 122 lines`
> expand-szdd.py - decompress MS "SZ " (old SZDD / install-media) LZSS files.
- const `_WINDOW` — scripts/expand-szdd.py:32
- const `_START` — scripts/expand-szdd.py:33
- const `_MAGIC` — scripts/expand-szdd.py:35
- function `expand(data)` — The expanded bytes, or None when this is not an old-SZDD file. — scripts/expand-szdd.py:38
- function `expanded_name(name)` — A compressed member's name with its truncated last character restored. — scripts/expand-szdd.py:76
- function `main(argv)` — scripts/expand-szdd.py:92

### gen-decompilation.sh  `Shell, 204 lines`
> Regenerates the decompilation reference inside docs/PB36.md (between the BEGIN/END GENERATED
- function `run()` — scripts/gen-decompilation.sh:15
- function `winpath()` — scripts/gen-decompilation.sh:34
- function `run_dosbox()` — scripts/gen-decompilation.sh:35
- function `mkconf()` — scripts/gen-decompilation.sh:38
- function `run_dosbox_retry()` — Decides the round-trip status of one example: SAME (recompiles + identical output), DIFFERS — scripts/gen-decompilation.sh:45
- function `roundtrip_status` — scripts/gen-decompilation.sh:51
- function `meta` — scripts/gen-decompilation.sh:67
- function `body()` — the program body without the @-metadata header comments — scripts/gen-decompilation.sh:70
- function `emit_section` — scripts/gen-decompilation.sh:71

### pack-toolchains.sh  `Shell, 87 lines`
> =============================================================================
- function `warn_wrong_build()` — Warn about executables that cannot serve as a DOS oracle. This is where pds70 and — scripts/pack-toolchains.sh:44

### roundtrip-check.sh  `Shell, 52 lines`
> Host-side round-trip gate (no DOSBox): for every program, emit-basic under its
- function `run()` — scripts/roundtrip-check.sh:17
- function `check()` — scripts/roundtrip-check.sh:20

### run-diff-tests.sh  `Shell, 373 lines`
> =============================================================================
- function `run_dosbox` — scripts/run-diff-tests.sh:87
- function `winpath` — scripts/run-diff-tests.sh:106
- function `u16_at` — scripts/run-diff-tests.sh:119
- function `u8_at()` — scripts/run-diff-tests.sh:121
- function `exe_blocker()` — Why one staged oracle executable cannot run, or nothing when it can. Two cases — scripts/run-diff-tests.sh:131
- function `conf_exes()` — Oracle executables a command template names, resolved from the D: mount. — scripts/run-diff-tests.sh:164
- function `battery_blocker()` — Why a whole battery cannot run here, or nothing when it can. — scripts/run-diff-tests.sh:174
- function `run_battery` — scripts/run-diff-tests.sh:200

### run-dos-tests.ps1  `PowerShell, 135 lines`
> =============================================================================
- (no top-level symbols found)

### run-dos-tests.sh  `Shell, 160 lines`
> =============================================================================
- function `is_unit()` — $COMPILE UNIT sources are prerequisites, not programs: compile them FIRST — scripts/run-dos-tests.sh:53
- function `norm()` — scripts/run-dos-tests.sh:129

### run-syntax-oracle-tests.sh  `Shell, 180 lines`
> Compare the exhaustive statement-form accept/reject matrix with genuine DOS compilers.
- function `run_dosbox` — scripts/run-syntax-oracle-tests.sh:35
- function `winpath` — scripts/run-syntax-oracle-tests.sh:50
- function `selected` — scripts/run-syntax-oracle-tests.sh:52

### run-vendor-corpus.sh  `Shell, 47 lines`
> =============================================================================
- (no top-level symbols found)

### stage-pds.sh  `Shell, 102 lines`
> =============================================================================
- function `harvest_from_dir` — scripts/stage-pds.sh:49
- function `harvest_from_image` — scripts/stage-pds.sh:60

## scripts/lib/

### dosbox.sh  `Shell, 147 lines`
> Locating a DOSBox and starting it in a way that works headless.
- function `_dosbox_starts_cleanly()` — Whether the emulator RAN A PROGRAM, established by making it write a file. — scripts/lib/dosbox.sh:66
- function `dosbox_detect_prefix()` — How to start the emulator here, established by trying the ways in cost order. — scripts/lib/dosbox.sh:86
- function `dosbox_kill()` — Stop a launched emulator AND everything it started. — scripts/lib/dosbox.sh:129
- function `dosbox_conf_path()` — An absolute config path, since the emulator may not share our working directory. — scripts/lib/dosbox.sh:141

### pds_layout.py  `Python, 93 lines`
> Lay expanded PDS 7.x media out as the BC7/BIN + BC7/LIB tree the oracle wants.
- const `TOOLS` — scripts/lib/pds_layout.py:15
- function `kind(path)` — Whether this executable runs under DOS, decided by its DOS stub. — scripts/lib/pds_layout.py:18
- function `main(argv)` — scripts/lib/pds_layout.py:53

