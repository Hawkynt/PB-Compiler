using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Execution-level gate for the register-argument conventions. FASTCALL takes its leading arguments
/// in AX,DX,BX and WATCALL in AX,DX,BX,CX; the routed prologue pushes them into the negative frame
/// cells <c>LayoutFrame</c> assigned, after which they are ordinary frame parameters.
///
/// <para>
/// FIVE parameters is the deliberate count: it overflows both register files, so the test observes
/// the boundary between register arguments and the stack arguments behind them - the place where a
/// wrong register order and a wrong overflow push order produce different answers. Every argument is
/// a different value for the same reason, and the two call sites pass different ones so IPCP cannot
/// prove either set constant and fold the parameters away before selection.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendRegisterConventionRoutingTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static void AssertRoutedMatchesDirect(string source, string procedure, bool optimize) {
    var routed = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = true };
    var routedImage = routed.EmitExecutable();
    Assert.That(routed.Errors, Is.Empty, "routed: " + string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain(procedure),
      $"{procedure} did not route - the comparison below would have compiled the same image twice");

    var direct = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = false };
    var directImage = direct.EmitExecutable();
    Assert.That(direct.Errors, Is.Empty, "direct: " + string.Join("; ", direct.Errors));

    var expected = Cpu8086.Run(directImage);
    var actual = Cpu8086.Run(routedImage);
    Assert.That((actual.Output, actual.ExitCode), Is.EqualTo((expected.Output, expected.ExitCode)));
  }

  private static string ByValSource(string convention) => $$"""
    SUB S {{convention}} (BYVAL a%, BYVAL b%, BYVAL c%, BYVAL d%, BYVAL e%) NOINLINE
      PRINT a%; b%; c%; d%; e%
      PRINT a% - b% - c% - d% - e%
    END SUB
    S 1, 2, 3, 4, 5
    S -6, 7, -8, 9, -10
    PRINT 99
    """;

  [TestCase("FASTCALL", false)]
  [TestCase("FASTCALL", true)]
  [TestCase("WATCALL", false)]
  [TestCase("WATCALL", true)]
  public void Procedure_GivenRegisterConvention_ThenRoutedAndDirectExecutionAgree(string convention, bool optimize)
    => AssertRoutedMatchesDirect(ByValSource(convention), "S", optimize);

  /// <summary>
  /// A BYREF argument crosses a register convention as one near pointer, so the write-back has to
  /// reach the caller's own storage. Doing the spill wrong would leave the callee dereferencing
  /// whatever the frame happened to hold, which prints plausibly rather than crashing.
  /// </summary>
  private static string ByRefSource(string convention) => $$"""
    SUB S {{convention}} (a%, b%, BYVAL c%) NOINLINE
      a% = a% + c%
      b% = b% - c%
    END SUB
    DIM p%, q%
    p% = 100 : q% = 200
    S p%, q%, 7
    PRINT p%; q%
    S p%, q%, -3
    PRINT p%; q%
    """;

  [TestCase("FASTCALL", false)]
  [TestCase("FASTCALL", true)]
  [TestCase("WATCALL", false)]
  [TestCase("WATCALL", true)]
  public void Procedure_GivenRegisterConventionByRef_ThenTheCallersStorageIsUpdated(string convention, bool optimize)
    => AssertRoutedMatchesDirect(ByRefSource(convention), "S", optimize);

  /// <summary>
  /// The register arguments share [BP-2], [BP-4], ... with whatever frame storage the routed body
  /// needs, and the two layouts are computed by different code. A local array forces real stack
  /// slots to exist alongside the spilled parameters, so an overlap shows up as a wrong number.
  /// </summary>
  private static string FrameSharingSource(string convention) => $$"""
    FUNCTION F {{convention}} (BYVAL n%, BYVAL m%) AS INTEGER NOINLINE
      DIM t%(0 TO 7)
      FOR i% = 0 TO 7
        t%(i%) = n% * i% + m%
      NEXT i%
      DIM s%
      FOR i% = 0 TO 7
        s% = s% + t%(i%)
      NEXT i%
      F = s% + n% - m%
    END FUNCTION
    PRINT F(3, 5)
    PRINT F(-2, 11)
    """;

  [TestCase("FASTCALL", false)]
  [TestCase("FASTCALL", true)]
  [TestCase("WATCALL", false)]
  [TestCase("WATCALL", true)]
  public void Procedure_GivenRegisterConventionWithFrameStorage_ThenLocalsDoNotOverlapTheSpilledArguments(
      string convention, bool optimize)
    => AssertRoutedMatchesDirect(FrameSharingSource(convention), "F", optimize);
}
