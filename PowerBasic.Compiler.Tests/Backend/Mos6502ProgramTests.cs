using PowerBasic.Compiler.Cli;
using PowerBasic.Compiler.Emit.Commodore;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// <c>pbc --platform 6502</c>: BASIC compiled through the shared IR and middle end into a C64
/// <c>.PRG</c>, then run on <see cref="Cpu6502"/>. Every test runs what it built and checks what it
/// printed, so a pass is a program that computed the right answer on the chip.
///
/// <para>
/// The inputs are opaque: <c>INP</c> reads a port the C64 does not have, which answers 0, and the
/// optimizer cannot know that - so the arithmetic below is done by the 6502 at run time rather than
/// by the compiler folding it away. Unary minus on a variable, and wide arithmetic written straight
/// into a PRINT, are avoided: BASIC evaluates both in floating point, which this back end declines
/// for now. Assigned to an integer variable first, the same arithmetic stays integral.
/// </para>
/// </summary>
[TestFixture]
public sealed class Mos6502ProgramTests {

  private string _work = null!;

  [SetUp]
  public void CreateWorkDirectory() => _work = Directory.CreateTempSubdirectory("pbc-6502-").FullName;

  [TearDown]
  public void DeleteWorkDirectory() => Directory.Delete(_work, recursive: true);

  private (int Code, string Error, byte[] Prg) Build(string source, params string[] options) {
    var path = Path.Combine(_work, "PROG.BAS");
    File.WriteAllText(path, source);
    var stderr = new StringWriter();
    var code = Driver.Run(["--dialect", "pb36", "--platform", "6502", .. options, path], TextWriter.Null, stderr);
    var prg = Path.ChangeExtension(path, ".PRG");
    return (code, stderr.ToString(), File.Exists(prg) ? File.ReadAllBytes(prg) : []);
  }

  private string Run(string source, params string[] options) {
    var (code, error, prg) = this.Build(source, options);
    Assert.That(code, Is.Zero, error);
    var result = Cpu6502.RunC64Program(prg);
    Assert.Multiple(() => {
      Assert.That(result.Returned, Is.True, "the program returns to BASIC");
      Assert.That(result.StackPointer, Is.EqualTo(0xFF), "and leaves the hardware stack as it found it");
      Assert.That(result.Memory[0x02..0x30], Is.All.Zero, "and BASIC's page zero as it found it");
    });
    return result.Output.TrimEnd('\n');
  }

  [Test]
  public void Run_GivenRecursiveFunctions_ThenEachCallKeepsItsOwnArgumentsAndLocals() {
    var output = this.Run("""
      DEFINT A-Z
      DECLARE FUNCTION fib(BYVAL n)
      DECLARE FUNCTION fact&(BYVAL n)
      DECLARE FUNCTION ack(BYVAL m, BYVAL n)
      k = INP(&H60)
      PRINT fib(k + 15); fact&(k + 10); ack(k + 2, k + 3)
      END
      FUNCTION fib(BYVAL n)
        IF n < 2 THEN fib = n ELSE fib = fib(n - 1) + fib(n - 2)
      END FUNCTION
      FUNCTION fact&(BYVAL n)
        IF n <= 1 THEN fact& = 1 ELSE fact& = n * fact&(n - 1)
      END FUNCTION
      FUNCTION ack(BYVAL m, BYVAL n)
        IF m = 0 THEN
          ack = n + 1
        ELSEIF n = 0 THEN
          ack = ack(m - 1, 1)
        ELSE
          ack = ack(m - 1, ack(m, n - 1))
        END IF
      END FUNCTION
      """);

    Assert.That(output, Is.EqualTo(" 610  3628800  9 "));
  }

  [Test]
  public void Run_GivenSignedDivisionAndRemainder_ThenTheyTruncateTowardZero() {
    var output = this.Run("""
      DEFINT A-Z
      k = INP(&H60)
      a = k - 17: b = k + 5: c& = k - 100000: d& = k + 7
      PRINT a \ b; a MOD b; (0 - a) \ b; (0 - a) MOD b
      PRINT a \ (0 - b); a MOD (0 - b)
      PRINT c& \ d&; c& MOD d&
      """);

    Assert.That(output, Is.EqualTo("-3 -2  3  2 \n 3 -2 \n-14285 -5 "));
  }

