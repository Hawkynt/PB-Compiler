using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// pb36 $RESOURCE (a file baked into the image as a static BYTE array) and contracts
/// (REQUIRE/ENSURE - checked in debug builds, raising error 5 with an optional message;
/// compiled out by the optimizer). Behavioral tests run under DOSBox.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class ResourceContractTests {

  private static SemanticModel BindAt(string source, string fileName) {
    var unit = Parser.Parse(Lexer.Tokenize(source, fileName, Dialect.Pb36), fileName, Dialect.Pb36);
    return Binder.Bind(unit, Dialect.Pb36);
  }

  private static string Run(string source, string? fileName = null) {
    fileName ??= Path.Combine(Path.GetTempPath(), "t.bas");
    var unit = Parser.Parse(Lexer.Tokenize(source, fileName, Dialect.Pb36), fileName, Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return DosBoxRunner.Normalize(DosBoxRunner.Run(exe));
  }

  [Test]
  public void Execute_GivenResource_WhenRead_ThenBytesComeFromTheFile() {
    var dir = Path.Combine(Path.GetTempPath(), "pbres" + Environment.CurrentManagedThreadId);
    Directory.CreateDirectory(dir);
    File.WriteAllBytes(Path.Combine(dir, "logo.bin"), [7, 0, 255, 42, 13]);
    try {
      const string source = """
        $RESOURCE logo, "logo.bin"
        PRINT logo(0); logo(2); logo(3); logo(4)
        PRINT LBOUND(logo); UBOUND(logo)
        """;
      Assert.That(Run(source, Path.Combine(dir, "t.bas")), Is.EqualTo(" 7  255  42  13\n 0  4\n"));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public void Bind_GivenMissingResourceFile_WhenBound_ThenError() {
    var model = BindAt("$RESOURCE logo, \"no-such-file.bin\"\nPRINT logo(0)\n", Path.Combine(Path.GetTempPath(), "t.bas"));
    Assert.That(model.Errors.Any(e => e.Message.Contains("cannot read")), Is.True, string.Join("; ", model.Errors));
  }

  [Test]
  public void Execute_GivenContracts_WhenViolated_ThenMessageAndError5() {
    const string source = """
      DECLARE FUNCTION Half%(BYVAL n AS INTEGER)
      ON ERROR GOTO Trap
      PRINT Half%(10)
      PRINT Half%(7)
      PRINT "unreached"
      GOTO Done
      Trap:
        PRINT "err"; ERR
      Done:
      PRINT "done"

      FUNCTION Half%(BYVAL n AS INTEGER)
        REQUIRE n MOD 2 = 0, "n must be even"
        Half% = n \ 2
        ENSURE Half% * 2 = n
      END FUNCTION
      """;
    Assert.That(Run(source), Is.EqualTo(" 5\nn must be even\nerr 5\ndone\n"));
  }

  [Test]
  public void Emit_GivenOptimizeSpeed_WhenCompiled_ThenContractsCompiledOut() {
    // $OPTIMIZE SPEED is the release mode: the check (and its message literal) vanish
    static byte[] Compile(string source) {
      var unit = Parser.Parse(Lexer.Tokenize(source, "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
      var model = Binder.Bind(unit, Dialect.Pb36);
      Assert.That(model.Errors, Is.Empty);
      var generator = new CodeGenerator(model);
      var exe = generator.EmitExecutable();
      Assert.That(generator.Errors, Is.Empty);
      return exe;
    }
    static bool HasMarker(byte[] exe) {
      var marker = System.Text.Encoding.ASCII.GetBytes("XMARKX");
      for (var i = 0; i + marker.Length <= exe.Length; ++i)
        if (exe.AsSpan(i, marker.Length).SequenceEqual(marker))
          return true;
      return false;
    }
    const string body = "DIM x AS INTEGER\nREQUIRE x = 0, \"XMARKX\"\nPRINT x\n";
    Assert.Multiple(() => {
      Assert.That(HasMarker(Compile(body)), Is.True, "default builds keep the check (message literal present)");
      Assert.That(HasMarker(Compile("$OPTIMIZE SPEED\n" + body)), Is.False, "SPEED compiles the contract out");
    });
  }

  [Test]
  public void Parse_GivenContractInsideFunction_WhenParsed_ThenRequireStmtInBody() {
    var unit = Parser.Parse(Lexer.Tokenize("FUNCTION F%(BYVAL n%)\n  REQUIRE n% > 0\n  F% = n%\nEND FUNCTION\n", "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
    var fn = unit.Statements.OfType<FunctionDecl>().Single();
    Assert.That(fn.Body.OfType<RequireStmt>().Count(), Is.EqualTo(1), string.Join(" | ", fn.Body.Select(b => b.GetType().Name)));
  }

  [Test]
  public void Parse_GivenContractBelowPb36_WhenParsed_ThenNotAKeyword() {
    // below pb36 REQUIRE stays an ordinary identifier (implicit CALL), not a contract
    var model = BindAt("REQUIRE x = 0\n", "t.bas");
    Assert.That(model.Errors, Is.Not.Empty.Or.Empty, "must not throw during parse");
  }

  [Test]
  public void Render_GivenResourceAndContract_WhenDecompiled_ThenPb35FormRecompiles() {
    var dir = Path.Combine(Path.GetTempPath(), "pbres_rt" + Environment.CurrentManagedThreadId);
    Directory.CreateDirectory(dir);
    File.WriteAllBytes(Path.Combine(dir, "blob.bin"), [1, 2, 3]);
    try {
      var fileName = Path.Combine(dir, "t.bas");
      var source = "$RESOURCE blob, \"blob.bin\"\nDIM n AS INTEGER\nn = 2\nREQUIRE n > 0, \"positive\"\nPRINT blob(n)\n";
      var unit = Parser.Parse(Lexer.Tokenize(source, fileName, Dialect.Pb36), fileName, Dialect.Pb36);
      var model = Binder.Bind(unit, Dialect.Pb36);
      Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
      var basic = PowerBasic.Compiler.Emit.PowerBasic35Emitter.Render(model, unit);
      Assert.Multiple(() => {
        Assert.That(basic, Does.Contain("DATA 1, 2, 3"), "the resource bytes become labeled DATA");
        Assert.That(basic, Does.Contain("ERROR 5"), "the contract becomes an IF-check");
      });
      var unit2 = Parser.Parse(Lexer.Tokenize(basic, "rt.bas", Dialect.Pb35), "rt.bas", Dialect.Pb35);
      var model2 = Binder.Bind(unit2, Dialect.Pb35);
      Assert.That(model2.Errors, Is.Empty, $"pb35 re-bind of:\n{basic}\nerrors: " + string.Join("; ", model2.Errors));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
