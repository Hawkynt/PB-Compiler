namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Reads one character of a string as a BYTE instead of building a one-character string and asking
/// for its first byte.
///
/// <para>
/// This is the IR half of the direct emitter's pb36 O0297/O0290. <c>ASC(MID$(s$, i, 1))</c> - the
/// spelling every hand-written parser in the corpus scans with - lowers to a substring allocation
/// whose only reader is <c>ASC</c>, so the heap is entered and left again for one byte. The runtime's
/// <c>rt_charat</c> reads it straight out of the source buffer.
/// </para>
///
/// <para>
/// The two agree at every boundary, which is what makes the rewrite a rewrite and not an
/// approximation: both clamp a start below 1 to 1, both answer 0 past the end (MID$ yields the empty
/// string there and ASC of the empty string is 0), and both CONSUME the handle they are given. The
/// length must be the CONSTANT one - a runtime length that happens to be 1 is a different program,
/// and proving it 1 is SCCP's job rather than this pass's.
/// </para>
///
/// <para>
/// <c>LEFT$(s$, 1)</c> is the same read at a fixed index and is rewritten with it. <c>RIGHT$(s$, 1)</c>
/// is NOT: its index is <c>LEN(s$)</c>, which is not a value this pass has, and the runtime's own
/// answer for it (<c>rt_lastchar</c>) is a separate entry - worth having, and not needed by anything
/// yet.
/// </para>
/// </summary>
public static class StringByteRead {

  private const string _ASC = "rt_str_asc";
  private const string _MID = "rt_str_mid";
  private const string _LEFT = "rt_str_left";
  private const string _CHAR_AT = "rt_str_char_at";

  /// <summary>Rewrites single-character reads across the module; the number rewritten.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var rewritten = 0;
    foreach (var function in module.Functions.ToList()) {
      if (function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm)
        continue;
      foreach (var call in function.AllInstructions.OfType<IrCall>().ToList()) {
        if (call.Parent is null || call.Callee is not IrFunction { Name: _ASC } || call.ArgCount != 1)
          continue;
        if (SingleCharacterSource(call.GetOperand(1)) is not var (substring, source, index))
          continue;
        var entry = module.FindFunction(_CHAR_AT)
          ?? module.AddFunction(new IrFunction(_CHAR_AT, IrType.I32,
            [new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.I32, 1)]));
        var read = new IrCall(call.Type, entry, [source, index]);
        call.Parent!.InsertBefore(read, call);
        call.ReplaceAllUsesWith(read);
        call.EraseFromParent();
        substring.EraseFromParent();
        ++rewritten;
      }
    }
    return rewritten;
  }

  /// <summary>
  /// The substring call behind a one-character read, with the string and the 1-based index it reads -
  /// or null when the argument is anything else.
  /// </summary>
  private static (IrCall Substring, IrValue Source, IrValue Index)? SingleCharacterSource(IrValue value) {
    if (value is not IrCall { Callee: IrFunction callee } substring || substring.Users.Count != 1)
      return null;
    return callee.Name switch {
      _MID when substring.ArgCount == 3 && substring.GetOperand(3) is IrConstantInt { Value: 1 }
        => (substring, substring.GetOperand(1), substring.GetOperand(2)),
      _LEFT when substring.ArgCount == 2 && substring.GetOperand(2) is IrConstantInt { Value: 1 }
        => (substring, substring.GetOperand(1), new IrConstantInt(IrType.I32, 1)),
      _ => null,
    };
  }
}
