using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// PRINT of an unsigned DWORD. There is no unsigned 32-bit printer in the runtime - rt_print_i32 would
/// render 4294967295 as -1 - so the value is staged in the frame as a zero-extended QWORD and FILDed
/// into the 64-bit printer, where the zeroed high half makes it positive. That is the four MOVs and the
/// FILD the direct emitter writes for exactly this case.
/// </summary>
[TestFixture]
public sealed class BackendUnsignedPrintTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static string Run(string source, bool routed) {
    var cg = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = routed };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  /// <summary>Without this, the value cases could pass by falling back to the direct emitter.</summary>
  [Test]
  public void Print_GivenADword_ThenTheFunctionActuallyRoutes() {
    var module = IrLowering.TryLowerModule(Bind("""
      DIM d AS DWORD
      d = 4294967295
      PRINT d
      """), out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    IrPassManager.Standard().RunOnModule(module!);
    foreach (var f in module!.Functions)
      if (!f.IsDeclaration)
        IntegerRecovery.Run(f);
    IrPassManager.Standard().RunOnModule(module);

    var main = module.Functions.First(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));
    var m = InstructionSelector.TrySelect(main, out var reason);
    Assert.That(m, Is.Not.Null, $"selection declined: {reason}");
    Assert.That(m!.AllInstructions.Any(i => i.Opcode == MOpcode.Fild), "the DWORD is FILDed as a qword");
    Assert.That(LinearScanAllocator.Allocate(m), Is.Not.Null, "and it allocates, so the function routes");
  }

  // the boundary is 2^31: below it a signed and an unsigned reading agree, at or above they do not
  [TestCase("0")]
  [TestCase("1")]
  [TestCase("2147483647")]
  [TestCase("2147483648")]
  [TestCase("4294967295")]
  [TestCase("65536")]
  public void Print_GivenADword_ThenItPrintsItsFullUnsignedRange(string value) {
    var source = $"""
      DIM d AS DWORD
      d = {value}
      PRINT d
      """;

    Assert.That(Run(source, routed: true), Is.EqualTo(Run(source, routed: false)), value);
  }

  [Test]
  public void Print_GivenADwordToAFile_ThenBothPathsAgree() {
    const string source = """
      DIM d AS DWORD
      OPEN "R.TXT" FOR OUTPUT AS #1
      d = 4294967295
      PRINT #1, d
      CLOSE #1
      PRINT "done"
      """;

    Assert.That(Run(source, routed: true), Is.EqualTo(Run(source, routed: false)));
  }
}