  [Test]
  public void Run_GivenWideArithmeticShiftsAndLogic_ThenEveryByteCarries() {
    var output = this.Run("""
      DEFINT A-Z
      k = INP(&H60)
      x& = k + 300000: y& = k + 4097
      p& = x& * 7: q& = x& + y&: r& = x& - y& * 100
      PRINT p&; q&; r&
      a = k + &H1234
      b = a * 2
      PRINT a AND &HFF; a OR &H8000; a XOR &H0F0F
      PRINT a \ 16; b
      """);

    Assert.That(output, Is.EqualTo(" 2100000  304097 -109700 \n 52 -28108  7483 \n 291  9320 "));
  }

  [Test]
  public void Run_GivenArraysLoopsAndASelect_ThenTheyComputeWhatTheSourceSays() {
    var output = this.Run("""
      DEFINT A-Z
      DIM a(40)
      k = INP(&H60)
      FOR i = 0 TO 40
        a(i) = i * 3 - k
      NEXT
      s& = 0
      FOR i = 40 TO 0 STEP -2
        s& = s& + a(i)
      NEXT
      WHILE k < 25
        k = k + 7
      WEND
      SELECT CASE k
      CASE 28: PRINT "twenty-eight";
      CASE ELSE: PRINT "other";
      END SELECT
      PRINT s&; a(39)
      """);

    Assert.That(output, Is.EqualTo("twenty-eight 1260  117 "));
  }

  [Test]
  public void Run_GivenCommasTabAndSpc_ThenColumnsFollowBasicsZones() {
    var output = this.Run("""
      PRINT "a", "b"; TAB(20); "c"; SPC(3); "d"
      """);

    Assert.That(output, Is.EqualTo("a" + new string(' ', 13) + "b" + new string(' ', 4) + "c   d"));
  }

  [Test]
  public void Run_GivenADivisionByZero_ThenErrorElevenEndsTheProgram() {
    var output = this.Run("""
      DEFINT A-Z
      k = INP(&H60)
      PRINT "before"
      PRINT 10 \ k
      PRINT "after"
      """);

    Assert.That(output, Is.EqualTo("before\nError 11 "));
  }

  [Test]
  public void Build_GivenFloatingPoint_ThenItIsDeclinedByName() {
    var (code, error, prg) = this.Build("x! = INP(&H60) / 3\nPRINT x!\n");

    Assert.Multiple(() => {
      Assert.That(code, Is.Not.Zero);
      Assert.That(error, Does.Contain("floating point"));
      Assert.That(prg, Is.Empty);
    });
  }

  [Test]
  public void Build_GivenAProgram_ThenItIsAPrgThatLoadsBehindASysLine() {
    var (_, error, prg) = this.Build("PRINT \"hi\"\n");

    Assert.That(prg, Is.Not.Empty, error);
    Assert.Multiple(() => {
      Assert.That(prg[0] | (prg[1] << 8), Is.EqualTo(C64Prg.LoadAddress));
      Assert.That(prg[2..14], Is.EqualTo(new byte[] { 0x0B, 0x08, 0x0A, 0x00, 0x9E, 0x32, 0x30, 0x36, 0x31, 0x00, 0x00, 0x00 }),
        "10 SYS 2061");
      Assert.That(C64Prg.LoadAddress + prg.Length - 2, Is.LessThanOrEqualTo(C64Prg.MemoryTop));
    });
  }

  [TestCase("--emit-com")]
  [TestCase("--emit-obj")]
  [TestCase("--emit-lib")]
  public void Build_GivenADosOrObjectFormat_ThenThe6502RefusesIt(string option) {
    var (code, error, _) = this.Build("PRINT 1\n", option);

    Assert.That(code, Is.Not.Zero);
    Assert.That(error, Does.Contain("C64 .PRG only"));
  }
}
