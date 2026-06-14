using PowerBasic.Compiler.Semantics;

namespace PowerBasic.Compiler.Ir;

/// <summary>
/// Maps resolved PowerBASIC scalar types onto the target-independent IR type lattice.
/// Only the scalar numeric types are mapped at this layer; strings, arrays, UDTs and
/// pointers are out of scope for the current lowering and report failure so the caller
/// can decline to lower (keeping the IR path sound on the subset it supports).
/// </summary>
public static class IrTypeMapper {

  /// <summary>Maps a scalar numeric PB type to its IR type; returns false for anything else.</summary>
  public static bool TryMap(PbType type, out IrType ir) {
    if (type is ScalarType s) {
      ir = s.IsFloat ? IrType.Floating(s.ByteSize * 8) : IrType.Integer(s.ByteSize * 8);
      return true;
    }
    ir = IrType.Void;
    return false;
  }

  /// <summary>Maps a scalar type or throws if unsupported.</summary>
  public static IrType Map(PbType type) =>
    TryMap(type, out var ir) ? ir : throw new IrLoweringException($"unsupported type for IR lowering: {type}");
}

/// <summary>Raised when the lowering meets a construct outside its supported subset; caught to decline gracefully.</summary>
public sealed class IrLoweringException(string message) : Exception(message);
