using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// BIT(value, n) on the IR path - one shift and a mask where the direct emitter writes a loop.
///
/// <para>
/// The interesting cases are the ones where a plausible lowering differs from the emitter without
/// looking wrong: a bit at or above the sign position, which an ARITHMETIC shift would smear; and a
/// count past the width, where the emitter's loop lands on zero and <c>lshr</c> has no defined
/// answer at all.
/// </para>
/// </summary>
[TestFixture]
public sealed class BitLoweringTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static string Run(string source, bool routed) {
    var cg = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = routed };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim();
  }

  private static readonly (string Name, string Source)[] _programs = [
    ("literal bits of a literal", """
      PRINT BIT(5, 0); BIT(5, 1); BIT(5, 2); BIT(5, 3)
      END
      """),
    ("through variables", """
      DIM v AS LONG
      DIM i AS INTEGER
      v = 5
      FOR i = 0 TO 3
        PRINT BIT(v, i);
      NEXT i
      PRINT
      END
      """),
    // the sign bit and the one below it: an arithmetic shift would answer 1 for every bit above 30
    ("high bits of a negative long", """
      DIM v AS LONG
      v = -1
      PRINT BIT(v, 0); BIT(v, 30); BIT(v, 31)
      END
      """),
    ("a single high bit", """
      DIM v AS LONG
      v = &H80000000
      PRINT BIT(v, 31); BIT(v, 30); BIT(v, 0)
      END
      """),
    ("an integer widens before the shift", """
      DIM v AS INTEGER
      v = -1
      PRINT BIT(v, 15); BIT(v, 16); BIT(v, 31)
      END
      """),
  ];

  [Test]
  public void Lowering_GivenBit_ThenTheModuleLowers() {
    foreach (var (name, source) in _programs) {
      var module = IrLowering.TryLowerModule(Bind(source), out var why);
      Assert.That(module, Is.Not.Null, $"'{name}' declined: {why}");
    }
  }

  [Test]
  public void Routed_GivenBit_ThenItAnswersAsTheDirectEmitterDoes() {
    foreach (var (name, source) in _programs)
      Assert.That(Run(source, routed: true), Is.EqualTo(Run(source, routed: false)), $"program '{name}'");
  }

  /// <summary>
  /// Stated outright, because "the two paths agree" would be satisfied by both being wrong: bit zero
  /// of 5 is set, bit one is not, bit two is.
  /// </summary>
  [Test]
  public void Bit_GivenAKnownValue_ThenItAnswersTheBit()
    => Assert.That(Run("PRINT BIT(5, 0); BIT(5, 1); BIT(5, 2)\nEND", routed: true),
        Is.EqualTo(Run("PRINT 1; 0; 1\nEND", routed: true)));

  /// <summary>
  /// A count past the width answers zero, matching where the emitter's shift loop lands. Without the
  /// guard this is the case where a defined-looking lowering and an undefined shift part company.
  /// </summary>
  [Test]
  public void Bit_GivenACountPastTheWidth_ThenItIsZero() {
    const string source = """
      DIM v AS LONG
      DIM n AS INTEGER
      v = -1
      n = 32
      PRINT BIT(v, n); BIT(v, 40)
      END
      """;
    Assert.That(Run(source, routed: true), Is.EqualTo(Run("PRINT 0; 0\nEND", routed: true)));
    Assert.That(Run(source, routed: true), Is.EqualTo(Run(source, routed: false)));
  }

  /// <summary>
  /// The edges of the literal guard, which is decided in the lowering rather than by constant folding:
  /// bit 0 and bit 31 are inside, 32 is one past, and a NEGATIVE literal is out of range too - the
  /// comparison is unsigned, so -1 is a huge count and not the last bit.
  /// </summary>
  [Test]
  public void Bit_GivenALiteralAtTheEdgesOfTheRange_ThenBothPathsAnswerTheSame() {
    const string source = """
      DIM v AS LONG
      v = -1
      PRINT BIT(v, -1); BIT(v, 0); BIT(v, 31); BIT(v, 32)
      END
      """;
    Assert.That(Run(source, routed: true), Is.EqualTo(Run("PRINT 0; 1; 1; 0\nEND", routed: true)));
    Assert.That(Run(source, routed: true), Is.EqualTo(Run(source, routed: false)));
  }

  /// <summary>
  /// A LITERAL bit number leaves no guard in the IR to fold away - which is the whole point, because
  /// the optimizer does not run over every function. LOWLEVEL.BAS holds inline assembly, so its module
  /// body is skipped whole, and <c>BIT(s, 2)</c> used to reach the selector as a 32-bit shift whose
  /// count was an <c>IrCast</c> of a 2 rather than a 2. It kept the program off the back end.
  ///
  /// <para>Asserted on the UNOPTIMIZED module, since a pass folding it later would prove nothing.</para>
  /// </summary>
  [Test]
  public void Bit_GivenALiteralBitNumber_ThenTheLoweringEmitsNeitherGuardNorCast() {
    var module = IrLowering.TryLowerModule(Bind("""
      DIM s AS WORD
      s = 12
      ! MOV CX, 1
      PRINT BIT(s, 2)
      END
      """), out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");

    var main = module!.Functions.First(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));
    var instructions = main.Blocks.SelectMany(b => b.Instructions).ToList();
    Assert.That(instructions.OfType<IrSelect>(), Is.Empty, "the range guard is decided at lowering");
    Assert.That(instructions.OfType<IrCmp>(), Is.Empty);

    var shift = instructions.OfType<IrBinary>().Single(b => b.Op == IrBinaryOp.LShr);
    Assert.That(shift.Rhs, Is.InstanceOf<IrConstantInt>(), "the count the back end has to read is a number");
    Assert.That(((IrConstantInt)shift.Rhs).Value, Is.EqualTo(2));
  }
}
