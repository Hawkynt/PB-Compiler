using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// <c>PLAY(n)</c> - how many notes are still queued for background music.
///
/// It bound and generated nothing, and the intrinsic census listed it as a decision rather than a
/// piece of work: there was no statement of what it should answer. The oracle supplied one. PBC 3.5
/// prints 0 before anything plays and 4 after <c>PLAY "MBT120L4CDEF"</c> queues four notes, and it
/// ignores the argument - <c>PLAY(0)</c> and <c>PLAY(1)</c> agree.
///
/// Zero is the truthful answer HERE, and for a reason worth stating: the PLAY STATEMENT is a no-op
/// in this runtime, so nothing is ever queued and nothing can ever be pending. That does diverge
/// from genuine PowerBASIC for background music, but the divergence belongs to the unimplemented
/// statement rather than to this function - so the binder warns instead of answering quietly, which
/// is exactly what the census was written to prevent.
/// </summary>
[TestFixture]
public sealed class PlayFunctionTests {

  private static (string Output, IReadOnlyList<string> Warnings) Compile(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35), Dialect.Pb35);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return (Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|"), [.. model.Warnings.Select(w => w.Message)]);
  }

  /// <summary>It generates code now - the census's "binds but emits nothing" no longer holds.</summary>
  [Test]
  public void Play_GivenAQueueQuery_ThenItAnswersZero() =>
    Assert.That(Compile("PRINT PLAY(0)\nEND\n").Output, Is.EqualTo("0"));

  /// <summary>The argument is ignored, as it is in PBC 3.5.</summary>
  [TestCase("PRINT PLAY(0)")]
  [TestCase("PRINT PLAY(1)")]
  [TestCase("PRINT PLAY(255)")]
  public void Play_GivenAnyArgument_ThenTheAnswerIsTheSame(string body) =>
    Assert.That(Compile(body + "\nEND\n").Output, Is.EqualTo("0"));

  /// <summary>
  /// And it says so. Returning 0 silently would hide that this runtime has no queue at all - the
  /// failure mode the intrinsic census exists to catch.
  /// </summary>
  [Test]
  public void Play_WhenBound_ThenItWarnsThatThereIsNoQueue() =>
    Assert.That(Compile("PRINT PLAY(0)\nEND\n").Warnings,
      Has.Some.Contains("background-music queue this runtime does not have"));

  /// <summary>Still zero after a PLAY statement, because that statement queues nothing here.</summary>
  [Test]
  public void Play_AfterAPlayStatement_ThenItIsStillZero() =>
    Assert.That(Compile("PLAY \"MBT120L4CDEF\"\nPRINT PLAY(0)\nEND\n").Output, Is.EqualTo("0"));
}
