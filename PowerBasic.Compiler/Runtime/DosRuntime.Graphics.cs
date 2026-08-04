using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// Line drawing for SCREEN 13 (320x200x256, linear A000:y*320+x), built on the <c>rt_pset</c> pixel
/// primitive next door.
///
/// The parameters travel in memory rather than in registers, which is a departure from the rest of
/// this runtime. <c>LINE</c> takes up to six of them - two endpoints, a colour and a style mask - and
/// there are not six free registers on an 8086 once the drawing loop has claimed its own. The cells
/// are written by the caller immediately before the call, exactly as the ON ERROR triple is.
///
/// <c>rt_gx1</c>/<c>rt_gy1</c> double as PB's "last point referenced", which is what makes
/// <c>LINE -(x, y)</c> mean anything: with no start point the line begins wherever the previous
/// graphics statement left off. Every entry here updates it, and so does <c>PSET</c>.
/// </summary>
public sealed partial class DosRuntime {

  /// <summary>Bresenham line between the two points in the parameter cells.</summary>
  public Label Line { get; private set; } = null!;

  /// <summary>The four edges of the rectangle the two points span (LINE ... , B).</summary>
  public Label LineBox { get; private set; } = null!;

  /// <summary>The solid rectangle the two points span (LINE ... , BF).</summary>
  public Label LineFill { get; private set; } = null!;

  /// <summary>The full circle of radius rt_gr about (rt_gx1, rt_gy1) - the midpoint algorithm.</summary>
  public Label Circle { get; private set; } = null!;

  private void EmitGraphicsProcedures(Assembler asm) {
    this.EmitLine(asm);
    this.EmitLineBox(asm);
    this.EmitLineFill(asm);
    this.EmitCircle(asm);
  }

  /// <summary>
  /// <c>rt_circle</c>: the midpoint circle algorithm, which needs no trigonometry and no division -
  /// one decision variable and a walk of the first octant, mirrored into the other seven.
  ///
  /// Unlike <c>rt_pset</c>, which trusts its caller, this one CLIPS. A circle is the first graphics
  /// primitive whose plotted points are computed rather than given, so a centre near an edge produces
  /// coordinates the caller never wrote and could not have checked; without the clip those wrap round
  /// the frame buffer and scribble on the opposite side of the screen, or outside it.
  /// </summary>
  private void EmitCircle(Assembler asm) {
    // rt_gcx / rt_gcy hold the centre, rt_gr the radius, rt_gx1/rt_gy1 the point being plotted
    var plot = asm.DefineLabel("rt_circle_plot");
    var clipped = asm.DefineLabel();
    {
      // plot: AX = x, BX = y, drawn only when both are on screen
      asm.MarkLabel(plot);
      asm.Cmp(Reg.AX, (Imm)0);
      asm.Jl(clipped);
      asm.Cmp(Reg.AX, 320);
      asm.Jge(clipped);
      asm.Cmp(Reg.BX, (Imm)0);
      asm.Jl(clipped);
      asm.Cmp(Reg.BX, 200);
      asm.Jge(clipped);
      asm.Mov(Reg.DX, Mem.Word(asm.Lbl("rt_gcolor")));
      asm.Call(this.Pset);
      asm.MarkLabel(clipped);
      asm.Ret();
    }

    this.Circle = asm.MarkLabel("rt_circle");
    var loop = asm.DefineLabel();
    var done = asm.DefineLabel();
    var noShrink = asm.DefineLabel();

    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);

    asm.Mov(Reg.SI, Mem.Word(asm.Lbl("rt_gr")));     // SI = x, starts at the radius
    asm.Xor(Reg.DI, Reg.DI);                          // DI = y, starts at zero
    asm.Mov(Reg.AX, 1);
    asm.Sub(Reg.AX, Reg.SI);
    asm.Mov(Mem.Word(asm.Lbl("rt_gerr")), Reg.AX);    // err = 1 - r

    asm.MarkLabel(loop);
    asm.Cmp(Reg.SI, Reg.DI);
    asm.Jl(done);                                     // the octant is walked while x >= y

