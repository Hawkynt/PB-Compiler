using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class InlineAsmCpuFloorTests {
  private static (byte[] Image, CodeGenerator Generator) Compile(string cpu, string assembly) {
    var source = $"$CPU {cpu}\n{assembly}\nPRINT \"OK\"\nEND\n";
    var tree = Parser.Parse(Lexer.Tokenize(source, "cpu-floor.bas", Dialect.Pb36),
      "cpu-floor.bas", Dialect.Pb36);
    var model = Binder.Bind(tree, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = false };
    return (generator.EmitExecutable(), generator);
  }

  [TestCase("8086", "! PUSHA", "PUSHA", "80186")]
  [TestCase("8086", "! POPA", "POPA", "80186")]
  [TestCase("8086", "! IMUL AX, 5", "IMUL", "80186")]
  [TestCase("8086", "! IMUL AX, BX, 5", "IMUL", "80186")]
  [TestCase("8086", "! IMUL AX, BX", "IMUL", "80386")]
  [TestCase("186", "! IMUL AX, BX", "IMUL", "80386")]
  public void Emit_GivenAnInstructionAboveTheSelectedCpu_ThenReportsItsRequiredCpu(
      string cpu, string assembly, string mnemonic, string requiredCpu) {
    var (image, generator) = Compile(cpu, assembly);

    Assert.Multiple(() => {
      Assert.That(image.Length, Is.Zero, "an invalid target must not yield an executable image");
      Assert.That(generator.Errors.Select(error => error.Message),
        Has.Some.Contains(mnemonic).And.Contains(requiredCpu));
    });
  }

  [TestCase("8086", "! IMUL BX")]
  [TestCase("186", "! PUSHA\n! POPA")]
  [TestCase("186", "! IMUL AX, 5")]
  [TestCase("186", "! IMUL AX, BX, 5")]
  [TestCase("80386", "! IMUL AX, BX")]
  [TestCase("8086", "! IMUL EAX, 5")]
  [TestCase("8086", "! IMUL EAX, EBX")]
  [TestCase("8086", "! IMUL EAX, EBX, 5")]
  public void Emit_GivenAnInstructionAtOrBelowTheSelectedCpu_ThenProducesAnImage(
      string cpu, string assembly) {
    var (image, generator) = Compile(cpu, assembly);

    Assert.Multiple(() => {
      Assert.That(generator.Errors, Is.Empty);
      Assert.That(image.Length, Is.GreaterThan(0));
    });
  }

  [Test]
  public void Run_GivenVirtualizedDwordImulOn8086_ThenUses8086InstructionsAndComputesTheProduct() {
    var (image, generator) = Compile("8086", """
      DIM result&
      ! MOV EBX, 7
      ! IMUL EAX, EBX, 5
      ! IMUL EAX, EBX
      ! IMUL EAX, 2
      ! MOV result&, EAX
      PRINT result&
      """);
    Assert.That(generator.Errors, Is.Empty);

    var cpu = Cpu8086.Run(image);
    Assert.Multiple(() => {
      Assert.That(cpu.Output, Does.Contain("490"));
      Assert.That(cpu.OperandSizePrefixes, Is.Zero);
    });
  }

}
