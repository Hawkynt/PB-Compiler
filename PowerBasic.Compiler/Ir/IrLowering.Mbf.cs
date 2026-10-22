using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Ir;

public sealed partial class IrLowering {

  private PbType GetFloatComputationType(PbType declaredType)
    => this._model.EffectiveDialect.HasMbfStorage() && declaredType is ScalarType { IsFloat: true } or MbfType
      ? PbType.Ext : declaredType;

  private static PbType GetDisplayType(PbType storageType)
    => storageType is MbfType mbf ? mbf.IsDouble ? PbType.Double : PbType.Single : Valued(storageType);

  private IrAlloca? _mbfSingleGuard;

  /// <summary>Microsoft's $ROUNS retains three guard bits before its nearest/even SINGLE rounding.</summary>
  private IrValue RoundInterpreterSingleArithmetic(IrValue value) {
    this._mbfSingleGuard ??= this._entry.InsertAt(this._entryAllocaCount++,
      new IrAlloca(IrType.F80) { Name = "mbf.single.guard" });
    this._b.Store(value, this._mbfSingleGuard);
    // MATH2.ASM clears AH's low five bits before rounding its 24-bit significand. Express the
    // corresponding 37 discarded x87 bits as memory effects, so every backend sees the rule.
    this._b.Store(new IrConstantInt(IrType.I32, 0), this._mbfSingleGuard);
    var guard = this._b.Gep(this._mbfSingleGuard, new IrConstantInt(IrType.I16, 4), IrType.I8);
    this._b.Store(this._b.And(this._b.Load(IrType.I8, guard), new IrConstantInt(IrType.I8, 0xE0)), guard);
    return this.RoundInterpreterFloat(this._b.Load(IrType.F80, this._mbfSingleGuard), PbType.Single);
  }

  private IrValue LowerFloatForStep(Hir.HirForStatement loop, VariableSymbol counter) {
    if (loop.Step is null && counter.Type is not MbfType)
      return new IrConstantFloat(MapType(counter.Type), 1);
    var sourceType = loop.Step is null ? PbType.Ext : this._model.TypeOf(loop.Step);
    var value = loop.Step is null
      ? (IrValue)new IrConstantFloat(IrType.F80, 1)
      : this.LowerExpr(loop.Step);
    if (counter.Type is not MbfType mbf)
      return this.Coerce(value, sourceType, counter.Type);
    return mbf.IsDouble
      ? this.RoundInterpreterFloat(this.Coerce(value, sourceType, PbType.Ext), PbType.Double)
      : this.RoundMbfSingleConversion(value, sourceType);
  }

  /// <summary>A local function returns an extended value; its source expression still has MBF storage precision.</summary>
  private IrValue CaptureMbfReturn(IrValue value, MbfType storageType) {
    var type = MapType(storageType);
    var slot = this._entry.InsertAt(this._entryAllocaCount++,
      new IrAlloca(type) { Name = "mbf.return" });
    this._b.Store(this.Coerce(value, PbType.Ext, storageType), slot);
    return this._b.Load(type, slot);
  }

  /// <summary>GW conversion ties go away from zero; default QuickBASIC 1-3 conversion ties go to even.</summary>
  private IrValue RoundMbfSingleConversion(IrValue value, PbType sourceType)
    => this.RoundInterpreterFloat(this._model.EffectiveDialect.IsGwBasica()
      ? this.NarrowInterpreterSingle(value, sourceType)
      : this.Coerce(value, sourceType, PbType.Ext), PbType.Single);

  /// <summary>MKS$/MKD$ serialize the dialect's storage format, not an IEEE approximation.</summary>
  private IrValue EncodeMbfString(CallOrIndexExpr call, bool isDouble) {
    var sourceType = this._model.TypeOf(call.Arguments[0]);
    var storageType = new MbfType(isDouble);
    var value = this.LowerExpr(call.Arguments[0]);
    var encoded = sourceType == storageType ? value : this.Coerce(value, sourceType, storageType);
    var slot = this._entry.InsertAt(this._entryAllocaCount++,
      new IrAlloca(MapType(storageType)) { Name = "mbf.encode" });
    this._b.Store(encoded, slot);
    return this._b.Call(IrType.Ptr,
      this.RuntimeFn("rt_str_from_fixed", IrType.Ptr, IrType.Ptr, IrType.I32),
      slot, new IrConstantInt(IrType.I32, storageType.Size));
  }

