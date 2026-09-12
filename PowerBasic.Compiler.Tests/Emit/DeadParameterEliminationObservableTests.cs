using System.Text;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Emit;

/// <summary>End-to-end observable check for the O0069 call-shape cloning path.</summary>
[TestFixture]
public sealed class DeadParameterEliminationObservableTests {

  [Test]
  public void CallShape_GivenADominantLiteralClone_ThenRenderedProgramStillPrintsTheSame() {
    var source = new StringBuilder();
    for (var value = 1; value <= 24; ++value)
      source.AppendLine($"PRINT Pick%(1, {value})");
    source.AppendLine("PRINT Pick%(2, 50)");
    source.AppendLine("END");
    source.AppendLine("FUNCTION Pick%(BYVAL mode%, BYVAL value%)");
    source.AppendLine("  Pick% = mode% + value%");
    source.AppendLine("END FUNCTION");

    var program = source.ToString();
    var expected = Run(program);
    var module = IrLowering.TryLowerModule(Bind(program), out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    module!.OwnsProcedureAbi = true;                 // this fixture renders and runs the WHOLE module
    foreach (var function in module!.Functions)
      if (!function.IsDeclaration)
        Mem2Reg.Run(function);

    Assert.That(DeadParameterElimination.Run(module), Is.GreaterThan(0));
    Assert.That(module.Functions.Any(function => function.Name.Contains("$o0069$shape", StringComparison.Ordinal)), Is.True,
      "the test must actually exercise the cloning half of O0069");

    var got = Run(IrBasicWriter.Write(module));
    Assert.That(got, Is.EqualTo(expected));
  }

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static string Run(string source) {
    var codeGenerator = new CodeGenerator(Bind(source)) { Optimize = false };
    var image = codeGenerator.EmitExecutable();
    Assert.That(codeGenerator.Errors, Is.Empty, string.Join("; ", codeGenerator.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }
}
