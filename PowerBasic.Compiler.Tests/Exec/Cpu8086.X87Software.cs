using System.Collections.Concurrent;
using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.Tests.Exec;

public sealed partial class Cpu8086 {

  private sealed record SoftwareX87Program(byte[] Image, int Inputs, int Outputs, int Control, int Status);

  private static readonly ConcurrentDictionary<byte, SoftwareX87Program> _softwareX87Programs = new();

  // Reuse the production integer-only engine rather than maintain a second transcendental library.
  // Its approximations are not a model of a particular Intel microcode revision, but unlike the
  // fast host-double mode they retain every operand bit that MBF64/EXT programs can observe.
  private bool TryExecuteSoftwareX87(byte instruction) {
    var (inputCount, outputCount) = instruction switch {
      0xE9 or 0xEA or 0xEB or 0xEC or 0xED => (0, 1),
      0xF0 or 0xFE or 0xFF => (1, 1),
      0xF2 or 0xFB => (1, 2),
      0xF1 or 0xF3 or 0xF9 => (2, 1),
      0xF8 or 0xF5 or 0xFD => (2, 2),
      _ => (0, 0),
    };
    if (outputCount == 0)
      return false;

    var program = _softwareX87Programs.GetOrAdd(instruction,
      operation => BuildSoftwareX87Program(operation, inputCount, outputCount));
    var cpu = new Cpu8086(exactFloatingPoint: false);
    cpu.Load(program.Image);
    var segment = cpu._ds;
    cpu.WriteWord(Linear(segment, (ushort)program.Control), this._controlWord);
    for (var i = 0; i < inputCount; ++i)
      cpu.WriteExtended(Linear(segment, (ushort)(program.Inputs + i * 10)), this.St(i).AsExtended);
    cpu.Execute(20_000_000);

    var status = cpu.ReadWord(Linear(segment, (ushort)program.Status));
    this._status = (ushort)((this._status & ~0x4700) | (status & 0x473F));
    // FPTAN/FSINCOS do not push when range reduction is refused. The emitted kernel has
    // space for both ordinary results, but only its unchanged ST0 is valid in that case.
    if (instruction is 0xF2 or 0xFB && (status & 0x0400) != 0)
      outputCount = 1;
    for (var i = 0; i < inputCount; ++i)
      this.FPop();
    for (var i = outputCount - 1; i >= 0; --i)
      this.FPush(X87Value.Extended(cpu.ReadExtended(Linear(segment, (ushort)(program.Outputs + i * 10)))));
    return true;
  }

  private static SoftwareX87Program BuildSoftwareX87Program(byte instruction, int inputCount, int outputCount) {
    var asm = new Assembler();
    // Labels are COM segment offsets, while Load expects the file without its PSP prefix.
    asm.Db(new byte[0x100]);
    var body = asm.DefineLabel();
    asm.Jmp(body);
    var inputs = asm.MarkLabel("inputs");
    asm.Db(new byte[inputCount * 10]);
    var outputs = asm.MarkLabel("outputs");
    asm.Db(new byte[outputCount * 10]);
    var control = asm.MarkLabel("control");
    asm.Dw(0x037F);
    var status = asm.MarkLabel("status");
    asm.Dw(0);
    asm.MarkLabel(body);
    asm.X87Sink = new SoftwareX87Engine(asm);
    asm.Fldcw(Mem.Word(control));
    for (var i = inputCount - 1; i >= 0; --i)
      asm.Fld(Mem.Tbyte(inputs, i * 10));
    if (!asm.X87Sink.TryEmitSimple(0xD9, instruction))
      throw new Cpu8086Exception($"software x87 cannot execute D9 {instruction:X2}");
    asm.FstswAx();
    asm.Mov(Mem.Word(status), Reg.AX);
    for (var i = 0; i < outputCount; ++i)
      asm.Fstp(Mem.Tbyte(outputs, i * 10));
    asm.Mov(Reg.AX, 0x4C00);
    asm.Int(0x21);
    return new(asm.ToArray()[0x100..], inputs.Position, outputs.Position, control.Position, status.Position);
  }
}