  /// <summary>CVS/CVD read raw MBF bytes through an address-bound conversion, retaining full precision.</summary>
  private IrValue DecodeMbfString(IrValue source, bool isDouble) {
    var storageType = isDouble ? IrType.Mbf64 : IrType.Mbf32;
    var size = new IrConstantInt(IrType.I32, storageType.Bits / 8);
    var length = this._b.Call(IrType.I32,
      this.RuntimeFn("rt_str_len_borrow", IrType.I32, IrType.Ptr), source);
    var slot = this._entry.InsertAt(this._entryAllocaCount++,
      new IrAlloca(storageType) { Name = "mbf.decode" });
    // Copy before the check so the consuming helper releases the owned handle even on Error 5.
    // Padding in this private cell is unobservable: an undersized source never reaches conversion.
    this._b.Call(IrType.Void,
      this.RuntimeFn("rt_str_to_fixed", IrType.Void, IrType.Ptr, IrType.I32, IrType.Ptr), slot, size, source);
    this.RaiseWhen(this._b.Cmp(IrCmpPred.Slt, length, size), 5, "short MBF string");
    return this._b.Cast(IrCastOp.MbfToFP, this._b.Load(storageType, slot), IrType.F80);
  }

  /// <summary>DOUBLE-to-SINGLE conversion rounds ties away; SINGLE arithmetic rounds ties to even.</summary>
  private IrValue NarrowInterpreterSingle(IrValue value, PbType sourceType) {
    var extended = this.Coerce(value, sourceType, PbType.Ext);
    return sourceType is MbfType { IsDouble: true } or ScalarType { Kind: ScalarKind.Double }
      ? this._b.Call(IrType.F80, this.RuntimeFn("rt_mbf32_narrow", IrType.F80, IrType.F80), extended)
      : extended;
  }

  /// <summary>Copies MBF storage without canonicalizing a zero exponent's observable mantissa bytes.</summary>
  private bool TryLowerMbfStorageCopy(AssignStmt assignment) {
    if (this.GetMbfStorageType(assignment.Target) is not { } target
        || this.GetMbfStorageType(assignment.Value) is not { } source
        || source.IsDouble && !target.IsDouble)
      return false;

    // Keep static-array assignment's target-before-source address evaluation. Snapshot every source
    // word before writing, so self-assignment and overlapping storage do not have memcpy's UB.
    var destination = this.AddressOfStorage(assignment.Target);
    var sourceAddress = this.AddressOfStorage(assignment.Value);
    var words = new IrValue[source.Size / 2];
    for (var i = 0; i < words.Length; ++i)
      words[i] = this._b.Load(IrType.I16, this.OffsetWithin(sourceAddress, i * 2));

    // MBF64 extends MBF32 with four low fraction bytes; the original sign/exponent word stays intact.
    var extension = target.Size - source.Size;
    for (var offset = 0; offset < extension; offset += 2)
      this._b.Store(new IrConstantInt(IrType.I16, 0), this.OffsetWithin(destination, offset));
    for (var i = 0; i < words.Length; ++i)
      this._b.Store(words[i], this.OffsetWithin(destination, extension + i * 2));
    return true;
  }

  /// <summary>The MBF encoding of a near scalar/static-array cell, not a computed numeric value.</summary>
  private MbfType? GetMbfStorageType(Expression expression) {
    if (!this._model.VariableBindings.TryGetValue(expression, out var symbol))
      return null;
    if ((expression is NameExpr or MemberExpr) && symbol.Type is MbfType scalar)
      return scalar;
    if (expression is not CallOrIndexExpr
        || symbol.Type is not ArrayType { IsDynamic: false, Element: MbfType element })
      return null;
    return symbol.Storage != VariableStorage.Parameter
      && symbol.ArrayClass is ArrayClass.Default or ArrayClass.Static or ArrayClass.Stack ? element : null;
  }
}
