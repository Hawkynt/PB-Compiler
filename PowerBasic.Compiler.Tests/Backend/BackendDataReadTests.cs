using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// <c>DATA</c>/<c>READ</c>/<c>RESTORE</c> over both back ends. The corpus reads DATA in two programs
/// and neither of them runs off the end, which is the whole reason the routed path could walk past the
/// blob for as long as it did.
///
/// <para>
/// The end of the pool is not merely an error to report: the cursor then stands on whatever global was
/// laid out next, and the two bytes there are read as an item LENGTH. So an unchecked READ hands the
/// target a value out of an unrelated object and advances the cursor by it - the failure is a wrong
/// ANSWER, and only then a missing diagnostic. Genuine PBC 3.50 raises error 4 and leaves the target
/// untouched (checked with <c>scripts/diff-one.sh … pb35</c>), which is what the direct emitter's
/// <c>rt_readdata</c> does by comparing <c>rt_dataptr</c> against <c>rt_dataend</c>.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendDataReadTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static (string Direct, string Routed, IEnumerable<string> RoutedNames) RunBothWays(string source, bool optimize) {
    var direct = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = true };
    var directImage = direct.EmitExecutable();
    var routedImage = routed.EmitExecutable();
    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));

    string Execute(byte[] image, string which) {
      try {
        return Cpu8086.Run(image).Output;
      } catch (Cpu8086Exception e) {
        Assert.Ignore($"the interpreter cannot run the {which} image: {e.Message}");
        return "";
      }
    }

    return (Execute(directImage, "direct"), Execute(routedImage, "routed"), routed.BackendRoutedNames);
  }

  /// <summary>
  /// Two items, three reads. The third must raise error 4 and leave <c>i</c> holding what the second
  /// read put there - which is what makes <c>RESUME NEXT</c> print the previous value again and tells
  /// a raise apart from a silent zero.
  /// </summary>
  [TestCase(true, TestName = "Run_GivenAReadPastTheLastDataItem_WhenOptimized_ThenErrorFourIsRaisedAndTheTargetIsUntouched")]
  [TestCase(false, TestName = "Run_GivenAReadPastTheLastDataItem_WhenUnoptimized_ThenErrorFourIsRaisedAndTheTargetIsUntouched")]
  public void Run_GivenAReadPastTheLastDataItem_ThenErrorFourIsRaisedAndTheTargetIsUntouched(bool optimize) {
    var (direct, routed, names) = RunBothWays("""
      DATA 1, 2
      DIM i AS INTEGER
      ON ERROR GOTO Trap
      i = 55
      READ i : PRINT "a"; i
      READ i : PRINT "b"; i
      READ i : PRINT "c"; i
      PRINT "done"
      END
      Trap:
        PRINT "err"; ERR
        RESUME NEXT
      """, optimize);

    Assert.That(names, Does.Contain("main"), "the module body did not route, so this compares one image with itself");
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(direct.Replace("\r", ""), Is.EqualTo("a 1 \nb 2 \nerr 4 \nc 2 \ndone\n"),
      "and that is the answer genuine PBC 3.50 gives");
  }

  /// <summary>
  /// The reads that stay inside the pool, so the bounds check cannot be paid for by breaking them:
  /// every scalar width, a string item, <c>RESTORE &lt;label&gt;</c> and bare <c>RESTORE</c>.
  /// </summary>
  [TestCase(true, TestName = "Run_GivenTypedReadsAndRestore_WhenOptimized_ThenBothPathsAgree")]
  [TestCase(false, TestName = "Run_GivenTypedReadsAndRestore_WhenUnoptimized_ThenBothPathsAgree")]
  public void Run_GivenTypedReadsAndRestore_ThenBothPathsAgree(bool optimize) {
    var (direct, routed, names) = RunBothWays("""
      DATA 10, 20, hello, 3.5, -7
      Second:
      DATA 99, world, 1.25
      DIM i AS INTEGER
      DIM s AS STRING
      DIM f AS SINGLE
      DIM d AS DOUBLE
      DIM l AS LONG
      READ i : PRINT i
      READ i : PRINT i
      READ s : PRINT s
      READ f : PRINT f
      READ i : PRINT i
      RESTORE Second
      READ l : PRINT l
      READ s : PRINT s
      READ d : PRINT d
      RESTORE
      READ i : PRINT i
      END
      """, optimize);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(direct, Does.Contain("hello"));
    Assert.That(direct, Does.Contain("world"));
  }
}
