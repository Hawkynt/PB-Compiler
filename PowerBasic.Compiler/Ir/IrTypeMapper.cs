using PowerBasic.Compiler.Semantics;

namespace PowerBasic.Compiler.Ir;

/// <summary>
/// Maps resolved PowerBASIC scalar types onto the target-independent IR type lattice.
/// Only the scalar numeric types are mapped at this layer; strings, arrays, UDTs and
/// pointers are out of scope for the current lowering and report failure so the caller
/// can decline to lower (keeping the IR path sound on the subset it supports).
/// </summary>
public static class IrTypeMapper {

  /// <summary>
  /// Maps a scalar numeric PB type to its IR type; returns false for anything else.
  /// Both distinctions the BASIC family makes and LLVM does not are preserved:
  /// <b>signedness</b> (<c>WORD</c> → <c>u16</c> where <c>INTEGER</c> → <c>i16</c>) and the
  /// <b>Microsoft Binary Format</b> floats of BASICA/GW-BASIC (<c>mbf32</c>/<c>mbf64</c>, a storage
  /// encoding the x87 cannot compute on - see <see cref="IrFloatFormat"/>).
  /// </summary>
  public static bool TryMap(PbType type, out IrType ir) {
    switch (type) {
      case ScalarType s:
        ir = s.IsFloat ? IrType.Floating(s.ByteSize * 8) : IrType.Integer(s.ByteSize * 8, s.Signed);
        return true;
      case MbfType m:
        ir = m.IsDouble ? IrType.Mbf64 : IrType.Mbf32;
        return true;
      default:
        ir = IrType.Void;
        return false;
    }
  }

  /// <summary>Maps a scalar type or throws if unsupported.</summary>
  public static IrType Map(PbType type) =>
    TryMap(type, out var ir) ? ir : throw new IrLoweringException($"unsupported type for IR lowering: {type}");
}

/// <summary>Raised when the lowering meets a construct outside its supported subset; caught to decline gracefully.</summary>
public sealed class IrLoweringException(string message) : Exception(message);
