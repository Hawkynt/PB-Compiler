using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// End-to-end and selector gates for BYTE/SBYTE integer-to-float staging. x87 FILD has no
/// byte source, so the value must first acquire a signed word representation.
/// </summary>
[TestFixture]
public sealed class BackendByteParameterRoutingTests {
  private const string _SOURCE = """
    FUNCTION Bump(BYVAL a AS BYTE) AS BYTE NOINLINE
      Bump = a + 1
    END FUNCTION
    DIM b AS BYTE
    b = 200
    PRINT Bump(b)
    """;

  private static SemanticModel Bind() {
    var unit = Parser.Parse(Lexer.Tokenize(_SOURCE, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Procedure_GivenUnsignedByteParameterAndResult_ThenRoutedExecutionMatchesDirect(bool optimize) {
    var routed = new CodeGenerator(Bind()) { Optimize = optimize, UseExperimentalBackend = true };
    var routedImage = routed.EmitExecutable();
    Assert.That(routed.Errors, Is.Empty, "routed: " + string.Join("; ", routed.Errors));
    Assert.Multiple(() => {
      Assert.That(routed.BackendRoutedNames, Does.Contain("Bump"),
        "the BYTE-taking function did not route: " + string.Join(" | ", routed.BackendDeclines.Select(d => d.Name + ": " + d.Reason)));
      Assert.That(routed.BackendRoutedNames, Does.Contain("main"), "the BYTE caller did not route");
    });

    var direct = new CodeGenerator(Bind()) { Optimize = optimize, UseExperimentalBackend = false };
    var directImage = direct.EmitExecutable();
    Assert.That(direct.Errors, Is.Empty, "direct: " + string.Join("; ", direct.Errors));
    var expected = Cpu8086.Run(directImage);
    var actual = Cpu8086.Run(routedImage);
    Assert.That((actual.Output, actual.ExitCode), Is.EqualTo((expected.Output, expected.ExitCode)));
  }

  [TestCase(false, IrCastOp.UIToFP)]
  [TestCase(true, IrCastOp.SIToFP)]
  public void Selector_GivenByteToFloat_ThenStagesAWordForFild(bool signed, IrCastOp op) {
    var sourceType = signed ? IrType.I8 : IrType.U8;
    var argument = new IrArgument(sourceType, 0, "value");
    var function = new IrFunction("ByteToFloat", IrType.F32, [argument]);
    var entry = function.CreateBlock("entry");
    var converted = entry.Append(new IrCast(op, argument, IrType.F32));
    entry.Append(new IrRet(converted));
    var selected = InstructionSelector.TrySelect(function, out var reason);
    Assert.That(selected, Is.Not.Null, reason);
    var instructions = selected!.AllInstructions.ToList();
    Assert.That(instructions.Any(instruction => instruction.Opcode == MOpcode.Fild
      && instruction.Operands.Single() is MOperand.StackSlot { Size: MRegSize.Word }), Is.True,
      "FILD must read a word-staged byte");
    Assert.That(instructions.Any(instruction => instruction.Opcode == MOpcode.Xor), Is.EqualTo(signed));
    Assert.That(instructions.Any(instruction => instruction.Opcode == MOpcode.Sub), Is.EqualTo(signed));
  }
}
