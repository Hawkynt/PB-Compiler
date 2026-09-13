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
/// each taking the whole module body with it - on the grounds that the direct emitter answers with a
/// far entry thunk it synthesizes beside the procedure. It does not: the thunk exists so a FAR call
/// can reach a near procedure, while CODEPTR asks for the entry OFFSET, which is the procedure's own
/// label either way.
/// </para>
/// <para>
/// The selector could already name one - <c>PtrToInt</c> of an <c>IrFunction</c> becomes
/// <c>MOperand.LabelRef</c>, resolved through the same callee lookup a CALL uses - so the whole fix
/// was saying so in the lowering.
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
    IF (w AND &HFFFF&) = a THEN PRINT "low matches" ELSE PRINT "BAD LOW"
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
  /// entry offsets, neither is zero, and CODEPTR32's low half is the same number CODEPTR gives. The
  /// offsets themselves are not asserted - they move whenever anything ahead of them in the image
  /// does - but a lowering that answered 0, or the same label twice, or put the segment in the low
  /// half, fails every one of these.
  /// </summary>
  [TestCase(false)]
  [TestCase(true)]
  public void Run_GivenCodePtrOfProcedures_ThenTheOffsetsAreDistinctAndPaired(bool optimize) {
    var (output, _) = Run(optimize);

    Assert.That(output, Is.EqualTo("distinct|low matches"));
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
