using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class InlineAsmPopcntPolicyTests {
  [TestCase("386")]
  [TestCase("SSE4.2")]
  public void DwordPopcnt_GivenCpuWithoutIndependentFeatureAndErrorPolicy_ThenRejectsNativeEncoding(string cpu) {
    var generator = Compile($"$CPU {cpu}\n$ISA POPCNT ERROR\n! POPCNT EAX, EBX\nEND\n");

    Assert.That(generator.Errors.Any(error =>
      error.Message.Contains("forbids emulation", StringComparison.OrdinalIgnoreCase)), Is.True,
      string.Join("; ", generator.Errors));
  }

  private static CodeGenerator Compile(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "popcnt-policy.bas", Dialect.Pb36), "popcnt-policy.bas", Dialect.Pb36);
    var model = PowerBasic.Compiler.Semantics.Binder.Bind(unit, Dialect.Pb36);
    if (model.Errors.Count != 0)
      throw new AssertionException("bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = false };
    _ = generator.EmitExecutable();
    return generator;
  }
}
