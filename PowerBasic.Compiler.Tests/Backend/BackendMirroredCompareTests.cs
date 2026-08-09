using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// A comparison written with the CONSTANT on the left, through the x86-16 back end.
///
/// <c>CMP</c> wants a register on the left, so the selector swaps the operands and mirrors the
/// predicate: <c>5 &gt; x</c> is asked as <c>x &lt; 5</c>. The mirror is not the negation - <c>Slt</c>
/// becomes <c>Sgt</c> and not <c>Sge</c> - and getting that wrong does not crash or refuse. It
/// silently takes the other branch, and only at the boundary, where <c>&lt;</c> and <c>&lt;=</c>
/// disagree about exactly one value.
///
/// So every relation is exercised at three points - below, ON, and above the constant - because the
/// equal case is the only one that separates a correct mirror from a negated one, and both signed
/// and unsigned, because they map to different condition codes.
/// </summary>
[TestFixture]
public sealed class BackendMirroredCompareTests {

  private static string Run(string body, bool routed) {
    var source = body + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = true, UseExperimentalBackend = routed };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  /// <summary>Signed INTEGER, every relation, with the constant on the left.</summary>
  [TestCase("<")]
  [TestCase("<=")]
  [TestCase(">")]
  [TestCase(">=")]
  [TestCase("=")]
  [TestCase("<>")]
  public void Compare_GivenAConstantOnTheLeft_ThenTheRoutedPathBranchesTheSameWay(string relation) {
    var source = $"""
      FOR i% = 4 TO 6
        IF 5 {relation} i% THEN
          PRINT "T";
        ELSE
          PRINT "F";
        END IF
      NEXT i%
      PRINT
      """;
    Assert.That(Run(source, routed: true), Is.EqualTo(Run(source, routed: false)));
  }

  /// <summary>The unsigned relations map to different condition codes, so they mirror separately.</summary>
  [TestCase("<")]
  [TestCase("<=")]
  [TestCase(">")]
  [TestCase(">=")]
  public void Compare_GivenAnUnsignedConstantOnTheLeft_ThenTheRoutedPathBranchesTheSameWay(string relation) {
    var source = $"""
      DIM w AS WORD
      FOR i% = 4 TO 6
        w = i%
        IF 5 {relation} w THEN
          PRINT "T";
        ELSE
          PRINT "F";
        END IF
      NEXT i%
      PRINT
      """;
    Assert.That(Run(source, routed: true), Is.EqualTo(Run(source, routed: false)));
  }

  /// <summary>
  /// The answer itself, not just agreement between the two paths - so a mirror that is wrong in
  /// BOTH cannot pass by agreeing with itself.
  /// </summary>
  [TestCase("<", "FFT")]
  [TestCase("<=", "FTT")]
  [TestCase(">", "TFF")]
  [TestCase(">=", "TTF")]
  [TestCase("=", "FTF")]
  [TestCase("<>", "TFT")]
  public void Compare_GivenAConstantOnTheLeft_ThenTheAnswerIsTheArithmeticOne(string relation, string expected) {
    var source = $"""
      FOR i% = 4 TO 6
        IF 5 {relation} i% THEN
          PRINT "T";
        ELSE
          PRINT "F";
        END IF
      NEXT i%
      PRINT
      """;
    Assert.That(Run(source, routed: true), Is.EqualTo(expected));
  }
}
