namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0302: <c>INSTR</c> of a compile-time needle of two bytes or more calls a search that knows the
/// needle, instead of building a string for it and handing both to the generic routine. A needle
/// shorter than <see cref="HorspoolThreshold"/> uses <c>rt_instr_short</c> (REPNE SCASB for candidate
/// first bytes, REPE CMPSB to verify); a longer one uses <c>rt_instr_horspool</c> with its 256-byte
/// bad-character table, computed here and pooled like any literal.
///
/// <para>
/// The needle is read in place from its literal, so the <c>rt_str_const</c> that allocated a string
/// for it goes when nothing else reads that string. It runs in the native DOS stage only - the two
/// searches are that runtime's routines - and only under the optimizer, like the direct emitter's
/// version it replaces.
/// </para>
/// </summary>
public static class ConstantInstrSpecialization {

  /// <summary>Needles at least this long use Horspool; below it the table does not pay for itself.</summary>
  public const int HorspoolThreshold = 5;

  /// <summary>Rewrites qualifying searches across the module; returns how many.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var rewritten = 0;
    foreach (var function in module.Functions.Where(function => !function.IsDeclaration).ToList()) {
      if (function.HasInlineAsm)
        continue;
      foreach (var call in function.AllInstructions.OfType<IrCall>().ToList())
        rewritten += TryRewrite(module, call) ? 1 : 0;
    }
    return rewritten;
  }

  private static bool TryRewrite(IrModule module, IrCall call) {
    if (call.Callee is not IrFunction { Name: var name })
      return false;
    var args = call.Args.ToArray();
    var (start, haystack, needle) = (name, args) switch {
      ("rt_str_instr", [var h, var n]) => ((IrValue)new IrConstantInt(IrType.I32, 1), h, n),
      ("rt_str_instr_start", [var s, var h, var n]) => (s, h, n),
      _ => (null!, null!, null!),
    };
    if (needle is not IrCall { Callee: IrFunction { Name: "rt_str_const" } } literal
        || literal.Args.ToArray() is not [IrGlobalVariable { Bytes: { } bytes } pooled, IrConstantInt { Value: var length }]
        || length < 2 || length > bytes.Length || literal.Users.Count != 1)
      return false;

    var text = bytes[..(int)length];
    IrCall search;
    if (length < HorspoolThreshold) {
      var shortSearch = Declare(module, "rt_instr_short", IrType.I32, IrType.Ptr, IrType.Ptr, IrType.I32);
      search = new IrCall(IrType.I32, shortSearch, [start, haystack, pooled, new IrConstantInt(IrType.I32, length)]);
    } else {
      var table = module.AddStringConstant(SkipTable(text));
      var horspool = Declare(module, "rt_instr_horspool", IrType.I32, IrType.Ptr, IrType.Ptr, IrType.I32, IrType.Ptr);
      search = new IrCall(IrType.I32, horspool, [start, haystack, pooled, new IrConstantInt(IrType.I32, length), table]);
    }
    call.Parent!.InsertBefore(search, call);
    call.ReplaceAllUsesWith(search);
    call.EraseFromParent();
    literal.EraseFromParent();
    return true;
  }

  /// <summary>
  /// Horspool's bad-character shifts: the needle length for a byte it does not contain, else the
  /// distance from that byte's last occurrence before the final position to the end. Saturated at
  /// 127, which only ever shortens a jump, so it stays correct - and keeps every entry a 7-bit byte,
  /// which is what the literal pool it travels through carries.
  /// </summary>
  public static byte[] SkipTable(ReadOnlySpan<byte> needle) {
    const int saturation = sbyte.MaxValue;
    var table = new byte[256];
    Array.Fill(table, (byte)Math.Min(needle.Length, saturation));
    for (var i = 0; i < needle.Length - 1; ++i)
      table[needle[i]] = (byte)Math.Min(needle.Length - 1 - i, saturation);
    return table;
  }

  private static IrFunction Declare(IrModule module, string name, params IrType[] parameterTypes)
    => module.FindFunction(name) ?? module.AddFunction(new IrFunction(name, IrType.I32,
      parameterTypes.Select((type, index) => new IrArgument(type, index)).ToArray()));
}
