using System.Text;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// pb36 P7 (docs/PB36.md): intrinsic lowering of trivial I/O. A program whose
/// every observable effect is printing compile-time text to the console lowers
/// to a raw COM-style image (no MZ header - DOS and DOSBox load images without
/// the MZ signature as .COM regardless of extension): the whole output is
/// precomputed, written with one DOS call, and the program exits. A hello
/// world becomes ~24 bytes. The general (trimmed) pb36 path takes over the
/// moment anything non-trivial appears.
/// </summary>
public sealed partial class CodeGenerator {

  private const byte _DOLLAR = (byte)'$';

  /// <summary>
  /// Tries the trivial-program lowering; null = not trivial, use the general
  /// path. Only PRINT of compile-time strings/integrals (plain console form),
  /// END, and inert declarations qualify - and there must be no procedures.
  /// </summary>
  private byte[]? TryLowerTrivialProgram() {
    if (model.ProcedureList.Any(p => !p.IsExternal))
      return null;

    var text = new List<byte>();
    var column = 0;
    long exitCode = 0;

    foreach (var statement in model.MainBody)
      switch (statement) {
        case MetaStmt or EquateStmt or DefTypeStmt or DataStmt:
          break; // inert for a program with no runtime

        case EndStmt end: {
          if (end.ExitCode is { } codeExpr) {
            if (this.Pb36Folder.TryFold(codeExpr) is not { Integer: { } code })
              return null;
            exitCode = (byte)code;
          }
          return this.BuildTrivialImage(text, (byte)exitCode); // END terminates - the rest is unreachable

        }
        case PrintStmt { FileNumber: null, IsLPrint: false, UsingFormat: null } print: {
          foreach (var item in print.Items) {
            if (item.Value is { } value && !this.TryRenderPrintItem(value, text, ref column))
              return null;
            switch (item.Separator) {
              case PrintSeparator.Comma: { // next 14-column print zone (rt_print_zone)
                var pad = 14 - column % 14;
                for (var i = 0; i < pad; ++i)
                  text.Add((byte)' ');
                column += pad;
                break;
              }
              case PrintSeparator.Newline:
                text.Add((byte)'\r');
                text.Add((byte)'\n');
                column = 0;
                break;
            }
          }
          if (print.Items.Count == 0) { // bare PRINT = newline
            text.Add((byte)'\r');
            text.Add((byte)'\n');
            column = 0;
          }
          break;
        }

        default:
          return null; // anything else needs the real runtime
      }

    return this.BuildTrivialImage(text, (byte)exitCode);
  }

  /// <summary>Renders one PRINT item at compile time (string literals and foldable integrals).</summary>
  private bool TryRenderPrintItem(Expression value, List<byte> text, ref int column) {
    switch (value) {
      case StringLiteralExpr s:
        foreach (var c in s.Value) {
          if (c > '\xFF')
            return false;
          text.Add((byte)c);
        }
        column += s.Value.Length;
        return true;

      default: {
        // numeric: PB renders "[ |-]digits[ ]" - only exact integral folds qualify
        if (model.TypeOf(value) is not ScalarType { ByteSize: <= 8 } type)
          return false;
        if (this.Pb36Folder.TryFold(value) is not { Integer: { } raw })
          return false;
        long wrapped;
        if (type.IsFloat) {
          // PB-promoted integral arithmetic: no wrapping; plain digits only
          // while the float display stays in fixed notation (SINGLE < 1E7,
          // DOUBLE < 1E15), beyond that the runtime would print an exponent
          var limit = type.Kind == ScalarKind.Single ? 10_000_000L : 1_000_000_000_000_000L;
          if (Math.Abs(raw) >= limit)
            return false;
          wrapped = raw;
        } else {
          if (type.ByteSize > 4)
            return false;
          wrapped = WrapToType(raw, type);
        }
        var rendered = (wrapped < 0 ? "" : " ") + wrapped.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ";
        text.AddRange(Encoding.ASCII.GetBytes(rendered));
        column += rendered.Length;
        return true;
      }
    }
  }

  /// <summary>
  /// Builds the raw COM-style image (org 100h): one DOS write of the prepared
  /// text, then exit. AH=9 ('$'-terminated, smallest) when the text is
  /// '$'-free, otherwise AH=40h to handle 1; exit via INT 20h (code 0) or
  /// AH=4Ch (explicit code).
  /// </summary>
  private byte[]? BuildTrivialImage(List<byte> text, byte exitCode) {
    if (text.Count > 60000)
      return null; // COM images top out below 64 KiB - use the general path
    var image = new List<byte>();
    var useDollarWriter = text.Count > 0 && !text.Contains(_DOLLAR);

    // exit sequence appended after the write
    var exitBytes = exitCode == 0
      ? new byte[] { 0xCD, 0x20 }                         // INT 20h
      : [0xB8, exitCode, 0x4C, 0xCD, 0x21];               // MOV AX,4Cnn / INT 21h

    if (text.Count == 0) {
      image.AddRange(exitBytes);
      return [.. image];
    }

    if (useDollarWriter) {
      // MOV AH,9 / MOV DX,text / INT 21h
      var textOffset = 0x100 + 7 + exitBytes.Length;
      image.AddRange([0xB4, 0x09, 0xBA, (byte)textOffset, (byte)(textOffset >> 8), 0xCD, 0x21]);
      image.AddRange(exitBytes);
      image.AddRange(text);
      image.Add(_DOLLAR);
    } else {
      // MOV AH,40h / MOV BX,1 / MOV CX,len / MOV DX,text / INT 21h
      var textOffset = 0x100 + 13 + exitBytes.Length;
      image.AddRange([
        0xB4, 0x40,
        0xBB, 0x01, 0x00,
        0xB9, (byte)text.Count, (byte)(text.Count >> 8),
        0xBA, (byte)textOffset, (byte)(textOffset >> 8),
        0xCD, 0x21,
      ]);
      image.AddRange(exitBytes);
      image.AddRange(text);
    }

    return [.. image];
  }
}
