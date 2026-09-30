using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Portable-runtime entries whose answers are the DOS runtime's by definition - RND's sequence,
/// ROUND, how an EXT prints, TAB and INPUT in a file - on x86-32, x64, the 6502 and DOS, each held
/// to DOS's output (<see cref="FlatTargets"/>).
/// </summary>
[TestFixture]
public sealed class FlatTargetRuntimeTests {

  private static readonly (string Name, string Source, string Expected)[] _programs = [
    ("rnd-sequence", """
      FOR i% = 1 TO 4
        PRINT RND
      NEXT
      RANDOMIZE 7
      PRINT RND; RND(1, 6); RND(1, 6); RND(-3, 3)
      """, " .08938599\n .8693542\n .36026\n .1998291\n .5970459  2  2  1"),
    ("round-and-ext", """
      DIM e AS EXT
      e = 2 / 3
      PRINT e; ROUND(2.5, 0); ROUND(-2.5, 0); ROUND(1.2345, 2)
      """, " .666666666666667  3 -3  1.23"),
    ("file-ext-tab-input", """
      DIM e AS EXT, n%
      e = 1 / 7
      OPEN "T.TXT" FOR OUTPUT AS #1
      PRINT #1, "a"; TAB(6); "b"; e
      PRINT #1, "c"; SPC(3); "d"
      PRINT #1, "word,"; 42
      CLOSE #1
      OPEN "T.TXT" FOR INPUT AS #1
      LINE INPUT #1, s$
      LINE INPUT #1, t$
      INPUT #1, w$, n%
      CLOSE #1
      KILL "T.TXT"
      PRINT s$
      PRINT t$
      PRINT w$; n% + 1
      """, "a    b .142857142857143\nc   d\nword 43"),
  ];

  private static IEnumerable<TestCaseData> Cases()
    => from platform in FlatTargets.Platforms
       from program in _programs
       select new TestCaseData(platform, program.Name).SetName($"Run_GivenARuntimeEntry_ThenItMatchesDos({platform}, {program.Name})");

  [TestCaseSource(nameof(Cases))]
  public void Run_GivenARuntimeEntry_ThenItMatchesDos(string platform, string name) {
    var (_, source, expected) = _programs.Single(program => program.Name == name);

    Assert.That(Vice.Normalize(FlatTargets.Run(platform, source)), Is.EqualTo(expected));
  }
}
