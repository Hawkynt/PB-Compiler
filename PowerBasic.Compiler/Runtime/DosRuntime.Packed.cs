using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class DosRuntime {

  /// <summary>The packed 16-bit kernels: routine label, the SIMD opcode (0F xx), and the scalar twin.</summary>
  private static readonly (string Label, byte Packed, Action<Assembler> Scalar)[] _packedKernels = [
    ("rt_packed16_add", 0xFD, asm => asm.Add(Reg.AX, Mem.Word(Reg.SI))),    // PADDW
    ("rt_packed16_sub", 0xF9, asm => asm.Sub(Reg.AX, Mem.Word(Reg.SI))),    // PSUBW
    ("rt_packed16_and", 0xDB, asm => asm.And(Reg.AX, Mem.Word(Reg.SI))),    // PAND
    ("rt_packed16_or", 0xEB, asm => asm.Or(Reg.AX, Mem.Word(Reg.SI))),      // POR
    ("rt_packed16_xor", 0xEF, asm => asm.Xor(Reg.AX, Mem.Word(Reg.SI))),    // PXOR
    ("rt_packed16_mul", 0xD5, asm => asm.Imul(Mem.Word(Reg.SI))),           // PMULLW; IMUL keeps the low word in AX
  ];

  /// <summary>
  /// R4 packed kernels, the target half of <c>Ir.Passes.PackedLoopVectorization</c>:
  /// <c>c(i) = a(i) OP b(i)</c> for CX 16-bit elements, DI -&gt; c, BX -&gt; a, SI -&gt; b, all in DS.
  ///
  /// <para>
  /// Each runs as many whole vectors as fit through the widest register the target declares - ZMM,
  /// YMM, XMM or MMX, the same order the direct emitter's vectoriser chose - then the remaining
  /// elements one word at a time. Every packed op is per-lane and wraps exactly like its scalar
  /// instruction, so the result is the loop's. MMX shares the x87 register file and is released with
  /// EMMS before returning; the wider registers are not the FPU's. A target without SIMD still gets the
  /// routine, as the scalar loop alone: the section's labels have to exist for the runtime trimmer's
  /// probe, which emits the runtime for the baseline CPU.
  /// </para>
  /// </summary>
  private void EmitPacked16(Assembler asm) {
    foreach (var (label, packed, scalar) in _packedKernels)
      this.EmitPackedKernel(asm, label, packed, scalar);
    EmitCheckedKernel(asm, "rt_packed16_add_checked", "rt_packed16_add", a => a.Add(Reg.AX, Mem.Word(Reg.SI)));
    EmitCheckedKernel(asm, "rt_packed16_sub_checked", "rt_packed16_sub", a => a.Sub(Reg.AX, Mem.Word(Reg.SI)));
  }

  /// <summary>
  /// O0308: the same registers as the plain kernel, answering AX = 0 when every element's sum or
  /// difference fits (and then computing them packed), or AX = 1 - having written nothing - when one
  /// overflows, so the caller's checked loop runs and raises Error 6 where it always did. The scan is
  /// the scalar instruction itself with JO, which is exactly the condition the loop tests.
  /// </summary>
  private static void EmitCheckedKernel(Assembler asm, string label, string plain, Action<Assembler> operation) {
    asm.MarkLabel(label);
    var scan = asm.DefineLabel();
    var clean = asm.DefineLabel();
    var overflow = asm.DefineLabel();
    asm.Push(Reg.BX);
    asm.Push(Reg.SI);
    asm.Push(Reg.CX);
    asm.Or(Reg.CX, Reg.CX);
    asm.Jz(clean);
    asm.MarkLabel(scan);
    asm.Mov(Reg.AX, Mem.Word(Reg.BX));
    operation(asm);
    asm.Jo(overflow);
    asm.Add(Reg.BX, 2);
    asm.Add(Reg.SI, 2);
    asm.Loop(scan);
    asm.MarkLabel(clean);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.BX);
    asm.Call(asm.Lbl(plain));
    asm.Xor(Reg.AX, Reg.AX);
    asm.Ret();
    asm.MarkLabel(overflow);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.BX);
    asm.Mov(Reg.AX, 1);
    asm.Ret();
  }

  private void EmitPackedKernel(Assembler asm, string label, byte packed, Action<Assembler> scalar) {
    asm.MarkLabel(label);
    var width = this.Target.PackedIntegerWidthBytes;
    if (width > 0) {
      var (vector, laneShift) = width switch {
        64 => (Reg.ZMM0, 5),
        32 => (Reg.YMM0, 4),
        16 => (Reg.XMM0, 3),
        _ => (Reg.MM0, 2),
      };
      var tail = asm.DefineLabel();
      var top = asm.DefineLabel();
      asm.Push(Reg.CX);                            // the element count, for the tail
      for (var k = 0; k < laneShift; ++k)
        asm.Shr(Reg.CX, 1);                         // whole vectors
      asm.Or(Reg.CX, Reg.CX);
      asm.Jz(tail);
      asm.MarkLabel(top);
      switch (width) {
        case 64:
          asm.Vmovdqu512(vector, Mem.At(Reg.BX));
          asm.EvexPacked(packed, vector, vector, Mem.At(Reg.SI));
          asm.Vmovdqu512Store(Mem.At(Reg.DI), vector);
          break;
        case 32:
          asm.Vmovdqu(vector, Mem.At(Reg.BX));
          asm.VexPacked(packed, vector, vector, Mem.At(Reg.SI));
          asm.VmovdquStore(Mem.At(Reg.DI), vector);
          break;
        case 16:
          asm.Movdqu(vector, Mem.At(Reg.BX));
          asm.EmitPacked(packed, vector, Mem.At(Reg.SI));
          asm.MovdquStore(Mem.At(Reg.DI), vector);
          break;
        default:
          asm.Movq(vector, Mem.At(Reg.BX));
          asm.EmitPacked(packed, vector, Mem.At(Reg.SI));
          asm.MovqStore(Mem.At(Reg.DI), vector);
          break;
      }
      asm.Add(Reg.BX, width);
      asm.Add(Reg.SI, width);
      asm.Add(Reg.DI, width);
      asm.Loop(top);
      if (width == 8)
        asm.Emms();                                 // MMX aliases the x87 stack
      asm.MarkLabel(tail);
      asm.Pop(Reg.CX);
      asm.And(Reg.CX, (width / 2) - 1);             // the elements no whole vector covered
    }

    var done = asm.DefineLabel();
    var word = asm.DefineLabel();
    asm.Or(Reg.CX, Reg.CX);
    asm.Jz(done);
    asm.MarkLabel(word);
    asm.Mov(Reg.AX, Mem.Word(Reg.BX));
    scalar(asm);
    asm.Mov(Mem.Word(Reg.DI), Reg.AX);
    asm.Add(Reg.BX, 2);
    asm.Add(Reg.SI, 2);
    asm.Add(Reg.DI, 2);
    asm.Loop(word);
    asm.MarkLabel(done);
    asm.Ret();
  }
}
