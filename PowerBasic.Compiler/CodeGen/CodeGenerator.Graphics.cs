using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  /// <summary>
  /// <c>LINE [(x1,y1)]-(x2,y2) [,colour] [,B|BF] [,style]</c>.
  ///
  /// Every part after the second point is optional, and they can be elided individually - PB accepts
  /// <c>LINE (0,0)-(9,9), , B</c> (no colour, but a box) and <c>LINE (0,0)-(9,9), 15, , &amp;HF0F0</c>
  /// (a styled line, not a box). The parser has already sorted that out; what is left here is which
  /// defaults to supply and which of the three runtime entries to call.
  ///
  /// Omitting the start point is not a default but a read: the segment begins at the last point any
  /// graphics statement referenced, which the runtime keeps in the same cells it draws from - so
  /// leaving them alone IS the correct behaviour.
  /// </summary>
  private void EmitLineStatement(LineStmt line) {
    var asm = this._asm;

    if (line.From is { } from) {
      this.EmitInt16Argument(from.X);
      asm.Mov(Mem.Word(asm.Lbl("rt_gx1")), Reg.AX);
      this.EmitInt16Argument(from.Y);
      asm.Mov(Mem.Word(asm.Lbl("rt_gy1")), Reg.AX);
    }
    this.EmitInt16Argument(line.To.X);
    asm.Mov(Mem.Word(asm.Lbl("rt_gx2")), Reg.AX);
    this.EmitInt16Argument(line.To.Y);
    asm.Mov(Mem.Word(asm.Lbl("rt_gy2")), Reg.AX);

    if (line.Color is { } color)
      this.EmitInt16Argument(color);
    else
      asm.Mov(Reg.AX, 15);                       // the default foreground, as PSET uses
    asm.Mov(Mem.Word(asm.Lbl("rt_gcolor")), Reg.AX);

    // the style mask is consulted a bit per pixel; all-ones is a solid line
    if (line.Style is { } style)
      this.EmitInt16Argument(style);
    else
      asm.Mov(Reg.AX, 0xFFFF);
    asm.Mov(Mem.Word(asm.Lbl("rt_gstyle")), Reg.AX);

    asm.Call(line switch {
      { Box: true, Fill: true } => this._rt.LineFill,
      { Box: true } => this._rt.LineBox,
      _ => this._rt.Line,
    });
  }
}
