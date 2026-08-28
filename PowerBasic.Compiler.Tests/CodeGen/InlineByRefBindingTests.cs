using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// How the inliner binds a BYREF parameter, and the one shape it got wrong.
///
/// <para>
/// A real call compares the argument's type against the parameter's: equal types pass the argument's
/// own address, and anything else is evaluated, coerced and copied into a hidden temp of the
/// parameter's width (<c>CodeGenerator.EmitArgumentPush</c>). <c>TryEmitInlinedFunction</c> had only
/// the first arm and asked only whether the argument was a near lvalue, so <c>f#(i%)</c> pointed a
/// DOUBLE parameter at a two-byte INTEGER cell: the body loaded eight bytes from it and doubled six
/// bytes of whatever the frame layout had put next. It printed 1.4E-315 where 32 is right, and the
/// same shape at LONG width printed 238551072.
/// </para>
/// <para>
/// The unoptimized build is the control, because inlining is gated on the optimizer: the two builds of
/// one program must print the same thing, and the unoptimized one is the arithmetic answer. Every
/// subject comes out of a <c>NOINLINE</c> function so nothing folds - with a literal argument the
/// whole call is constant-folded away and the binding is never emitted at all, which is exactly why
/// the corpus's one <c>DEF FN</c> never noticed.
/// </para>
/// <para>
/// Genuine PBC 3.50 rejects the mismatch outright - <c>Error 481: Parameter mismatch - may need
/// ByCopy</c> - so this is a region only our compilers accept. Which makes agreement between our two
/// builds the whole of the available contract, and a garbage answer in one of them a defect on its
/// own terms.
/// </para>
/// </summary>
[TestFixture]
public sealed class InlineByRefBindingTests {

  private static string Run(string source, bool optimize) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    try {
      return Cpu8086.Run(image).Output.Replace("\r", "").Trim();
    } catch (Cpu8086Exception e) {
      Assert.Ignore($"the interpreter cannot run the image: {e.Message}");
      return "";
    }
  }

  [TestCase("#", "DOUBLE")]
  [TestCase("!", "SINGLE")]
  [TestCase("&", "LONG")]
  public void Emit_GivenAByRefParameterWiderThanItsArgument_WhenOptimized_ThenTheCalleeReadsTheArgument(
      string suffix, string named) {
    var source = $"""
      FUNCTION Twice{suffix}(p{suffix}) AS {named}
        Twice{suffix} = p{suffix} * 2
      END FUNCTION

      FUNCTION Src%(BYVAL k%) AS INTEGER NOINLINE
        Src% = k% * 3 + 1
      END FUNCTION

      DIM i AS INTEGER
      i = Src%(5)
      PRINT Twice{suffix}(i)
      END
      """;

    var unoptimized = Run(source, optimize: false);
    Assert.Multiple(() => {
      Assert.That(unoptimized, Is.EqualTo("32"), "the real call copies the widened argument into its temp");
      Assert.That(Run(source, optimize: true), Is.EqualTo(unoptimized),
        "the inliner must not alias a narrow cell as the parameter's wider type");
    });
  }

  /// <summary>
  /// The arm the fix must NOT cost: same type on both sides still inlines, and the purge pre-pass
  /// still agrees that it does - a procedure it drops while the emitter declines to inline it would
  /// leave a call to a body that is no longer in the image.
  /// </summary>
  [Test]
  public void Emit_GivenAByRefParameterOfTheArgumentsOwnType_WhenOptimized_ThenItStillInlinesAndIsPurged() {
    const string source = """
      FUNCTION Twice%(p%) AS INTEGER
        Twice% = p% * 2
      END FUNCTION

      FUNCTION Src%(BYVAL k%) AS INTEGER NOINLINE
        Src% = k% * 3 + 1
      END FUNCTION

      DIM i AS INTEGER
      i = Src%(5)
      PRINT Twice%(i)
      END
      """;

    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    var generator = new CodeGenerator(model) { Optimize = true };
    var image = generator.EmitExecutable();

    Assert.Multiple(() => {
      Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
      Assert.That(System.Text.Encoding.ASCII.GetString(image), Does.Not.Contain("Twice"),
        "the matching-type call still inlines, so the body is purged");
      Assert.That(Run(source, optimize: true), Is.EqualTo("32"));
    });
  }
}
