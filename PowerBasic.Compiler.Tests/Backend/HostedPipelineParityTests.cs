using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The hosted pipeline gives x86-32 and x64 the passes the native one gives x86-16 and the 6502:
/// SELECT CASE re-formed as a switch (for every hosted writer), and on the portable runtime the
/// constant-needle INSTR searches and constant PRINT rendering. The C writer, whose runtime has no
/// such searches, keeps the generic routine.
/// </summary>
[TestFixture]
public sealed class HostedPipelineParityTests {

  private static IrModule Compile(string source, IrBackendTarget target) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var compiled = IrBackendModule.TryCompile(model, new IrBackendOptions {
      Target = target,
      Optimize = true,
      PortableRuntimeHeapBytes = target is IrBackendTarget.X86_32 or IrBackendTarget.X64 ? 1 << 16 : null,
    }, out var declined);
    Assert.That(compiled, Is.Not.Null, declined);
    return compiled!.Module;
  }

  private static IEnumerable<string> Callees(IrModule module, string function)
    => module.FindFunction(function)!.AllInstructions.OfType<IrCall>().Select(call => call.Callee).OfType<IrFunction>().Select(callee => callee.Name);

  private const string SelectProgram = """
    INPUT k%
    SELECT CASE k%
      CASE 1: PRINT "one"
      CASE 2: PRINT "two"
      CASE 3: PRINT "three"
      CASE 4: PRINT "four"
      CASE ELSE: PRINT "many"
    END SELECT
    """;

  [TestCase(IrBackendTarget.X64)]
  [TestCase(IrBackendTarget.X86_32)]
  [TestCase(IrBackendTarget.C)]
  [TestCase(IrBackendTarget.Llvm)]
  public void Compile_GivenASelectCase_ThenEveryHostedTargetSeesOneSwitch(IrBackendTarget target) {
    var module = Compile(SelectProgram, target);

    Assert.That(module.FindFunction("main")!.AllInstructions.OfType<IrSwitch>().Count(), Is.EqualTo(1));
  }

  [Test]
  public void Compile_GivenAConstantNeedle_ThenThePortableRuntimeSearchIsCalled() {
    var native = Compile("INPUT s$\nPRINT INSTR(s$, \"needle\")\n", IrBackendTarget.X64);
    var c = Compile("INPUT s$\nPRINT INSTR(s$, \"needle\")\n", IrBackendTarget.C);

    Assert.Multiple(() => {
      Assert.That(Callees(native, "main"), Has.Some.StartsWith("rt_instr_"));
      Assert.That(Callees(native, "main"), Has.None.EqualTo("rt_str_instr"));
      Assert.That(Callees(c, "main"), Has.Some.EqualTo("rt_str_instr"), "pbc_rt.h has no constant-needle search");
    });
  }

  [Test]
  public void Compile_GivenAConstantNumericPrint_ThenX64PrintsTheRenderedText() {
    var module = Compile("PRINT 6 * 7\n", IrBackendTarget.X64);
    var print = module.FindFunction("main")!.AllInstructions.OfType<IrCall>()
      .Single(call => call.Callee is IrFunction { Name: "rt_print_str" });

    Assert.That(((IrGlobalVariable)print.Args.First()).Bytes, Is.EqualTo(" 42 "u8.ToArray()),
      "the number printer is gone and its text is a literal");
  }
}
