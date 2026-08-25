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
/// Two things have to hold for a call to be sound here. The ABI must match on both sides - the back
/// end emits the BASIC/PASCAL convention (arguments pushed left to right, callee cleans with
/// <c>RET n</c>). An optimized routed function may only call procedures that are themselves routed,
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
  public void Execute_GivenUnoptimizedRoutedMainCallingDirectBasicCallee_ThenStackAbiMatches() {
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
      Assert.That(routed.BackendRoutedNames, Does.Not.Contain("Touch"),
        "the BYREF callee stays on the direct emitter");
      Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output));
      Assert.That(routedCpu.Output.Trim(), Is.EqualTo("8"));
    });
  }

  [Test]
  public void Execute_GivenSizeOptimizedRoutedMainCallingDirectBasicCallee_ThenStackAbiMatches() {
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
      Assert.That(routed.BackendRoutedNames, Does.Not.Contain("CountDown"),
        "the recursive BYREF callee stays on the direct emitter");
      Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output));
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
}
