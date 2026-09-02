using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

[TestFixture]
public sealed class AssemblerBmiTests {
  private static byte[] Assemble(Action<Assembler> emit) {
    var asm = new Assembler();
    emit(asm);
    return asm.ToArray();
  }

  [TestCase("ANDN", "C4E270F2C2")]
  [TestCase("BEXTR", "C4E270F7C2")]
  [TestCase("BLSR", "C4E278F3CA")]
  [TestCase("BLSMSK", "C4E278F3D2")]
  [TestCase("BLSI", "C4E278F3DA")]
  [TestCase("TZCNT", "66F30FBCC2")]
  [TestCase("BZHI", "C4E270F5C2")]
  [TestCase("PDEP", "C4E273F5C2")]
  [TestCase("PEXT", "C4E272F5C2")]
  [TestCase("SARX", "C4E272F7C2")]
  [TestCase("SHLX", "C4E271F7C2")]
  [TestCase("SHRX", "C4E273F7C2")]
  [TestCase("MULX", "C4E273F6C3")]
  [TestCase("RORX", "C4E37BF0C205")]
  public void Bmi_RegisterForms_EmitIntelEncoding(string mnemonic, string expectedHex) {
    var actual = Assemble(asm => {
      switch (mnemonic) {
        case "ANDN": asm.Andn(Reg.EAX, Reg.ECX, Reg.EDX); break;
        case "BEXTR": asm.Bextr(Reg.EAX, Reg.EDX, Reg.ECX); break;
        case "BLSR": asm.Blsr(Reg.EAX, Reg.EDX); break;
        case "BLSMSK": asm.Blsmsk(Reg.EAX, Reg.EDX); break;
        case "BLSI": asm.Blsi(Reg.EAX, Reg.EDX); break;
        case "TZCNT": asm.Tzcnt(Reg.EAX, Reg.EDX); break;
        case "BZHI": asm.Bzhi(Reg.EAX, Reg.EDX, Reg.ECX); break;
        case "PDEP": asm.Pdep(Reg.EAX, Reg.ECX, Reg.EDX); break;
        case "PEXT": asm.Pext(Reg.EAX, Reg.ECX, Reg.EDX); break;
        case "SARX": asm.Sarx(Reg.EAX, Reg.EDX, Reg.ECX); break;
        case "SHLX": asm.Shlx(Reg.EAX, Reg.EDX, Reg.ECX); break;
        case "SHRX": asm.Shrx(Reg.EAX, Reg.EDX, Reg.ECX); break;
        case "MULX": asm.Mulx(Reg.EAX, Reg.ECX, Reg.EBX); break;
        case "RORX": asm.Rorx(Reg.EAX, Reg.EDX, 5); break;
        default: throw new AssertionException($"unhandled BMI test mnemonic {mnemonic}");
      }
    });

    Assert.That(actual, Is.EqualTo(Convert.FromHexString(expectedHex)));
  }

  [Test]
  public void Pdep_GivenMemoryMask_ThenEncodesMaskInModRmAndSourceInVex() {
    Assert.That(Assemble(asm => asm.Pdep(Reg.EAX, Reg.ECX, Mem.Dword(Reg.BX).Es())),
      Is.EqualTo(Convert.FromHexString("26C4E273F507")));
  }

  [Test]
  public void Bextr_GivenMemoryData_ThenEncodesControlInVexAndDataInModRm() {
    Assert.That(Assemble(asm => asm.Bextr(Reg.EAX, Mem.Dword(Reg.BX), Reg.ECX)),
      Is.EqualTo(Convert.FromHexString("C4E270F707")));
  }

  [Test]
  public void Mulx_GivenDistinctDestinations_ThenFirstDestinationIsHighAndSecondIsLow() {
    Assert.That(Assemble(asm => asm.Mulx(Reg.EAX, Reg.ECX, Reg.EBX)),
      Is.EqualTo(Convert.FromHexString("C4E273F6C3")));
  }
}
