using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// <c>FLEX</c>, the PB 3.5 flexible-structure string.
///
/// <para>
/// It is a dynamic string handle and nothing else - <c>FlexType</c>'s own summary says "stored like a
/// dynamic string handle", and both the binder and the direct emitter spell the pair
/// <c>StringType or FlexType</c> wherever one is meant. The lowering had picked up the first half of
/// that pair and not the second, so <c>DEFFLX</c> declined at its storage, its assignment, its read
/// and its PRINT - four sites, each of which named the next when the one before it was fixed.
/// </para>
/// <para>
/// So what these assert is that a FLEX variable behaves as the <c>$</c> one beside it does. Comparing
/// the two IN THE SAME PROGRAM is the point: a lowering that gave FLEX a separate and subtly different
/// storage would still print something plausible on its own.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendFlexStringTests {

  private static (string Output, IEnumerable<string> Routed) Run(string source, bool optimize, bool routed) {
    var model = Binder.Bind(
      Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize, UseExperimentalBackend = routed };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return (Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|"), generator.BackendRoutedNames.ToList());
  }

  /// <summary>
  /// Assignment, re-assignment, reading, concatenating and measuring - the operations a handle has.
  ///
  /// <para>
  /// The re-assignment matters more than it looks: a string variable's old handle is freed when a new
  /// one replaces it, and a FLEX slot the lowering had not marked as null-initialised would hand the
  /// allocator whatever the frame happened to contain. Two assignments are what reach that path at all.
  /// </para>
  /// </summary>
  [TestCase(true)]
  [TestCase(false)]
  public void Execute_GivenAFlexVariable_WhenRouted_ThenItBehavesLikeAString(bool optimize) {
    const string source = """
      DEFFLX Y-Z
      DIM s AS STRING
      yv = "ab"
      s = "ab"
      yv = yv + "cd"
      s = s + "cd"
      PRINT yv; "|"; s
      PRINT LEN(yv); LEN(s)
      PRINT yv = s
      zv = MID$(yv, 2, 2)
      PRINT zv
      """;

    var (output, routed) = Run(source, optimize, routed: true);
    Assert.That(routed, Does.Contain("main"), "a body naming a FLEX variable must route now");
    Assert.Multiple(() => {
      Assert.That(output, Is.EqualTo(Run(source, optimize, routed: false).Output));
      Assert.That(output, Is.EqualTo("abcd|abcd| 4  4 |-1 |bc"),
        "the FLEX answered what the STRING beside it answered, at every step");
    });
  }
}
