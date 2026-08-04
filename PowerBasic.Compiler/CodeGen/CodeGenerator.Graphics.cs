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

  /// <summary>
  /// <c>CIRCLE (x,y), r [,colour] [,start] [,end] [,aspect]</c>, in the full-circle forms.
  ///
  /// The arc and aspect arguments are declined rather than ignored. They are angles in radians, so
  /// drawing one needs the x87 - and the interpreter the graphics tests run on deliberately does not
  /// emulate x87, because a half-faithful 80-bit stack would let a float test pass while disagreeing
  /// with the hardware. Emitting an arc nobody could execute would mean shipping the one thing worse
  /// than a missing feature: a CIRCLE that quietly draws all 360 degrees when the program asked for a
  /// quarter of them.
  /// </summary>
  private void EmitCircleStatement(CircleStmt circle) {
    if (circle.Start is not null || circle.End is not null || circle.Aspect is not null) {
      this.Unsupported(circle.Position, "CIRCLE with a start/end angle or aspect ratio (the arc needs x87 trigonometry)");
      return;
    }
    var asm = this._asm;
    this.EmitInt16Argument(circle.Center.X);
    asm.Mov(Mem.Word(asm.Lbl("rt_gcx")), Reg.AX);
    this.EmitInt16Argument(circle.Center.Y);
    asm.Mov(Mem.Word(asm.Lbl("rt_gcy")), Reg.AX);
    this.EmitInt16Argument(circle.Radius);
    asm.Mov(Mem.Word(asm.Lbl("rt_gr")), Reg.AX);

    if (circle.Color is { } color)
      this.EmitInt16Argument(color);
    else
      asm.Mov(Reg.AX, 15);
    asm.Mov(Mem.Word(asm.Lbl("rt_gcolor")), Reg.AX);
    asm.Call(this._rt.Circle);
  }
}
