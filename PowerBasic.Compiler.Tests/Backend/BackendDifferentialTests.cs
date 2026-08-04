using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The measurement the retargetable path has been missing: the same program compiled BOTH ways, both
/// images <b>executed</b>, and their output compared.
///
/// Everything else about the x86-16 back end is checked statically - what selects, what allocates,
/// which registers an ABI names, whether an image assembles. None of that says the emitted code
/// computes the right thing. This does, and it needs no vintage oracle to do it: byte-identity with
/// PBC 3.50 is the direct emitter's job, and the IR path will never match those bytes because it is a
/// different code generator. What it must match is what the program PRINTS, and the direct emitter -
/// which the golden battery holds to the genuine compiler - is the reference for that.
///
/// A program <see cref="Cpu8086"/> cannot run is skipped, never passed: the interpreter throws on any
/// opcode or DOS call it does not implement, so a green test here means the code really ran.
/// </summary>
[TestFixture]
public sealed class BackendDifferentialTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static (string Output, string Routed, IEnumerable<string> RoutedNames) RunBothWays(string source) {
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

  [Test]
  public void Run_GivenAnIntegerFunction_ThenBothPathsPrintTheSameThing() {
    var (direct, routed, names) = RunBothWays("""
      FUNCTION Twice%(BYVAL v%)
        Twice% = v% + v%
      END FUNCTION

      PRINT Twice%(21)
      """);

    Assert.That(names, Does.Contain("Twice"), "the back end did not take the function under test");
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(direct.Trim(), Is.EqualTo("42"), "and the answer is the one BASIC gives");
  }

  [Test]
  public void Run_GivenAConstantDivide_ThenBothPathsAgree() {
    var (direct, routed, names) = RunBothWays("""
      FUNCTION Tenth%(BYVAL v%)
        Tenth% = v% \ 10
      END FUNCTION

      PRINT Tenth%(250)
      PRINT Tenth%(-7)
      """);

    Assert.That(names, Does.Contain("Tenth"));
    Assert.That(routed, Is.EqualTo(direct));
  }

  [Test]
  public void Run_GivenAModuleBodyTheBackEndOwns_ThenTheWholeProgramAgrees() {
    var (direct, routed, names) = RunBothWays("""
      DIM n AS INTEGER
      n = 42
      PRINT "n="
      PRINT n
      """);

    Assert.That(names, Does.Contain("main"), "this is the whole-program case, not the per-function one");
    Assert.That(routed, Is.EqualTo(direct));
  }

  [Test]
  public void Run_GivenAValueLiveAcrossACall_ThenTheSpilledFormComputesTheSameAnswer() {
    // the parameter is live across a PRINT, so the back end spills it into the caller's own word -
    // this is the first check that the spill actually preserves the value rather than merely allocating
    var (direct, routed, names) = RunBothWays("""
      FUNCTION Twice%(BYVAL v%)
        PRINT "in"
        Twice% = v% + v%
      END FUNCTION

      PRINT Twice%(21)
      """);

    Assert.That(names, Does.Contain("Twice"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(routed, Does.Contain("42"));
  }

  [Test]
  public void Run_GivenALoopAndAControlFlowMerge_ThenBothPathsAgree() {
    var (direct, routed, names) = RunBothWays("""
      FUNCTION SumTo%(BYVAL n%)
        DIM i AS INTEGER
        DIM total AS INTEGER
        total = 0
        FOR i = 1 TO n%
          IF i MOD 2 = 0 THEN
            total = total + i
          ELSE
            total = total - 1
          END IF
        NEXT i
        SumTo% = total
      END FUNCTION

      PRINT SumTo%(10)
      """);

    Assert.That(names, Does.Contain("SumTo"));
    Assert.That(routed, Is.EqualTo(direct));
  }

  [Test]
  public void Run_GivenASharedGlobal_ThenBothPathsAddressTheSameStorage() {
    var (direct, routed, names) = RunBothWays("""
      DIM g AS SHARED INTEGER

      FUNCTION AddG%(BYVAL v%)
        AddG% = v% + g
      END FUNCTION

      g = 40
      PRINT AddG%(2)
      """);

    Assert.That(names, Does.Contain("AddG"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(routed, Does.Contain("42"), "the routed function read the global the direct path wrote");
  }
}
