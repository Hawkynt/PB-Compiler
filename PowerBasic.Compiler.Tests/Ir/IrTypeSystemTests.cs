using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// The two distinctions the BASIC family makes that LLVM's type system does not, and that the IR
/// therefore has to carry itself if a back end reading only the IR is to emit faithful code:
/// <b>signedness</b> (PB has a signed and an unsigned scalar at every width) and the <b>Microsoft
/// Binary Format</b> floats that BASICA, GW-BASIC and the BASCOM-heritage QuickBASIC releases store
/// natively. Signedness is an interpretation of the same storage; MBF is a different encoding
/// entirely, so the two behave differently under <see cref="IrType.SameStorage"/>.
/// </summary>
[TestFixture]
public sealed class IrTypeSystemTests {

  private static SemanticModel Bind(string source, Dialect dialect = Dialect.Pb35)
    => Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect), dialect);

  #region signedness

  [Test]
  public void IrType_GivenUnsignedWidth_ThenDistinctFromSignedAndInterned() {
    Assert.That(IrType.Integer(16, signed: false), Is.SameAs(IrType.U16));
    Assert.That(IrType.U16, Is.Not.EqualTo(IrType.I16));         // the type distinguishes them
    Assert.That(IrType.U16.IsUnsigned, Is.True);
    Assert.That(IrType.I16.IsUnsigned, Is.False);
    Assert.That(IrType.I16.WithSign(signed: false), Is.SameAs(IrType.U16));
    Assert.That(IrType.U32.ToString(), Is.EqualTo("u32"));
    Assert.That(IrType.I32.ToString(), Is.EqualTo("i32"));
  }

  [Test]
  public void IrType_GivenSameWidthDifferentSign_ThenStorageCompatible() {
    // signedness says how the bits are READ (sdiv vs udiv, slt vs ult, sext vs zext), not what they
    // are - so a phi, a store or a binary operand pair may mix them
    Assert.That(IrType.U16.SameStorage(IrType.I16), Is.True);
    Assert.That(IrType.U16.SameStorage(IrType.I32), Is.False);
  }

  [Test]
  public void TypeMapper_GivenSignedAndUnsignedScalars_ThenSignednessSurvives() {
    var model = Bind("""
      DIM i AS INTEGER
      DIM w AS WORD
      DIM l AS LONG
      DIM d AS DWORD
      DIM b AS BYTE
      """);
    IrType Map(string name) => IrTypeMapper.Map(model.ModuleVariables[name].Type);

    Assert.That(Map("i"), Is.EqualTo(IrType.I16));
    Assert.That(Map("w"), Is.EqualTo(IrType.U16));
    Assert.That(Map("l"), Is.EqualTo(IrType.I32));
    Assert.That(Map("d"), Is.EqualTo(IrType.U32));
    Assert.That(Map("b"), Is.EqualTo(IrType.U8));
  }

  #endregion

  #region Microsoft Binary Format

  [Test]
  public void IrType_GivenMbf_ThenDistinctEncodingFromIeee() {
    Assert.That(IrType.Floating(32, IrFloatFormat.Mbf), Is.SameAs(IrType.Mbf32));
    Assert.That(IrType.Mbf32, Is.Not.EqualTo(IrType.F32));
    Assert.That(IrType.Mbf32.IsMbf, Is.True);
    Assert.That(IrType.Mbf32.IsIeeeFloat, Is.False);
    Assert.That(IrType.F32.IsIeeeFloat, Is.True);
    Assert.That(IrType.Mbf32.ToString(), Is.EqualTo("mbf32"));
    Assert.That(IrType.Mbf64.ToString(), Is.EqualTo("mbf64"));
  }

  [Test]
  public void IrType_GivenMbfAndIeeeOfSameWidth_ThenNotStorageCompatible() {
    // unlike signedness, the encodings genuinely differ (exponent bias, explicit sign layout),
    // so moving between them is a conversion, never a reinterpretation
    Assert.That(IrType.Mbf32.SameStorage(IrType.F32), Is.False);
    Assert.That(IrType.Mbf32.SameStorage(IrType.Mbf32), Is.True);
  }

  [Test]
  public void TypeMapper_GivenGwBasicSingle_ThenMapsToMbf32() {
    var model = Bind("DIM x AS SINGLE", Dialect.Gw);
    var x = model.ModuleVariables["x"];

    Assert.That(x.Type, Is.InstanceOf<MbfType>(), "GW-BASIC stores SINGLE in MBF");
    Assert.That(IrTypeMapper.Map(x.Type), Is.EqualTo(IrType.Mbf32));
  }

  [Test]
  public void TypeMapper_GivenPb35Single_ThenStaysIeee() {
    var model = Bind("DIM x AS SINGLE");
    var x = model.ModuleVariables["x"];

    Assert.That(IrTypeMapper.Map(x.Type), Is.EqualTo(IrType.F32));
  }

  #endregion

  #region verifier

  private static IrFunction FunctionWith(System.Action<IrBuilder, IrBasicBlock> body, IrType ret) {
    var fn = new IrFunction("f", ret, []);
    var entry = fn.CreateBlock("entry");
    var b = new IrBuilder(entry);
    body(b, entry);
    return fn;
  }

  [Test]
  public void Verifier_GivenArithmeticOnMbf_ThenReportsStorageOnly() {
    var fn = FunctionWith((b, _) => {
      var slot = b.Alloca(IrType.Mbf32);
      var value = b.Load(IrType.Mbf32, slot);
      b.Ret(b.Binary(IrBinaryOp.FAdd, value, value));            // MBF is storage - the x87 cannot add it
    }, IrType.Mbf32);

    var errors = IrVerifier.Verify(fn);

    Assert.That(errors, Has.Some.Contains("Microsoft Binary Format"), string.Join("; ", errors));
  }

  [Test]
  public void Verifier_GivenMbfConversionCasts_ThenAccepted() {
    var fn = FunctionWith((b, _) => {
      var slot = b.Alloca(IrType.Mbf32);
      var stored = b.Load(IrType.Mbf32, slot);
      var ieee = b.Cast(IrCastOp.MbfToFP, stored, IrType.F32);        // load converts
      var sum = b.Binary(IrBinaryOp.FAdd, ieee, ieee);                                    // arithmetic is IEEE
      b.Store(b.Cast(IrCastOp.FPToMbf, sum, IrType.Mbf32), slot);      // store converts back
      b.Ret(null);
    }, IrType.Void);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Verifier_GivenIeeeCastFromMbf_ThenRejected() {
    var fn = FunctionWith((b, _) => {
      var slot = b.Alloca(IrType.Mbf32);
      var stored = b.Load(IrType.Mbf32, slot);
      b.Ret(b.Cast(IrCastOp.FPExt, stored, IrType.F64));   // fpext does not know the MBF encoding
    }, IrType.F64);

    Assert.That(IrVerifier.Verify(fn), Has.Some.Contains("invalid cast"));
  }

  [Test]
  public void Verifier_GivenSignedAndUnsignedOperands_ThenAccepted() {
    // WORD and INTEGER share storage; the op (udiv/sdiv, ult/slt) carries the reading
    var fn = FunctionWith((b, _) => {
      var s = b.Alloca(IrType.I16);
      var u = b.Alloca(IrType.U16);
      b.Ret(b.Add(b.Load(IrType.I16, s), b.Load(IrType.U16, u)));
    }, IrType.I16);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  #endregion

  #region back ends

  [Test]
  public void Lowering_GivenGwBasicSingle_ThenDeclinesRatherThanTreatMbfAsIeee() {
    var model = Bind("""
      DIM x AS SINGLE
      x = 1.5
      PRINT x
      """, Dialect.Gw);

    // the IR can express mbf32, but the lowering does not emit the load/store conversions yet -
    // declining is correct; treating the bits as IEEE would be a miscompile
    Assert.That(IrLowering.TryLowerModule(model), Is.Null);
  }

  [Test]
  public void Emitters_GivenMbfType_ThenRefuseRatherThanRenderAsIeee() {
    var fn = FunctionWith((b, _) => b.Ret(b.Load(IrType.Mbf32, b.Alloca(IrType.Mbf32))), IrType.Mbf32);
    var module = new IrModule("m");
    module.AddFunction(fn);

    Assert.That(() => LlvmEmitter.Emit(module), Throws.TypeOf<NotSupportedException>());
    Assert.That(() => CEmitter.Emit(module), Throws.TypeOf<NotSupportedException>());
  }

  #endregion
}
