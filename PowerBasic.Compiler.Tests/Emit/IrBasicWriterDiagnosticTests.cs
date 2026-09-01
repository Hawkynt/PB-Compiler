using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Emit;

[TestFixture]
public sealed class IrBasicWriterDiagnosticTests {
  [Test]
  public void Diff23_ReportRenderedFileMismatch() {
    var repoRoot = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));
    var path = Path.Combine(repoRoot, "tests", "diff", "DIFF23.BAS");
    var source = File.ReadAllText(path);

    var originalModel = Bind(source, "DIFF23.BAS", Dialect.Pb36);
    Assert.That(originalModel.Errors, Is.Empty);
    var module = IrLowering.TryLowerModule(originalModel)!;
    IrPassManager.Standard().RunOnModule(module);
    var rendered = IrBasicWriter.Write(module);

    var original = Run(source, Dialect.Pb36);
    var plain = Run(rendered, Dialect.Pb35);
    Assert.Fail($"original file={Escape(original)}\nrendered file={Escape(plain)}\n--- rendered BASIC ---\n{rendered}");
  }

  private static SemanticModel Bind(string source, string name, Dialect dialect)
    => Binder.Bind(Parser.Parse(Lexer.Tokenize(source, name, dialect), name, dialect), dialect);

  private static string? Run(string source, Dialect dialect) {
    var model = Bind(source, "DIFF23.BAS", dialect);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = false };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "emit: " + string.Join("; ", generator.Errors));
    return Cpu8086.Run(image).FileContent("RESULT.TXT");
  }

  private static string Escape(string? value)
    => value is null ? "<null>" : string.Concat(value.Select(c => c switch {
      '\r' => "\\r",
      '\n' => "\\n",
      _ when char.IsControl(c) => $"\\x{(int)c:X2}",
      _ => c.ToString(),
    }));
}
