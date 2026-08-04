using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Exec;

/// <summary>
/// O0079 in its separated form: <c>q = n \ d</c> and a LATER <c>m = n MOD d</c> share the one divide.
/// The adjacent pair could reuse <c>DX</c> directly; once anything sits between them the remainder has
/// to be kept somewhere, so it goes to a frame slot at the divide and is loaded at the MOD.
///
/// Both halves are checked here, because either alone is worthless: the emitted code must really drop
/// the second <c>IDIV</c>, and the program must still print what BASIC prints - which is what the
/// interpreter is for. A wrong-but-fast divide is the failure mode this optimization invites.
/// </summary>
[TestFixture]
public sealed class SharedDivideTests {

  private static (string Output, int Divides) Compile(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var codegen = new CodeGenerator(model) { Optimize = true, UseExperimentalBackend = false };
    var image = codegen.EmitExecutable();
    Assert.That(codegen.Errors, Is.Empty, string.Join("; ", codegen.Errors));
    // F7 /7 is IDIV r/m16; the runtime has its own, so only the user-code area is counted by
    // comparing against the same program without the pairing opportunity
    var divides = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0xF7 && (image[i + 1] & 0x38) == 0x38)
        ++divides;
    return (Cpu8086.Run(image).Output, divides);
  }

  // the divisor comes from DATA so it is a genuine RUNTIME value - a foldable constant would be
  // strength-reduced into a multiply and there would be no divide to share in the first place
  private const string _separated = """
    DATA 5
    n% = 47
    READ d%
    q% = n% \ d%
    FOR i% = 1 TO 3
      PRINT i%;
    NEXT i%
    r% = n% MOD d%
    PRINT q%; r%
    """;

  private const string _twoDivides = """
    DATA 5
    n% = 47
    READ d%
    q% = n% \ d%
    FOR i% = 1 TO 3
      PRINT i%;
    NEXT i%
    d% = d% + 0
    r% = n% MOD d%
    PRINT q%; r%
    """;

  [Test]
  public void Emit_GivenALoopBetweenTheDivideAndTheMod_ThenOnlyOneDivideSurvives() {
    var shared = Compile(_separated);
    var separate = Compile(_twoDivides);

    // the second program writes d% in between, so its remainder cannot be the stashed one
    Assert.That(shared.Divides, Is.LessThan(separate.Divides),
      "the loop between the two statements should not force a second IDIV");
  }

  [Test]
  public void Run_GivenALoopBetweenTheDivideAndTheMod_ThenTheAnswersAreStillRight() {
    Assert.That(Compile(_separated).Output.Trim(), Is.EqualTo("1  2  3  9  2"),
      "the quotient is 9 and the remainder 2, whatever the emitter did with the divide");
  }

  [Test]
  public void Run_GivenAWriteToTheDivisorBetween_ThenTheModIsRecomputed() {
    // d% changes in between, so reusing the stash would answer the OLD remainder
    Assert.That(Compile(_twoDivides).Output.Trim(), Is.EqualTo("1  2  3  9  2"));
  }

  [Test]
  public void Run_GivenTheAdjacentPair_ThenItStillWorks() {
    Assert.That(Compile("""
      DATA 5
      n% = 47
      READ d%
      q% = n% \ d%
      r% = n% MOD d%
      PRINT q%; r%
      """).Output.Trim(), Is.EqualTo("9  2"));
  }
}
