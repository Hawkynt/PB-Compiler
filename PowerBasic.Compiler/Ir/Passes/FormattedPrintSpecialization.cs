using System.Globalization;
using System.Text;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0303 — specializes formatted output whose scaled numeric field and packed USING specification are
/// both constant after value folding. The lowering has already interpreted the literal format into a
/// straight-line plan; this pass finishes the constant fields by materializing their exact bytes and
/// replacing the generic formatter call with an ordinary literal print.
/// </summary>
public static class FormattedPrintSpecialization {

  private const string _USING_FIELD = "rt_using_field";
  private const string _FILE_USING_FIELD = "rt_fusing_field";
  private const string _PRINT_STRING = "rt_print_str";
  private const string _FILE_PRINT_STRING = "rt_fprint_str";

  /// <summary>Specializes constant formatted fields in-place; returns the number of formatter calls replaced.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var specialized = 0;
    foreach (var function in module.Functions.Where(function => !function.IsDeclaration).ToList()) {
      if (function.HasErrorHandler || function.HasInlineAsm)
        continue;
      foreach (var call in function.AllInstructions.OfType<IrCall>().ToList())
        specialized += TrySpecialize(module, call) ? 1 : 0;
    }
    return specialized;
  }

  private static bool TrySpecialize(IrModule module, IrCall call) {
    if (call.Callee is not IrFunction callee)
      return false;

    var args = call.Args.ToArray();
    var isFile = callee.Name == _FILE_USING_FIELD;
    if ((!isFile && (callee.Name != _USING_FIELD || args.Length != 2)) || (isFile && args.Length != 3))
      return false;

    var valueIndex = isFile ? 1 : 0;
    if (args[valueIndex] is not IrConstantInt { Type.Bits: 32 } scaled
        || args[valueIndex + 1] is not IrConstantInt { Type.Bits: 32 } packedSpec)
      return false;

    var bytes = FormatField(
      unchecked((int)(uint)scaled.ZeroExtended),
      unchecked((ushort)packedSpec.ZeroExtended));
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

  private static IrFunction Declare(IrModule module, string name, params IrType[] parameterTypes) {
    if (module.FindFunction(name) is { } existing)
      return existing;
    return module.AddFunction(new IrFunction(name, IrType.Void,
      parameterTypes.Select((type, index) => new IrArgument(type, index)).ToArray()));
  }

  /// <summary>
  /// Renders the DOS runtime's current numeric USING contract: right-aligned fixed point, optional
  /// thousands separators, sign inside the field, and no truncation when the value exceeds the width.
  /// The value is already decimal-scaled and rounded before O0303 sees it.
  /// </summary>
  private static byte[] FormatField(int scaled, ushort packedSpec) {
    var width = packedSpec >> 8;
    var decimals = packedSpec & 0x7f;
    var group = (packedSpec & 0x80) != 0;
    var negative = scaled < 0;
    var magnitude = negative ? -(long)scaled : scaled;
    var digits = magnitude.ToString(CultureInfo.InvariantCulture);

    if (digits.Length <= decimals)
      digits = new string('0', decimals + 1 - digits.Length) + digits;

    var integerLength = digits.Length - decimals;
    var integer = digits[..integerLength];
    if (group)
      integer = GroupThousands(integer);

    var rendered = negative ? "-" + integer : integer;
    if (decimals > 0)
      rendered += "." + digits[integerLength..];

    return Encoding.ASCII.GetBytes(rendered.PadLeft(width));
  }

  private static string GroupThousands(string digits) {
    var firstGroup = digits.Length % 3;
    if (firstGroup == 0)
      firstGroup = 3;
    var result = digits[..firstGroup];
    for (var i = firstGroup; i < digits.Length; i += 3)
      result += "," + digits.Substring(i, 3);
    return result;
  }
}