    // the eight mirror points of (x, y) about the centre
    void Point(bool negateFirst, bool negateSecond, bool swap) {
      asm.Mov(Reg.AX, swap ? Reg.DI : Reg.SI);
      if (negateFirst)
        asm.Neg(Reg.AX);
      asm.Add(Reg.AX, Mem.Word(asm.Lbl("rt_gcx")));
      asm.Mov(Reg.BX, swap ? Reg.SI : Reg.DI);
      if (negateSecond)
        asm.Neg(Reg.BX);
      asm.Add(Reg.BX, Mem.Word(asm.Lbl("rt_gcy")));
      asm.Call(plot);
    }

    Point(false, false, false);   // ( x,  y)
    Point(true, false, false);    // (-x,  y)
    Point(false, true, false);    // ( x, -y)
    Point(true, true, false);     // (-x, -y)
    Point(false, false, true);    // ( y,  x)
    Point(true, false, true);     // (-y,  x)
    Point(false, true, true);     // ( y, -x)
    Point(true, true, true);      // (-y, -x)

    // y++; err += 2y + 1; and when the decision variable turns positive, x--
    asm.Inc(Reg.DI);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gerr")));
    asm.Mov(Reg.CX, Reg.DI);
    asm.Add(Reg.CX, Reg.CX);
    asm.Add(Reg.AX, Reg.CX);
    asm.Inc(Reg.AX);
    asm.Cmp(Reg.AX, (Imm)0);
    asm.Jle(noShrink);
    asm.Dec(Reg.SI);
    asm.Mov(Reg.CX, Reg.SI);
    asm.Add(Reg.CX, Reg.CX);
    asm.Sub(Reg.AX, Reg.CX);
    asm.MarkLabel(noShrink);
    asm.Mov(Mem.Word(asm.Lbl("rt_gerr")), Reg.AX);
    asm.Jmp(loop);

    asm.MarkLabel(done);
    // PB leaves the last point referenced at the centre after a CIRCLE
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gcx")));
    asm.Mov(Mem.Word(asm.Lbl("rt_gx1")), Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gcy")));
    asm.Mov(Mem.Word(asm.Lbl("rt_gy1")), Reg.AX);

    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Ret();
  }

  /// <summary>
  /// <c>rt_line</c>: the segment from (rt_gx1, rt_gy1) to (rt_gx2, rt_gy2) in rt_gcolor, masked by
  /// rt_gstyle. Bresenham's integer form - no division, no multiplication, one error accumulator.
  ///
  /// The style mask is PB's dotted-line control: bit 15 is consulted for each pixel and the mask
  /// rotates left, so a solid line is &amp;HFFFF (every bit set) and &amp;HF0F0 alternates four on, four
  /// off. Rotating rather than indexing is what makes the pattern continue across a polyline.
  /// </summary>
  private void EmitLine(Assembler asm) {
    this.Line = asm.MarkLabel("rt_line");
    var loop = asm.DefineLabel();
    var noStep = asm.DefineLabel();
    var skipPixel = asm.DefineLabel();
    var done = asm.DefineLabel();
    var xPositive = asm.DefineLabel();
    var yPositive = asm.DefineLabel();

    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);

    // dx = |x2-x1|, sx = sign; the same for y. SI and DI carry the two step directions.
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gx2")));
    asm.Sub(Reg.AX, Mem.Word(asm.Lbl("rt_gx1")));
    asm.Mov(Reg.SI, 1);
    asm.Cmp(Reg.AX, (Imm)0);
    asm.Jge(xPositive);
    asm.Neg(Reg.AX);
    asm.Mov(Reg.SI, 0xFFFF);                       // -1
    asm.MarkLabel(xPositive);
    asm.Mov(Reg.CX, Reg.AX);                       // CX = dx

    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gy2")));
    asm.Sub(Reg.AX, Mem.Word(asm.Lbl("rt_gy1")));
    asm.Mov(Reg.DI, 1);
    asm.Cmp(Reg.AX, (Imm)0);
    asm.Jge(yPositive);
    asm.Neg(Reg.AX);
    asm.Mov(Reg.DI, 0xFFFF);
    asm.MarkLabel(yPositive);
    asm.Mov(Reg.DX, Reg.AX);                       // DX = dy

