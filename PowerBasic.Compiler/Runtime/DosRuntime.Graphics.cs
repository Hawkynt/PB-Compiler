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

  /// <summary>CIRCLE's arc and aspect forms: the parametric walk from rt_gastart to rt_gaend.</summary>
  public Label Arc { get; private set; } = null!;

  /// <summary>
  /// <c>rt_arc</c>: the angle stepped from start to end, plotting cosine and sine of it.
  ///
  /// The full circle next door is the integer midpoint walk and stays that way - it is exact and
  /// needs no x87. This is for the forms that one cannot express: a start and end angle, and an
  /// aspect ratio. Parametric rather than a midpoint ELLIPSE because aspect then costs nothing (it
  /// simply scales the sine) and no 32-bit arithmetic is needed, where the ellipse's radius squared
  /// leaves 16 bits at a radius of 181 on a screen 320 wide.
  ///
  /// The step is one over the radius, which is about a pixel of arc per plot - fine enough to leave
  /// no gaps and coarse enough not to plot the same pixel a dozen times.
  ///
  /// y is SUBTRACTED because the screen counts rows downward while the angle turns anticlockwise,
  /// which is what makes an arc from 0 to pi/2 the upper right quadrant rather than the lower.
  /// </summary>
  private void EmitArc(Assembler asm) {
    var loop = asm.DefineLabel();
    var done = asm.DefineLabel();

    this.Arc = asm.MarkLabel("rt_arc");
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.DX);

    // step = 1/r, guarding a zero radius so a degenerate circle plots its centre and stops
    asm.Cmp(Mem.Word(asm.Lbl("rt_gr")), (Imm)0);
    asm.Jle(done);
    asm.Fld1();
    asm.Fidiv(Mem.Word(asm.Lbl("rt_gr")));
    asm.Fstp(Mem.Qword(asm.Lbl("rt_gastep")));

    asm.Fld(Mem.Qword(asm.Lbl("rt_gastart")));            // ST0 = t, kept for the whole walk
    asm.MarkLabel(loop);
    asm.Fcom(Mem.Qword(asm.Lbl("rt_gaend")));
    asm.Fstsw(Mem.Word(this._scratch, 16));
    asm.Mov(Reg.AX, Mem.Word(this._scratch, 16));
    asm.Sahf();
    asm.Ja(done);                                          // past the end angle

    asm.Fld(St.St0);                                       // x = cx + r*cos t
    asm.Fcos();
    asm.Fimul(Mem.Word(asm.Lbl("rt_gr")));
    asm.Fiadd(Mem.Word(asm.Lbl("rt_gcx")));
    asm.Fistp(Mem.Word(asm.Lbl("rt_gax")));

    asm.Fld(St.St0);                                       // y = cy - r*aspect*sin t
    asm.Fsin();
    asm.Fmul(Mem.Qword(asm.Lbl("rt_gaspect")));
    asm.Fimul(Mem.Word(asm.Lbl("rt_gr")));
    asm.Fchs();
    asm.Fiadd(Mem.Word(asm.Lbl("rt_gcy")));
    asm.Fistp(Mem.Word(asm.Lbl("rt_gay")));

    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gax")));
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_gay")));
    asm.Mov(Reg.DX, Mem.Word(asm.Lbl("rt_gcolor")));
    asm.Call(this.Pset);

    asm.Fadd(Mem.Qword(asm.Lbl("rt_gastep")));
    asm.Jmp(loop);

    asm.MarkLabel(done);
    asm.Fstp(St.St0);                                      // drop t

    // the last point referenced ends at the centre, as it does after a full CIRCLE
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gcx")));
    asm.Mov(Mem.Word(asm.Lbl("rt_gx1")), Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gcy")));
    asm.Mov(Mem.Word(asm.Lbl("rt_gy1")), Reg.AX);

    asm.Pop(Reg.DX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Ret();

    foreach (var cell in new[] { "rt_gax", "rt_gay" }) {
      asm.MarkLabel(cell);
      asm.Dw(0);
    }
    foreach (var cell in new[] { "rt_gastart", "rt_gaend", "rt_gaspect", "rt_gastep" }) {
      asm.MarkLabel(cell);
      asm.Dq(0.0);
    }
  }

  /// <summary>The flood fill bounded by rt_gpbord, seeded at (rt_gx1, rt_gy1) - PAINT.</summary>
  public Label Paint { get; private set; } = null!;

  /// <summary>
  /// Spans the fill can have outstanding at once. A scanline fill pushes one seed per contiguous run
  /// on the row above and below the one it just filled, so the depth is the number of separate runs
  /// still to visit rather than the pixel count - a filled rectangle never needs more than two.
  /// Shapes with many disjoint fingers need more, and QuickBASIC answers those with "Out of memory"
  /// rather than a partial fill, so this does too.
  ///
  /// The size is a compromise the runtime trimmer only half solves. This stack is four bytes an entry
  /// of image, and the trimmer drops the whole PAINT section for a program that does not fill - but
  /// only with the optimizer on, so an unoptimized build carries it whatever it draws. 128 spans is
  /// half a kilobyte, comfortably more than any convex shape needs. Putting the stack on the machine
  /// stack instead (SUB SP at entry, SS:BP-relative addressing) would cost the image nothing at all
  /// and is the right answer; it is more assembly than this first cut wanted to be.
  /// </summary>
  private const int _paintSpans = 128;

  private void EmitGraphicsProcedures(Assembler asm) {
    this.EmitLine(asm);
    this.EmitLineBox(asm);
    this.EmitLineFill(asm);
    this.EmitCircle(asm);
    this.EmitArc(asm);
  }

  /// <summary>GET: the rectangle between the two points captured into an array.</summary>
  public Label GGet { get; private set; } = null!;

  /// <summary>PUT: the captured rectangle drawn back at a point, under one of the five actions.</summary>
  public Label GPut { get; private set; } = null!;

  /// <summary>
  /// <c>GET (x1,y1)-(x2,y2), a%(0)</c> and <c>PUT (x,y), a%(0)[, verb]</c> - sprite capture and blit.
  ///
  /// The array holds QuickBASIC's layout: a word of width in BITS, a word of height in pixels, then
  /// the bytes row by row. Width is in bits rather than pixels because the format predates 256-colour
  /// modes, where a pixel was a bit; at eight bits per pixel it is simply the pixel count times eight,
  /// and storing it that way is what lets an array DIMmed by the usual 4 + INT((x*bpp+7)/8)*y formula
  /// be the right size.
  ///
  /// PUT's five actions are the point of the statement: XOR is the default because drawing the same
  /// sprite twice erases it, which is how everything moved on screen before there were sprites in
  /// hardware. PRESET is PSET's complement, not "restore".
  ///
  /// The buffer segment and offset travel in cells rather than ES:DI, because rt_point and rt_pset
  /// both take ES for the frame buffer - they save and restore it, so the cursor survives the call,
  /// but only if it was not living in ES to begin with.
  /// </summary>
  private void EmitGetPutProcedures(Assembler asm) {
    this.GGet = asm.MarkLabel("rt_gget");
    {
      var rowLoop = asm.DefineLabel();
      var colLoop = asm.DefineLabel();
      var nextRow = asm.DefineLabel();
      var rowsDone = asm.DefineLabel();
      var xOrdered = asm.DefineLabel();
      var yOrdered = asm.DefineLabel();
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Push(Reg.SI);
      asm.Push(Reg.DI);
      asm.Push(Reg.ES);
      asm.Cld();

      // either corner may be given first, exactly as LINE's box takes them
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

      asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_gbufseg")));
      asm.Mov(Reg.DI, Mem.Word(asm.Lbl("rt_gbufofs")));
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gbx2")));
      asm.Sub(Reg.AX, Mem.Word(asm.Lbl("rt_gbx1")));
      asm.Inc(Reg.AX);                                  // width in pixels
      asm.Shl(Reg.AX, 3);                               // ... stored as bits, at 8 per pixel
      asm.Stosw();
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gby2")));
      asm.Sub(Reg.AX, Mem.Word(asm.Lbl("rt_gby1")));
      asm.Inc(Reg.AX);
      asm.Stosw();

      asm.Mov(Reg.SI, Mem.Word(asm.Lbl("rt_gby1")));
      asm.MarkLabel(rowLoop);
      asm.Cmp(Reg.SI, Mem.Word(asm.Lbl("rt_gby2")));
      asm.Jg(rowsDone);
      asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_gbx1")));
      asm.MarkLabel(colLoop);
      asm.Cmp(Reg.CX, Mem.Word(asm.Lbl("rt_gbx2")));
      asm.Jg(nextRow);
      asm.Mov(Reg.AX, Reg.CX);
      asm.Mov(Reg.BX, Reg.SI);
      asm.Push(Reg.CX);
      asm.Call(this.Point);                             // AL = the pixel; ES and DI survive it
      asm.Pop(Reg.CX);
      asm.Stosb();
      asm.Inc(Reg.CX);
      asm.Jmp(colLoop);
      asm.MarkLabel(nextRow);
      asm.Inc(Reg.SI);
      asm.Jmp(rowLoop);
      asm.MarkLabel(rowsDone);

      asm.Pop(Reg.ES);
      asm.Pop(Reg.DI);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    this.GPut = asm.MarkLabel("rt_gput");
    {
      var rowLoop = asm.DefineLabel();
      var colLoop = asm.DefineLabel();
      var nextRow = asm.DefineLabel();
      var rowsDone = asm.DefineLabel();
      var combined = asm.DefineLabel();
      var doPreset = asm.DefineLabel();
      var doAnd = asm.DefineLabel();
      var doOr = asm.DefineLabel();
      var doXor = asm.DefineLabel();
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Push(Reg.SI);
      asm.Push(Reg.DI);
      asm.Push(Reg.ES);
      asm.Cld();

      asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_gbufseg")));
      asm.Mov(Reg.DI, Mem.Word(asm.Lbl("rt_gbufofs")));
      asm.Mov(Reg.AX, Mem.Word(Reg.DI).Seg(Reg.ES));
      asm.Shr(Reg.AX, 3);                               // bits back to pixels
      asm.Mov(Mem.Word(asm.Lbl("rt_gpw")), Reg.AX);
      asm.Mov(Reg.AX, Mem.Word(Reg.DI, 2).Seg(Reg.ES));
      asm.Mov(Mem.Word(asm.Lbl("rt_gph")), Reg.AX);
      asm.Add(Reg.DI, (Imm)4);

      asm.Xor(Reg.SI, Reg.SI);                          // SI = row within the sprite
      asm.MarkLabel(rowLoop);
      asm.Cmp(Reg.SI, Mem.Word(asm.Lbl("rt_gph")));
      asm.Jge(rowsDone);
      asm.Xor(Reg.CX, Reg.CX);                          // CX = column within the sprite
      asm.MarkLabel(colLoop);
      asm.Cmp(Reg.CX, Mem.Word(asm.Lbl("rt_gpw")));
      asm.Jge(nextRow);

      asm.Mov(Reg.DL, Mem.Byte(Reg.DI).Seg(Reg.ES));    // the sprite byte
      asm.Inc(Reg.DI);
      asm.Mov(Reg.AX, Reg.CX);
      asm.Add(Reg.AX, Mem.Word(asm.Lbl("rt_gx1")));     // target x
      asm.Mov(Reg.BX, Reg.SI);
      asm.Add(Reg.BX, Mem.Word(asm.Lbl("rt_gy1")));     // target y

      // PSET writes the byte through; the other four need what is already on screen
      asm.Cmp(Mem.Word(asm.Lbl("rt_gverb")), (Imm)0);
      asm.Je(combined);
      asm.Cmp(Mem.Word(asm.Lbl("rt_gverb")), (Imm)1);
      asm.Je(doPreset);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Call(this.Point);                             // AL = the screen pixel
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Cmp(Mem.Word(asm.Lbl("rt_gverb")), (Imm)2);
      asm.Je(doAnd);
      asm.Cmp(Mem.Word(asm.Lbl("rt_gverb")), (Imm)3);
      asm.Je(doOr);
      asm.MarkLabel(doXor);
      asm.Xor(Reg.DL, Reg.AL);
      asm.Jmp(combined);
      asm.MarkLabel(doAnd);
      asm.And(Reg.DL, Reg.AL);
      asm.Jmp(combined);
      asm.MarkLabel(doOr);
      asm.Or(Reg.DL, Reg.AL);
      asm.Jmp(combined);
      asm.MarkLabel(doPreset);
      asm.Not(Reg.DL);                                  // PRESET is PSET's complement
      asm.MarkLabel(combined);

      asm.Mov(Reg.AX, Reg.CX);
      asm.Add(Reg.AX, Mem.Word(asm.Lbl("rt_gx1")));
      asm.Mov(Reg.BX, Reg.SI);
      asm.Add(Reg.BX, Mem.Word(asm.Lbl("rt_gy1")));
      asm.Push(Reg.CX);
      asm.Call(this.Pset);
      asm.Pop(Reg.CX);
      asm.Inc(Reg.CX);
      asm.Jmp(colLoop);
      asm.MarkLabel(nextRow);
      asm.Inc(Reg.SI);
      asm.Jmp(rowLoop);
      asm.MarkLabel(rowsDone);

      asm.Pop(Reg.ES);
      asm.Pop(Reg.DI);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    foreach (var cell in new[] { "rt_gbufseg", "rt_gbufofs", "rt_gverb", "rt_gpw", "rt_gph" }) {
      asm.MarkLabel(cell);
      asm.Dw(0);
    }
  }

  /// <summary>
  /// <c>rt_paint</c>: the scanline flood fill behind <c>PAINT (x, y), paint, border</c>.
  ///
  /// A pixel belongs to the region when it is neither the border colour nor already the paint colour.
  /// The second half of that is what makes the fill terminate - without it a filled pixel is still
  /// fillable and the seeds never run out.
  ///
  /// Scanline rather than four-way: pop a seed, walk left and right to the ends of its run, fill the
  /// whole run at once, then push ONE seed for each contiguous run on the row above and the row
  /// below. Four-way pushes a seed per pixel and would need thousands of them for a shape this one
  /// crosses in a few dozen; the stack below is sized for spans because of it.
  ///
  /// The state lives in memory cells like the rest of this file - a fill needs x, the two run ends,
  /// the row, the row being scanned and a cursor along it, which is more than the 8086 has registers
  /// to spare once the pixel primitives have taken theirs.
  /// </summary>
  internal void EmitPaint(Assembler asm) {
    var fillable = asm.DefineLabel("rt_paint_ok");
    var notFillable = asm.DefineLabel();
    var okDone = asm.DefineLabel();
    var popLoop = asm.DefineLabel();
    var done = asm.DefineLabel();
    var scanLeft = asm.DefineLabel();
    var scanLeftDone = asm.DefineLabel();
    var scanRight = asm.DefineLabel();
    var scanRightDone = asm.DefineLabel();
    var fillRun = asm.DefineLabel();
    var fillDone = asm.DefineLabel();
    var neighbourRow = asm.DefineLabel();
    var scanRow = asm.DefineLabel();
    var scanRowDone = asm.DefineLabel();
    var inRun = asm.DefineLabel();
    var skipRun = asm.DefineLabel();
    var overflow = asm.DefineLabel();

    this.Paint = asm.MarkLabel("rt_paint");
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    // The seed stack comes off the MACHINE stack. As static data it was half a kilobyte of zeros in
    // every image, because the trimmer that drops this whole section only runs with the optimizer on
    // - an unoptimized build carried it whatever it drew. Here it costs nothing until PAINT is
    // called. [BP+DI] is one of the four base+index modes the 8086 has and addresses through SS,
    // which is where BP already points.
    asm.Push(Reg.BP);
    asm.Mov(Reg.BP, Reg.SP);
    asm.Sub(Reg.SP, (Imm)(_paintSpans * 4));

    // seed the stack with the point PAINT was given
    asm.Mov(Mem.Word(asm.Lbl("rt_psp")), (Imm)0);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gx1")));
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_gy1")));
    asm.Mov(Reg.DI, Mem.Word(asm.Lbl("rt_psp")));
    asm.Mov(Mem.Word(Reg.BP, Reg.DI, -_paintSpans * 4), Reg.AX);
    asm.Mov(Mem.Word(Reg.BP, Reg.DI, -_paintSpans * 4 + 2), Reg.BX);
    asm.Add(Mem.Word(asm.Lbl("rt_psp")), (Imm)4);

    asm.MarkLabel(popLoop);
    asm.Cmp(Mem.Word(asm.Lbl("rt_psp")), (Imm)0);
    asm.Je(done);
    asm.Sub(Mem.Word(asm.Lbl("rt_psp")), (Imm)4);
    asm.Mov(Reg.DI, Mem.Word(asm.Lbl("rt_psp")));
    asm.Mov(Reg.AX, Mem.Word(Reg.BP, Reg.DI, -_paintSpans * 4));
    asm.Mov(Reg.BX, Mem.Word(Reg.BP, Reg.DI, -_paintSpans * 4 + 2));
    asm.Mov(Mem.Word(asm.Lbl("rt_px1")), Reg.AX);
    asm.Mov(Mem.Word(asm.Lbl("rt_px2")), Reg.AX);
    asm.Mov(Mem.Word(asm.Lbl("rt_py")), Reg.BX);
    // the run may already have been filled by another seed reaching it first
    asm.Call(fillable);
    asm.Cmp(Reg.AL, (Imm)0);
    asm.Je(popLoop);

    asm.MarkLabel(scanLeft);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_px1")));
    asm.Dec(Reg.AX);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_py")));
    asm.Call(fillable);
    asm.Cmp(Reg.AL, (Imm)0);
    asm.Je(scanLeftDone);
    asm.Dec(Mem.Word(asm.Lbl("rt_px1")));
    asm.Jmp(scanLeft);
    asm.MarkLabel(scanLeftDone);

    asm.MarkLabel(scanRight);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_px2")));
    asm.Inc(Reg.AX);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_py")));
    asm.Call(fillable);
    asm.Cmp(Reg.AL, (Imm)0);
    asm.Je(scanRightDone);
    asm.Inc(Mem.Word(asm.Lbl("rt_px2")));
    asm.Jmp(scanRight);
    asm.MarkLabel(scanRightDone);

    // fill the run before scanning the neighbours, so their fillable tests see it as done
    asm.Mov(Reg.SI, Mem.Word(asm.Lbl("rt_px1")));
    asm.MarkLabel(fillRun);
    asm.Cmp(Reg.SI, Mem.Word(asm.Lbl("rt_px2")));
    asm.Jg(fillDone);
    asm.Mov(Reg.AX, Reg.SI);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_py")));
    asm.Mov(Reg.DX, Mem.Word(asm.Lbl("rt_gcolor")));
    asm.Call(this.Pset);
    asm.Inc(Reg.SI);
    asm.Jmp(fillRun);
    asm.MarkLabel(fillDone);

    // the row above, then the row below
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_py")));
    asm.Dec(Reg.AX);
    asm.Mov(Mem.Word(asm.Lbl("rt_pny")), Reg.AX);
    asm.Mov(Mem.Word(asm.Lbl("rt_pside")), (Imm)0);

    asm.MarkLabel(neighbourRow);
    asm.Mov(Reg.SI, Mem.Word(asm.Lbl("rt_px1")));
    asm.MarkLabel(scanRow);
    asm.Cmp(Reg.SI, Mem.Word(asm.Lbl("rt_px2")));
    asm.Jg(scanRowDone);
    asm.Mov(Reg.AX, Reg.SI);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_pny")));
    asm.Call(fillable);
    asm.Cmp(Reg.AL, (Imm)0);
    asm.Jne(inRun);
    asm.Inc(Reg.SI);
    asm.Jmp(scanRow);

    // the start of a run: push it once, then step over the rest of the run so the same run is not
    // seeded again for every pixel it contains
    asm.MarkLabel(inRun);
    asm.Mov(Reg.DI, Mem.Word(asm.Lbl("rt_psp")));
    asm.Cmp(Reg.DI, (Imm)(_paintSpans * 4));
    asm.Jae(overflow);
    asm.Mov(Reg.AX, Reg.SI);
    asm.Mov(Mem.Word(Reg.BP, Reg.DI, -_paintSpans * 4), Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_pny")));
    asm.Mov(Mem.Word(Reg.BP, Reg.DI, -_paintSpans * 4 + 2), Reg.AX);
    asm.Add(Mem.Word(asm.Lbl("rt_psp")), (Imm)4);

    asm.MarkLabel(skipRun);
    asm.Inc(Reg.SI);
    asm.Cmp(Reg.SI, Mem.Word(asm.Lbl("rt_px2")));
    asm.Jg(scanRowDone);
    asm.Mov(Reg.AX, Reg.SI);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_pny")));
    asm.Call(fillable);
    asm.Cmp(Reg.AL, (Imm)0);
    asm.Jne(skipRun);
    asm.Jmp(scanRow);

    asm.MarkLabel(scanRowDone);
    asm.Cmp(Mem.Word(asm.Lbl("rt_pside")), (Imm)0);
    asm.Jne(popLoop);
    asm.Mov(Mem.Word(asm.Lbl("rt_pside")), (Imm)1);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_py")));
    asm.Inc(Reg.AX);
    asm.Mov(Mem.Word(asm.Lbl("rt_pny")), Reg.AX);
    asm.Jmp(neighbourRow);

    asm.MarkLabel(overflow);
    asm.Mov(Reg.AX, (Imm)7);            // "Out of memory", as QuickBASIC answers a fill it cannot hold
    asm.Call(this.Raise);

    asm.MarkLabel(done);
    asm.Mov(Reg.SP, Reg.BP);
    asm.Pop(Reg.BP);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Ret();

    // AX = x, BX = y -> AL = 1 when the pixel is part of the region still to be filled. Off-screen
    // is not fillable, which is what keeps the walk inside the frame buffer without every caller
    // having to clip.
    asm.MarkLabel(fillable);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Cmp(Reg.AX, (Imm)0);
    asm.Jl(notFillable);
    asm.Cmp(Reg.AX, (Imm)320);
    asm.Jge(notFillable);
    asm.Cmp(Reg.BX, (Imm)0);
    asm.Jl(notFillable);
    asm.Cmp(Reg.BX, (Imm)200);
    asm.Jge(notFillable);
    asm.Call(this.Point);               // AL = colour, AH cleared
    asm.Cmp(Reg.AX, Mem.Word(asm.Lbl("rt_gpbord")));
    asm.Je(notFillable);
    asm.Cmp(Reg.AX, Mem.Word(asm.Lbl("rt_gcolor")));
    asm.Je(notFillable);
    asm.Mov(Reg.AL, (Imm)1);
    asm.Jmp(okDone);
    asm.MarkLabel(notFillable);
    asm.Mov(Reg.AL, (Imm)0);
    asm.MarkLabel(okDone);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Ret();

    // The seed stack and the fill's scratch, placed here rather than in the common data block so the
    // runtime trimmer drops all of it with the graphics section when a program draws nothing.
    foreach (var cell in new[] { "rt_gpbord", "rt_psp", "rt_px1", "rt_px2", "rt_py", "rt_pny", "rt_pside" }) {
      asm.MarkLabel(cell);
      asm.Dw(0);
    }

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
