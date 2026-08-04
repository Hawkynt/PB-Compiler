using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// Who owns a string handle, and therefore who is allowed to free it.
///
/// PB's string runtime works on one rule, stated at the head of <c>DosRuntime.Strings.cs</c>: every
/// string value in generated code is an OWNED TEMPORARY, and a routine documented as "consumes" frees
/// what it is handed. <c>rt_strcat</c> consumes both operands; <c>rt_str_print</c> consumes what it
/// prints. That makes reading a string variable the interesting case - the handle in the cell belongs
/// to the cell, so passing it on directly is a use-after-free, and the value has to be copied first.
///
/// The lowering did not copy, which nothing noticed while no string-printing function was routed. As
/// soon as one was, <c>PRINT a$</c> twice printed "hello" and then nothing, and <c>a$ + b$</c> emptied
/// both operands. Neither faulted: freeing a handle just marks its descriptor free, so the next read
/// finds a zero-length string and prints happily.
/// </summary>
[TestFixture]
public sealed class StringOwnershipTests {

  private static string Run(string source, bool routed, out IEnumerable<string> names) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = true, UseExperimentalBackend = routed };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    names = cg.BackendRoutedNames.ToList();
    return Cpu8086.Run(image).Output.Replace("\r\n", "|").Trim();
  }

  /// <param name="mustRoute">
  /// Whether the back end has to have taken the program. Where it does, the test is a real
  /// differential; where it declines for an unrelated reason the ownership rule still has to hold,
  /// because the same lowering feeds the C and LLVM emitters.
  /// </param>
  private static void BothPathsAgree(string source, string expected, bool mustRoute = true) {
    var routed = Run(source, routed: true, out var names);
    if (mustRoute)
      Assert.That(names, Is.Not.Empty, "nothing was routed, so this proves nothing");
    Assert.That(routed, Is.EqualTo(Run(source, routed: false, out _)), "the two emitters disagree");
    Assert.That(routed, Is.EqualTo(expected));
  }

  [Test]
  public void Run_GivenAStringPrintedTwice_ThenTheVariableSurvivesTheFirstPrint() =>
    BothPathsAgree("""
      a$ = "hello"
      PRINT a$
      PRINT a$
      END
      """, "hello|hello|");

  [Test]
  public void Run_GivenAConcatenation_ThenBothOperandsSurviveIt() =>
    BothPathsAgree("""
      a$ = "al"
      b$ = "be"
      PRINT a$ + b$
      PRINT a$
      PRINT b$
      END
      """, "albe|al|be|");

  [Test]
  public void Run_GivenAStringArrayElement_ThenReadingItDoesNotEmptyIt() =>
    BothPathsAgree("""
      DIM s$(0 TO 2)
      s$(1) = "mid"
      PRINT s$(1)
      PRINT s$(1)
      END
      """, "mid|mid|", mustRoute: false);   // a string ARRAY still declines selection for its own reasons

  /// <summary>An assignment from one variable to another must copy, not alias the same handle.</summary>
  [Test]
  public void Run_GivenAnAssignmentBetweenVariables_ThenTheyAreIndependent() =>
    BothPathsAgree("""
      a$ = "one"
      b$ = a$
      a$ = "two"
      PRINT a$
      PRINT b$
      END
      """, "two|one|");

  [Test]
  public void Lower_GivenAStringVariableRead_ThenItIsCopiedBeforeBeingPassedOn() {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize("""
      a$ = "x"
      PRINT a$
      END
      """, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    var module = IrLowering.TryLowerModule(model, out var why);

    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    var calls = module!.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions).OfType<IrCall>()
      .Select(c => (c.Callee as IrFunction)?.Name).ToList();
    Assert.That(calls, Does.Contain("rt_str_dup"), "the copy is what makes the consuming print safe");
  }
}
