using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Emit;

/// <summary>
/// pb36 P7 (docs/PB36.md): a program whose every observable effect, once optimized, is writing known
/// text to the console becomes a raw COM-style image of a few dozen bytes - the whole output
/// precomputed, written with one DOS call, then the exit. DOS loads an image without the MZ signature
/// as a .COM whatever its extension, so this is also what an .EXE of such a program is.
///
/// <para>
/// It reads the OPTIMIZED module body rather than the source, which is what makes it reach further
/// than the direct emitter's version did: a value folded to a constant by inlining or propagation, or
/// a numeric PRINT <see cref="Ir.Passes.ConstantNumericPrint"/> already turned into text, qualifies
/// exactly like a literal. It stands aside the moment the body does anything else.
/// </para>
///
/// <para>
/// The text is assembled with the runtime's own bookkeeping, because that is what it replaces: a
/// string print writes its bytes and advances the column by its length, a newline writes CR LF and
/// resets it, and a comma pads to the next fourteen-column zone. A string holding a control
/// character is refused rather than modelled, since the column the runtime keeps then stops meaning
/// the column the screen shows.
/// </para>
/// </summary>
public static class DosTrivialImage {

  private const byte _DOLLAR = (byte)'$';
  private const int _ZONE = 14;
  private const int _LOAD_ADDRESS = 0x100;

  /// <summary>The image for <paramref name="module"/>, or null when its body does more than print constant text.</summary>
  public static byte[]? TryBuild(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    if (module.FindFunction("main") is not { IsDeclaration: false, Blocks.Count: 1 } main)
      return null;

    var text = new List<byte>();
    var column = 0;
    byte exitCode = 0;
    foreach (var instruction in main.Blocks[0].Instructions)
      switch (instruction) {
        case IrCall { Callee: IrFunction { Name: "rt_print_str" } } print
            when print.Args.ToArray() is [IrGlobalVariable { Bytes: { } bytes }, IrConstantInt { Value: var length }]
              && length >= 0 && length <= bytes.Length && bytes.Take((int)length).All(b => b >= 0x20):
          text.AddRange(bytes.Take((int)length));
          column += (int)length;
          break;
        case IrCall { Callee: IrFunction { Name: "rt_print_nl" }, ArgCount: 0 }:
          text.AddRange("\r\n"u8.ToArray());
          column = 0;
          break;
        case IrCall { Callee: IrFunction { Name: "rt_print_comma" }, ArgCount: 0 }: {
          var pad = _ZONE - column % _ZONE;
          text.AddRange(Enumerable.Repeat((byte)' ', pad));
          column += pad;
          break;
        }
        case IrCall { Callee: IrFunction { Name: "rt_end" } } end when end.Args.ToArray() is [IrConstantInt { Value: var code }]:
          exitCode = (byte)code;
          return Build(text, exitCode);        // END terminates; what follows is unreachable
        case IrRet { Value: null }:
          return Build(text, exitCode);
        default:
          return null;                           // anything else needs the real runtime
      }
    return null;
  }

  /// <summary>
  /// One DOS write of the text, then the exit. AH=9 ('$'-terminated, smallest) when the text holds no
  /// '$', otherwise AH=40h to handle 1; the exit is INT 20h for code 0 and AH=4Ch for any other.
  /// </summary>
  private static byte[]? Build(List<byte> text, byte exitCode) {
    if (text.Count > 60000)
      return null;                               // COM images top out below 64 KiB
    byte[] exit = exitCode == 0
      ? [0xCD, 0x20]                             // INT 20h
      : [0xB8, exitCode, 0x4C, 0xCD, 0x21];      // MOV AX,4Cnn / INT 21h
    if (text.Count == 0)
      return exit;

    var image = new List<byte>();
    if (!text.Contains(_DOLLAR)) {
      var textOffset = _LOAD_ADDRESS + 7 + exit.Length;
      image.AddRange([0xB4, 0x09, 0xBA, (byte)textOffset, (byte)(textOffset >> 8), 0xCD, 0x21]);   // MOV AH,9 / MOV DX,text / INT 21h
      image.AddRange(exit);
      image.AddRange(text);
      image.Add(_DOLLAR);
    } else {
      var textOffset = _LOAD_ADDRESS + 13 + exit.Length;
      image.AddRange([
        0xB4, 0x40,                                          // MOV AH,40h
        0xBB, 0x01, 0x00,                                    // MOV BX,1
        0xB9, (byte)text.Count, (byte)(text.Count >> 8),     // MOV CX,len
        0xBA, (byte)textOffset, (byte)(textOffset >> 8),     // MOV DX,text
        0xCD, 0x21,                                          // INT 21h
      ]);
      image.AddRange(exit);
      image.AddRange(text);
    }
    return [.. image];
  }
}
