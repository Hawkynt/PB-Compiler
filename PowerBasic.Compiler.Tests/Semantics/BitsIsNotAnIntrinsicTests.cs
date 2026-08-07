using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Semantics;

/// <summary>
/// <c>BITS</c> is not a PowerBASIC function, and this compiler no longer pretends it is.
///
/// It was declared as a three-argument Long-returning intrinsic that nothing implemented and nothing
/// documented - the intrinsic census listed it as binding with no code generation, and the note
/// there said writing it would mean inventing the semantics.
///
/// Asked directly, PBC 3.5 settles it the other way round: there is no such function. It parses
/// <c>BITS(255)</c> as a subscript of an undeclared array and refuses it at compile time with
/// "Error 479: Array bounds error", and compiles <c>BITS(x, 0, 4)</c> as a subscripted read that
/// answers 0 for every combination of arguments. So the declaration was the mistake, not the missing
/// code generation, and removing it makes the name behave here as it does there.
/// </summary>
[TestFixture]
public sealed class BitsIsNotAnIntrinsicTests {

  [Test]
  public void Intrinsics_GivenBits_ThenItIsNotInTheTable() =>
    Assert.That(Intrinsics.All.Select(i => i.Name), Has.None.EqualTo("BITS"));

  /// <summary>BIT, the real one, is untouched - the two differ by a letter and only one exists.</summary>
  [Test]
  public void Intrinsics_GivenBit_ThenItIsStillThere() =>
    Assert.That(Intrinsics.All.Select(i => i.Name), Has.Some.EqualTo("BIT"));

  /// <summary>
  /// And the name now binds as ordinary source rather than as a function this compiler cannot
  /// generate: whatever diagnostic it draws, it is no longer "intrinsic not implemented".
  /// </summary>
  [Test]
  public void Bind_GivenBitsCall_ThenItIsNoLongerAnUnimplementedIntrinsic() {
    const string source = "DIM BITS(10, 10, 10)\nX% = 1\nY% = BITS(X%, 0, 4)\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35), Dialect.Pb35);
    Assert.That(model.Errors.Select(e => e.Message), Is.Empty,
      "with an array of that name declared, the call is an ordinary subscripted read");
  }
}
