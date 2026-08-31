using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// Zero-cost aggregate lowering invariants.
///
/// Source-level TYPE and generic TYPE abstractions may disappear when their storage does not escape,
/// but an optimization must first prove that the memory it splits really consists of independent
/// scalar regions. In particular, packed UDT backing storage is an <c>alloca i8, N</c> too; it is not
/// therefore an N-element BYTE array.
/// </summary>
[TestFixture]
public sealed class AggregateZeroCostTests {

  private static IrModule Lower(string source, Dialect dialect = Dialect.Pb35) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect);
    var model = Binder.Bind(unit, dialect);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    return module!;
  }

  [Test]
  public void ArraySroa_GivenPackedRecordWithWiderField_WhenRun_ThenItDoesNotTreatBytesAsArrayElements() {
    // Given a three-byte packed record. The INTEGER at byte offset one occupies two bytes; replacing
    // that pointer with a one-byte scalar slot would shrink its storage while opaque pointers hide
    // the mistake from the verifier.
    var module = Lower("""
      TYPE Rec
        A AS BYTE
        B AS INTEGER
      END TYPE
      PRINT Probe%()
      END
      FUNCTION Probe%()
        DIM r AS Rec
        r.B = 1234
        Probe% = r.B
      END FUNCTION
      """);
    var probe = module.Functions.Single(f => f.Name.Equals("Probe", StringComparison.OrdinalIgnoreCase));
    var backing = probe.AllInstructions.OfType<IrAlloca>().Single(a => a.Allocated == IrType.I8 && a.Count == 3);

    // When the small-array SROA considers the function.
    var changes = ScalarReplaceArrays.Run(probe);

    // Then packed aggregate storage is outside that pass's contract and remains intact.
    Assert.Multiple(() => {
      Assert.That(changes, Is.Zero);
      Assert.That(probe.AllInstructions.Contains(backing), Is.True);
      Assert.That(probe.AllInstructions.OfType<IrGep>(), Is.Not.Empty);
      Assert.That(IrVerifier.Verify(probe), Is.Empty);
    });
  }
}
