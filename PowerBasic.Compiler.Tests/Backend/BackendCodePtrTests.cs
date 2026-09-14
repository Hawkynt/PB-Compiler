using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// <c>CODEPTR</c> / <c>CODEPTR32</c> of a PROCEDURE.
///
/// <para>
/// Of a LABEL these already lowered. Of a procedure they declined - 15 times over the SVGA corpus,
/// each taking the whole module body with it.
/// </para>
/// <para>
/// The two spellings answer with DIFFERENT addresses, and have to. <c>CODEPTR</c> is the procedure's
/// own entry, which is what a near transfer needs. <c>CODEPTR32</c> is a far pointer, and the only
/// thing a far call may land on is the ENTRY THUNK - a near procedure's <c>RET</c> pops one word
/// where the far call pushed two, and the thunk is what reconciles them. The direct emitter has
/// always synthesized one here; a routed <c>CODEPTR32</c> that answered with the near entry was a
/// number that looked right and returned to nowhere the moment <c>CALL DWORD</c> used it.
/// </para>
/// <para>
/// The selector could already name either - <c>PtrToInt</c> of an <c>IrFunction</c> or an
/// <c>IrFarEntry</c> becomes <c>MOperand.LabelRef</c>, resolved through the same callee lookup a CALL
/// uses - so the whole fix was saying which in the lowering.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendCodePtrTests {

  private static readonly string _source = """
    DECLARE SUB Alpha()
    DECLARE SUB Beta()
    DIM a AS WORD, b AS WORD, w AS DWORD
    a = CODEPTR(Alpha)
    b = CODEPTR(Beta)
    w = CODEPTR32(Alpha)
    IF a <> 0 AND b <> 0 AND a <> b THEN PRINT "distinct" ELSE PRINT "BAD"
    IF (w AND &HFFFF&) <> 0 AND (w AND &HFFFF&) <> a THEN PRINT "far entry" ELSE PRINT "BAD FAR"
    CALL DWORD w BDECL()
    END
    SUB Alpha()
      PRINT "a"
    END SUB
    SUB Beta()
      PRINT "b"
    END SUB
    """;

  private static (string Output, IEnumerable<string> Routed) Run(bool optimize) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(_source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return (Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|"), generator.BackendRoutedNames.ToList());
  }

  /// <summary>
  /// What is asserted is what an ADDRESS can promise: two different procedures have two different
  /// entry offsets, neither is zero, and CODEPTR32's low half is a third address again - the far
  /// entry, not the near one. The offsets themselves are not asserted (they move whenever anything
  /// ahead of them in the image does), but a lowering that answered 0, or the same label twice, or
  /// put the segment in the low half, fails one of these.
  ///
  /// <para>
  /// The <c>CALL DWORD</c> is the assertion that matters: it is the only thing the value is FOR, and
  /// it is what the near entry cannot satisfy. Calling that one far leaves the return segment on the
  /// stack and returns into whatever the caller last pushed, so this line either prints "a" or does
  /// not come back at all.
  /// </para>
  /// </summary>
  [TestCase(false)]
  [TestCase(true)]
  public void Run_GivenCodePtrOfProcedures_ThenTheFarEntryDiffersAndIsCallable(bool optimize) {
    var (output, _) = Run(optimize);

    Assert.That(output, Is.EqualTo("distinct|far entry|a"));
  }

  /// <summary>
  /// The premise: before this the module body declined, and the answers above would have been the
  /// direct emitter's - correct, and proving nothing about this back end.
  /// </summary>
  [Test]
  public void Route_GivenCodePtrOfAProcedure_ThenTheModuleBodyIsTakenByTheBackEnd() {
    var (_, routed) = Run(optimize: false);

    Assert.That(routed, Does.Contain("main"), "a body using CODEPTR of a procedure must route now");
  }
}
