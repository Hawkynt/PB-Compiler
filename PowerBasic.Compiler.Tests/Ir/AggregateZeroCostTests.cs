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

  private static IrModule Optimize(string source, Dialect dialect = Dialect.Pb35) {
    // Given valid lowered IR.
    var module = Lower(source, dialect);

    // When the standard pipeline runs, verify every intermediate representation as well as the end.
    var pipeline = IrPassManager.Standard();
    pipeline.VerifyEachPass = true;
    pipeline.RunOnModule(module);

    // Then the optimized module remains structurally valid.
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    return module;
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

  [Test]
  public void AggregateSroa_GivenIndependentPackedFields_WhenRun_ThenItCreatesTypedScalarSlots() {
    // Given independent fields in one packed byte-backed TYPE.
    var module = Lower("""
      TYPE Rec
        A AS BYTE
        B AS INTEGER
        C AS LONG
      END TYPE
      PRINT Probe&(3)
      END
      FUNCTION Probe&(BYVAL x&)
        DIM r AS Rec
        r.A = 7
        r.B = 1234
        r.C = x&
        Probe& = r.A + r.B + r.C
      END FUNCTION
      """);
    var probe = module.Functions.Single(f => f.Name.Equals("Probe", StringComparison.OrdinalIgnoreCase));
    var backing = probe.AllInstructions.OfType<IrAlloca>().Single(a => a.Allocated == IrType.I8 && a.Count == 7);

    // When array SROA declines the heterogeneous buffer and aggregate SROA proves its byte regions.
    Assert.That(ScalarReplaceArrays.Run(probe), Is.Zero);
    var changes = ScalarReplaceAggregates.Run(probe);

    // Then the packed object is replaced by the actual field storage types, with no field GEP left.
    var slots = probe.AllInstructions.OfType<IrAlloca>().ToList();
    Assert.Multiple(() => {
      Assert.That(changes, Is.EqualTo(1));
      Assert.That(probe.AllInstructions.Contains(backing), Is.False);
      Assert.That(slots.Any(a => a.Allocated == IrType.U8 && a.Count == 1), Is.True);
      Assert.That(slots.Any(a => a.Allocated == IrType.I16 && a.Count == 1), Is.True);
      Assert.That(slots.Any(a => a.Allocated == IrType.I32 && a.Count == 1), Is.True);
      Assert.That(probe.AllInstructions.OfType<IrGep>(), Is.Empty);
      Assert.That(IrVerifier.Verify(probe), Is.Empty);
    });
  }

  [Test]
  public void Pipeline_GivenIndependentTypeFields_WhenOptimized_ThenTheAggregateStorageDisappearsIntoSsa() {
    // Given a local concrete TYPE whose only observations are independent fields.
    var module = Optimize("""
      TYPE Pair
        A AS LONG
        B AS LONG
      END TYPE
      PRINT Sum&(5, 8)
      END
      FUNCTION Sum&(BYVAL x&, BYVAL y&)
        DIM p AS Pair
        p.A = x&
        p.B = y&
        Sum& = p.A + p.B
      END FUNCTION
      """);
    var sum = module.Functions.Single(f => f.Name.Equals("Sum", StringComparison.OrdinalIgnoreCase));

    // When the full pipeline has run, aggregate SROA feeds the second mem2reg sweep.
    // Then neither packed backing nor field-address computation survives.
    Assert.Multiple(() => {
      Assert.That(sum.AllInstructions.OfType<IrAlloca>().Any(a => a.Count > 1), Is.False);
      Assert.That(sum.AllInstructions.OfType<IrGep>(), Is.Empty);
    });
  }

  [Test]
  public void Pipeline_GivenGenericTypeInstance_WhenOptimized_ThenMonomorphizationAddsNoRuntimeRepresentation() {
    // Given a PB 3.6 generic TYPE instantiated at LONG inside a function.
    var module = Optimize("""
      TYPE Box OF T
        V AS T
      END TYPE
      PRINT GenericProbe(7)
      END
      FUNCTION GenericProbe(BYVAL x AS LONG) AS LONG
        DIM b AS Box OF LONG
        b.V = x
        GenericProbe = b.V + 1
      END FUNCTION
      """, Dialect.Pb36);
    var probe = module.Functions.Single(f => f.Name.Equals("GenericProbe", StringComparison.OrdinalIgnoreCase));

    // When monomorphization has produced the concrete UDT and aggregate SROA sees its field.
    // Then no generic box/dictionary/descriptor/dispatch survives in executable IR.
    Assert.Multiple(() => {
      Assert.That(probe.AllInstructions.OfType<IrAlloca>().Any(a => a.Count > 1), Is.False);
      Assert.That(probe.AllInstructions.OfType<IrGep>(), Is.Empty);
      Assert.That(probe.AllInstructions.OfType<IrCall>(), Is.Empty,
        "a field-only generic TYPE needs no runtime generic helper or dispatch");
      Assert.That(module.Functions.Any(f => f.Name.Equals("Box", StringComparison.OrdinalIgnoreCase)), Is.False,
        "the generic template itself is not emitted as a runtime function");
    });
  }

  [Test]
  public void Pipeline_GivenPb36TypeAlias_WhenOptimized_ThenAliasHasNoRuntimeRepresentation() {
    // Given a source-level alias for LONG.
    var module = Optimize("""
      TYPE Meter AS LONG
      PRINT AliasProbe(9)
      END
      FUNCTION AliasProbe(BYVAL x AS LONG) AS LONG
        DIM distance AS Meter
        distance = x
        AliasProbe = distance + 1
      END FUNCTION
      """, Dialect.Pb36);
    var probe = module.Functions.Single(f => f.Name.Equals("AliasProbe", StringComparison.OrdinalIgnoreCase));

    // When binding resolves the alias and ordinary scalar promotion runs.
    // Then there is no alias object, descriptor, conversion helper, or extra storage in IR.
    Assert.Multiple(() => {
      Assert.That(probe.AllInstructions.OfType<IrAlloca>(), Is.Empty);
      Assert.That(probe.AllInstructions.OfType<IrCall>(), Is.Empty);
      Assert.That(probe.AllInstructions.OfType<IrGep>(), Is.Empty);
    });
  }

  [Test]
  public void Pipeline_GivenUnionViewsThatOverlap_WhenOptimized_ThenSharedBackingStorageIsPreserved() {
    // Given two differently-sized UNION fields that intentionally name the same bytes.
    var module = Optimize("""
      UNION Overlay
        I AS INTEGER
        L AS LONG
      END UNION
      PRINT UnionProbe(7)
      END
      FUNCTION UnionProbe(BYVAL x AS LONG) AS LONG
        DIM u AS Overlay
        u.L = x
        UnionProbe = u.I
      END FUNCTION
      """);
    var probe = module.Functions.Single(f => f.Name.Equals("UnionProbe", StringComparison.OrdinalIgnoreCase));

    // When aggregate SROA sees the overlapping i32/i16 regions.
    // Then it keeps one four-byte shared object: no invented tag, variant object, or helper call.
    Assert.Multiple(() => {
      Assert.That(probe.AllInstructions.OfType<IrAlloca>().Any(a => a.Allocated == IrType.I8 && a.Count == 4), Is.True);
      Assert.That(probe.AllInstructions.OfType<IrCall>(), Is.Empty);
      Assert.That(IrVerifier.Verify(probe), Is.Empty);
    });
  }
}
