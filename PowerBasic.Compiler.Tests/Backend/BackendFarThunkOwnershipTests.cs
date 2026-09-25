using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The far entry thunks are SHARED, not the direct emitter's.
///
/// <para>
/// A routed delegate's code half is a far pointer at an adapter named <c>thk_&lt;proc&gt;</c>, and
/// <c>CalleeLabel</c> mints it by calling <c>ThunkOf</c> - which is the thing that fills
/// <c>_farThunks</c>, which is what <c>EmitFarThunks</c> walks. So the routed path both REGISTERS and
/// NEEDS the thunks, and the adapter a routed delegate points at is the same one a directly emitted
/// <c>CODEPTR32</c> names, which is why closure values are interchangeable between the paths.
/// </para>
/// <para>
/// This exists because <c>docs/DIRECT-EMITTER-RETIREMENT.md</c> said the opposite - "only direct
/// emission ever populated <c>_farThunks</c>" - in a list of things to delete, one bullet after a list
/// of four symbols the routed path needs that included this one. The claim was true before closures
/// lowered and false afterwards, and a deletion carried out against it would have removed the entry
/// point of every routed delegate. A comment cannot fail; this can.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendFarThunkOwnershipTests {

  private const string _source = """
    DECLARE FUNCTION Apply%(BYVAL f AS FUNCTION(INTEGER) AS INTEGER, BYVAL n AS INTEGER)
    DIM g AS FUNCTION(INTEGER) AS INTEGER
    g = Twice
    PRINT Apply%(g, 20)
    END
    FUNCTION Twice%(BYVAL v AS INTEGER)
      Twice% = v * 2
    END FUNCTION
    FUNCTION Apply%(BYVAL f AS FUNCTION(INTEGER) AS INTEGER, BYVAL n AS INTEGER)
      Apply% = f(n)
    END FUNCTION
    """;

  private static (byte[] Image, IEnumerable<string> Routed) Compile(bool optimize) {
    var model = Binder.Bind(
      Parser.Parse(Lexer.Tokenize(_source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize, UseExperimentalBackend = true };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return (image, generator.BackendRoutedNames.ToList());
  }

  /// <summary>
  /// The delegate is CALLED, so a thunk that was never emitted is not a missing optimization - it is a
  /// far call into whatever occupies the address, and the program does not survive it. Executing is
  /// therefore the assertion: 40 comes back only if the adapter exists, strips the far return address
  /// and lands in the near procedure.
  /// </summary>
  [TestCase(true)]
  [TestCase(false)]
  public void Execute_GivenARoutedDelegate_ThenTheFarThunkIsEmittedForIt(bool optimize) {
    var (image, routed) = Compile(optimize);

    Assert.That(routed, Does.Contain("main"), "the body holding the delegate must route");
    Assert.That(Cpu8086.Run(image).Output.Trim(), Is.EqualTo("40"),
      "the routed delegate reached its target through the far entry thunk");
  }
}
