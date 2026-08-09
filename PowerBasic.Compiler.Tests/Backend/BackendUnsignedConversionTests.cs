using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// A float converted to an UNSIGNED integer through the x86-16 back end.
///
/// The x87 stores only SIGNED integers, so the staging cell has to be one size larger than the
/// destination: a WORD's 65535 does not fit a signed word but fits a signed dword, and a DWORD's
/// 4294967295 needs the qword store. The bits that come back are the value either way - the sign of
/// the cell is a statement about the cell, not about them.
///
/// Which is why the values here sit at the TOP of each range. A staging cell one size too small
/// does not fault; it stores the x87's "integer indefinite" or a truncated value, and only the
/// values above the signed maximum can tell. 200 in a byte, 40000 in a word and 3000000000 in a
/// dword all pass with the wrong width; 255, 65535 and 4294967295 do not.
/// </summary>
[TestFixture]
public sealed class BackendUnsignedConversionTests {

  private static string Run(string body, bool routed) {
    var source = body + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = true, UseExperimentalBackend = routed };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  private static void Agrees(string body) =>
    Assert.That(Run(body, routed: true), Is.EqualTo(Run(body, routed: false)));

  [Test]
  public void Byte_GivenAFloatAcrossItsRange_ThenTheRoutedPathAgreesWithTheDirectOne() => Agrees("""
    DIM b AS BYTE, d AS DOUBLE
    DIM v(1 TO 4) AS DOUBLE
    v(1) = 0 : v(2) = 1.5 : v(3) = 200.4 : v(4) = 255
    FOR i% = 1 TO 4
      d = v(i%)
      b = d
      PRINT b;
    NEXT i%
    PRINT
    """);

  [Test]
  public void Word_GivenAFloatAboveTheSignedMaximum_ThenTheRoutedPathAgreesWithTheDirectOne() => Agrees("""
    DIM w AS WORD, d AS DOUBLE
    DIM v(1 TO 4) AS DOUBLE
    v(1) = 0 : v(2) = 32767 : v(3) = 40000.4 : v(4) = 65535
    FOR i% = 1 TO 4
      d = v(i%)
      w = d
      PRINT w;
    NEXT i%
    PRINT
    """);

  [Test]
  public void Dword_GivenAFloatAboveTheSignedMaximum_ThenTheRoutedPathAgreesWithTheDirectOne() => Agrees("""
    DIM u AS DWORD, d AS DOUBLE
    DIM v(1 TO 4) AS DOUBLE
    v(1) = 0 : v(2) = 2147483647 : v(3) = 3000000000# : v(4) = 4294967295#
    FOR i% = 1 TO 4
      d = v(i%)
      u = d
      PRINT u;
    NEXT i%
    PRINT
    """);

  /// <summary>A SINGLE source as well as a DOUBLE one - the staging width is about the destination.</summary>
  [Test]
  public void Dword_GivenASingleSource_ThenTheRoutedPathAgreesWithTheDirectOne() => Agrees("""
    DIM u AS DWORD, s AS SINGLE
    s = 70000.5
    u = s
    PRINT u
    """);
}
