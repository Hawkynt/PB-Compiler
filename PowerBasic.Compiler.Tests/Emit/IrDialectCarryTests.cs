using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Emit;

/// <summary>
/// Dialect facts the IR carries, and what the pb35 renderer does with the ones pb35 has no spelling
/// for.
///
/// The rule these tests hold: a property the source dialect has is <b>recorded in the IR</b>, and a
/// back end that cannot honour it either declines on it or drops it and SAYS SO. What must never
/// happen is the third thing - carrying on as though the property were never there.
/// </summary>
[TestFixture]
public sealed class IrDialectCarryTests {

  private static IrModule Lower(string source, Dialect dialect) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect), dialect);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    return module!;
  }

  private const string _gwSingle = """
    10 A! = 1.5
    20 B! = A! * 2.0
    30 PRINT B!
    40 END
    """;

  private static readonly Dialect[] _allDialects = Enum.GetValues<Dialect>();

  /// <summary>
  /// Given any advertised dialect, when its program enters the middle end, then the module retains
  /// the runtime contract a detached back end needs instead of flattening everything to pb35.
  /// </summary>
  [TestCaseSource(nameof(_allDialects))]
  public void Lower_GivenAnyAdvertisedDialect_ThenTheModuleCarriesIt(Dialect dialect) {
    var module = Lower("10 A% = 42\n20 PRINT A%\n30 END\n", dialect);

    Assert.That(module.Dialect, Is.EqualTo(dialect));
    Assert.That(module.EffectiveDialect, Is.EqualTo(dialect));
  }

  /// <summary>A $COMPAT source keeps the compile dialect and runtime dialect as separate facts.</summary>
  [Test]
  public void Lower_GivenACompatDirective_ThenTheModuleCarriesTheEffectiveDialect() {
    var module = Lower("$COMPAT qb45\nA% = 42\nPRINT A%\nEND\n", Dialect.Pb35);

    Assert.That(module.Dialect, Is.EqualTo(Dialect.Pb35));
    Assert.That(module.EffectiveDialect, Is.EqualTo(Dialect.Qb45));
  }

  /// <summary>
  /// Given a non-pb35 module, when it is rendered back to BASIC, then the recompile retains the
  /// source runtime rules through $COMPAT (including formatting and the QB1-3 close marker).
  /// </summary>
  [Test]
  public void Write_GivenAQuickBasicModule_ThenTheRoundTripKeepsItsRuntimeDialect() {
    var rendered = IrBasicWriter.Write(Lower("10 A% = 42\n20 PRINT A%\n30 END\n", Dialect.Qb10));
    var rebound = Binder.Bind(
      Parser.Parse(Lexer.Tokenize(rendered, "RT.BAS", Dialect.Pb35), "RT.BAS", Dialect.Pb35),
      Dialect.Pb35);

    Assert.That(rendered, Does.StartWith("$COMPAT qb10"));
    Assert.That(rebound.Errors, Is.Empty, "rendered: " + rendered);
    Assert.That(rebound.EffectiveDialect, Is.EqualTo(Dialect.Qb10));
  }

  /// <summary>pb35 is the target spelling already, so its IR round-trip needs no compatibility shim.</summary>
  [Test]
  public void Write_GivenAPb35Module_ThenItDoesNotAddACompatDirective() {
    var rendered = IrBasicWriter.Write(Lower("A% = 42\nPRINT A%\nEND\n", Dialect.Pb35));

    Assert.That(rendered, Does.Not.Contain("$COMPAT"));
  }

  /// <summary>
  /// A GW-BASIC SINGLE is Microsoft Binary Format, and the IR says so. It used to refuse the program
  /// outright, which lost every BASICA and GW-BASIC program that declared a float.
  /// </summary>
  [Test]
  public void Lower_GivenAGwBasicSingle_ThenTheIrCarriesTheMbfFormat() {
    var module = Lower(_gwSingle, Dialect.Gw);
    var instructions = module.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions).ToList();

    Assert.Multiple(() => {
      Assert.That(instructions.Any(i => i.Type.IsMbf || i.Operands.Any(o => o.Type.IsMbf)),
        "the MBF storage format has to survive lowering, not be silently read as IEEE");
      Assert.That(instructions.OfType<IrBinary>().Select(i => i.Op), Does.Contain(IrBinaryOp.FMul),
        "an MBF cell is numeric storage, not an integer expression");
      Assert.That(instructions.OfType<IrCast>().Select(i => i.Op), Does.Not.Contain(IrCastOp.FPToSIRound));
    });
  }

  /// <summary>
  /// MBF64 carries 56 significant bits, three more than IEEE64. Its storage conversions therefore
  /// use x87 extended as the compute-side type; an f64 bridge would lose bits before the MBF store
  /// had a chance to apply its own rounding.
  /// </summary>
  [Test]
  public void Lower_GivenAGwBasicDouble_ThenItsMbfConversionsPreserveTheFullPrecision() {
    var module = Lower("10 A# = 1#\n20 B# = 36028797018963968#\n30 X# = A# + A# / B#\n40 END\n", Dialect.Gw);
    var instructions = module.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions).ToList();
    var casts = instructions.OfType<IrCast>().ToList();
    var arithmetic = instructions.OfType<IrBinary>().Where(i => i.Type.IsFloat).ToList();

    Assert.Multiple(() => {
      Assert.That(casts.Where(c => c.Op == IrCastOp.MbfToFP).Select(c => c.Type),
        Is.Not.Empty.And.All.EqualTo(IrType.F80));
      Assert.That(casts.Where(c => c.Op == IrCastOp.FPToMbf).Select(c => c.Value.Type),
        Is.Not.Empty.And.All.EqualTo(IrType.F80));
      Assert.That(arithmetic.Select(i => i.Type), Is.Not.Empty.And.All.EqualTo(IrType.F80));
      Assert.That(arithmetic.Select(i => i.Op), Does.Contain(IrBinaryOp.FAdd).And.Contain(IrBinaryOp.FDiv));
      Assert.That(casts.Select(c => c.Op), Does.Not.Contain(IrCastOp.FPToSIRound));
    });
  }

  /// <summary>The same program under a dialect with IEEE floats must NOT be marked MBF.</summary>
  [Test]
  public void Lower_GivenAPowerBasicSingle_ThenNothingIsMarkedMbf() {
    var module = Lower("a! = 1.5\nb! = a! * 2.0\nPRINT b!\nEND\n", Dialect.Pb36);

    Assert.That(module.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions)
      .Any(i => i.Type.IsMbf), Is.False);
  }

  /// <summary>
  /// pb35 has no MBF, so the rendered program stores IEEE - and the rendering says so in the text
  /// rather than leaving the reader to discover that the storage layout changed under them.
  /// </summary>
  [Test]
  public void Write_GivenMbfStorage_ThenItIsDroppedWithAStatedWarning() {
    var rendered = IrBasicWriter.Write(Lower(_gwSingle, Dialect.Gw), out var warnings);

    Assert.That(warnings, Has.Some.Contains("Microsoft Binary Format"));
    Assert.That(rendered, Does.Contain("' WARNING:"), "the text carries the warning too");
    Assert.That(rendered, Does.Contain("SINGLE"), "and the storage becomes an ordinary IEEE SINGLE");
  }

  /// <summary>What it renders is still a program the pb35 front end accepts.</summary>
  [Test]
  public void Write_GivenMbfStorage_ThenTheRenderedTextStillBindsAsPb35() {
    var rendered = IrBasicWriter.Write(Lower(_gwSingle, Dialect.Gw));
    var back = Binder.Bind(Parser.Parse(Lexer.Tokenize(rendered, "RT.BAS", Dialect.Pb35), "RT.BAS", Dialect.Pb35), Dialect.Pb35);

    Assert.That(back.Errors, Is.Empty, "rendered: " + rendered);
  }

  private const string _rounding = """
    10 A% = CINT(2.5)
    20 B% = CINT(-2.5)
    30 C% = CINT(3.5)
    40 PRINT A%; B%; C%
    50 END
    """;

  /// <summary>
  /// WHICH rounding is a dialect fact. QuickBASIC 1.0 to 3.0 round half AWAY from zero - CINT(2.5) is
  /// 3 - where QB 4.x and PowerBASIC take the FPU's round-half-to-even, which gives 2. Flattening both
  /// into one cast made every QB 1-3 program round the pb35 way once it went through the IR.
  /// </summary>
  [Test]
  public void Lower_GivenABascomDialect_ThenTheRoundingModeIsCarriedAsItsOwnCall() {
    var calls = Lower(_rounding, Dialect.Qb10).Functions.SelectMany(f => f.Blocks)
      .SelectMany(b => b.Instructions).OfType<IrCall>()
      .Select(c => (c.Callee as IrFunction)?.Name).ToList();

    Assert.That(calls, Does.Contain("rt_round_half_away"));
  }

  /// <summary>A dialect that rounds half to even keeps the plain cast - no call, no extra code.</summary>
  [Test]
  public void Lower_GivenPowerBasic_ThenTheOrdinaryRoundingCastIsUsed() {
    var module = Lower("a% = CINT(2.5)\nPRINT a%\nEND\n", Dialect.Pb36);
    var body = module.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions).ToList();

    Assert.That(body.OfType<IrCall>().Select(c => (c.Callee as IrFunction)?.Name),
      Does.Not.Contain("rt_round_half_away"));
  }

  /// <summary>
  /// pb35 has no half-away rounding, so reproducing the source dialect means WRITING IT OUT rather
  /// than adopting the target's rule - which is the whole reason the mode is carried as a call.
  /// </summary>
  [Test]
  public void Write_GivenBascomRounding_ThenItIsExpandedIntoArithmeticThatReproducesIt() {
    var rendered = IrBasicWriter.Write(Lower(_rounding, Dialect.Qb10), out var warnings);

    Assert.That(rendered, Does.Contain("SGN(").And.Contain("INT("), "the half-away rule is written out");
    Assert.That(warnings, Has.Some.Contains("half away from zero"));
    var back = Binder.Bind(Parser.Parse(Lexer.Tokenize(rendered, "RT.BAS", Dialect.Pb35), "RT.BAS", Dialect.Pb35), Dialect.Pb35);
    Assert.That(back.Errors, Is.Empty, "rendered: " + rendered);
  }

  /// <summary>
  /// The x86-16 back end must convert MBF at the cell boundary rather than compute on its bits: the
  /// x87 cannot read the storage encoding, and treating mbf32 as f32 reads another number entirely.
  /// </summary>
  [Test]
  public void Select_GivenMbfStorage_ThenTheBackEndUsesTheConversionRoutines() {
    var module = Lower(_gwSingle, Dialect.Gw);
    IrMiddleEndPipeline.Standard().RunOnModule(module);

    var machine = PowerBasic.Compiler.Backend.InstructionSelector.TrySelect(module.FindFunction("main")!, out var why);
    Assert.That(machine, Is.Not.Null, why);
    var calls = machine!.AllInstructions.Where(i => i.Opcode == PowerBasic.Compiler.Backend.MOpcode.Call)
      .Select(i => ((PowerBasic.Compiler.Backend.MOperand.LabelRef)i.Operands[0]).Name).ToList();

    Assert.Multiple(() => {
      Assert.That(why, Is.Null);
      Assert.That(calls, Does.Contain("rt_mbfst"));
      Assert.That(calls, Does.Contain("rt_mbfld"));
    });
  }
}
