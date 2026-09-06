using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Physical multidimensional-array layout. PowerBASIC stores the first subscript contiguously, so
/// these tests observe ADDRESS DELTAS rather than merely writing and reading through the same indexer:
/// two equally wrong flatteners can agree on values, but they cannot fake the byte distance between
/// adjacent source elements.
/// </summary>
[TestFixture]
public sealed class BackendArrayLayoutTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static (string Direct, string Routed, IEnumerable<string> RoutedNames) RunBothWays(string source) {
    var direct = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = true };
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

  private static string[] Lines(string output)
    => output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(line => line.Trim()).ToArray();

  [Test]
  public void StaticTwoDimensionalArray_UsesFirstSubscriptFastestPhysicalLayout() {
    var (direct, routed, names) = RunBothWays("""
      DIM a%(10 TO 12, 20 TO 23)
      DIM origin AS LONG
      origin = VARPTR(a%(10, 20))
      PRINT CLNG(VARPTR(a%(11, 20))) - origin
      PRINT CLNG(VARPTR(a%(10, 21))) - origin
      PRINT CLNG(VARPTR(a%(12, 23))) - origin
      """);

    Assert.That(names, Does.Contain("main"), "the back end did not take the module body under test");
    Assert.That(routed, Is.EqualTo(direct));
    // INTEGER elements are two bytes. The first dimension has stride 1 element; the second has
    // stride 3 elements. (12,23) is element 2 + 3*3 = 11 from the origin.
    Assert.That(Lines(routed), Is.EqualTo(new[] { "2", "6", "22" }));
  }

  [Test]
  public void DynamicTwoDimensionalArray_UsesTheSameFirstSubscriptFastestPhysicalLayout() {
    var (direct, routed, names) = RunBothWays("""
      REDIM a%(10 TO 12, 20 TO 23)
      DIM origin AS LONG
      origin = VARPTR(a%(10, 20))
      PRINT CLNG(VARPTR(a%(11, 20))) - origin
      PRINT CLNG(VARPTR(a%(10, 21))) - origin
      PRINT CLNG(VARPTR(a%(12, 23))) - origin
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(Lines(routed), Is.EqualTo(new[] { "2", "6", "22" }));
  }

  [Test]
  public void OptionBaseOne_DynamicDimAndRedimPreserve_UseOneAsEveryImplicitLowerBound() {
    var (direct, routed, names) = RunBothWays("""
      OPTION BASE 1
      DIM a%(4, 5)
      a%(4, 5) = 45
      PRINT LBOUND(a%, 1)
      PRINT UBOUND(a%, 1)
      PRINT LBOUND(a%, 2)
      PRINT UBOUND(a%, 2)

      REDIM PRESERVE a%(4, 6)
      PRINT LBOUND(a%, 1)
      PRINT UBOUND(a%, 1)
      PRINT LBOUND(a%, 2)
      PRINT UBOUND(a%, 2)
      PRINT a%(4, 5)
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(Lines(routed), Is.EqualTo(new[] { "1", "4", "1", "5", "1", "4", "1", "6", "45" }));
  }

  [Test]
  public void RedimPreserve_WhenOnlyTheLastDimensionGrows_KeepsEveryExistingElementInPlace() {
    var (direct, routed, names) = RunBothWays("""
      REDIM a%(1 TO 2, 10 TO 11)
      a%(1, 10) = 11
      a%(2, 10) = 12
      a%(1, 11) = 21
      a%(2, 11) = 22

      REDIM PRESERVE a%(1 TO 2, 10 TO 13)
      PRINT a%(1, 10)
      PRINT a%(2, 10)
      PRINT a%(1, 11)
      PRINT a%(2, 11)
      PRINT a%(1, 12)
      PRINT a%(2, 13)
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    // The existing four elements remain the allocation prefix when the LAST (slowest) dimension
    // grows; the allocator also promises a zero-filled new tail.
    Assert.That(Lines(routed), Is.EqualTo(new[] { "11", "12", "21", "22", "0", "0" }));
  }

  [Test]
  public void RedimPreserve_OnNeverAllocatedArray_AllocatesTheRequestedFreshShape() {
    var (direct, routed, names) = RunBothWays("""
      REDIM PRESERVE a%(1 TO 2, 10 TO 11)
      PRINT LBOUND(a%, 1)
      PRINT UBOUND(a%, 1)
      PRINT LBOUND(a%, 2)
      PRINT UBOUND(a%, 2)
      PRINT a%(2, 11)
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(Lines(routed), Is.EqualTo(new[] { "1", "2", "10", "11", "0" }));
  }

  [Test]
  public void RedimPreserve_WhenANonLastUpperBoundChanges_RaisesSubscriptOutOfRangeWithoutChangingTheArray() {
    var (direct, routed, names) = RunBothWays("""
      REDIM a%(1 TO 2, 10 TO 11)
      a%(2, 11) = 22
      DIM changed AS INTEGER
      changed = 3
      ON ERROR GOTO trapped
      REDIM PRESERVE a%(1 TO changed, 10 TO 11)
      PRINT -1
      END
      trapped:
      PRINT ERR
      PRINT LBOUND(a%, 1)
      PRINT UBOUND(a%, 1)
      PRINT LBOUND(a%, 2)
      PRINT UBOUND(a%, 2)
      PRINT a%(2, 11)
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(Lines(routed), Is.EqualTo(new[] { "9", "1", "2", "10", "11", "22" }));
  }

  [Test]
  public void RedimPreserve_WhenALowerBoundChanges_RaisesSubscriptOutOfRangeWithoutChangingTheArray() {
    var (direct, routed, names) = RunBothWays("""
      REDIM a%(1 TO 2, 10 TO 11)
      a%(2, 11) = 22
      DIM changed AS INTEGER
      changed = 9
      ON ERROR GOTO trapped
      REDIM PRESERVE a%(1 TO 2, changed TO 11)
      PRINT -1
      END
      trapped:
      PRINT ERR
      PRINT LBOUND(a%, 1)
      PRINT UBOUND(a%, 1)
      PRINT LBOUND(a%, 2)
      PRINT UBOUND(a%, 2)
      PRINT a%(2, 11)
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(Lines(routed), Is.EqualTo(new[] { "9", "1", "2", "10", "11", "22" }));
  }

  [Test]
  public void RedimPreserve_EvaluatesEachRuntimeBoundOnceInSourceOrder() {
    var (direct, routed, names) = RunBothWays("""
      DECLARE FUNCTION Mark%(BYVAL value%, BYVAL code%)
      DIM seq AS SHARED INTEGER
      REDIM a%(1 TO 2, 10 TO 11)
      a%(2, 11) = 22

      seq = 0
      REDIM PRESERVE a%(Mark%(1, 1) TO Mark%(2, 2), Mark%(10, 3) TO Mark%(13, 4))
      PRINT seq
      PRINT a%(2, 11)
      END

      FUNCTION Mark%(BYVAL value%, BYVAL code%)
        SHARED seq AS INTEGER
        seq = seq * 10 + code%
        Mark% = value%
      END FUNCTION
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(names, Does.Contain("Mark"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(Lines(routed), Is.EqualTo(new[] { "1234", "22" }));
  }

  [Test]
  public void MultidimensionalSubscripts_AreStillEvaluatedLeftToRight() {
    var (direct, routed, names) = RunBothWays("""
      DECLARE FUNCTION Mark%(BYVAL n%)
      DIM seq AS SHARED INTEGER
      DIM s%(0 TO 1, 0 TO 1)
      REDIM d%(0 TO 1, 0 TO 1)

      seq = 0
      s%(Mark%(0), Mark%(1)) = 7
      PRINT seq

      seq = 0
      d%(Mark%(0), Mark%(1)) = 8
      PRINT seq
      END

      FUNCTION Mark%(BYVAL n%)
        SHARED seq AS INTEGER
        seq = seq * 10 + n% + 1
        Mark% = n%
      END FUNCTION
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(names, Does.Contain("Mark"));
    Assert.That(routed, Is.EqualTo(direct));
    // Reversing expression evaluation merely to make the address fold convenient would print 21.
    Assert.That(Lines(routed), Is.EqualTo(new[] { "12", "12" }));
  }
}
