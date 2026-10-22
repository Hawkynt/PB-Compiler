using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Numerics;

namespace PowerBasic.Compiler.Tests.Exec;

[TestFixture]
public sealed class Cpu8086X87PrecisionTests {

  private static Extended80 Execute(byte instruction, params Extended80[] operands) {
    var asm = new Assembler();
    asm.Db(new byte[0x100]);
    var body = asm.DefineLabel();
    asm.Jmp(body);
    var input = asm.MarkLabel("input");
    foreach (var operand in operands)
      asm.Db(operand.ToBytes());
    var output = asm.MarkLabel("output");
    asm.Db(new byte[10]);
    asm.MarkLabel(body);
    for (var i = operands.Length - 1; i >= 0; --i)
      asm.Fld(Mem.Tbyte(input, i * 10));
    asm.Db(0xD9, instruction);
    if (instruction == 0xF2)
      asm.Fstp(St.St0); // FPTAN pushes 1 above the tangent.
    asm.Fstp(Mem.Tbyte(output));
    asm.Mov(Reg.AX, 0x4C00);
    asm.Int(0x21);
    var cpu = Cpu8086.Run(asm.ToArray()[0x100..], exactFloatingPoint: true);
    return Extended80.FromBytes(Enumerable.Range(0, 10)
      .Select(i => cpu.MemoryAt(0x100, output.Position + i)).ToArray());
  }

  [TestCase(0xE9, 0x4000, 0xD49A784BCD1B8AFEUL)]
  [TestCase(0xEA, 0x3FFF, 0xB8AA3B295C17F0BCUL)]
  [TestCase(0xEB, 0x4000, 0xC90FDAA22168C235UL)]
  [TestCase(0xEC, 0x3FFD, 0x9A209A84FBCFF799UL)]
  [TestCase(0xED, 0x3FFE, 0xB17217F7D1CF79ACUL)]
  public void Constant_GivenExtendedExecution_ThenRetainsItsFullSignificand(
      int instruction, int exponent, ulong significand) {
    var expected = BitConverter.GetBytes(significand).Concat(BitConverter.GetBytes((ushort)exponent)).ToArray();
    Assert.That(Execute((byte)instruction).ToBytes(), Is.EqualTo(expected));
  }

  [Test]
  public void Remainder_GivenAValueBelowTheDivisor_ThenKeepsTheDividend() {
    Assert.That(Execute(0xF8, Extended80.One, Extended80.FromDouble(Math.PI / 2)).ToBytes(),
      Is.EqualTo(Extended80.One.ToBytes()));
  }

  [Test]
  public void Tangent_GivenAHalfRadian_ThenReturnsItsRatio() {
    Assert.That(Execute(0xF2, Extended80.FromDouble(0.5)).ToDouble(),
      Is.EqualTo(Math.Tan(0.5)).Within(1e-15));
  }

  [Test]
  public void Logarithm_GivenAnIncrementBelowBinary64Resolution_ThenDoesNotBecomeZero() {
    var increment = Extended80.FromDouble(Math.ScaleB(1, -53));
    var value = Execute(0xF1, Extended80.Add(Extended80.One, increment), Extended80.One);
    Assert.That(value.ToDouble(), Is.EqualTo(Math.ScaleB(Math.Log2(Math.E), -53)).Within(1e-31));
  }

  [TestCase(0xF2)]
  [TestCase(0xFB)]
  public void Transcendental_GivenAnOutOfRangeArgument_ThenSetsC2WithoutPushingOrConsumingItsNeighbor(int instruction) {
    var asm = new Assembler();
    asm.Db(new byte[0x100]);
    var body = asm.DefineLabel();
    asm.Jmp(body);
    var input = asm.MarkLabel("input");
    var large = Extended80.FromDouble(Math.ScaleB(1, 64));
    asm.Db(large.ToBytes());
    var neighbor = asm.MarkLabel("neighbor");
    asm.Dt(13.0);
    var output = asm.MarkLabel("output");
    asm.Db(new byte[20]);
    var status = asm.MarkLabel("status");
    asm.Dw(0);
    asm.MarkLabel(body);
    asm.Fld(Mem.Tbyte(neighbor));
    asm.Fld(Mem.Tbyte(input));
    asm.Db(0xD9, (byte)instruction);
    asm.FstswAx();
    asm.Mov(Mem.Word(status), Reg.AX);
    asm.Fstp(Mem.Tbyte(output));
    asm.Fstp(Mem.Tbyte(output, 10));
    asm.Mov(Reg.AX, 0x4C00);
    asm.Int(0x21);
    var cpu = Cpu8086.Run(asm.ToArray()[0x100..], exactFloatingPoint: true);
    Assert.Multiple(() => {
      Assert.That(cpu.MemoryAt(0x100, status.Position + 1) & 4, Is.EqualTo(4), "C2 reports range refusal");
      Assert.That(Enumerable.Range(0, 10).Select(i => cpu.MemoryAt(0x100, output.Position + i)),
        Is.EqualTo(large.ToBytes()));
      Assert.That(Enumerable.Range(0, 10).Select(i => cpu.MemoryAt(0x100, output.Position + 10 + i)),
        Is.EqualTo(Extended80.FromInt64(13).ToBytes()));
    });
  }
}
