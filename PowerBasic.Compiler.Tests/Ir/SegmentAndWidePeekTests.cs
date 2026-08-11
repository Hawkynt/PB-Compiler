using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// The rest of the PEEK/POKE family, and the segment queries beside it.
///
/// <para>
/// <c>VARSEG</c>, <c>CODESEG</c> and <c>STRSEG</c> ask for the segment half of an address. Two of them
/// are segment REGISTERS, which the IR has no way to name, so each became a one-instruction routine;
/// <c>STRSEG</c> is a runtime cell the IR could already read. All three share one property worth
/// stating: the operand is never evaluated, because asking where something lives is not reading it.
/// </para>
/// </summary>
[TestFixture]
public sealed class SegmentAndWidePeekTests {

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
    // POKE writes bytes; PEEKI reads the word they form, so the two halves and the whole must agree
    ("word read over two poked bytes", """
      DEF SEG = &HA000
      POKE 100, &H34
      POKE 101, &H12
      PRINT PEEKI(100)
      PRINT PEEK(100); PEEK(101)
      DEF SEG
      END
      """),
    ("dword read over four poked bytes", """
      DEF SEG = &HA000
      POKE 200, &H78
      POKE 201, &H56
      POKE 202, &H34
      POKE 203, &H12
      PRINT PEEKL(200)
      DEF SEG
      END
      """),
    ("segments", """
      DIM v AS INTEGER
      DIM s AS STRING
      v = 1
      s = "x"
      PRINT VARSEG(v); STRSEG(s)
      END
      """),
    // asking where something lives must not read it, and must not disturb it
    ("the operand is not evaluated", """
      DIM v AS INTEGER
      v = 7
      PRINT VARSEG(v)
      PRINT v
      END
      """),
  ];

  [Test]
  public void Lowering_GivenTheWiderFamily_ThenTheModuleLowers() {
    foreach (var (name, source) in _programs) {
      var module = IrLowering.TryLowerModule(Bind(source), out var why);
      Assert.That(module, Is.Not.Null, $"'{name}' declined: {why}");
    }
  }

  [Test]
  public void Routed_GivenTheWiderFamily_ThenItBehavesAsTheDirectEmitterDoes() {
    foreach (var (name, source) in _programs)
      Assert.That(Run(source, routed: true), Is.EqualTo(Run(source, routed: false)), $"program '{name}'");
  }

  /// <summary>
  /// The byte order, stated rather than only compared: bytes &amp;H34 then &amp;H12 read back as the
  /// word &amp;H1234. A reader that assembled the halves the other way round would agree with itself
  /// and disagree with everything else on the machine.
  /// </summary>
  [Test]
  public void PeekI_GivenTwoPokedBytes_ThenItReadsThemLittleEndian()
    => Assert.That(Run("""
      DEF SEG = &HA000
      POKE 300, &H34
      POKE 301, &H12
      PRINT PEEKI(300)
      DEF SEG
      END
      """, routed: true).Trim(), Is.EqualTo("4660"));   // &H1234
}
