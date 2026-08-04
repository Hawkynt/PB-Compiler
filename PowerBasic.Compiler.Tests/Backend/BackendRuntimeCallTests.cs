using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The runtime-label bridge: a back-end-compiled function calling the DOS runtime.
///
/// The two sides describe the same routines differently. The IR lowering declares them C-style -
/// <c>rt_print_str(ptr, i32)</c> - because the same IR feeds the C and LLVM back ends; the DOS runtime
/// the direct emitter calls is register-based (<c>SI</c> = address, <c>CX</c> = length, nothing pushed).
/// <see cref="RuntimeAbi"/> is the explicit per-routine mapping, and these are the tests that hold it to
/// the shape the direct emitter actually uses - because a wrong entry miscompiles silently.
/// </summary>
[TestFixture]
public sealed class BackendRuntimeCallTests {

  private const string _printingFunction = """
    FUNCTION Announce%
      PRINT "HI"
      Announce% = 7
    END FUNCTION

    PRINT Announce%
    """;

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static IrModule Optimized(string source) {
    var module = IrLowering.TryLowerModule(Bind(source));
    Assert.That(module, Is.Not.Null, "outside the IR lowering's subset");
    IrPassManager.Standard().RunOnModule(module!);
    foreach (var f in module!.Functions)
      if (!f.IsDeclaration)
        IntegerRecovery.Run(f);
    IrPassManager.Standard().RunOnModule(module);
    return module;
  }

  private static MFunction Select(string source, string function) {
    var fn = Optimized(source).Functions.First(f => f.Name.Equals(function, StringComparison.OrdinalIgnoreCase));
    var m = InstructionSelector.TrySelect(fn, out var reason);
    Assert.That(m, Is.Not.Null, $"{function} declined: {reason}");
    return m!;
  }

  [Test]
  public void Select_GivenPrintOfALiteral_ThenLoadsSiAndCxAndCallsTheRuntimeLabel() {
    var m = Select(_printingFunction, "Announce");

    var call = m.AllInstructions.First(i => i.Opcode == MOpcode.Call);
    Assert.That(((MOperand.LabelRef)call.Operands[0]).Name, Is.EqualTo("rt_print_str"));
    var argument = m.AllInstructions
      .TakeWhile(i => i != call)
      .Where(i => i.Opcode == MOpcode.Mov && i.Operands[0] is MOperand.Register { Reg.IsVirtual: false })
      .ToDictionary(i => ((MOperand.Register)i.Operands[0]).Reg.Physical, i => i.Operands[1]);
    Assert.That(argument.Keys, Is.EquivalentTo(new[] { Reg.SI, Reg.CX }), "SI = address, CX = length");
    Assert.That(argument[Reg.SI], Is.InstanceOf<MOperand.DataOffset>(), "the ADDRESS of the literal, not its bytes");
    Assert.That(((MOperand.Immediate)argument[Reg.CX]).Value, Is.EqualTo(2), """length of "HI" """);
  }

  [Test]
  public void Select_GivenARuntimeCall_ThenItClobbersTheCallerSavedFile() {
    var m = Select(_printingFunction, "Announce");

    var call = m.AllInstructions.First(i => i.Opcode == MOpcode.Call);

    // conservative on purpose: the print routines do preserve everything they touch, but a clobber
    // claim one register too small miscompiles a value that is never recomputed
    Assert.That(call.Clobbers, Is.EquivalentTo(new[] { Reg.AX, Reg.BX, Reg.CX, Reg.DX, Reg.SI, Reg.DI }));
  }

  [Test]
  public void Select_GivenTheNewlineAfterThePrint_ThenCallsRtPrintNl() {
    var m = Select(_printingFunction, "Announce");

    var callees = m.AllInstructions
      .Where(i => i.Opcode == MOpcode.Call)
      .Select(i => ((MOperand.LabelRef)i.Operands[0]).Name)
      .ToList();

    Assert.That(callees, Is.EqualTo(new[] { "rt_print_str", "rt_print_nl" }),
      "PRINT of one item is the text then the newline, in that order");
  }

  [Test]
  public void Select_GivenARoutineOutsideTheTable_ThenDeclinesNamingIt() {
    // a string-valued expression goes through rt_str_const, which returns a HANDLE - a representation
    // the back end has no model for yet, so it must decline rather than guess a convention
    var module = Optimized("""
      FUNCTION Length%
        DIM s AS STRING
        s = "abc"
        Length% = LEN(s)
      END FUNCTION

      PRINT Length%
      """);
    var fn = module.Functions.First(f => f.Name.Equals("Length", StringComparison.OrdinalIgnoreCase));

    InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(reason, Does.Contain("not in the runtime ABI table"));
  }

  [Test]
  public void Emit_GivenARoutedPrintingFunction_ThenTheImageAssemblesAndDiffersFromTheDirectPath() {
    var direct = new CodeGenerator(Bind(_printingFunction)) { Optimize = true, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(_printingFunction)) { Optimize = true, UseExperimentalBackend = true };

    var directImage = direct.EmitExecutable();
    var routedImage = routed.EmitExecutable();

    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    // an unresolved runtime label or literal would have thrown while the fixups resolved
    Assert.That(routedImage, Is.Not.Empty);
    Assert.That(routedImage, Is.Not.EqualTo(directImage), "the back end did not take the printing function");
  }

  [Test]
  public void Emit_GivenARoutedPrintingFunction_ThenTheRuntimeTrimmerStillKeepsThePrintSections() {
    // the trimmer seeds from the labels emitted code references, and a back-end CALL references the
    // very same named label - so a section only the routed function needs is not trimmed away
    var routed = new CodeGenerator(Bind(_printingFunction)) { Optimize = true, UseExperimentalBackend = true };

    var image = routed.EmitExecutable();

    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(image, Is.Not.Empty);
  }
}
