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
  /// The arc and aspect arguments are declined rather than ignored, which remains the right answer -
  /// a CIRCLE quietly drawing all 360 degrees when the program asked for a quarter of them would be
  /// worse than one that refuses.
  ///
  /// The reason given here used to be that the test interpreter does not emulate x87. That is no
  /// longer true and may never have been: SIN and COS run through it correctly, so an arc CAN be
  /// executed and checked here. What is missing is the work, not the ability to verify it.
  ///
  /// The shape it wants is the parametric one rather than an extension of the midpoint walk below -
  /// step an angle from start to end by about 1/r radians and PSET cos and sin of it - because that
  /// form takes the aspect ratio for free (it scales the sine) and needs no 32-bit arithmetic, while
  /// a midpoint ELLIPSE would need it: the radius squared leaves 16 bits at a radius of 181 and this
  /// screen is 320 wide. The full-circle case should keep the integer midpoint walk it has.
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

  /// <summary>
  /// <c>DRAW "..."</c> with a CONSTANT string, expanded here into the moves it denotes.
  ///
  /// DRAW is a macro language, and the obvious way to run one is an interpreter in the runtime that
  /// walks the string byte by byte. It does not need one when the string is written down: the deltas
  /// are all known while compiling, and each step becomes half a dozen instructions against the
  /// runtime's "last point referenced" - which is the same cell LINE reads when its start point is
  /// omitted, so the turtle is already there and needs no state of its own.
  ///
  /// A computed string still declines. So does one using A, S, TA, P or X: those carry state from
  /// one step to the next - a rotation, a scale, a fill, a string that is not this one - and the
  /// point of doing it here is that the answer is knowable, which for those it is not.
  /// </summary>
  private void EmitDrawStatement(CommandStmt draw, string picture) {
    if (!Semantics.MacroStringValidator.TryParseDraw(picture, out var steps, out var declined)) {
      this.Unsupported(draw.Position, declined ?? "DRAW string");
      return;
    }
    var asm = this._asm;
    asm.Mov(Mem.Word(asm.Lbl("rt_gstyle")), 0xFFFF);        // solid; DRAW has no style mask

    foreach (var step in steps) {
      if (step.Kind == Semantics.DrawStepKind.Colour) {
        asm.Mov(Mem.Word(asm.Lbl("rt_gcolor")), (Imm)step.X);
        continue;
      }

      // where this step ends: a delta from the current point, or the point itself
      if (step.Kind == Semantics.DrawStepKind.Relative) {
        asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gx1")));
        if (step.X != 0)
          asm.Add(Reg.AX, (Imm)step.X);
        asm.Mov(Mem.Word(asm.Lbl("rt_gx2")), Reg.AX);
        asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gy1")));
        if (step.Y != 0)
          asm.Add(Reg.AX, (Imm)step.Y);
        asm.Mov(Mem.Word(asm.Lbl("rt_gy2")), Reg.AX);
      } else {
        asm.Mov(Mem.Word(asm.Lbl("rt_gx2")), (Imm)step.X);
        asm.Mov(Mem.Word(asm.Lbl("rt_gy2")), (Imm)step.Y);
      }

      // B moves without drawing: the endpoint simply becomes the current point
      if (step.Blank) {
        asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gx2")));
        asm.Mov(Mem.Word(asm.Lbl("rt_gx1")), Reg.AX);
        asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gy2")));
        asm.Mov(Mem.Word(asm.Lbl("rt_gy1")), Reg.AX);
        continue;
      }

      // N draws and comes back, so the point it started from is kept over the call that moves it
      if (step.NoUpdate) {
        asm.Push(Mem.Word(asm.Lbl("rt_gx1")));
        asm.Push(Mem.Word(asm.Lbl("rt_gy1")));
      }
      asm.Call(this._rt.Line);
      if (step.NoUpdate) {
        asm.Pop(Mem.Word(asm.Lbl("rt_gy1")));
        asm.Pop(Mem.Word(asm.Lbl("rt_gx1")));
      }
    }
  }

  /// <summary>
  /// <c>GET (x1,y1)-(x2,y2), a%(0)</c> and <c>PUT (x,y), a%(0)[, verb]</c> - sprite capture and blit.
  ///
  /// The array is named by one of its elements rather than bare, which is how QuickBASIC spells it
  /// too (<c>arrayname(index)</c>): the statement wants an ADDRESS to read or write from, and an
  /// element is the thing that has one. A bare name is refused with a diagnostic saying so, rather
  /// than being guessed at as element zero - if a program means the middle of the array, it says so.
  ///
  /// PUT's action defaults to XOR. That is QuickBASIC's default and not an arbitrary one: drawing
  /// the same sprite twice erases it, which is how anything moved on screen before hardware sprites.
  /// </summary>
  private void EmitGetPutGraphics(GetPutGraphicsStmt gg) {
    var asm = this._asm;
    var verbs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) {
      ["PSET"] = 0, ["PRESET"] = 1, ["AND"] = 2, ["OR"] = 3, ["XOR"] = 4,
    };
    if (gg.Verb is { } verbName && !verbs.ContainsKey(verbName)) {
      this.Unsupported(gg.Position, $"PUT action '{verbName}' (PSET, PRESET, AND, OR and XOR are the five)");
      return;
    }
    if (gg.IsGet && gg.To is null) {
      this.Unsupported(gg.Position, "GET needs both corners of the rectangle to capture");
      return;
    }

    this.EmitInt16Argument(gg.From.X);
    asm.Mov(Mem.Word(asm.Lbl("rt_gx1")), Reg.AX);
    this.EmitInt16Argument(gg.From.Y);
    asm.Mov(Mem.Word(asm.Lbl("rt_gy1")), Reg.AX);
    if (gg.To is { } corner) {
      this.EmitInt16Argument(corner.X);
      asm.Mov(Mem.Word(asm.Lbl("rt_gx2")), Reg.AX);
      this.EmitInt16Argument(corner.Y);
      asm.Mov(Mem.Word(asm.Lbl("rt_gy2")), Reg.AX);
    }

    if (this.EmitPlace(gg.Array) is not { } buffer) {
      this.Unsupported(gg.Position, $"{(gg.IsGet ? "GET" : "PUT")} needs an array element to address, as in a%(0)");
      return;
    }
    asm.Lea(Reg.AX, buffer.Cell);
    asm.Mov(Mem.Word(asm.Lbl("rt_gbufofs")), Reg.AX);
    asm.Mov(Reg.AX, buffer.Far ? Reg.ES : Reg.DS);
    asm.Mov(Mem.Word(asm.Lbl("rt_gbufseg")), Reg.AX);

    if (gg.IsGet) {
      asm.Call(this._rt.GGet);
      return;
    }
    asm.Mov(Mem.Word(asm.Lbl("rt_gverb")), (Imm)verbs[gg.Verb ?? "XOR"]);
    asm.Call(this._rt.GPut);
  }

  /// <summary>
  /// <c>PAINT (x, y), paint [, border]</c> - the flood fill, which the parser hands over as a command
  /// whose point has already been split into two arguments, so the shapes are [x, y, paint] and
  /// [x, y, paint, border].
  ///
  /// An omitted border is the paint colour itself, which is BASIC's rule and not an arbitrary
  /// default: a fill with no stated boundary stops where it has already been, so it spreads over
  /// everything reachable that is not already the colour being painted.
  /// </summary>
  private void EmitPaintStatement(CommandStmt paint) {
    if (paint.Arguments is not [{ } x, { } y, { } colour, ..] || paint.Arguments.Count > 4) {
      this.Unsupported(paint.Position, "PAINT takes a point, a paint colour and an optional border colour");
      return;
    }
    var asm = this._asm;
    this.EmitInt16Argument(x);
    asm.Mov(Mem.Word(asm.Lbl("rt_gx1")), Reg.AX);
    this.EmitInt16Argument(y);
    asm.Mov(Mem.Word(asm.Lbl("rt_gy1")), Reg.AX);
    this.EmitInt16Argument(colour);
    asm.Mov(Mem.Word(asm.Lbl("rt_gcolor")), Reg.AX);

    if (paint.Arguments.Count == 4 && paint.Arguments[3] is { } border)
      this.EmitInt16Argument(border);
    // else: AX still holds the paint colour, which is what an omitted border means
    asm.Mov(Mem.Word(asm.Lbl("rt_gpbord")), Reg.AX);
    asm.Call(this._rt.Paint);
  }
}
