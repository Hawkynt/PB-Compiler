using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend.Targets;

namespace PowerBasic.Compiler.Tests.Backend;

[TestFixture]
public sealed class MachineCodeImageBridgeTests {

  [Test]
  public void AppendMachineCode_GivenInternalBranchLabel_ThenItNeverEscapesToTheExternalResolver() {
    var assembler = new Assembler();
    var code = new MachineCode(
      [0xE9, 0x00, 0x00, 0x90],
      [new MachineRelocation(1, MachineRelocationKind.Relative16, "s_0", -2)],
      new Dictionary<string, int>(StringComparer.Ordinal) { ["s_0"] = 3 });
    var resolved = new List<string>();

    assembler.AppendMachineCode(code, symbol => {
      resolved.Add(symbol);
      return assembler.Lbl(symbol);
    });

    Assert.Multiple(() => {
      Assert.That(() => assembler.ToArray(), Throws.Nothing);
      Assert.That(resolved, Is.Empty,
        "function-local labels must be bound from MachineCode.Labels, never minted by the image/link resolver");
    });
  }
  [Test]
  public void AppendMachineCode_GivenTwoFunctionsWithSameLocalLabelName_ThenTheirLabelsStayDisjoint() {
    static MachineCode Body()
      => new(
        [0xE9, 0x00, 0x00, 0x90],
        [new MachineRelocation(1, MachineRelocationKind.Relative16, "s_0", -2)],
        new Dictionary<string, int>(StringComparer.Ordinal) { ["s_0"] = 3 });

    var assembler = new Assembler();
    assembler.AppendMachineCode(Body(), _ => null);
    assembler.AppendMachineCode(Body(), _ => null);

    var image = assembler.ToArray();

    Assert.That(image, Is.EqualTo(new byte[] {
      0xE9, 0x00, 0x00, 0x90,
      0xE9, 0x00, 0x00, 0x90,
    }), "each function-local s_0 must bind to that function's own offset");
  }


}
