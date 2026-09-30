using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// PB's non-local jumps - <c>TRY</c>/<c>CATCH</c>/<c>FINALLY</c>, <c>ON ERROR</c> across a procedure
/// that arms its own handler, and <c>EXIT FAR</c> - on the targets that are not DOS: x86-32 and x64
/// (the static ELF, run) and the 6502 (the <c>.PRG</c>, run on <see cref="Cpu6502"/>). Each program's
/// expected output is DOS's, and the <c>dos</c> column proves it by compiling the same program for
/// DOS and running it under DOSBox, when DOSBox is there.
/// </summary>
[TestFixture]
public sealed class FlatTargetNonLocalJumpTests {

  private static readonly (string Name, string Source, string Expected)[] _programs = [
    ("try-no-error", """
      TRY
        PRINT "body"
      CATCH
        PRINT "catch"
      FINALLY
        PRINT "fin"
      END TRY
      PRINT "after"
      """, "body\nfin\nafter"),
    ("try-catch-finally", """
      TRY
        ERROR 5
        PRINT "unreached"
      CATCH
        PRINT "catch"; ERR
      FINALLY
        PRINT "fin"
      END TRY
      PRINT "after"
      """, "catch 5\nfin\nafter"),
    ("nested-try", """
      TRY
        PRINT "outerbody"
        TRY
          ERROR 9
        CATCH
          PRINT "innercatch"; ERR
        FINALLY
          PRINT "innerfin"
        END TRY
        PRINT "outerresume"
      CATCH
        PRINT "outercatch"
      FINALLY
        PRINT "outerfin"
      END TRY
      PRINT "after"
      """, "outerbody\ninnercatch 9\ninnerfin\nouterresume\nouterfin\nafter"),
    ("finally-reraises", """
      ON ERROR GOTO Trap
      TRY
        ERROR 11
      FINALLY
        TRY
          ERROR 5
        CATCH
          PRINT "inner"; ERR
        END TRY
        PRINT "fin"
      END TRY
      PRINT "unreached"
      GOTO Done
      Trap:
        PRINT "trap"; ERR
      Done:
      PRINT "done"
      """, "inner 5\nfin\ntrap 11\ndone"),
    ("try-restores-on-error", """
      ON ERROR GOTO Trap
      TRY
        PRINT "trybody"
      CATCH
        PRINT "trycatch"
      END TRY
      ERROR 13
      PRINT "unreached"
      GOTO Done
      Trap:
        PRINT "trap"; ERR
      Done:
      PRINT "done"
      """, "trybody\ntrap 13\ndone"),
    ("procedure-keeps-callers-handler", """
      DECLARE SUB Guarded()
      ON ERROR GOTO Outer
      Guarded
      ERROR 6
      PRINT "unreached"
      GOTO Done
      Outer:
        PRINT "outer"; ERR
      Done:
      PRINT "done"
      END
      SUB Guarded()
        ON ERROR GOTO Inner
        ERROR 5
        PRINT "resumed"
        EXIT SUB
      Inner:
        PRINT "inner"; ERR
        RESUME NEXT
      END SUB
      """, "inner 5\nresumed\nouter 6\ndone"),
    ("exit-far-nested", """
      DECLARE SUB Noisy(BYVAL n%)
      EXIT FAR AT Unwound
      Noisy 3
      PRINT "not reached"
      Unwound:
      PRINT "unwound"
      END
      SUB Noisy(BYVAL n%)
        IF n% = 0 THEN
          EXIT FAR
        END IF
        Noisy n% - 1
        PRINT "after"; n%
      END SUB
      """, "unwound"),
    ("exit-far-loop", """
      DECLARE SUB Counter()
      EXIT FAR AT Done
      Counter
      PRINT "not reached"
      Done:
      PRINT "done"
      END
      SUB Counter()
        FOR i% = 1 TO 10
          PRINT "i="; i%
          IF i% = 3 THEN EXIT FAR
        NEXT i%
        PRINT "loop finished"
      END SUB
      """, "i= 1\ni= 2\ni= 3\ndone"),
  ];

  private static IEnumerable<TestCaseData> Cases()
    => from platform in FlatTargets.Platforms
       from program in _programs
       select new TestCaseData(platform, program.Name).SetName($"Run_GivenANonLocalJump_ThenItMatchesDos({platform}, {program.Name})");

  [TestCaseSource(nameof(Cases))]
  public void Run_GivenANonLocalJump_ThenItMatchesDos(string platform, string name) {
    var (_, source, expected) = _programs.Single(program => program.Name == name);

    var output = FlatTargets.Run(platform, source);

    Assert.That(Vice.Normalize(output), Is.EqualTo(expected));
  }
}