    // err = dx - dy, kept in a cell so the loop can use every register for the walk
    asm.Mov(Reg.AX, Reg.CX);
    asm.Sub(Reg.AX, Reg.DX);
    asm.Mov(Mem.Word(asm.Lbl("rt_gerr")), Reg.AX);
    asm.Mov(Mem.Word(asm.Lbl("rt_gsx")), Reg.SI);
    asm.Mov(Mem.Word(asm.Lbl("rt_gsy")), Reg.DI);
    asm.Mov(Mem.Word(asm.Lbl("rt_gdx")), Reg.CX);
    asm.Mov(Mem.Word(asm.Lbl("rt_gdy")), Reg.DX);

    asm.MarkLabel(loop);
    // plot the current point when the style mask's top bit is set, then rotate the mask
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gstyle")));
    asm.Rol(Reg.AX, 1);
    asm.Mov(Mem.Word(asm.Lbl("rt_gstyle")), Reg.AX);
    asm.Test(Reg.AX, (Imm)1);                      // after ROL, bit 0 holds what bit 15 was
    asm.Jz(skipPixel);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gx1")));
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_gy1")));
    asm.Mov(Reg.DX, Mem.Word(asm.Lbl("rt_gcolor")));
    asm.Call(this.Pset);
    asm.MarkLabel(skipPixel);

    // reached the far end?
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gx1")));
    asm.Cmp(Reg.AX, Mem.Word(asm.Lbl("rt_gx2")));
    asm.Jne(noStep);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gy1")));
    asm.Cmp(Reg.AX, Mem.Word(asm.Lbl("rt_gy2")));
    asm.Je(done);
    asm.MarkLabel(noStep);

    // e2 = 2*err; step x when e2 > -dy, step y when e2 < dx
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gerr")));
    asm.Mov(Reg.BX, Reg.AX);
    asm.Add(Reg.BX, Reg.AX);                       // BX = e2
    var noX = asm.DefineLabel();
    var noY = asm.DefineLabel();
    asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_gdy")));
    asm.Neg(Reg.CX);
    asm.Cmp(Reg.BX, Reg.CX);
    asm.Jle(noX);
    asm.Sub(Reg.AX, Mem.Word(asm.Lbl("rt_gdy")));
    asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_gx1")));
    asm.Add(Reg.CX, Mem.Word(asm.Lbl("rt_gsx")));
    asm.Mov(Mem.Word(asm.Lbl("rt_gx1")), Reg.CX);
    asm.MarkLabel(noX);
    asm.Cmp(Reg.BX, Mem.Word(asm.Lbl("rt_gdx")));
    asm.Jge(noY);
    asm.Add(Reg.AX, Mem.Word(asm.Lbl("rt_gdx")));
    asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_gy1")));
    asm.Add(Reg.CX, Mem.Word(asm.Lbl("rt_gsy")));
    asm.Mov(Mem.Word(asm.Lbl("rt_gy1")), Reg.CX);
    asm.MarkLabel(noY);
    asm.Mov(Mem.Word(asm.Lbl("rt_gerr")), Reg.AX);
    asm.Jmp(loop);

