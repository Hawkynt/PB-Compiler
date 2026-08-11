using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// REG and INTERRUPT on the IR path.
///
/// <para>
/// They are one facility and had to arrive together: REG fills the buffer, INTERRUPT executes with it,
/// and either alone is unusable. Both reach the SAME <c>rt_regs</c> buffer the direct emitter indexes
/// inline, which is what lets a routed REG set up a directly-emitted INTERRUPT and the other way
/// round - the two paths share the state, not just the answer.
/// </para>
/// <para>
/// The scaling moved into the routines because the IR has no way to name a scaled index into a
/// runtime table; that is a change of where the shift happens, not of what it means.
/// </para>
/// </summary>
[TestFixture]
public sealed class RegInterruptLoweringTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static string Run(string source, bool routed) {
    var cg = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = routed };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output;
  }

  private static readonly (string Name, string Source)[] _programs = [
    // PB numbers the buffer 0=FLAGS 1=AX 2=BX 3=CX 4=DX 5=SI 6=DI 7=BP 8=DS 9=ES
    ("write then read back", """
      REG 1, 1234
      REG 4, 5678
      PRINT REG(1); REG(4)
      END
      """),
    // DOS get-version (AH=30h) - the interrupt INTREG.BAS uses, and one the test CPU implements.
    // Its whole observable effect is what comes BACK in the buffer, which is the half of the
    // contract a write-only test would miss.
    ("interrupt returns through the buffer", """
      REG 1, &H3000
      CALL INTERRUPT &H21
      DIM ver AS INTEGER
      ver = REG(1) AND &HFF
      PRINT ver
      END
      """),
    ("indices through a variable", """
      DIM i AS INTEGER
      FOR i = 1 TO 6
        REG i, i * 100
      NEXT i
      FOR i = 1 TO 6
        PRINT REG(i);
      NEXT i
      PRINT
      END
      """),
  ];

  [Test]
  public void Lowering_GivenRegAndInterrupt_ThenTheModuleLowers() {
    foreach (var (name, source) in _programs) {
      var module = IrLowering.TryLowerModule(Bind(source), out var why);
      Assert.That(module, Is.Not.Null, $"'{name}' declined: {why}");
    }
  }

  [Test]
  public void Routed_GivenRegAndInterrupt_ThenItBehavesAsTheDirectEmitterDoes() {
    foreach (var (name, source) in _programs)
      Assert.That(Run(source, routed: true), Is.EqualTo(Run(source, routed: false)), $"program '{name}'");
  }

  /// <summary>
  /// Stated rather than only compared: the buffer holds what was put in it, at the index it was put
  /// at. A routine that scaled the index differently from the direct emitter's <c>SHL BX, 1</c> would
  /// still round-trip - it would just be reading and writing the wrong cell consistently, which only
  /// an INTERRUPT would notice.
  /// </summary>
  [Test]
  public void Reg_GivenAWriteAndARead_ThenTheValueIsAtThatIndex()
    => Assert.That(Run("REG 1, 1234\nREG 4, 5678\nPRINT REG(1); REG(4)\nEND", routed: true),
        Is.EqualTo(Run("PRINT 1234; 5678\nEND", routed: true)));

  /// <summary>
  /// And the index really is the one the interrupt uses. DOS get-version answers in AX, which is
  /// REG 1; were the scaling off by one, the call would take its function number from the wrong cell
  /// and answer nothing recognisable.
  /// </summary>
  [Test]
  public void Interrupt_GivenDosGetVersion_ThenTheAnswerComesBackInRegOne() {
    const string source = """
      REG 1, &H3000
      CALL INTERRUPT &H21
      PRINT REG(1) AND &HFF
      END
      """;
    Assert.That(Run(source, routed: true), Is.EqualTo(Run(source, routed: false)));
    Assert.That(Run(source, routed: true).Trim(), Is.Not.EqualTo("0"),
      "a version of zero means the buffer never came back");
  }
}
