using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class InlineAsm8086PushTests {
  private static (byte[] Image, CodeGenerator Generator) Compile(string cpu, string instruction) {
    var source = $"$CPU {cpu}\n{instruction}\nPRINT \"OK\"\nEND\n";
    var tree = Parser.Parse(Lexer.Tokenize(source, "push.bas", Dialect.Pb36), "push.bas", Dialect.Pb36);
    var model = Binder.Bind(tree, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = false };
    return (generator.EmitExecutable(), generator);
  }

  [TestCase(5)]
  [TestCase(128)]
  public void PushImmediate_GivenAn8086Target_ThenCompilationRejectsThe80186Instruction(int value) {
    var (image, generator) = Compile("8086", $"! PUSH {value}");

    Assert.Multiple(() => {
      Assert.That(image, Is.Empty, "the rejected program must not yield an executable image");
      Assert.That(generator.Errors.Any(error => error.Message.Contains("PUSH", StringComparison.Ordinal)
        && error.Message.Contains("80186", StringComparison.Ordinal)), Is.True,
        "an immediate PUSH must not silently emit opcode 6A or 68 on an 8086 target");
    });
  }

  [Test]
  public void PushImmediate_GivenAn80186Target_ThenTheProgramRuns() {
    var (image, generator) = Compile("186", "! PUSH 5\n! POP AX");

    Assert.Multiple(() => {
      Assert.That(generator.Errors, Is.Empty);
      Assert.That(Cpu8086.Run(image).Output, Does.Contain("OK"));
    });
  }

  [Test]
  public void PushRegister_GivenAn8086Target_ThenTheProgramRuns() {
    var (image, generator) = Compile("8086", "! PUSH AX\n! POP AX");

    Assert.Multiple(() => {
      Assert.That(generator.Errors, Is.Empty);
      Assert.That(Cpu8086.Run(image).Output, Does.Contain("OK"));
    });
  }
}