    asm.MarkLabel(done);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Ret();
  }

  /// <summary>
  /// <c>rt_linebox</c>: the four edges of the rectangle spanned by the two points. Each edge is a
  /// call to rt_line, which consumes the start cell as it walks - so every edge restores the corner
  /// it starts from first.
  /// </summary>
  private void EmitLineBox(Assembler asm) {
    this.LineBox = asm.MarkLabel("rt_linebox");
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);

    // the corners have to be latched: rt_line advances rt_gx1/rt_gy1 to the far end as it draws
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gx1")));
    asm.Mov(Mem.Word(asm.Lbl("rt_gbx1")), Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gy1")));
    asm.Mov(Mem.Word(asm.Lbl("rt_gby1")), Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gx2")));
    asm.Mov(Mem.Word(asm.Lbl("rt_gbx2")), Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gy2")));
    asm.Mov(Mem.Word(asm.Lbl("rt_gby2")), Reg.AX);

    void Edge(string x1, string y1, string x2, string y2) {
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl(x1)));
      asm.Mov(Mem.Word(asm.Lbl("rt_gx1")), Reg.AX);
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl(y1)));
      asm.Mov(Mem.Word(asm.Lbl("rt_gy1")), Reg.AX);
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl(x2)));
      asm.Mov(Mem.Word(asm.Lbl("rt_gx2")), Reg.AX);
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl(y2)));
      asm.Mov(Mem.Word(asm.Lbl("rt_gy2")), Reg.AX);
      asm.Call(this.Line);
    }

    Edge("rt_gbx1", "rt_gby1", "rt_gbx2", "rt_gby1");   // top
    Edge("rt_gbx2", "rt_gby1", "rt_gbx2", "rt_gby2");   // right
    Edge("rt_gbx2", "rt_gby2", "rt_gbx1", "rt_gby2");   // bottom
    Edge("rt_gbx1", "rt_gby2", "rt_gbx1", "rt_gby1");   // left

    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Ret();
  }

  /// <summary>
  /// <c>rt_linefill</c>: the solid rectangle spanned by the two points, drawn as horizontal spans so
  /// the style mask never applies - a filled box is filled whatever the mask says, which is what the
  /// genuine BF does.
  /// </summary>
  private void EmitLineFill(Assembler asm) {
    this.LineFill = asm.MarkLabel("rt_linefill");
    var rowLoop = asm.DefineLabel();
    var colLoop = asm.DefineLabel();
    var rowDone = asm.DefineLabel();
    var yOrdered = asm.DefineLabel();
    var xOrdered = asm.DefineLabel();

    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);

    // normalize the corners, so a rectangle given right-to-left or bottom-to-top still fills
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gx1")));
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_gx2")));
    asm.Cmp(Reg.AX, Reg.BX);
    asm.Jle(xOrdered);
    asm.Xchg(Reg.AX, Reg.BX);
    asm.MarkLabel(xOrdered);
    asm.Mov(Mem.Word(asm.Lbl("rt_gbx1")), Reg.AX);
    asm.Mov(Mem.Word(asm.Lbl("rt_gbx2")), Reg.BX);

    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gy1")));
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_gy2")));
    asm.Cmp(Reg.AX, Reg.BX);
    asm.Jle(yOrdered);
    asm.Xchg(Reg.AX, Reg.BX);
    asm.MarkLabel(yOrdered);
    asm.Mov(Mem.Word(asm.Lbl("rt_gby1")), Reg.AX);
    asm.Mov(Mem.Word(asm.Lbl("rt_gby2")), Reg.BX);

    var nextRow = asm.DefineLabel();
    asm.Mov(Reg.SI, Mem.Word(asm.Lbl("rt_gby1")));   // SI = current row
    asm.MarkLabel(rowLoop);
    asm.Cmp(Reg.SI, Mem.Word(asm.Lbl("rt_gby2")));
    asm.Jg(rowDone);
    asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_gbx1")));   // CX = current column
    asm.MarkLabel(colLoop);
    asm.Cmp(Reg.CX, Mem.Word(asm.Lbl("rt_gbx2")));
    asm.Jg(nextRow);
    asm.Mov(Reg.AX, Reg.CX);
    asm.Mov(Reg.BX, Reg.SI);
    asm.Mov(Reg.DX, Mem.Word(asm.Lbl("rt_gcolor")));
    asm.Call(this.Pset);
    asm.Inc(Reg.CX);
    asm.Jmp(colLoop);
    asm.MarkLabel(nextRow);
    asm.Inc(Reg.SI);
    asm.Jmp(rowLoop);
    asm.MarkLabel(rowDone);

    // the last point referenced ends at the far corner, as it does after any LINE
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gx2")));
    asm.Mov(Mem.Word(asm.Lbl("rt_gx1")), Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gy2")));
    asm.Mov(Mem.Word(asm.Lbl("rt_gy1")), Reg.AX);

    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Ret();
  }
}
