using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// PEEK and POKE on the IR path.
///
/// <para>
/// The direct emitter writes both inline - <c>MOV ES, [rt_defseg]</c> and a segment-overridden byte
/// access - and a segment override is not something the IR can say. They become runtime routines for
/// the same reason CSRLIN and CONSIN did, and reading the SAME <c>rt_defseg</c> cell the inline form
/// reads is what lets a program set DEF SEG in a directly-emitted statement and read it in a routed
/// one without noticing which is which.
/// </para>
/// </summary>
[TestFixture]
public sealed class PeekPokeLoweringTests {

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

  /// <summary>
  /// Programs shaped like the corpus one this unblocked (LOWLEVEL.BAS): DEF SEG names a segment and
  /// the pair reads and writes bytes in it. A000 is the VGA graphics window - present, writable, and
  /// not somewhere the runtime keeps anything, so the byte that comes back is the byte that went in.
  /// </summary>
  private static readonly (string Name, string Source)[] _programs = [
    ("round trip", """
      DEF SEG = &HA000
      POKE 100, 77
      PRINT PEEK(100)
      DEF SEG
      END
      """),
    ("segmented form", """
      POKE &HA000:200, 90
      PRINT PEEK(&HA000:200)
      DEF SEG
      END
      """),
    ("in a loop", """
      DIM i AS INTEGER
      DIM t AS INTEGER
      DEF SEG = &HA000
      FOR i = 0 TO 5
        POKE 300 + i, 48 + i
      NEXT i
      FOR i = 0 TO 5
        t = t + PEEK(300 + i)
      NEXT i
      DEF SEG
      PRINT t
      END
      """),
    ("segment outlives the statement", """
      DEF SEG = &HA000
      POKE 400, 12
      DEF SEG = &HB800
      POKE 400, 34
      DEF SEG = &HA000
      PRINT PEEK(400)
      DEF SEG
      END
      """),
  ];

  /// <summary>The IR must accept the program at all - before this, POKE took the whole program off the path.</summary>
  [Test]
  public void Lowering_GivenPeekAndPoke_ThenTheModuleLowers() {
    foreach (var (name, source) in _programs) {
      var module = IrLowering.TryLowerModule(Bind(source), out var why);
      Assert.That(module, Is.Not.Null, $"'{name}' declined: {why}");
    }
  }

  /// <summary>
  /// And the routed program must behave as the directly-emitted one does. This is the assertion that
  /// matters: a runtime routine that read a different segment, or reported the byte signed, would
  /// lower perfectly well and answer wrongly.
  /// </summary>
  [Test]
  public void Routed_GivenPeekAndPoke_ThenItBehavesAsTheDirectEmitterDoes() {
    foreach (var (name, source) in _programs)
      Assert.That(Run(source, routed: true), Is.EqualTo(Run(source, routed: false)), $"program '{name}'");
  }

  /// <summary>
  /// A byte above 127 comes back as a positive INTEGER. PB reports a PEEK unsigned, and reading it
  /// through a signed byte load would make 200 answer -56 - a difference no round trip would show,
  /// because poking it back writes the same low byte either way.
  /// </summary>
  [Test]
  public void Peek_GivenAByteAboveOneTwentySeven_ThenItIsReportedUnsigned() {
    const string source = """
      DEF SEG = &HA000
      POKE 500, 200
      PRINT PEEK(500)
      DEF SEG
      END
      """;
    Assert.That(Run(source, routed: true).Trim(), Is.EqualTo("200"));
    Assert.That(Run(source, routed: true), Is.EqualTo(Run(source, routed: false)));
  }
}
