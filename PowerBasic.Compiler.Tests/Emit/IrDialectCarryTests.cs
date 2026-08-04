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

  /// <summary>
  /// A GW-BASIC SINGLE is Microsoft Binary Format, and the IR says so. It used to refuse the program
  /// outright, which lost every BASICA and GW-BASIC program that declared a float.
  /// </summary>
  [Test]
  public void Lower_GivenAGwBasicSingle_ThenTheIrCarriesTheMbfFormat() {
    var module = Lower(_gwSingle, Dialect.Gw);

    var mbf = module.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions)
      .Any(i => i.Type.IsMbf || i.Operands.Any(o => o.Type.IsMbf));
    Assert.That(mbf, "the MBF storage format has to survive lowering, not be silently read as IEEE");
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

  /// <summary>
  /// The x86-16 back end must REFUSE the MBF value rather than compute on it: the x87 cannot read
  /// those bits, and treating mbf32 as f32 reads a different number entirely.
  /// </summary>
  [Test]
  public void Select_GivenMbfStorage_ThenTheBackEndDeclinesRatherThanMisreadIt() {
    var module = Lower(_gwSingle, Dialect.Gw);
    IrPassManager.Standard().RunOnModule(module);

    var machine = PowerBasic.Compiler.Backend.InstructionSelector.TrySelect(module.FindFunction("main")!, out var why);
    Assert.That(machine, Is.Null, "an MBF value must not reach the x87");
    Assert.That(why, Does.Contain("Microsoft Binary Format"));
  }
}
