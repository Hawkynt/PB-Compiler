using PowerBasic.Compiler.Backend.Mos6502;
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
/// by the compiler folding it away.
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

  private string Run(string source, params string[] options) => this.Run(source, [], options);

  private string Run(string source, Dictionary<string, List<byte>> disk, params string[] options) {
    var (code, error, prg) = this.Build(source, options);
    Assert.That(code, Is.Zero, error);
    var result = Cpu6502.RunC64Program(prg, disk: disk);
    Assert.Multiple(() => {
      Assert.That(result.Returned, Is.True, "the program returns to BASIC");
      Assert.That(result.StackPointer, Is.EqualTo(0xFF), "and leaves the hardware stack as it found it");
      Assert.That(result.Memory[Mos6502ZeroPage.First..(Mos6502ZeroPage.Last + 1)], Is.All.Zero, "and BASIC's page zero as it found it");
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
  public void Run_GivenFloatingPoint_ThenSoftFloatComputesWhatThePcPrints() {
    var output = this.Run("""
      k = INP(&H60)
      a! = (k + 1) / 3
      b# = (k + 2) / 3#
      PRINT a!; b#; (k - 5) / 2; (k + 1.5) * 1E-7; (k + 1) / 10000
      x& = (k + 7.5)
      y% = (k + 2.5)
      PRINT x&; y%; FIX(k - 2.7); INT(k - 2.7); CINT(k + 3.5)
      """);

    Assert.That(output, Is.EqualTo(" .3333333  .666666666666667 -2.5  1.5E-07  .0001 \n 8  2 -2 -3  4 "));
  }

  private static readonly double[] MathArguments = [0.5, 1, 2, 3, 10, 0.001, 123.456, 1000, 7.25, 0.1];

  /// <summary>Math functions in groups small enough for a C64 each: the BASIC, and .NET's answers.</summary>
  private static readonly (string Basic, Func<double, double>[] Expected)[] MathGroups = [
    ("SQR(x#); LOG(x#); EXP(x# / 100); LOG(x# * 1D+40); SQR(x# * 1D-40)",
      [Math.Sqrt, Math.Log, x => Math.Exp(x / 100), x => Math.Log(x * 1E+40), x => Math.Sqrt(x * 1E-40)]),
    ("SIN(x#); COS(x#); TAN(x#); SIN(x# * 128)", [Math.Sin, Math.Cos, Math.Tan, x => Math.Sin(x * 128)]),
    ("ATN(x#); ATN(-x#)", [Math.Atan, x => Math.Atan(-x)]),
    ("x# ^ 1.5; (-x#) ^ 3; x# ^ -2", [x => Math.Pow(x, 1.5), x => Math.Pow(-x, 3), x => Math.Pow(x, -2)]),
  ];

  [TestCase(0), TestCase(1), TestCase(2), TestCase(3)]
  public void Run_GivenMathFunctions_ThenSoftFloatAgreesWithThePcToFourteenDigits(int group) {
    var (basic, functions) = MathGroups[group];
    var data = string.Join(", ", MathArguments.Select(value => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
    var output = this.Run($"""
      k = INP(&H60)
      FOR i = 1 TO {MathArguments.Length}
        READ x#
        x# = x# + k
        PRINT {basic}
      NEXT
      DATA {data}
      """);

    var lines = output.Split('\n');
    Assert.That(lines, Has.Length.EqualTo(MathArguments.Length));
    for (var i = 0; i < MathArguments.Length; ++i) {
      var x = MathArguments[i];
      var printed = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(text => double.Parse(text.Replace("D", "E"), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
      Assert.That(printed, Has.Length.EqualTo(functions.Length), lines[i]);
      for (var j = 0; j < functions.Length; ++j) {
        var expected = functions[j](x);
        Assert.That(printed[j], Is.EqualTo(expected).Within(Math.Abs(expected) * 1e-14 + 1e-300),
          $"function {j} of {x}: printed {lines[i]}");
      }
    }
  }

  [Test]
  public void Run_GivenAWholePower_ThenItIsExact() {
    var output = this.Run("""
      k = INP(&H60)
      PRINT (k + 2) ^ 10; (k + 3) ^ 20; (k - 2) ^ 5; (k + 10) ^ -3; (k + 0) ^ 0; (k + 0) ^ 3; (k + 7) ^ 0
      """);

    Assert.That(output, Is.EqualTo(" 1024  3486784401 -32  .001  1  0  1 "));
  }

  [Test]
  public void Run_GivenAMathDomainError_ThenErrorFiveEndsTheProgram() {
    var output = this.Run("""
      k = INP(&H60)
      PRINT "before"
      PRINT SQR(k - 1)
      PRINT "after"
      """);

    Assert.That(output, Does.StartWith("before\n").And.Contains("5").And.Not.Contains("after"));
  }

  [Test]
  public void Run_GivenStrings_ThenThePortableRuntimeBuildsThemOnTheC64() {
    var output = this.Run("""
      k = INP(&H60)
      a$ = "Hello" + STR$(k + 42)
      b$ = MID$(a$, 2, 3) + LEFT$(a$, 1) + RIGHT$(a$, 2)
      MID$(b$, 1, 1) = "E"
      PRINT a$; LEN(a$); b$; INSTR(a$, "lo"); UCASE$("mixed Case")
      """);

    Assert.That(output, Is.EqualTo("Hello 42 8 EllH42 4 MIXED CASE"));
  }

  [Test]
  public void Run_GivenStringChurn_ThenTheHeapReusesWhatIsFreed() {
    // two hundred concatenations through a 4 KB heap: only reuse keeps it from running out
    var output = this.Run("""
      FOR i = 1 TO 200
        c$ = c$ + CHR$(65 + i MOD 26)
        IF LEN(c$) > 30 THEN c$ = MID$(c$, 10)
      NEXT
      PRINT c$; LEN(c$)
      """);

    Assert.That(output, Is.EqualTo("QRSTUVWXYZABCDEFGHIJKLMNOPQRS 29 "));
  }

  [Test]
  public void Run_GivenAFileLeftOpenAtEnd_ThenTheReturnToBasicClosesItOntoTheDisk() {
    var disk = new Dictionary<string, List<byte>>();
    this.Run("""
      OPEN "notes.txt" FOR OUTPUT AS #1
      PRINT #1, "kept"
      END
      """, disk);

    // a lower-case name reaches the drive in PETSCII capitals, as DOS names are case-blind
    Assert.That(disk.Keys, Is.EquivalentTo(new[] { "NOTES.TXT" }));
    Assert.That(disk["NOTES.TXT"], Is.EqualTo("kept\n"u8.ToArray()));
  }

  [Test]
  public void Run_GivenAppendToAMissingFile_ThenItIsCreatedAsDosWould() {
    var disk = new Dictionary<string, List<byte>>();
    var output = this.Run("""
      OPEN "LOG.TXT" FOR APPEND AS #1
      PRINT #1, "one"
      CLOSE #1
      OPEN "log.txt" FOR APPEND AS #1
      PRINT #1, "two"
      CLOSE #1
      OPEN "LOG.TXT" FOR INPUT AS #1
      DIM s AS STRING
      DO UNTIL EOF(1)
        LINE INPUT #1, s
        PRINT s
      LOOP
      CLOSE #1
      """, disk);

    Assert.That(output, Is.EqualTo("one\ntwo"));
    Assert.That(disk["LOG.TXT"], Is.EqualTo("one\ntwo\n"u8.ToArray()));
  }

  [Test]
  public void Build_GivenARandomFile_ThenThe6502DeclinesItBecauseA1541CannotSeek() {
    var (code, error, _) = this.Build("""
      OPEN "R.DAT" FOR RANDOM AS #1 LEN = 4
      PRINT LOF(1)
      """);

    Assert.That(code, Is.Not.Zero);
    Assert.That(error, Does.Contain("cannot seek"));
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
