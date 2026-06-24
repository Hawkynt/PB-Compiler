using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

/// <summary>
/// The assembler-level instruction scheduler (<see cref="Assembler.RunSchedule"/>): reorders contiguous
/// fixup/label-free windows of recorded MOV/ALU instructions to group memory and ALU operations while
/// preserving every register/flags/memory dependency. It runs on the final byte stream (downstream of
/// every codegen transform), so it sees fully unrolled / inlined / const-folded code; output-preserving.
/// </summary>
[TestFixture]
public sealed class AssemblerScheduleTests {

  [Test]
  public void RunSchedule_GivenIndependentLoadAluLoad_ThenGroupsTheLoads() {
    var asm = new Assembler { EnableSchedule = true };
    asm.Mov(Reg.AX, Mem.Word(Reg.BP, 2));   // load m1 -> AX        (memory)
    asm.Add(Reg.BX, Reg.CX);                // BX += CX             (ALU, independent)
    asm.Mov(Reg.DX, Mem.Word(Reg.BP, 4));   // load m2 -> DX        (memory, independent)
    var bytes = asm.ToArray();
    // grouped: both independent loads first, then the ALU -> 8B 46 02 | 8B 56 04 | 01 CB
    Assert.That(bytes, Is.EqualTo(new byte[] { 0x8B, 0x46, 0x02, 0x8B, 0x56, 0x04, 0x01, 0xCB }));
  }

  [Test]
  public void RunSchedule_GivenDependentChain_ThenOrderPreserved() {
    var asm = new Assembler { EnableSchedule = true };
    asm.Mov(Reg.AX, Mem.Word(Reg.BP, 2));   // AX = m1
    asm.Add(Reg.AX, Reg.CX);                // AX += CX   (RAW on AX)
    asm.Mov(Mem.Word(Reg.BP, 4), Reg.AX);   // m2 = AX    (RAW on AX)
    var bytes = asm.ToArray();
    Assert.That(bytes, Is.EqualTo(new byte[] { 0x8B, 0x46, 0x02, 0x01, 0xC8, 0x89, 0x46, 0x04 }));
  }

  [Test]
  public void RunSchedule_GivenAliasingStore_ThenNotHoistedPastIt() {
    // a store to [BP+2] and a later load of the same cell must keep their order (RAW through memory)
    var asm = new Assembler { EnableSchedule = true };
    asm.Mov(Mem.Word(Reg.BP, 2), Reg.AX);   // m = AX     (mem write)
    asm.Add(Reg.CX, Reg.DX);                // CX += DX   (ALU, independent)
    asm.Mov(Reg.BX, Mem.Word(Reg.BP, 2));   // BX = m     (mem read of the same cell -> after the store)
    var bytes = asm.ToArray();
    // the load of [BP+2] must stay after its store; grouping may put the ALU last
    var storeAt = IndexOf(bytes, [0x89, 0x46, 0x02]);
    var loadAt = IndexOf(bytes, [0x8B, 0x5E, 0x02]);
    Assert.That(storeAt, Is.GreaterThanOrEqualTo(0).And.LessThan(loadAt), "the aliasing load stays after the store");
  }

  [Test]
  public void RunSchedule_GivenIndependentChainsAcrossIncAndShift_ThenInterleaves() {
    // two independent register chains - a SHL on BX and an INC on DX - sit between two independent
    // loads. Before coverage was widened SHL/INC were barriers that split the window into singletons
    // and nothing moved; now the whole run is one window and the independent loads group to the front.
    var asm = new Assembler { EnableSchedule = true };
    asm.Mov(Reg.AX, Mem.Word(Reg.BP, 2));   // load -> AX   (memory)
    asm.Shl(Reg.BX, 1);                     // BX <<= 1     (independent ALU, was a barrier)
    asm.Inc(Reg.DX);                        // DX++         (independent ALU, was a barrier)
    asm.Mov(Reg.SI, Mem.Word(Reg.BP, 4));   // load -> SI   (memory, independent)
    var bytes = asm.ToArray();
    // the two independent loads are issued first (latency hiding), then the two ALU ops
    Assert.That(bytes[0], Is.EqualTo(0x8B), "an independent load leads");
    var firstLoad = IndexOf(bytes, [0x8B, 0x46, 0x02]);
    var secondLoad = IndexOf(bytes, [0x8B, 0x76, 0x04]);
    var shl = IndexOf(bytes, [0xD1, 0xE3]);
    Assert.That(firstLoad, Is.GreaterThanOrEqualTo(0));
    Assert.That(secondLoad, Is.GreaterThanOrEqualTo(0));
    Assert.That(System.Math.Max(firstLoad, secondLoad), Is.LessThan(shl), "both loads precede the shift");
  }

  [Test]
  public void RunSchedule_GivenShiftThenFlagDependentAdc_ThenOrderPreserved() {
    // a SHL writes flags and a following ADC reads them - the scheduler must not reorder across the
    // flag dependency even though the registers are disjoint.
    var asm = new Assembler { EnableSchedule = true };
    asm.Mov(Reg.AX, Mem.Word(Reg.BP, 2));   // AX = m
    asm.Shl(Reg.AX, 1);                     // AX <<= 1   (writes flags)
    asm.Adc(Reg.BX, Reg.CX);                // BX += CX + CF  (reads flags -> must stay after the SHL)
    var bytes = asm.ToArray();
    var shl = IndexOf(bytes, [0xD1, 0xE0]);
    var adc = IndexOf(bytes, [0x11, 0xCB]);
    Assert.That(shl, Is.GreaterThanOrEqualTo(0).And.LessThan(adc), "the flag-consuming ADC stays after the SHL");
  }

  [Test]
  public void RunSchedule_WhenDisabled_ThenStreamUntouched() {
    var asm = new Assembler();   // EnableSchedule = false
    asm.Mov(Reg.AX, Mem.Word(Reg.BP, 2));
    asm.Add(Reg.BX, Reg.CX);
    asm.Mov(Reg.DX, Mem.Word(Reg.BP, 4));
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0x8B, 0x46, 0x02, 0x01, 0xCB, 0x8B, 0x56, 0x04 }));
  }

  private static int IndexOf(byte[] haystack, byte[] needle) {
    for (var i = 0; i + needle.Length <= haystack.Length; ++i) {
      var hit = true;
      for (var k = 0; k < needle.Length; ++k)
        if (haystack[i + k] != needle[k]) { hit = false; break; }
      if (hit)
        return i;
    }
    return -1;
  }
}
