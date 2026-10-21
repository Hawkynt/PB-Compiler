using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Ir;

public sealed partial class IrLowering {

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
