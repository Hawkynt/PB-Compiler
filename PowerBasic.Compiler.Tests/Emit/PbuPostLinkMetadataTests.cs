using System.Text;
using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Emit;

namespace PowerBasic.Compiler.Tests.Emit;

[TestFixture]
public sealed class PbuPostLinkMetadataTests {

  [Test]
  public void RoundTrip_GivenPostLinkMetadata_ThenFragmentsAndRelativeFixupsSurvive() {
    var unit = new PbuFile { Name = "LAYOUT", CpuFlags = PbuCpuFlags.Needs386 };
    unit.Code = [0x74, 0x01, 0x90, 0xC3];
    unit.Fragments.Add(new("Work", 7, 0, 3, 1, PbuFragmentControlKind.Conditional,
      8, 9, (byte)Condition.Equal, [8, 9]));
    unit.RelativeFixups.Add(new(0, 2, PbuRelativeFixupKind.Conditional,
      (byte)Condition.Equal, 3));

    using var stream = new MemoryStream();
    unit.Write(stream);
    stream.Position = 0;
    var read = PbuFile.Read(stream);

    Assert.Multiple(() => {
      Assert.That(read.CpuFlags, Is.EqualTo(PbuCpuFlags.Needs386));
      Assert.That(read.Fragments, Has.Count.EqualTo(1));
      var expected = unit.Fragments[0];
      var actual = read.Fragments[0];
      Assert.That(actual with { Successors = expected.Successors }, Is.EqualTo(expected));
      Assert.That(actual.Successors, Is.EqualTo(expected.Successors));
      Assert.That(read.RelativeFixups, Is.EqualTo(unit.RelativeFixups));
    });
  }

  [Test]
  public void Read_GivenVersionOneUnit_ThenLegacyImageLoadsWithNoPostLinkMetadata() {
    using var stream = new MemoryStream();
    using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true)) {
      writer.Write("PBU1"u8.ToArray());
      writer.Write((ushort)1);
      writer.Write((ushort)PbuCpuFlags.None);
      WriteString(writer, "OLD");
      writer.Write((ushort)0); // exports
      writer.Write((ushort)0); // imports
      writer.Write((ushort)0); // commons
      writer.Write(1u);
      writer.Write((byte)0xC3);
      writer.Write(0u);        // data
      writer.Write(0u);        // bss
      writer.Write((ushort)0); // fixups
    }
    stream.Position = 0;

    var read = PbuFile.Read(stream);

    Assert.Multiple(() => {
      Assert.That(read.Name, Is.EqualTo("OLD"));
      Assert.That(read.Code, Is.EqualTo(new byte[] { 0xC3 }));
      Assert.That(read.Fragments, Is.Empty);
      Assert.That(read.RelativeFixups, Is.Empty);
    });
  }

  private static void WriteString(BinaryWriter writer, string value) {
    var bytes = Encoding.ASCII.GetBytes(value);
    writer.Write((byte)bytes.Length);
    writer.Write(bytes);
  }
}
