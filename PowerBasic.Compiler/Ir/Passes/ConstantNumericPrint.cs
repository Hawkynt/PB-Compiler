using System.Globalization;
using System.Text;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// A numeric <c>PRINT</c> whose value is a whole-number constant is a literal <c>PRINT</c>: the pass
/// renders it the way the runtime would - a space or a minus sign, the digits, a trailing space - and
/// replaces the formatter call with an ordinary string print of those bytes.
///
/// <para>
/// It is exact because the DOS runtime's own number printers end in that same string print: they render
/// into a buffer and hand the buffer to the routine a literal goes to, so the column bookkeeping and
/// the zone arithmetic of a following comma see the same thing. Only whole numbers qualify, and a
/// real only while it is small enough that every dialect prints it without an exponent (fewer than
/// seven digits for a SINGLE, fifteen for a DOUBLE or EXTENDED); anything with a fraction keeps the
/// runtime, which is where a dialect's own float formatting lives. A QUAD is formatted like a DOUBLE by
/// the runtime, so it follows the same fifteen-digit limit.
/// </para>
///
/// <para>
/// This replaces the direct emitter's trivial-program renderer, which did the same for a program made
/// of nothing but constant PRINTs. Working on the optimized IR it reaches further: a value becomes
/// constant after inlining or propagation just as well as when it was written that way, and the
/// program around it need not be trivial.
/// </para>
/// </summary>
public static class ConstantNumericPrint {

  private const string _PRINT_STRING = "rt_print_str";
  private const string _FILE_PRINT_STRING = "rt_fprint_str";

  /// <summary>Rewrites qualifying numeric prints in-place; returns the number replaced.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var rewritten = 0;
    foreach (var function in module.Functions.Where(function => !function.IsDeclaration).ToList()) {
      if (function.HasErrorHandler || function.HasInlineAsm)
        continue;
      foreach (var call in function.AllInstructions.OfType<IrCall>().ToList())
        rewritten += TryRewrite(module, call) ? 1 : 0;
    }
    return rewritten;
  }

  private static bool TryRewrite(IrModule module, IrCall call) {
    if (call.Callee is not IrFunction { Name: var name } || PrintedSuffix(name) is not { } suffix)
      return false;
    var isFile = name.StartsWith("rt_fprint_", StringComparison.Ordinal);
    var args = call.Args.ToArray();
    if (args.Length != (isFile ? 2 : 1) || Render(suffix, args[^1]) is not { } text)
      return false;

    var bytes = Encoding.ASCII.GetBytes(text);
    var literal = module.AddStringConstant(bytes);
    var print = isFile
      ? Declare(module, _FILE_PRINT_STRING, IrType.I32, IrType.Ptr, IrType.I32)
      : Declare(module, _PRINT_STRING, IrType.Ptr, IrType.I32);
    IrValue[] printArgs = isFile
      ? [args[0], literal, new IrConstantInt(IrType.I32, bytes.Length)]
      : [literal, new IrConstantInt(IrType.I32, bytes.Length)];
    call.Parent!.InsertBefore(new IrCall(IrType.Void, print, printArgs), call);
    call.EraseFromParent();
    return true;
  }

  /// <summary>The number-kind suffix of a console or file number printer, or null for any other routine.</summary>
  private static string? PrintedSuffix(string name) {
    var suffix = name.StartsWith("rt_print_", StringComparison.Ordinal) ? name["rt_print_".Length..]
      : name.StartsWith("rt_fprint_", StringComparison.Ordinal) ? name["rt_fprint_".Length..]
      : null;
    return suffix is "i16" or "i32" or "i64" or "u8" or "u16" or "u32" or "single" or "double" or "ext"
      ? suffix
      : null;
  }

  /// <summary>The runtime's text for a constant argument, or null when the runtime has to decide.</summary>
  private static string? Render(string suffix, IrValue value) {
    long? whole = value switch {
      IrConstantInt integer when suffix.StartsWith('u') => (long)integer.ZeroExtended,
      IrConstantInt integer when suffix.StartsWith('i') => integer.Value,
      IrConstantFloat real when suffix is "single" or "double" or "ext" => WholeWithoutExponent(real.Value, suffix),
      _ => null,
    };
    // a QUAD is printed through the runtime's real formatter: exact digits only below 10^15, the
    // shortest-DOUBLE form (9.22337203685478E+18) above
    if (whole is not { } number || (suffix == "i64" && Math.Abs((double)number) >= 1e15))
      return null;
    return (number < 0 ? "" : " ") + number.ToString(CultureInfo.InvariantCulture) + " ";
  }

  /// <summary>
  /// A real that is a whole number printed in plain digits by every dialect's formatter: no fraction,
  /// not negative zero, and below the magnitude at which the shortest form switches to an exponent.
  /// </summary>
  private static long? WholeWithoutExponent(double value, string suffix) {
    var limit = suffix == "single" ? 1e6 : 1e15;
    if (!double.IsFinite(value) || Math.Floor(value) != value || Math.Abs(value) >= limit
        || (value == 0 && double.IsNegative(value)))
      return null;
    return (long)value;
  }

  private static IrFunction Declare(IrModule module, string name, params IrType[] parameterTypes) {
    if (module.FindFunction(name) is { } existing)
      return existing;
    return module.AddFunction(new IrFunction(name, IrType.Void,
      parameterTypes.Select((type, index) => new IrArgument(type, index)).ToArray()));
  }
}
