using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// A back-end-compiled function that <b>calls</b> another one. Until calls were selectable the
/// in-house x86-16 back end could only take leaf functions, which is most of why so little of the
/// corpus routed through it (see <see cref="BackendCoverageTests"/>).
///
/// Two things have to hold for a call to be sound here. The ABI must match on both sides: routed
/// definitions use BASIC/PASCAL, while an <see cref="IrCall"/> may select another declared stack ABI.
/// An optimized routed function may only call procedures that are themselves routed,
/// since those are exactly the ones excluded from the register-parameter conversion. With optimization
/// off, a directly emitted BASIC/PASCAL callee keeps the same stack ABI and is therefore compatible.
/// Nothing may sit in a register across any call: this ABI preserves no register at all, so the
/// allocator has to refuse such a function rather than let a value be destroyed.
/// </summary>
[TestFixture]
public sealed class BackendCallRoutingTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  /// <summary>The back end's own pipeline, as <c>CodeGenerator.BackendProcs</c> runs it.</summary>
  private static IrModule Optimized(SemanticModel model) {
    var module = IrLowering.TryLowerModule(model);
    Assert.That(module, Is.Not.Null, "the program is outside the IR lowering's subset");
    IrPassManager.Standard().RunOnModule(module!);
    foreach (var f in module!.Functions)
      if (!f.IsDeclaration)
        IntegerRecovery.Run(f);
    IrPassManager.Standard().RunOnModule(module);
    return module;
  }

  private static IrFunction FunctionNamed(IrModule module, string name)
    => module.Functions.First(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

  [Test]
  public void Select_GivenCallToDefinedFunction_ThenSelectsWithPushesAndCall() {
    var module = Optimized(Bind("""
      FUNCTION Twice%(BYVAL v%)
        Twice% = v% * 2
      END FUNCTION

      FUNCTION Quad%(BYVAL v%)
        Quad% = Twice%(Twice%(v%))
      END FUNCTION

      PRINT Quad%(3)
      """));

    var selected = InstructionSelector.TrySelect(FunctionNamed(module, "Quad"), out var reason);

    Assert.That(selected, Is.Not.Null, $"Quad declined: {reason}");
    var opcodes = selected!.AllInstructions.Select(i => i.Opcode).ToList();
    Assert.That(opcodes, Does.Contain(MOpcode.Call), "the call must survive selection");
    Assert.That(opcodes.Count(o => o == MOpcode.Push), Is.EqualTo(2), "one argument pushed per call");
    Assert.That(selected.AllInstructions.First(i => i.Opcode == MOpcode.Call).Clobbers,
      Is.SupersetOf(new[] { Reg.AX, Reg.SI, Reg.DI }),
      "this ABI preserves nothing, so the call must declare it destroys the register file");
  }

  [Test]
  public void Select_GivenNearIndirectCall_ThenStagesTargetInBxAndEmitsFfSlash2() {
    var target = new IrArgument(IrType.Ptr, 0, "target");
    var value = new IrArgument(IrType.I16, 1, "value");
    var fn = new IrFunction("Invoke", IrType.I16, [target, value]);
    var entry = fn.CreateBlock("entry");
    var call = entry.Append(new IrCall(IrType.I16, target, [value], IrCallConvention.Basic));
    entry.Append(new IrRet(call));

    var selected = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(selected, Is.Not.Null, $"declined: {reason}");
    var indirect = selected!.AllInstructions.Single(i => i.Opcode == MOpcode.Call);
    Assert.That(indirect.Operands[0], Is.EqualTo(new MOperand.Register(MReg.Physical_(Reg.BX))),
      "the computed near target is pinned in BX for CALL r/m16");
    var allocation = LinearScanAllocator.Allocate(selected, out var allocationReason);
    Assert.That(allocation, Is.Not.Null, $"allocation declined: {allocationReason}");

    var asm = new Assembler();
    MachineEmitter.EmitFunction(asm, selected, allocation!, [6, 4], 4);
    var bytes = asm.ToArray();

    Assert.That(Contains(bytes, 0xFF, 0xD3), Is.True,
      "CALL BX must encode as the 8086 near-indirect FF /2 form");
  }

  [TestCase(IrCallConvention.Fastcall, 3)]
  [TestCase(IrCallConvention.Watcall, 4)]
  public void Select_GivenNearIndirectRegisterCall_ThenKeepsTargetOutsideArgumentPrefix(
      IrCallConvention convention, int argumentCount) {
    var target = new IrArgument(IrType.Ptr, 0, "target");
    var arguments = Enumerable.Range(0, argumentCount)
      .Select(i => new IrArgument(IrType.I16, i + 1, $"value{i}"))
      .ToList();
    var parameters = new List<IrArgument> { target };
    parameters.AddRange(arguments);
    var fn = new IrFunction("Invoke", IrType.I16, parameters);
    var entry = fn.CreateBlock("entry");
    var call = entry.Append(new IrCall(IrType.I16, target, arguments.Cast<IrValue>().ToList(), convention));
    entry.Append(new IrRet(call));

    var selected = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(selected, Is.Not.Null, $"declined: {reason}");
    MachineScheduler.Schedule(selected!);
    var indirect = selected!.AllInstructions.Single(i => i.Opcode == MOpcode.Call);
    var argumentRegisters = indirect.Operands.Skip(1).Cast<MOperand.Register>()
      .Select(operand => operand.Reg.Physical).ToList();
    Assert.Multiple(() => {
      Assert.That(indirect.Operands[0], Is.EqualTo(new MOperand.Register(MReg.Physical_(Reg.SI))),
        "BX carries argument three, so the computed call target needs a disjoint register");
      Assert.That(argumentRegisters,
        Is.EqualTo(X86CallAbi.For(convention).ArgumentRegisters.Take(argumentCount)));
      Assert.That(indirect.Effect.ReadRegs, Is.EqualTo(Enumerable.Range(0, argumentCount + 1)),
        "the call must consume its target and every physical argument register");
    });
    var allocation = LinearScanAllocator.Allocate(selected, out var allocationReason);
    Assert.That(allocation, Is.Not.Null, $"allocation declined: {allocationReason}");

    var asm = new Assembler();
    var parameterOffsets = Enumerable.Range(0, parameters.Count)
      .Select(i => 4 + (parameters.Count - i - 1) * 2).ToArray();
    MachineEmitter.EmitFunction(asm, selected, allocation!, parameterOffsets, parameters.Count * 2);

    Assert.That(Contains(asm.ToArray(), 0xFF, 0xD6), Is.True,
      "CALL SI must encode as the 8086 near-indirect FF /2 form");
  }

  [TestCase(IrCallConvention.Cdecl, true)]
  [TestCase(IrCallConvention.Stdcall, false)]
  public void Select_GivenRightToLeftStackConvention_ThenReversesArgumentGroupsAndUsesDeclaredCleanup(
      IrCallConvention convention, bool callerCleans) {
    // Given a foreign two-argument declaration whose operand values make stack order observable.
    var module = new IrModule("t");
    var callee = module.AddFunction(new IrFunction("foreign", IrType.Void,
      [new IrArgument(IrType.I16, 0), new IrArgument(IrType.I16, 1)]));
    var fn = module.AddFunction(new IrFunction("main", IrType.Void));
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    entry.Append(new IrCall(IrType.Void, callee,
      [new IrConstantInt(IrType.I16, 11), new IrConstantInt(IrType.I16, 22)], convention));
    entry.Append(new IrRet());

    // When the call is selected, the last source argument is pushed first.
    var selected = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(selected, Is.Not.Null, $"declined: {reason}");
    var instructions = selected!.AllInstructions.ToList();
    var pushes = instructions.Where(i => i.Opcode == MOpcode.Push).ToList();
    Assert.Multiple(() => {
      Assert.That(pushes, Has.Count.EqualTo(2));
      Assert.That(pushes[0].Operands[0], Is.EqualTo(new MOperand.Immediate(22)));
      Assert.That(pushes[1].Operands[0], Is.EqualTo(new MOperand.Immediate(11)));
      Assert.That(instructions.Any(i => i.Opcode == MOpcode.Add
        && i.Operands[0] is MOperand.Register { Reg.IsVirtual: false, Reg.Physical: Reg.SP }
        && i.Operands[1] is MOperand.Immediate { Value: 4 }), Is.EqualTo(callerCleans));
    });
  }

  [Test]
  public void Select_GivenCdeclWideArguments_ThenReversesGroupsWithoutReversingWords() {
    var module = new IrModule("t");
    var callee = module.AddFunction(new IrFunction("foreign", IrType.Void,
      [new IrArgument(IrType.I32, 0), new IrArgument(IrType.I32, 1)]));
    var fn = module.AddFunction(new IrFunction("main", IrType.Void));
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    entry.Append(new IrCall(IrType.Void, callee,
      [new IrConstantInt(IrType.I32, 0x11112222), new IrConstantInt(IrType.I32, 0x33334444)],
      IrCallConvention.Cdecl));
    entry.Append(new IrRet());

    var selected = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(selected, Is.Not.Null, $"declined: {reason}");
    var pushes = selected!.AllInstructions.Where(i => i.Opcode == MOpcode.Push)
      .Select(i => ((MOperand.Immediate)i.Operands[0]).Value)
      .ToList();
    Assert.That(pushes, Is.EqualTo(new long[] { 0x3333, 0x4444, 0x1111, 0x2222 }));
    Assert.That(selected.AllInstructions.Any(i => i.Opcode == MOpcode.Add
      && i.Operands[1] is MOperand.Immediate { Value: 8 }), Is.True);
  }

  [TestCase(IrCallConvention.Fastcall)]
  [TestCase(IrCallConvention.Watcall)]
  public void Select_GivenRegisterConvention_ThenStagesLeadingWordsAndPushesOverflowInDeclaredOrder(
      IrCallConvention convention) {
    var module = new IrModule("t");
    var callee = module.AddFunction(new IrFunction("foreign", IrType.Void,
      Enumerable.Range(0, 6).Select(i => new IrArgument(IrType.I16, i)).ToList()));
    var fn = module.AddFunction(new IrFunction("main", IrType.Void));
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    entry.Append(new IrCall(IrType.Void, callee,
      Enumerable.Range(1, 6).Select(i => (IrValue)new IrConstantInt(IrType.I16, i * 11)).ToList(),
      convention));
    entry.Append(new IrRet());

    var selected = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(selected, Is.Not.Null, $"declined: {reason}");
    var beforeCall = selected!.AllInstructions.TakeWhile(i => i.Opcode != MOpcode.Call).ToList();
    var stages = beforeCall.Where(i => i.Opcode == MOpcode.Mov).Select(i => (
      Register: ((MOperand.Register)i.Operands[0]).Reg.Physical,
      Value: ((MOperand.Immediate)i.Operands[1]).Value)).ToList();
    var pushes = beforeCall.Where(i => i.Opcode == MOpcode.Push)
      .Select(i => ((MOperand.Immediate)i.Operands[0]).Value).ToList();
    var expectedStages = convention == IrCallConvention.Fastcall
      ? new[] { (Reg.AX, 11L), (Reg.DX, 22L), (Reg.BX, 33L) }
      : [(Reg.AX, 11L), (Reg.DX, 22L), (Reg.BX, 33L), (Reg.CX, 44L)];
    var expectedPushes = convention == IrCallConvention.Fastcall
      ? new long[] { 44, 55, 66 }
      : [66, 55];
    var callInstruction = selected.AllInstructions.First(i => i.Opcode == MOpcode.Call);
    Assert.Multiple(() => {
      Assert.That(stages, Is.EqualTo(expectedStages));
      Assert.That(pushes, Is.EqualTo(expectedPushes));
      Assert.That(callInstruction.Effect.ReadRegs, Has.Count.EqualTo(expectedStages.Length),
        "the call must keep every staged physical register in flight until it consumes it");
      Assert.That(selected.AllInstructions.Any(i => i.Opcode == MOpcode.Add
        && i.Operands[0] is MOperand.Register { Reg.IsVirtual: false, Reg.Physical: Reg.SP }), Is.False,
        "both register conventions leave overflow cleanup to the callee");
    });
  }

  [TestCase(IrCallConvention.Fastcall)]
  [TestCase(IrCallConvention.Watcall)]
  public void Allocate_GivenRegisterConventionWithSixLiveArguments_ThenStagingRemainsAllocatable(
      IrCallConvention convention) {
    var parameters = Enumerable.Range(0, 6).Select(i => new IrArgument(IrType.I16, i)).ToList();
    var module = new IrModule("t");
    var callee = module.AddFunction(new IrFunction("foreign", IrType.Void,
      Enumerable.Range(0, 6).Select(i => new IrArgument(IrType.I16, i)).ToList()));
    var caller = module.AddFunction(new IrFunction("caller", IrType.Void, parameters));
    var entry = caller.AddBlock(new IrBasicBlock("entry"));
    entry.Append(new IrCall(IrType.Void, callee, parameters.Cast<IrValue>().ToList(), convention));
    entry.Append(new IrRet());

    var selected = InstructionSelector.TrySelect(caller, out var selectionReason);
    Assert.That(selected, Is.Not.Null, $"selection declined: {selectionReason}");
    MachineScheduler.Schedule(selected!);

    Assert.That(LinearScanAllocator.Allocate(selected!, out var allocationReason), Is.Not.Null,
      $"allocation declined: {allocationReason}");
  }

  [TestCase(IrCallConvention.Fastcall)]
  [TestCase(IrCallConvention.Watcall)]
  public void Select_GivenRegisterConventionWithWideArgument_ThenDeclinesExplicitly(
      IrCallConvention convention) {
    var module = new IrModule("t");
    var callee = module.AddFunction(new IrFunction("foreign", IrType.Void,
      [new IrArgument(IrType.I32, 0)]));
    var fn = module.AddFunction(new IrFunction("main", IrType.Void));
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    entry.Append(new IrCall(IrType.Void, callee, [new IrConstantInt(IrType.I32, 1)], convention));
    entry.Append(new IrRet());

    Assert.That(InstructionSelector.TrySelect(fn, out var reason), Is.Null);
    Assert.That(reason, Does.Contain("word arguments"));
  }

  [Test]
  public void Select_GivenSelfRecursion_ThenSelects() {
    // the call target is the function itself, so the ABI trivially agrees
    var module = Optimized(Bind("""
      FUNCTION Down%(BYVAL n%)
        IF n% <= 0 THEN
          Down% = 0
        ELSE
          Down% = Down%(n% - 1)
        END IF
      END FUNCTION

      PRINT Down%(4)
      """));

    Assert.That(InstructionSelector.TrySelect(FunctionNamed(module, "Down"), out var reason), Is.Not.Null,
      $"self-recursive function declined: {reason}");
  }

  /// <summary>
  /// An rt_* helper is a declaration: its label lives in the runtime, and the only thing that says
  /// where its arguments go is RuntimeAbi's table. Anything not in that table must DECLINE rather than
  /// be guessed at - a wrong register claim miscompiles silently.
  ///
  /// The callee is deliberately fictitious, for the reason recorded in BackendRuntimeCallTests: this
  /// was written against LEN, then HEX$, then STRING$, and each time the routine got listed and the
  /// test failed for the best possible reason. The rule is what is under test.
  /// </summary>
  [Test]
  public void Select_GivenAnUnlistedRuntimeCall_ThenDeclinesWithTheReason() {
    var module = new IrModule("t");
    var unknown = module.AddFunction(new IrFunction("rt_no_such_routine", IrType.Void, [new IrArgument(IrType.I16, 0)]));
    var fn = module.AddFunction(new IrFunction("main", IrType.Void));
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    entry.Append(new IrCall(IrType.Void, unknown, [new IrConstantInt(IrType.I16, 1)]));
    entry.Append(new IrRet());

    Assert.That(InstructionSelector.TrySelect(fn, out var reason), Is.Null);
    Assert.That(reason, Does.Contain("runtime declaration"));
  }

  /// <summary>
  /// The other half of the same rule: a routine that IS in the table selects. PRINT of a string
  /// variable is the one that moved - it maps to the runtime's StrPrint, and it was the single
  /// largest selection decline in the corpus census before it was listed.
  /// </summary>
  [Test]
  public void Select_GivenAListedRuntimeCall_ThenItSelects() {
    var module = Optimized(Bind("""
      DIM s AS STRING
      s = "x"
      PRINT s
      """));

    Assert.That(InstructionSelector.TrySelect(FunctionNamed(module, "main"), out var reason), Is.Not.Null,
      $"declined: {reason}");
  }

  [Test]
  public void Emit_GivenRoutedCall_ThenTheProgramLinksWithEveryLabelBound() {
    // the end-to-end check that matters for a CALL: procedure labels are minted in a different
    // registry than Assembler.Lbl, so a mis-bridged callee would leave an unbound label and the
    // image would not assemble at all
    var model = Bind("""
      FUNCTION Twice%(BYVAL v%)
        Twice% = v% * 2
      END FUNCTION

      FUNCTION Quad%(BYVAL v%)
        Quad% = Twice%(Twice%(v%))
      END FUNCTION

      PRINT Quad%(3)
      """);
    var generator = new CodeGenerator(model) { Optimize = true, UseExperimentalBackend = true };

    var image = generator.EmitExecutable();

    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(image, Is.Not.Empty);
  }

  [Test]
  public void Emit_GivenBackendOnAndOff_ThenBothCompileTheSameProgram() {
    const string source = """
      FUNCTION Twice%(BYVAL v%)
        Twice% = v% * 2
      END FUNCTION

      FUNCTION Quad%(BYVAL v%)
        Quad% = Twice%(Twice%(v%))
      END FUNCTION

      PRINT Quad%(3)
      """;

    var direct = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = true };
    var directImage = direct.EmitExecutable();
    var routedImage = routed.EmitExecutable();

    Assert.That(direct.Errors, Is.Empty);
    Assert.That(routed.Errors, Is.Empty);
    Assert.That(directImage, Is.Not.Empty);
    Assert.That(routedImage, Is.Not.Empty);
    // The two paths deliberately differ in the bytes they emit - the back end register-allocates and
    // schedules from SSA where the direct codegen is AX-serial. That difference is also the proof
    // that the routing really took the call-containing function: were it declining and falling back,
    // the two images would be identical. (Equality of OUTPUT is what the DOSBox battery verifies.)
    Assert.That(routedImage, Is.Not.EqualTo(directImage),
      "the back end did not compile anything - the call-containing function fell back to the direct codegen");
  }

  [Test]
  public void Execute_GivenUnoptimizedRoutedMainCallingRoutedByRefCallee_ThenStackAbiMatches() {
    const string source = """
      DECLARE SUB Touch(v%)
      DIM a AS INTEGER
      a = 7
      Touch a
      PRINT a
      END

      SUB Touch(v%)
        v% = v% + 1
      END SUB
      """;
    var direct = new CodeGenerator(Bind(source)) { Optimize = false, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = false, UseExperimentalBackend = true };

    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.Multiple(() => {
      Assert.That(routed.BackendRoutedNames, Does.Contain("main"));
      Assert.That(routed.BackendRoutedNames, Does.Contain("Touch"),
        "the near numeric BYREF callee must route with its caller");
      Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output));
      Assert.That(routedCpu.Output.Trim(), Is.EqualTo("8"));
    });
  }

  [Test]
  public void Execute_GivenSizeOptimizedRoutedMainCallingRecursiveByRefCallee_ThenStackAbiMatches() {
    const string source = """
      DECLARE SUB CountDown(v%)
      DIM a AS INTEGER
      a = 2
      CountDown a
      END

      SUB CountDown(v%)
        PRINT v%
        IF v% > 0 THEN CountDown v% - 1
      END SUB
      """;
    var direct = new CodeGenerator(Bind(source)) {
      Optimize = true,
      OptimizeSize = true,
      UseExperimentalBackend = false,
    };
    var routed = new CodeGenerator(Bind(source)) {
      Optimize = true,
      OptimizeSize = true,
      UseExperimentalBackend = true,
    };

    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.Multiple(() => {
      Assert.That(routed.BackendRoutedNames, Does.Contain("main"));
      Assert.That(routed.BackendRoutedNames, Does.Contain("CountDown"),
        "the recursive near numeric BYREF callee must route");
      Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output));
    });
  }

  [Test]
  public void Route_GivenUnoptimizedMainCallingDirectCalleeWithUnsupportedResultShape_ThenDeclinesTheCaller() {
    var generator = new CodeGenerator(Bind("""
      FUNCTION F(BYVAL a%) AS FIX
        F = a% / 2
      END FUNCTION
      PRINT F(3)
      END
      """)) {
      Optimize = false,
      UseExperimentalBackend = true,
    };

    var routed = generator.BackendRoutedNames.ToList();
    var declines = generator.BackendDeclines.ToList();

    Assert.Multiple(() => {
      Assert.That(routed, Does.Not.Contain("F"), "FIX results are not a routed return shape yet");
      Assert.That(routed, Does.Not.Contain("main"),
        "a routed caller must not consume a direct callee result shape it cannot transport");
      Assert.That(declines.Any(d => d.Name == "main" && d.Reason.Contains("calls 'F', which is not routed", StringComparison.Ordinal)),
        Is.True, string.Join(" | ", declines.Select(d => d.Name + ": " + d.Reason)));
    });
  }

  [Test]
  public void Route_GivenUnoptimizedMainCallingUnresolvedDeclaration_ThenDeclinesTheCaller() {
    var generator = new CodeGenerator(Bind("""
      DECLARE FUNCTION Imported%(BYVAL v%)
      PRINT Imported%(1)
      END
      """)) {
      Optimize = false,
      UseExperimentalBackend = true,
    };

    Assert.That(generator.BackendRoutedNames, Does.Not.Contain("main"),
      "a compatible calling convention cannot create a missing external definition");
  }

  [Test]
  public void Emit_GivenARoutedRecursiveProcedureUsingASuffixedSharedGlobal_ThenResolvesItsDataCell() {
    const string source = """
      DECLARE SUB Sum(BYVAL n%)
      total% = 0
      Sum 3
      PRINT total%
      END

      SUB Sum(BYVAL n%)
        SHARED total%
        total% = total% + n%
        IF n% > 0 THEN Sum n% - 1
      END SUB
      """;
    var direct = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = true };

    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(routed.BackendRoutedNames, Does.Contain("Sum"));
    Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output));
  }
  private static bool Contains(byte[] bytes, byte first, byte second) {
    for (var i = 0; i + 1 < bytes.Length; ++i)
      if (bytes[i] == first && bytes[i + 1] == second)
        return true;
    return false;
  }

}
